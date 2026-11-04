using System.Text;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Content streams: which fonts and characters the text uses (for 6.2.11), where device colour is drawn (6.2.4.3),
/// operators PDF/A forbids (6.2.2, 6.2.6, 6.2.8, 6.1.10), the nesting of graphics states (6.1.13) and resources
/// inherited from a parent (6.2.2).
/// </summary>
internal sealed partial class PdfAConverter
{
    /// <summary>How deep forms, patterns and Type 3 glyphs that draw one another are followed.</summary>
    private const int MaxContentDepth = 24;

    /// <summary>The q/Q nesting PDF/A allows (6.1.13).</summary>
    private const int MaxGraphicsStateNesting = 28;

    /// <summary>The fonts the text uses, with the codes it shows in them.</summary>
    private sealed class FontUse
    {
        public FontUse(PdfDict dict, PdfFont? font)
        {
            Dict = dict;
            Font = font;
        }

        public readonly PdfDict Dict;
        public readonly PdfFont? Font;

        /// <summary>Codes shown in a rendering mode that paints or clips.</summary>
        public readonly HashSet<int> Visible = new();

        /// <summary>Codes shown only as invisible text (rendering mode 3), such as the text layer of a scan.</summary>
        public readonly HashSet<int> Invisible = new();

        /// <summary>The number of bytes of each code, which a CID font's CMap can vary.</summary>
        public readonly Dictionary<int, int> Lengths = new();

        public IEnumerable<int> AllCodes => Visible.Union(Invisible);
    }

    private readonly Dictionary<PdfDict, FontUse> _fontUses = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(PdfObject, PdfDict?)> _walked = new();

    // Every operator PDF 1.7 defines (6.2.2): content streams may use no other, not even between BX and EX.
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI", "EMC", "ET",
        "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n", "q", "Q", "re", "RG", "rg",
        "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj", "TJ", "TL", "Tm", "Tr", "Ts", "Tw", "Tz",
        "v", "w", "W", "W*", "y", "'", "\"",
    };

    /// <summary>
    /// The text state that graphics states save and restore: the font, or the name of one the resources do not define,
    /// and the rendering mode.
    /// </summary>
    private readonly record struct TextState(PdfDict? Font, int Mode, string? UndefinedFont = null);

    /// <summary>A change to make in a content stream: the bytes from Start to End become Replacement.</summary>
    private readonly record struct Edit(int Start, int End, byte[] Replacement);

    /// <summary>
    /// Walks a page's content: the page's streams in order, as one, with the page's resources. Edits replace the page's
    /// content with one stream.
    /// </summary>
    private void WalkPage(PdfDict page, PdfDict? resources)
    {
        var contents = _file.Resolve(page["Contents"]);
        var streams = contents switch
        {
            PdfStream s => new List<PdfStream> { s },
            PdfArray a => a.Items.Select(i => _file.Resolve(i) as PdfStream).Where(s => s != null).Select(s => s!).ToList(),
            _ => new List<PdfStream>(),
        };
        if (streams.Count == 0) return;
        byte[] data;
        if (streams.Count == 1)
        {
            if (Decode(streams[0]) is not { } single) return;
            data = single;
        }
        else
        {
            var all = new MemoryStream();
            foreach (var s in streams)
            {
                if (Decode(s) is not { } part) continue;
                all.Write(part);
                all.WriteByte((byte)'\n');
            }
            data = all.ToArray();
        }
        var edits = Walk(data, resources, new TextState(null, 0), 0);
        if (edits == null) return;
        var content = new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal) { ["Filter"] = PdfName.Get("FlateDecode") },
            Deflate(Apply(data, edits)));
        page.Entries["Contents"] = _file.AddObject(content);
    }

    /// <summary>
    /// Walks a stream that draws on its own (a form, a pattern, an appearance, a Type 3 glyph) once for the resources it
    /// is drawn with. Without resources of its own it uses <paramref name="inherited"/>, and is given them when it
    /// names any (6.2.2).
    /// </summary>
    private void WalkStream(PdfStream stream, PdfDict? inherited, TextState state, int depth, PdfDict? owner = null)
    {
        if (depth > MaxContentDepth) return;
        var own = _file.GetDict(owner ?? stream, "Resources");
        var resources = own ?? inherited;
        if (!_walked.Add((stream, resources))) return;
        if (Decode(stream) is not { } data) return;
        _usesNames = false;
        var edits = Walk(data, resources, state, depth);
        if (own == null && _usesNames)
        {
            if (resources != null && _fix)
            {
                _report.Add("Gave {0} form{s}, pattern{s} or glyph procedure{s} that used their parent's resources resources of their own, as PDF/A needs.");
                (owner ?? stream).Entries["Resources"] = resources;
            }
            else Problem("{0} form{s}, pattern{s} or glyph procedure{s} use resources they do not have themselves, which PDF/A forbids.");
        }
        if (edits == null) return;
        stream.RawData = Deflate(Apply(data, edits));
        stream.Entries["Filter"] = PdfName.Get("FlateDecode");
        stream.Entries.Remove("DecodeParms");
    }

    // Whether the content walked last named a resource.
    private bool _usesNames;

    // The font given to names that content selects but no resources define.
    private PdfDict? _fallbackFont;

    private byte[]? Decode(PdfStream s)
    {
        try
        {
            return _file.DecodeStream(s);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            Problem("{0} content stream{s} cannot be decoded.");
            return null;
        }
    }

    private static byte[] Apply(byte[] data, List<Edit> edits)
    {
        var result = new MemoryStream(data.Length + 64);
        int at = 0;
        foreach (var e in edits)
        {
            // Two changes to one operator (rare: a number with an exponent in an operator that is also replaced) keep the first.
            if (e.Start < at) continue;
            result.Write(data, at, e.Start - at);
            result.Write(e.Replacement);
            at = e.End;
        }
        result.Write(data, at, data.Length - at);
        return result.ToArray();
    }

    /// <summary>Reads one content stream; returns the edits it needs, or null when it needs none (or when only checking).</summary>
    private List<Edit>? Walk(byte[] data, PdfDict? resources, TextState start, int depth)
    {
        List<Edit>? edits = null;
        var parser = new ContentParser(data);
        var stack = new Stack<TextState>();
        var state = start;
        int nesting = 0, maxNesting = 0;
        bool usesNames = false;
        int ops = 0;
        while (parser.Next())
        {
            if ((++ops & 4095) == 0) PdfFile.CheckCancel();
            var op = parser.Op;
            var operands = parser.Operands;
            if (operands.Count > 0 && op != "BI" && Operators.Contains(op) && Exponent(data, parser.Start, parser.End, operands) is { } plain)
            {
                // PDF numbers have no exponent; a reader that does not take one sees an operator such as "e2".
                Found("Wrote {0} number{s} in content streams that used exponents, which PDF does not allow, as plain numbers.",
                    "Content streams write {0} number{s} with exponents, which PDF does not allow.");
                (edits ??= new()).Add(new Edit(parser.Start, parser.End, plain));
            }
            switch (op)
            {
                case "q":
                    stack.Push(state);
                    maxNesting = Math.Max(maxNesting, ++nesting);
                    break;
                case "Q":
                    if (stack.Count > 0) state = stack.Pop();
                    nesting = Math.Max(0, nesting - 1);
                    break;
                case "Tf":
                    if (operands.Count >= 1 && operands[0].Name is { } fontName)
                    {
                        usesNames = true;
                        var selected = Resource(resources, "Font", fontName) as PdfDict;
                        state = state with { Font = selected, UndefinedFont = selected == null ? fontName : null };
                        if (selected != null && _file.GetName(selected, "Subtype") == "Type3") Type3(selected, resources, depth);
                    }
                    break;
                case "Tr":
                    if (operands.Count >= 1 && operands[0].Kind == OperandKind.Number) state = state with { Mode = (int)operands[0].Num };
                    break;
                case "Tj" or "'":
                    if (operands.Count >= 1 && operands[^1].Bytes is { } shown) ShowText(ref state, shown, resources);
                    break;
                case "\"":
                    if (operands.Count >= 3 && operands[2].Bytes is { } shown2) ShowText(ref state, shown2, resources);
                    break;
                case "TJ":
                    if (operands.Count >= 1 && operands[^1].Array is { } array)
                        foreach (var item in array.Items)
                            if (item is PdfString s) ShowText(ref state, s.Bytes, resources);
                    break;
                case "rg" or "RG":
                    UseDevice(DeviceSpace.Rgb, resources);
                    break;
                case "k" or "K":
                    UseDevice(DeviceSpace.Cmyk, resources);
                    break;
                case "g" or "G":
                    UseDevice(DeviceSpace.Gray, resources);
                    break;
                case "cs" or "CS":
                    if (operands.Count >= 1 && operands[0].Name is { } space)
                    {
                        usesNames |= space is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern");
                        UseDevice(Devices(PdfName.Get(space), resources), resources);
                    }
                    break;
                case "sc" or "scn" or "SC" or "SCN":
                    if (operands.Count >= 1 && operands[^1].Name is { } patternName)
                    {
                        usesNames = true;
                        Pattern(Resource(resources, "Pattern", patternName), resources, state, depth);
                    }
                    break;
                case "sh":
                    if (operands.Count >= 1 && operands[0].Name is { } shadingName)
                    {
                        usesNames = true;
                        if (Resource(resources, "Shading", shadingName) is PdfDict shading) UseDevice(Devices(shading["ColorSpace"], null), resources);
                        else Undefined(shadingName);
                    }
                    break;
                case "Do":
                    if (operands.Count >= 1 && operands[0].Name is { } xobjectName)
                    {
                        usesNames = true;
                        if (Resource(resources, "XObject", xobjectName) is PdfStream xobject) XObject(xobject, resources, state, depth);
                        else if (_fix) RemoveUndefined(ref edits, parser, xobjectName);
                        else Undefined(xobjectName);
                    }
                    break;
                case "gs":
                    if (operands.Count >= 1 && operands[0].Name is { } gsName)
                    {
                        usesNames = true;
                        if (Resource(resources, "ExtGState", gsName) is PdfDict gs) state = GraphicsState(gs, resources, state, depth);
                        else if (_fix) RemoveUndefined(ref edits, parser, gsName);
                        else Undefined(gsName);
                    }
                    break;
                case "BDC" or "DP":
                    if (operands.Count >= 2 && operands[1].Name != null) usesNames = true;
                    break;
                case "ri":
                    if (operands.Count >= 1 && operands[0].Name is { } intent && !RenderingIntents.Contains(intent))
                    {
                        Found("Replaced {0} rendering intent{s} PDF/A does not know with RelativeColorimetric{names}.", "{0} rendering intent{s} are not one PDF/A knows{names}.", intent);
                        (edits ??= new()).Add(new Edit(parser.Start, parser.End, "\n/RelativeColorimetric ri"u8.ToArray()));
                    }
                    break;
                case "BI":
                    if (parser.InlineDict is { } inline && InlineImage(inline, parser.InlineData ?? Array.Empty<byte>(), resources, ref usesNames) is { } replacement)
                        (edits ??= new()).Add(new Edit(parser.Start, parser.End, replacement));
                    break;
                default:
                    if (!Operators.Contains(op))
                    {
                        Found("Removed {0} operator{s} PDF does not define from content streams, which PDF/A forbids; viewers ignore them{names}.",
                            "Content streams use {0} operator{s} PDF does not define, which PDF/A forbids{names}.", op.Length <= 16 ? op : op[..16]);
                        (edits ??= new()).Add(new Edit(parser.Start, parser.End, "\n"u8.ToArray()));
                    }
                    break;
            }
        }
        if (maxNesting > MaxGraphicsStateNesting) Problem("Content nests graphics states (q and Q) more than 28 levels deep, the PDF/A limit.");
        _usesNames = usesNames;
        return _fix ? edits : null;
    }

    /// <summary>A name the content uses that its resources do not define (6.2.2), which nothing can draw.</summary>
    private void Undefined(string name)
        => Problem("Content streams use {0} resource{s} their resources do not define{names}.", Printable(name));

    /// <summary>Removes an operator that draws or sets a resource that is not defined, which viewers skip too.</summary>
    private void RemoveUndefined(ref List<Edit>? edits, ContentParser parser, string name)
    {
        _report.Add("Removed {0} use{s} of resources that are not defined from content streams; viewers skipped them{names}.", Printable(name));
        (edits ??= new()).Add(new Edit(parser.Start, parser.End, "\n"u8.ToArray()));
    }

    /// <summary>
    /// The operator written again with plain numbers, when a number among its operands has an exponent (such as 1e2);
    /// null when none has, or when an operand is not a number or a name and so is better left as written.
    /// </summary>
    private static byte[]? Exponent(byte[] data, int start, int end, List<Operand> operands)
    {
        bool found = false;
        for (int i = start + 1; i + 1 < end && !found; i++)
        {
            if ((data[i] | 0x20) != 'e' || !char.IsAsciiDigit((char)data[i - 1]) && data[i - 1] != '.') continue;
            if (!char.IsAsciiDigit((char)data[i + 1]) && data[i + 1] is not ((byte)'+' or (byte)'-')) continue;
            // The exponent must belong to a number token, not a name or a string.
            int t = i - 1;
            while (t > start && (char.IsAsciiDigit((char)data[t - 1]) || data[t - 1] is (byte)'.' or (byte)'+' or (byte)'-')) t--;
            found = t == start || data[t - 1] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'[';
        }
        if (!found) return null;
        var sb = new StringBuilder("\n");
        foreach (var operand in operands)
        {
            if (operand.Kind == OperandKind.Number) sb.Append(Plain(operand.Num));
            else if (operand.Name is { } name && name.All(c => c is > ' ' and < '\u007f' and not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or '#')))
                sb.Append('/').Append(name);
            else return null;
            sb.Append(' ');
        }
        return Encoding.ASCII.GetBytes(sb.Append(OperatorText(data, start, end)).ToString());
    }

    private static string Plain(double v)
        => double.IsFinite(v) ? v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) : "0";

    // The operator keyword itself: the last token before End.
    private static string OperatorText(byte[] data, int start, int end)
    {
        int e = end;
        while (e > start && data[e - 1] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') e--;
        int b = e;
        while (b > start && data[b - 1] is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)']' or (byte)')' or (byte)'>')) b--;
        return Encoding.ASCII.GetString(data, b, e - b);
    }

    private PdfObject? Resource(PdfDict? resources, string category, string name)
        => resources == null ? null : _file.Resolve(_file.GetDict(resources, category)?[name]);

    private void ShowText(ref TextState state, byte[] text, PdfDict? resources)
    {
        if (state.Font is not { } dict)
        {
            // Selecting a font that is not there matters only once text is shown with it.
            if (state.UndefinedFont is not { } undefined || text.Length == 0) return;
            if (!_fix || resources == null)
            {
                Undefined(undefined);
                return;
            }
            // Viewers draw such text in Helvetica, so the name is defined as Helvetica, which the font pass embeds.
            _fallbackFont ??= NewDict(("Type", PdfName.Get("Font")), ("Subtype", PdfName.Get("Type1")), ("BaseFont", PdfName.Get("Helvetica")),
                ("Encoding", PdfName.Get("WinAnsiEncoding")));
            var fonts = _file.GetDict(resources, "Font");
            if (fonts == null)
            {
                fonts = new PdfDict();
                resources.Entries["Font"] = fonts;
            }
            fonts.Entries[undefined] = _fallbackFont;
            _report.Add("Defined {0} font{s} that content streams used without defining them as Helvetica, which viewers draw them with{names}.", Printable(undefined));
            state = state with { Font = _fallbackFont, UndefinedFont = null };
            dict = _fallbackFont;
        }
        if (!_fontUses.TryGetValue(dict, out var use))
        {
            PdfFont? font = null;
            try
            {
                font = PdfFont.Load(_file, dict);
            }
            catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or NotSupportedException or InvalidDataException or InvalidOperationException)
            {
                SwallowedErrors.Record(ex);
            }
            _fontUses[dict] = use = new FontUse(dict, font);
        }
        if (use.Font == null) return;
        var codes = state.Mode == 3 ? use.Invisible : use.Visible;
        int pos = 0;
        // A string can hold no more codes than bytes; the guard keeps a damaged CMap from looping.
        for (int guard = 0; pos < text.Length && guard <= text.Length; guard++)
        {
            int before = pos;
            int code = use.Font.NextCode(text, ref pos, out _);
            if (pos <= before) break;
            codes.Add(code);
            use.Lengths.TryAdd(code, pos - before);
        }
    }

    private TextState GraphicsState(PdfDict gs, PdfDict? resources, TextState state, int depth)
    {
        if (_file.GetArray(gs, "Font") is { Count: >= 1 } font && _file.Resolve(font[0]) is PdfDict fontDict) state = state with { Font = fontDict };
        if (_file.GetDict(gs, "SMask") is { } mask && _file.GetStream(mask, "G") is { } group) WalkStream(group, resources, state, depth + 1);
        return state;
    }

    private void XObject(PdfStream? x, PdfDict? resources, TextState state, int depth)
    {
        if (x == null) return;
        switch (_file.GetName(x, "Subtype"))
        {
            case "Image":
                if (!_file.GetBool(x, "ImageMask", false)) UseDevice(Devices(x["ColorSpace"], null), resources);
                break;
            case "Form":
                WalkStream(x, resources, state, depth + 1);
                break;
        }
    }

    private void Pattern(PdfObject? pattern, PdfDict? resources, TextState state, int depth)
    {
        switch (pattern)
        {
            case PdfStream tiling:
                WalkStream(tiling, resources, state, depth + 1);
                break;
            case PdfDict shadingPattern when _file.GetDict(shadingPattern, "Shading") is { } shading:
                UseDevice(Devices(shading["ColorSpace"], null), resources);
                break;
        }
    }

    // Inline image filters PDF/A allows (6.1.10), by full and abbreviated name.
    private static readonly HashSet<string> InlineFilters = new(StringComparer.Ordinal)
    {
        "ASCIIHexDecode", "AHx", "ASCII85Decode", "A85", "FlateDecode", "Fl", "RunLengthDecode", "RL", "CCITTFaxDecode", "CCF", "DCTDecode", "DCT",
    };

    /// <summary>
    /// An inline image: its colour space, interpolation (6.2.8), rendering intent (6.2.6) and filters (6.1.10). Returns
    /// the image written again when it needs a change.
    /// </summary>
    private byte[]? InlineImage(PdfDict inline, byte[] data, PdfDict? resources, ref bool usesNames)
    {
        var cs = inline["CS"] ?? inline["ColorSpace"];
        if (cs is PdfName { Value: not ("G" or "RGB" or "CMYK" or "I" or "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Indexed") }) usesNames = true;
        if (!_file.GetBool(inline, "IM", _file.GetBool(inline, "ImageMask", false))) UseDevice(Devices(cs, resources), resources);
        bool change = false;
        foreach (var key in new[] { "I", "Interpolate" })
        {
            if (inline[key] is not PdfBool { Value: true }) continue;
            Found("Turned off interpolation of {0} image{s}, which PDF/A forbids; viewers may show them a little sharper.", "{0} image{s} ask for interpolation, which PDF/A forbids.");
            inline.Entries.Remove(key);
            change = true;
        }
        if (inline["Intent"] is PdfName { Value: var intent } && !RenderingIntents.Contains(intent))
        {
            Found("Replaced {0} rendering intent{s} PDF/A does not know with RelativeColorimetric{names}.", "{0} rendering intent{s} are not one PDF/A knows{names}.", intent);
            inline.Entries["Intent"] = PdfName.Get("RelativeColorimetric");
            change = true;
        }
        var filter = inline["F"] ?? inline["Filter"];
        var filters = filter switch
        {
            PdfName n => new List<string> { n.Value },
            PdfArray a => a.Items.Select(i => (i as PdfName)?.Value ?? "").ToList(),
            _ => new List<string>(),
        };
        if (filters.Any(f => !InlineFilters.Contains(f)))
        {
            // Only a lone LZW filter can be re-encoded here; other chains are left for the report.
            if (_fix && filters is ["LZW" or "LZWDecode"])
            {
                try
                {
                    var decoded = Filters.Decode("LZWDecode", data, (inline["DP"] ?? inline["DecodeParms"]) as PdfDict, _file);
                    data = Deflate(decoded);
                    inline.Entries.Remove("Filter");
                    inline.Entries.Remove("DecodeParms");
                    inline.Entries.Remove("DP");
                    inline.Entries["F"] = PdfName.Get("Fl");
                    _report.Add("Re-encoded {0} inline image{s} that used LZW compression, which PDF/A forbids, with Flate.");
                    change = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
                {
                    SwallowedErrors.Record(ex);
                }
            }
            else Problem("{0} inline image{s} use filters PDF/A forbids.");
        }
        if (!change || !_fix) return null;
        var b = new ByteBuffer(data.Length + 128);
        b.Ascii("\nBI");
        var writer = new PdfObjectWriter(PdfObjectSyntax.Rewrite);
        foreach (var (key, value) in inline.Entries)
        {
            b.Sp();
            PdfObjectWriter.WriteName(b, key);
            b.Sp();
            writer.WriteObject(b, value);
        }
        b.Ascii(" ID ").Bytes(data).Ascii("\nEI");
        return b.Span.ToArray();
    }

    /// <summary>Text for messages: a name without characters that would garble a sentence.</summary>
    private static string Printable(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 64));
        foreach (char c in s.AsSpan(0, Math.Min(s.Length, 64))) sb.Append(c is >= ' ' and < '\u007f' ? c : '?');
        return sb.ToString();
    }
}

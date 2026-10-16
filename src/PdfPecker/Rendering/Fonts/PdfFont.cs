using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

internal enum FontKind : byte { Simple, Cid, Type3 }

/// <summary>
/// A font resource prepared for rendering: splits strings into codes and maps each code to a width and a glyph.
/// Embedded TrueType, CFF (Type1C/CIDFontType0C/OpenType) and Type 1 programs are used directly; other fonts are
/// substituted with the built-in font pack.
/// </summary>
internal sealed class PdfFont
{
    public FontKind Kind;
    public IGlyphSource? Source;
    public bool Vertical;
    public string BaseFont = "";

    // Simple fonts.
    private readonly int[] _codeToGid = new int[256];
    private readonly double[] _widths = new double[256];
    private readonly float[] _hScale = new float[256];

    // CID fonts.
    private CMap? _cmap;
    private int[]? _cidToGid;
    private readonly Dictionary<int, double> _cidWidths = new();
    private double _defaultWidth = 1000;
    private double _vDefaultY = 880, _vDefaultW1 = -1000;
    private readonly Dictionary<int, (double W1, double Vx, double Vy)> _vMetrics = new();
    private CffFont? _cidCff;
    private Func<int, int>? _cidFallbackGid;

    // Text extraction: what each code stands for. ToUnicode wins; then the glyph names of the encoding; then the
    // characters a substituted face was looked up by; then the embedded program's own cmap or glyph names.
    private CMap? _toUnicode;
    private readonly string?[] _encNames = new string?[256];
    private int[]? _substUnicode;
    private bool _unicodeCodes; // a predefined Uni...-UCS2 or -UTF16 CMap: the codes are UTF-16 code units
    private Lazy<Dictionary<int, int>?>? _gidToUnicode;

    // Type 3.
    public Matrix FontMatrix = Matrix.Scale(0.001, 0.001);
    public PdfStream?[]? Type3Procs;
    public PdfDict? Type3Resources;

    public static PdfFont Load(PdfFile file, PdfDict dict)
    {
        var f = new PdfFont();
        var subtype = file.GetName(dict, "Subtype");
        f.BaseFont = StripSubset(file.GetName(dict, "BaseFont") ?? "");
        try
        {
            switch (subtype)
            {
                case "Type0": f.LoadType0(file, dict); break;
                case "Type3": f.LoadType3(file, dict); break;
                default: f.LoadSimple(file, dict, subtype); break;
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            // Unusable font program: fall back to substitution.
            if (f.Kind != FontKind.Type3)
            {
                f.Kind = FontKind.Simple;
                f.Substitute(file, dict, null, null);
            }
        }
        f.LoadUnicode(file, dict);
        return f;
    }

    private static string StripSubset(string name)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            bool tag = true;
            for (int i = 0; i < 6; i++) if (name[i] < 'A' || name[i] > 'Z') { tag = false; break; }
            if (tag) return name.Substring(7);
        }
        return name;
    }

    // ------------------------------------------------------------------ per-code access

    /// <summary>Reads the next code. <paramref name="isSpace"/> is set for the single-byte code 32 (for word spacing).</summary>
    public int NextCode(ReadOnlySpan<byte> s, ref int pos, out bool isSpace)
    {
        if (Kind == FontKind.Cid)
        {
            uint code = _cmap!.NextCode(s, ref pos, out int len);
            isSpace = len == 1 && code == 32;
            return (int)code;
        }
        int c = s[pos++];
        isSpace = c == 32;
        return c;
    }

    /// <summary>Horizontal advance in text space units for a font size of 1.</summary>
    public double Width(int code)
    {
        if (Kind == FontKind.Cid)
        {
            int cid = _cmap!.ToCid((uint)code);
            return (_cidWidths.TryGetValue(cid, out double w) ? w : _defaultWidth) / 1000;
        }
        if (Kind == FontKind.Type3) return _widths[code & 255] * FontMatrix.A;
        return _widths[code & 255] / 1000;
    }

    /// <summary>Vertical-mode metrics (text space, size 1): advance w1 and the position vector v.</summary>
    public (double W1, double Vx, double Vy) VerticalMetrics(int code)
    {
        int cid = Kind == FontKind.Cid ? _cmap!.ToCid((uint)code) : code;
        if (_vMetrics.TryGetValue(cid, out var m)) return (m.W1 / 1000, m.Vx / 1000, m.Vy / 1000);
        return (_vDefaultW1 / 1000, Width(code) / 2, _vDefaultY / 1000);
    }

    public int GlyphId(int code)
    {
        if (Kind == FontKind.Cid)
        {
            int cid = _cmap!.ToCid((uint)code);
            if (_cidFallbackGid != null) return _cidFallbackGid(code);
            if (_cidCff != null) return _cidCff.GidForCid(cid);
            if (_cidToGid != null) return cid < _cidToGid.Length ? _cidToGid[cid] : 0;
            return cid;
        }
        return _codeToGid[code & 255];
    }

    /// <summary>Horizontal glyph stretch applied to substituted fonts so they keep the document's widths.</summary>
    public float HScale(int code) => Kind == FontKind.Simple ? _hScale[code & 255] : 1f;

    public Matrix GlyphMatrix => Source?.FontMatrix ?? FontMatrix;

    // ------------------------------------------------------------------ text extraction

    private void LoadUnicode(PdfFile file, PdfDict dict)
    {
        try
        {
            if (file.GetStream(dict, "ToUnicode") is PdfStream tu) _toUnicode = CMap.Parse(file.DecodeStream(tu));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            // A damaged ToUnicode map only loses the text, not the drawing; the other sources still apply.
        }
        var source = Source;
        _gidToUnicode = new Lazy<Dictionary<int, int>?>(() => ReverseCmap(source), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The glyph-to-character map of an embedded TrueType program, from its Unicode cmap.</summary>
    private static Dictionary<int, int>? ReverseCmap(IGlyphSource? source)
    {
        if (source is not TrueTypeFont tt) return null;
        var map = tt.GetCmap(3, 10) ?? tt.GetCmap(3, 1) ?? tt.GetCmap(0, 4) ?? tt.GetCmap(0, 3);
        if (map == null) return null;
        var reverse = new Dictionary<int, int>();
        foreach (var (u, g) in map)
        {
            // Prefer the lowest code point for a glyph that several characters share (such as a space and a no-break space).
            if (g > 0 && (!reverse.TryGetValue(g, out int have) || u < have)) reverse[g] = u;
        }
        return reverse;
    }

    /// <summary>The text a character code stands for, or null when the font does not say.</summary>
    public string? Unicode(int code)
    {
        if (_toUnicode?.ToUnicode((uint)code) is { Length: > 0 } mapped) return mapped;
        if (Kind == FontKind.Cid)
        {
            if (_unicodeCodes) return code is > 0 and <= 0xFFFF && !char.IsSurrogate((char)code) ? ((char)code).ToString() : null;
            if (Source is CffFont { IsCid: false } cff && cff.GlyphName(GlyphId(code)) is string cidName) return NameToText(cidName);
            return GlyphText(GlyphId(code));
        }
        int c = code & 255;
        if (_encNames[c] is string name && NameToText(name) is string named) return named;
        if (_substUnicode != null && _substUnicode[c] > 0) return FromCodePoint(_substUnicode[c]);
        if (Kind == FontKind.Simple) return GlyphText(_codeToGid[c]);
        // Type 3 glyphs are often named freely ("a12", "tri"); their codes, like TeX's bitmap fonts, are then usually
        // the ASCII letters themselves, the guess other extractors make too.
        return c is > 0x20 and < 0x7F ? ((char)c).ToString() : null;
    }

    private string? GlyphText(int gid)
        => gid > 0 && _gidToUnicode?.Value is { } map && map.TryGetValue(gid, out int u) ? FromCodePoint(u) : null;

    /// <summary>The text of a glyph name: an AGL or uniXXXX name, or a ligature name such as "f_f_i" (suffixes such as ".sc" ignored).</summary>
    internal static string? NameToText(string name)
    {
        int u = Encodings.NameToUnicode(name);
        if (u >= 0) return FromCodePoint(u);
        int dot = name.IndexOf('.');
        var core = dot > 0 ? name.Substring(0, dot) : name;
        if (core.IndexOf('_') <= 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var part in core.Split('_'))
        {
            int pu = Encodings.NameToUnicode(part);
            if (pu < 0 || FromCodePoint(pu) is not string ps) return null;
            sb.Append(ps);
        }
        return sb.ToString();
    }

    private static string? FromCodePoint(int u)
        => u is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(u) : null;

    /// <summary>The advance of the space character for word gap detection, in text space for size 1, or 0 when unknown.</summary>
    public double SpaceWidth => Kind != FontKind.Cid && _widths[32] > 0 ? Width(32) : 0;

    // ------------------------------------------------------------------ simple fonts

    private void LoadSimple(PdfFile file, PdfDict dict, string? subtype)
    {
        Kind = FontKind.Simple;
        Array.Fill(_hScale, 1f);
        var desc = file.GetDict(dict, "FontDescriptor");
        int flags = file.GetInt(desc, "Flags", 0);
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;

        // Widths.
        int first = file.GetInt(dict, "FirstChar", 0);
        var widths = file.GetNumbers(dict, "Widths");
        double missing = file.GetNumber(desc, "MissingWidth", 0);
        bool haveWidths = widths != null && widths.Length > 0;
        for (int c = 0; c < 256; c++)
        {
            int i = c - first;
            _widths[c] = haveWidths && i >= 0 && i < widths!.Length ? widths[i] : missing;
        }

        // Encoding: base table plus differences.
        string?[]? baseEnc = null;
        string?[] diffs = new string?[256];
        bool hasDiffs = false;
        var encObj = file.Resolve(dict["Encoding"]);
        if (encObj is PdfName en) baseEnc = Encodings.ByName(en.Value);
        else if (encObj is PdfDict ed)
        {
            baseEnc = Encodings.ByName(file.GetName(ed, "BaseEncoding"));
            if (file.GetArray(ed, "Differences") is PdfArray da)
            {
                int code = 0;
                foreach (var item in da.Items)
                {
                    var o = file.Resolve(item);
                    if (o is PdfNumber n) code = n.IntValue;
                    else if (o is PdfName nm)
                    {
                        if (code >= 0 && code < 256) { diffs[code] = nm.Value; hasDiffs = true; }
                        code++;
                    }
                }
            }
        }

        for (int c = 0; c < 256; c++) _encNames[c] = diffs[c] ?? baseEnc?[c] ?? (symbolic || subtype == "TrueType" && encObj == null ? null : Encodings.Standard[c]);

        // Font program.
        var ff = file.GetStream(desc, "FontFile");
        var ff2 = file.GetStream(desc, "FontFile2");
        var ff3 = file.GetStream(desc, "FontFile3");
        if (subtype == "Type3") return;

        if (ff2 != null || ff3 != null && file.GetName(ff3, "Subtype") == "OpenType")
        {
            var data = file.DecodeStream((ff2 ?? ff3)!);
            var tt = TrueTypeFont.Parse(data);
            if (tt != null && tt.HasGlyf)
            {
                Source = tt;
                MapTrueType(tt, baseEnc, diffs, symbolic, flags, encObj != null);
                FillMissingWidths(haveWidths, tt);
                return;
            }
            if (tt?.CffTable is byte[] cffData && CffFont.Parse(cffData) is CffFont otc)
            {
                Source = otc;
                MapCff(otc, baseEnc, diffs, symbolic);
                FillMissingWidths(haveWidths, otc);
                return;
            }
        }
        if (ff3 != null)
        {
            var cff = CffFont.Parse(file.DecodeStream(ff3));
            if (cff != null)
            {
                Source = cff;
                MapCff(cff, baseEnc, diffs, symbolic);
                FillMissingWidths(haveWidths, cff);
                return;
            }
        }
        if (ff != null)
        {
            var t1 = Type1Font.Parse(file.DecodeStream(ff), file.GetInt(ff, "Length1", 0));
            if (t1 != null)
            {
                Source = t1;
                for (int c = 0; c < 256; c++)
                {
                    string? name = diffs[c] ?? (baseEnc != null && !symbolic ? baseEnc[c] : null) ?? t1.BuiltinEncoding[c] ?? baseEnc?[c];
                    int g = name != null ? t1.GlyphByName(name) : -1;
                    if (g < 0 && t1.BuiltinEncoding[c] is string bn) g = t1.GlyphByName(bn);
                    _codeToGid[c] = Math.Max(0, g < 0 ? t1.GlyphByName(".notdef") : g);
                    if (name != null) _encNames[c] = name;
                }
                FillMissingWidths(haveWidths, t1);
                return;
            }
        }
        Substitute(file, dict, baseEnc, hasDiffs ? diffs : null);
    }

    private void FillMissingWidths(bool haveWidths, IGlyphSource src)
    {
        if (haveWidths) return;
        double scale = src.FontMatrix.A * 1000;
        for (int c = 0; c < 256; c++)
        {
            double a = src.Advance(_codeToGid[c]);
            if (a >= 0) _widths[c] = a * scale;
        }
    }

    private void MapCff(CffFont cff, string?[]? baseEnc, string?[] diffs, bool symbolic)
    {
        for (int c = 0; c < 256; c++)
        {
            int g = -1;
            string? name = diffs[c] ?? (symbolic ? null : baseEnc?[c]);
            if (name != null) g = cff.GlyphByName(name);
            if (g <= 0) g = cff.BuiltinEncoding[c];
            if (g <= 0 && name == null && baseEnc?[c] is string bn) g = cff.GlyphByName(bn);
            if (g <= 0 && cff.IsCid) g = cff.GidForCid(c);
            _codeToGid[c] = Math.Max(0, g);
            if (name == null && g > 0 && cff.GlyphName(g) is string gn) _encNames[c] = gn;
        }
    }

    private void MapTrueType(TrueTypeFont tt, string?[]? baseEnc, string?[] diffs, bool symbolic, int flags, bool hasEncoding)
    {
        var unicodeMap = tt.GetCmap(3, 1) ?? tt.GetCmap(0, 3) ?? tt.GetCmap(0, 4) ?? tt.GetCmap(0, 1) ?? tt.GetCmap(0, 0);
        var symbolMap = tt.GetCmap(3, 0);
        var macMap = tt.GetCmap(1, 0);
        var enc = baseEnc ?? (symbolic ? null : Encodings.Standard);
        for (int c = 0; c < 256; c++)
        {
            int g = 0;
            string? name = diffs[c] ?? enc?[c];
            // Symbolic fonts with a (3,0) or Mac cmap are keyed by the codes themselves (subsetters write them so).
            if (symbolic && (symbolMap != null || macMap != null && unicodeMap == null))
            {
                if (symbolMap != null)
                    foreach (int prefix in new[] { 0xF000, 0, 0xF100, 0xF200 })
                        if (symbolMap.TryGetValue(prefix + c, out int sg)) { g = sg; break; }
                if (g == 0 && macMap != null && macMap.TryGetValue(c, out int mg0)) g = mg0;
            }
            if (g == 0 && (!symbolic || diffs[c] != null))
            {
                if (name != null && unicodeMap != null)
                {
                    int u = Encodings.NameToUnicode(name);
                    if (u >= 0 && unicodeMap.TryGetValue(u, out int ug)) g = ug;
                }
                if (g == 0 && name != null && macMap != null)
                {
                    int mc = Array.IndexOf(Encodings.MacRoman, name);
                    if (mc >= 0 && macMap.TryGetValue(mc, out int mg)) g = mg;
                }
                if (g == 0 && name != null)
                {
                    int pg = tt.GlyphByName(name);
                    if (pg > 0) g = pg;
                }
            }
            if (g == 0 && symbolMap != null)
            {
                foreach (int prefix in new[] { 0, 0xF000, 0xF100, 0xF200 })
                    if (symbolMap.TryGetValue(prefix + c, out int sg)) { g = sg; break; }
            }
            if (g == 0 && macMap != null && macMap.TryGetValue(c, out int m2)) g = m2;
            if (g == 0 && unicodeMap != null && (symbolic || !hasEncoding) && unicodeMap.TryGetValue(c, out int u2)) g = u2;
            if (g == 0 && unicodeMap == null && symbolMap == null && macMap == null && tt.AnyCmap() is Dictionary<int, int> any && any.TryGetValue(c, out int ag)) g = ag;
            if (g == 0 && !tt.HasAnyCmap) g = c; // no cmap at all: codes are glyph ids
            _codeToGid[c] = g;
        }
    }

    /// <summary>Uses a built-in face for a font without a usable embedded program.</summary>
    private void Substitute(PdfFile file, PdfDict dict, string?[]? baseEnc, string?[]? diffs)
    {
        Kind = FontKind.Simple;
        Array.Fill(_hScale, 1f);
        var desc = file.GetDict(dict, "FontDescriptor");
        int flags = file.GetInt(desc, "Flags", 0);
        string name = BaseFont;
        string lower = name.ToLowerInvariant();
        string faceName;
        int[]? builtin = null;
        bool standard14 = false;
        if (lower.Contains("symbol") && !lower.Contains("wingding"))
        {
            faceName = "Symbol";
            builtin = Encodings.SymbolUnicode;
            standard14 = lower == "symbol";
        }
        else if (lower.Contains("dingbat"))
        {
            faceName = "Dingbats";
            builtin = Encodings.DingbatsUnicode;
            standard14 = true;
        }
        else
        {
            bool mono = lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") || (flags & 1) != 0;
            bool serif = !mono && (lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman") ||
                lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("cambria") || lower.Contains("book") ||
                lower.Contains("palatino") || lower.Contains("minion") || lower.Contains("century") ||
                ((flags & 2) != 0 && !lower.Contains("arial") && !lower.Contains("helvetica") && !lower.Contains("sans")));
            bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") ||
                lower.Contains("demi") || (flags & (1 << 18)) != 0 || file.GetNumber(desc, "FontWeight", 400) >= 600;
            bool italic = lower.Contains("italic") || lower.Contains("oblique") || (flags & 64) != 0 || Math.Abs(file.GetNumber(desc, "ItalicAngle", 0)) > 1;
            faceName = (mono ? "Mono" : serif ? "Serif" : "Sans") + (bold && italic ? "-BoldItalic" : bold ? "-Bold" : italic ? "-Italic" : "");
            standard14 = lower.StartsWith("helvetica", StringComparison.Ordinal) || lower.StartsWith("times", StringComparison.Ordinal) ||
                lower.StartsWith("courier", StringComparison.Ordinal) || lower.StartsWith("arial", StringComparison.Ordinal) ||
                lower.StartsWith("liberation", StringComparison.Ordinal);
        }
        var face = StandardFontPack.Face(faceName) ?? StandardFontPack.Face("Sans");
        Source = face;
        if (face == null) return;
        _substUnicode = new int[256];

        var enc = baseEnc ?? (builtin != null ? null : Encodings.Standard);
        bool haveWidths = file.GetArray(dict, "Widths") != null;
        double toThousand = 1000.0 / face.UnitsPerEm;
        for (int c = 0; c < 256; c++)
        {
            int u = -1;
            string? glyphName = diffs?[c] ?? enc?[c];
            if (glyphName != null && builtin == Encodings.DingbatsUnicode && Encodings.DingbatsNames.TryGetValue(glyphName, out int dc)) u = builtin[dc];
            else if (glyphName != null) u = Encodings.NameToUnicode(glyphName);
            if (u < 0 && builtin != null && diffs?[c] == null) u = builtin[c];
            if (u < 0 && glyphName == null && enc == null) u = c;
            int g = u >= 0 ? face.Lookup(u) : 0;
            _substUnicode[c] = u;
            // Symbol-font names used with a text face (e.g. "bullet" via Differences): try the symbol face's mapping.
            _codeToGid[c] = g;
            double adv = face.Advance(g) * toThousand;
            if (!haveWidths)
            {
                // Standard 14 metrics: the Adobe AFM widths where the character is in WinAnsi, else the face's own advance.
                double w = standard14 && builtin == null && u >= 0 ? AfmWidth(faceName, u) : -1;
                _widths[c] = w >= 0 ? w : Math.Max(0, adv);
            }
            else if (!standard14 && adv > 0 && _widths[c] > 0)
            {
                _hScale[c] = (float)Math.Clamp(_widths[c] / adv, 0.6, 1.4);
            }
        }
    }

    private static double AfmWidth(string faceName, int unicode)
    {
        if (unicode > 0xFFFF) return -1;
        byte code = PdfPecker.Fonts.WinAnsi.Encode((char)unicode);
        if (code == (byte)'?' && unicode != '?') return -1;
        var family = faceName.StartsWith("Mono", StringComparison.Ordinal) ? PdfPecker.Fonts.FontFamily.Courier
            : faceName.StartsWith("Serif", StringComparison.Ordinal) ? PdfPecker.Fonts.FontFamily.Times : PdfPecker.Fonts.FontFamily.Helvetica;
        bool bold = faceName.Contains("Bold"), italic = faceName.Contains("Italic");
        var font = PdfPecker.Fonts.StandardFont.Get(family, bold, italic);
        return font.CharWidth((char)unicode, 1000);
    }

    // ------------------------------------------------------------------ Type 0 (CID) fonts

    private void LoadType0(PdfFile file, PdfDict dict)
    {
        Kind = FontKind.Cid;
        var encObj = file.Resolve(dict["Encoding"]);
        if (encObj is PdfName en)
        {
            _cmap = CMap.Identity(en.Value.EndsWith("-V", StringComparison.Ordinal));
            _unicodeCodes = en.Value.StartsWith("Uni", StringComparison.Ordinal) && (en.Value.Contains("UCS2") || en.Value.Contains("UTF16"));
        }
        else if (encObj is PdfStream es)
        {
            _cmap = CMap.Parse(file.DecodeStream(es), file) ?? CMap.Identity(false);
        }
        else _cmap = CMap.Identity(false);
        Vertical = _cmap.Vertical;

        var descendants = file.GetArray(dict, "DescendantFonts");
        var cidFont = descendants != null && descendants.Count > 0 ? file.Resolve(descendants[0]) as PdfDict : null;
        if (cidFont == null) throw new PdfFormatException("Type0 font without a descendant font.");
        BaseFont = StripSubset(file.GetName(cidFont, "BaseFont") ?? BaseFont);

        _defaultWidth = file.GetNumber(cidFont, "DW", 1000);
        if (file.GetArray(cidFont, "W") is PdfArray w) ReadCidWidths(file, w);
        if (file.GetNumbers(cidFont, "DW2") is { Length: >= 2 } dw2) { _vDefaultY = dw2[0]; _vDefaultW1 = dw2[1]; }
        if (file.GetArray(cidFont, "W2") is PdfArray w2) ReadVerticalMetrics(file, w2);

        var desc = file.GetDict(cidFont, "FontDescriptor");
        var ff2 = file.GetStream(desc, "FontFile2");
        var ff3 = file.GetStream(desc, "FontFile3");
        var ff = file.GetStream(desc, "FontFile");
        var subtype = file.GetName(cidFont, "Subtype");

        if (ff2 != null || ff3 != null && file.GetName(ff3, "Subtype") == "OpenType")
        {
            var tt = TrueTypeFont.Parse(file.DecodeStream((ff2 ?? ff3)!));
            if (tt != null && tt.HasGlyf)
            {
                Source = tt;
                var map = file.Resolve(cidFont["CIDToGIDMap"]);
                if (map is PdfStream ms)
                {
                    var b = file.DecodeStream(ms);
                    _cidToGid = new int[b.Length / 2];
                    for (int i = 0; i < _cidToGid.Length; i++) _cidToGid[i] = b[2 * i] << 8 | b[2 * i + 1];
                }
                return;
            }
            if (tt?.CffTable is byte[] cffData && CffFont.Parse(cffData) is CffFont otc)
            {
                Source = otc;
                if (otc.IsCid) _cidCff = otc;
                return;
            }
        }
        if (ff3 != null && CffFont.Parse(file.DecodeStream(ff3)) is CffFont cff)
        {
            Source = cff;
            if (cff.IsCid) _cidCff = cff;
            return;
        }
        if (ff != null && Type1Font.Parse(file.DecodeStream(ff), file.GetInt(ff, "Length1", 0)) is Type1Font t1)
        {
            Source = t1;
            return;
        }
        // Not embedded: map through ToUnicode to a built-in face (covers Latin text only).
        var toUni = file.GetStream(dict, "ToUnicode") is PdfStream tus ? CMap.Parse(file.DecodeStream(tus)) : null;
        var desc2 = file.GetDict(cidFont, "FontDescriptor");
        int flags = file.GetInt(desc2, "Flags", 0);
        string lower = BaseFont.ToLowerInvariant();
        bool mono = lower.Contains("mono") || lower.Contains("courier") || (flags & 1) != 0;
        bool serif = !mono && ((flags & 2) != 0 || lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("mincho") || lower.Contains("song"));
        bool bold = lower.Contains("bold") || lower.Contains("black");
        bool italic = lower.Contains("italic") || lower.Contains("oblique");
        var face = StandardFontPack.Face((mono ? "Mono" : serif ? "Serif" : "Sans") + (bold && italic ? "-BoldItalic" : bold ? "-Bold" : italic ? "-Italic" : ""));
        Source = face;
        if (face != null)
        {
            var cmap = _cmap;
            _cidFallbackGid = code =>
            {
                var s = toUni?.ToUnicode((uint)code);
                if (s == null || s.Length == 0)
                {
                    // Identity encodings of Latin fonts often carry the character code itself.
                    int cid = cmap.ToCid((uint)code);
                    return cid < 0x250 ? face.Lookup(cid) : 0;
                }
                return face.Lookup(char.ConvertToUtf32(s, 0));
            };
        }
    }

    private void ReadCidWidths(PdfFile file, PdfArray w)
    {
        int i = 0;
        while (i < w.Count)
        {
            int first = file.GetIntValue(w[i]);
            if (i + 1 >= w.Count) break;
            var next = file.Resolve(w[i + 1]);
            if (next is PdfArray list)
            {
                for (int k = 0; k < list.Count && k < 65536; k++) _cidWidths[first + k] = file.Num(list[k]);
                i += 2;
            }
            else
            {
                if (i + 2 >= w.Count) break;
                int last = file.GetIntValue(next);
                double width = file.Num(w[i + 2]);
                if (last - first <= 65536) for (int c = first; c <= last; c++) _cidWidths[c] = width;
                i += 3;
            }
        }
    }

    private void ReadVerticalMetrics(PdfFile file, PdfArray w2)
    {
        int i = 0;
        while (i < w2.Count)
        {
            int first = file.GetIntValue(w2[i]);
            if (i + 1 >= w2.Count) break;
            var next = file.Resolve(w2[i + 1]);
            if (next is PdfArray list)
            {
                for (int k = 0; k + 2 < list.Count; k += 3) _vMetrics[first + k / 3] = (file.Num(list[k]), file.Num(list[k + 1]), file.Num(list[k + 2]));
                i += 2;
            }
            else
            {
                if (i + 4 >= w2.Count) break;
                int last = file.GetIntValue(next);
                var m = (file.Num(w2[i + 2]), file.Num(w2[i + 3]), file.Num(w2[i + 4]));
                if (last - first <= 65536) for (int c = first; c <= last; c++) _vMetrics[c] = m;
                i += 5;
            }
        }
    }

    // ------------------------------------------------------------------ Type 3

    private void LoadType3(PdfFile file, PdfDict dict)
    {
        Kind = FontKind.Type3;
        FontMatrix = Matrix.FromArray(file.GetNumbers(dict, "FontMatrix") ?? new double[] { 0.001, 0, 0, 0.001, 0, 0 });
        Type3Resources = file.GetDict(dict, "Resources");
        int first = file.GetInt(dict, "FirstChar", 0);
        var widths = file.GetNumbers(dict, "Widths");
        for (int c = 0; c < 256; c++)
        {
            int i = c - first;
            _widths[c] = widths != null && i >= 0 && i < widths.Length ? widths[i] : 0;
        }
        var procs = file.GetDict(dict, "CharProcs");
        Type3Procs = new PdfStream?[256];
        var enc = file.GetDict(dict, "Encoding");
        if (procs == null || enc == null) return;
        if (file.GetArray(enc, "Differences") is PdfArray da)
        {
            int code = 0;
            foreach (var item in da.Items)
            {
                var o = file.Resolve(item);
                if (o is PdfNumber n) code = n.IntValue;
                else if (o is PdfName nm)
                {
                    if (code >= 0 && code < 256)
                    {
                        Type3Procs[code] = file.Resolve(procs[nm.Value]) as PdfStream;
                        _encNames[code] = nm.Value;
                    }
                    code++;
                }
            }
        }
    }
}

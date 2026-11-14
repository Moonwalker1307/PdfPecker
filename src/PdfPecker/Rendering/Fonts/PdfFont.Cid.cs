using System.Buffers;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>Type 0 (CID) fonts: the CMap, the descendant font's widths and program, and the fallback for Latin text.</summary>
internal sealed partial class PdfFont
{
    /// <summary>A W or W2 array range longer than this is ignored, so a damaged array cannot fill memory.</summary>
    private const int MaxCidRange = 65536;

    /// <summary>
    /// The entries one W or W2 array may set in all. Each range is bounded on its own, but many of them would still
    /// take unbounded time, and no font has more CIDs than this.
    /// </summary>
    private const long MaxCidEntries = 1 << 20;

    /// <summary>The Latin fallback of a non-embedded CID font treats CIDs below this as character codes.</summary>
    private const int LatinCidLimit = 0x250;

    private void LoadType0(PdfFile file, PdfDict dict)
    {
        Kind = FontKind.Cid;
        var encodingObject = file.Resolve(dict["Encoding"]);
        if (encodingObject is PdfName encodingName)
        {
            // The Chinese, Japanese and Korean predefined CMaps are Adobe's own; a name the library does not have is read
            // as Identity. The codes of the Unicode ones are also the text.
            string name = encodingName.Value;
            var predefined = name.StartsWith("Identity", StringComparison.Ordinal) ? null : PredefinedCMaps.Get(name);
            _cmap = predefined ?? CMap.Identity(name.EndsWith("-V", StringComparison.Ordinal));
            if (predefined != null) _predefinedOrdering = PredefinedCMaps.OrderingOf(name);
            _unicodeCodes = name.StartsWith("Uni", StringComparison.Ordinal) && (name.Contains("UCS2") || name.Contains("UTF16"));
        }
        else if (encodingObject is PdfStream encodingStream)
        {
            var data = file.DecodeStream(encodingStream);
            // An embedded CMap may name a predefined one it builds on in its dictionary instead of with usecmap.
            if (file.GetName(encodingStream, "UseCMap") is { } useName && PredefinedCMaps.Get(useName) is { } parent)
            {
                _cmap = CMap.Over(parent, data);
                _predefinedOrdering = PredefinedCMaps.OrderingOf(useName);
            }
            else _cmap = CMap.Parse(data) ?? CMap.Identity(false);
        }
        else
        {
            _cmap = CMap.Identity(false);
        }
        Vertical = _cmap.Vertical;

        var descendants = file.GetArray(dict, "DescendantFonts");
        var cidFont = descendants != null && descendants.Count > 0 ? file.Resolve(descendants[0]) as PdfDict : null;
        if (cidFont == null) throw new PdfFormatException("Type0 font without a descendant font.");
        BaseFont = StripSubset(file.GetName(cidFont, "BaseFont") ?? BaseFont);

        _defaultWidth = file.GetNumber(cidFont, "DW", 1000);
        if (file.GetArray(cidFont, "W") is { } widths) ReadCidWidths(file, widths);
        if (file.GetNumbers(cidFont, "DW2") is { Length: >= 2 } dw2)
        {
            _vDefaultY = dw2[0];
            _vDefaultW1 = dw2[1];
        }
        if (file.GetArray(cidFont, "W2") is { } verticalMetrics) ReadVerticalMetrics(file, verticalMetrics);

        var systemInfo = file.GetDict(cidFont, "CIDSystemInfo");
        _ordering = (file.Resolve(systemInfo?["Registry"]) as PdfString)?.Text == "Adobe"
            ? (file.Resolve(systemInfo?["Ordering"]) as PdfString)?.Text
            : null;
        if (PredefinedCMaps.CidToUnicode(_ordering) == null) _ordering = _predefinedOrdering;

        if (LoadCidProgram(file, cidFont))
        {
            if (Source is TrueTypeFont trueType && _cidToGid == null && _predefinedOrdering != null) UseUnicodeCmap(trueType);
            return;
        }
        SubstituteCidFont(file, dict, cidFont);
    }

    /// <summary>The character a code stands for by its CID in the font's Adobe ordering, or -1.</summary>
    private int OrderingCodePoint(int code)
    {
        if (_unicodeCodes) return code is > 0 and <= 0xFFFF && !char.IsSurrogate((char)code) ? code : -1;
        var map = _cidUnicode ??= PredefinedCMaps.CidToUnicode(_ordering);
        if (map?.ToUnicode((uint)_cmap!.ToCid((uint)code)) is not { Length: > 0 } text) return -1;
        return Rune.DecodeFromUtf16(text, out var rune, out _) == OperationStatus.Done ? rune.Value : -1;
    }

    /// <summary>
    /// An embedded TrueType font with a predefined CJK CMap and no CIDToGIDMap: its glyphs are not in the order of the
    /// Adobe CIDs, so they are found through the font's own Unicode cmap, as other readers do. Adobe's orderings give
    /// their proportional spaces as en and em spaces, which a font may only have as a plain space; a character the
    /// font lacks shows nothing, since its CID is no glyph of this font.
    /// </summary>
    private void UseUnicodeCmap(TrueTypeFont trueType)
    {
        var bmp = trueType.GetCmap(3, 1);
        var full = trueType.GetCmap(3, 10);
        if (bmp == null && full == null) return;
        _cidFallbackGid = code =>
        {
            int u = OrderingCodePoint(code);
            int gid = Find(u);
            if (gid == 0 && u is >= 0x2000 and <= 0x200A) gid = Find(' ');
            return gid;
        };

        int Find(int u)
        {
            int gid = 0;
            if (u >= 0 && !(bmp != null && bmp.TryGetValue(u, out gid)) && full != null) full.TryGetValue(u, out gid);
            return gid;
        }
    }

    /// <summary>The CID a code selects in a CID font, read through the font's CMap; the code itself in other fonts.</summary>
    internal int Cid(int code) => Kind == FontKind.Cid ? _cmap!.ToCid((uint)code) : code;

    /// <summary>
    /// Uses the descendant's embedded program: TrueType (with its CIDToGIDMap) or the CFF table of an OpenType
    /// FontFile3, a bare CFF FontFile3, or a Type 1 FontFile.
    /// </summary>
    private bool LoadCidProgram(PdfFile file, PdfDict cidFont)
    {
        var descriptor = file.GetDict(cidFont, "FontDescriptor");
        var fontFile2 = file.GetStream(descriptor, "FontFile2");
        var fontFile3 = file.GetStream(descriptor, "FontFile3");
        var fontFile = file.GetStream(descriptor, "FontFile");

        if (fontFile2 != null || fontFile3 != null && file.GetName(fontFile3, "Subtype") == "OpenType")
        {
            var trueType = TrueTypeFont.Parse(file.DecodeStream((fontFile2 ?? fontFile3)!));
            if (trueType != null && trueType.HasGlyf)
            {
                trueType.CheckTrickyName(BaseFont);
                Source = trueType;
                if (file.Resolve(cidFont["CIDToGIDMap"]) is PdfStream map) _cidToGid = ReadCidToGid(file.DecodeStream(map));
                return true;
            }
            if (trueType?.CffTable is { } cffTable && CffFont.Parse(cffTable) is { } openTypeCff)
            {
                UseCff(openTypeCff);
                return true;
            }
        }
        if (fontFile3 != null && CffFont.Parse(file.DecodeStream(fontFile3)) is { } cff)
        {
            UseCff(cff);
            return true;
        }
        if (fontFile != null && Type1Font.Parse(file.DecodeStream(fontFile), file.GetInt(fontFile, "Length1", 0)) is { } type1)
        {
            Source = type1;
            return true;
        }
        return false;
    }

    // A CID-keyed CFF maps CIDs through its charset; a name-keyed one is used by glyph index.
    private void UseCff(CffFont cff)
    {
        Source = cff;
        if (cff.IsCid) _cidCff = cff;
    }

    // The CIDToGIDMap stream: a big-endian glyph id per CID.
    internal static int[] ReadCidToGid(byte[] bytes)
    {
        var map = new int[bytes.Length / 2];
        for (int i = 0; i < map.Length; i++) map[i] = bytes[2 * i] << 8 | bytes[2 * i + 1];
        return map;
    }

    // Not embedded: map through ToUnicode to a built-in face (covers Latin text only).
    private void SubstituteCidFont(PdfFile file, PdfDict dict, PdfDict cidFont)
    {
        var toUnicode = file.GetStream(dict, "ToUnicode") is { } stream ? CMap.Parse(file.DecodeStream(stream)) : null;
        var descriptor = file.GetDict(cidFont, "FontDescriptor");
        int flags = file.GetInt(descriptor, "Flags", 0);
        string lower = BaseFont.ToLowerInvariant();
        bool mono = lower.Contains("mono") || lower.Contains("courier") || (flags & FixedPitchFlag) != 0;
        bool serif = !mono && ((flags & SerifFlag) != 0 || lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("mincho") || lower.Contains("song"));
        bool bold = lower.Contains("bold") || lower.Contains("black");
        bool italic = lower.Contains("italic") || lower.Contains("oblique");
        var face = StandardFontPack.Face(FaceName(mono, serif, bold, italic));
        Source = face;
        if (face == null) return;
        var cmap = _cmap!;
        _cidFallbackGid = code =>
        {
            var text = toUnicode?.ToUnicode((uint)code);
            if ((text == null || text.Length == 0) && OrderingCodePoint(code) is >= 0 and var u)
                return face.Lookup(u) is > 0 and var gid ? gid : u is >= 0x2000 and <= 0x200A ? face.Lookup(' ') : 0;
            if (text == null || text.Length == 0)
            {
                // Identity encodings of Latin fonts often carry the character code itself.
                int cid = cmap.ToCid((uint)code);
                return cid < LatinCidLimit ? face.Lookup(cid) : 0;
            }
            // A damaged ToUnicode map can give a lone surrogate, which names no character the face could have.
            return Rune.DecodeFromUtf16(text, out var rune, out _) == OperationStatus.Done ? face.Lookup(rune.Value) : 0;
        };
    }

    // W: "c [w1 w2 ...]" runs and "cfirst clast w" ranges.
    private void ReadCidWidths(PdfFile file, PdfArray w)
    {
        int i = 0;
        long budget = MaxCidEntries;
        while (i < w.Count)
        {
            int first = file.GetIntValue(w[i]);
            if (i + 1 >= w.Count) break;
            var next = file.Resolve(w[i + 1]);
            if (next is PdfArray list)
            {
                int count = (int)Math.Min(Math.Min(list.Count, MaxCidRange), budget);
                budget -= count;
                for (int k = 0; k < count; k++) _cidWidths[first + k] = file.Num(list[k]);
                i += 2;
            }
            else
            {
                if (i + 2 >= w.Count) break;
                int last = file.GetIntValue(next);
                double width = file.Num(w[i + 2]);
                // Long arithmetic, because a range ending at int.MaxValue would otherwise wrap and never end.
                long span = (long)last - first;
                if (span >= 0 && span <= MaxCidRange && span < budget)
                {
                    budget -= span + 1;
                    for (long c = first; c <= last; c++) _cidWidths[(int)c] = width;
                }
                i += 3;
            }
        }
    }

    // W2: "c [w1y vx vy ...]" runs and "cfirst clast w1y vx vy" ranges.
    private void ReadVerticalMetrics(PdfFile file, PdfArray w2)
    {
        int i = 0;
        long budget = MaxCidEntries;
        while (i < w2.Count)
        {
            int first = file.GetIntValue(w2[i]);
            if (i + 1 >= w2.Count) break;
            var next = file.Resolve(w2[i + 1]);
            if (next is PdfArray list)
            {
                int count = (int)Math.Min(list.Count / 3, budget);
                budget -= count;
                for (int k = 0; k < count; k++) _vMetrics[first + k] = (file.Num(list[3 * k]), file.Num(list[3 * k + 1]), file.Num(list[3 * k + 2]));
                i += 2;
            }
            else
            {
                if (i + 4 >= w2.Count) break;
                int last = file.GetIntValue(next);
                var metrics = (file.Num(w2[i + 2]), file.Num(w2[i + 3]), file.Num(w2[i + 4]));
                long span = (long)last - first;
                if (span >= 0 && span <= MaxCidRange && span < budget)
                {
                    budget -= span + 1;
                    for (long c = first; c <= last; c++) _vMetrics[(int)c] = metrics;
                }
                i += 5;
            }
        }
    }
}

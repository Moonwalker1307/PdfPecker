using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>Simple fonts: the Widths and Encoding entries, and mapping codes to the glyphs of an embedded program.</summary>
internal sealed partial class PdfFont
{
    // Symbolic TrueType fonts often key their (3,0) cmap by the code in one of these high-byte ranges.
    private static readonly int[] SymbolPrefixesCodeFirst = { 0xF000, 0, 0xF100, 0xF200 };
    private static readonly int[] SymbolPrefixesPlainFirst = { 0, 0xF000, 0xF100, 0xF200 };

    private void LoadSimple(PdfFile file, PdfDict dict, string? subtype)
    {
        Kind = FontKind.Simple;
        Array.Fill(_hScale, 1f);
        var descriptor = file.GetDict(dict, "FontDescriptor");
        int flags = file.GetInt(descriptor, "Flags", 0);
        bool symbolic = (flags & SymbolicFlag) != 0 && (flags & NonsymbolicFlag) == 0;
        bool haveWidths = ReadSimpleWidths(file, dict, descriptor);

        // Encoding: base table plus differences.
        string?[]? baseEncoding = null;
        var differences = new string?[256];
        bool hasDifferences = false;
        var encodingObject = file.Resolve(dict["Encoding"]);
        if (encodingObject is PdfName encodingName)
        {
            baseEncoding = Encodings.ByName(encodingName.Value);
        }
        else if (encodingObject is PdfDict encodingDict)
        {
            baseEncoding = Encodings.ByName(file.GetName(encodingDict, "BaseEncoding"));
            if (file.GetArray(encodingDict, "Differences") is { } array)
            {
                foreach (var (code, name) in Differences(file, array))
                {
                    if (code < 0 || code >= 256) continue;
                    differences[code] = name;
                    hasDifferences = true;
                }
            }
        }
        // A symbolic font, a TrueType font without an Encoding, or the standard Symbol and ZapfDingbats fonts (which usually
        // come without a descriptor to say they are symbolic) use their built-in encoding rather than the standard one.
        bool builtinEncoding = symbolic || subtype == "TrueType" && encodingObject == null || IsStandardSymbolFont(BaseFont);
        for (int c = 0; c < 256; c++) _encNames[c] = differences[c] ?? baseEncoding?[c] ?? (builtinEncoding ? null : Encodings.Standard[c]);

        if (LoadSimpleProgram(file, descriptor, baseEncoding, differences, symbolic, flags, encodingObject != null, haveWidths)) return;
        Substitute(file, dict, baseEncoding, hasDifferences ? differences : null);
    }

    private static bool IsStandardSymbolFont(string baseFont)
    {
        var name = baseFont.AsSpan();
        if (name.Length > 7 && name[6] == '+') name = name[7..]; // a subset tag such as ABCDEF+
        int comma = name.IndexOf(',');
        if (comma >= 0) name = name[..comma]; // a style suffix such as ,Bold
        return name is "Symbol" or "ZapfDingbats";
    }

    // The Widths array, with MissingWidth for the codes outside it; false when the font has no widths at all.
    private bool ReadSimpleWidths(PdfFile file, PdfDict dict, PdfDict? descriptor)
    {
        int first = file.GetInt(dict, "FirstChar", 0);
        var widths = file.GetNumbers(dict, "Widths");
        double missing = file.GetNumber(descriptor, "MissingWidth", 0);
        bool haveWidths = widths != null && widths.Length > 0;
        for (int c = 0; c < 256; c++)
        {
            int i = c - first;
            _widths[c] = haveWidths && i >= 0 && i < widths!.Length ? widths[i] : missing;
        }
        return haveWidths;
    }

    /// <summary>
    /// Uses the embedded program when there is one it can read: FontFile2 or an OpenType FontFile3 as TrueType (or
    /// its CFF table), FontFile3 as bare CFF, FontFile as Type 1.
    /// </summary>
    private bool LoadSimpleProgram(PdfFile file, PdfDict? descriptor, string?[]? baseEncoding, string?[] differences, bool symbolic, int flags, bool hasEncoding, bool haveWidths)
    {
        var fontFile = file.GetStream(descriptor, "FontFile");
        var fontFile2 = file.GetStream(descriptor, "FontFile2");
        var fontFile3 = file.GetStream(descriptor, "FontFile3");

        if (fontFile2 != null || fontFile3 != null && file.GetName(fontFile3, "Subtype") == "OpenType")
        {
            var trueType = TrueTypeFont.Parse(file.DecodeStream((fontFile2 ?? fontFile3)!));
            if (trueType != null && trueType.HasGlyf)
            {
                Source = trueType;
                MapTrueType(trueType, baseEncoding, differences, symbolic, hasEncoding);
                FillMissingWidths(haveWidths, trueType);
                return true;
            }
            if (trueType?.CffTable is { } cffTable && CffFont.Parse(cffTable) is { } openTypeCff)
            {
                Source = openTypeCff;
                MapCff(openTypeCff, baseEncoding, differences, symbolic);
                FillMissingWidths(haveWidths, openTypeCff);
                return true;
            }
        }
        if (fontFile3 != null && CffFont.Parse(file.DecodeStream(fontFile3)) is { } cff)
        {
            Source = cff;
            MapCff(cff, baseEncoding, differences, symbolic);
            FillMissingWidths(haveWidths, cff);
            return true;
        }
        if (fontFile != null && Type1Font.Parse(file.DecodeStream(fontFile), file.GetInt(fontFile, "Length1", 0)) is { } type1)
        {
            Source = type1;
            MapType1(type1, baseEncoding, differences, symbolic);
            FillMissingWidths(haveWidths, type1);
            return true;
        }
        return false;
    }

    // Without a Widths array the advances come from the program.
    private void FillMissingWidths(bool haveWidths, IGlyphSource source)
    {
        if (haveWidths) return;
        double scale = source.FontMatrix.A * GlyphUnitsPerTextUnit;
        for (int c = 0; c < 256; c++)
        {
            double advance = source.Advance(_codeToGid[c]);
            if (advance >= 0) _widths[c] = advance * scale;
        }
    }

    // Type 1: the Differences, then the base encoding (unless symbolic), then the program's own encoding.
    private void MapType1(Type1Font type1, string?[]? baseEncoding, string?[] differences, bool symbolic)
    {
        for (int c = 0; c < 256; c++)
        {
            string? name = differences[c] ?? (baseEncoding != null && !symbolic ? baseEncoding[c] : null) ?? type1.BuiltinEncoding[c] ?? baseEncoding?[c];
            int gid = name != null ? type1.GlyphByName(name) : -1;
            if (gid < 0 && type1.BuiltinEncoding[c] is { } builtinName) gid = type1.GlyphByName(builtinName);
            _codeToGid[c] = Math.Max(0, gid < 0 ? type1.GlyphByName(".notdef") : gid);
            if (name != null) _encNames[c] = name;
        }
    }

    // CFF: names from the Differences and base encoding, then the program's built-in encoding, then CIDs for a
    // CID-keyed program used as a simple font.
    private void MapCff(CffFont cff, string?[]? baseEncoding, string?[] differences, bool symbolic)
    {
        for (int c = 0; c < 256; c++)
        {
            int gid = -1;
            string? name = differences[c] ?? (symbolic ? null : baseEncoding?[c]);
            if (name != null) gid = cff.GlyphByName(name);
            if (gid <= 0) gid = cff.BuiltinEncoding[c];
            if (gid <= 0 && name == null && baseEncoding?[c] is { } baseName) gid = cff.GlyphByName(baseName);
            if (gid <= 0 && cff.IsCid) gid = cff.GidForCid(c);
            _codeToGid[c] = Math.Max(0, gid);
            if (name == null && gid > 0 && cff.GlyphName(gid) is { } glyphName) _encNames[c] = glyphName;
        }
    }

    // TrueType: through the cmap subtables in the order PDF readers have settled on, then the post table names.
    private void MapTrueType(TrueTypeFont trueType, string?[]? baseEncoding, string?[] differences, bool symbolic, bool hasEncoding)
    {
        var unicodeMap = trueType.GetCmap(3, 1) ?? trueType.GetCmap(0, 3) ?? trueType.GetCmap(0, 4) ?? trueType.GetCmap(0, 1) ?? trueType.GetCmap(0, 0);
        var symbolMap = trueType.GetCmap(3, 0);
        var macMap = trueType.GetCmap(1, 0);
        var encoding = baseEncoding ?? (symbolic ? null : Encodings.Standard);
        for (int c = 0; c < 256; c++)
        {
            int gid = 0;
            string? name = differences[c] ?? encoding?[c];
            // Symbolic fonts with a (3,0) or Mac cmap are keyed by the codes themselves (subsetters write them so).
            if (symbolic && (symbolMap != null || macMap != null && unicodeMap == null))
            {
                if (symbolMap != null) gid = LookupWithPrefixes(symbolMap, c, SymbolPrefixesCodeFirst);
                if (gid == 0 && macMap != null && macMap.TryGetValue(c, out int macGid)) gid = macGid;
            }
            if (gid == 0 && (!symbolic || differences[c] != null) && name != null) gid = GlyphByName(trueType, unicodeMap, macMap, name);
            if (gid == 0 && symbolMap != null) gid = LookupWithPrefixes(symbolMap, c, SymbolPrefixesPlainFirst);
            if (gid == 0 && macMap != null && macMap.TryGetValue(c, out int macGid2)) gid = macGid2;
            if (gid == 0 && unicodeMap != null && (symbolic || !hasEncoding) && unicodeMap.TryGetValue(c, out int unicodeGid)) gid = unicodeGid;
            if (gid == 0 && unicodeMap == null && symbolMap == null && macMap == null && trueType.AnyCmap() is { } any && any.TryGetValue(c, out int anyGid)) gid = anyGid;
            if (gid == 0 && !trueType.HasAnyCmap) gid = c; // no cmap at all: codes are glyph ids
            _codeToGid[c] = gid;
        }
    }

    // A named character: its Unicode value in the Unicode cmap, its Mac Roman code in the Mac cmap, or its post name,
    // whichever first gives a glyph.
    private static int GlyphByName(TrueTypeFont trueType, Dictionary<int, int>? unicodeMap, Dictionary<int, int>? macMap, string name)
    {
        int gid = 0;
        if (unicodeMap != null)
        {
            int u = Encodings.NameToUnicode(name);
            if (u >= 0 && unicodeMap.TryGetValue(u, out int unicodeGid)) gid = unicodeGid;
        }
        if (gid == 0 && macMap != null)
        {
            int macCode = Array.IndexOf(Encodings.MacRoman, name);
            if (macCode >= 0 && macMap.TryGetValue(macCode, out int macGid)) gid = macGid;
        }
        if (gid == 0)
        {
            int postGid = trueType.GlyphByName(name);
            if (postGid > 0) gid = postGid;
        }
        return gid;
    }

    private static int LookupWithPrefixes(Dictionary<int, int> map, int code, int[] prefixes)
    {
        foreach (int prefix in prefixes)
        {
            if (map.TryGetValue(prefix + code, out int gid)) return gid;
        }
        return 0;
    }
}

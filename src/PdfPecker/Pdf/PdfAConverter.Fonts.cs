using System.Globalization;
using System.Text;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>Fonts (6.2.11): embedding, the font dictionaries' required entries, CMaps, widths, glyphs and ToUnicode.</summary>
internal sealed partial class PdfAConverter
{
    private const int SymbolicFlag = 4, NonsymbolicFlag = 32;

    // The CMaps ISO 32000-1 predefines (Table 118); others must be embedded (6.2.11.3.3). The table spells the GB and CNS
    // UTF-16 CMaps "UFT16", and veraPDF holds files to that text, so the correctly spelled ones count as not predefined.
    private static readonly HashSet<string> PredefinedCMaps = new(StringComparer.Ordinal)
    {
        "Identity-H", "Identity-V",
        "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V", "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V",
        "UniGB-UCS2-H", "UniGB-UCS2-V", "UniGB-UFT16-H", "UniGB-UFT16-V",
        "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V", "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V",
        "UniCNS-UCS2-H", "UniCNS-UCS2-V", "UniCNS-UFT16-H", "UniCNS-UTF16-V",
        "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H", "Add-RKSJ-H", "Add-RKSJ-V",
        "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V", "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
        "UniJIS-UTF16-H", "UniJIS-UTF16-V",
        "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V", "KSCpc-EUC-H",
        "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
    };

    /// <summary>The character collection (ordering) of a predefined CMap other than Identity.</summary>
    private static string? CMapOrdering(string name)
    {
        if (name.StartsWith("Identity", StringComparison.Ordinal)) return null;
        if (name.Contains("GB", StringComparison.Ordinal)) return "GB1";
        if (name.Contains("CNS", StringComparison.Ordinal) || name.Contains("B5", StringComparison.Ordinal)) return "CNS1";
        if (name.Contains("KS", StringComparison.Ordinal)) return "Korea1";
        return "Japan1";
    }

    private void Fonts()
    {
        // Every font the resources hold, used or not: a font no text uses still needs a valid dictionary, and is embedded
        // with no glyphs so that it costs almost nothing.
        var fonts = new List<PdfDict>();
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        foreach (var dict in _fontUses.Keys)
            if (seen.Add(dict)) fonts.Add(dict);
        foreach (var resources in _resources)
        {
            if (_file.GetDict(resources, "Font") is not { } fontResources) continue;
            foreach (var value in fontResources.Entries.Values)
                if (_file.Resolve(value) is PdfDict dict && seen.Add(dict)) fonts.Add(dict);
        }
        foreach (var dict in fonts)
        {
            PdfFile.CheckCancel();
            _fontUses.TryGetValue(dict, out var use);
            if (use == null)
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
                use = new FontUse(dict, font);
            }
            Font(use);
        }
    }

    private void Font(FontUse use)
    {
        var dict = use.Dict;
        var name = Printable(FontName(dict));
        if (_file.GetName(dict, "Type") != "Font")
        {
            Found("Added the missing /Type entry of {0} font{s}.", "{0} font{s} lack the /Type /Font entry PDF requires{names}.", name);
            if (_fix) dict.Entries["Type"] = PdfName.Get("Font");
        }
        var subtype = _file.GetName(dict, "Subtype");
        if (subtype is not ("Type1" or "MMType1" or "TrueType" or "Type3" or "Type0"))
        {
            Problem("{0} font{s} have no font type PDF knows{names}.", name);
            return;
        }
        if (subtype == "Type3")
        {
            // Type 3 glyphs are drawn by the document's own procedures, which are walked with the content; level U still
            // needs their text.
            if (_unicode && use.Font != null) ToUnicode(use, name);
            return;
        }
        if (use.Font == null)
        {
            Problem("{0} font{s} cannot be read{names}.", name);
            return;
        }
        if (subtype == "Type0") CidFont(use, name);
        else SimpleFont(use, name);
        if (_unicode) ToUnicode(use, name);
    }

    // ------------------------------------------------------------------ simple fonts

    private void SimpleFont(FontUse use, string name)
    {
        var dict = use.Dict;
        var font = use.Font!;
        var descriptor = _file.GetDict(dict, "FontDescriptor");
        bool embedded = Embedded(descriptor, out var fontFile3Subtype);
        if (fontFile3Subtype is not (null or "Type1C" or "OpenType"))
            Problem("{0} font{s} embed a program of a type PDF/A does not allow{names}.", name);

        // Fonts with no program, or one PdfPecker cannot read (and draws with its own faces too), get the substitute.
        if (!embedded || font.Source is PackFace)
        {
            if (use.Visible.Count == 0 && use.Invisible.Count > 0)
                return; // only invisible text, which PDF/A lets use fonts that are not embedded (6.2.11.4.1)
            if (!_fix)
            {
                Problem(embedded ? "{0} font{s} embed a program that cannot be read{names}." : "{0} font{s} are not embedded{names}.", name);
                return;
            }
            EmbedSimple(use, name, embedded);
            return;
        }

        if (_file.GetArray(dict, "Widths") == null || _file.Resolve(dict["FirstChar"]) is not PdfNumber || _file.Resolve(dict["LastChar"]) is not PdfNumber)
        {
            Found("Added the widths of {0} embedded font{s} to their dictionaries, as PDF/A requires{names}.",
                "{0} embedded font{s} have no Widths array{names}.", name);
            if (_fix) WriteSimpleWidths(dict, font, Enumerable.Range(0, 256));
        }
        if (_file.GetName(dict, "Subtype") is "Type1" or "MMType1" && descriptor != null && descriptor.ContainsKey("CharSet"))
        {
            // A CharSet must list every glyph of the program (6.2.11.4.2); it is optional, and nothing needs it.
            if (_fix)
            {
                descriptor.Entries.Remove("CharSet");
                _report.Add("Removed the glyph lists (CharSet) of {0} embedded Type 1 font{s}; PDF/A requires them to be complete, and nothing needs them.");
            }
        }
        EmbeddedGlyphs(use, name);
    }

    /// <summary>Whether a descriptor embeds a program; also the Subtype of a FontFile3 stream.</summary>
    private bool Embedded(PdfDict? descriptor, out string? fontFile3Subtype)
    {
        fontFile3Subtype = null;
        if (descriptor == null) return false;
        if (_file.GetStream(descriptor, "FontFile3") is { } ff3)
        {
            fontFile3Subtype = _file.GetName(ff3, "Subtype") ?? "";
            return true;
        }
        return _file.GetStream(descriptor, "FontFile") != null || _file.GetStream(descriptor, "FontFile2") != null;
    }

    private static void WriteSimpleWidths(PdfDict dict, PdfFont font, IEnumerable<int> codes)
    {
        var list = codes.ToList();
        if (list.Count == 0) list.Add(32);
        int first = list.Min(), last = list.Max();
        var widths = new List<PdfObject>(last - first + 1);
        for (int c = first; c <= last; c++) widths.Add(Number(font.Width(c) * 1000));
        dict.Entries["FirstChar"] = PdfNumber.Int(first);
        dict.Entries["LastChar"] = PdfNumber.Int(last);
        dict.Entries["Widths"] = new PdfArray(widths);
    }

    private static PdfNumber Number(double v)
    {
        double rounded = Math.Round(v, 3);
        return rounded == Math.Floor(rounded) && Math.Abs(rounded) < int.MaxValue ? PdfNumber.Int((long)rounded) : PdfNumber.Real(rounded);
    }

    /// <summary>
    /// Embeds a TrueType program for a simple font from the face PdfPecker draws it with: a glyph per code the text uses,
    /// at the font's own widths. The font becomes a symbolic TrueType font without an Encoding, whose codes select the
    /// glyphs through a (3,0) cmap, and gets a ToUnicode map so its text still reads the same.
    /// </summary>
    private void EmbedSimple(FontUse use, string name, bool damaged)
    {
        var dict = use.Dict;
        var font = use.Font!;
        if (font.Source is not PackFace face)
        {
            _report.Add("{0} font{s} are not embedded, and PdfPecker has no glyphs to embed for them{names}.", name);
            return;
        }
        var codes = use.AllCodes.Where(c => c is >= 0 and < 256).OrderBy(c => c).ToList();
        var glyphs = new List<PdfASubstituteFont.Glyph>(codes.Count);
        var cmap = new List<(int, int)>(codes.Count);
        var missing = new List<string>();
        foreach (int code in codes)
        {
            int gid = font.GlyphId(code);
            if (gid <= 0 && font.Unicode(code) is { } text && !string.IsNullOrWhiteSpace(text) && text.Any(ch => !char.IsControl(ch)) && use.Visible.Contains(code))
                missing.Add(text);
            int advance = (int)Math.Round(font.Width(code) * face.UnitsPerEm, MidpointRounding.AwayFromZero);
            glyphs.Add(new PdfASubstituteFont.Glyph(gid > 0 ? gid : -1, advance, font.HScale(code)));
            cmap.Add((code, glyphs.Count));
        }
        if (missing.Count > 0)
        {
            _report.Add("{0} font{s} are not embedded, and PdfPecker's fonts lack some of the characters they show, so they stay as they are{names}.",
                name + ": " + string.Join("", missing.Distinct().Take(8)));
            return;
        }
        var baseName = PostScriptName(font.BaseFont.Length > 0 ? font.BaseFont : FontName(dict));
        var (program, xMin, yMin, xMax, yMax) = PdfASubstituteFont.Build(face, glyphs, baseName, cmap, symbol: true);
        var fullName = SubsetTag(program) + "+" + baseName;

        var oldDescriptor = _file.GetDict(dict, "FontDescriptor");
        var descriptor = Descriptor(oldDescriptor, fullName, face, xMin, yMin, xMax, yMax, symbolic: true);
        descriptor.Entries["FontFile2"] = FontFile(program);

        // A font without ToUnicode reads its text through the Encoding, which a symbolic TrueType font may not have.
        if (dict["ToUnicode"] == null && codes.Count > 0) dict.Entries["ToUnicode"] = ToUnicodeStream(codes.Select(c => (c, 1, font.Unicode(c))));
        dict.Entries["Subtype"] = PdfName.Get("TrueType");
        dict.Entries["BaseFont"] = PdfName.Get(fullName);
        dict.Entries.Remove("Encoding");
        dict.Entries["FontDescriptor"] = _file.AddObject(descriptor);
        WriteSimpleWidths(dict, font, codes);
        _report.Add(damaged
            ? "Replaced the damaged programs of {0} font{s} with glyphs from PdfPecker's bundled fonts{names}."
            : "Embedded {0} font{s} that were not embedded, with glyphs from PdfPecker's bundled fonts at the document's widths{names}.", name);
    }

    /// <summary>A font descriptor for an embedded substitute, keeping the original's style flags and metrics where it had them.</summary>
    private PdfDict Descriptor(PdfDict? old, string fontName, PackFace face, int xMin, int yMin, int xMax, int yMax, bool symbolic)
    {
        double scale = 1000.0 / face.UnitsPerEm;
        int flags = _file.GetInt(old, "Flags", 0);
        if (old == null)
        {
            if (face.Name.Contains("Mono", StringComparison.Ordinal)) flags |= 1;
            if (face.Name.Contains("Serif", StringComparison.Ordinal)) flags |= 2;
            if (face.Name.Contains("Italic", StringComparison.Ordinal)) flags |= 64;
        }
        flags = symbolic ? (flags | SymbolicFlag) & ~NonsymbolicFlag : (flags | NonsymbolicFlag) & ~SymbolicFlag;
        double ascent = face.Ascent * scale, descent = face.Descent * scale;
        var d = NewDict(
            ("Type", PdfName.Get("FontDescriptor")),
            ("FontName", PdfName.Get(fontName)),
            ("Flags", PdfNumber.Int(flags)),
            ("FontBBox", NewArray(Number(xMin * scale), Number(yMin * scale), Number(xMax * scale), Number(yMax * scale))),
            ("ItalicAngle", Number(_file.GetNumber(old, "ItalicAngle", (flags & 64) != 0 ? -12 : 0))),
            ("Ascent", Number(ascent)),
            ("Descent", Number(descent)),
            ("CapHeight", Number(_file.GetNumber(old, "CapHeight", ascent * 0.7))),
            ("StemV", Number(_file.GetNumber(old, "StemV", 80))));
        foreach (var key in new[] { "FontFamily", "FontStretch", "FontWeight", "XHeight", "StemH", "Leading", "AvgWidth", "MaxWidth" })
            if (old?[key] is { } value && _file.Resolve(value) is PdfNumber or PdfString or PdfName) d.Entries[key] = _file.Resolve(value)!;
        return d;
    }

    private PdfRef FontFile(byte[] program)
        => _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Length1"] = PdfNumber.Int(program.Length),
            ["Filter"] = PdfName.Get("FlateDecode"),
        }, Deflate(program)));

    /// <summary>A PostScript name from a font name: printable ASCII without spaces or delimiters.</summary>
    private static string PostScriptName(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, 60));
        foreach (char c in name)
        {
            if (sb.Length >= 60) break;
            if (c is > ' ' and < '\u007f' and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%')) sb.Append(c);
        }
        return sb.Length == 0 ? "Font" : sb.ToString();
    }

    /// <summary>The six capital letters that mark a subset font, taken from its program so the same font gets the same tag.</summary>
    private static string SubsetTag(byte[] program)
    {
        uint hash = 2166136261;
        foreach (byte b in program) hash = (hash ^ b) * 16777619;
        var tag = new char[6];
        for (int i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + hash % 26);
            hash /= 26;
        }
        return new string(tag);
    }

    // ------------------------------------------------------------------ composite fonts

    private void CidFont(FontUse use, string name)
    {
        var type0 = use.Dict;
        var font = use.Font!;
        if (_file.GetArray(type0, "DescendantFonts") is not { Count: > 0 } descendants || _file.Resolve(descendants[0]) is not PdfDict cid)
        {
            Problem("{0} composite font{s} have no CIDFont{names}.", name);
            return;
        }
        var encoding = _file.Resolve(type0["Encoding"]);
        string? cmapName = encoding switch
        {
            PdfName n => n.Value,
            PdfStream s => _file.GetName(s, "CMapName"),
            _ => null,
        };
        if (encoding is PdfName && (cmapName == null || !PredefinedCMaps.Contains(cmapName)))
            Problem("{0} composite font{s} use a CMap that is neither predefined nor embedded{names}.", name);
        if (encoding is PdfStream cmapStream) EmbeddedCMap(cmapStream, name);

        CidSystemInfo(cid, encoding, cmapName, name);
        var descriptor = _file.GetDict(cid, "FontDescriptor");
        bool embedded = Embedded(descriptor, out var fontFile3Subtype);
        if (fontFile3Subtype is not (null or "CIDFontType0C" or "OpenType"))
            Problem("{0} font{s} embed a program of a type PDF/A does not allow{names}.", name);
        if (!embedded || font.Source is PackFace)
        {
            if (use.Visible.Count == 0 && use.Invisible.Count > 0) return;
            if (!_fix)
            {
                Problem(embedded ? "{0} font{s} embed a program that cannot be read{names}." : "{0} font{s} are not embedded{names}.", name);
                return;
            }
            EmbedCid(use, cid, encoding, cmapName, name, embedded);
            return;
        }

        if (descriptor != null && descriptor.ContainsKey("CIDSet") && _fix)
        {
            // A CIDSet must list every CID of the program (6.2.11.4.2); it is optional, and nothing needs it.
            descriptor.Entries.Remove("CIDSet");
            _report.Add("Removed the glyph lists (CIDSet) of {0} embedded CID font{s}; PDF/A requires them to be complete, and nothing needs them.");
        }
        if (font.Source is CffFont && _file.GetStream(descriptor, "FontFile2") is { } cffAsTrueType)
        {
            // A CFF program in FontFile2 is not a TrueType font, and validators cannot read its glyphs.
            if (_fix && MoveCff(use, type0, cid, descriptor!, cffAsTrueType))
                _report.Add("Moved the CFF programs of {0} font{s} that were stored as TrueType programs to where PDF/A expects them, keeping the glyphs viewers drew{names}.", name);
            else Problem("{0} font{s} store a CFF program as a TrueType one, which PDF/A does not allow{names}.", name);
            return;
        }
        if (_file.GetName(cid, "Subtype") == "CIDFontType2" && cid["CIDToGIDMap"] == null)
        {
            Found("Added the CIDToGIDMap that PDF requires to {0} embedded TrueType CID font{s}{names}.", "{0} embedded TrueType CID font{s} lack a CIDToGIDMap{names}.", name);
            if (_fix) cid.Entries["CIDToGIDMap"] = PdfName.Get("Identity");
        }
        EmbeddedGlyphs(use, name);
    }

    /// <summary>
    /// Makes a CIDFontType2 whose FontFile2 holds a CFF program a CIDFontType0 with the program in FontFile3. Viewers
    /// selected its glyphs through the CIDToGIDMap, which a CFF program does not use: it selects glyphs by CID. So the
    /// font gets an embedded CMap that maps each code it shows to the CID of the glyph viewers drew for it, and widths
    /// by those CIDs. Only horizontal fonts with two-byte codes are moved.
    /// </summary>
    private bool MoveCff(FontUse use, PdfDict type0, PdfDict cid, PdfDict descriptor, PdfStream fontFile2)
    {
        var font = use.Font!;
        var cff = (CffFont)font.Source!;
        if (font.Vertical || use.Lengths.Values.Any(l => l != 2)) return false;
        byte[] program;
        int[]? map = null;
        try
        {
            program = _file.DecodeStream(fontFile2);
            if (_file.Resolve(cid["CIDToGIDMap"]) is PdfStream mapStream) map = PdfFont.ReadCidToGid(_file.DecodeStream(mapStream));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return false;
        }
        bool openType = program.Length >= 4 && program.AsSpan(0, 4).SequenceEqual("OTTO"u8);

        var cids = new List<(int Code, int Cid)>();
        var widths = new SortedDictionary<int, double>();
        foreach (int code in use.AllCodes.Where(c => c is >= 0 and <= 0xFFFF).Order())
        {
            int oldCid = font.Cid(code);
            int gid = map != null ? oldCid < map.Length ? map[oldCid] : 0 : cff.GidForCid(oldCid);
            int newCid = gid > 0 ? cff.CidForGid(gid) : 0;
            cids.Add((code, newCid));
            widths.TryAdd(newCid, font.Width(code) * 1000);
        }

        var info = _file.GetDict(cid, "CIDSystemInfo");
        string registry = (info == null ? null : Text(info, "Registry")) ?? "Adobe", ordering = (info == null ? null : Text(info, "Ordering")) ?? "Identity";
        int supplement = _file.GetInt(info, "Supplement", 0);
        var cmapName = "PdfPecker-" + SubsetTag(program) + "-H";
        var systemInfo = NewDict(
            ("Registry", new PdfString(Encoding.ASCII.GetBytes(registry))),
            ("Ordering", new PdfString(Encoding.ASCII.GetBytes(ordering))),
            ("Supplement", PdfNumber.Int(supplement)));
        var cmap = new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Type"] = PdfName.Get("CMap"),
            ["CMapName"] = PdfName.Get(cmapName),
            ["CIDSystemInfo"] = systemInfo,
            ["WMode"] = PdfNumber.Int(0),
            ["Filter"] = PdfName.Get("FlateDecode"),
        }, Deflate(CidCMap(cmapName, registry, ordering, supplement, cids)));

        var w = new List<PdfObject>(widths.Count * 2);
        foreach (var (c, width) in widths)
        {
            w.Add(PdfNumber.Int(c));
            w.Add(NewArray(Number(width)));
        }
        var newDescriptor = new PdfDict();
        foreach (var (key, value) in descriptor.Entries) newDescriptor.Entries[key] = value;
        newDescriptor.Entries.Remove("FontFile2");
        newDescriptor.Entries.Remove("CIDSet");
        newDescriptor.Entries["FontFile3"] = _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
        {
            ["Subtype"] = PdfName.Get(openType ? "OpenType" : "CIDFontType0C"),
            ["Filter"] = PdfName.Get("FlateDecode"),
        }, Deflate(program)));
        cid.Entries["Subtype"] = PdfName.Get("CIDFontType0");
        cid.Entries["FontDescriptor"] = _file.AddObject(newDescriptor);
        cid.Entries["W"] = new PdfArray(w);
        cid.Entries.Remove("CIDToGIDMap");
        type0.Entries["Encoding"] = _file.AddObject(cmap);
        if (type0["ToUnicode"] == null && cids.Count > 0) type0.Entries["ToUnicode"] = ToUnicodeStream(cids.Select(c => (c.Code, 2, font.Unicode(c.Code))));
        return true;
    }

    /// <summary>The text of an embedded CMap that maps two-byte codes to CIDs.</summary>
    private static byte[] CidCMap(string name, string registry, string ordering, int supplement, List<(int Code, int Cid)> cids)
    {
        var sb = new StringBuilder();
        sb.Append("%!PS-Adobe-3.0 Resource-CMap\n/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        // The character collection is repeated in the program; parentheses and backslashes would end its strings early.
        static string Plain(string s) => new(s.Where(c => c is >= '!' and <= '~' and not ('(' or ')' or '\\')).ToArray());
        sb.Append("/CIDSystemInfo << /Registry (").Append(Plain(registry)).Append(") /Ordering (").Append(Plain(ordering)).Append(") /Supplement ")
            .Append(supplement.ToString(CultureInfo.InvariantCulture)).Append(" >> def\n");
        sb.Append("/CMapName /").Append(name).Append(" def\n/CMapType 1 def\n/WMode 0 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int i = 0; i < cids.Count; i += 100)
        {
            int n = Math.Min(100, cids.Count - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" begincidchar\n");
            for (int k = i; k < i + n; k++)
                sb.Append('<').Append(cids[k].Code.ToString("X4", CultureInfo.InvariantCulture)).Append("> ")
                    .Append(cids[k].Cid.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("endcidchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>The CIDFont's character collection must be the CMap's (6.2.11.3.1), unless the CMap is Identity.</summary>
    private void CidSystemInfo(PdfDict cid, PdfObject? encoding, string? cmapName, string name)
    {
        string? registry, ordering;
        int supplement;
        if (encoding is PdfStream s && _file.GetDict(s, "CIDSystemInfo") is { } info)
        {
            registry = Text(info, "Registry");
            ordering = Text(info, "Ordering");
            supplement = _file.GetInt(info, "Supplement", 0);
        }
        else if (cmapName != null && CMapOrdering(cmapName) is { } predefined)
        {
            registry = "Adobe";
            ordering = predefined;
            supplement = 0;
        }
        else return;
        if (registry == null || ordering == null || cmapName is "Identity-H" or "Identity-V") return;
        var own = _file.GetDict(cid, "CIDSystemInfo");
        if (own != null && Text(own, "Registry") == registry && Text(own, "Ordering") == ordering && _file.GetInt(own, "Supplement", int.MaxValue) <= supplement
            && encoding is PdfStream)
            return;
        if (own != null && Text(own, "Registry") == registry && Text(own, "Ordering") == ordering && encoding is PdfName)
        {
            // The supplement of a predefined CMap is not known here; the lowest is always compatible.
            if (_file.GetInt(own, "Supplement", -1) == 0) return;
        }
        Found("Made the character collection of {0} CID font{s} match their CMap, as PDF/A requires{names}.",
            "{0} CID font{s} have a character collection that differs from their CMap's{names}.", name);
        if (!_fix) return;
        cid.Entries["CIDSystemInfo"] = NewDict(
            ("Registry", new PdfString(Encoding.ASCII.GetBytes(registry))),
            ("Ordering", new PdfString(Encoding.ASCII.GetBytes(ordering))),
            ("Supplement", PdfNumber.Int(Math.Max(0, supplement))));
    }

    private string? Text(PdfDict dict, string key) => _file.Resolve(dict[key]) switch
    {
        PdfString s => Encoding.Latin1.GetString(s.Bytes),
        PdfName n => n.Value,
        _ => null,
    };

    /// <summary>An embedded CMap: its WMode must match the stream's own (6.2.11.3.3), and it may use only predefined CMaps.</summary>
    private void EmbeddedCMap(PdfStream cmap, string name)
    {
        if (cmap["UseCMap"] is { } use && (_file.Resolve(use) is not PdfName useName || !PredefinedCMaps.Contains(useName.Value)))
            Problem("{0} embedded CMap{s} use CMaps that are not predefined, which PDF/A forbids{names}.", name);
        byte[] data;
        try
        {
            data = _file.DecodeStream(cmap);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return;
        }
        var text = Encoding.Latin1.GetString(data);
        int at = text.IndexOf("/WMode", StringComparison.Ordinal);
        int streamMode = 0;
        if (at >= 0)
        {
            int i = at + 6;
            while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n') i++;
            if (i < text.Length && text[i] == '1') streamMode = 1;
        }
        if (_file.GetInt(cmap, "WMode", 0) == streamMode) return;
        Found("Made the writing mode of {0} embedded CMap{s} match the CMap itself{names}.", "{0} embedded CMap{s} state a writing mode that differs from the CMap's own{names}.", name);
        if (_fix) cmap.Entries["WMode"] = PdfNumber.Int(streamMode);
    }

    /// <summary>
    /// Embeds a TrueType CIDFont for a composite font from the face PdfPecker draws it with. Only CMaps whose codes are
    /// the CIDs (Identity) or Unicode (the predefined UCS2 CMaps, which become Identity) are handled, which covers the
    /// Latin fonts PdfPecker has glyphs for.
    /// </summary>
    private void EmbedCid(FontUse use, PdfDict cid, PdfObject? encoding, string? cmapName, string name, bool damaged)
    {
        var type0 = use.Dict;
        var font = use.Font!;
        bool identity = cmapName is "Identity-H" or "Identity-V";
        // The UTF-16 CMaps read two-byte codes as the UCS2 ones do; a four-byte code (a surrogate pair) rules them out below.
        bool ucs2 = encoding is PdfName && cmapName != null && cmapName.StartsWith("Uni", StringComparison.Ordinal)
            && (cmapName.Contains("UCS2", StringComparison.Ordinal) || cmapName.Contains("UTF16", StringComparison.Ordinal));
        if (font.Source is not PackFace face || !(identity || ucs2) || use.Lengths.Values.Any(l => l != 2))
        {
            _report.Add("{0} composite font{s} are not embedded, and PdfPecker has no glyphs to embed for them{names}.", name);
            return;
        }
        var codes = use.AllCodes.Where(c => c is >= 0 and <= 0xFFFF).OrderBy(c => c).ToList();
        var glyphs = new List<PdfASubstituteFont.Glyph>(codes.Count);
        var unicodeMap = new List<(int, int)>();
        var missing = new List<string>();
        foreach (int code in codes)
        {
            int gid = font.GlyphId(code);
            var text = font.Unicode(code);
            if (gid <= 0 && text is { } t && !string.IsNullOrWhiteSpace(t) && t.Any(ch => !char.IsControl(ch)) && use.Visible.Contains(code)) missing.Add(t);
            int advance = (int)Math.Round(font.Width(code) * face.UnitsPerEm, MidpointRounding.AwayFromZero);
            glyphs.Add(new PdfASubstituteFont.Glyph(gid > 0 ? gid : -1, advance, 1));
            if (text is { Length: 1 } single) unicodeMap.Add((single[0], glyphs.Count));
        }
        if (missing.Count > 0)
        {
            _report.Add("{0} font{s} are not embedded, and PdfPecker's fonts lack some of the characters they show, so they stay as they are{names}.",
                name + ": " + string.Join("", missing.Distinct().Take(8)));
            return;
        }
        var baseName = PostScriptName(font.BaseFont.Length > 0 ? font.BaseFont : FontName(type0));
        var (program, xMin, yMin, xMax, yMax) = PdfASubstituteFont.Build(face, glyphs, baseName, unicodeMap, symbol: false);
        var fullName = SubsetTag(program) + "+" + baseName;

        // With Identity (and the UCS2 CMaps turned into it) each code is its CID; the map gives each CID its new glyph.
        int maxCid = codes.Count == 0 ? 0 : codes[^1];
        var cidToGid = new byte[(maxCid + 1) * 2];
        var widths = new List<PdfObject>();
        for (int i = 0; i < codes.Count; i++)
        {
            int code = codes[i];
            cidToGid[2 * code] = (byte)((i + 1) >> 8);
            cidToGid[2 * code + 1] = (byte)(i + 1);
            widths.Add(PdfNumber.Int(code));
            widths.Add(NewArray(Number(font.Width(code) * 1000)));
        }
        var descriptor = Descriptor(_file.GetDict(cid, "FontDescriptor"), fullName, face, xMin, yMin, xMax, yMax, symbolic: true);
        descriptor.Entries["FontFile2"] = FontFile(program);
        var map = _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal) { ["Filter"] = PdfName.Get("FlateDecode") }, Deflate(cidToGid)));

        if (type0["ToUnicode"] == null && codes.Count > 0) type0.Entries["ToUnicode"] = ToUnicodeStream(codes.Select(c => (c, 2, font.Unicode(c))));
        bool vertical = cmapName!.EndsWith("-V", StringComparison.Ordinal);
        type0.Entries["Encoding"] = PdfName.Get(vertical ? "Identity-V" : "Identity-H");
        type0.Entries["BaseFont"] = PdfName.Get(fullName);
        cid.Entries["Subtype"] = PdfName.Get("CIDFontType2");
        cid.Entries["BaseFont"] = PdfName.Get(fullName);
        cid.Entries["CIDSystemInfo"] = NewDict(
            ("Registry", new PdfString("Adobe"u8.ToArray())),
            ("Ordering", new PdfString("Identity"u8.ToArray())),
            ("Supplement", PdfNumber.Int(0)));
        cid.Entries["FontDescriptor"] = _file.AddObject(descriptor);
        cid.Entries["CIDToGIDMap"] = map;
        cid.Entries["W"] = new PdfArray(widths);
        _report.Add(damaged
            ? "Replaced the damaged programs of {0} font{s} with glyphs from PdfPecker's bundled fonts{names}."
            : "Embedded {0} font{s} that were not embedded, with glyphs from PdfPecker's bundled fonts at the document's widths{names}.", name);
    }

    // ------------------------------------------------------------------ embedded programs

    /// <summary>
    /// The glyphs an embedded program must have for the text (6.2.11.4.1, 6.2.11.8), their widths, which must match the
    /// dictionary's (6.2.11.5), and for TrueType fonts how codes find glyphs (6.2.11.6). A TrueType program is rewritten
    /// to agree; for other programs (CFF and Type 1) these are reported, since changing their glyphs or the dictionary's
    /// widths would change what viewers show.
    /// </summary>
    private void EmbeddedGlyphs(FontUse use, string name)
    {
        var font = use.Font!;
        var source = font.Source;
        if (source == null || source is PackFace) return;
        var fontDict = DescendantOrSelf(use.Dict);
        bool composite = !ReferenceEquals(fontDict, use.Dict);
        var descriptor = _file.GetDict(fontDict, "FontDescriptor");
        // A FontFile2 that holds a CFF program is not a TrueType font, and validators cannot read its glyphs either.
        if (source is CffFont && _file.GetStream(descriptor, "FontFile2") != null)
        {
            if (!composite) Problem("{0} font{s} store a CFF program as a TrueType one, which PDF/A does not allow{names}.", name);
            return;
        }
        bool trueType = source is TrueTypeFont { HasGlyf: true } && _file.GetStream(descriptor, "FontFile2") != null;
        bool notdefIsZero = source is TrueTypeFont or CffFont;
        int missing = 0, widthDiffers = 0;
        double scale = source.FontMatrix.A * 1000;
        foreach (int code in use.AllCodes)
        {
            int gid = font.GlyphId(code);
            if (notdefIsZero && gid <= 0)
            {
                missing++;
                continue;
            }
            double program = source.Advance(gid);
            if (program < 0 || !use.Visible.Contains(code)) continue;
            if (Math.Abs(program * scale - font.Width(code) * 1000) > 1) widthDiffers++;
        }
        bool encodingWrong = trueType && !composite && TrueTypeEncodingWrong(use.Dict, descriptor, (TrueTypeFont)source);
        if (missing == 0 && widthDiffers == 0 && !encodingWrong) return;
        if (_fix && trueType && RewriteTrueType(use, fontDict, descriptor!, (TrueTypeFont)source, composite))
        {
            _report.Add("Rewrote the embedded programs of {0} TrueType font{s} so that their glyphs, widths and character maps agree with the font dictionaries, as PDF/A requires{names}.", name);
            if (missing > 0)
                _report.Add("{0} font{s} had no glyph for some characters the text shows; they get empty glyphs, as most viewers drew nothing for them{names}.", name);
            return;
        }
        if (missing > 0) Problem("{0} embedded font{s} lack glyphs for characters the text shows{names}.", name);
        if (widthDiffers > 0) Problem("{0} embedded font{s} give characters widths that differ from their programs'{names}.", name);
        if (encodingWrong) Problem("{0} embedded TrueType font{s} have an encoding or character map PDF/A does not allow{names}.", name);
    }

    /// <summary>
    /// Whether a simple TrueType font breaks 6.2.11.6: a symbolic font may have no Encoding and needs a (3,0) cmap or a
    /// single one; a non-symbolic font needs the WinAnsi or MacRoman encoding, glyph names from the Adobe Glyph List and
    /// a cmap other than (3,0).
    /// </summary>
    private bool TrueTypeEncodingWrong(PdfDict dict, PdfDict? descriptor, TrueTypeFont program)
    {
        var cmaps = program.CmapEncodings.ToList();
        bool has30 = cmaps.Contains((3, 0));
        if ((_file.GetInt(descriptor, "Flags", 0) & SymbolicFlag) != 0)
            return dict.ContainsKey("Encoding") || !(cmaps.Count == 1 || has30);
        if (!(has30 ? cmaps.Count > 1 : cmaps.Count > 0)) return true;
        var encoding = _file.Resolve(dict["Encoding"]);
        string? baseEncoding = encoding switch
        {
            PdfName n => n.Value,
            PdfDict d => _file.GetName(d, "BaseEncoding"),
            _ => null,
        };
        if (baseEncoding is not ("WinAnsiEncoding" or "MacRomanEncoding")) return true;
        if (encoding is PdfDict e && _file.GetArray(e, "Differences") is { } differences)
            foreach (var item in differences.Items)
                if (_file.Resolve(item) is PdfName glyph && Encodings.NameToUnicode(glyph.Value) < 0) return true;
        return false;
    }

    /// <summary>
    /// Rewrites an embedded TrueType program with <see cref="PdfATrueType"/>. A simple font becomes symbolic, without an
    /// Encoding, its codes mapped by a (3,0) cmap to the glyphs PdfPecker drew; a CID font gets a CIDToGIDMap stream.
    /// The font gets a copy of its descriptor, which another font may share, and a ToUnicode map when it had none.
    /// </summary>
    private bool RewriteTrueType(FontUse use, PdfDict fontDict, PdfDict descriptor, TrueTypeFont program, bool composite)
    {
        var font = use.Font!;
        double toUnits = program.UnitsPerEm / 1000.0;
        var uses = new List<PdfATrueType.Use>();
        foreach (int code in use.AllCodes.Order())
        {
            int key = composite ? font.Cid(code) : code;
            int advance = use.Visible.Contains(code) ? (int)Math.Round(font.Width(code) * 1000 * toUnits, MidpointRounding.AwayFromZero) : -1;
            uses.Add(new PdfATrueType.Use(key, font.GlyphId(code), advance));
        }
        if (!composite)
        {
            // Codes the text does not show keep the glyphs they had, so the cmap is whole.
            for (int code = 0; code < 256; code++)
                if (!use.Visible.Contains(code) && !use.Invisible.Contains(code) && font.GlyphId(code) is > 0 and var gid) uses.Add(new PdfATrueType.Use(code, gid, -1));
        }
        if (PdfATrueType.Rewrite(program, uses, symbolCmap: !composite) is not var (data, gids)) return false;

        var copy = PdfDict.CopyOf(descriptor);
        copy.Entries["FontFile2"] = FontFile(data);
        copy.Entries.Remove("CIDSet");
        copy.Entries.Remove("CharSet");
        if (!composite)
        {
            int flags = _file.GetInt(descriptor, "Flags", 0);
            copy.Entries["Flags"] = PdfNumber.Int((flags | SymbolicFlag) & ~NonsymbolicFlag);
            if (use.Dict["ToUnicode"] == null && use.AllCodes.Any())
                use.Dict.Entries["ToUnicode"] = ToUnicodeStream(use.AllCodes.Order().Select(c => (c, 1, font.Unicode(c))));
            use.Dict.Entries.Remove("Encoding");
        }
        else
        {
            // The CIDs the text shows map to their new glyphs; the others keep their old ones.
            var oldMap = _file.Resolve(fontDict["CIDToGIDMap"]) is PdfStream s ? Decode(s) : null;
            int maxCid = gids.Keys.DefaultIfEmpty(0).Max();
            if (oldMap != null) maxCid = Math.Max(maxCid, oldMap.Length / 2 - 1);
            var map = new byte[(maxCid + 1) * 2];
            for (int cid = 0; cid <= maxCid; cid++)
            {
                int gid = gids.TryGetValue(cid, out int g) ? g
                    : oldMap != null ? (2 * cid + 1 < oldMap.Length ? oldMap[2 * cid] << 8 | oldMap[2 * cid + 1] : 0)
                    : cid < program.GlyphCount ? cid : 0;
                map[2 * cid] = (byte)(gid >> 8);
                map[2 * cid + 1] = (byte)gid;
            }
            fontDict.Entries["CIDToGIDMap"] = _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["Filter"] = PdfName.Get("FlateDecode"),
            }, Deflate(map)));
        }
        fontDict.Entries["FontDescriptor"] = _file.AddObject(copy);
        return true;
    }

    /// <summary>The CIDFont of a composite font, or the font itself.</summary>
    private PdfDict DescendantOrSelf(PdfDict font)
        => _file.GetArray(font, "DescendantFonts") is { Count: > 0 } d && _file.Resolve(d[0]) is PdfDict cid ? cid : font;

    // ------------------------------------------------------------------ ToUnicode (6.2.11.7.2, level U)

    /// <summary>Level U wants the Unicode text of every code shown; a font that lacks some gets a ToUnicode map with all of them.</summary>
    private void ToUnicode(FontUse use, string name)
    {
        var font = use.Font!;
        var codes = use.AllCodes.OrderBy(c => c).ToList();
        if (codes.Count == 0) return;
        bool complete = true, known = true;
        foreach (int code in codes)
        {
            if (!font.InToUnicode(code)) complete = false;
            var text = font.Unicode(code);
            if (text == null || text.Contains('\0') || text.Contains('￾') || text.Contains('﻿')) known = false;
        }
        if (complete && known) return;
        if (!_fix)
        {
            // Without a ToUnicode entry veraPDF reads a code's text from its glyph name; this reports what it may not find.
            if (!complete && use.Dict["ToUnicode"] != null || !known)
                Problem("{0} font{s} show characters whose Unicode text is not known, which level U requires{names}.", name);
            else if (!complete) Problem("{0} font{s} have no ToUnicode map, which level U requires{names}.", name);
            return;
        }
        if (!known)
        {
            _report.Add("{0} font{s} show characters whose Unicode text is not known, which level U requires{names}.", name);
            return;
        }
        if (use.Lengths.Values.Distinct().Count() > 1) return;
        int length = use.Lengths.Count == 0 ? 1 : use.Lengths.Values.First();
        use.Dict.Entries["ToUnicode"] = ToUnicodeStream(codes.Select(c => (c, length, font.Unicode(c))));
        _report.Add("Added the Unicode text of every character to {0} font{s}, as level U requires{names}.", name);
    }

    /// <summary>A ToUnicode CMap stream for codes of one length (in bytes) and their text; codes without text are left out.</summary>
    private PdfRef ToUnicodeStream(IEnumerable<(int Code, int Length, string? Text)> entries)
    {
        var list = entries.Where(e => !string.IsNullOrEmpty(e.Text)).ToList();
        int length = list.Count > 0 ? list[0].Length : 1;
        var sb = new StringBuilder(256 + list.Count * 20);
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
          .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<")
          .Append('0', 2 * length).Append("> <").Append('F', 2 * length).Append(">\nendcodespacerange\n");
        for (int i = 0; i < list.Count; i += 100)
        {
            int n = Math.Min(100, list.Count - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int k = i; k < i + n; k++)
            {
                sb.Append('<').Append(list[k].Code.ToString("X" + (2 * length).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)).Append("> <");
                foreach (char c in list[k].Text!) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMapResource defineresource pop\nend\nend\n");
        return _file.AddObject(new PdfStream(new Dictionary<string, PdfObject>(StringComparer.Ordinal) { ["Filter"] = PdfName.Get("FlateDecode") },
            Deflate(Encoding.ASCII.GetBytes(sb.ToString()))));
    }

    // ------------------------------------------------------------------ Type 3

    /// <summary>Walks the glyph procedures of a Type 3 font with the resources in force where it is used.</summary>
    private void Type3(PdfDict font, PdfDict? resources, int depth)
    {
        if (_file.GetDict(font, "CharProcs") is not { } procs) return;
        foreach (var value in procs.Entries.Values)
            if (_file.Resolve(value) is PdfStream proc) WalkStream(proc, resources, new TextState(null, 0), depth + 1, owner: font);
    }
}

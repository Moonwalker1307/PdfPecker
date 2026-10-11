namespace PdfPecker.Rendering.Fonts;

/// <summary>The predefined PDF simple-font encodings (code to glyph name) and glyph name to Unicode mapping.</summary>
internal static class Encodings
{
    // Declared first: the encoding tables below are built from these during type initialization.
    private static readonly string[] AsciiNames =
    {
        "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle", "parenleft", "parenright",
        "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six", "seven",
        "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H",
        "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft", "backslash",
        "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n",
        "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde",
    };

    private static readonly string[] Latin1Names =
    {
        "space", "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section", "dieresis", "copyright",
        "ordfeminine", "guillemotleft", "logicalnot", "hyphen", "registered", "macron", "degree", "plusminus", "twosuperior",
        "threesuperior", "acute", "mu", "paragraph", "periodcentered", "cedilla", "onesuperior", "ordmasculine",
        "guillemotright", "onequarter", "onehalf", "threequarters", "questiondown", "Agrave", "Aacute", "Acircumflex", "Atilde",
        "Adieresis", "Aring", "AE", "Ccedilla", "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute",
        "Icircumflex", "Idieresis", "Eth", "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply",
        "Oslash", "Ugrave", "Uacute", "Ucircumflex", "Udieresis", "Yacute", "Thorn", "germandbls", "agrave", "aacute",
        "acircumflex", "atilde", "adieresis", "aring", "ae", "ccedilla", "egrave", "eacute", "ecircumflex", "edieresis",
        "igrave", "iacute", "icircumflex", "idieresis", "eth", "ntilde", "ograve", "oacute", "ocircumflex", "otilde",
        "odieresis", "divide", "oslash", "ugrave", "uacute", "ucircumflex", "udieresis", "yacute", "thorn", "ydieresis",
    };

    public static readonly string?[] Standard = Build(new (int, string)[]
    {
        (39, "quoteright"), (96, "quoteleft"),
        (161, "exclamdown"), (162, "cent"), (163, "sterling"), (164, "fraction"), (165, "yen"), (166, "florin"), (167, "section"),
        (168, "currency"), (169, "quotesingle"), (170, "quotedblleft"), (171, "guillemotleft"), (172, "guilsinglleft"),
        (173, "guilsinglright"), (174, "fi"), (175, "fl"), (177, "endash"), (178, "dagger"), (179, "daggerdbl"),
        (180, "periodcentered"), (182, "paragraph"), (183, "bullet"), (184, "quotesinglbase"), (185, "quotedblbase"),
        (186, "quotedblright"), (187, "guillemotright"), (188, "ellipsis"), (189, "perthousand"), (191, "questiondown"),
        (193, "grave"), (194, "acute"), (195, "circumflex"), (196, "tilde"), (197, "macron"), (198, "breve"), (199, "dotaccent"),
        (200, "dieresis"), (202, "ring"), (203, "cedilla"), (205, "hungarumlaut"), (206, "ogonek"), (207, "caron"),
        (208, "emdash"), (225, "AE"), (227, "ordfeminine"), (232, "Lslash"), (233, "Oslash"), (234, "OE"), (235, "ordmasculine"),
        (241, "ae"), (245, "dotlessi"), (248, "lslash"), (249, "oslash"), (250, "oe"), (251, "germandbls"),
    }, ascii: true);

    public static readonly string?[] WinAnsi = BuildWinAnsi();

    public static readonly string?[] MacRoman = BuildMacRoman();

    private static string?[] Build((int Code, string Name)[] entries, bool ascii)
    {
        var t = new string?[256];
        if (ascii) for (int i = 0; i < AsciiNames.Length; i++) t[32 + i] = AsciiNames[i];
        foreach (var (c, n) in entries) t[c] = n;
        return t;
    }

    private static string?[] BuildWinAnsi()
    {
        var t = Build(new (int, string)[]
        {
            (128, "Euro"), (130, "quotesinglbase"), (131, "florin"), (132, "quotedblbase"), (133, "ellipsis"), (134, "dagger"),
            (135, "daggerdbl"), (136, "circumflex"), (137, "perthousand"), (138, "Scaron"), (139, "guilsinglleft"), (140, "OE"),
            (142, "Zcaron"), (145, "quoteleft"), (146, "quoteright"), (147, "quotedblleft"), (148, "quotedblright"),
            (149, "bullet"), (150, "endash"), (151, "emdash"), (152, "tilde"), (153, "trademark"), (154, "scaron"),
            (155, "guilsinglright"), (156, "oe"), (158, "zcaron"), (159, "Ydieresis"),
        }, ascii: true);
        for (int i = 0; i < Latin1Names.Length; i++) t[0xA0 + i] = Latin1Names[i];
        // Undefined codes in WinAnsi show as bullets in Acrobat.
        foreach (int c in new[] { 127, 129, 141, 143, 144, 157 }) t[c] = "bullet";
        return t;
    }

    private static string?[] BuildMacRoman()
    {
        var t = Build(Array.Empty<(int, string)>(), ascii: true);
        string[] hi =
        {
            "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave", "acircumflex",
            "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis", "iacute", "igrave",
            "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave",
            "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls",
            "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus",
            "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral", "ordfeminine",
            "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin",
            "approxequal", "Delta", "guillemotleft", "guillemotright", "ellipsis", "space", "Agrave", "Atilde", "Otilde", "OE",
            "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge",
            "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl",
            "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute",
            "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple", "Ograve",
            "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring",
            "cedilla", "hungarumlaut", "ogonek", "caron",
        };
        for (int i = 0; i < hi.Length; i++) t[128 + i] = hi[i];
        return t;
    }

    public static string?[]? ByName(string? name) => name switch
    {
        "StandardEncoding" => Standard,
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "MacExpertEncoding" => Standard,
        "PDFDocEncoding" => WinAnsi,
        _ => null,
    };

    // ------------------------------------------------------------------ Symbol and ZapfDingbats

    /// <summary>Unicode for each code of the Symbol font's built-in encoding (0 = unused).</summary>
    public static readonly int[] SymbolUnicode = BuildSymbol();

    private static int[] BuildSymbol()
    {
        var t = new int[256];
        // ASCII positions that keep their meaning.
        foreach (int c in "!#%&()+,./0123456789:;<=>?[]_{|}") t[c] = c;
        t[32] = 0x20;
        t[0x22] = 0x2200; t[0x24] = 0x2203; t[0x27] = 0x220B; t[0x2A] = 0x2217; t[0x2D] = 0x2212; t[0x40] = 0x2245;
        int[] upper = { 0x391, 0x392, 0x3A7, 0x394, 0x395, 0x3A6, 0x393, 0x397, 0x399, 0x3D1, 0x39A, 0x39B, 0x39C, 0x39D, 0x39F,
            0x3A0, 0x398, 0x3A1, 0x3A3, 0x3A4, 0x3A5, 0x3C2, 0x3A9, 0x39E, 0x3A8, 0x396 };
        for (int i = 0; i < 26; i++) t[0x41 + i] = upper[i];
        t[0x5C] = 0x2234; t[0x5E] = 0x22A5; t[0x60] = 0x203E;
        int[] lower = { 0x3B1, 0x3B2, 0x3C7, 0x3B4, 0x3B5, 0x3C6, 0x3B3, 0x3B7, 0x3B9, 0x3D5, 0x3BA, 0x3BB, 0x3BC, 0x3BD, 0x3BF,
            0x3C0, 0x3B8, 0x3C1, 0x3C3, 0x3C4, 0x3C5, 0x3D6, 0x3C9, 0x3BE, 0x3C8, 0x3B6 };
        for (int i = 0; i < 26; i++) t[0x61 + i] = lower[i];
        t[0x7E] = 0x223C;
        int[] high =
        {
            0x20AC, 0x3D2, 0x2032, 0x2264, 0x2044, 0x221E, 0x192, 0x2663, 0x2666, 0x2665, 0x2660, 0x2194, 0x2190, 0x2191, 0x2192, 0x2193,
            0xB0, 0xB1, 0x2033, 0x2265, 0xD7, 0x221D, 0x2202, 0x2022, 0xF7, 0x2260, 0x2261, 0x2248, 0x2026, 0x23D0, 0x23AF, 0x21B5,
            0x2135, 0x2111, 0x211C, 0x2118, 0x2297, 0x2295, 0x2205, 0x2229, 0x222A, 0x2283, 0x2287, 0x2284, 0x2282, 0x2286, 0x2208, 0x2209,
            0x2220, 0x2207, 0xAE, 0xA9, 0x2122, 0x220F, 0x221A, 0x22C5, 0xAC, 0x2227, 0x2228, 0x21D4, 0x21D0, 0x21D1, 0x21D2, 0x21D3,
            0x25CA, 0x2329, 0xAE, 0xA9, 0x2122, 0x2211, 0x239B, 0x239C, 0x239D, 0x23A1, 0x23A2, 0x23A3, 0x23A7, 0x23A8, 0x23A9, 0x23AA,
            0, 0x232A, 0x222B, 0x2320, 0x23AE, 0x2321, 0x239E, 0x239F, 0x23A0, 0x23A4, 0x23A5, 0x23A6, 0x23AB, 0x23AC, 0x23AD, 0,
        };
        for (int i = 0; i < high.Length; i++) t[0xA0 + i] = high[i];
        return t;
    }

    /// <summary>ZapfDingbats glyph names (a1 … a206) to their codes in the font's built-in encoding.</summary>
    public static readonly Dictionary<string, int> DingbatsNames = new(StringComparer.Ordinal)
    {
        ["space"] = 0x20, ["a1"] = 0x21, ["a2"] = 0x22, ["a202"] = 0x23, ["a3"] = 0x24, ["a4"] = 0x25, ["a5"] = 0x26, ["a119"] = 0x27, ["a118"] = 0x28, ["a117"] = 0x29, ["a11"] = 0x2A, ["a12"] = 0x2B, ["a13"] = 0x2C, ["a14"] = 0x2D, ["a15"] = 0x2E, ["a16"] = 0x2F, ["a105"] = 0x30, ["a17"] = 0x31, ["a18"] = 0x32, ["a19"] = 0x33, ["a20"] = 0x34, ["a21"] = 0x35, ["a22"] = 0x36, ["a23"] = 0x37, ["a24"] = 0x38, ["a25"] = 0x39, ["a26"] = 0x3A, ["a27"] = 0x3B, ["a28"] = 0x3C, ["a6"] = 0x3D, ["a7"] = 0x3E, ["a8"] = 0x3F, ["a9"] = 0x40, ["a10"] = 0x41, ["a29"] = 0x42, ["a30"] = 0x43, ["a31"] = 0x44, ["a32"] = 0x45, ["a33"] = 0x46, ["a34"] = 0x47, ["a35"] = 0x48, ["a36"] = 0x49, ["a37"] = 0x4A, ["a38"] = 0x4B, ["a39"] = 0x4C, ["a40"] = 0x4D, ["a41"] = 0x4E, ["a42"] = 0x4F, ["a43"] = 0x50, ["a44"] = 0x51, ["a45"] = 0x52, ["a46"] = 0x53, ["a47"] = 0x54, ["a48"] = 0x55, ["a49"] = 0x56, ["a50"] = 0x57, ["a51"] = 0x58, ["a52"] = 0x59, ["a53"] = 0x5A, ["a54"] = 0x5B, ["a55"] = 0x5C, ["a56"] = 0x5D, ["a57"] = 0x5E, ["a58"] = 0x5F, ["a59"] = 0x60, ["a60"] = 0x61, ["a61"] = 0x62, ["a62"] = 0x63, ["a63"] = 0x64, ["a64"] = 0x65, ["a65"] = 0x66, ["a66"] = 0x67, ["a67"] = 0x68, ["a68"] = 0x69, ["a69"] = 0x6A, ["a70"] = 0x6B, ["a71"] = 0x6C, ["a72"] = 0x6D, ["a73"] = 0x6E, ["a74"] = 0x6F, ["a203"] = 0x70, ["a75"] = 0x71, ["a204"] = 0x72, ["a76"] = 0x73, ["a77"] = 0x74, ["a78"] = 0x75, ["a79"] = 0x76, ["a81"] = 0x77, ["a82"] = 0x78, ["a83"] = 0x79, ["a84"] = 0x7A, ["a97"] = 0x7B, ["a98"] = 0x7C, ["a99"] = 0x7D, ["a100"] = 0x7E, ["a89"] = 0x80, ["a90"] = 0x81, ["a93"] = 0x82, ["a94"] = 0x83, ["a91"] = 0x84, ["a92"] = 0x85, ["a205"] = 0x86, ["a85"] = 0x87, ["a206"] = 0x88, ["a86"] = 0x89, ["a87"] = 0x8A, ["a88"] = 0x8B, ["a95"] = 0x8C, ["a96"] = 0x8D, ["a101"] = 0xA1, ["a102"] = 0xA2, ["a103"] = 0xA3, ["a104"] = 0xA4, ["a106"] = 0xA5, ["a107"] = 0xA6, ["a108"] = 0xA7, ["a112"] = 0xA8, ["a111"] = 0xA9, ["a110"] = 0xAA, ["a109"] = 0xAB, ["a120"] = 0xAC, ["a121"] = 0xAD, ["a122"] = 0xAE, ["a123"] = 0xAF, ["a124"] = 0xB0, ["a125"] = 0xB1, ["a126"] = 0xB2, ["a127"] = 0xB3, ["a128"] = 0xB4, ["a129"] = 0xB5, ["a130"] = 0xB6, ["a131"] = 0xB7, ["a132"] = 0xB8, ["a133"] = 0xB9, ["a134"] = 0xBA, ["a135"] = 0xBB, ["a136"] = 0xBC, ["a137"] = 0xBD, ["a138"] = 0xBE, ["a139"] = 0xBF, ["a140"] = 0xC0, ["a141"] = 0xC1, ["a142"] = 0xC2, ["a143"] = 0xC3, ["a144"] = 0xC4, ["a145"] = 0xC5, ["a146"] = 0xC6, ["a147"] = 0xC7, ["a148"] = 0xC8, ["a149"] = 0xC9, ["a150"] = 0xCA, ["a151"] = 0xCB, ["a152"] = 0xCC, ["a153"] = 0xCD, ["a154"] = 0xCE, ["a155"] = 0xCF, ["a156"] = 0xD0, ["a157"] = 0xD1, ["a158"] = 0xD2, ["a159"] = 0xD3, ["a160"] = 0xD4, ["a161"] = 0xD5, ["a163"] = 0xD6, ["a164"] = 0xD7, ["a196"] = 0xD8, ["a165"] = 0xD9, ["a192"] = 0xDA, ["a166"] = 0xDB, ["a167"] = 0xDC, ["a168"] = 0xDD, ["a169"] = 0xDE, ["a170"] = 0xDF, ["a171"] = 0xE0, ["a172"] = 0xE1, ["a173"] = 0xE2, ["a162"] = 0xE3, ["a174"] = 0xE4, ["a175"] = 0xE5, ["a176"] = 0xE6, ["a177"] = 0xE7, ["a178"] = 0xE8, ["a179"] = 0xE9, ["a193"] = 0xEA, ["a180"] = 0xEB, ["a199"] = 0xEC, ["a181"] = 0xED, ["a200"] = 0xEE, ["a182"] = 0xEF, ["a201"] = 0xF1, ["a183"] = 0xF2, ["a184"] = 0xF3, ["a197"] = 0xF4, ["a185"] = 0xF5, ["a194"] = 0xF6, ["a198"] = 0xF7, ["a186"] = 0xF8, ["a195"] = 0xF9, ["a187"] = 0xFA, ["a188"] = 0xFB, ["a189"] = 0xFC, ["a190"] = 0xFD, ["a191"] = 0xFE
    };

    /// <summary>Unicode for each code of the ZapfDingbats font (0 = unused); the Unicode Dingbats block follows its layout.</summary>
    public static readonly int[] DingbatsUnicode = BuildDingbats();

    private static int[] BuildDingbats()
    {
        var t = new int[256];
        t[0x20] = 0x20;
        for (int c = 0x21; c <= 0x7E; c++) t[c] = 0x2700 + c - 0x20;
        t[0x25] = 0x260E; t[0x2A] = 0x261B; t[0x2B] = 0x261E; t[0x48] = 0x2605; t[0x6C] = 0x25CF; t[0x6E] = 0x25A0;
        t[0x73] = 0x25B2; t[0x74] = 0x25BC; t[0x75] = 0x25C6; t[0x77] = 0x25D7;
        for (int c = 0x80; c <= 0x8D; c++) t[c] = 0x2768 + c - 0x80;
        for (int c = 0xA1; c <= 0xA7; c++) t[c] = 0x2760 + c - 0xA0;
        t[0xA8] = 0x2663; t[0xA9] = 0x2666; t[0xAA] = 0x2665; t[0xAB] = 0x2660;
        for (int c = 0xAC; c <= 0xB5; c++) t[c] = 0x2460 + c - 0xAC;
        for (int c = 0xB6; c <= 0xD4; c++) t[c] = 0x2776 + c - 0xB6;
        t[0xD5] = 0x2192; t[0xD6] = 0x2194; t[0xD7] = 0x2195;
        for (int c = 0xD8; c <= 0xEF; c++) t[c] = 0x2798 + c - 0xD8;
        for (int c = 0xF1; c <= 0xFE; c++) t[c] = 0x27B1 + c - 0xF1;
        return t;
    }

    // ------------------------------------------------------------------ glyph names to Unicode

    /// <summary>Unicode value of a glyph name (AGL names, uniXXXX, uXXXX[XX], and names like "a.sc" or "f_i" by their first part).</summary>
    public static int NameToUnicode(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        if (StandardFontPack.NameToUnicode(name) is int u && u >= 0) return u;
        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal) && TryHex(name.AsSpan(3), out int v)) return v;
        if (name.Length >= 5 && name.Length <= 7 && name[0] == 'u' && TryHex(name.AsSpan(1), out v)) return v;
        int dot = name.IndexOf('.');
        if (dot > 0) return NameToUnicode(name.Substring(0, dot));
        if (name.Length > 7 && name.StartsWith("uni", StringComparison.Ordinal) && TryHex(name.AsSpan(3, 4), out v)) return v;
        // Some generators use "gXX", "cidXX" or "Gxx" names; those carry no character identity.
        return -1;
    }

    private static bool TryHex(ReadOnlySpan<char> s, out int v)
    {
        v = 0;
        if (s.Length == 0) return false;
        foreach (char c in s)
        {
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
            if (d < 0) return false;
            v = v * 16 + d;
        }
        return true;
    }
}

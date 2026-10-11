namespace PdfPecker.Fonts;

/// <summary>Maps Unicode text to the single-byte WinAnsiEncoding used with the standard PDF fonts.</summary>
internal static class WinAnsi
{
    // Unicode code points for bytes 0x80..0x9F (0 = undefined).
    private static readonly ushort[] C1 =
    {
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
    };

    private static readonly Dictionary<char, byte> Extra = BuildExtra();

    private static Dictionary<char, byte> BuildExtra()
    {
        var d = new Dictionary<char, byte>();
        for (int i = 0; i < C1.Length; i++)
            if (C1[i] != 0) d[(char)C1[i]] = (byte)(0x80 + i);
        // Close substitutes for common characters that have no WinAnsi glyph.
        d['\u2212'] = (byte)'-';  // minus sign
        d['\u2010'] = (byte)'-';  // hyphen
        d['\u2011'] = (byte)'-';  // non-breaking hyphen
        d['\u2012'] = 0x96;       // figure dash
        d['\u2015'] = 0x97;       // horizontal bar
        d['\u2002'] = (byte)' ';
        d['\u2003'] = (byte)' ';
        d['\u2009'] = (byte)' ';
        d['\u200A'] = (byte)' ';
        d['\u202F'] = 0xA0;
        d['\u2032'] = (byte)'\'';
        d['\u2033'] = (byte)'"';
        d['\u2044'] = (byte)'/';
        d['\u2219'] = 0x95;
        d['\u25CF'] = 0x95;       // black circle
        d['\u2713'] = (byte)'v';
        d['\u2190'] = (byte)'<';
        d['\u2192'] = (byte)'>';
        d['\u2264'] = (byte)'<';
        d['\u2265'] = (byte)'>';
        return d;
    }

    public static int C1ToUnicode(int value)
    {
        int u = C1[value - 0x80];
        return u == 0 ? value : u;
    }

    /// <summary>True when the character is in the encoding itself, not just replaced by a close substitute or '?'.</summary>
    public static bool IsExact(char c)
        => c >= 0x20 && c < 0x7F || c >= 0xA0 && c <= 0xFF || c >= 0x80 && Table[c] >= 0x80 && Table[c] < 0xA0 && C1[Table[c] - 0x80] == c;

    /// <summary>Encodes one character. Returns '?' for characters that the encoding cannot represent.</summary>
    public static byte Encode(char c) => Table[c];

    private static readonly byte[] Table = BuildTable();

    private static byte[] BuildTable()
    {
        var t = new byte[65536];
        for (int c = 0; c < t.Length; c++)
        {
            if (c < 0x80) t[c] = c >= 0x20 ? (byte)c : (byte)' ';
            else if (c >= 0xA0 && c <= 0xFF) t[c] = (byte)c;
            else t[c] = Extra.TryGetValue((char)c, out var b) ? b : (byte)'?';
        }
        return t;
    }
}

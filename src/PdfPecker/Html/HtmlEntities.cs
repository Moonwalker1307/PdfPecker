using System.Globalization;
using System.Text;

namespace PdfPecker.Html;

internal static partial class HtmlEntities
{
    // The table is built on the first named reference, so documents without one never pay for its 2,125 entries.
    private static class Table
    {
        public static readonly Dictionary<string, string> Named = BuildNamed();
        public static readonly HashSet<string> Legacy = new(LegacyNames.Split(' '), StringComparer.Ordinal);
    }

    // The longest name, "CounterClockwiseContourIntegral", has 31 characters; the longest legacy name has 6.
    private const int MaxNameLength = 31, MaxLegacyLength = 6;

    private static Dictionary<string, string> BuildNamed()
    {
        var d = new Dictionary<string, string>(2200, StringComparer.Ordinal);
        foreach (var pair in NamedTable.Split(' '))
        {
            int eq = pair.IndexOf('=');
            int comma = pair.IndexOf(',', eq);
            string value = char.ConvertFromUtf32(Hex(comma < 0 ? pair.AsSpan(eq + 1) : pair.AsSpan(eq + 1, comma - eq - 1)));
            if (comma >= 0) value += char.ConvertFromUtf32(Hex(pair.AsSpan(comma + 1)));
            d[pair.Substring(0, eq)] = value;
        }
        return d;

        static int Hex(ReadOnlySpan<char> s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Looks up a named character reference (without '&amp;' and ';').</summary>
    public static bool TryGetNamed(string name, out string value) => Table.Named.TryGetValue(name, out value!);

    /// <summary>Decodes character references in text. Returns the input when it has none.</summary>
    public static string Decode(string s) => Decode(s, attribute: false);

    /// <summary>
    /// Decodes character references in an attribute value, where a legacy name without ';' followed by a letter, a
    /// digit or '=' stays as written, so "?a=1&amp;region=eu" in a URL keeps "&amp;reg".
    /// </summary>
    public static string DecodeAttribute(string s) => Decode(s, attribute: true);

    private static string Decode(string s, bool attribute)
    {
        int amp = s.IndexOf('&');
        if (amp < 0) return s;
        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, amp);
        int i = amp;
        while (i < s.Length)
        {
            char c = s[i];
            if (c != '&') { sb.Append(c); i++; continue; }
            int consumed = TryDecodeAt(s, i, sb, attribute);
            if (consumed == 0) { sb.Append('&'); i++; }
            else i += consumed;
        }
        return sb.ToString();
    }

    private static int TryDecodeAt(string s, int i, StringBuilder sb, bool attribute)
    {
        int start = i + 1;
        if (start >= s.Length) return 0;
        if (s[start] == '#')
        {
            int p = start + 1;
            bool hex = p < s.Length && (s[p] == 'x' || s[p] == 'X');
            if (hex) p++;
            int digitsStart = p;
            int value = 0;
            while (p < s.Length && p - digitsStart < 8)
            {
                int d = HexVal(s[p]);
                if (d < 0 || (!hex && d > 9)) break;
                value = value * (hex ? 16 : 10) + d;
                p++;
            }
            if (p == digitsStart) return 0;
            if (p < s.Length && s[p] == ';') p++;
            // Eight hex digits can overflow into a negative value, so the range check is unsigned.
            if (value == 0 || (uint)value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF)) value = 0xFFFD;
            // Windows-1252 remapping of C1 controls, as browsers do.
            if (value >= 0x80 && value <= 0x9F) value = Fonts.WinAnsi.C1ToUnicode(value);
            sb.Append(char.ConvertFromUtf32(value));
            return p - i;
        }
        // The HTML tokenizer takes the longest name in the table. A name with ';' can only be the whole run of letters
        // and digits; without ';', only the legacy names count, as a prefix of the run, so "&copy2024" in text is
        // "©2024" and "&notit;" is "¬it;" while "&notin;" is "∉".
        int end = start;
        while (end < s.Length && end - start <= MaxNameLength && char.IsAsciiLetterOrDigit(s[end])) end++;
        int run = end - start;
        if (run == 0) return 0;
        if (run <= MaxNameLength && end < s.Length && s[end] == ';'
            && Table.Named.TryGetValue(s.Substring(start, run), out var full))
        {
            sb.Append(full);
            return end + 1 - i;
        }
        for (int len = Math.Min(run, MaxLegacyLength); len >= 2; len--)
        {
            string name = s.Substring(start, len);
            if (!Table.Legacy.Contains(name)) continue;
            // In an attribute, a legacy name followed by a letter, a digit or '=' is not a reference (HTML 13.2.5.73),
            // which keeps query strings such as "&region=eu" intact.
            int after = start + len;
            if (attribute && after < s.Length && (char.IsAsciiLetterOrDigit(s[after]) || s[after] == '=')) return 0;
            sb.Append(Table.Named[name]);
            return after - i;
        }
        return 0;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
}

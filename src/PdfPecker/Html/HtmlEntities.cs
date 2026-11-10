using System.Globalization;
using System.Text;

namespace PdfPecker.Html;

internal static partial class HtmlEntities
{
    // Both tables are sorted in ordinal order (the generator writes them so) and are searched by halving in the
    // assembly's own string data. Only an index of where each entry starts is built, on the first named reference: a
    // dictionary of the 2,231 names and their texts allocated about 300 KB in the first conversion of every process.
    private static class Table
    {
        public static readonly int[] Named = Starts(NamedTable);
        public static readonly int[] Legacy = Starts(LegacyNames);
    }

    // The longest name, "CounterClockwiseContourIntegral", has 31 characters; the longest legacy name has 6.
    private const int MaxNameLength = 31, MaxLegacyLength = 6;

    // A named reference decodes to at most two code points, so to at most four UTF-16 units.
    private const int MaxValueLength = 4;

    private static int[] Starts(string table)
    {
        var starts = new int[table.AsSpan().Count(' ') + 1];
        int n = 1;
        for (int i = 0; i < table.Length; i++)
            if (table[i] == ' ') starts[n++] = i + 1;
        return starts;
    }

    // Returns where the entry for the name starts in the table, or -1. A named entry's name ends at its '=', a legacy
    // entry at the next space or the table's end.
    private static int Find(string table, int[] starts, ReadOnlySpan<char> name)
    {
        int lo = 0, hi = starts.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            var entry = table.AsSpan(starts[mid]);
            int end = entry.IndexOfAny('=', ' ');
            int cmp = (end < 0 ? entry : entry[..end]).SequenceCompareTo(name);
            if (cmp == 0) return starts[mid];
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    // Writes the text of the named entry that starts at the index and returns its length.
    private static int WriteValue(int start, Span<char> destination)
    {
        var value = NamedTable.AsSpan(start);
        value = value[(value.IndexOf('=') + 1)..];
        int space = value.IndexOf(' ');
        if (space >= 0) value = value[..space];
        int comma = value.IndexOf(',');
        int length = WriteCodePoint(Hex(comma < 0 ? value : value[..comma]), destination);
        if (comma >= 0) length += WriteCodePoint(Hex(value[(comma + 1)..]), destination[length..]);
        return length;

        static int Hex(ReadOnlySpan<char> s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        static int WriteCodePoint(int codePoint, Span<char> destination) => new Rune(codePoint).EncodeToUtf16(destination);
    }

    private static void AppendValue(StringBuilder sb, int start)
    {
        Span<char> value = stackalloc char[MaxValueLength];
        sb.Append(value[..WriteValue(start, value)]);
    }

    /// <summary>Looks up a named character reference (without '&amp;' and ';').</summary>
    public static bool TryGetNamed(string name, out string value)
    {
        int start = name.Length <= MaxNameLength ? Find(NamedTable, Table.Named, name) : -1;
        if (start < 0)
        {
            value = null!;
            return false;
        }
        Span<char> text = stackalloc char[MaxValueLength];
        value = new string(text[..WriteValue(start, text)]);
        return true;
    }

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
            && Find(NamedTable, Table.Named, s.AsSpan(start, run)) is >= 0 and var full)
        {
            AppendValue(sb, full);
            return end + 1 - i;
        }
        for (int len = Math.Min(run, MaxLegacyLength); len >= 2; len--)
        {
            var name = s.AsSpan(start, len);
            if (Find(LegacyNames, Table.Legacy, name) < 0) continue;
            // In an attribute, a legacy name followed by a letter, a digit or '=' is not a reference (HTML 13.2.5.73),
            // which keeps query strings such as "&region=eu" intact.
            int after = start + len;
            if (attribute && after < s.Length && (char.IsAsciiLetterOrDigit(s[after]) || s[after] == '=')) return 0;
            // The generator checks that every legacy name is also in the full table.
            AppendValue(sb, Find(NamedTable, Table.Named, name));
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

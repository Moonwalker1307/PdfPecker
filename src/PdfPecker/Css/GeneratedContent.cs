using System.Globalization;
using System.Text;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Evaluates the content property of ::before and ::after: strings, attr(), counter(), counters() and quotes.</summary>
internal static class GeneratedContent
{
    // Chrome's quotes for English: curly double quotes outside, curly single quotes inside.
    private static readonly string[] Open = { "“", "‘" }, Close = { "”", "’" };

    // Generated text far beyond any real use (a counters() over runaway nesting) is cut here.
    private const int MaxLength = 10_000;

    /// <summary>The text to show, or null when the value is invalid or generates no box.</summary>
    public static string? Evaluate(string value, Element host, StyleResolver resolver)
    {
        var sb = new StringBuilder();
        int i = 0, n = value.Length;
        while (i < n)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '"' or '\'')
            {
                if (!ReadString(value, ref i, sb)) return null;
                continue;
            }
            int start = i;
            while (i < n && (char.IsLetterOrDigit(value[i]) || value[i] is '-' or '_')) i++;
            var name = value.Substring(start, i - start).ToLowerInvariant();
            if (name.Length == 0) return null;
            if (i < n && value[i] == '(')
            {
                int close = FindClose(value, i);
                if (close < 0) return null;
                var args = SplitArgs(value.Substring(i + 1, close - i - 1));
                i = close + 1;
                if (!Function(name, args, host, resolver, sb)) return null;
                continue;
            }
            switch (name)
            {
                case "open-quote":
                    sb.Append(Open[Math.Min(resolver.QuoteDepth, 1)]);
                    resolver.QuoteDepth++;
                    break;
                case "close-quote":
                    if (resolver.QuoteDepth > 0) resolver.QuoteDepth--;
                    sb.Append(Close[Math.Min(resolver.QuoteDepth, 1)]);
                    break;
                case "no-open-quote": resolver.QuoteDepth++; break;
                case "no-close-quote": if (resolver.QuoteDepth > 0) resolver.QuoteDepth--; break;
                default: return null;
            }
            if (sb.Length > MaxLength) break;
        }
        return sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
    }

    private static bool Function(string name, List<string> args, Element host, StyleResolver resolver, StringBuilder sb)
    {
        switch (name)
        {
            case "attr":
                if (args.Count == 0) return false;
                sb.Append(host.GetAttribute(args[0].Split(' ')[0].ToLowerInvariant()) ?? "");
                return true;
            case "counter":
            {
                if (args.Count is < 1 or > 2) return false;
                var values = resolver.CounterValues(args[0]);
                sb.Append(Counters.Format(values.Count > 0 ? values[^1] : 0, args.Count == 2 ? args[1].ToLowerInvariant() : "decimal"));
                return true;
            }
            case "counters":
            {
                if (args.Count is < 2 or > 3) return false;
                // The separator must be a quoted string; "counters(item, )" or an unquoted word drops the declaration.
                if (args[1].Length == 0 || args[1][0] is not ('"' or '\'')) return false;
                var sep = new StringBuilder();
                int k = 0;
                if (!ReadString(args[1], ref k, sep)) return false;
                var values = resolver.CounterValues(args[0]);
                string style = args.Count == 3 ? args[2].ToLowerInvariant() : "decimal";
                if (values.Count == 0) sb.Append(Counters.Format(0, style));
                for (int v = 0; v < values.Count; v++)
                {
                    if (v > 0) sb.Append(sep);
                    sb.Append(Counters.Format(values[v], style));
                }
                return true;
            }
            case "url":
            case "image-set":
            case "linear-gradient":
            case "radial-gradient":
                return true; // images are not drawn as generated content
            default:
                return false;
        }
    }

    /// <summary>Reads a quoted string with CSS escapes (\" \\ \A \201C) at <paramref name="i"/>.</summary>
    private static bool ReadString(string v, ref int i, StringBuilder sb)
    {
        char quote = v[i++];
        while (i < v.Length)
        {
            char c = v[i];
            if (c == quote) { i++; return true; }
            if (c == '\\' && i + 1 < v.Length)
            {
                i++;
                if (Uri.IsHexDigit(v[i]))
                {
                    int start = i;
                    while (i < v.Length && i - start < 6 && Uri.IsHexDigit(v[i])) i++;
                    int code = int.Parse(v.AsSpan(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (i < v.Length && v[i] == ' ') i++; // one space ends the escape
                    if (code == 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) code = 0xFFFD;
                    sb.Append(char.ConvertFromUtf32(code));
                    continue;
                }
                if (v[i] is '\n' or '\r') { i++; continue; } // an escaped newline continues the string
                sb.Append(v[i++]);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return false;
    }

    private static int FindClose(string v, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < v.Length; i++)
        {
            char c = v[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == ',') { list.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
        }
        var last = s.Substring(start).Trim();
        if (last.Length > 0 || list.Count > 0) list.Add(last);
        return list;
    }
}

/// <summary>Counter and list marker numbering in the predefined counter styles.</summary>
internal static class Counters
{
    public static string Format(int n, string style)
        => style switch
        {
            "none" => "",
            "disc" => "•",
            "circle" => "◦",
            "square" => "▪",
            "decimal-leading-zero" => n is >= 0 and < 10 ? "0" + n.ToString(CultureInfo.InvariantCulture) : n.ToString(CultureInfo.InvariantCulture),
            "lower-alpha" or "lower-latin" => Alpha(n, 'a'),
            "upper-alpha" or "upper-latin" => Alpha(n, 'A'),
            "lower-roman" => Roman(n).ToLowerInvariant(),
            "upper-roman" => Roman(n),
            _ => n.ToString(CultureInfo.InvariantCulture),
        };

    public static string Alpha(int n, char a)
    {
        if (n <= 0) return n.ToString(CultureInfo.InvariantCulture);
        var s = "";
        while (n > 0)
        {
            n--;
            s = (char)(a + n % 26) + s;
            n /= 26;
        }
        return s;
    }

    public static string Roman(int n)
    {
        if (n <= 0 || n >= 4000) return n.ToString(CultureInfo.InvariantCulture);
        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] numerals = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new StringBuilder();
        for (int i = 0; i < values.Length; i++)
            while (n >= values[i]) { sb.Append(numerals[i]); n -= values[i]; }
        return sb.ToString();
    }
}

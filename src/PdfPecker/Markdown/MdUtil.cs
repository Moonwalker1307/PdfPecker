using System.Globalization;
using System.Net;
using System.Text;

namespace PdfPecker.Markdown;

internal static class MdUtil
{
    public static bool IsSpaceOrTab(char c) => c == ' ' || c == '\t';

    public static bool IsAsciiPunctuation(char c)
        => (c >= '!' && c <= '/') || (c >= ':' && c <= '@') || (c >= '[' && c <= '`') || (c >= '{' && c <= '~');

    /// <summary>CommonMark 0.31 "Unicode punctuation": ASCII punctuation or a character in a Unicode P or S category.</summary>
    public static bool IsPunctuation(char c)
    {
        if (c < 128) return IsAsciiPunctuation(c);
        switch (char.GetUnicodeCategory(c))
        {
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
            case UnicodeCategory.MathSymbol:
            case UnicodeCategory.CurrencySymbol:
            case UnicodeCategory.ModifierSymbol:
            case UnicodeCategory.OtherSymbol:
                return true;
            default:
                return false;
        }
    }

    public static bool IsUnicodeWhitespace(char c)
        => c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r'
           || (c > 127 && char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    public static bool IsBlank(string s, int start = 0)
    {
        for (int i = start; i < s.Length; i++)
            if (s[i] is not (' ' or '\t' or '\n' or '\r')) return false;
        return true;
    }

    /// <summary>Normalizes a link label for case-insensitive matching: trims, collapses whitespace and case-folds.</summary>
    public static string NormalizeReference(string label)
    {
        // The label includes its brackets.
        var inner = label.AsSpan(1, label.Length - 2).Trim(" \t\r\n");
        var sb = new StringBuilder(inner.Length);
        bool space = false;
        foreach (var c in inner)
        {
            if (c is ' ' or '\t' or '\r' or '\n') { space = true; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        // Unicode case fold approximation: ß and ẞ fold to "ss" as the spec requires.
        return sb.ToString().ToLowerInvariant().ToUpperInvariant().Replace("ẞ", "SS").Replace("ß", "SS");
    }

    /// <summary>Tries to decode an entity reference starting at <paramref name="pos"/> (which holds '&amp;'). Requires the closing ';'.</summary>
    public static bool TryDecodeEntity(string s, int pos, out string value, out int length)
    {
        value = "";
        length = 0;
        int i = pos + 1;
        if (i >= s.Length) return false;
        if (s[i] == '#')
        {
            i++;
            bool hex = i < s.Length && (s[i] == 'x' || s[i] == 'X');
            if (hex) i++;
            int start = i, max = hex ? 6 : 7, code = 0;
            while (i < s.Length && i - start < max)
            {
                char c = s[i];
                int d = c >= '0' && c <= '9' ? c - '0'
                    : hex && c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : hex && c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (d < 0) break;
                code = code * (hex ? 16 : 10) + d;
                i++;
            }
            if (i == start || i >= s.Length || s[i] != ';') return false;
            if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF)) code = 0xFFFD;
            value = char.ConvertFromUtf32(code);
            length = i + 1 - pos;
            return true;
        }
        int nameStart = i;
        if (i >= s.Length || !char.IsAsciiLetter(s[i])) return false;
        while (i < s.Length && i - nameStart < 32 && char.IsAsciiLetterOrDigit(s[i])) i++;
        if (i >= s.Length || s[i] != ';' || i - nameStart < 2) return false;
        var name = s.Substring(nameStart, i - nameStart);
        if (!Html.HtmlEntities.TryGetNamed(name, out value))
        {
            var entity = "&" + name + ";";
            var decoded = WebUtility.HtmlDecode(entity);
            if (decoded == entity) return false;
            value = decoded;
        }
        length = i + 1 - pos;
        return true;
    }

    /// <summary>Processes backslash escapes and entity references.</summary>
    public static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0 && s.IndexOf('&') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && IsAsciiPunctuation(s[i + 1]))
            {
                sb.Append(s[++i]);
            }
            else if (c == '&' && TryDecodeEntity(s, i, out var v, out int len))
            {
                sb.Append(v);
                i += len - 1;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Percent-encodes a URL the way the CommonMark reference implementation does, keeping existing escapes.</summary>
    public static string NormalizeUri(string uri)
    {
        StringBuilder? sb = null;
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < uri.Length; i++)
        {
            char c = uri[i];
            if (IsUriSafe(c) || (c == '%' && i + 2 < uri.Length && char.IsAsciiHexDigit(uri[i + 1]) && char.IsAsciiHexDigit(uri[i + 2])))
            {
                sb?.Append(c);
                continue;
            }
            sb ??= new StringBuilder(uri, 0, i, uri.Length + 16);
            int n;
            if (char.IsHighSurrogate(c) && i + 1 < uri.Length && char.IsLowSurrogate(uri[i + 1]))
            {
                n = Encoding.UTF8.GetBytes(uri.AsSpan(i, 2), bytes);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                n = Encoding.UTF8.GetBytes("�", bytes);
            }
            else n = Encoding.UTF8.GetBytes(uri.AsSpan(i, 1), bytes);
            for (int k = 0; k < n; k++)
            {
                sb.Append('%');
                sb.Append("0123456789ABCDEF"[bytes[k] >> 4]);
                sb.Append("0123456789ABCDEF"[bytes[k] & 15]);
            }
        }
        return sb?.ToString() ?? uri;
    }

    private static bool IsUriSafe(char c)
        => char.IsAsciiLetterOrDigit(c) || c is ';' or '/' or '?' or ':' or '@' or '&' or '=' or '+' or '$' or ',' or '-' or '_'
            or '.' or '!' or '~' or '*' or '\'' or '(' or ')' or '#';

    public static void EscapeHtml(StringBuilder sb, string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            string? rep = s[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => null,
            };
            if (rep == null) continue;
            sb.Append(s, start, i - start).Append(rep);
            start = i + 1;
        }
        sb.Append(s, start, s.Length - start);
    }
}

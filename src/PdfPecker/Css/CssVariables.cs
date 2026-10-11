using System.Text;

namespace PdfPecker.Css;

/// <summary>Custom properties (<c>--name: value</c>) and <c>var()</c> substitution.</summary>
internal static class CssVariables
{
    private const int MaxDepth = 32;

    /// <summary>
    /// Replaces every <c>var(--name, fallback)</c> in <paramref name="value"/>. Returns null when a reference cannot be
    /// resolved and has no fallback, which makes the declaration invalid at computed-value time.
    /// </summary>
    public static string? Substitute(string value, IReadOnlyDictionary<string, string>? vars, int depth = 0)
    {
        if (depth > MaxDepth) return null;
        int i = value.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return value;
        var sb = new StringBuilder(value.Length + 16);
        int last = 0;
        while (i >= 0)
        {
            // "var(" must start a token, not end a longer identifier.
            if (i > 0 && (char.IsLetterOrDigit(value[i - 1]) || value[i - 1] == '-'))
            {
                i = value.IndexOf("var(", i + 4, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int open = i + 3;
            int close = MatchingParen(value, open);
            if (close < 0) return null;
            sb.Append(value, last, i - last);
            string inner = value.Substring(open + 1, close - open - 1);
            int comma = TopLevelComma(inner);
            string name = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
            string? fallback = comma < 0 ? null : inner.Substring(comma + 1).Trim();

            string? replacement = null;
            if (vars != null && vars.TryGetValue(name, out var v) && v.Length > 0)
                replacement = Substitute(v, vars, depth + 1);
            if (replacement == null && fallback != null)
                replacement = Substitute(fallback, vars, depth + 1);
            if (replacement == null) return null;
            sb.Append(replacement);
            last = close + 1;
            i = value.IndexOf("var(", last, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(value, last, value.Length - last);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the custom properties of an element from its parent's and the element's own declarations (in cascade
    /// order). References between variables are resolved here, so stored values never contain var().
    /// </summary>
    public static Dictionary<string, string> Compute(Dictionary<string, string>? inherited, List<KeyValuePair<string, string>> own)
    {
        var raw = inherited != null ? new Dictionary<string, string>(inherited, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in own)
        {
            var v = value.Trim();
            if (v.Equals("initial", StringComparison.OrdinalIgnoreCase)) raw.Remove(name);
            else if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase))
            {
                if (inherited != null && inherited.TryGetValue(name, out var pv)) raw[name] = pv;
                else raw.Remove(name);
            }
            else raw[name] = v;
        }
        // Resolve the element's own values against the combined set; inherited values are already resolved.
        var result = new Dictionary<string, string>(raw, StringComparer.Ordinal);
        foreach (var (name, _) in own)
        {
            if (!raw.TryGetValue(name, out var v) || !CssParser.HasVar(v)) continue;
            var resolved = Substitute(v, raw);
            if (resolved == null) result.Remove(name);
            else result[name] = resolved;
        }
        return result;
    }

    private static int MatchingParen(string s, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static int TopLevelComma(string s)
    {
        int depth = 0;
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
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }
}

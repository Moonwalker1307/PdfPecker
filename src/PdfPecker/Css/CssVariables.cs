using System.Text;

namespace PdfPecker.Css;

/// <summary>Custom properties (<c>--name: value</c>) and <c>var()</c> substitution.</summary>
internal static class CssVariables
{
    private const int MaxDepth = 32;

    /// <summary>
    /// Longest value a substitution may produce. Variables that reference each other several times grow exponentially
    /// (a "billion laughs" stylesheet); like browsers, an over-long result makes the declaration invalid.
    /// </summary>
    internal const int MaxLength = 1 << 20;

    /// <summary>
    /// Replaces every <c>var(--name, fallback)</c> in <paramref name="value"/>. Returns null when a reference cannot be
    /// resolved and has no fallback, which makes the declaration invalid at computed-value time.
    /// </summary>
    public static string? Substitute(string value, CssVarScope? vars, int depth = 0)
    {
        if (depth > MaxDepth || value.Length > MaxLength) return null;
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
            if (replacement == null || sb.Length + replacement.Length > MaxLength) return null;
            sb.Append(replacement);
            last = close + 1;
            i = value.IndexOf("var(", last, StringComparison.OrdinalIgnoreCase);
        }
        sb.Append(value, last, value.Length - last);
        return sb.Length > MaxLength ? null : sb.ToString();
    }

    /// <summary>
    /// Builds the custom properties of an element from its parent's and the element's own declarations (in cascade
    /// order). References between variables are resolved here, so stored values never contain var().
    /// </summary>
    public static CssVarScope Compute(CssVarScope? inherited, List<KeyValuePair<string, string>> own)
    {
        // Only the element's own declarations are stored; lookups fall through to the parent. Copying the whole inherited
        // set into every element would cost (variables x elements) time and memory.
        var raw = new CssVarScope(inherited);
        foreach (var (name, value) in own)
        {
            var v = value.Trim();
            if (v.Equals("initial", StringComparison.OrdinalIgnoreCase)) raw.Set(name, null);
            else if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                raw.Set(name, inherited != null && inherited.TryGetValue(name, out var pv) ? pv : null);
            else raw.Set(name, v);
        }
        // Resolve the element's own values against the combined set; inherited values are already resolved.
        var result = new CssVarScope(inherited);
        foreach (var (name, _) in own)
        {
            if (!raw.TryGetValue(name, out var v)) { result.Set(name, null); continue; }
            result.Set(name, CssParser.HasVar(v) ? Substitute(v, raw) : v);
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

/// <summary>The custom properties visible to an element: its own values over its parent's scope.</summary>
internal sealed class CssVarScope
{
    private readonly CssVarScope? _parent;
    private readonly Dictionary<string, string?> _own = new(StringComparer.Ordinal); // null: removed (initial, invalid)

    // Chains are at most as long as the element nesting (which the HTML parser caps), and only elements that declare
    // custom properties add a link.
    public CssVarScope(CssVarScope? parent) => _parent = parent;

    public void Set(string name, string? value) => _own[name] = value;

    public bool TryGetValue(string name, out string value)
    {
        for (var s = this; s != null; s = s._parent)
        {
            if (s._own.TryGetValue(name, out var v))
            {
                value = v!;
                return v != null;
            }
        }
        value = null!;
        return false;
    }
}

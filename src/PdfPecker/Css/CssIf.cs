using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The if() function of CSS Values 5, as Chrome ships it: <c>if(media(print): 1cm; style(--theme: dark): 2cm; else: 0)</c>.
/// The first branch whose condition holds gives the value; a condition is <c>media()</c>, <c>supports()</c> or
/// <c>style()</c> of a custom property, joined with <c>and</c>, <c>or</c> and <c>not</c> and grouped in parentheses,
/// or <c>else</c>. Like var(), it is substituted per element before the declaration is parsed, after var() and attr().
/// </summary>
internal static class CssIf
{
    // Nested if() calls, and calls in one value, that one substitution handles. Values come from untrusted style
    // sheets, and a template needs a handful.
    private const int MaxDepth = 16, MaxCalls = 256;

    /// <summary>Whether the value calls if() outside a string: a cheap test made before any other work.</summary>
    public static bool Has(string value)
    {
        int i = value.IndexOf("if(", StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            if (i == 0 || !IsNameChar(value[i - 1])) return true;
            i = value.IndexOf("if(", i + 3, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// Replaces every if() in the value with the value of its first branch that holds. Returns null when a call is
    /// malformed or no branch holds, which makes the declaration invalid at computed-value time.
    /// </summary>
    public static string? Resolve(string value, CssVarScope? vars, MediaContext media)
    {
        if (!Has(value)) return value;
        int calls = MaxCalls;
        return Run(value, vars, media, 0, ref calls);
    }

    private static string? Run(string value, CssVarScope? vars, MediaContext media, int depth, ref int calls)
    {
        if (depth > MaxDepth) return null;
        var sb = new StringBuilder(value.Length);
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < value.Length) sb.Append(value[++i]);
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c is 'i' or 'I' && i + 3 <= value.Length && value.AsSpan(i, 3).Equals("if(", StringComparison.OrdinalIgnoreCase)
                && (i == 0 || !IsNameChar(value[i - 1])))
            {
                if (--calls < 0) return null;
                int close = MediaQuery.MatchingParen(value, i + 2);
                // An unclosed call runs to the end of the value, as the end of a declaration closes open functions.
                string body = close < 0 ? value.Substring(i + 3) : value.Substring(i + 3, close - i - 3);
                var chosen = Choose(body, vars, media, depth);
                if (chosen == null) return null;
                var inner = Run(chosen, vars, media, depth + 1, ref calls);
                if (inner == null) return null;
                sb.Append(inner);
                if (close < 0) break;
                i = close;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The value of the first branch whose condition holds, or null when none does or a branch is malformed.</summary>
    private static string? Choose(string body, CssVarScope? vars, MediaContext media, int depth)
    {
        foreach (var branch in CssParser.SplitTopLevel(body, ';'))
        {
            if (string.IsNullOrWhiteSpace(branch)) continue;
            int colon = TopLevelColon(branch);
            if (colon < 0) return null;
            var condition = branch.Substring(0, colon).Trim();
            bool? holds = condition.Equals("else", StringComparison.OrdinalIgnoreCase) ? true : Condition(condition, vars, media, depth);
            if (holds == null) return null;
            if (holds.Value) return branch.Substring(colon + 1).Trim();
        }
        return null;
    }

    /// <summary>The first colon outside parentheses, brackets and strings, or -1.</summary>
    private static int TopLevelColon(string s)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>A condition: tests joined by and or or (not both without parentheses), each maybe negated; null when malformed.</summary>
    private static bool? Condition(string s, CssVarScope? vars, MediaContext media, int depth)
    {
        s = s.Trim();
        if (s.Length == 0 || depth > MaxDepth) return null;
        if (StartsWithWord(s, "not")) return !Condition(s.Substring(3), vars, media, depth + 1);
        bool? result = null;
        string? op = null;
        int i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;
            int open = s.IndexOf('(', i);
            if (open < 0) return null;
            int close = MediaQuery.MatchingParen(s, open);
            if (close < 0) return null;
            var name = s.Substring(i, open - i).Trim().ToLowerInvariant();
            var arg = s.Substring(open + 1, close - open - 1).Trim();
            bool? value = name switch
            {
                "" => Condition(arg, vars, media, depth + 1),
                "media" => arg.Length > 0 ? MediaQuery.Matches(MediaArgument(arg), media) : null,
                "supports" => arg.Length > 0 ? CssSupports.Evaluate(TopLevelColon(arg) >= 0 ? "(" + arg + ")" : arg) : null,
                "style" => Style(arg, vars),
                _ => null,
            };
            if (value == null) return null;
            result = result == null ? value : op == "or" ? result.Value || value.Value : result.Value && value.Value;
            i = close + 1;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return result;
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var word = s.Substring(start, i - start).ToLowerInvariant();
            if (word is not ("and" or "or") || op != null && op != word) return null;
            op = word;
        }
    }

    /// <summary>A media() argument as a query list: a bare feature or range, such as width >= 400px, goes in parentheses.</summary>
    private static string MediaArgument(string arg)
        => arg[0] != '(' && (arg.Contains(':') || arg.AsSpan().IndexOfAny('<', '>', '=') >= 0) ? "(" + arg + ")" : arg;

    /// <summary>
    /// style(--name: value) holds when the element's custom property has that value, compared as written with runs of
    /// white space as one; style(--name) holds when the property has a value. Other properties are not queried, as in
    /// Chrome's if().
    /// </summary>
    private static bool? Style(string arg, CssVarScope? vars)
    {
        int colon = TopLevelColon(arg);
        var name = (colon < 0 ? arg : arg.Substring(0, colon)).Trim();
        if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length < 3) return null;
        string? actual = null;
        bool set = vars != null && vars.TryGetValue(name, out actual, out _);
        if (colon < 0) return set;
        if (!set) return false;
        return string.Equals(Normalize(actual!), Normalize(arg.Substring(colon + 1)), StringComparison.Ordinal);
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool StartsWithWord(string s, string word)
        => s.Length > word.Length && s.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (char.IsWhiteSpace(s[word.Length]) || s[word.Length] == '(');

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}

using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// A custom function defined with <c>@function</c> (CSS Functions and Mixins, as Chrome ships it from version 139):
/// <c>@function --double(--x) { result: calc(var(--x) * 2) }</c>, called as <c>width: --double(30px)</c>.
/// </summary>
internal sealed class CssFunction
{
    public required string Name;
    public required List<CssFunctionParameter> Parameters;
    public required List<CssFunctionStatement> Body;
}

/// <summary>A parameter of a custom function, with the default value an argument left out takes.</summary>
internal readonly record struct CssFunctionParameter(string Name, string? Default);

/// <summary>
/// A local custom property or a result descriptor in a function body, with the @media and @supports conditions of the
/// blocks it sits in, which must all hold for it to apply.
/// </summary>
internal readonly record struct CssFunctionStatement(string Name, string Value, string[]? Media, string[]? Supports);

/// <summary>
/// Parses @function rules and replaces calls of custom functions in declaration values. Like var() and if(), calls are
/// substituted per element before the declaration is parsed: the body sees the function's parameters and locals and,
/// beneath them, the custom properties of the element that calls it (and of the function that calls it, when one
/// function calls another). The last result descriptor whose conditions hold gives the value; a call with too many
/// arguments, a missing argument without a default, or no result makes the declaration invalid at computed-value time,
/// as in Chrome. Parameter and result types are not checked: arguments are passed on as written.
/// </summary>
internal static class CssFunctions
{
    // Nesting of calls (a function calling another, or a call in an argument) and calls in one value that one
    // substitution handles. Values come from untrusted style sheets; a design system needs a handful.
    private const int MaxDepth = 16, MaxCalls = 256;

    // Parameters and statements one rule keeps; real functions have a few.
    private const int MaxParameters = 32, MaxStatements = 256;

    /// <summary>Whether the value has something that looks like a custom function call (--name() outside a string: a cheap test made at parse time.</summary>
    public static bool HasCall(string value)
    {
        int i = value.IndexOf("--", StringComparison.Ordinal);
        while (i >= 0)
        {
            if (CallAt(value, i, out _, out _)) return true;
            i = value.IndexOf("--", i + 2, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>
    /// Whether a call starts at <paramref name="i"/>: "--" that starts a token, a name and "(". Gives the name and the
    /// index of the parenthesis.
    /// </summary>
    private static bool CallAt(string value, int i, out string name, out int open)
    {
        name = "";
        open = -1;
        if (i > 0 && (IsNameChar(value[i - 1]) || value[i - 1] == '\\')) return false;
        int j = i + 2;
        while (j < value.Length && IsNameChar(value[j])) j++;
        if (j == i + 2 || j >= value.Length || value[j] != '(') return false;
        name = value.Substring(i, j - i);
        open = j;
        return true;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;

    /// <summary>
    /// Replaces every call of a function in <paramref name="functions"/>. Returns null when a call is invalid. A
    /// --name() that names no function is left as written, so the declaration's own parsing rejects it.
    /// </summary>
    public static string? Resolve(string value, Dictionary<string, CssFunction>? functions, CssVarScope? vars, MediaContext media)
    {
        if (functions == null || functions.Count == 0 || !HasCall(value)) return value;
        int calls = MaxCalls;
        return Run(value, functions, vars, media, 0, ref calls);
    }

    private static string? Run(string value, Dictionary<string, CssFunction> functions, CssVarScope? vars, MediaContext media, int depth, ref int calls)
    {
        if (depth > MaxDepth) return null;
        StringBuilder? sb = null;
        int last = 0;
        char quote = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c != '-' || !CallAt(value, i, out var name, out int open) || !functions.TryGetValue(name, out var function)) continue;
            if (--calls < 0) return null;
            int close = MediaQuery.MatchingParen(value, open);
            if (close < 0) return null;
            var args = Arguments(value.Substring(open + 1, close - open - 1));
            if (args == null) return null;
            // Arguments are values of the caller: calls in them are made first, in the caller's scope.
            for (int k = 0; k < args.Count; k++)
            {
                var arg = Run(args[k], functions, vars, media, depth + 1, ref calls);
                if (arg == null) return null;
                args[k] = arg;
            }
            var result = Call(function, args, functions, vars, media, depth, ref calls);
            if (result == null) return null;
            sb ??= new StringBuilder(value.Length + result.Length);
            sb.Append(value, last, i - last).Append(result);
            if (sb.Length > CssVariables.MaxLength) return null;
            last = close + 1;
            i = close;
        }
        if (sb == null) return value;
        sb.Append(value, last, value.Length - last);
        return sb.Length > CssVariables.MaxLength ? null : sb.ToString();
    }

    private static string? Call(CssFunction function, List<string> args, Dictionary<string, CssFunction> functions, CssVarScope? vars,
        MediaContext media, int depth, ref int calls)
    {
        if (args.Count > function.Parameters.Count) return null;
        // The function's own scope sits over the caller's custom properties. Values go in as written, as a custom
        // property's do, and var() resolves them when it reads them, so locals can use each other in any order.
        var scope = new CssVarScope(vars);
        for (int k = 0; k < function.Parameters.Count; k++)
        {
            var p = function.Parameters[k];
            var given = k < args.Count ? args[k] : null;
            if (given != null && given.Length > 0) scope.Set(p.Name, given);
            else if (p.Default != null) scope.Set(p.Name, p.Default);
            else return null;
        }
        string? result = null;
        foreach (var statement in function.Body)
        {
            if (!Holds(statement, media)) continue;
            if (statement.Name == "result") result = statement.Value;
            else scope.Set(statement.Name, statement.Value);
        }
        if (result == null) return null;
        var value = CssVariables.Substitute(result, scope);
        if (value == null) return null;
        value = Run(value, functions, scope, media, depth + 1, ref calls);
        // if() in the result sees the function's parameters and locals in its style() conditions.
        return value == null ? null : CssIf.Resolve(value, scope, media);
    }

    private static bool Holds(CssFunctionStatement statement, MediaContext media)
    {
        if (statement.Media != null)
            foreach (var query in statement.Media)
                if (!MediaQuery.Matches(query, media)) return false;
        if (statement.Supports != null)
            foreach (var condition in statement.Supports)
                if (!CssSupports.Evaluate(condition)) return false;
        return true;
    }

    /// <summary>
    /// The arguments of a call, split at top-level commas. An argument in braces ({1px, 2px}) may hold commas; the
    /// braces are taken off. Null for a malformed list.
    /// </summary>
    private static List<string>? Arguments(string inner)
    {
        var list = new List<string>();
        if (inner.Trim().Length == 0) return list;
        foreach (var part in SplitTopLevel(inner, ','))
        {
            var arg = part.Trim();
            if (arg.Length > 0 && arg[0] == '{')
            {
                if (arg[^1] != '}') return null;
                arg = arg.Substring(1, arg.Length - 2).Trim();
            }
            list.Add(arg);
        }
        return list;
    }

    /// <summary>Splits at <paramref name="separator"/> outside strings, parentheses, brackets and braces.</summary>
    private static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
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
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (c == separator && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    /// <summary>
    /// Parses an @function rule: its prelude (--name(--a, --b &lt;length&gt;: 1px) returns &lt;length&gt;) and its body of
    /// locals, result descriptors and @media and @supports blocks. Null when the prelude is not valid.
    /// </summary>
    public static CssFunction? Parse(string prelude, string body)
    {
        prelude = prelude.Trim();
        if (!CallAt(prelude, 0, out var name, out int open)) return null;
        int close = MediaQuery.MatchingParen(prelude, open);
        if (close < 0) return null;
        var rest = prelude.Substring(close + 1).Trim();
        if (rest.Length > 0 && !rest.StartsWith("returns", StringComparison.OrdinalIgnoreCase)) return null;
        var parameters = new List<CssFunctionParameter>();
        var list = prelude.Substring(open + 1, close - open - 1);
        if (list.Trim().Length > 0)
            foreach (var part in SplitTopLevel(list, ','))
            {
                var p = part.Trim();
                int colon = TopLevelColon(p);
                var head = (colon < 0 ? p : p.Substring(0, colon)).Trim();
                int end = 2;
                if (!head.StartsWith("--", StringComparison.Ordinal)) return null;
                while (end < head.Length && IsNameChar(head[end])) end++;
                if (end == 2) return null;
                var paramName = head.Substring(0, end);
                if (parameters.Exists(q => q.Name == paramName) || parameters.Count >= MaxParameters) return null;
                parameters.Add(new CssFunctionParameter(paramName, colon < 0 ? null : p.Substring(colon + 1).Trim()));
            }
        var statements = new List<CssFunctionStatement>();
        ParseBody(body, null, null, statements, 0);
        return new CssFunction { Name = name, Parameters = parameters, Body = statements };
    }

    private static void ParseBody(string body, string[]? media, string[]? supports, List<CssFunctionStatement> output, int depth)
    {
        int i = 0;
        while (i < body.Length && output.Count < MaxStatements)
        {
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ';')) i++;
            if (i >= body.Length) break;
            if (body[i] == '@')
            {
                int brace = IndexOutside(body, i, '{');
                if (brace < 0) return;
                int end = MatchingBrace(body, brace);
                if (end < 0) end = body.Length;
                var at = body.Substring(i + 1, brace - i - 1).Trim();
                int space = 0;
                while (space < at.Length && !char.IsWhiteSpace(at[space]) && at[space] != '(') space++;
                var keyword = at.Substring(0, space).ToLowerInvariant();
                var condition = at.Substring(space).Trim();
                var inner = body.Substring(brace + 1, Math.Max(0, end - brace - 1));
                if (depth < MaxDepth)
                {
                    if (keyword == "media") ParseBody(inner, Append(media, condition), supports, output, depth + 1);
                    else if (keyword == "supports") ParseBody(inner, media, Append(supports, condition), output, depth + 1);
                    // Other at-rules, such as @container, which needs a container the function cannot know, never apply.
                }
                i = end + 1;
                continue;
            }
            int semi = IndexOutside(body, i, ';');
            var declaration = body.Substring(i, (semi < 0 ? body.Length : semi) - i);
            i = semi < 0 ? body.Length : semi + 1;
            int colon = TopLevelColon(declaration);
            if (colon <= 0) continue;
            var key = declaration.Substring(0, colon).Trim();
            var value = declaration.Substring(colon + 1).Trim();
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) continue; // not allowed in a function
            if (key.Equals("result", StringComparison.OrdinalIgnoreCase)) output.Add(new CssFunctionStatement("result", value, media, supports));
            else if (key.StartsWith("--", StringComparison.Ordinal) && key.Length > 2) output.Add(new CssFunctionStatement(key, value, media, supports));
        }
    }

    private static string[] Append(string[]? list, string item)
    {
        if (list == null) return new[] { item };
        var longer = new string[list.Length + 1];
        list.CopyTo(longer, 0);
        longer[^1] = item;
        return longer;
    }

    /// <summary>The first <paramref name="target"/> from <paramref name="from"/> outside strings, parentheses and brackets (and braces, for ';').</summary>
    private static int IndexOutside(string s, int from, char target)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == target && depth == 0) return i;
            else if (c is '(' or '[' || c == '{' && target == ';') depth++;
            else if (c is ')' or ']' || c == '}' && target == ';') depth = Math.Max(0, depth - 1);
        }
        return -1;
    }

    private static int MatchingBrace(string s, int open)
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
            if (c is '"' or '\'') quote = c;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static int TopLevelColon(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ':' && depth == 0) return i;
        }
        return -1;
    }
}

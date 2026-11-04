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
        bool tainted = false;
        return Run(value, vars, null, null, depth, ref tainted);
    }

    /// <summary>
    /// Replaces every <c>var()</c> and <c>attr()</c> in <paramref name="value"/>, reading attributes through
    /// <paramref name="attrs"/>. Returns null when the declaration is invalid at computed-value time.
    /// </summary>
    public static string? Substitute(string value, CssVarScope? vars, CssAttr.Context attrs)
    {
        bool tainted = false;
        return Run(value, vars, null, attrs, 0, ref tainted);
    }

    /// <summary>
    /// The substitution itself. <paramref name="tainted"/> is set when the result holds text that came from an attribute,
    /// directly or through a custom property. Outside custom properties (where <paramref name="own"/> is null) such text
    /// may not be or reach a resource such as url(): an attribute must not make the document load anything.
    /// </summary>
    private static string? Run(string value, CssVarScope? vars, OwnValues? own, CssAttr.Context? attrs, int depth, ref bool tainted)
    {
        if (depth > MaxDepth || value.Length > MaxLength) return null;
        int i = Next(value, 0, attrs != null, out bool isAttr);
        if (i < 0) return value;
        var sb = new StringBuilder(value.Length + 16);
        var resources = new CssAttr.ResourceScan();
        int last = 0;
        while (i >= 0)
        {
            int close;
            string? replacement = null;
            bool fromAttribute = false;
            if (isAttr)
            {
                // The head is read first, so that an attr() left for generated text costs nothing more, however deeply
                // its fallback nests others.
                if (!CssAttr.TryParseHead(value, i + 5, out var call)) return null;
                if (call.IsGeneratedText && attrs!.KeepUntyped)
                {
                    // An untyped attr() of generated text stays for GeneratedContent; a var() in its fallback is still replaced.
                    i = Next(value, call.End, true, out isAttr);
                    continue;
                }
                string? fallback = null;
                if (call.End >= value.Length) close = value.Length; // closed by the end of the declaration
                else if (value[call.End] == ')') close = call.End;
                else
                {
                    close = MatchingParen(value, i + 4);
                    if (close < 0) close = value.Length;
                    fallback = value.Substring(call.End + 1, close - call.End - 1).Trim();
                }
                replacement = ResolveAttr(call, fallback, vars, own, attrs!, depth);
                fromAttribute = true;
            }
            else
            {
                int open = i + 3;
                close = MatchingParen(value, open);
                if (close < 0) return null;
                string inner = value.Substring(open + 1, close - open - 1);
                int comma = TopLevelComma(inner);
                string name = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
                string? fallback = comma < 0 ? null : inner.Substring(comma + 1).Trim();
                bool t = false;
                if (own != null && own.Declares(name))
                {
                    if (vars!.TryGetValue(name, out var ov) && ov.Length > 0)
                    {
                        replacement = own.Resolve(name, depth + 1);
                        t = own.IsTainted(name);
                    }
                }
                else if (vars != null && vars.TryGetValue(name, out var v, out t) && v.Length > 0)
                    // A value with text from an attribute is final: that text is never read as CSS again.
                    replacement = t ? v : Run(v, vars, own, attrs, depth + 1, ref t);
                // On an attribute reference cycle, every substitution up to the attr() of that attribute fails, fallbacks included.
                if (replacement == null && fallback != null && attrs?.Cycle == null)
                {
                    t = false;
                    replacement = Run(fallback, vars, own, attrs, depth + 1, ref t);
                }
                fromAttribute = t;
            }
            if (replacement == null || sb.Length + replacement.Length > MaxLength) return null;
            if (fromAttribute)
            {
                if (own == null && (resources.Inside(value, i) || CssAttr.ContainsResource(replacement))) return null;
                tainted = true;
            }
            sb.Append(value, last, i - last);
            sb.Append(replacement);
            last = Math.Min(close + 1, value.Length);
            i = Next(value, last, attrs != null, out isAttr);
        }
        sb.Append(value, last, value.Length - last);
        return sb.Length > MaxLength ? null : sb.ToString();
    }

    /// <summary>
    /// The next var( (or attr( with <paramref name="attrs"/>) from <paramref name="from"/> that starts a token. attr( is
    /// looked for outside strings only, so that the text of an attribute, which becomes a string, is never read again.
    /// </summary>
    private static int Next(string value, int from, bool attrs, out bool isAttr)
    {
        isAttr = false;
        int i = value.IndexOf("var(", from, StringComparison.OrdinalIgnoreCase);
        // "var(" must start a token, not end a longer identifier.
        while (i > 0 && (char.IsLetterOrDigit(value[i - 1]) || value[i - 1] == '-'))
            i = value.IndexOf("var(", i + 4, StringComparison.OrdinalIgnoreCase);
        if (!attrs) return i;
        int a = CssAttr.Next(value, from);
        if (a >= 0 && (i < 0 || a < i))
        {
            isAttr = true;
            return a;
        }
        return i;
    }

    /// <summary>
    /// The value of one attr() call: the attribute parsed as the call's type, else the fallback; null when neither
    /// gives a value. <paramref name="fallback"/> is null when the call has no comma.
    /// </summary>
    private static string? ResolveAttr(in CssAttr.Call call, string? fallback, CssVarScope? vars, OwnValues? own, CssAttr.Context attrs, int depth)
    {
        if (--attrs.Budget < 0 || depth > MaxDepth) return null;
        foreach (var active in attrs.Active)
        {
            if (string.Equals(active, call.Name, StringComparison.OrdinalIgnoreCase))
            {
                attrs.Cycle = active;
                return null;
            }
        }
        attrs.Active.Add(call.Name);
        string? result = null;
        var raw = attrs.Attribute(call.Name);
        if (raw != null && CssAttr.Typed(call, raw) is { } typed)
        {
            if (call.Type != CssAttr.AttrType.Syntax) result = typed;
            else
            {
                // The value of a type() is CSS: var() and attr() in it are substituted before it is matched to the syntax.
                bool t = false;
                var v = Run(typed, vars, own, attrs, depth + 1, ref t)?.Trim();
                if (v != null && CssAttr.IsDeclarationValue(v) && !CssParser.IsCssWideKeyword(v)
                    && (call.Argument == "*" || v.Length > 0 && CssPropertySyntax.Matches(call.Argument!, v, relativeLengths: true)))
                    result = v;
            }
        }
        if (result == null && attrs.Cycle == call.Name) attrs.Cycle = null;
        if (result == null && fallback != null && attrs.Cycle == null)
        {
            bool t = false;
            result = Run(fallback, vars, own, attrs, depth + 1, ref t);
        }
        // Without a type and a fallback, a missing attribute is the empty string.
        else if (result == null && raw == null && call.Type == CssAttr.AttrType.String && fallback == null) result = "\"\"";
        attrs.Active.RemoveAt(attrs.Active.Count - 1);
        if (attrs.Cycle == call.Name) attrs.Cycle = null;
        // Attributes that refer to others twice double the text at each step; every result is charged to the
        // declaration's budget of characters, and one result is no longer than an attribute may be.
        if (result != null && (result.Length > CssAttr.MaxValueLength || (attrs.Characters -= result.Length) < 0)) return null;
        return result;
    }

    /// <summary>
    /// Builds the custom properties of an element from its parent's and the element's own declarations (in cascade
    /// order). References between variables are resolved here, so stored values never contain var(); attr() is
    /// resolved here too, on the element that declares the property, when <paramref name="attrs"/> is given.
    /// </summary>
    public static CssVarScope Compute(CssVarScope? inherited, List<KeyValuePair<string, string>> own, CssAttr.Context? attrs = null)
    {
        // Only the element's own declarations are stored; lookups fall through to the parent. Copying the whole inherited
        // set into every element would cost (variables x elements) time and memory.
        var raw = new CssVarScope(inherited);
        foreach (var (name, value) in own)
        {
            var v = value.Trim();
            if (v.Equals("initial", StringComparison.OrdinalIgnoreCase)) raw.Set(name, null);
            else if (v.Equals("inherit", StringComparison.OrdinalIgnoreCase))
            {
                if (inherited != null && inherited.TryGetValue(name, out var pv, out bool pt)) raw.Set(name, pv, pt);
                else raw.Set(name, null);
            }
            else raw.Set(name, v);
        }
        // Resolve the element's own values against the combined set; inherited values are already resolved.
        var resolver = new OwnValues(raw, own, attrs);
        var result = new CssVarScope(inherited);
        foreach (var (name, _) in own)
        {
            var value = resolver.Resolve(name, 0);
            result.Set(name, value, value != null && resolver.IsTainted(name));
        }
        return result;
    }

    /// <summary>
    /// Resolves each of an element's own custom properties once. Without the cache, a value that names a variable in
    /// both its reference and its fallback was resolved twice per level, which is exponential in the nesting; and a
    /// variable that refers back to itself ran to the depth limit before its fallback was tried.
    /// </summary>
    private sealed class OwnValues(CssVarScope raw, List<KeyValuePair<string, string>> own, CssAttr.Context? attrs)
    {
        private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
        private readonly List<string> _active = [];
        private readonly HashSet<string> _cyclic = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = NamesOf(own);
        private HashSet<string>? _tainted; // the resolved values that hold text from an attribute

        public bool IsTainted(string name) => _tainted != null && _tainted.Contains(name);

        private static HashSet<string> NamesOf(List<KeyValuePair<string, string>> own)
        {
            var names = new HashSet<string>(own.Count, StringComparer.Ordinal);
            foreach (var (name, _) in own) names.Add(name);
            return names;
        }

        public bool Declares(string name) => _names.Contains(name);

        public string? Resolve(string name, int depth)
        {
            if (_resolved.TryGetValue(name, out var done)) return done;
            int at = _active.IndexOf(name);
            if (at >= 0)
            {
                // Like browsers, every variable on a reference cycle is invalid, so a var() of one takes its fallback.
                for (int k = at; k < _active.Count; k++) _cyclic.Add(_active[k]);
                return null;
            }
            if (!raw.TryGetValue(name, out var v, out bool tainted)) return _resolved[name] = null;
            if (tainted) (_tainted ??= new(StringComparer.Ordinal)).Add(name);
            if (tainted || !CssParser.HasVar(v) && (attrs == null || !CssAttr.Has(v))) return _resolved[name] = v;
            // Each custom property gets its own attr() budget; one it refers to shares the budget of the one being resolved.
            if (_active.Count == 0) attrs?.ResetBudget();
            _active.Add(name);
            var result = Run(v, raw, this, attrs, depth, ref tainted);
            _active.RemoveAt(_active.Count - 1);
            if (_cyclic.Contains(name)) result = null;
            if (tainted && result != null) (_tainted ??= new(StringComparer.Ordinal)).Add(name);
            return _resolved[name] = result;
        }
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

    // The names whose values hold text from an attribute, which may not reach a url(); null while there are none.
    private HashSet<string>? _tainted;

    public void Set(string name, string? value, bool tainted = false)
    {
        _own[name] = value;
        if (tainted) (_tainted ??= new(StringComparer.Ordinal)).Add(name);
        else _tainted?.Remove(name);
    }

    public bool TryGetValue(string name, out string value) => TryGetValue(name, out value, out _);

    public bool TryGetValue(string name, out string value, out bool tainted)
    {
        for (var s = this; s != null; s = s._parent)
        {
            if (s._own.TryGetValue(name, out var v))
            {
                value = v!;
                tainted = v != null && s._tainted != null && s._tainted.Contains(name);
                return v != null;
            }
        }
        value = null!;
        tainted = false;
        return false;
    }
}

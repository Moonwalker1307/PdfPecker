using System.Text;

namespace PdfPecker.Css;

internal sealed class Declaration
{
    public readonly string Property;
    public readonly string Value;
    public readonly bool Important;

    public Declaration(string property, string value, bool important)
    {
        Property = property;
        Value = value;
        Important = important;
    }

    public override string ToString() => Property + ": " + Value + (Important ? " !important" : "");
}

internal sealed class StyleRule
{
    public readonly Selector Selector;
    public readonly Declaration[] Declarations;
    public readonly int Order;
    public readonly CascadeLayer? Layer; // null for rules outside any @layer

    public StyleRule(Selector selector, Declaration[] declarations, int order, CascadeLayer? layer = null)
    {
        Selector = selector;
        Declarations = declarations;
        Order = order;
        Layer = layer;
    }
}

/// <summary>
/// A cascade layer (<c>@layer</c>). Layers are ordered by their first appearance; a layer's sub-layers come before the
/// layer's own rules, and rules outside every layer come last, so they win over layered ones.
/// </summary>
internal sealed class CascadeLayer
{
    public readonly string Name;
    public readonly List<CascadeLayer> Children = new();
    public int Rank; // 0 = the earliest layer; set by Stylesheet.RankLayers

    public CascadeLayer(string name) => Name = name;

    public CascadeLayer Child(string name)
    {
        foreach (var c in Children)
            if (c.Name == name) return c;
        var layer = new CascadeLayer(name);
        if (Children.Count < 1000) Children.Add(layer);
        return layer;
    }
}

/// <summary>An <c>@page</c> rule: its page selector, declarations and margin boxes (<c>@top-center</c> and so on).</summary>
internal sealed class PageRule
{
    public string Name = "";     // the page name of a named page rule (@page wide), or ""
    public string Selector = ""; // "", ":first", ":left", ":right" or ":blank"
    public readonly List<Declaration> Declarations = new();
    public readonly Dictionary<string, List<Declaration>> MarginBoxes = new(StringComparer.Ordinal);
    public int Specificity => (Name.Length > 0 ? 4 : 0) + Selector switch { ":first" or ":blank" => 2, ":left" or ":right" => 1, _ => 0 };
}

/// <summary>A custom property registered with <c>@property</c>.</summary>
internal sealed record RegisteredProperty(string Name, string? InitialValue, bool Inherits);

internal sealed class Stylesheet
{
    public readonly List<StyleRule> Rules = new();
    public readonly List<Declaration> PageDeclarations = new(); // from @page rules without a page selector
    public readonly List<PageRule> PageRules = new();
    public readonly List<List<Declaration>> FontFaces = new(); // @font-face descriptors, one list per rule
    public readonly List<RegisteredProperty> RegisteredProperties = new();
    public readonly CascadeLayer LayerRoot = new("");
    public MediaContext Media = new();
    public int NextOrder;
    private int _anonymousLayers;
    private bool _ranked;

    internal string AnonymousLayerName() => "\0anon" + _anonymousLayers++;

    /// <summary>Numbers the layers in cascade order (sub-layers before their parent) and returns how many there are.</summary>
    public int RankLayers()
    {
        int next = 0;
        void Visit(CascadeLayer layer)
        {
            foreach (var c in layer.Children) Visit(c);
            if (layer != LayerRoot) layer.Rank = Math.Min(next++, MaxLayerRank);
        }
        Visit(LayerRoot);
        _ranked = true;
        return next;
    }

    /// <summary>Ranks above this share the last rank: far more layers than any stylesheet uses.</summary>
    public const int MaxLayerRank = 250;

    public bool LayersRanked => _ranked;
}

/// <summary>Parses CSS stylesheets and declaration blocks. Shorthands are expanded into longhands while parsing.</summary>
internal static class CssParser
{
    public static void ParseStylesheet(string css, Stylesheet sheet)
    {
        string s = StripComments(css);
        int i = 0;
        ParseRules(s, ref i, sheet, s.Length, 0, null);
    }

    // Nested @media, @supports and @layer blocks (and nested style rules) deeper than this are skipped, so hostile CSS
    // cannot recurse without bound.
    private const int MaxMediaNesting = 16;

    private static void ParseRules(string s, ref int i, Stylesheet sheet, int end, int depth, CascadeLayer? layer)
    {
        while (i < end)
        {
            while (i < end && (char.IsWhiteSpace(s[i]) || s[i] == ';')) i++;
            if (i >= end) break;
            if (s[i] == '}') { i++; continue; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0) { i += 4; continue; }
            if (s.Length - i >= 3 && string.CompareOrdinal(s, i, "-->", 0, 3) == 0) { i += 3; continue; }

            if (s[i] == '@')
            {
                int nameStart = ++i;
                while (i < end && (char.IsLetterOrDigit(s[i]) || s[i] == '-')) i++;
                string atName = s.Substring(nameStart, i - nameStart).ToLowerInvariant();
                int brace = IndexOfOutsideQuotes(s, '{', i, end);
                // Only look for the ';' that ends a statement before the block starts: scanning to the end for every
                // at-rule would make a sheet full of blocks quadratic.
                int semi = IndexOfOutsideQuotes(s, ';', i, brace < 0 ? end : brace);
                if (semi >= 0 && (brace < 0 || semi < brace))
                {
                    // "@layer a, b;" fixes the order of layers; @import, @charset and @namespace are ignored.
                    if (atName == "layer")
                        foreach (var name in SplitTopLevel(s.Substring(i, semi - i), ','))
                            LayerFor(sheet, layer, name.Trim());
                    i = semi + 1;
                    continue;
                }
                if (brace < 0) { i = end; break; }
                string prelude = s.Substring(i, brace - i).Trim();
                int blockEnd = MatchingBrace(s, brace, end);
                if (depth < MaxMediaNesting && ConditionalBlock(atName, prelude, sheet, layer, out var inner))
                {
                    int start = brace + 1;
                    ParseRules(s, ref start, sheet, blockEnd, depth + 1, inner);
                }
                else if (atName == "page")
                    ParsePage(prelude, s.Substring(brace + 1, blockEnd - brace - 1), sheet);
                else if (atName == "font-face")
                {
                    var descriptors = new List<Declaration>();
                    ParseDeclarations(s.Substring(brace + 1, blockEnd - brace - 1), descriptors);
                    if (descriptors.Count > 0) sheet.FontFaces.Add(descriptors);
                }
                else if (atName == "property")
                    ParseProperty(prelude, s.Substring(brace + 1, blockEnd - brace - 1), sheet);
                i = Math.Min(blockEnd + 1, end);
                continue;
            }

            int open = IndexOfOutsideQuotes(s, '{', i, end);
            if (open < 0) { i = end; break; }
            int close = MatchingBrace(s, open, end);
            string selectorText = s.Substring(i, open - i);
            i = Math.Min(close + 1, end);
            var selectors = SplitTopLevel(selectorText, ',');
            for (int k = 0; k < selectors.Count; k++) selectors[k] = selectors[k].Trim();
            ParseStyleBlock(s, open + 1, close, selectors, sheet, layer, depth);
        }
    }

    /// <summary>
    /// For @media, @supports and @layer blocks: whether their rules apply, and the layer they go into. A block layer
    /// without a name is a new anonymous layer.
    /// </summary>
    private static bool ConditionalBlock(string atName, string prelude, Stylesheet sheet, CascadeLayer? layer, out CascadeLayer? inner)
    {
        inner = layer;
        switch (atName)
        {
            case "media":
                return MediaQuery.Matches(prelude, sheet.Media);
            case "supports":
                return CssSupports.Evaluate(prelude);
            case "layer":
                inner = LayerFor(sheet, layer, prelude.Length == 0 ? sheet.AnonymousLayerName() : prelude);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The layer named <paramref name="name"/> (dotted for sub-layers) inside <paramref name="parent"/>, created on first use.</summary>
    private static CascadeLayer LayerFor(Stylesheet sheet, CascadeLayer? parent, string name)
    {
        var layer = parent ?? sheet.LayerRoot;
        foreach (var part in name.Split('.'))
        {
            var n = part.Trim();
            if (n.Length > 0) layer = layer.Child(n);
        }
        return layer;
    }

    /// <summary>
    /// Parses a style rule's block: its declarations and any nested rules (CSS Nesting). Declarations that follow a
    /// nested rule come after it in the cascade, as in Chrome.
    /// </summary>
    private static void ParseStyleBlock(string s, int start, int end, List<string> selectors, Stylesheet sheet, CascadeLayer? layer, int depth)
    {
        if (IndexOfOutsideQuotes(s, '{', start, end) < 0)
        {
            AddRule(s.Substring(start, end - start), selectors, sheet, layer);
            return;
        }
        int i = start, declStart = start;
        while (i < end)
        {
            int brace = IndexOfOutsideQuotes(s, '{', i, end);
            if (brace < 0) break;
            // The nested rule's prelude starts after the last ';' (or '}') before its '{'.
            int preludeStart = brace;
            while (preludeStart > declStart && s[preludeStart - 1] != ';' && s[preludeStart - 1] != '}') preludeStart--;
            if (preludeStart > declStart) AddRule(s.Substring(declStart, preludeStart - declStart), selectors, sheet, layer);
            int close = MatchingBrace(s, brace, end);
            var prelude = s.Substring(preludeStart, brace - preludeStart).Trim();
            if (depth < MaxMediaNesting)
            {
                if (prelude.StartsWith('@'))
                {
                    int nameEnd = 1;
                    while (nameEnd < prelude.Length && (char.IsLetterOrDigit(prelude[nameEnd]) || prelude[nameEnd] == '-')) nameEnd++;
                    var atName = prelude.Substring(1, nameEnd - 1).ToLowerInvariant();
                    if (ConditionalBlock(atName, prelude.Substring(nameEnd).Trim(), sheet, layer, out var inner))
                        ParseStyleBlock(s, brace + 1, close, selectors, sheet, inner, depth + 1);
                }
                else if (prelude.Length > 0)
                    ParseStyleBlock(s, brace + 1, close, NestedSelectors(selectors, prelude), sheet, layer, depth + 1);
            }
            i = declStart = Math.Min(close + 1, end);
        }
        if (declStart < end) AddRule(s.Substring(declStart, end - declStart), selectors, sheet, layer);
    }

    private static void AddRule(string body, List<string> selectors, Stylesheet sheet, CascadeLayer? layer)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        var decls = new List<Declaration>();
        ParseDeclarations(body, decls);
        if (decls.Count == 0) return;
        var declArray = decls.ToArray();
        foreach (var selText in selectors)
        {
            var sel = Selector.Parse(selText);
            if (sel != null) sheet.Rules.Add(new StyleRule(sel, declArray, sheet.NextOrder++, layer));
        }
    }

    /// <summary>
    /// Resolves the selectors of a nested rule against its parent's: "&amp;" stands for the parent, and a selector
    /// without one is relative to it (a descendant, or after a leading combinator). Each parent selector is substituted
    /// in turn, so ".a, .b { &amp;:hover {} }" gives ".a:hover" and ".b:hover".
    /// </summary>
    private static List<string> NestedSelectors(List<string> parents, string nested)
    {
        var result = new List<string>();
        foreach (var raw in SplitTopLevel(nested, ','))
        {
            var child = raw.Trim();
            if (child.Length == 0) continue;
            foreach (var parent in parents)
            {
                if (result.Count >= 256) return result;
                if (child.IndexOf('&') >= 0) result.Add(child.Replace("&", parent));
                else result.Add(parent + " " + child);
            }
        }
        return result;
    }

    /// <summary>Parses an @page rule: "@page", "@page :first" and so on, with declarations and margin boxes.</summary>
    private static void ParsePage(string prelude, string body, Stylesheet sheet)
    {
        // Page selectors: an optional page name (named pages) and an optional :first, :left, :right or :blank;
        // several separated by commas.
        var selectors = new List<(string Name, string Pseudo)>();
        foreach (var part in prelude.Split(','))
        {
            var sel = part.Trim();
            int colon = sel.IndexOf(':');
            var name = colon < 0 ? sel : sel.Substring(0, colon).TrimEnd();
            var pseudo = colon < 0 ? "" : sel.Substring(colon).ToLowerInvariant();
            if (pseudo.Length > 0 && pseudo is not (":first" or ":left" or ":right" or ":blank")) return;
            if (name.Length > 0 && !IsPageName(name)) return;
            selectors.Add((name, pseudo));
        }
        if (selectors.Count == 0) return;
        var rule = new PageRule { Name = selectors[0].Name, Selector = selectors[0].Pseudo };
        int i = 0, declStart = 0;
        var declText = new System.Text.StringBuilder();
        while (i < body.Length)
        {
            int at = IndexOfOutsideQuotes(body, '@', i, body.Length);
            if (at < 0) break;
            int brace = IndexOfOutsideQuotes(body, '{', at, body.Length);
            if (brace < 0) break;
            declText.Append(body, declStart, at - declStart).Append(';');
            int close = MatchingBrace(body, brace, body.Length);
            var name = body.Substring(at + 1, brace - at - 1).Trim().ToLowerInvariant();
            if (PageMarginBoxNames.Contains(name))
            {
                var decls = new List<Declaration>();
                ParseDeclarations(body.Substring(brace + 1, Math.Max(0, close - brace - 1)), decls);
                if (rule.MarginBoxes.TryGetValue(name, out var existing)) existing.AddRange(decls);
                else rule.MarginBoxes[name] = decls;
            }
            i = declStart = Math.Min(close + 1, body.Length);
        }
        declText.Append(body, declStart, body.Length - declStart);
        ParseDeclarations(declText.ToString(), rule.Declarations);
        foreach (var (name, pseudo) in selectors)
        {
            var r = rule;
            if (name != rule.Name || pseudo != rule.Selector)
            {
                r = new PageRule { Name = name, Selector = pseudo };
                r.Declarations.AddRange(rule.Declarations);
                foreach (var (box, decls) in rule.MarginBoxes) r.MarginBoxes[box] = decls;
            }
            sheet.PageRules.Add(r);
            if (name.Length == 0 && pseudo.Length == 0) sheet.PageDeclarations.AddRange(r.Declarations);
        }
    }

    /// <summary>A page name: a CSS identifier, not "auto".</summary>
    internal static bool IsPageName(string name)
    {
        if (name.Length == 0 || name.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        if (!(char.IsLetter(name[0]) || name[0] is '_' or '-')) return false;
        foreach (char c in name)
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-')) return false;
        return true;
    }

    /// <summary>The sixteen page-margin boxes of CSS Paged Media.</summary>
    internal static readonly HashSet<string> PageMarginBoxNames = new(StringComparer.Ordinal)
    {
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner",
        "bottom-left-corner", "bottom-left", "bottom-center", "bottom-right", "bottom-right-corner",
        "left-top", "left-middle", "left-bottom", "right-top", "right-middle", "right-bottom",
    };

    /// <summary>Parses an @property rule; one without a valid "inherits" descriptor is ignored, as the spec requires.</summary>
    private static void ParseProperty(string prelude, string body, Stylesheet sheet)
    {
        var name = prelude.Trim();
        if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length < 3) return;
        string? initial = null, syntax = null;
        bool? inherits = null;
        foreach (var part in SplitTopLevel(body, ';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var key = part.Substring(0, colon).Trim().ToLowerInvariant();
            var value = part.Substring(colon + 1).Trim();
            switch (key)
            {
                case "initial-value": initial = value; break;
                case "syntax": syntax = value.Trim('"', '\'', ' '); break;
                case "inherits":
                    inherits = value.ToLowerInvariant() switch { "true" => true, "false" => false, _ => null };
                    break;
            }
        }
        if (inherits == null || syntax == null) return;
        // A syntax other than "*" needs an initial value.
        if (syntax != "*" && string.IsNullOrEmpty(initial)) return;
        if (sheet.RegisteredProperties.Count >= 1024) return;
        sheet.RegisteredProperties.RemoveAll(p => p.Name == name); // the last registration wins
        sheet.RegisteredProperties.Add(new RegisteredProperty(name, string.IsNullOrEmpty(initial) ? null : initial, inherits.Value));
    }

    private static int MatchingBrace(string s, int open, int end)
    {
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\'')
            {
                int q = s.IndexOf(c, i + 1);
                if (q < 0 || q >= end) return end;
                i = q;
            }
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return end;
    }

    private static int IndexOfOutsideQuotes(string s, char target, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c == target) return i;
            if (c == '"' || c == '\'')
            {
                int q = s.IndexOf(c, i + 1);
                if (q < 0 || q >= end) return -1;
                i = q;
            }
        }
        return -1;
    }

    public static string StripComments(string css)
    {
        int c = css.IndexOf("/*", StringComparison.Ordinal);
        if (c < 0) return css;
        var sb = new StringBuilder(css.Length);
        int i = 0;
        while (c >= 0)
        {
            sb.Append(css, i, c - i);
            int e = css.IndexOf("*/", c + 2, StringComparison.Ordinal);
            if (e < 0) { i = css.Length; break; }
            i = e + 2;
            c = css.IndexOf("/*", i, StringComparison.Ordinal);
        }
        if (i < css.Length) sb.Append(css, i, css.Length - i);
        return sb.ToString();
    }

    /// <summary>Parses "a: b; c: d !important" and appends the expanded longhand declarations.</summary>
    public static void ParseDeclarations(string body, List<Declaration> output)
    {
        if (body.IndexOf("/*", StringComparison.Ordinal) >= 0) body = StripComments(body);
        foreach (var part in SplitTopLevel(body, ';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string name = part.Substring(0, colon).Trim();
            if (!name.StartsWith("--", StringComparison.Ordinal)) name = name.ToLowerInvariant(); // custom properties are case-sensitive
            string value = part.Substring(colon + 1).Trim();
            bool important = false;
            int bang = value.LastIndexOf('!');
            if (bang >= 0 && value.Substring(bang + 1).Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value.Substring(0, bang).TrimEnd();
            }
            if (name.Length == 0 || value.Length == 0 && !name.StartsWith("--", StringComparison.Ordinal)) continue;
            Expand(name, value, important, output);
        }
    }

    /// <summary>Splits on a separator that is not inside parentheses, brackets or quotes.</summary>
    public static List<string> SplitTopLevel(string s, char sep)
    {
        var result = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\') i++;
                continue;
            }
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == sep && depth == 0)
            {
                if (i > start) result.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        if (start < s.Length) result.Add(s.Substring(start));
        return result;
    }

    /// <summary>Splits a value into whitespace-separated tokens, keeping functions and quoted strings intact.</summary>
    public static List<string> Tokens(string s)
    {
        var result = new List<string>(4);
        int depth = 0, start = -1;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            bool ws = c == ' ' || c == '\t' || c == '\n' || c == '\r';
            if (ws && depth == 0)
            {
                if (start >= 0) { result.Add(s.Substring(start, i - start)); start = -1; }
                continue;
            }
            if (start < 0) start = i;
            if (c == '"' || c == '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
        }
        if (start >= 0) result.Add(s.Substring(start));
        return result;
    }

    private static readonly string[] Sides = { "top", "right", "bottom", "left" };

    /// <summary>True when the value references a custom property and can only be expanded once the element's variables are known.</summary>
    public static bool HasVar(string value) => value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static void Expand(string name, string value, bool important, List<Declaration> output)
    {
        void Add(string n, string v) => output.Add(new Declaration(n, v, important));

        // A shorthand whose value uses var() is kept whole; the style resolver substitutes and expands it per element.
        if (name.StartsWith("--", StringComparison.Ordinal) || HasVar(value))
        {
            output.Add(new Declaration(name, value, important));
            return;
        }

        switch (name)
        {
            case "inset":
            {
                var t = Tokens(value);
                if (t.Count == 0 || t.Count > 4) return;
                var four = FourSides(t);
                for (int k = 0; k < 4; k++) Add(Sides[k], four[k]);
                return;
            }
            case "margin":
            case "padding":
            {
                var t = Tokens(value);
                if (t.Count == 0 || t.Count > 4) return;
                var four = FourSides(t);
                for (int k = 0; k < 4; k++) Add(name + "-" + Sides[k], four[k]);
                return;
            }
            case "border-radius":
            {
                // Horizontal radii, then optionally "/" and vertical radii, each one to four values like margin.
                int slash = value.IndexOf('/');
                var ht = Tokens(slash < 0 ? value : value.Substring(0, slash));
                var vt = slash < 0 ? ht : Tokens(value.Substring(slash + 1));
                if (ht.Count is 0 or > 4 || vt.Count is 0 or > 4) return;
                var hs = FourSides(ht);
                var vs = FourSides(vt);
                string[] corners = { "top-left", "top-right", "bottom-right", "bottom-left" };
                for (int k = 0; k < 4; k++) Add("border-" + corners[k] + "-radius", hs[k] == vs[k] ? hs[k] : hs[k] + " " + vs[k]);
                return;
            }
            case "border-width":
            case "border-style":
            case "border-color":
            {
                var t = Tokens(value);
                if (t.Count == 0 || t.Count > 4) return;
                var four = FourSides(t);
                string suffix = name.Substring(7); // "width" / "style" / "color"
                for (int k = 0; k < 4; k++) Add("border-" + Sides[k] + "-" + suffix, four[k]);
                return;
            }
            case "border":
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
            {
                string width = "medium", style = "none", color = "currentcolor";
                foreach (var tok in Tokens(value))
                {
                    var lower = tok.ToLowerInvariant();
                    if (IsBorderStyle(lower)) style = lower;
                    else if (lower is "thin" or "medium" or "thick" || char.IsDigit(lower[0]) || lower[0] == '.' || CssMath.IsMathFunction(lower)) width = lower;
                    else color = tok;
                }
                if (name == "border")
                {
                    foreach (var side in Sides)
                    {
                        Add("border-" + side + "-width", width);
                        Add("border-" + side + "-style", style);
                        Add("border-" + side + "-color", color);
                    }
                }
                else
                {
                    Add(name + "-width", width);
                    Add(name + "-style", style);
                    Add(name + "-color", color);
                }
                return;
            }
            case "background":
                CssBackground.ExpandShorthand(value, Add);
                return;
            case "outline":
            {
                string width = "medium", style = "none", color = "currentcolor";
                foreach (var tok in Tokens(value))
                {
                    var lower = tok.ToLowerInvariant();
                    if (IsBorderStyle(lower) || lower == "auto") style = lower;
                    else if (lower is "thin" or "medium" or "thick" || char.IsDigit(lower[0]) || lower[0] == '.') width = lower;
                    else color = tok;
                }
                Add("outline-width", width);
                Add("outline-style", style);
                Add("outline-color", color);
                return;
            }
            case "font":
                ExpandFont(value, output, important);
                return;
            case "list-style":
                foreach (var tok in Tokens(value))
                {
                    var lower = tok.ToLowerInvariant();
                    if (lower is "inside" or "outside") Add("list-style-position", lower);
                    else if (lower[0] is '"' or '\'') Add("list-style-type", tok);
                    else if (!lower.StartsWith("url(", StringComparison.Ordinal)) Add("list-style-type", lower);
                }
                return;
            case "page-break-before":
            case "page-break-after":
            case "page-break-inside":
                Add(name.Substring(5), value.ToLowerInvariant());
                return;
            case "text-decoration":
            {
                // Line keywords, a style, a thickness and a colour, in any order; missing parts reset.
                var lines = new List<string>();
                string style = "solid", thickness = "auto", color = "currentcolor";
                foreach (var tok in Tokens(value))
                {
                    var t = tok.ToLowerInvariant();
                    if (t is "none" or "underline" or "overline" or "line-through" or "blink") { if (t != "none") lines.Add(t); }
                    else if (t is "solid" or "double" or "dotted" or "dashed" or "wavy") style = t;
                    else if (t is "auto" or "from-font" || char.IsDigit(t[0]) || t[0] == '.' || CssMath.IsMathFunction(t)) thickness = t;
                    else color = tok;
                }
                Add("text-decoration-line", lines.Count == 0 ? "none" : string.Join(" ", lines));
                Add("text-decoration-style", style);
                Add("text-decoration-thickness", thickness);
                Add("text-decoration-color", color);
                return;
            }
            case "border-spacing":
            {
                var t = Tokens(value);
                if (t.Count == 0) return;
                Add("border-spacing-h", t[0]);
                Add("border-spacing-v", t.Count > 1 ? t[1] : t[0]);
                return;
            }
            case "word-wrap":
                Add("overflow-wrap", value);
                return;
            case "flex":
                ExpandFlex(value, output, important);
                return;
            case "flex-flow":
                foreach (var tok in Tokens(value.ToLowerInvariant()))
                {
                    if (tok is "row" or "row-reverse" or "column" or "column-reverse") Add("flex-direction", tok);
                    else Add("flex-wrap", tok);
                }
                return;
            case "gap":
            case "grid-gap":
            {
                var t = Tokens(value);
                if (t.Count == 0) return;
                Add("row-gap", t[0]);
                Add("column-gap", t.Count > 1 ? t[1] : t[0]);
                return;
            }
            case "grid-row-gap":
                Add("row-gap", value);
                return;
            case "grid-column-gap":
                Add("column-gap", value);
                return;
            case "grid-column":
            case "grid-row":
            {
                var parts = SplitTopLevel(value, '/');
                string start = parts.Count > 0 ? parts[0].Trim() : "auto";
                string end = parts.Count > 1 ? parts[1].Trim() : IdentOrAuto(start);
                Add(name + "-start", start);
                Add(name + "-end", end);
                return;
            }
            case "grid-area":
            {
                // Omitted values copy the matching start value when it is a name, otherwise they are auto.
                var parts = SplitTopLevel(value, '/');
                string Part(int k) => parts.Count > k ? parts[k].Trim() : "auto";
                string rowStart = Part(0);
                string colStart = parts.Count > 1 ? Part(1) : IdentOrAuto(rowStart);
                string rowEnd = parts.Count > 2 ? Part(2) : IdentOrAuto(rowStart);
                string colEnd = parts.Count > 3 ? Part(3) : IdentOrAuto(colStart);
                Add("grid-row-start", rowStart);
                Add("grid-column-start", colStart);
                Add("grid-row-end", rowEnd);
                Add("grid-column-end", colEnd);
                return;
            }
            case "grid-template":
                ExpandGridTemplate(value, output, important);
                return;
            case "place-items":
            case "place-content":
            case "place-self":
            {
                var t = Tokens(value);
                if (t.Count == 0) return;
                string suffix = name.Substring(6);
                Add("align-" + suffix, t[0]);
                Add("justify-" + suffix, t.Count > 1 ? t[t.Count - 1] : t[0]);
                return;
            }
            case "font-variant":
            {
                // The caps and ligature parts; numeric, east-asian and the rest are not supported.
                var lower = value.Trim().ToLowerInvariant();
                if (lower is "normal" or "none" or "inherit" or "initial" or "unset")
                {
                    Add("font-variant-caps", lower == "none" ? "normal" : lower);
                    Add("font-variant-ligatures", lower);
                    return;
                }
                string caps = "normal";
                var ligatures = new List<string>();
                foreach (var t in Tokens(lower))
                {
                    if (t is "small-caps" or "all-small-caps" or "petite-caps" or "all-petite-caps" or "unicase" or "titling-caps") caps = t;
                    else if (t.EndsWith("ligatures", StringComparison.Ordinal) || t.EndsWith("contextual", StringComparison.Ordinal)) ligatures.Add(t);
                }
                Add("font-variant-caps", caps);
                Add("font-variant-ligatures", ligatures.Count == 0 ? "normal" : string.Join(" ", ligatures));
                return;
            }
            case "column-rule":
            {
                // Width, style and colour in any order, like a border side.
                string width = "medium", style = "none", color = "currentcolor";
                foreach (var tok in Tokens(value))
                {
                    var lower = tok.ToLowerInvariant();
                    if (IsBorderStyle(lower)) style = lower;
                    else if (lower is "thin" or "medium" or "thick" || char.IsDigit(lower[0]) || lower[0] == '.' || CssMath.IsMathFunction(lower)) width = lower;
                    else color = tok;
                }
                Add("column-rule-width", width);
                Add("column-rule-style", style);
                Add("column-rule-color", color);
                return;
            }
            case "columns":
                foreach (var tok in Tokens(value.ToLowerInvariant()))
                {
                    if (int.TryParse(tok, out _)) Add("column-count", tok);
                    else if (tok != "auto") Add("column-width", tok);
                }
                return;
            default:
                output.Add(new Declaration(name, value, important));
                return;
        }
    }

    private static string IdentOrAuto(string v)
    {
        var t = v.Trim();
        return t.Length > 0 && (char.IsLetter(t[0]) || t[0] is '_' or '-') && !t.Equals("auto", StringComparison.OrdinalIgnoreCase)
               && !t.StartsWith("span", StringComparison.OrdinalIgnoreCase) && t.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? t : "auto";
    }

    /// <summary>grid-template: none, "rows / columns", or area strings each optionally followed by a row size, then "/ columns".</summary>
    private static void ExpandGridTemplate(string value, List<Declaration> output, bool important)
    {
        void Add(string n, string v) => output.Add(new Declaration(n, v, important));
        var v = value.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Add("grid-template-rows", "none");
            Add("grid-template-columns", "none");
            Add("grid-template-areas", "none");
            return;
        }
        var parts = SplitTopLevel(v, '/');
        string rowsPart = parts.Count > 0 ? parts[0].Trim() : "";
        string columns = parts.Count > 1 ? parts[1].Trim() : "none";
        if (rowsPart.IndexOf('"') < 0 && rowsPart.IndexOf('\'') < 0)
        {
            Add("grid-template-rows", rowsPart.Length > 0 ? rowsPart : "none");
            Add("grid-template-columns", columns);
            Add("grid-template-areas", "none");
            return;
        }
        var areas = new StringBuilder();
        var rows = new StringBuilder();
        bool pendingRow = false;
        foreach (var tok in Tokens(rowsPart))
        {
            if (tok[0] is '"' or '\'')
            {
                if (pendingRow) rows.Append(" auto");
                areas.Append(' ').Append(tok);
                pendingRow = true;
            }
            else if (tok[0] == '[') rows.Append(' ').Append(tok);
            else
            {
                rows.Append(' ').Append(tok);
                pendingRow = false;
            }
        }
        if (pendingRow) rows.Append(" auto");
        Add("grid-template-areas", areas.ToString().Trim());
        Add("grid-template-rows", rows.ToString().Trim());
        Add("grid-template-columns", columns);
    }

    private static void ExpandFlex(string value, List<Declaration> output, bool important)
    {
        void Add(string n, string v) => output.Add(new Declaration(n, v, important));
        var lower = value.Trim().ToLowerInvariant();
        switch (lower)
        {
            case "none": Add("flex-grow", "0"); Add("flex-shrink", "0"); Add("flex-basis", "auto"); return;
            case "auto": Add("flex-grow", "1"); Add("flex-shrink", "1"); Add("flex-basis", "auto"); return;
            case "initial": Add("flex-grow", "0"); Add("flex-shrink", "1"); Add("flex-basis", "auto"); return;
        }
        string grow = "1", shrink = "1", basis = "0%";
        int numbers = 0;
        foreach (var tok in Tokens(lower))
        {
            bool isNumber = float.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
            if (isNumber && numbers == 0) { grow = tok; numbers++; }
            else if (isNumber && numbers == 1) { shrink = tok; numbers++; }
            else basis = tok;
        }
        Add("flex-grow", grow);
        Add("flex-shrink", shrink);
        Add("flex-basis", basis);
    }

    private static string[] FourSides(List<string> t)
        => t.Count switch
        {
            1 => new[] { t[0], t[0], t[0], t[0] },
            2 => new[] { t[0], t[1], t[0], t[1] },
            3 => new[] { t[0], t[1], t[2], t[1] },
            _ => new[] { t[0], t[1], t[2], t[3] },
        };

    internal static bool IsBorderStyle(string s)
        => s is "none" or "hidden" or "solid" or "dashed" or "dotted" or "double" or "groove" or "ridge" or "inset" or "outset";

    private static void ExpandFont(string value, List<Declaration> output, bool important)
    {
        void Add(string n, string v) => output.Add(new Declaration(n, v, important));
        var lower = value.Trim().ToLowerInvariant();
        if (lower is "caption" or "icon" or "menu" or "message-box" or "small-caption" or "status-bar") return;

        var tokens = Tokens(value);
        string style = "normal", weight = "normal", size = "medium", lineHeight = "normal", caps = "normal";
        int i = 0;
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i].ToLowerInvariant();
            if (t is "italic" or "oblique") style = t;
            else if (t is "bold" or "bolder" or "lighter" || t.Length == 3 && char.IsDigit(t[0]) && t.EndsWith("00", StringComparison.Ordinal)) weight = t;
            else if (t == "small-caps") caps = t;
            else if (t == "normal") { }
            else break;
        }
        if (i >= tokens.Count) return;
        var sizeTok = tokens[i++];
        int slash = sizeTok.IndexOf('/');
        if (slash >= 0)
        {
            size = sizeTok.Substring(0, slash);
            lineHeight = sizeTok.Substring(slash + 1);
            if (lineHeight.Length == 0 && i < tokens.Count) lineHeight = tokens[i++];
        }
        else
        {
            size = sizeTok;
            if (i < tokens.Count && tokens[i].StartsWith("/", StringComparison.Ordinal))
            {
                lineHeight = tokens[i].Length > 1 ? tokens[i].Substring(1) : (i + 1 < tokens.Count ? tokens[++i] : "normal");
                i++;
            }
        }
        string family = string.Join(" ", tokens.Skip(i));
        Add("font-style", style);
        Add("font-weight", weight);
        Add("font-size", size);
        Add("line-height", lineHeight);
        Add("font-variant-caps", caps);
        if (family.Length > 0) Add("font-family", family);
    }
}

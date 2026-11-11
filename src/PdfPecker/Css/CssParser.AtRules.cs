namespace PdfPecker.Css;

internal static partial class CssParser
{
    /// <summary>
    /// Imported sheets nested deeper than this, and imports beyond the second limit in one document, are skipped, so
    /// a hostile set of sheets that import each other cannot multiply without bound.
    /// </summary>
    private const int MaxImportNesting = 8, MaxImports = 64;

    /// <summary>@property, @counter-style and @font-palette-values rules beyond these are ignored.</summary>
    private const int MaxRegisteredProperties = 1024, MaxCounterStyles = 1024, MaxFontPaletteValues = 1024;

    /// <summary>
    /// For @media, @supports, @layer and @container blocks: whether their rules apply, and the scope they get (the
    /// layer they go into and the container conditions they depend on). A block layer without a name is a new
    /// anonymous layer. Container conditions are kept with the rules, as only the style resolver knows the
    /// containers' sizes.
    /// </summary>
    private static bool TryEnterConditionalBlock(string name, string prelude, in RuleScope scope, out RuleScope inner)
    {
        inner = scope.Inside(scope.Layer, scope.Containers);
        switch (name)
        {
            case "container":
                if (ContainerQuery.Parse(prelude) is not { } query) return false;
                ContainerQuery[] containers = scope.Containers == null ? new[] { query } : [.. scope.Containers, query];
                scope.Sheet.HasContainerRules = true;
                inner = scope.Inside(scope.Layer, containers);
                return true;
            case "media":
                return MediaQuery.Matches(prelude, scope.Sheet.Media);
            case "supports":
                return CssSupports.Evaluate(prelude);
            case "layer":
                var layer = LayerFor(scope.Sheet, scope.Layer, prelude.Length == 0 ? scope.Sheet.AnonymousLayerName() : prelude);
                inner = scope.Inside(layer, scope.Containers);
                return true;
            case "scope":
                if (ParseScope(prelude, scope) is not { } scoped) return false;
                inner = scope.Inside(scope.Layer, scope.Containers) with { Scope = scoped };
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The prelude of an @scope block: an optional "(start)" and an optional "to (end)", each a selector list. Null when
    /// it is not valid, which drops the block as in Chrome.
    /// </summary>
    private static ScopeRule? ParseScope(string prelude, in RuleScope scope)
    {
        var rule = new ScopeRule { Parent = scope.Scope };
        var rest = prelude.AsSpan().Trim();
        if (rest.StartsWith("("))
        {
            int close = MatchingParen(rest);
            if (close < 0 || ParseSelectorList(rest.Slice(1, close - 1)) is not { } start) return null;
            rule.Start = start;
            rest = rest.Slice(close + 1).TrimStart();
        }
        else if (scope.Scope == null) rule.ImplicitRoot = scope.Sheet.CurrentScopeRoot;
        if (rest.Length > 0)
        {
            if (!rest.StartsWith("to", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest.Slice(2).TrimStart();
            if (!rest.StartsWith("(")) return null;
            int close = MatchingParen(rest);
            if (close != rest.Length - 1 || ParseSelectorList(rest.Slice(1, close - 1)) is not { } end) return null;
            rule.End = end;
            rule.EndNamesScope = Selector.NamesScope(end);
        }
        return rule;
    }

    private static int MatchingParen(ReadOnlySpan<char> s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static Selector[]? ParseSelectorList(ReadOnlySpan<char> text)
    {
        var list = new List<Selector>();
        foreach (var part in SplitTopLevel(text.ToString(), ','))
        {
            if (Selector.Parse(part.Trim()) is not { } selector) return null;
            list.Add(selector);
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    /// <summary>
    /// A selector in an @scope block: one that names neither :scope nor &amp; matches below the scope root, as if it
    /// started with ":where(:scope) "; &amp; stands for the root.
    /// </summary>
    private static string Scoped(string selector)
    {
        if (selector.Contains('&')) return selector.Replace("&", ":scope");
        if (selector.Contains(":scope", StringComparison.OrdinalIgnoreCase)) return selector;
        return ":where(:scope) " + selector;
    }

    /// <summary>The layer named <paramref name="name"/> (dotted for sub-layers) inside <paramref name="parent"/>, created on first use.</summary>
    private static CascadeLayer LayerFor(Stylesheet sheet, CascadeLayer? parent, string name)
    {
        var layer = parent ?? sheet.LayerRoot;
        foreach (var part in name.Split('.'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) layer = layer.Child(trimmed);
        }
        return layer;
    }

    /// <summary>
    /// An <c>@import</c> rule: url("a.css") or "a.css", then optionally layer or layer(name), supports(condition) and
    /// a media query list. The imported rules go where the rule is, as if written there, in the layer it names.
    /// </summary>
    private static void Import(string prelude, Stylesheet sheet, CascadeLayer? layer)
    {
        if (sheet.ImportLoader == null || sheet.ImportChain.Count >= MaxImportNesting || sheet.ImportCount >= MaxImports) return;
        if (!TryReadImportTarget(prelude.Trim(), out string target, out bool plainString, out string clauses)) return;
        if (target.Trim().Length == 0) return;
        if (!TryReadImportLayer(ref clauses, sheet, layer, out var into)) return;
        if (!ImportSupported(ref clauses)) return;
        if (clauses.Length > 0 && !MediaQuery.Matches(clauses, sheet.Media)) return;

        // Every attempt counts, not only the imports that load: each one asks the caller's resource resolver, which may
        // fetch it over the network.
        sheet.ImportCount++;
        var loaded = sheet.ImportLoader(target, plainString ? sheet.CurrentHref : null);
        if (loaded is not { } imported || sheet.ImportChain.Contains(imported.Href)) return;
        sheet.ImportChain.Add(imported.Href);
        var saved = sheet.CurrentHref;
        sheet.CurrentHref = imported.Href;
        try
        {
            string text = StripComments(imported.Css);
            ParseRules(text, 0, text.Length, new RuleScope(sheet, into, null, 0));
        }
        finally
        {
            sheet.CurrentHref = saved;
            sheet.ImportChain.RemoveAt(sheet.ImportChain.Count - 1);
        }
    }

    /// <summary>
    /// The sheet an @import names, as url(...) or a quoted string, and the clauses after it. A string is relative to
    /// the importing sheet, which the loader is told.
    /// </summary>
    private static bool TryReadImportTarget(string prelude, out string target, out bool plainString, out string clauses)
    {
        target = "";
        plainString = false;
        clauses = "";
        int close;
        if (prelude.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            close = MediaQuery.MatchingParen(prelude, 3);
            if (close < 0) return false;
            target = Unquote(prelude.Substring(4, close - 4).Trim());
        }
        else if (prelude.Length > 0 && prelude[0] is '"' or '\'')
        {
            close = prelude.IndexOf(prelude[0], 1);
            if (close < 0) return false;
            target = prelude.Substring(1, close - 1);
            plainString = true;
        }
        else return false;
        clauses = prelude.Substring(close + 1).TrimStart();
        return true;
    }

    /// <summary>The optional layer clause: "layer" puts the sheet in a new anonymous layer, "layer(name)" in that layer.</summary>
    private static bool TryReadImportLayer(ref string clauses, Stylesheet sheet, CascadeLayer? layer, out CascadeLayer? into)
    {
        into = layer;
        bool hasClause = clauses.StartsWith("layer", StringComparison.OrdinalIgnoreCase)
            && (clauses.Length == 5 || clauses[5] == '(' || char.IsWhiteSpace(clauses[5]));
        if (!hasClause) return true;
        string name = "";
        if (clauses.Length > 5 && clauses[5] == '(')
        {
            int close = MediaQuery.MatchingParen(clauses, 5);
            if (close < 0) return false;
            name = clauses.Substring(6, close - 6).Trim();
            if (name.Length == 0) return false;
            clauses = clauses.Substring(close + 1).TrimStart();
        }
        else clauses = clauses.Substring(5).TrimStart();
        into = LayerFor(sheet, layer, name.Length == 0 ? sheet.AnonymousLayerName() : name);
        return true;
    }

    /// <summary>The optional supports(condition) clause: false when the condition does not hold, so the sheet is not imported.</summary>
    private static bool ImportSupported(ref string clauses)
    {
        if (!clauses.StartsWith("supports(", StringComparison.OrdinalIgnoreCase)) return true;
        int close = MediaQuery.MatchingParen(clauses, 8);
        // The condition is evaluated with its parentheses, like the prelude of an @supports block.
        if (close < 0 || !CssSupports.Evaluate(clauses.Substring(8, close + 1 - 8))) return false;
        clauses = clauses.Substring(close + 1).TrimStart();
        return true;
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s.Substring(1, s.Length - 2) : s;

    /// <summary>An @font-face rule: its descriptors, kept as one list; a rule without any is dropped.</summary>
    private static void ParseFontFace(string body, Stylesheet sheet)
    {
        var descriptors = new List<Declaration>();
        ParseDeclarations(body, descriptors);
        if (descriptors.Count > 0) sheet.FontFaces.Add(descriptors);
    }

    private static void ParseCounterStyle(string prelude, string body, Stylesheet sheet)
    {
        if (sheet.CounterStyles.Count >= MaxCounterStyles) return;
        if (CounterStyleRule.Parse(prelude, body) is { } rule) sheet.CounterStyles.Add(rule);
    }

    private static void ParseFontPaletteValues(string prelude, string body, Stylesheet sheet)
    {
        if (sheet.FontPaletteValues.Count >= MaxFontPaletteValues) return;
        if (FontPaletteValuesRule.Parse(prelude, body) is { } rule) sheet.FontPaletteValues.Add(rule);
    }

    /// <summary>
    /// Parses an @property rule; one without a valid "inherits" descriptor, or whose syntax or initial value is not
    /// valid, is ignored, as the spec requires.
    /// </summary>
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
        // A syntax other than "*" needs an initial value that matches it.
        if (!CssPropertySyntax.Accepts(syntax, initial)) return;
        if (sheet.RegisteredProperties.Count >= MaxRegisteredProperties) return;
        sheet.RegisteredProperties.RemoveAll(p => p.Name == name); // the last registration wins
        sheet.RegisteredProperties.Add(new RegisteredProperty(name, string.IsNullOrEmpty(initial) ? null : initial, inherits.Value));
    }
}

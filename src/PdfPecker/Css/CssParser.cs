namespace PdfPecker.Css;

/// <summary>
/// Parses CSS stylesheets and declaration blocks. Shorthands are expanded into longhands while parsing. The input is
/// untrusted: nesting, imports and the registered rules are capped, and malformed text is skipped the way browsers
/// skip it, so a bad rule costs only itself and a string or block left open hides the rest of its sheet.
/// </summary>
internal static partial class CssParser
{
    /// <summary>
    /// Nested @media, @supports, @layer and @container blocks (and nested style rules) deeper than this are skipped,
    /// so hostile CSS cannot recurse without bound.
    /// </summary>
    private const int MaxBlockNesting = 16;

    /// <summary>A nested rule gets at most this many selectors from combining its own with its parents'.</summary>
    private const int MaxNestedSelectors = 256;

    /// <summary>
    /// The selectors a nested rule gets may together be at most this many characters. Each "&amp;" repeats the whole
    /// parent selector, so a few of them per level would otherwise grow selectors exponentially with the nesting depth.
    /// </summary>
    private const int MaxNestedSelectorChars = 16 * 1024;

    public static void ParseStylesheet(string css, Stylesheet sheet, string? href = null)
    {
        string text = StripComments(css);
        var saved = sheet.CurrentHref;
        sheet.CurrentHref = href;
        try
        {
            ParseRules(text, 0, text.Length, new RuleScope(sheet, null, null, 0));
        }
        finally
        {
            sheet.CurrentHref = saved;
        }
    }

    /// <summary>
    /// Where the rules being parsed land: the sheet, the layer and the container conditions of the blocks around
    /// them, and how deep inside blocks they are.
    /// </summary>
    private readonly record struct RuleScope(Stylesheet Sheet, CascadeLayer? Layer, ContainerQuery[]? Containers, int Depth)
    {
        public bool CanNest => Depth < MaxBlockNesting;

        /// <summary>The scope of the rules inside a block of this scope.</summary>
        public RuleScope Inside(CascadeLayer? layer, ContainerQuery[]? containers) => new(Sheet, layer, containers, Depth + 1);
    }

    private static void ParseRules(string text, int start, int end, in RuleScope scope)
    {
        var parser = new RuleListParser(new CssReader(text, start, end), scope);
        parser.Parse();
    }

    /// <summary>
    /// Parses one list of rules: a stylesheet, an imported sheet or the inside of a conditional block. A statement
    /// (@import, @layer a, b; @charset) ends at a semicolon; every other rule has a block, matched over the quoted
    /// strings and nested blocks in it.
    /// </summary>
    private ref struct RuleListParser
    {
        private CssReader _reader;
        private readonly RuleScope _scope;
        // @import is only valid at the top of a sheet, before any rule other than @charset and @layer statements.
        private bool _importsAllowed;

        public RuleListParser(CssReader reader, in RuleScope scope)
        {
            _reader = reader;
            _scope = scope;
            _importsAllowed = scope.Depth == 0;
        }

        public void Parse()
        {
            while (true)
            {
                // A stray ';' is not skipped: it starts the prelude of the next rule, whose selector it makes invalid,
                // so that rule is dropped as in browsers (Acid2 counts on it).
                _reader.SkipWhitespace();
                if (_reader.AtEnd) return;
                // A stray closing brace, and the HTML comment markers of old style blocks, are skipped.
                if (_reader.Skip('}') || _reader.Skip("<!--") || _reader.Skip("-->")) continue;
                if (_reader.Skip('@')) ParseAtRule();
                else ParseStyleRule();
            }
        }

        private void ParseAtRule()
        {
            string name = _reader.ReadAtRuleName();
            int brace = _reader.IndexOfOutsideQuotes('{');
            // Only look for the ';' that ends a statement before the block starts: scanning to the end for every
            // at-rule would make a sheet full of blocks quadratic.
            int semicolon = _reader.IndexOfOutsideQuotes(';', _reader.Position, brace < 0 ? _reader.End : brace);
            if (semicolon >= 0)
            {
                ParseStatement(name, _reader.Text(_reader.Position, semicolon));
                _reader.MoveTo(semicolon + 1);
            }
            else if (brace < 0)
            {
                // An @import at the very end of a sheet needs no semicolon.
                if (name == "import" && _importsAllowed) Import(_reader.RemainingText(), _scope.Sheet, _scope.Layer);
                _reader.MoveTo(_reader.End);
            }
            else
            {
                _importsAllowed = false;
                string prelude = _reader.Text(_reader.Position, brace).Trim();
                int close = _reader.MatchingBrace(brace);
                ParseBlockRule(name, prelude, brace, close);
                _reader.MoveTo(close + 1);
            }
        }

        /// <summary>"@layer a, b;" fixes the order of layers and @import loads a sheet; @charset and @namespace are ignored.</summary>
        private void ParseStatement(string name, string prelude)
        {
            switch (name)
            {
                case "layer":
                    foreach (var layerName in SplitTopLevel(prelude, ',')) LayerFor(_scope.Sheet, _scope.Layer, layerName.Trim());
                    break;
                case "import":
                    if (_importsAllowed) Import(prelude, _scope.Sheet, _scope.Layer);
                    break;
                case "charset":
                    break;
                default:
                    _importsAllowed = false;
                    break;
            }
        }

        /// <summary>An at-rule with a block: a conditional block parsed as a rule list in its own scope, or one of the descriptor rules.</summary>
        private void ParseBlockRule(string name, string prelude, int brace, int close)
        {
            if (_scope.CanNest && TryEnterConditionalBlock(name, prelude, _scope, out var inner))
            {
                var parser = new RuleListParser(_reader.Inside(brace, close), inner);
                parser.Parse();
                return;
            }
            switch (name)
            {
                case "page": ParsePage(prelude, _reader.Text(brace + 1, close), _scope.Sheet); break;
                case "font-face": ParseFontFace(_reader.Text(brace + 1, close), _scope.Sheet); break;
                case "property": ParseProperty(prelude, _reader.Text(brace + 1, close), _scope.Sheet); break;
                case "counter-style": ParseCounterStyle(prelude, _reader.Text(brace + 1, close), _scope.Sheet); break;
            }
        }

        private void ParseStyleRule()
        {
            _importsAllowed = false;
            int open = _reader.IndexOfOutsideQuotes('{');
            if (open < 0)
            {
                _reader.MoveTo(_reader.End);
                return;
            }
            int close = _reader.MatchingBrace(open);
            var selectors = SplitTopLevel(_reader.Text(_reader.Position, open), ',');
            for (int k = 0; k < selectors.Count; k++) selectors[k] = selectors[k].Trim();
            _reader.MoveTo(close + 1);
            ParseStyleBlock(_reader.Inside(open, close), selectors, _scope);
        }
    }

    /// <summary>
    /// Parses a style rule's block: its declarations and any nested rules (CSS Nesting). Declarations that follow a
    /// nested rule come after it in the cascade, as in Chrome.
    /// </summary>
    private static void ParseStyleBlock(CssReader block, List<string> selectors, in RuleScope scope)
    {
        if (block.IndexOfOutsideQuotes('{') < 0)
        {
            AddRule(block.RemainingText(), selectors, scope);
            return;
        }
        int declarationsStart = block.Position;
        while (!block.AtEnd)
        {
            int brace = block.IndexOfOutsideQuotes('{');
            if (brace < 0) break;
            int preludeStart = block.StartOfPrelude(brace, declarationsStart);
            if (preludeStart > declarationsStart) AddRule(block.Text(declarationsStart, preludeStart), selectors, scope);
            int close = block.MatchingBrace(brace);
            string prelude = block.Text(preludeStart, brace).Trim();
            if (scope.CanNest) ParseNestedRule(prelude, block.Inside(brace, close), selectors, scope);
            block.MoveTo(close + 1);
            declarationsStart = block.Position;
        }
        if (declarationsStart < block.End) AddRule(block.Text(declarationsStart, block.End), selectors, scope);
    }

    /// <summary>
    /// A rule nested in a style rule: a conditional block, whose rules keep the parent's selectors, or a selector
    /// relative to the parent's.
    /// </summary>
    private static void ParseNestedRule(string prelude, CssReader block, List<string> selectors, in RuleScope scope)
    {
        if (prelude.StartsWith('@'))
        {
            var reader = new CssReader(prelude, 1, prelude.Length);
            string name = reader.ReadAtRuleName();
            if (TryEnterConditionalBlock(name, reader.RemainingText().Trim(), scope, out var inner))
                ParseStyleBlock(block, selectors, inner);
        }
        else if (prelude.Length > 0)
        {
            ParseStyleBlock(block, NestedSelectors(selectors, prelude), scope.Inside(scope.Layer, scope.Containers));
        }
    }

    /// <summary>Adds one rule per selector for a block of declarations; a selector the parser does not know drops its rule.</summary>
    private static void AddRule(string body, List<string> selectors, in RuleScope scope)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        var array = scope.Sheet.ShareDeclarations ? SharedDeclarations.Get(body) : Parse(body);
        if (array.Length == 0) return;
        foreach (var text in selectors)
        {
            if (Selector.Parse(text) is { } selector)
                scope.Sheet.Rules.Add(new StyleRule(selector, array, scope.Sheet.NextOrder++, scope.Layer, scope.Containers));
        }
    }

    private static Declaration[] Parse(string body)
    {
        var declarations = new List<Declaration>();
        ParseDeclarations(body, declarations);
        return declarations.ToArray();
    }

    /// <summary>
    /// Resolves the selectors of a nested rule against its parent's: "&amp;" stands for the parent, and a selector
    /// without one is relative to it (a descendant, or after a leading combinator). Each parent selector is substituted
    /// in turn, so ".a, .b { &amp;:hover {} }" gives ".a:hover" and ".b:hover".
    /// </summary>
    private static List<string> NestedSelectors(List<string> parents, string nested)
    {
        var result = new List<string>();
        long chars = 0;
        foreach (var raw in SplitTopLevel(nested, ','))
        {
            var child = raw.Trim();
            if (child.Length == 0) continue;
            int amps = child.AsSpan().Count('&');
            foreach (var parent in parents)
            {
                if (result.Count >= MaxNestedSelectors) return result;
                long length = child.Length + (long)Math.Max(amps, 1) * parent.Length;
                if (chars + length > MaxNestedSelectorChars) return result;
                chars += length;
                result.Add(amps > 0 ? child.Replace("&", parent) : parent + " " + child);
            }
        }
        return result;
    }
}

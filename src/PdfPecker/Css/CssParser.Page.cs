using System.Text;

namespace PdfPecker.Css;

internal static partial class CssParser
{
    /// <summary>Parses an @page rule: "@page", "@page :first", "@page wide" and so on, with declarations and margin boxes.</summary>
    private static void ParsePage(string prelude, string body, Stylesheet sheet)
    {
        if (!TryParsePageSelectors(prelude, out var selectors)) return;
        var rule = new PageRule { Name = selectors[0].Name, Selector = selectors[0].Pseudo };
        ParsePageBody(body, rule);
        // One rule per selector; the others are copies that share the first one's margin boxes and footnote area.
        foreach (var (name, pseudo) in selectors)
        {
            var r = name == rule.Name && pseudo == rule.Selector ? rule : CopyPageRule(rule, name, pseudo);
            sheet.PageRules.Add(r);
            if (name.Length == 0 && pseudo.Length == 0) sheet.PageDeclarations.AddRange(r.Declarations);
        }
    }

    /// <summary>
    /// The page selectors: an optional page name (named pages) and an optional :first, :left, :right or :blank,
    /// several separated by commas. A selector the parser does not know makes the whole rule invalid.
    /// </summary>
    private static bool TryParsePageSelectors(string prelude, out List<(string Name, string Pseudo)> selectors)
    {
        selectors = new List<(string, string)>();
        foreach (var part in prelude.Split(','))
        {
            var selector = part.Trim();
            int colon = selector.IndexOf(':');
            var name = colon < 0 ? selector : selector.Substring(0, colon).TrimEnd();
            var pseudo = colon < 0 ? "" : selector.Substring(colon).ToLowerInvariant();
            if (pseudo.Length > 0 && pseudo is not (":first" or ":left" or ":right" or ":blank")) return false;
            if (name.Length > 0 && !IsPageName(name)) return false;
            selectors.Add((name, pseudo));
        }
        return true;
    }

    /// <summary>
    /// The body of an @page rule: declarations, with the margin boxes (@top-center { ... }) and the @footnote area
    /// cut out and parsed on their own. The text around the boxes is parsed as one declaration block.
    /// </summary>
    private static void ParsePageBody(string body, PageRule rule)
    {
        var reader = new CssReader(body, 0, body.Length);
        var declarations = new StringBuilder();
        int declarationsStart = 0;
        while (!reader.AtEnd)
        {
            int at = reader.IndexOfOutsideQuotes('@');
            if (at < 0) break;
            int brace = reader.IndexOfOutsideQuotes('{', at, reader.End);
            if (brace < 0) break;
            declarations.Append(body, declarationsStart, at - declarationsStart).Append(';');
            int close = reader.MatchingBrace(brace);
            var name = reader.Text(at + 1, brace).Trim().ToLowerInvariant();
            if (PageMarginBoxNames.Contains(name)) ParseDeclarations(reader.Text(brace + 1, close), MarginBox(rule, name));
            else if (name == "footnote") ParseDeclarations(reader.Text(brace + 1, close), rule.Footnote ??= new List<Declaration>());
            reader.MoveTo(close + 1);
            declarationsStart = reader.Position;
        }
        declarations.Append(body, declarationsStart, body.Length - declarationsStart);
        ParseDeclarations(declarations.ToString(), rule.Declarations);
    }

    /// <summary>The declarations of a margin box of the rule, created on first use; a box given twice adds to the first.</summary>
    private static List<Declaration> MarginBox(PageRule rule, string name)
    {
        if (!rule.MarginBoxes.TryGetValue(name, out var declarations))
        {
            declarations = new List<Declaration>();
            rule.MarginBoxes[name] = declarations;
        }
        return declarations;
    }

    private static PageRule CopyPageRule(PageRule rule, string name, string pseudo)
    {
        var copy = new PageRule { Name = name, Selector = pseudo };
        copy.Declarations.AddRange(rule.Declarations);
        foreach (var (box, declarations) in rule.MarginBoxes) copy.MarginBoxes[box] = declarations;
        copy.Footnote = rule.Footnote;
        return copy;
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
}

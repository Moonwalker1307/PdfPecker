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

    public StyleRule(Selector selector, Declaration[] declarations, int order)
    {
        Selector = selector;
        Declarations = declarations;
        Order = order;
    }
}

internal sealed class Stylesheet
{
    public readonly List<StyleRule> Rules = new();
    public readonly List<Declaration> PageDeclarations = new(); // from @page
    public readonly List<List<Declaration>> FontFaces = new(); // @font-face descriptors, one list per rule
    public int NextOrder;
}

/// <summary>Parses CSS stylesheets and declaration blocks. Shorthands are expanded into longhands while parsing.</summary>
internal static class CssParser
{
    public static void ParseStylesheet(string css, Stylesheet sheet)
    {
        string s = StripComments(css);
        int i = 0;
        ParseRules(s, ref i, sheet, s.Length, 0);
    }

    // Nested @media blocks deeper than this are skipped, so hostile CSS cannot recurse without bound.
    private const int MaxMediaNesting = 16;

    private static void ParseRules(string s, ref int i, Stylesheet sheet, int end, int depth)
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
                    i = semi + 1; // @import, @charset, @namespace: ignored
                    continue;
                }
                if (brace < 0) { i = end; break; }
                string prelude = s.Substring(i, brace - i).Trim();
                int blockEnd = MatchingBrace(s, brace, end);
                if (atName == "media" && depth < MaxMediaNesting && MediaApplies(prelude))
                {
                    int inner = brace + 1;
                    ParseRules(s, ref inner, sheet, blockEnd, depth + 1);
                }
                else if (atName == "page" && !prelude.Contains(':'))
                {
                    // @page { size; margin } - pseudo pages (:first, :left) are ignored.
                    ParseDeclarations(s.Substring(brace + 1, blockEnd - brace - 1), sheet.PageDeclarations);
                }
                else if (atName == "font-face")
                {
                    var descriptors = new List<Declaration>();
                    ParseDeclarations(s.Substring(brace + 1, blockEnd - brace - 1), descriptors);
                    if (descriptors.Count > 0) sheet.FontFaces.Add(descriptors);
                }
                i = Math.Min(blockEnd + 1, end);
                continue;
            }

            int open = IndexOfOutsideQuotes(s, '{', i, end);
            if (open < 0) { i = end; break; }
            int close = MatchingBrace(s, open, end);
            string selectorText = s.Substring(i, open - i);
            string body = s.Substring(open + 1, close - open - 1);
            i = Math.Min(close + 1, end);

            var decls = new List<Declaration>();
            ParseDeclarations(body, decls);
            if (decls.Count == 0) continue;
            var declArray = decls.ToArray();
            foreach (var selText in SplitTopLevel(selectorText, ','))
            {
                var sel = Selector.Parse(selText);
                if (sel != null) sheet.Rules.Add(new StyleRule(sel, declArray, sheet.NextOrder++));
            }
        }
    }

    private static bool MediaApplies(string prelude)
    {
        // Print output: honour "print" and "all" (and queries with no media type), skip screen/speech-only blocks.
        var p = prelude.ToLowerInvariant();
        foreach (var query in p.Split(','))
        {
            var q = query.Trim();
            if (q.StartsWith("not ", StringComparison.Ordinal)) continue;
            if (q.StartsWith("only ", StringComparison.Ordinal)) q = q.Substring(5);
            if (q.StartsWith("print", StringComparison.Ordinal) || q.StartsWith("all", StringComparison.Ordinal) || q.StartsWith("(", StringComparison.Ordinal))
            {
                // Width-based queries are evaluated loosely: assume a page is a "wide" medium.
                if (q.Contains("max-width")) continue;
                return true;
            }
        }
        return false;
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
            case "border-width":
            case "border-style":
            case "border-color":
            {
                var t = Tokens(value);
                if (t.Count == 0 || t.Count > 4) return;
                var four = FourSides(t);
                string suffix = name.Substring(6); // "width" / "style" / "color"
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
                    else if (lower is "thin" or "medium" or "thick" || char.IsDigit(lower[0]) || lower[0] == '.') width = lower;
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
            case "font":
                ExpandFont(value, output, important);
                return;
            case "list-style":
                foreach (var tok in Tokens(value))
                {
                    var lower = tok.ToLowerInvariant();
                    if (lower is "inside" or "outside") Add("list-style-position", lower);
                    else if (!lower.StartsWith("url(", StringComparison.Ordinal)) Add("list-style-type", lower);
                }
                return;
            case "page-break-before":
            case "page-break-after":
            case "page-break-inside":
                Add(name.Substring(5), value.ToLowerInvariant());
                return;
            case "text-decoration":
                Add("text-decoration-line", value);
                return;
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
        string style = "normal", weight = "normal", size = "medium", lineHeight = "normal";
        int i = 0;
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i].ToLowerInvariant();
            if (t is "italic" or "oblique") style = t;
            else if (t is "bold" or "bolder" or "lighter" || t.Length == 3 && char.IsDigit(t[0]) && t.EndsWith("00", StringComparison.Ordinal)) weight = t;
            else if (t is "normal" or "small-caps") { }
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
        if (family.Length > 0) Add("font-family", family);
    }
}

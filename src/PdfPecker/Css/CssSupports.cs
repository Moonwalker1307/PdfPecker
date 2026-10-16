namespace PdfPecker.Css;

/// <summary>Evaluates <c>@supports</c> conditions against the properties and values this library understands.</summary>
internal static class CssSupports
{
    private const int MaxDepth = 16;

    /// <summary>Properties the style resolver applies, longhands and shorthands.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "display", "color", "background", "background-color", "background-image", "background-repeat", "background-size",
        "background-position", "background-position-x", "background-position-y", "background-origin", "background-clip",
        "font", "font-family", "font-size", "font-weight", "font-style", "font-variant", "font-variant-ligatures",
        "font-variant-caps", "font-feature-settings", "line-height", "text-align", "text-align-last",
        "text-decoration", "text-decoration-line", "text-decoration-color", "text-decoration-style", "text-decoration-thickness",
        "text-underline-offset", "text-transform", "text-indent", "letter-spacing", "word-spacing", "white-space",
        "overflow-wrap", "word-wrap", "word-break", "vertical-align", "list-style", "list-style-type", "list-style-position",
        "list-style-image",
        "visibility", "box-shadow", "table-layout", "border-collapse", "border-spacing", "box-sizing", "float", "clear",
        "position", "inset", "top", "right", "bottom", "left", "z-index", "overflow", "overflow-x", "overflow-y", "transform",
        "transform-origin", "translate", "rotate", "scale", "content", "counter-reset", "counter-set", "counter-increment",
        "break-before", "break-after", "break-inside", "page-break-before", "page-break-after", "page-break-inside",
        "orphans", "widows", "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "padding", "padding-top",
        "padding-right", "padding-bottom", "padding-left", "width", "height", "min-width", "min-height", "max-width",
        "max-height", "opacity", "outline", "outline-style", "outline-width", "outline-color", "outline-offset", "text-shadow",
        "text-overflow", "object-fit", "object-position", "aspect-ratio", "flex", "flex-flow", "flex-direction", "flex-wrap",
        "flex-grow", "flex-shrink", "flex-basis", "order", "justify-content", "align-items", "align-content", "justify-items",
        "align-self", "justify-self", "place-content", "place-items", "place-self", "gap", "row-gap", "column-gap", "grid-gap",
        "grid-row-gap", "grid-column-gap", "grid", "grid-template", "grid-template-columns", "grid-template-rows",
        "grid-template-areas", "grid-auto-columns", "grid-auto-rows", "grid-auto-flow", "grid-area", "grid-row",
        "grid-column", "grid-column-start", "grid-column-end", "grid-row-start", "grid-row-end", "columns", "column-count",
        "column-width", "column-rule", "column-rule-width", "column-rule-style", "column-rule-color", "column-span",
        "fill", "stroke", "stroke-width", "fill-opacity", "stroke-opacity", "fill-rule", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "clip-path", "clip-rule", "mask", "mask-type", "filter", "marker", "marker-start", "marker-mid",
        "marker-end", "flood-color", "flood-opacity", "color-interpolation-filters", "text-anchor", "dominant-baseline",
        "alignment-baseline", "border", "border-top", "border-right", "border-bottom", "border-left", "border-width",
        "border-style", "border-color", "border-radius", "border-top-width", "border-right-width", "border-bottom-width",
        "border-left-width", "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color", "border-top-left-radius",
        "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius", "tab-size",
        "line-clamp", "-webkit-line-clamp", "hyphens", "hyphenate-character", "size", "string-set", "page", "direction", "unicode-bidi", "margin-inline",
        "margin-inline-start", "margin-inline-end", "padding-inline", "padding-inline-start", "padding-inline-end", "margin-block",
        "margin-block-start", "margin-block-end", "padding-block", "padding-block-start", "padding-block-end",
    };

    /// <summary>Whether this library applies the property.</summary>
    public static bool IsKnownProperty(string name) => name.StartsWith("--", StringComparison.Ordinal) || Known.Contains(name);

    /// <summary>Evaluates the prelude of an @supports rule; a condition that does not parse is false.</summary>
    public static bool Evaluate(string condition) => Condition(condition.Trim(), 0) == true;

    private static bool? Condition(string s, int depth)
    {
        s = s.Trim();
        if (s.Length == 0 || depth > MaxDepth) return null;
        if (s.StartsWith("not", StringComparison.OrdinalIgnoreCase) && s.Length > 3 && (s[3] == ' ' || s[3] == '('))
            return !Condition(s.Substring(3), depth + 1);
        bool? result = null;
        string? op = null;
        int i = 0;
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return null;
            bool value;
            if (s[i] == '(')
            {
                int close = MediaQuery.MatchingParen(s, i);
                if (close < 0) return null;
                value = InParens(s.Substring(i + 1, close - i - 1).Trim(), depth + 1);
                i = close + 1;
            }
            else
            {
                // A function: selector(), font-tech(), font-format().
                int open = s.IndexOf('(', i);
                if (open < 0) return null;
                int close = MediaQuery.MatchingParen(s, open);
                if (close < 0) return null;
                var name = s.Substring(i, open - i).Trim().ToLowerInvariant();
                var arg = s.Substring(open + 1, close - open - 1).Trim();
                value = name switch
                {
                    "selector" => Selector.Parse(arg) != null,
                    "font-format" => arg.ToLowerInvariant() is "truetype" or "opentype" or "woff" or "woff2" or "collection",
                    "font-tech" => arg.ToLowerInvariant() is "features-opentype",
                    "at-rule" => arg.ToLowerInvariant() is "@media" or "@supports" or "@layer" or "@import" or "@page" or "@font-face"
                        or "@property" or "@counter-style" or "@charset" or "@namespace",
                    _ => false,
                };
                i = close + 1;
            }
            result = result == null ? value : op == "or" ? result.Value || value : result.Value && value;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) return result;
            int start = i;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            var word = s.Substring(start, i - start).ToLowerInvariant();
            if (word is not ("and" or "or") || op != null && op != word) return null;
            op = word;
        }
    }

    private static bool InParens(string inner, int depth)
    {
        if (inner.Length == 0) return false;
        int colon = inner.IndexOf(':');
        int paren = inner.IndexOf('(');
        if (colon < 0 || paren >= 0 && paren < colon) return Condition(inner, depth) == true;
        var name = inner.Substring(0, colon).Trim();
        if (!name.StartsWith("--", StringComparison.Ordinal)) name = name.ToLowerInvariant();
        var value = inner.Substring(colon + 1).Trim();
        if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 10).TrimEnd();
        if (!IsKnownProperty(name) || value.Length == 0 && !name.StartsWith("--", StringComparison.Ordinal)) return false;
        return ValueSupported(name, value);
    }

    /// <summary>A light check of the value: colours must parse, display must be a known keyword.</summary>
    private static bool ValueSupported(string name, string value)
    {
        if (name.StartsWith("--", StringComparison.Ordinal) || CssParser.HasVar(value)) return true;
        var lower = value.ToLowerInvariant();
        if (lower is "inherit" or "initial" or "unset" or "revert" or "revert-layer") return true;
        switch (name)
        {
            case "color":
            case "background-color":
            case "border-color":
            case "outline-color":
            case "text-decoration-color":
            case "column-rule-color":
            case "flood-color":
                return CssColor.TryParse(value, CssColor.Black, out _);
            case "display":
                return lower is "block" or "inline" or "inline-block" or "flex" or "inline-flex" or "grid" or "inline-grid"
                    or "flow-root" or "contents" or "list-item" or "none" or "table" or "inline-table" or "table-row"
                    or "table-cell" or "table-row-group" or "table-header-group" or "table-footer-group" or "table-caption"
                    or "table-column" or "table-column-group" or "block flex" or "block grid" or "inline flex" or "inline grid";
            case "position":
                return lower is "static" or "relative" or "absolute" or "fixed" or "sticky";
        }
        // Other values: functions this library does not know make the value unsupported.
        foreach (var fn in new[] { "attr(", "anchor(", "env(", "image-set(", "element(", "paint(", "cross-fade(" })
            if (lower.Contains(fn, StringComparison.Ordinal)) return false;
        return true;
    }
}

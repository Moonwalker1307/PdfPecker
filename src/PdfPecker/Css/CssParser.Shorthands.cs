using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

internal static partial class CssParser
{
    private static readonly string[] Sides = { "top", "right", "bottom", "left" };
    private static readonly string[] Corners = { "top-left", "top-right", "bottom-right", "bottom-left" };

    /// <summary>Writes the longhands of one shorthand, all with the shorthand's importance.</summary>
    private readonly struct LonghandWriter
    {
        private readonly List<Declaration> _output;
        private readonly bool _important;

        public LonghandWriter(List<Declaration> output, bool important)
        {
            _output = output;
            _important = important;
        }

        public void Add(string property, string value) => _output.Add(new Declaration(property, value, _important));
    }

    /// <summary>
    /// The four sides of a box shorthand from its one to four values, in the top, right, bottom, left order of
    /// <see cref="Sides"/>: a missing bottom repeats the top, a missing left the right.
    /// </summary>
    private readonly struct SideValues
    {
        private readonly string _top, _right, _bottom, _left;

        public SideValues(List<string> values)
        {
            _top = values[0];
            _right = values.Count > 1 ? values[1] : values[0];
            _bottom = values.Count > 2 ? values[2] : values[0];
            _left = values.Count > 3 ? values[3] : _right;
        }

        public string this[int side] => side switch { 0 => _top, 1 => _right, 2 => _bottom, _ => _left };
    }

    /// <summary>
    /// The width, style and colour of a border-like shorthand (border, border-top, outline, column-rule): the parts in
    /// any order, each with its initial value when left out. An outline also takes the style auto, and its width
    /// cannot be a math function.
    /// </summary>
    private readonly record struct LineParts(string Width, string Style, string Color)
    {
        public static LineParts Parse(string value, bool outline)
        {
            string width = "medium", style = "none", color = "currentcolor";
            foreach (var token in Tokens(value))
            {
                var lower = token.ToLowerInvariant();
                if (IsBorderStyle(lower) || outline && lower == "auto") style = lower;
                else if (IsLineWidth(lower) || !outline && CssMath.IsMathFunction(lower)) width = lower;
                else color = token;
            }
            return new LineParts(width, style, color);
        }
    }

    /// <summary>A line width keyword or a number (a length); a math function is checked by the caller.</summary>
    private static bool IsLineWidth(string lower) => lower is "thin" or "medium" or "thick" || char.IsDigit(lower[0]) || lower[0] == '.';

    /// <summary>Expands a declaration into its longhands, or keeps it as it is when it is not a shorthand.</summary>
    internal static void Expand(string name, string value, bool important, List<Declaration> output)
    {
        var longhands = new LonghandWriter(output, important);

        // A custom property is kept as written, and so is a shorthand whose value uses var(): the style resolver
        // substitutes and expands it per element.
        if (name.StartsWith("--", StringComparison.Ordinal) || HasVar(value))
        {
            longhands.Add(name, value);
            return;
        }

        // A CSS-wide keyword (border: inherit) gives every longhand of the shorthand that keyword. The shorthand's own
        // expansion of the word says which longhands those are, whatever it made of the word itself.
        int first = output.Count;
        bool wide = IsCssWideKeyword(value);

        switch (name)
        {
            case "container": ExpandContainer(value, longhands); break;
            case "inset": ExpandFourSides(value, "", "", longhands); break;
            case "margin":
            case "padding":
                ExpandFourSides(value, name + "-", "", longhands);
                break;
            case "margin-inline":
            case "padding-inline":
            case "margin-block":
            case "padding-block":
                ExpandStartEnd(name, value, longhands);
                break;
            case "border-radius": ExpandBorderRadius(value, longhands); break;
            case "border-width":
            case "border-style":
            case "border-color":
                ExpandFourSides(value, "border-", name.Substring("border".Length), longhands);
                break;
            case "border": ExpandBorder(value, longhands); break;
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
                ExpandBorderSide(name, value, longhands);
                break;
            case "background": CssBackground.ExpandShorthand(value, longhands.Add); break;
            case "border-image": CssBorderImage.ExpandShorthand(value, longhands.Add); break;
            case "outline": ExpandOutline(value, longhands); break;
            case "font": ExpandFont(value, longhands); break;
            case "list-style": ExpandListStyle(value, longhands); break;
            case "page-break-before":
            case "page-break-after":
            case "page-break-inside":
                longhands.Add(name.Substring("page-".Length), value.ToLowerInvariant());
                break;
            case "text-decoration": ExpandTextDecoration(value, longhands); break;
            case "border-spacing": ExpandPair(value, "border-spacing-h", "border-spacing-v", longhands); break;
            case "word-wrap": longhands.Add("overflow-wrap", value); break;
            case "-webkit-background-clip": longhands.Add("background-clip", value); break;
            case "-webkit-box-decoration-break": longhands.Add("box-decoration-break", value); break;
            case "flex": ExpandFlex(value, longhands); break;
            case "flex-flow": ExpandFlexFlow(value, longhands); break;
            case "gap":
            case "grid-gap":
                ExpandPair(value, "row-gap", "column-gap", longhands);
                break;
            case "grid-row-gap": longhands.Add("row-gap", value); break;
            case "grid-column-gap": longhands.Add("column-gap", value); break;
            case "grid-column":
            case "grid-row":
                ExpandGridLine(name, value, longhands);
                break;
            case "grid-area": ExpandGridArea(value, longhands); break;
            case "grid-template": ExpandGridTemplate(value, longhands); break;
            case "place-items":
            case "place-content":
            case "place-self":
                ExpandPlace(name, value, longhands);
                break;
            case "font-variant": ExpandFontVariant(value, longhands); break;
            case "column-rule": ExpandColumnRule(value, longhands); break;
            case "columns": ExpandColumns(value, longhands); break;
            default: longhands.Add(name, value); break;
        }

        if (wide)
        {
            var keyword = value.Trim();
            for (int i = first; i < output.Count; i++) output[i] = new Declaration(output[i].Property, keyword, important);
        }
    }

    /// <summary>Whether a value is inherit, initial, unset, revert or revert-layer, which every property takes.</summary>
    internal static bool IsCssWideKeyword(string value)
        => value.AsSpan().Trim() is var v
           && (v.Equals("inherit", StringComparison.OrdinalIgnoreCase) || v.Equals("initial", StringComparison.OrdinalIgnoreCase)
               || v.Equals("unset", StringComparison.OrdinalIgnoreCase) || v.Equals("revert", StringComparison.OrdinalIgnoreCase)
               || v.Equals("revert-layer", StringComparison.OrdinalIgnoreCase));

    /// <summary>container: &lt;name&gt; [ / &lt;type&gt; ]; the type is normal when left out.</summary>
    private static void ExpandContainer(string value, LonghandWriter longhands)
    {
        var v = value.Trim();
        if (v.ToLowerInvariant() is "inherit" or "initial" or "unset" or "revert" or "revert-layer")
        {
            longhands.Add("container-name", v);
            longhands.Add("container-type", v);
            return;
        }
        int slash = v.IndexOf('/');
        var names = (slash < 0 ? v : v.Substring(0, slash)).Trim();
        var type = slash < 0 ? "normal" : v.Substring(slash + 1).Trim();
        // An invalid part makes the whole declaration invalid.
        if (type.ToLowerInvariant() is not ("normal" or "size" or "inline-size") || !ContainerNamesValid(names)) return;
        longhands.Add("container-name", names);
        longhands.Add("container-type", type);
    }

    /// <summary>One to four values for the four sides, as longhands named prefix + side + suffix ("margin-top", "border-top-width", "top").</summary>
    private static void ExpandFourSides(string value, string prefix, string suffix, LonghandWriter longhands)
    {
        var tokens = Tokens(value);
        if (tokens.Count == 0 || tokens.Count > 4) return;
        var sides = new SideValues(tokens);
        for (int k = 0; k < 4; k++) longhands.Add(prefix + Sides[k] + suffix, sides[k]);
    }

    /// <summary>A logical pair (margin-inline and the like): one value for both sides, or start and end.</summary>
    private static void ExpandStartEnd(string name, string value, LonghandWriter longhands)
    {
        var tokens = Tokens(value);
        if (tokens.Count is 0 or > 2) return;
        longhands.Add(name + "-start", tokens[0]);
        longhands.Add(name + "-end", tokens[^1]);
    }

    /// <summary>Horizontal radii, then optionally "/" and vertical radii, each one to four values like margin.</summary>
    private static void ExpandBorderRadius(string value, LonghandWriter longhands)
    {
        int slash = value.IndexOf('/');
        var horizontal = Tokens(slash < 0 ? value : value.Substring(0, slash));
        var vertical = slash < 0 ? horizontal : Tokens(value.Substring(slash + 1));
        if (horizontal.Count is 0 or > 4 || vertical.Count is 0 or > 4) return;
        var h = new SideValues(horizontal);
        var v = new SideValues(vertical);
        for (int k = 0; k < 4; k++) longhands.Add("border-" + Corners[k] + "-radius", h[k] == v[k] ? h[k] : h[k] + " " + v[k]);
    }

    private static void ExpandBorder(string value, LonghandWriter longhands)
    {
        var parts = LineParts.Parse(value, outline: false);
        foreach (var side in Sides)
        {
            longhands.Add("border-" + side + "-width", parts.Width);
            longhands.Add("border-" + side + "-style", parts.Style);
            longhands.Add("border-" + side + "-color", parts.Color);
        }
        // The border shorthand also resets border-image, which it cannot set.
        foreach (var longhand in CssBorderImage.Longhands) longhands.Add(longhand, "initial");
    }

    private static void ExpandBorderSide(string name, string value, LonghandWriter longhands)
    {
        var parts = LineParts.Parse(value, outline: false);
        longhands.Add(name + "-width", parts.Width);
        longhands.Add(name + "-style", parts.Style);
        longhands.Add(name + "-color", parts.Color);
    }

    private static void ExpandOutline(string value, LonghandWriter longhands)
    {
        var parts = LineParts.Parse(value, outline: true);
        longhands.Add("outline-width", parts.Width);
        longhands.Add("outline-style", parts.Style);
        longhands.Add("outline-color", parts.Color);
    }

    /// <summary>Width, style and colour in any order, like a border side.</summary>
    private static void ExpandColumnRule(string value, LonghandWriter longhands)
    {
        var parts = LineParts.Parse(value, outline: false);
        longhands.Add("column-rule-width", parts.Width);
        longhands.Add("column-rule-style", parts.Style);
        longhands.Add("column-rule-color", parts.Color);
    }

    /// <summary>The shorthand sets all three longhands; "none" is the type or the image, whichever is not given.</summary>
    private static void ExpandListStyle(string value, LonghandWriter longhands)
    {
        string? position = null, image = null, type = null;
        int nones = 0;
        foreach (var token in Tokens(value))
        {
            var lower = token.ToLowerInvariant();
            if (lower is "inside" or "outside")
            {
                if (position != null) return;
                position = lower;
            }
            else if (lower == "none") nones++;
            else if (lower.StartsWith("url(", StringComparison.Ordinal) || lower.Contains("gradient(", StringComparison.Ordinal)
                     || lower.StartsWith("image-set(", StringComparison.Ordinal))
            {
                if (image != null) return;
                image = token;
            }
            else
            {
                if (type != null) return;
                type = token;
            }
        }
        if (nones > 2 || nones == 2 && (type != null || image != null) || nones == 1 && type != null && image != null) return;
        if (nones > 0)
        {
            type ??= "none";
            image ??= "none";
        }
        longhands.Add("list-style-position", position ?? "outside");
        longhands.Add("list-style-image", image ?? "none");
        longhands.Add("list-style-type", type ?? "disc");
    }

    /// <summary>Line keywords, a style, a thickness and a colour, in any order; missing parts reset.</summary>
    private static void ExpandTextDecoration(string value, LonghandWriter longhands)
    {
        var lines = new List<string>();
        string style = "solid", thickness = "auto", color = "currentcolor";
        foreach (var token in Tokens(value))
        {
            var lower = token.ToLowerInvariant();
            if (lower is "none" or "underline" or "overline" or "line-through" or "blink")
            {
                if (lower != "none") lines.Add(lower);
            }
            else if (lower is "solid" or "double" or "dotted" or "dashed" or "wavy") style = lower;
            else if (lower is "auto" or "from-font" || char.IsDigit(lower[0]) || lower[0] == '.' || CssMath.IsMathFunction(lower)) thickness = lower;
            else color = token;
        }
        longhands.Add("text-decoration-line", lines.Count == 0 ? "none" : string.Join(" ", lines));
        longhands.Add("text-decoration-style", style);
        longhands.Add("text-decoration-thickness", thickness);
        longhands.Add("text-decoration-color", color);
    }

    /// <summary>A pair of longhands (gap, border-spacing): the second repeats the first when there is only one value.</summary>
    private static void ExpandPair(string value, string first, string second, LonghandWriter longhands)
    {
        var tokens = Tokens(value);
        if (tokens.Count == 0) return;
        longhands.Add(first, tokens[0]);
        longhands.Add(second, tokens.Count > 1 ? tokens[1] : tokens[0]);
    }

    private static void ExpandFlexFlow(string value, LonghandWriter longhands)
    {
        foreach (var token in Tokens(value.ToLowerInvariant()))
        {
            if (token is "row" or "row-reverse" or "column" or "column-reverse") longhands.Add("flex-direction", token);
            else longhands.Add("flex-wrap", token);
        }
    }

    private static void ExpandColumns(string value, LonghandWriter longhands)
    {
        foreach (var token in Tokens(value.ToLowerInvariant()))
        {
            if (Invariant.TryParseInt(token, out _)) longhands.Add("column-count", token);
            else if (token != "auto") longhands.Add("column-width", token);
        }
    }

    /// <summary>grid-row and grid-column: "start / end"; a missing end copies a named start line, or is auto.</summary>
    private static void ExpandGridLine(string name, string value, LonghandWriter longhands)
    {
        var parts = SplitTopLevel(value, '/');
        string start = parts.Count > 0 ? parts[0].Trim() : "auto";
        string end = parts.Count > 1 ? parts[1].Trim() : IdentOrAuto(start);
        longhands.Add(name + "-start", start);
        longhands.Add(name + "-end", end);
    }

    /// <summary>grid-area: row-start / column-start / row-end / column-end; omitted values copy the matching start value when it is a name, otherwise they are auto.</summary>
    private static void ExpandGridArea(string value, LonghandWriter longhands)
    {
        var parts = SplitTopLevel(value, '/');
        string rowStart = PartOrAuto(parts, 0);
        string columnStart = parts.Count > 1 ? PartOrAuto(parts, 1) : IdentOrAuto(rowStart);
        string rowEnd = parts.Count > 2 ? PartOrAuto(parts, 2) : IdentOrAuto(rowStart);
        string columnEnd = parts.Count > 3 ? PartOrAuto(parts, 3) : IdentOrAuto(columnStart);
        longhands.Add("grid-row-start", rowStart);
        longhands.Add("grid-column-start", columnStart);
        longhands.Add("grid-row-end", rowEnd);
        longhands.Add("grid-column-end", columnEnd);
    }

    private static string PartOrAuto(List<string> parts, int index) => parts.Count > index ? parts[index].Trim() : "auto";

    /// <summary>The value when it is a line name (an identifier other than auto or span ...), otherwise auto.</summary>
    private static string IdentOrAuto(string value)
    {
        var t = value.Trim();
        if (t.Length == 0 || !(char.IsLetter(t[0]) || t[0] is '_' or '-')) return "auto";
        if (t.Equals("auto", StringComparison.OrdinalIgnoreCase) || t.StartsWith("span", StringComparison.OrdinalIgnoreCase)) return "auto";
        foreach (char c in t)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_')) return "auto";
        return t;
    }

    /// <summary>place-items, place-content and place-self: the align value, then the justify value or the same again.</summary>
    private static void ExpandPlace(string name, string value, LonghandWriter longhands)
    {
        var tokens = Tokens(value);
        if (tokens.Count == 0) return;
        string suffix = name.Substring("place-".Length);
        longhands.Add("align-" + suffix, tokens[0]);
        longhands.Add("justify-" + suffix, tokens.Count > 1 ? tokens[tokens.Count - 1] : tokens[0]);
    }

    /// <summary>The caps, ligature, numeric, east-asian and position parts of font-variant; alternates are not supported.</summary>
    private static void ExpandFontVariant(string value, LonghandWriter longhands)
    {
        var lower = value.Trim().ToLowerInvariant();
        if (lower is "normal" or "none" or "inherit" or "initial" or "unset")
        {
            longhands.Add("font-variant-caps", lower == "none" ? "normal" : lower);
            longhands.Add("font-variant-ligatures", lower);
            longhands.Add("font-variant-numeric", lower == "none" ? "normal" : lower);
            longhands.Add("font-variant-east-asian", lower == "none" ? "normal" : lower);
            longhands.Add("font-variant-position", lower == "none" ? "normal" : lower);
            return;
        }
        string caps = "normal", position = "normal";
        var ligatures = new List<string>();
        var numeric = new List<string>();
        var eastAsian = new List<string>();
        foreach (var token in Tokens(lower))
        {
            if (token is "small-caps" or "all-small-caps" or "petite-caps" or "all-petite-caps" or "unicase" or "titling-caps") caps = token;
            else if (token.EndsWith("ligatures", StringComparison.Ordinal) || token.EndsWith("contextual", StringComparison.Ordinal)) ligatures.Add(token);
            else if (token.EndsWith("-nums", StringComparison.Ordinal) || token.EndsWith("-fractions", StringComparison.Ordinal) || token is "ordinal" or "slashed-zero") numeric.Add(token);
            else if (token is "sub" or "super") position = token;
            else if (token is "jis78" or "jis83" or "jis90" or "jis04" or "simplified" or "traditional" or "full-width" or "proportional-width" or "ruby") eastAsian.Add(token);
        }
        longhands.Add("font-variant-caps", caps);
        longhands.Add("font-variant-ligatures", ligatures.Count == 0 ? "normal" : string.Join(" ", ligatures));
        longhands.Add("font-variant-numeric", numeric.Count == 0 ? "normal" : string.Join(" ", numeric));
        longhands.Add("font-variant-east-asian", eastAsian.Count == 0 ? "normal" : string.Join(" ", eastAsian));
        longhands.Add("font-variant-position", position);
    }

    /// <summary>grid-template: none, "rows / columns", or area strings each optionally followed by a row size, then "/ columns".</summary>
    private static void ExpandGridTemplate(string value, LonghandWriter longhands)
    {
        var v = value.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            longhands.Add("grid-template-rows", "none");
            longhands.Add("grid-template-columns", "none");
            longhands.Add("grid-template-areas", "none");
            return;
        }
        var parts = SplitTopLevel(v, '/');
        string rowsPart = parts.Count > 0 ? parts[0].Trim() : "";
        string columns = parts.Count > 1 ? parts[1].Trim() : "none";
        if (rowsPart.IndexOf('"') < 0 && rowsPart.IndexOf('\'') < 0)
        {
            longhands.Add("grid-template-rows", rowsPart.Length > 0 ? rowsPart : "none");
            longhands.Add("grid-template-columns", columns);
            longhands.Add("grid-template-areas", "none");
            return;
        }
        // Each area string is a row; a row without a size after it is auto, and line names stay with the rows.
        var areas = new StringBuilder();
        var rows = new StringBuilder();
        bool pendingRow = false;
        foreach (var token in Tokens(rowsPart))
        {
            if (token[0] is '"' or '\'')
            {
                if (pendingRow) rows.Append(" auto");
                areas.Append(' ').Append(token);
                pendingRow = true;
            }
            else if (token[0] == '[') rows.Append(' ').Append(token);
            else
            {
                rows.Append(' ').Append(token);
                pendingRow = false;
            }
        }
        if (pendingRow) rows.Append(" auto");
        longhands.Add("grid-template-areas", areas.ToString().Trim());
        longhands.Add("grid-template-rows", rows.ToString().Trim());
        longhands.Add("grid-template-columns", columns);
    }

    /// <summary>flex: the keywords none, auto and initial, or grow, shrink and basis, where a number is grow then shrink and anything else the basis.</summary>
    private static void ExpandFlex(string value, LonghandWriter longhands)
    {
        var lower = value.Trim().ToLowerInvariant();
        switch (lower)
        {
            case "none": AddFlex("0", "0", "auto"); return;
            case "auto": AddFlex("1", "1", "auto"); return;
            case "initial": AddFlex("0", "1", "auto"); return;
        }
        string grow = "1", shrink = "1", basis = "0%";
        int numbers = 0;
        foreach (var token in Tokens(lower))
        {
            bool isNumber = float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (isNumber && numbers == 0)
            {
                grow = token;
                numbers++;
            }
            else if (isNumber && numbers == 1)
            {
                shrink = token;
                numbers++;
            }
            else basis = token;
        }
        AddFlex(grow, shrink, basis);

        void AddFlex(string g, string s, string b)
        {
            longhands.Add("flex-grow", g);
            longhands.Add("flex-shrink", s);
            longhands.Add("flex-basis", b);
        }
    }

    /// <summary>
    /// font: optional style, weight, small-caps and stretch keywords in any order, then the size with an optional
    /// "/line-height", then the family. The system font keywords (caption, menu, ...) are not supported.
    /// </summary>
    private static void ExpandFont(string value, LonghandWriter longhands)
    {
        var lower = value.Trim().ToLowerInvariant();
        if (lower is "caption" or "icon" or "menu" or "message-box" or "small-caption" or "status-bar") return;

        var tokens = Tokens(value);
        string style = "normal", weight = "normal", lineHeight = "normal", caps = "normal", stretch = "normal";
        int i = 0;
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i].ToLowerInvariant();
            if (t is "italic" or "oblique") style = t;
            else if (t is "bold" or "bolder" or "lighter" || t.Length == 3 && char.IsDigit(t[0]) && t.EndsWith("00", StringComparison.Ordinal)) weight = t;
            else if (t == "small-caps") caps = t;
            else if (t is "ultra-condensed" or "extra-condensed" or "condensed" or "semi-condensed" or "semi-expanded" or "expanded"
                     or "extra-expanded" or "ultra-expanded") stretch = t;
            else if (t == "normal") { }
            else break;
        }
        if (i >= tokens.Count) return;

        // The size, with the line height after a slash that may be glued to the size, to the line height, or on its own.
        string size;
        var sizeToken = tokens[i++];
        int slash = sizeToken.IndexOf('/');
        if (slash >= 0)
        {
            size = sizeToken.Substring(0, slash);
            lineHeight = sizeToken.Substring(slash + 1);
            if (lineHeight.Length == 0 && i < tokens.Count) lineHeight = tokens[i++];
        }
        else
        {
            size = sizeToken;
            if (i < tokens.Count && tokens[i].StartsWith('/'))
            {
                lineHeight = tokens[i].Length > 1 ? tokens[i].Substring(1) : (i + 1 < tokens.Count ? tokens[++i] : "normal");
                i++;
            }
        }
        string family = string.Join(" ", tokens.Skip(i));
        longhands.Add("font-style", style);
        longhands.Add("font-weight", weight);
        longhands.Add("font-size", size);
        longhands.Add("line-height", lineHeight);
        longhands.Add("font-variant-caps", caps);
        longhands.Add("font-stretch", stretch);
        longhands.Add("font-variant-numeric", "normal");
        longhands.Add("font-variant-east-asian", "normal");
        longhands.Add("font-variant-position", "normal");
        longhands.Add("font-kerning", "auto");
        longhands.Add("font-size-adjust", "none");
        if (family.Length > 0 || IsCssWideKeyword(value)) longhands.Add("font-family", family);
    }
}

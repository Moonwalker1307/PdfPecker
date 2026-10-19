using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>Presentational attributes (HTML and SVG) as style hints.</summary>
internal sealed partial class StyleResolver
{
    private void AddHints(Element e)
    {
        if (e.Attributes.Count == 0 && e.Name != "td" && e.Name != "th" && e.Name != "bdi") return;
        _scratch.Clear();
        string? v;
        if (e.HasAttribute("dir") || e.Name == "bdi") DirHints(e);
        // The content language, inherited, for hyphens: auto. An empty lang means an unknown language.
        if ((e.GetAttribute("lang") ?? e.GetAttribute("xml:lang")) is { } lang) Hint(LangProperty, lang.Trim() is { Length: > 0 } tag ? tag : "und");
        if (IsSvgElement(e))
        {
            SvgHints(e);
            for (int i = 0; i < _scratch.Count; i++) Push(TierHint, 0, 0, i, _scratch[i]);
            return;
        }
        switch (e.Name)
        {
            case "img":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "table":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("cellspacing")) != null) { Hint("border-spacing-h", PxOrPercent(v)); Hint("border-spacing-v", PxOrPercent(v)); }
                if ((v = e.GetAttribute("border")) != null)
                {
                    var bw = v.Length == 0 ? "1px" : PxOrPercent(v);
                    if (bw != "0px")
                    {
                        foreach (var side in new[] { "top", "right", "bottom", "left" })
                        {
                            Hint("border-" + side + "-width", bw);
                            Hint("border-" + side + "-style", "outset");
                            Hint("border-" + side + "-color", "gray");
                        }
                    }
                }
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "td":
            case "th":
            {
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("height")) != null) Hint("height", PxOrPercent(v));
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("valign")) != null) Hint("vertical-align", v.ToLowerInvariant());
                if (e.HasAttribute("nowrap")) Hint("white-space", "nowrap");
                var table = FindTable(e);
                if (table != null)
                {
                    if ((v = table.GetAttribute("cellpadding")) != null)
                        foreach (var side in new[] { "top", "right", "bottom", "left" }) Hint("padding-" + side, PxOrPercent(v));
                    if ((v = table.GetAttribute("border")) != null && (v.Length == 0 || PxOrPercent(v) != "0px"))
                        foreach (var side in new[] { "top", "right", "bottom", "left" })
                        {
                            Hint("border-" + side + "-width", "1px");
                            Hint("border-" + side + "-style", "inset");
                            Hint("border-" + side + "-color", "gray");
                        }
                }
                break;
            }
            case "tr":
            case "thead":
            case "tbody":
            case "tfoot":
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("valign")) != null) Hint("vertical-align", v.ToLowerInvariant());
                break;
            case "input" when e.Control is { Kind: Layout.ControlKind.Text }:
                // Chrome sizes a text field from its size attribute: 0.6em a character, and a little more.
                if (Layout.FormControls.FieldChars(e) is int chars) Hint("width", Em(Layout.FormControls.FieldWidthEm(chars)));
                break;
            case "textarea":
                if (Layout.FormControls.FieldChars(e) is int cols) Hint("width", Em(Layout.FormControls.FieldWidthEm(cols)));
                break;
            case "select" when e.Control is { Kind: Layout.ControlKind.List }:
            {
                // A list box shows size rows (four when only multiple is given) of 17px, and room for a scroll bar.
                Hint("height", "calc(" + Em(e.Control.Rows * 1.275f) + " + 2px)"); // select is border-box
                Hint("padding-top", "0px");
                Hint("padding-bottom", "0px");
                Hint("padding-left", "0px");
                Hint("padding-right", "16px");
                break;
            }
            case "font":
                if ((v = e.GetAttribute("color")) != null) Hint("color", v);
                if ((v = e.GetAttribute("face")) != null) Hint("font-family", v);
                if ((v = e.GetAttribute("size")) != null) Hint("font-size", FontSizeAttr(v));
                break;
            case "hr":
                if ((v = e.GetAttribute("width")) != null) Hint("width", PxOrPercent(v));
                if ((v = e.GetAttribute("size")) != null) { Hint("border-top-width", PxOrPercent(v)); Hint("border-bottom-width", "0"); }
                if ((v = e.GetAttribute("color")) != null) { Hint("border-top-color", v); Hint("border-bottom-color", v); Hint("background-color", v); }
                if (e.HasAttribute("noshade")) { Hint("border-top-style", "solid"); Hint("border-bottom-style", "solid"); }
                AlignHint(e.GetAttribute("align"), block: true);
                break;
            case "body":
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                if ((v = e.GetAttribute("text")) != null) Hint("color", v);
                break;
            default:
                if ((v = e.GetAttribute("align")) != null) Hint("text-align", v.ToLowerInvariant());
                if ((v = e.GetAttribute("bgcolor")) != null) Hint("background-color", v);
                break;
        }
        for (int i = 0; i < _scratch.Count; i++) Push(TierHint, 0, 0, i, _scratch[i]);
    }

    private static readonly HashSet<string> SvgPresentationAttributes = new(StringComparer.Ordinal)
    {
        "fill", "stroke", "stroke-width", "fill-opacity", "stroke-opacity", "fill-rule", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "opacity", "display", "visibility", "color", "clip-path", "clip-rule", "text-anchor",
        "dominant-baseline", "alignment-baseline", "font-size", "font-family", "font-weight", "font-style", "letter-spacing",
        "mask", "mask-type", "filter", "marker", "marker-start", "marker-mid", "marker-end", "flood-color", "flood-opacity",
        "lighting-color", "color-interpolation-filters", "direction", "unicode-bidi", "stroke-dasharray", "stroke-dashoffset",
    };

    /// <summary>The fragment of a url(#id) reference, as "#id"; null for none or anything else.</summary>
    private static string? SvgUrl(string v, string lower)
        => lower.StartsWith("url(", StringComparison.Ordinal) && v.IndexOf(')') is int end and > 4
            ? v.Substring(4, end - 4).Trim().Trim('"', '\'') : null;

    /// <summary>A font-stretch keyword or percentage as a percentage of the normal width; null when invalid.</summary>
    internal static float? FontStretchPercent(string v) => v switch
    {
        "ultra-condensed" => 50, "extra-condensed" => 62.5f, "condensed" => 75, "semi-condensed" => 87.5f, "normal" => 100,
        "semi-expanded" => 112.5f, "expanded" => 125, "extra-expanded" => 150, "ultra-expanded" => 200,
        _ => v.EndsWith('%') && float.TryParse(v.AsSpan(0, v.Length - 1), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float p) && p >= 0 ? p : null,
    };

    /// <summary>Whether font-feature-settings turns standard ligatures on or off ("liga" 0), or null when it leaves them.</summary>
    private static bool? LigaSetting(string value)
    {
        bool? result = null;
        foreach (var part in value.Split(','))
        {
            var p = part.Trim();
            if (p.Length < 6 || (p[0] != '"' && p[0] != '\'') || p[5] != p[0] || p.Substring(1, 4) != "liga") continue;
            var arg = p.Substring(6).Trim();
            result = arg is "" or "on" || (Invariant.TryParseInt(arg, out int n) && n != 0);
        }
        return result;
    }

    /// <summary>True for &lt;svg&gt; and the elements inside it.</summary>
    internal static bool IsSvgElement(Element e)
    {
        if (e.Name == "svg") return true;
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "svg") return true;
        return false;
    }

    private void SvgHints(Element e)
    {
        foreach (var (name, value) in e.Attributes)
        {
            if (SvgPresentationAttributes.Contains(name)) Hint(name, value);
            else if (e.Name == "svg" && name is "width" or "height" && e.Parent?.Name != "svg") Hint(name, SvgLength(value));
        }
    }

    private static string SvgLength(string v)
    {
        v = v.Trim();
        // Plain numbers are pixels; values with units or percentages are CSS lengths.
        return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? v + "px" : v.ToLowerInvariant();
    }

    // The dir attribute (and bdi, which defaults to dir=auto): the direction, and an isolated run of text. dir=auto
    // takes the direction of the first strong character of the content, as HTML specifies.
    private void DirHints(Element e)
    {
        var dir = e.GetAttribute("dir")?.Trim().ToLowerInvariant();
        if (dir is not ("ltr" or "rtl"))
        {
            if (dir != "auto" && e.Name != "bdi") return;
            dir = FirstStrongDirection(e) ? "rtl" : "ltr";
        }
        Hint("direction", dir);
        if (e.Name != "bdo") Hint("unicode-bidi", "isolate");
    }

    // True when the first strong character of the element's text is right to left; descendants with their own dir
    // attribute, and script and style elements, are skipped.
    private static bool FirstStrongDirection(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t)
            {
                foreach (char ch in t.Text)
                {
                    var bc = Text.UnicodeTables.Bidi(ch);
                    if (bc == Text.BidiClass.L) return false;
                    if (bc is Text.BidiClass.R or Text.BidiClass.AL) return true;
                }
            }
            else if (c is Element ce && ce.Name is not ("script" or "style" or "bdi") && !ce.HasAttribute("dir"))
            {
                if (HasStrong(ce, out bool rtl)) return rtl;
            }
        }
        return false;

        static bool HasStrong(Element e, out bool rtl)
        {
            foreach (var c in e.Children)
            {
                if (c is TextNode t)
                    foreach (char ch in t.Text)
                    {
                        var bc = Text.UnicodeTables.Bidi(ch);
                        if (bc == Text.BidiClass.L) { rtl = false; return true; }
                        if (bc is Text.BidiClass.R or Text.BidiClass.AL) { rtl = true; return true; }
                    }
                else if (c is Element ce && ce.Name is not ("script" or "style" or "bdi") && !ce.HasAttribute("dir") && HasStrong(ce, out rtl)) return true;
            }
            rtl = false;
            return false;
        }
    }

    private void AlignHint(string? align, bool block)
    {
        if (align == null) return;
        var a = align.ToLowerInvariant();
        if (a == "center" && block) { Hint("margin-left", "auto"); Hint("margin-right", "auto"); }
        else if (a is "middle" or "top" or "bottom") Hint("vertical-align", a);
    }

    // Chrome draws text fields, text areas, drop-downs and buttons natively (rounded, with a thin grey border) until the
    // page styles their border or background. Then it falls back to its user-agent sheet: square corners, a 2px inset
    // grey border and 1px 2px padding on a text field, a 2px outset black border and 1px 6px padding on a button. The
    // native look's border and padding add up to the same size, so only a page that also sets a border width sees the
    // padding change.
    private static readonly Declaration[] FieldFallback = Fallback("border-width: 2px; border-style: inset; padding: 1px 2px; border-radius: 0");
    private static readonly Declaration[] ButtonFallback = Fallback("border-width: 2px; border-style: outset; padding: 1px 6px; border-radius: 0");
    private static readonly Declaration[] ButtonFallbackColor = Fallback("border-color: black");
    private static readonly Declaration[] SquareFallback = Fallback("border-radius: 0");

    private static Declaration[] Fallback(string css)
    {
        var list = new List<Declaration>();
        CssParser.ParseDeclarations(css, list);
        return list.ToArray();
    }

    /// <summary>
    /// Adds Chrome's fallback look for a form control whose border or background the page styles, as user-agent
    /// declarations that the page's own still override. <paramref name="authorStart"/> is where the page's
    /// declarations begin in the working buffer.
    /// </summary>
    private void StyledControlFallback(Element e, int authorStart)
    {
        if (e.Name is not ("input" or "textarea" or "select" or "button")) return;
        bool styled = false;
        for (int i = authorStart; i < _count && !styled; i++)
        {
            var p = _decls[i].Property;
            styled = p.StartsWith("background", StringComparison.Ordinal)
                     || p.StartsWith("border", StringComparison.Ordinal) && p is not ("border-collapse" or "border-spacing");
        }
        if (!styled) return;
        string? type = e.Name == "input" ? e.GetAttribute("type")?.Trim().ToLowerInvariant() : null;
        Declaration[] fallback;
        bool button = e.Name == "button" || type is "submit" or "reset" or "button";
        if (button) fallback = ButtonFallback;
        else if (e.Name == "input")
        {
            // Check boxes and radio buttons keep their native look in Chrome; the rest have no box of their own.
            if (type is "checkbox" or "radio" or "hidden" or "image" or "file" or "range" or "color") return;
            fallback = FieldFallback;
        }
        else fallback = SquareFallback;
        // After every user-agent rule, before the presentational hints and the page's rules.
        const int Last = 0xFFFFF;
        for (int i = 0; i < fallback.Length; i++) Push(TierUa, Last, Last, i, fallback[i], UnlayeredRank);
        if (button && !e.HasAttribute("disabled"))
            for (int i = 0; i < ButtonFallbackColor.Length; i++) Push(TierUa, Last, Last, fallback.Length + i, ButtonFallbackColor[i], UnlayeredRank);
    }

    // The internal property that carries the lang attribute into the computed style.
    private const string LangProperty = "-pdfpecker-lang";

    private void Hint(string property, string value)
    {
        // Interned so that elements with identical attributes produce identical declaration lists (see the style cache).
        if (!_hintCache.TryGetValue((property, value), out var d)) _hintCache[(property, value)] = d = new Declaration(property, value, false);
        _scratch.Add(d);
    }

    private static Element? FindTable(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "table") return p;
        return null;
    }

    private static string Em(float n) => n.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "em";

    private static string PxOrPercent(string v)
    {
        v = v.Trim();
        if (v.EndsWith('%')) return v;
        int n = 0;
        while (n < v.Length && (char.IsDigit(v[n]) || v[n] == '.')) n++;
        return n == 0 ? "0px" : string.Concat(v.AsSpan(0, n), "px");
    }

    private static string FontSizeAttr(string v)
    {
        v = v.Trim();
        int size;
        if (v.StartsWith('+') && Invariant.TryParseInt(v.AsSpan(1), out int plus)) size = 3 + plus;
        else if (v.StartsWith('-') && Invariant.TryParseInt(v.AsSpan(1), out int minus)) size = 3 - minus;
        else if (!Invariant.TryParseInt(v, out size)) size = 3;
        return Math.Clamp(size, 1, 7) switch { 1 => "x-small", 2 => "small", 3 => "medium", 4 => "large", 5 => "x-large", 6 => "xx-large", _ => "48px" };
    }
}

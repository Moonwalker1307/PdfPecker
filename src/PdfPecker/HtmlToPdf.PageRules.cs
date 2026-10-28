using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    /// <summary>A page of the options' size and margins, before any @page rule applies.</summary>
    private static PageSetup PageOfOptions(PdfOptions options) => new()
    {
        Width = options.PageSize.Width, Height = options.PageSize.Height,
        MarginTop = options.Margins.Top, MarginRight = options.Margins.Right, MarginBottom = options.Margins.Bottom, MarginLeft = options.Margins.Left,
    };

    /// <summary>
    /// Scales a page set up on paper to layout points and fits its margins. The page is laid out at its paper size
    /// divided by the scale, as Chrome's print scale does; the writer then draws it scaled, so the margins come out at
    /// their paper size again. A page scaled by PdfOptions.Scale is written at the paper size Chrome would give it.
    /// </summary>
    private static void ScaleAndFit(PageSetup page, float scale, bool chromePaper, ComputedStyle initial)
    {
        page.ChromePaper = chromePaper;
        if (scale != 1)
        {
            page.Scale = scale;
            page.Width /= scale; page.Height /= scale;
            page.MarginTop /= scale; page.MarginRight /= scale; page.MarginBottom /= scale; page.MarginLeft /= scale;
        }
        if (page.BoxDeclarations != null) ResolvePageBox(page, initial);
        FitMargins(page);
        // Margins shrunk to fit may leave less room than the page box's border and padding.
        page.BoxTop = Math.Min(page.BoxTop, page.MarginTop);
        page.BoxRight = Math.Min(page.BoxRight, page.MarginRight);
        page.BoxBottom = Math.Min(page.BoxBottom, page.MarginBottom);
        page.BoxLeft = Math.Min(page.BoxLeft, page.MarginLeft);
    }

    /// <summary>
    /// The properties of an @page rule that style the page box itself: its border, padding and background. Chrome draws
    /// the border inside the page margins and lays the content out inside the border and padding.
    /// </summary>
    private static bool IsPageBoxProperty(string p)
        => p.StartsWith("border", StringComparison.Ordinal) || p.StartsWith("padding", StringComparison.Ordinal)
           || p.StartsWith("background", StringComparison.Ordinal);

    /// <summary>
    /// Resolves the page box's style from its declarations, as the style of an element would be, and moves the page area
    /// in by its border and padding. The page's background colour is the style's. A page box without a border, padding or
    /// background image keeps no style, so that a page with only a background colour is drawn as before.
    /// </summary>
    private static void ResolvePageBox(PageSetup page, ComputedStyle initial)
    {
        var doc = new Element("#document");
        var box = new Element("div");
        var style = new StringBuilder();
        foreach (var d in page.BoxDeclarations!)
        {
            style.Append(d.Property).Append(':').Append(d.Value);
            if (d.Important) style.Append(" !important");
            style.Append(';');
        }
        box.AddAttribute("style", style.ToString());
        doc.AppendChild(box);
        new StyleResolver(new Stylesheet(), page.ContentWidth, page.ContentHeight).ResolveTree(doc, initial);
        var s = box.Style!;
        page.Background = s.BackgroundColor;
        // Percentages refer to the width of the page area the margins leave, as for a block's padding.
        float basis = page.ContentWidth;
        float top = s.PaddingTop.Resolve(basis), right = s.PaddingRight.Resolve(basis);
        float bottom = s.PaddingBottom.Resolve(basis), left = s.PaddingLeft.Resolve(basis);
        if (!s.HasBorder && s.BorderImageSource == null && s.BackgroundImages == null && top <= 0 && right <= 0 && bottom <= 0 && left <= 0)
            return;
        page.Box = s;
        page.BoxTop = s.BorderTop + Math.Max(0, top);
        page.BoxRight = s.BorderRight + Math.Max(0, right);
        page.BoxBottom = s.BorderBottom + Math.Max(0, bottom);
        page.BoxLeft = s.BorderLeft + Math.Max(0, left);
        page.MarginTop += page.BoxTop;
        page.MarginRight += page.BoxRight;
        page.MarginBottom += page.BoxBottom;
        page.MarginLeft += page.BoxLeft;
    }

    /// <summary>Applies the size, margin and background declarations of an @page rule to a page.</summary>
    private static void ApplyPageRules(List<Declaration> decls, PageSetup page)
    {
        foreach (var d in decls)
        {
            if (IsPageBoxProperty(d.Property)) (page.BoxDeclarations ??= new List<Declaration>()).Add(d);
            var v = d.Value.Trim().ToLowerInvariant();
            switch (d.Property)
            {
                case "size":
                {
                    var tokens = CssParser.Tokens(v);
                    bool landscape = tokens.Remove("landscape");
                    bool portrait = tokens.Remove("portrait");
                    PageSize? size = null;
                    if (tokens.Count == 1)
                    {
                        size = tokens[0] switch
                        {
                            "a3" => PageSize.A3,
                            "a4" => PageSize.A4,
                            "a5" => PageSize.A5,
                            "letter" => PageSize.Letter,
                            "legal" => PageSize.Legal,
                            _ => StyleResolver.TryParseLength(tokens[0], 12, 12, out var l) && !l.IsAuto && !l.IsPercent && l.Value > 0
                                ? new PageSize(l.Value, l.Value) : null,
                        };
                    }
                    else if (tokens.Count == 2
                             && StyleResolver.TryParseLength(tokens[0], 12, 12, out var w) && !w.IsAuto && !w.IsPercent && w.Value > 0
                             && StyleResolver.TryParseLength(tokens[1], 12, 12, out var h) && !h.IsAuto && !h.IsPercent && h.Value > 0)
                    {
                        size = new PageSize(w.Value, h.Value);
                    }
                    var final = size ?? new PageSize(page.Width, page.Height);
                    if (landscape) final = final.Landscape;
                    else if (portrait && final.Width > final.Height) final = new PageSize(final.Height, final.Width);
                    page.Width = final.Width;
                    page.Height = final.Height;
                    break;
                }
                case "margin-top": SetMargin(v, ref page.MarginTop, page.Height); break;
                case "margin-right": SetMargin(v, ref page.MarginRight, page.Width); break;
                case "margin-bottom": SetMargin(v, ref page.MarginBottom, page.Height); break;
                case "margin-left": SetMargin(v, ref page.MarginLeft, page.Width); break;
                case "background-color":
                case "background":
                    // The page box's own background covers the whole sheet, margins included.
                    if (CssColor.TryParse(v.Trim(), CssColor.Black, out uint color)) page.Background = color;
                    break;
            }
        }
    }

    /// <summary>Sets a page margin from a length; percentages refer to the page's width or height, as <paramref name="basis"/> gives.</summary>
    private static void SetMargin(string v, ref float target, float basis)
    {
        if (StyleResolver.TryParseLength(v, 12, 12, out var l) && !l.IsAuto) target = Math.Max(0, l.Resolve(basis));
    }

    /// <summary>
    /// The value an @page rule's counter-reset gives the footnote counter (CSS GCPM: footnotes numbered per page), or
    /// null when no @page rule resets it.
    /// </summary>
    private static int? FootnotePageReset(List<PageRule> rules)
    {
        int? value = null;
        foreach (var rule in rules.OrderBy(r => r.Specificity))
            foreach (var d in rule.Declarations)
            {
                if (d.Property != "counter-reset") continue;
                var tokens = CssParser.Tokens(d.Value.Trim());
                int at = tokens.IndexOf("footnote");
                if (at < 0) continue;
                value = at + 1 < tokens.Count && int.TryParse(tokens[at + 1], System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
            }
        return value;
    }

    /// <summary>The smallest content area, in layout points, that a page may have in either direction.</summary>
    private const float MinContentSize = 10;

    /// <summary>
    /// Shrinks @page margins (and a tiny @page size) that leave less than <see cref="MinContentSize"/> for content. A
    /// browser prints such a document rather than refusing it, and the document's CSS may be untrusted, so it must not
    /// be able to make the conversion fail. The margins keep their proportions to each other.
    /// </summary>
    private static void FitMargins(PageSetup page)
    {
        FitAxis(ref page.Width, ref page.MarginLeft, ref page.MarginRight);
        FitAxis(ref page.Height, ref page.MarginTop, ref page.MarginBottom);

        static void FitAxis(ref float size, ref float start, ref float end)
        {
            if (size - start - end >= MinContentSize) return;
            if (!(size >= MinContentSize)) size = MinContentSize;
            float margins = start + end, room = size - MinContentSize;
            if (margins > 0 && float.IsFinite(margins) && float.IsFinite(room))
            {
                start *= room / margins;
                end *= room / margins;
            }
            else start = end = 0; // an infinite or NaN margin has no proportion to keep
        }
    }
}

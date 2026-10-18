using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    /// <summary>The first element with the attribute, before styles are known: the candidates for the bands' estimated heights.</summary>
    private static Element? FindByAttribute(Element parent, string attribute)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e) continue;
            if (e.HasAttribute(attribute)) return e;
            var found = FindByAttribute(e, attribute);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>The first displayed element with the attribute: a band that display: none hides takes no space.</summary>
    private static Element? FindBand(Element parent, string attribute)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e || e.Style == null || e.Style.Display == Display.None) continue;
            if (e.HasAttribute(attribute)) return e;
            var found = FindBand(e, attribute);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// The height a band reserves with its height attribute, in layout points. With <paramref name="truncate"/>, it is
    /// rounded down to whole points on paper, as Chrome rounds page margins.
    /// </summary>
    private static float DeclaredHeight(Element? band, PageSetup page, bool truncate)
    {
        float height = DeclaredHeight(band);
        if (!truncate || height <= 0) return height;
        // The small allowance keeps a height that is already whole on paper (48pt) from losing a point to float error.
        return MathF.Floor(height * page.Scale + 0.001f) / page.Scale;
    }

    /// <summary>The band height given by a height attribute (e.g. height="17mm"; plain numbers are pixels), or 0.</summary>
    private static float DeclaredHeight(Element? band)
    {
        var attr = band?.GetAttribute("height")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(attr)) return 0;
        if (float.TryParse(attr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)) return Math.Max(0, px * 0.75f);
        return StyleResolver.TryParseLength(attr!, 12, 12, out var len) && !len.IsAuto && !len.IsPercent ? Math.Max(0, len.Value) : 0;
    }

    /// <summary>The laid-out height of a band, measured with two-digit page numbers in place of the placeholders.</summary>
    private static float BandHeight(Element band, PageSetup page, ImageLoader images, CancellationToken cancellationToken)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        foreach (var (node, original) in texts) node.Text = ReplacePlaceholders(original, "99", "99");
        try
        {
            return Math.Max(0, new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken }.LayoutBand(band, page.ContentWidth));
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private static string ReplacePlaceholders(string text, string page, string pages)
        => text.Replace("{page}", page).Replace("{total-pages}", pages).Replace("{pages}", pages);

    /// <summary>Lays out a band for one page, with {page}, {pages} and {total-pages} replaced in its text.</summary>
    private static List<DrawOp> LayoutBandForPage(Element band, PageSetup page, ImageLoader images, int pageIndex, int pageCount, bool alignBottom, CancellationToken cancellationToken)
    {
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        string number = (pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string total = pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (node, original) in texts)
            node.Text = ReplacePlaceholders(original, number, total);
        try
        {
            var engine = new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken };
            float h = engine.LayoutBand(band, page.ContentWidth);
            if (alignBottom)
            {
                float dy = page.ContentHeight - h;
                foreach (var op in engine.Ops) op.Offset(0, dy);
            }
            return engine.Ops;
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private static void CollectPlaceholders(Element e, List<(TextNode, string)> texts)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t && t.Text.IndexOf('{') >= 0) texts.Add((t, t.Text));
            else if (c is Element ce) CollectPlaceholders(ce, texts);
        }
    }
}

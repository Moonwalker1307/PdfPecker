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
        foreach (var (node, original) in texts) node.Text = ReplacePlaceholders(original, "99", "99", null, 99);
        try
        {
            return Math.Max(0, new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken }.LayoutBand(band, page.ContentWidth));
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    private const string SectionPagePlaceholder = "{section-page:";

    /// <summary>
    /// The text with its placeholders replaced: {page}, {pages} and {total-pages}, and {section-page:id} with the page's
    /// number counted from the page the element with that (percent-encoded) id starts on, which
    /// <paramref name="firstPageOf"/> gives; 0 for an id it does not know.
    /// </summary>
    private static string ReplacePlaceholders(string text, string page, string pages, Func<string, int?>? firstPageOf, int pageNumber)
    {
        text = text.Replace("{page}", page).Replace("{total-pages}", pages).Replace("{pages}", pages);
        int at = text.IndexOf(SectionPagePlaceholder, StringComparison.Ordinal);
        if (at < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        int from = 0;
        while (at >= 0)
        {
            int close = text.IndexOf('}', at + SectionPagePlaceholder.Length);
            if (close < 0) break;
            sb.Append(text, from, at - from);
            string encoded = text.Substring(at + SectionPagePlaceholder.Length, close - at - SectionPagePlaceholder.Length);
            if (firstPageOf == null) sb.Append("99");
            else
            {
                string id;
                try { id = Uri.UnescapeDataString(encoded); }
                catch (UriFormatException) { id = encoded; }
                int number = firstPageOf(id) is int first ? pageNumber - first + 1 : 0;
                sb.Append(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            from = close + 1;
            at = text.IndexOf(SectionPagePlaceholder, from, StringComparison.Ordinal);
        }
        sb.Append(text, from, text.Length - from);
        return sb.ToString();
    }

    /// <summary>
    /// Lays out a band for one page, with {page}, {pages}, {total-pages} and {section-page:id} replaced in its text. A band without them
    /// looks the same on every page, so it is laid out once per page setup and its ops are drawn on each page, as fixed
    /// boxes are. A band with them is laid out for every page, within a budget for the whole document.
    /// </summary>
    private static List<DrawOp> LayoutBandForPage(Element band, PageSetup page, ImageLoader images, int pageIndex, int pageCount, bool alignBottom,
        BandLayouts laidOut, Func<string, int?> firstPageOf, CancellationToken cancellationToken)
    {
        if (laidOut.Unnumbered.TryGetValue((band, page), out var same)) return same;
        var texts = new List<(TextNode Node, string Original)>();
        CollectPlaceholders(band, texts);
        if (texts.Count > 0) laidOut.CheckBefore(band, pageCount);
        string number = (pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string total = pageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (node, original) in texts)
            node.Text = ReplacePlaceholders(original, number, total, firstPageOf, pageIndex + 1);
        try
        {
            var engine = new LayoutEngine(page.ContentHeight, images) { Cancellation = cancellationToken };
            float h = engine.LayoutBand(band, page.ContentWidth);
            if (alignBottom)
            {
                float dy = page.ContentHeight - h;
                foreach (var op in engine.Ops) op.Offset(0, dy);
            }
            // A band taller than the page shows only the part on the paper; the rest would be written on every page.
            OffPaper(engine.Ops, -page.MarginTop, page.Height - page.MarginTop);
            if (texts.Count == 0) laidOut.Unnumbered[(band, page)] = engine.Ops;
            else laidOut.Charge(band, engine.BoxLayouts + CountOps(engine.Ops), pageCount);
            return engine.Ops;
        }
        finally
        {
            foreach (var (node, original) in texts) node.Text = original;
        }
    }

    /// <summary>
    /// The bands of a document laid out so far: those without page numbers, whose ops are drawn again on every page, and
    /// the work that those laid out again for each page have taken.
    /// </summary>
    private sealed class BandLayouts
    {
        /// <summary>
        /// The work, in box layouts and kept ops, that the bands laid out for each page may take in all: a base and a
        /// share per page. A header and a footer that say "Page {page} of {total-pages}" take about 30 per page, so a
        /// document of 1,000 pages uses less than 2% of its budget and one of 100,000 pages about 5%. A band that holds
        /// a table of 85,000 rows took 600,000 per page, which for 350 pages was over five minutes and gigabytes of ops.
        /// </summary>
        private const long BaseWork = 1_000_000;
        private const long WorkPerPage = 500;

        private readonly Dictionary<Element, long> _lastWork = new();
        private long _spent;

        /// <summary>The ops of the bands without page numbers, by band and page setup.</summary>
        public Dictionary<(Element, PageSetup), List<DrawOp>> Unnumbered { get; } = new();

        /// <summary>
        /// Before a numbered band is laid out again, stops the document when doing so as often as last time would go over
        /// the budget, so that the work is refused before it is done rather than after.
        /// </summary>
        public void CheckBefore(Element band, int pageCount)
        {
            if (_lastWork.TryGetValue(band, out long last) && _spent + last > Budget(pageCount)) throw TooMuchWork(pageCount);
        }

        /// <summary>Charges the work of laying out a numbered band once, and stops the document when that goes over the budget.</summary>
        public void Charge(Element band, long work, int pageCount)
        {
            _lastWork[band] = work;
            _spent += work;
            if (_spent > Budget(pageCount)) throw TooMuchWork(pageCount);
        }

        private static long Budget(int pageCount) => BaseWork + WorkPerPage * Math.Max(1, pageCount);

        private static PdfLimitException TooMuchWork(int pageCount)
            => new($"The running header or footer is too large to lay out again for each of the {pageCount:N0} pages to number them: "
                   + "keep large content out of a header or footer with {page}, {pages} or {total-pages}.");
    }

    /// <summary>The ops of a list and of the groups in it, which a numbered band keeps for its page.</summary>
    private static long CountOps(List<DrawOp> ops)
    {
        long n = ops.Count;
        foreach (var op in ops)
            if (op is GroupOp g) n += CountOps(g.Ops);
        return n;
    }

    /// <summary>
    /// Removes the text, boxes, images and links that lie wholly above <paramref name="top"/> or below
    /// <paramref name="bottom"/>, going into groups, clips and layers but not into layers whose transform or filter can
    /// move or spread them back. Text keeps two font sizes either side of its baseline, more than any glyph reaches.
    /// </summary>
    private static void OffPaper(List<DrawOp> ops, float top, float bottom)
    {
        ops.RemoveAll(op => op switch
        {
            TextOp t => t.Baseline - 2 * t.Size > bottom || t.Baseline + 2 * t.Size < top,
            RectOp r => r.Y > bottom || r.Y + r.H < top,
            ImageOp i => i.Y > bottom || i.Y + i.H < top,
            LinkOp l => l.Y > bottom || l.Y + l.H < top,
            _ => false,
        });
        foreach (var op in ops)
            if (op is GroupOp g && (g is not LayerOp l || l is { Transform: null, Transform3D: null, Raster: null })) OffPaper(g.Ops, top, bottom);
    }

    /// <summary>
    /// The text nodes of a band that hold {page}, {pages} or {total-pages}. Text with other braces, such as the fluent
    /// package's caller text, which it writes as "{&lt;span&gt;&lt;/span&gt;page}" so that it stays as written, is the
    /// same on every page and must not make the band be laid out again for each.
    /// </summary>
    private static void CollectPlaceholders(Element e, List<(TextNode, string)> texts)
    {
        foreach (var c in e.Children)
        {
            if (c is TextNode t && HasPlaceholder(t.Text)) texts.Add((t, t.Text));
            else if (c is Element ce) CollectPlaceholders(ce, texts);
        }
    }

    /// <summary>Whether the text holds a placeholder that <see cref="ReplacePlaceholders"/> replaces.</summary>
    private static bool HasPlaceholder(string text)
        => text.Contains("{page}", StringComparison.Ordinal) || text.Contains("{pages}", StringComparison.Ordinal)
           || text.Contains("{total-pages}", StringComparison.Ordinal) || text.Contains(SectionPagePlaceholder, StringComparison.Ordinal);
}

namespace PdfPecker.Pdf;

/// <summary>
/// The catalog entries that tell a viewer how to open the document (page layout, page mode, open action and viewer
/// preferences) and the page labels, written the same way by the document writer and the merge writer.
/// </summary>
internal static class ViewerCatalog
{
    /// <summary>
    /// Whether the settings use entries PDF 1.4 lacks: the two-page layouts (PDF 1.5), the attachments panel and print
    /// scaling (PDF 1.6) and duplex (PDF 1.7).
    /// </summary>
    public static bool NeedsPdf17(PdfViewerSettings? v)
        => v != null && (v.PageLayout is PdfPageLayout.TwoPageLeft or PdfPageLayout.TwoPageRight
                         || v.PageMode == PdfPageMode.Attachments || v.DisablePrintScaling || v.Duplex != PdfDuplex.Default);

    /// <summary>
    /// Writes /PageLayout, /PageMode, /OpenAction and /ViewerPreferences. <paramref name="displayTitle"/> and
    /// <paramref name="rightToLeft"/> are the preferences the document needs whatever the caller set: PDF/UA's title in
    /// the title bar, and the page order of a right-to-left document. A nonzero <paramref name="openActionId"/> is the
    /// open action written as an object of its own, which a PDF/UA-2 file needs for its structure destination.
    /// </summary>
    public static void Write(ByteBuffer b, PdfViewerSettings? v, bool displayTitle, bool rightToLeft, IReadOnlyList<int> pageIds, int openActionId = 0)
    {
        if (v != null)
        {
            string? layout = v.PageLayout switch
            {
                PdfPageLayout.SinglePage => "SinglePage",
                PdfPageLayout.OneColumn => "OneColumn",
                PdfPageLayout.TwoColumnLeft => "TwoColumnLeft",
                PdfPageLayout.TwoColumnRight => "TwoColumnRight",
                PdfPageLayout.TwoPageLeft => "TwoPageLeft",
                PdfPageLayout.TwoPageRight => "TwoPageRight",
                _ => null,
            };
            if (layout != null) b.Ascii(" /PageLayout /").Ascii(layout);
            string? mode = v.PageMode switch
            {
                PdfPageMode.PagesOnly => "UseNone",
                PdfPageMode.Bookmarks => "UseOutlines",
                PdfPageMode.Thumbnails => "UseThumbs",
                PdfPageMode.Attachments => "UseAttachments",
                PdfPageMode.FullScreen => "FullScreen",
                _ => null,
            };
            if (mode != null) b.Ascii(" /PageMode /").Ascii(mode);
            if (openActionId != 0) b.Ascii(" /OpenAction ").Int(openActionId).Ascii(" 0 R");
            else if (StartPage(v, pageIds.Count) is int start and >= 0)
            {
                b.Ascii(" /OpenAction ");
                Destination(b, pageIds[start], v.Zoom);
            }
            displayTitle |= v.DisplayDocTitle;
        }
        bool any = displayTitle || rightToLeft || v != null && (v.HideToolbar || v.HideMenuBar || v.HideWindowControls
            || v.FitWindow || v.CenterWindow || v.DisablePrintScaling || v.Duplex != PdfDuplex.Default);
        if (!any) return;
        b.Ascii(" /ViewerPreferences <<");
        if (v != null)
        {
            if (v.HideToolbar) b.Ascii(" /HideToolbar true");
            if (v.HideMenuBar) b.Ascii(" /HideMenubar true");
            if (v.HideWindowControls) b.Ascii(" /HideWindowUI true");
            if (v.FitWindow) b.Ascii(" /FitWindow true");
            if (v.CenterWindow) b.Ascii(" /CenterWindow true");
        }
        if (displayTitle) b.Ascii(" /DisplayDocTitle true");
        if (rightToLeft) b.Ascii(" /Direction /R2L");
        if (v != null)
        {
            if (v.DisablePrintScaling) b.Ascii(" /PrintScaling /None");
            string? duplex = v.Duplex switch
            {
                PdfDuplex.Simplex => "Simplex",
                PdfDuplex.FlipShortEdge => "DuplexFlipShortEdge",
                PdfDuplex.FlipLongEdge => "DuplexFlipLongEdge",
                _ => null,
            };
            if (duplex != null) b.Ascii(" /Duplex /").Ascii(duplex);
        }
        b.Ascii(" >>");
    }

    /// <summary>
    /// The page the document opens at, from 0 (a start page past the end is the last page), or -1 when the viewer's own
    /// choice is asked for and no open action is written.
    /// </summary>
    public static int StartPage(PdfViewerSettings? v, int pageCount)
        => v != null && (v.StartPage != 1 || v.Zoom != PdfZoom.Default) && pageCount > 0 ? Math.Min(v.StartPage, pageCount) - 1 : -1;

    /// <summary>
    /// Writes an explicit destination at the top of object <paramref name="targetId"/>, a page or, for a structure
    /// destination, a structure element. A null coordinate or zoom keeps the viewer's own, which is what opening at a page
    /// without a zoom means.
    /// </summary>
    public static void Destination(ByteBuffer b, int targetId, PdfZoom zoom)
        => b.Ascii("[").Int(targetId).Ascii(" 0 R ").Ascii(zoom switch
        {
            PdfZoom.FitPage => "/Fit",
            PdfZoom.FitWidth => "/FitH null",
            PdfZoom.ActualSize => "/XYZ null null 1",
            _ => "/XYZ null null null",
        }).Ascii("]");

    /// <summary>
    /// Writes the caller's page labels as /PageLabels, sorted by start page; of two ranges starting at the same page the
    /// later one wins, and ranges starting past the last page are left out. Writes nothing when no range applies.
    /// </summary>
    /// <exception cref="ArgumentException">A label is null.</exception>
    public static void WriteLabels(ByteBuffer b, IList<PdfPageLabel>? labels, int pageCount, Action<ByteBuffer, string> text)
    {
        if (labels == null || labels.Count == 0) return;
        var ranges = new SortedDictionary<int, PdfPageLabel>();
        foreach (var label in labels)
        {
            if (label == null) throw new ArgumentException("The page labels contain null.", nameof(labels));
            if (label.StartPage <= pageCount) ranges[label.StartPage - 1] = label;
        }
        if (ranges.Count == 0) return;
        b.Ascii(" /PageLabels << /Nums [");
        // The number tree must cover the first page: pages before the first range are numbered as viewers number them anyway.
        if (!ranges.ContainsKey(0)) b.Ascii("0 << /S /D >> ");
        foreach (var (index, label) in ranges)
        {
            b.Int(index).Ascii(" <<");
            string? style = label.Style switch
            {
                PdfPageNumberStyle.Decimal => "D",
                PdfPageNumberStyle.UpperRoman => "R",
                PdfPageNumberStyle.LowerRoman => "r",
                PdfPageNumberStyle.UpperLetters => "A",
                PdfPageNumberStyle.LowerLetters => "a",
                _ => null,
            };
            if (style != null) b.Ascii(" /S /").Ascii(style);
            if (!string.IsNullOrEmpty(label.Prefix))
            {
                b.Ascii(" /P ");
                text(b, label.Prefix);
            }
            if (label.FirstNumber != 1) b.Ascii(" /St ").Int(label.FirstNumber);
            b.Ascii(" >> ");
        }
        b.Ascii("] >>");
    }
}

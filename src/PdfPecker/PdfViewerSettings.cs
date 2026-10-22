namespace PdfPecker;

/// <summary>How a PDF viewer arranges the pages when it opens the document (the catalog's <c>/PageLayout</c>).</summary>
public enum PdfPageLayout
{
    /// <summary>The viewer's own choice (no <c>/PageLayout</c> is written).</summary>
    Default,

    /// <summary>One page at a time.</summary>
    SinglePage,

    /// <summary>The pages in one column, scrolling.</summary>
    OneColumn,

    /// <summary>The pages in two columns, scrolling, with odd-numbered pages on the left.</summary>
    TwoColumnLeft,

    /// <summary>The pages in two columns, scrolling, with odd-numbered pages on the right.</summary>
    TwoColumnRight,

    /// <summary>Two pages at a time, with odd-numbered pages on the left.</summary>
    TwoPageLeft,

    /// <summary>Two pages at a time, with odd-numbered pages on the right, like a book with a cover page.</summary>
    TwoPageRight,
}

/// <summary>Which panel a PDF viewer shows next to the pages when it opens the document (the catalog's <c>/PageMode</c>).</summary>
public enum PdfPageMode
{
    /// <summary>The viewer's own choice (no <c>/PageMode</c> is written).</summary>
    Default,

    /// <summary>The pages only, with no panel open.</summary>
    PagesOnly,

    /// <summary>The bookmarks panel (<c>/UseOutlines</c>).</summary>
    Bookmarks,

    /// <summary>The page thumbnails panel (<c>/UseThumbs</c>).</summary>
    Thumbnails,

    /// <summary>The attachments panel (<c>/UseAttachments</c>).</summary>
    Attachments,

    /// <summary>Full screen, with no menu bar, window controls or panel.</summary>
    FullScreen,
}

/// <summary>The zoom a PDF viewer opens the document at.</summary>
public enum PdfZoom
{
    /// <summary>The viewer's own choice.</summary>
    Default,

    /// <summary>The whole page fits in the window (<c>/Fit</c>).</summary>
    FitPage,

    /// <summary>The page's width fits in the window (<c>/FitH</c>).</summary>
    FitWidth,

    /// <summary>100%.</summary>
    ActualSize,
}

/// <summary>The duplex setting a print dialog starts with (<c>/Duplex</c> in the viewer preferences).</summary>
public enum PdfDuplex
{
    /// <summary>The printer's own setting (no <c>/Duplex</c> is written).</summary>
    Default,

    /// <summary>Print on one side of the paper.</summary>
    Simplex,

    /// <summary>Print on both sides, flipping on the short edge.</summary>
    FlipShortEdge,

    /// <summary>Print on both sides, flipping on the long edge.</summary>
    FlipLongEdge,
}

/// <summary>
/// How a PDF viewer opens and shows the document: the page and zoom it opens at, the page layout, the panel it shows
/// and its window and print dialog settings. Viewers treat these as suggestions, and some (especially browsers) ignore
/// some of them. Every setting's default leaves the choice to the viewer.
/// </summary>
/// <example><code>options.Viewer = new PdfViewerSettings { PageMode = PdfPageMode.Bookmarks, PageLayout = PdfPageLayout.OneColumn, Zoom = PdfZoom.FitWidth };</code></example>
public sealed class PdfViewerSettings
{
    private int _startPage = 1;

    /// <summary>How the viewer arranges the pages. Default: <see cref="PdfPageLayout.Default"/>.</summary>
    public PdfPageLayout PageLayout { get; set; }

    /// <summary>Which panel the viewer shows next to the pages. Default: <see cref="PdfPageMode.Default"/>.</summary>
    public PdfPageMode PageMode { get; set; }

    /// <summary>
    /// The page the viewer opens at, from 1; a number past the last page opens the last page. Default: 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int StartPage
    {
        get => _startPage;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _startPage = value;
        }
    }

    /// <summary>The zoom the viewer opens the document at. Default: <see cref="PdfZoom.Default"/>.</summary>
    public PdfZoom Zoom { get; set; }

    /// <summary>
    /// Show the document's title in the window's title bar instead of the file name. It is always on for a tagged PDF
    /// with a title, as PDF/UA requires. Default: false.
    /// </summary>
    public bool DisplayDocTitle { get; set; }

    /// <summary>Resize the window to fit the first page. Default: false.</summary>
    public bool FitWindow { get; set; }

    /// <summary>Centre the window on the screen. Default: false.</summary>
    public bool CenterWindow { get; set; }

    /// <summary>Hide the viewer's toolbars. Default: false.</summary>
    public bool HideToolbar { get; set; }

    /// <summary>Hide the viewer's menu bar. Default: false.</summary>
    public bool HideMenuBar { get; set; }

    /// <summary>Hide the scroll bars, navigation controls and other window controls, showing only the pages. Default: false.</summary>
    public bool HideWindowControls { get; set; }

    /// <summary>
    /// Start the print dialog at 100% instead of fitting the pages to the paper, so forms and labels print at their
    /// real size. Default: false.
    /// </summary>
    public bool DisablePrintScaling { get; set; }

    /// <summary>The duplex setting the print dialog starts with. Default: <see cref="PdfDuplex.Default"/>.</summary>
    public PdfDuplex Duplex { get; set; }
}

/// <summary>How a page label numbers its pages.</summary>
public enum PdfPageNumberStyle
{
    /// <summary>No number: the label is only the prefix.</summary>
    None,

    /// <summary>1, 2, 3, ...</summary>
    Decimal,

    /// <summary>I, II, III, ...</summary>
    UpperRoman,

    /// <summary>i, ii, iii, ...</summary>
    LowerRoman,

    /// <summary>A, B, ..., Z, AA, BB, ...</summary>
    UpperLetters,

    /// <summary>a, b, ..., z, aa, bb, ...</summary>
    LowerLetters,
}

/// <summary>
/// A range of page labels: the page numbers a PDF viewer shows instead of the physical ones, such as "i" to "iv" for
/// the front matter and "1" onwards after it, or "A-1" for an appendix. A range starts at <see cref="StartPage"/> and
/// runs to the next range's start; pages before the first range are numbered 1, 2, 3, ...
/// </summary>
/// <example><code>options.PageLabels.Add(new PdfPageLabel(1, PdfPageNumberStyle.LowerRoman));
/// options.PageLabels.Add(new PdfPageLabel(5, PdfPageNumberStyle.Decimal));</code></example>
public sealed class PdfPageLabel
{
    /// <summary>Creates a range of page labels.</summary>
    /// <param name="startPage">The physical page the range starts at, from 1.</param>
    /// <param name="style">How the pages are numbered.</param>
    /// <param name="prefix">Text before each page's number, such as "A-"; null for none.</param>
    /// <param name="firstNumber">The number of the range's first page, from 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPage"/> or <paramref name="firstNumber"/> is less than 1.</exception>
    public PdfPageLabel(int startPage, PdfPageNumberStyle style = PdfPageNumberStyle.Decimal, string? prefix = null, int firstNumber = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstNumber, 1);
        StartPage = startPage;
        Style = style;
        Prefix = prefix;
        FirstNumber = firstNumber;
    }

    /// <summary>The physical page the range starts at, from 1.</summary>
    public int StartPage { get; }

    /// <summary>How the pages are numbered.</summary>
    public PdfPageNumberStyle Style { get; }

    /// <summary>Text before each page's number, or null for none.</summary>
    public string? Prefix { get; }

    /// <summary>The number of the range's first page, from 1.</summary>
    public int FirstNumber { get; }
}

namespace PdfPecker.Pdf;

/// <summary>
/// Where the pages of one section's flow start and end. All pages have the same setup unless <c>@page :first</c>,
/// <c>:left</c> or <c>:right</c> rules change the margins or size of some of them: then the section's first page can
/// differ, and the pages after it alternate between two setups, as left and right pages do. Positions are y in the
/// section's flow. The layout engine also lays out the content of a multi-column box in a grid of its own, whose
/// fragments are the columns (Layout/ColumnLayout.cs).
/// </summary>
internal class PageGrid
{
    private readonly PageSetup _first, _odd, _even; // page 0, pages 1, 3, 5 and so on, pages 2, 4, 6 and so on
    private readonly float _h0, _h1, _h2;

    /// <summary>Every page has the same flow height, so positions map to pages exactly as with a single setup.</summary>
    public readonly bool Uniform;

    /// <summary>Some pages have another setup than the first, even with the same flow height.</summary>
    public readonly bool Varies;

    public PageGrid(PageSetup page) : this(page, page, page) { }

    public PageGrid(PageSetup first, PageSetup odd, PageSetup even)
    {
        _first = first;
        _odd = odd;
        _even = even;
        _h0 = first.FlowHeight;
        _h1 = odd.FlowHeight;
        _h2 = even.FlowHeight;
        Uniform = _h0 == _h1 && _h1 == _h2;
        Varies = !ReferenceEquals(first, odd) || !ReferenceEquals(odd, even);
    }

    /// <summary>A grid of fragments that are not pages, which computes their positions itself.</summary>
    protected PageGrid(PageSetup setup, bool uniform) : this(setup, setup, setup)
    {
        Uniform = uniform;
    }

    public PageSetup SetupOf(int page) => page <= 0 ? _first : (page & 1) == 1 ? _odd : _even;

    public virtual float Height(int page) => page <= 0 ? _h0 : (page & 1) == 1 ? _h1 : _h2;

    public virtual float MaxHeight => Math.Max(_h0, Math.Max(_h1, _h2));

    public virtual float MinHeight => Math.Min(_h0, Math.Min(_h1, _h2));

    /// <summary>Where a page starts. With one height this is exactly page times height, as the layout always computed it.</summary>
    public virtual float Start(int page)
    {
        if (Uniform || page <= 0) return page * _h0;
        int k = page - 1;
        return _h0 + (k >> 1) * (_h1 + _h2) + ((k & 1) == 1 ? _h1 : 0);
    }

    /// <summary>The page a y falls on, in float arithmetic, as the layout engine computes it.</summary>
    public virtual int PageOf(float y)
    {
        if (Uniform || y < _h0) return (int)Math.Floor(y / _h0);
        float r = y - _h0, pair = _h1 + _h2;
        int n = (int)Math.Floor(r / pair);
        return 1 + 2 * n + (r - n * pair >= _h1 ? 1 : 0);
    }

    /// <summary>The page a y falls on, in double arithmetic so a huge y gives a huge page rather than an overflow.</summary>
    public virtual double PageOf(double y)
    {
        if (Uniform || y < _h0) return Math.Floor(y / _h0);
        double r = y - _h0, pair = (double)_h1 + _h2;
        double n = Math.Floor(r / pair);
        return 1 + 2 * n + (r - n * pair >= _h1 ? 1 : 0);
    }

    /// <summary>The number of pages content of the given height needs: at least one.</summary>
    public int PagesFor(float contentHeight)
        => Uniform ? PageSection.PagesFor(contentHeight, _h0) : Math.Max(1, (int)PageOf(Math.Max(0, contentHeight - 0.5f) + 0.01) + 1);
}

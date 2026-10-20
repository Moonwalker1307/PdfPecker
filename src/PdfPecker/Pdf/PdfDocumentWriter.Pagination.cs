using PdfPecker.Layout;

namespace PdfPecker.Pdf;

/// <summary>Pagination: the draw ops of each section split over its pages, and the running bands and canvas of each page.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Puts the sections' ops on the pages they fall on, each section's pages after the last section's, and records
    /// which section each page is in and where in the document's one flow each section starts.
    /// </summary>
    private void DistributeOverPages(WriteState doc)
    {
        var sections = doc.Sections;
        var distributor = new PageDistributor(Math.Max(1, MaxPages));
        // The per-page arrays say which section a page is in and where in that section's flow it starts. Anchors go
        // into one space: a section's flow y plus the flow length of the sections before it.
        var pageSection = new List<int>();
        var sectionFirst = new int[sections.Count];
        var sectionBase = new float[sections.Count + 1];
        for (int si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            int sectionPages = distributor.AddSection(section, sectionFirst[si] = pageSection.Count);
            for (int k = 0; k < sectionPages; k++) pageSection.Add(si);
            sectionBase[si + 1] = sectionBase[si] + section.Grid.Start(sectionPages);
        }
        doc.Pages = distributor.Pages;
        doc.PageCount = doc.Pages.Count;
        _page = doc.FirstSetup;
        _sections = sections;
        _pageSection = pageSection.ToArray();
        _sectionFirst = sectionFirst;
        _sectionBase = sectionBase;
        doc.Anchors = MergeAnchors(sections, sectionBase);
    }

    /// <summary>The anchors of all sections, each moved to the document's flow by the length of the sections before it.</summary>
    private static IReadOnlyDictionary<string, float> MergeAnchors(IReadOnlyList<PageSection> sections, float[] sectionBase)
    {
        if (sections.Count <= 1) return sections[0].Anchors;
        var merged = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int si = 0; si < sections.Count; si++)
            foreach (var (key, y) in sections[si].Anchors) merged.TryAdd(key, sectionBase[si] + y);
        return merged;
    }

    /// <summary>
    /// Lays out the running headers and footers per page, now that the page count is known, together with the fixed
    /// boxes and the canvas background of each page. Their ops use y from the top of the content area.
    /// </summary>
    private void LayOutPageBands(WriteState doc, Func<int, int, List<DrawOp>>? pageBands)
    {
        int pageCount = doc.PageCount;
        var bands = doc.Bands = new List<Placed>[pageCount];
        var canvas = doc.Canvas = new List<Placed>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            var list = bands[p] = new List<Placed>();
            canvas[p] = new List<Placed>();
            var section = doc.Sections[_pageSection[p]];
            var fixedOps = section.FixedOps;
            if (pageBands == null && fixedOps == null && section.Canvas == null) continue;
            if (section.Canvas != null) foreach (var op in section.Canvas(p - _sectionFirst[_pageSection[p]])) Flatten(op, canvas[p]);
            if (pageBands != null) foreach (var op in pageBands(p, pageCount)) Flatten(op, list);
            // position: fixed boxes repeat on every page, as in Chrome's print output.
            if (fixedOps != null) foreach (var op in fixedOps) Flatten(op, list);
        }
    }

    /// <summary>
    /// Splits the ops of a document's flow over its pages: an op that crosses a page break goes on every page it
    /// reaches, clipped to that page, or whole on the page where it starts when it cannot be split.
    /// </summary>
    private sealed class PageDistributor
    {
        private readonly int _maxPages;
        private PageGrid _grid = null!;  // the pages of the section being distributed
        private int _pageOffset;         // pages of the sections before the one being distributed
        private Func<int, float>? _footnoteCut; // the space footnote areas take at the bottom of the pages being distributed

        public PageDistributor(int maxPages) => _maxPages = maxPages;

        /// <summary>The ops of each page of the document, by page index.</summary>
        public List<List<Placed>> Pages { get; } = new();

        /// <summary>
        /// Distributes a section's ops, and then its footnotes, over the pages from <paramref name="firstPage"/> on.
        /// Returns the number of pages the section takes: as many as its content needs, and at least one.
        /// </summary>
        public int AddSection(PageSection section, int firstPage)
        {
            _grid = section.Grid;
            _pageOffset = firstPage;
            _footnoteCut = section.FootnoteReserve;
            foreach (var op in section.Ops) Distribute(op, null);
            _footnoteCut = null;
            if (section.FootnoteOps != null) foreach (var op in section.FootnoteOps) Distribute(op, null);
            int sectionPages = Math.Max(Pages.Count - firstPage, Math.Max(1, PageOf(Math.Max(0, section.ContentHeight - 0.5f)) + 1));
            while (Pages.Count < firstPage + sectionPages) Pages.Add(new List<Placed>());
            return sectionPages;
        }

        /// <summary>The page, within the section, that flow position <paramref name="y"/> is on.</summary>
        private int PageOf(float y)
        {
            // Compare as doubles: content far down the flow (a huge height or margin) must hit the page limit, not
            // overflow the page number.
            double p = _grid.PageOf(y + 0.01);
            if (p + _pageOffset >= _maxPages || double.IsNaN(p)) throw TooManyPages(_maxPages);
            return Math.Max(0, (int)p);
        }

        private List<Placed> PageList(int p)
        {
            // Sized like the page before: pages of a document tend to hold about as many ops each.
            while (Pages.Count <= p) Pages.Add(new List<Placed>(Pages.Count > 0 ? Math.Max(16, Pages[^1].Count) : 16));
            return Pages[p];
        }

        /// <summary>The list of a page: the document's own, or, for a transformed layer, the layer's part on that page.</summary>
        private List<Placed> Target(SortedDictionary<int, List<Placed>>? into, int p)
        {
            if (into == null) return PageList(_pageOffset + p);
            if (!into.TryGetValue(p, out var list)) into[p] = list = new List<Placed>();
            return list;
        }

        /// <summary>Adds an op to every page its vertical extent reaches, clipped to the part on that page.</summary>
        private void AddSplit(DrawOp op, float top, float bottom, SortedDictionary<int, List<Placed>>? into)
        {
            if (bottom < top) (top, bottom) = (bottom, top);
            int p0 = PageOf(top), p1 = Math.Max(p0, PageOf(bottom - 0.5f));
            for (int p = p0; p <= p1; p++)
            {
                float y0 = Math.Max(top, _grid.Start(p)), y1 = Math.Min(bottom, _grid.Start(p + 1) - (_footnoteCut?.Invoke(p) ?? 0));
                if (y1 > y0 || top == bottom) Target(into, p).Add(new Placed(op, y0, y1));
            }
        }

        private void Distribute(DrawOp op, SortedDictionary<int, List<Placed>>? into)
        {
            switch (op)
            {
                case LayerOp { NeedsBlock: true } layer:
                {
                    // Each page gets the part of the layer that falls on it, drawn with the whole layer's transform.
                    var parts = new SortedDictionary<int, List<Placed>>();
                    foreach (var o in layer.Ops) Distribute(o, parts);
                    foreach (var (p, items) in parts) Target(into, p).Add(new Placed(new LayerBlock(layer, items), 0, 0));
                    break;
                }
                case ClipOp clip:
                {
                    // Content hidden beyond the clip's last page must not add pages of its own.
                    var parts = new SortedDictionary<int, List<Placed>>();
                    foreach (var o in clip.Ops) Distribute(o, parts);
                    bool clipsY = clip.H < ClipOp.Far;
                    int first = clipsY ? PageOf(clip.Y) : 0, last = clipsY ? Math.Max(first, PageOf(clip.Y + clip.H - 0.5f)) : int.MaxValue;
                    // Content starting above the clip's first page, such as an image that object-fit: none centres in a
                    // smaller box, shows only through the clip, so it is drawn with the clip on that page.
                    List<Placed>? above = null;
                    foreach (var (p, items) in parts)
                    {
                        if (p < first)
                        {
                            (above ??= new List<Placed>()).AddRange(items);
                            continue;
                        }
                        if (p > last) continue;
                        if (p == first && above != null)
                        {
                            items.InsertRange(0, above);
                            above = null;
                        }
                        Target(into, p).Add(new Placed(new ClipBlock(clip, items), 0, 0));
                    }
                    if (above != null && first <= last) Target(into, first).Add(new Placed(new ClipBlock(clip, above), 0, 0));
                    break;
                }
                case FieldOp field:
                {
                    // A field stays whole, on the page where it starts.
                    var items = new List<Placed>();
                    foreach (var o in field.Ops) Flatten(o, items);
                    Target(into, PageOf(field.Y)).Add(new Placed(new FieldBlock(field, items), field.Y, field.Y + field.H));
                    break;
                }
                case GroupOp g:
                    foreach (var o in g.Ops) Distribute(o, into);
                    break;
                case RectOp r:
                    if (r.W > 0 && r.H > 0) AddSplit(r, r.Y, r.Y + r.H, into);
                    break;
                case RoundRectOp rr:
                    if (rr.W > 0 && rr.H > 0) AddSplit(rr, rr.Y, rr.Y + rr.H, into); // only the outer shape is painted
                    break;
                case BorderRectOp br:
                    if (PageOf(br.Y) == PageOf(br.Y + br.H - 0.5f)) Target(into, PageOf(br.Y)).Add(new Placed(br, br.Y, br.Y + br.H));
                    else foreach (var side in br.Sides()) Distribute(side, into); // a box split by a page break is open at the break
                    break;
                case BackgroundOp bg:
                {
                    var (x0, y0, x1, y1) = bg.Painted();
                    if (x1 > x0 && y1 > y0) AddSplit(bg, y0, y1, into);
                    break;
                }
                case LineOp l:
                    if (l.X1 == l.X2) AddSplit(l, l.Y1, l.Y2, into);
                    else Target(into, PageOf(l.Y1)).Add(new Placed(l, l.Y1, l.Y2));
                    break;
                case TextOp t:
                    Target(into, PageOf(t.LineTop)).Add(new Placed(t, 0, 0));
                    break;
                case ImageOp im:
                    Target(into, PageOf(im.Y)).Add(new Placed(im, 0, 0));
                    break;
                case SvgOp sv:
                    Target(into, PageOf(sv.Y)).Add(new Placed(sv, 0, 0));
                    break;
                case LinkOp lk:
                    if (lk.W > 0 && lk.H > 0) AddSplit(lk, lk.Y, lk.Y + lk.H, into); // a link box split by a page break is clickable on both pages
                    break;
                case CircleOp c:
                    Target(into, PageOf(c.Cy - c.R)).Add(new Placed(c, 0, 0));
                    break;
            }
        }
    }
}

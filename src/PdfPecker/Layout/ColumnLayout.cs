using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Pdf;

namespace PdfPecker.Layout;

/// <summary>
/// Multi-column layout (column-count / column-width / columns). The content between column-span: all boxes is laid out
/// as one flow in a <see cref="ColumnGrid"/>, whose fragments are the columns, with the same pagination that splits
/// lines, boxes and table rows between pages: a box that does not fit the rest of a column continues in the next one,
/// as in Chrome. The columns on one page form a row as tall as the room left on it. With column-fill: balance (the
/// default) the last row's columns get the smallest height that holds the rest of the content, found by laying the
/// content out again with taller columns until it fits; with column-fill: auto they are filled in turn.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Balancing lays a box's content out several times, and a multi-column box inside it each time again, so a box
    // nested this deep in others keeps its children whole and balances their heights instead.
    private const int MaxBalancedNesting = 2;

    // The most layouts that balancing a row of columns tries before it settles for the best one found.
    private const int MaxBalancePasses = 24;

    // The most box layouts that balancing one row of columns spends on its passes. Each pass lays the row's content out
    // again, so content that is expensive to lay out gets fewer passes: a column of nested tables around a flex row of
    // two thousand items took 17 passes of 23,000 layouts each. A row of ordinary text costs a few hundred per pass.
    private const long MaxBalanceLayouts = 200_000;

    // A box split between more columns than this is drawn in the first ones only (a huge height in tiny columns).
    private const int MaxSplitFragments = 4096;

    // Columns narrower than this (one CSS pixel) are laid out as a single column.
    private const float MinColumnWidth = 0.75f;

    // The widest a PDF page can be, in points. Overflow columns further than this beside a box are on no page.
    private const float MaxPageExtent = 14_400;

    // Without pages or a height, columns start this tall, so that only forced breaks end them; taller content would
    // break there too.
    private const float UnboundedColumn = 100_000;

    private int _columnDepth;
    private ColumnNotes? _columnNotes;      // the breaks of the column layout in progress, for balancing
    private (int From, int To)? _flowRange; // the children the next LayoutFlow lays out
    private List<FlowResume>? _flowMarks;   // where the next LayoutFlow notes that a later pass could go on from
    private FlowResume? _flowResume;        // where the next LayoutFlow goes on from

    /// <summary>
    /// A point between two children of a multi-column box where nothing laid out before it reaches past it: no float,
    /// no keep with the box after it and no absolutely positioned box waiting. A balancing pass whose columns before
    /// this point are as in the pass that noted it keeps that pass's content up to here and lays out only the rest.
    /// </summary>
    private readonly record struct FlowResume(FlowMark Mark, int Anchors);

    /// <summary>Whether a later pass could go on from y between two children of the flow being laid out.</summary>
    private bool CanResumeAt(float y) => !FloatsBelow(y) && _frames[^1].Pending.Count == 0;

    // column-count is capped at this many when parsed; a narrow column-width on a very wide box must not give more, or
    // balancing would spread the content over a hundred million columns.
    private const int MaxColumns = 100;

    private static int ColumnCountFor(ComputedStyle s, float w, float gap)
    {
        int n = s.ColumnCount;
        if (!s.ColumnWidth.IsAuto)
        {
            float cw = Math.Max(1, s.ColumnWidth.Value);
            int fit = (int)Math.Clamp(Math.Floor((w + gap) / (cw + gap)), 1, MaxColumns);
            n = n > 0 ? Math.Min(n, fit) : fit;
        }
        return Math.Clamp(n, 1, MaxColumns);
    }

    private float LayoutColumns(Element e, float x, float y, float w, float? definiteH)
    {
        var s = e.Style;
        float gap = s.ColumnGap is { } g ? Math.Max(0, g.Resolve(w)) : s.FontSize; // normal = 1em
        int n = ColumnCountFor(s, w, gap);
        if (n <= 1) return LayoutFlow(e, x, y, w);
        float colW = Math.Max(0, (w - (n - 1) * gap) / n);
        // Columns narrower than a pixel hold nothing readable, and each nested multi-column box divides the width
        // again: laid out as columns, every level copied all the content inside it, so hundreds of nested boxes took
        // the square of their depth. They are laid out as one column, which bounds the nesting to a few dozen levels.
        if (colW < MinColumnWidth) return LayoutFlow(e, x, y, w);
        if (_columnDepth >= MaxBalancedNesting) return LayoutWholeColumns(e, x, y, w, n, colW, gap);
        // The columns are as wide on every page: the page geometry does not reach into them.
        _pageBox = null;
        _columnDepth++;
        try
        {
            var children = e.Children;
            bool spanners = false;
            foreach (var child in children)
                if (child is Element ce && IsSpanner(ce)) { spanners = true; break; }
            float cy = y;
            int from = 0;
            if (spanners)
            {
                // An element with column-span: all ends the columns before it, spans the whole width, and starts a
                // new set of columns after it. A height for the whole box does not apply to each set.
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i] is not Element ce || !IsSpanner(ce)) continue;
                    cy = LayoutColumnSet(e, from, i, x, cy, n, colW, gap, null);
                    cy = LayoutSpanner(ce, x, cy, w);
                    from = i + 1;
                }
                definiteH = null;
            }
            cy = LayoutColumnSet(e, from, children.Count, x, cy, n, colW, gap, definiteH);
            _lastBoxRight = Math.Max(_lastBoxRight, x + w);
            return cy;
        }
        finally
        {
            _columnDepth--;
        }
    }

    private static bool IsSpanner(Element e)
        => e.Style is { ColumnSpanAll: true, Floated: false, OutOfFlow: false } s && s.Display != Display.None && !e.Detached && !IsInlineLevel(e);

    /// <summary>Lays out a column-span: all box across the whole width at y. Returns the bottom of its margin box.</summary>
    private float LayoutSpanner(Element e, float x, float y, float w)
    {
        var s = e.Style;
        float top = y + LeadingMargin(e, w).With(s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(w)).Value;
        if (s.PageBreakBefore) top = NextPage(top);
        float bottom = LayoutBlockBox(e, x, top, w, shrinkToFit: false);
        float mb = _boxTrailing.With(s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(w)).Value;
        return s.PageBreakAfter ? NextPage(bottom + mb) : bottom + mb;
    }

    /// <summary>
    /// A forced break before or after a box: to the next page, or with break-before or break-after: column inside a
    /// multi-column box, to the next column.
    /// </summary>
    private float ForcedBreak(float y)
    {
        if (_paginate && !AtPageTop(y)) _columnNotes?.Break(PageOf(y), y, 0, forced: true);
        return NextPage(y);
    }

    /// <summary>
    /// Where a box goes in a column whose content before it ended at y, given its top margin edge: a margin that
    /// reaches past the end of a column, or follows content that ended there, is cut off at the break, as Chrome cuts
    /// it. After a forced break the margin stays.
    /// </summary>
    private float TruncatedMargin(float y, float top)
    {
        if (top <= y) return top;
        int k = PageOf(y);
        if (PageOf(top) > k) return _grid.Start(k + 1);
        return k > 0 && AtPageTop(y) && _columnNotes is { } notes && !notes.ForcedAt(k - 1) ? y : top;
    }

    /// <summary>Whether children[from..to) hold anything to lay out.</summary>
    private static bool HasColumnContent(Element e, int from, int to)
    {
        var children = e.Children;
        for (int i = from; i < to; i++)
        {
            switch (children[i])
            {
                case TextNode t when !string.IsNullOrWhiteSpace(t.Text):
                    return true;
                case Element ce when ce.Style != null && ce.Style.Display != Display.None && !ce.Detached:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lays out children[from..to) of a multi-column box in n columns from y. Returns the bottom of the columns, which
    /// leaves out the bottom margin of the last box in them, as Chrome does.
    /// </summary>
    private float LayoutColumnSet(Element e, int from, int to, float x, float y, int n, float colW, float gap, float? definiteH)
    {
        if (!HasColumnContent(e, from, to)) return y;
        var s = e.Style;
        bool balance = !s.ColumnFillAuto;
        float limit = definiteH is { } dh ? Math.Max(0, dh) : float.PositiveInfinity;
        var outer = _grid;
        bool paginated = _paginate;
        int page = paginated ? PageOf(y) : 0;
        float room = paginated ? Math.Max(0, FlowEnd(page) - y) : float.PositiveInfinity;

        float[] rows;
        bool overflows;
        if (!paginated && float.IsPositiveInfinity(limit))
        {
            // Nothing limits the columns' height: with column-fill: auto everything goes in the first column.
            if (!balance)
            {
                _flowRange = (from, to);
                float end = LayoutFlow(e, x, y, colW);
                return end - _flowTrailing.Value;
            }
            // Balancing starts from the content's height in columns that only forced breaks end.
            rows = new[] { UnboundedColumn };
            overflows = true;
        }
        else rows = FillRows(outer, page, room, limit, paginated, out overflows);
        var grid = new ColumnGrid(outer, page, y, n, rows, overflows);
        var fill = RunColumnPass(e, from, to, x, colW, grid, 0, noteMarks: true);
        if (paginated && !AtPageTop(y) && fill.Notes!.FirstViolation < n)
        {
            // The columns on the rest of this page would leave lines alone that orphans keeps together: as in Chrome
            // the columns start on the next page instead.
            y = NextPage(y);
            page = PageOf(y);
            rows = FillRows(outer, page, Math.Max(0, FlowEnd(page) - y), limit, paginated, out overflows);
            grid = new ColumnGrid(outer, page, y, n, rows, overflows);
            fill = RunColumnPass(e, from, to, x, colW, grid, 0, noteMarks: true);
        }

        int endK = EndFragment(fill, grid);
        int row = grid.RowOf(endK);
        var best = (Pass: fill, Grid: grid, Height: Math.Min(grid.RowHeight(row), Tallest(fill, grid, row, endK)));
        if (!balance && endK == grid.FirstFragmentOf(row)) best.Height = fill.End - grid.Start(endK);
        else if (balance && endK < grid.FirstFragmentOf(row) + n) best = Balance(e, from, to, x, colW, best, row, endK);

        var (pass, bestGrid, rowH) = best;
        float bottom = PlaceColumns(pass, bestGrid, row, rowH, s, x, n, colW, gap);
        return bottom;
    }

    /// <summary>
    /// The column height of each row for filling the columns in turn: the room left on the first page and then whole
    /// pages, all within the box's height when it has one. Rows past the last given go on with the outer grid's pages.
    /// </summary>
    private static float[] FillRows(PageGrid outer, int page, float room, float limit, bool paginated, out bool lastOverflows)
    {
        var rows = new List<float>(2);
        float left = limit;
        float h = Math.Min(room, left);
        rows.Add(h);
        left -= h;
        lastOverflows = !paginated || left <= Eps;
        // Columns inside outer columns that have no height from here on (a box shorter than its own borders) would
        // follow them through as many rows as the arithmetic allows, and content never fits in any of them: they
        // overflow beside the first row instead.
        if (!lastOverflows && outer is ColumnGrid around && around.TallestFrom(page + 1) <= Eps) lastOverflows = true;
        // A height of many pages ends in columns that overflow beside the last of those given; that is far enough.
        if (!lastOverflows && !float.IsPositiveInfinity(limit))
        {
            for (int j = 1; ; j++)
            {
                h = Math.Min(ColumnGrid.FragmentHeight(outer, page + j), left);
                rows.Add(h);
                left -= h;
                if (left <= Eps || rows.Count >= 64) { lastOverflows = true; break; }
            }
        }
        return rows.ToArray();
    }

    /// <summary>The fragment the content of a pass ends in.</summary>
    private static int EndFragment(ColumnPass p, ColumnGrid g) => p.End <= 0 ? 0 : Math.Max(0, g.PageOf(p.End - Eps));

    /// <summary>The tallest content among the columns of a row up to the column the content ends in.</summary>
    private static float Tallest(ColumnPass p, ColumnGrid g, int row, int endK)
    {
        float tallest = 0;
        for (int k = g.FirstFragmentOf(row); k <= endK; k++)
        {
            float start = g.Start(k);
            float used = k == endK ? p.End - start : (p.Notes?.EndOf(k) is { } end ? end - start : g.OwnHeight(k));
            tallest = Math.Max(tallest, used);
        }
        return tallest;
    }

    /// <summary>
    /// Balances the last row of columns as Chrome does. The first height tried comes from the content in the row: its
    /// runs between forced breaks, with the columns left over given one by one to the run whose columns are then
    /// tallest. While the content does not fit (or leaves fewer lines than orphans at a column's end that taller
    /// columns would keep together), the height grows by the least that lets some content move back into a column,
    /// the smallest space shortage of the breaks, or else halfway to a height known to fit. Returns the layout to keep,
    /// its grid and the row's height.
    /// </summary>
    private (ColumnPass Pass, ColumnGrid Grid, float Height) Balance(Element e, int from, int to, float x, float colW,
        (ColumnPass Pass, ColumnGrid Grid, float Height) fill, int row, int endK)
    {
        var (fillPass, fillGrid, fillHeight) = fill;
        int n = fillGrid.Columns, first = fillGrid.FirstFragmentOf(row);
        float h = InitialColumnHeight(fillPass, fillGrid, first, endK, n);
        if (float.IsNaN(h)) return fill;
        // Content that already fits the first height keeps it, with the bottom margin it counts, as in Chrome.
        if (h >= fillHeight - 0.01f) return (fillPass, fillGrid, Math.Min(h, fillGrid.RowHeight(row)));

        // The rows before the last keep their heights, so their content breaks as it did; the rows after it, there for
        // content that does not fit, are as they were too.
        var rows = new float[Math.Max(fillGrid.GivenRows, row + 1)];
        for (int j = 0; j < rows.Length; j++) rows[j] = fillGrid.RowHeight(j);
        bool overflows = fillGrid.IsLastRow(rows.Length - 1);
        // Only the last row changes, so each pass keeps what the filling pass laid out before the last child that
        // starts before the row and lays out the rest again.
        FlowResume? resume = null;
        if (first > 0 && fillPass.Marks != null)
        {
            float rowStart = fillGrid.Start(first) + Eps;
            foreach (var m in fillPass.Marks)
            {
                if (m.Mark.Y > rowStart) break;
                resume = m;
            }
        }
        float hi = fillHeight;
        long start = _boxLayouts, passCost = 0;
        for (int pass = 0; pass < MaxBalancePasses && h < hi - 0.01f; pass++)
        {
            Cancellation.ThrowIfCancellationRequested();
            // Another pass costs about what the last one did. What a pass costs depends on what it could reuse, which
            // depends on how deeply the columns are nested, so the columns kept then may too.
            if (_boxLayouts - start + passCost > MaxBalanceLayouts)
            {
                _depthDependentLayouts++;
                break;
            }
            long before = _boxLayouts;
            rows[row] = h;
            var grid = new ColumnGrid(fillGrid.Outer, fillGrid.FirstPage, fillGrid.Top, n, (float[])rows.Clone(), overflows);
            // The content before the row asks how tall the columns after it are, so it lays out as before only while
            // the tallest of them is the same.
            bool same = Math.Abs(grid.Height(first) - fillGrid.Height(first)) < Eps;
            var p = RunColumnPass(e, from, to, x, colW, grid, first, earlier: fillPass, resume: same ? resume : null);
            passCost = _boxLayouts - before;
            if (EndFragment(p, grid) < first + n && !p.Notes!.Violated) return (p, grid, h);
            float shortage = p.Notes!.MinShortage;
            h = float.IsPositiveInfinity(shortage) ? (h + hi) / 2 : h + Math.Max(shortage, 0.05f);
        }
        return fill;
    }

    /// <summary>
    /// The first column height to balance with: the content of the row's columns in the layout that filled them,
    /// split into runs at forced breaks, each run over as many columns as the others leave it. NaN when the forced
    /// breaks alone take more columns than there are.
    /// </summary>
    private static float InitialColumnHeight(ColumnPass fill, ColumnGrid g, int first, int endK, int n)
    {
        var runs = new List<float>(1);
        float run = 0;
        for (int k = first; k <= endK; k++)
        {
            float start = g.Start(k);
            if (k == endK)
            {
                // The last box's bottom margin counts here, though it may be cut off at the end of the last column.
                runs.Add(run + fill.EndWithMargin - start);
                break;
            }
            run += (fill.Notes?.EndOf(k) is { } end ? end : start + g.OwnHeight(k)) - start;
            if (fill.Notes != null && fill.Notes.ForcedAt(k))
            {
                runs.Add(run);
                run = 0;
            }
        }
        if (runs.Count > n) return float.NaN;
        var breaks = new int[runs.Count];
        for (int extra = n - runs.Count; extra > 0; extra--)
        {
            int tallest = 0;
            for (int r = 1; r < runs.Count; r++)
                if (runs[r] / (breaks[r] + 1) > runs[tallest] / (breaks[tallest] + 1)) tallest = r;
            breaks[tallest]++;
        }
        float h = 0;
        for (int r = 0; r < runs.Count; r++) h = Math.Max(h, runs[r] / (breaks[r] + 1));
        // Chrome's first height is never less than the tallest box that break-inside keeps whole: growing from a lower
        // one by the shortages of the breaks would either split that box or overshoot the height that holds it.
        if (fill.Notes != null) h = Math.Max(h, fill.Notes.TallestUnbreakable(first, endK));
        return Math.Max(0.5f, h);
    }

    /// <summary>The lines of a paragraph placed in one column so far.</summary>
    private struct LineChunk
    {
        public int Start, Lines, Fragment;
        public float Top;
    }

    /// <summary>
    /// Notes a paragraph's lines as they are placed in columns being balanced. Lines that start a column and are fewer
    /// than orphans before the next break could not move back: Chrome then makes the columns taller, by at least what
    /// the missing lines need, rather than leave them.
    /// </summary>
    private void NoteOrphans(ref LineChunk chunk, int li, float y, float[] lineA, float[] lineD, int n, int orphans)
    {
        int k = PageOf(y);
        if (chunk.Start >= 0 && k != chunk.Fragment)
        {
            if (chunk.Lines < orphans)
            {
                float need = 0;
                int count = 0;
                for (int i = chunk.Start; i < n && count < orphans; i++)
                {
                    if (float.IsNaN(lineA[i])) continue;
                    need += lineA[i] + lineD[i];
                    count++;
                }
                if (count > chunk.Lines) _columnNotes!.Violation(chunk.Fragment, chunk.Top + need - FlowEnd(chunk.Fragment));
            }
            chunk.Start = -1;
        }
        if (chunk.Start < 0) chunk = new LineChunk { Start = li, Fragment = k, Top = y };
        chunk.Lines++;
    }

    /// <summary>
    /// Notes a break that orphans or widows move before line li, ahead of where the lines would break by themselves:
    /// the column falls short there by what the first line that does not fit needs.
    /// </summary>
    private void NotePlannedBreak(int li, float y, float[] lineA, float[] lineD, int n)
    {
        if (AtPageTop(y)) return;
        int k = PageOf(y);
        float end = FlowEnd(k), at = y;
        for (int i = li; i < n; i++)
        {
            if (float.IsNaN(lineA[i])) continue;
            float h = lineA[i] + lineD[i];
            if (at + h > end + Eps)
            {
                _columnNotes!.Break(k, y, at + h - end);
                return;
            }
            at += h;
        }
    }

    /// <summary>
    /// The layout of a column set's content in one grid of columns, in the grid's own coordinates: x as on the page, y
    /// from 0 at the top of the first column.
    /// </summary>
    private sealed class ColumnPass
    {
        public List<DrawOp> Ops = null!;
        public float End;                                       // the bottom of the content, without a last bottom margin
        public float EndWithMargin;                             // and with it
        public List<(string Id, float Y)>? Anchors;
        public List<(Element E, float X, float Y)>? Absolutes;  // still waiting for a containing block outside
        public ColumnNotes? Notes;
        public ListMarker? Marker;                              // a list marker not drawn yet
        public int ListIndex;
        public List<FlowResume>? Marks;                         // where later passes can go on from
        public List<string>? AnchorIds;                         // the anchors set, in order
    }

    /// <summary>The breaks of a column layout: where the content of each column ends, and the smallest shortage.</summary>
    private sealed class ColumnNotes(int from)
    {
        private readonly Dictionary<int, float> _ends = new();

        /// <summary>The smallest amount by which a piece of content missed fitting its column, from column <c>from</c> on.</summary>
        public float MinShortage = float.PositiveInfinity;

        /// <summary>Set while lines try breaks that are not taken (orphans and widows).</summary>
        public bool Paused;

        private HashSet<int>? _forced;

        /// <summary>Content moved from column k on to the next one at y, missing by <paramref name="shortage"/> (0 for a forced or avoided break).</summary>
        public void Break(int k, float y, float shortage, bool forced = false)
        {
            if (Paused || k < from) return;
            if (!_ends.TryGetValue(k, out float end) || y > end) _ends[k] = y;
            if (shortage > Eps && shortage < MinShortage) MinShortage = shortage;
            if (forced) (_forced ??= new HashSet<int>()).Add(k);
        }

        /// <summary>
        /// Content of height h starting exactly at the top of column k, where the content before it ended: the column
        /// before would have held it too if it were h taller, unless a break was forced there.
        /// </summary>
        public void Exact(int k, float y, float h, PageGrid grid)
        {
            if (Paused || k <= from || Math.Abs(y - grid.Start(k)) > Eps || ForcedAt(k - 1) || _ends.ContainsKey(k - 1)) return;
            Break(k - 1, y, h);
        }

        /// <summary>Whether a forced break ended column k.</summary>
        public bool ForcedAt(int k) => _forced != null && _forced.Contains(k);

        /// <summary>How many columns a forced break ended so far; layout reads those breaks (see TruncatedMargin).</summary>
        public int ForcedCount => _forced?.Count ?? 0;

        /// <summary>Whether a break from column <c>from</c> on left fewer lines than orphans asks for, which taller columns could fix.</summary>
        public bool Violated;

        /// <summary>The first column with such a break.</summary>
        public int FirstViolation = int.MaxValue;

        /// <summary>A break in column k that taller columns, by <paramref name="shortage"/>, would avoid.</summary>
        public void Violation(int k, float shortage)
        {
            if (Paused || k < from) return;
            Violated = true;
            FirstViolation = Math.Min(FirstViolation, k);
            if (shortage > Eps && shortage < MinShortage) MinShortage = shortage;
        }

        public float? EndOf(int k) => _ends.TryGetValue(k, out float end) ? end : null;

        private Dictionary<int, float>? _unbreakable;

        /// <summary>A box of height h that break-inside: avoid or avoid-column keeps whole, starting in column k.</summary>
        public void Unbreakable(int k, float h)
        {
            if (Paused || k < from) return;
            _unbreakable ??= new Dictionary<int, float>();
            if (!_unbreakable.TryGetValue(k, out float tallest) || h > tallest) _unbreakable[k] = h;
        }

        /// <summary>The tallest box that must stay whole among those starting in columns first to last.</summary>
        public float TallestUnbreakable(int first, int last)
        {
            if (_unbreakable == null) return 0;
            float tallest = 0;
            foreach (var (k, h) in _unbreakable)
                if (k >= first && k <= last) tallest = Math.Max(tallest, h);
            return tallest;
        }
    }

    /// <summary>
    /// Lays out children[from..to) of e in the grid of columns, or with no grid in one column without pages, on their
    /// own like captured content, and returns the layout without changing the output.
    /// </summary>
    private ColumnPass RunColumnPass(Element e, int from, int to, float x, float colW, ColumnGrid? grid, int notesFrom,
        bool noteMarks = false, ColumnPass? earlier = null, FlowResume? resume = null)
    {
        var savedOps = _ops;
        var savedGrid = _grid;
        bool savedPaginate = _paginate;
        var savedMarker = _marker;
        int savedList = _currentListIndex;
        float savedBaseline = _lastBaseline;
        var savedAnchors = _captureAnchors;
        bool savedCapturing = _capturing;
        float savedRight = _lastBoxRight;
        var savedNotes = _columnNotes;
        var savedFloats = EnterFormattingContext();
        var boundary = _boundaryPool.TryPop(out var pooled) ? pooled : new CbFrame { Boundary = true };
        _frames.Add(boundary);
        _ops = resume is { } r ? earlier!.Ops.GetRange(0, r.Mark.OpsCount) : new List<DrawOp>();
        if (grid != null) _grid = grid;
        _paginate = grid != null;
        _columnNotes = grid != null ? new ColumnNotes(notesFrom) : null;
        _lastBaseline = float.NaN;
        _captureAnchors = resume is { Anchors: > 0 } ra ? earlier!.AnchorIds!.GetRange(0, ra.Anchors) : null;
        _capturing = true;
        _pageBox = null;
        _absorbTop = false;
        _flowRange = (from, to);
        _flowResume = resume;
        var marks = noteMarks ? new List<FlowResume>() : null;
        _flowMarks = marks;
        try
        {
            float withMargin = LayoutFlow(e, x, 0, colW), end = withMargin - _flowTrailing.Value;
            if (_floats.Any && _floats.Bottom > end) end = _floats.Bottom;
            List<(string, float)>? anchors = null;
            if (_captureAnchors != null)
            {
                anchors = new List<(string, float)>(_captureAnchors.Count);
                foreach (var id in _captureAnchors)
                    if (Anchors.TryGetValue(id, out float ay)) anchors.Add((id, ay));
            }
            return new ColumnPass
            {
                Ops = _ops, End = Math.Max(0, end), EndWithMargin = Math.Max(end, withMargin), Anchors = anchors, Notes = _columnNotes,
                Absolutes = boundary.Pending.Count > 0 ? boundary.TakePending() : null,
                Marker = _marker, ListIndex = _currentListIndex, Marks = marks, AnchorIds = _captureAnchors,
            };
        }
        finally
        {
            _frames.Remove(boundary);
            boundary.TakePending();
            _boundaryPool.Push(boundary);
            LeaveFormattingContext(savedFloats, 0);
            _ops = savedOps;
            _grid = savedGrid;
            _paginate = savedPaginate;
            _marker = savedMarker;
            _currentListIndex = savedList;
            _lastBaseline = savedBaseline;
            _captureAnchors = savedAnchors;
            _capturing = savedCapturing;
            _lastBoxRight = savedRight;
            _columnNotes = savedNotes;
            _flowRange = null;
            _flowResume = null;
            _flowMarks = null;
        }
    }

    /// <summary>
    /// Moves the ops of a pass into their columns and appends them to the output, with the anchors, absolutely
    /// positioned boxes and column rules. Returns the bottom of the last row, <paramref name="lastHeight"/> tall.
    /// </summary>
    private float PlaceColumns(ColumnPass p, ColumnGrid g, int lastRow, float lastHeight, ComputedStyle s, float x, int n, float colW, float gap)
    {
        float step = colW + gap;
        bool rtl = s.Rtl;
        var parts = new SortedDictionary<int, List<DrawOp>>();
        int lastK = 0;
        // Overflow columns beside the last row that are further away than any page is wide show nothing, so what falls
        // in them is dropped rather than split between them. A row of columns half a point tall, left at the bottom of
        // a column around the box, split each of 3,000 boxes 40 mm tall into 150 copies, nearly half a million in all.
        int last = g.GivenRows - 1;
        if (last >= 0 && g.IsLastRow(last))
        {
            long beside = step > 0 ? (long)Math.Min(int.MaxValue, Math.Ceiling(MaxPageExtent / step)) : 0;
            g.Hidden = (int)Math.Min(int.MaxValue, g.FirstFragmentOf(last) + (long)n + beside);
        }
        var dropped = new List<DrawOp>();
        List<DrawOp> Into(int k)
        {
            if (k >= g.Hidden)
            {
                dropped.Clear();
                return dropped;
            }
            if (!parts.TryGetValue(k, out var list)) parts[k] = list = new List<DrawOp>();
            return list;
        }
        foreach (var op in p.Ops) Distribute(op, g, Into, ref lastK);

        // The number of columns used in each row, for the rules between them.
        var used = new int[lastRow + 1];
        foreach (var (k, ops) in parts)
        {
            var (dx, dy) = FragmentOffset(g, k, n, step, rtl);
            foreach (var op in ops)
            {
                op.Offset(dx, dy);
                _ops.Add(op);
            }
            int row = g.RowOf(k);
            if (row <= lastRow && ops.Count > 0) used[row] = Math.Max(used[row], (int)Math.Min(int.MaxValue, g.ColumnOf(k) + 1));
        }
        if (p.Anchors != null)
            foreach (var (id, ay) in p.Anchors)
                SetAnchor(id, ay + FragmentOffset(g, g.PageOf(ay + Eps), n, step, rtl).Dy);
        if (p.Absolutes != null)
            foreach (var (ae, ax, ay) in p.Absolutes)
            {
                var (dx, dy) = FragmentOffset(g, g.PageOf(ay + Eps), n, step, rtl);
                QueueAbsolute(ae, ax + dx, ay + dy);
            }
        _marker = p.Marker;
        _currentListIndex = p.ListIndex;

        float bottom = g.RowTop(lastRow) + lastHeight;
        for (int row = 0; row <= lastRow; row++)
        {
            float top = g.RowTop(row), h = row == lastRow ? lastHeight : g.RowHeight(row);
            ColumnRules(s, used[row], x, top, top + h, colW, gap, n, rtl);
            // A box around this one that balances its own columns learns where this one's content ends in them.
            if (row < lastRow && _paginate) _columnNotes?.Break(g.FirstPage + row, top + h, 0);
        }
        return bottom;
    }

    /// <summary>How far content laid out in fragment k of the grid moves to reach its column.</summary>
    private static (float Dx, float Dy) FragmentOffset(ColumnGrid g, int k, int n, float step, bool rtl)
    {
        long col = g.ColumnOf(k);
        // Right to left, the first column is the rightmost; columns past the count overflow on the far side.
        float dx = (rtl ? n - 1 - col : col) * step;
        return (dx, g.RowTop(g.RowOf(k)) - g.Start(k));
    }

    /// <summary>
    /// Puts an op laid out in the grid into the fragments it falls in: content goes to the column of its top, and a box
    /// split by a column break is drawn in each column it reaches, cut at the break as a page break cuts it.
    /// </summary>
    private static void Distribute(DrawOp op, ColumnGrid g, Func<int, List<DrawOp>> into, ref int lastK)
    {
        switch (op)
        {
            case FieldOp field:
                into(Frag(g, field.Y)).Add(field);
                break;
            case GroupOp group:
            {
                // The group's first part is the group itself; each further column gets a copy holding its part.
                var kids = group.Ops.ToArray();
                group.Ops.Clear();
                int firstK = int.MinValue;
                Dictionary<int, GroupOp>? copies = null;
                List<DrawOp> Part(int k)
                {
                    if (firstK == int.MinValue)
                    {
                        firstK = k;
                        into(k).Add(group);
                    }
                    if (k == firstK) return group.Ops;
                    copies ??= new Dictionary<int, GroupOp>();
                    if (!copies.TryGetValue(k, out var copy))
                    {
                        copy = (GroupOp)group.Copy();
                        copies[k] = copy;
                        into(k).Add(copy);
                    }
                    return copy.Ops;
                }
                int inner = lastK;
                foreach (var kid in kids) Distribute(kid, g, Part, ref inner);
                if (firstK == int.MinValue) into(lastK).Add(group); // an empty slot stays with the content before it
                else lastK = inner;
                break;
            }
            case TextOp t:
                into(lastK = Frag(g, t.LineTop)).Add(t);
                break;
            case ImageOp im:
                into(lastK = Frag(g, im.Y)).Add(im);
                break;
            case SvgOp sv:
                into(lastK = Frag(g, sv.Y)).Add(sv);
                break;
            case CircleOp c:
                into(lastK = Frag(g, c.Cy - c.R)).Add(c);
                break;
            case LineOp l when l.X1 == l.X2:
                Split(l, Math.Min(l.Y1, l.Y2), Math.Max(l.Y1, l.Y2), g, into, ref lastK, static (o, y0, y1) =>
                {
                    var line = (LineOp)o;
                    line.Y1 = y0;
                    line.Y2 = y1;
                });
                break;
            case LineOp l:
                into(lastK = Frag(g, l.Y1)).Add(l);
                break;
            case RectOp r:
                Split(r, r.Y, r.Y + r.H, g, into, ref lastK, static (o, y0, y1) =>
                {
                    var rect = (RectOp)o;
                    rect.Y = y0;
                    rect.H = y1 - y0;
                });
                break;
            case LinkOp lk:
                Split(lk, lk.Y, lk.Y + lk.H, g, into, ref lastK, static (o, y0, y1) =>
                {
                    var link = (LinkOp)o;
                    link.Y = y0;
                    link.H = y1 - y0;
                });
                break;
            case BorderRectOp br:
                if (Frag(g, br.Y) == Frag(g, br.Y + br.H - 0.5f)) into(lastK = Frag(g, br.Y)).Add(br);
                else foreach (var side in br.Sides()) Distribute(side, g, into, ref lastK); // open at the break
                break;
            case RoundRectOp rr:
                ClipToFragments(rr, rr.Y, rr.Y + rr.H, g, into, ref lastK);
                break;
            case PathOp path:
                ClipToFragments(path, path.Y0, path.Y1, g, into, ref lastK);
                break;
            case BackgroundOp bg:
            {
                var (_, y0, _, y1) = bg.Painted();
                ClipToFragments(bg, y0, y1, g, into, ref lastK);
                break;
            }
            default:
                into(lastK).Add(op);
                break;
        }
    }

    /// <summary>The fragment a y is in, as the layout decides it; all of the hidden ones count as the first of them.</summary>
    private static int Frag(ColumnGrid g, float y) => Math.Min(g.PageOf(y + Eps), g.Hidden);

    /// <summary>The fragments [top, bottom) falls in, at most <see cref="MaxSplitFragments"/>.</summary>
    private static (int First, int Last) FragRange(ColumnGrid g, float top, float bottom)
    {
        int k0 = Frag(g, top), k1 = Math.Max(k0, Frag(g, bottom - 0.5f));
        return (k0, (int)Math.Min(k1, (long)k0 + MaxSplitFragments - 1));
    }

    /// <summary>Splits a rectangle-like op at the column breaks into copies, each set to its part by <paramref name="cut"/>.</summary>
    private static void Split(DrawOp op, float top, float bottom, ColumnGrid g, Func<int, List<DrawOp>> into, ref int lastK, Action<DrawOp, float, float> cut)
    {
        var (k0, k1) = FragRange(g, top, bottom);
        if (k0 == k1 || bottom <= top)
        {
            into(lastK = k0).Add(op);
            return;
        }
        for (int k = k0; k <= k1; k++)
        {
            float y0 = Math.Max(top, g.Start(k)), y1 = Math.Min(bottom, g.Start(k + 1));
            if (y1 <= y0) continue;
            var part = k == k0 ? op : op.Copy();
            cut(part, y0, y1);
            into(lastK = k).Add(part);
        }
    }

    /// <summary>Draws a shape split by column breaks whole in each column it reaches, clipped to that column's part.</summary>
    private static void ClipToFragments(DrawOp op, float top, float bottom, ColumnGrid g, Func<int, List<DrawOp>> into, ref int lastK)
    {
        var (k0, k1) = FragRange(g, top, bottom);
        if (k0 == k1 || bottom <= top)
        {
            into(lastK = k0).Add(op);
            return;
        }
        for (int k = k0; k <= k1; k++)
        {
            float y0 = Math.Max(top, g.Start(k)), y1 = Math.Min(bottom, g.Start(k + 1));
            if (y1 <= y0) continue;
            var clip = new ClipOp(-ClipOp.Far, y0, 2 * ClipOp.Far, y1 - y0, null);
            clip.Ops.Add(k == k0 ? op : op.Copy());
            into(lastK = k).Add(clip);
        }
    }

    /// <summary>column-rule: a line in the middle of each gap between columns that hold content.</summary>
    private void ColumnRules(ComputedStyle s, int usedColumns, float x, float top, float bottom, float colW, float gap, int n, bool rtl)
    {
        if (s.ColumnRuleStyle == BorderStyle.None || s.ColumnRuleWidth <= 0 || bottom <= top) return;
        float rw = s.ColumnRuleWidth, h = bottom - top;
        uint color = s.ColumnRuleColor ?? s.Color;
        for (int col = 1; col < usedColumns; col++)
        {
            float mid = x + (rtl ? n - col : col) * (colW + gap) - gap / 2;
            switch (s.ColumnRuleStyle)
            {
                case BorderStyle.Dotted:
                case BorderStyle.Dashed:
                    _ops.Add(new LineOp(mid, top, mid, bottom, rw, color, dotted: s.ColumnRuleStyle == BorderStyle.Dotted));
                    break;
                case BorderStyle.Double when rw >= 2.25f:
                {
                    float line = rw / 3;
                    _ops.Add(new RectOp(mid - rw / 2, top, line, h, color));
                    _ops.Add(new RectOp(mid + rw / 2 - line, top, line, h, color));
                    break;
                }
                default:
                    _ops.Add(new RectOp(mid - rw / 2, top, rw, h, color));
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ boxes kept whole, for deep nesting

    /// <summary>
    /// Multi-column layout that keeps the children whole (as with break-inside: avoid-column) and balances their
    /// heights over the columns; used for boxes nested in too many others to balance by laying out again.
    /// </summary>
    private float LayoutWholeColumns(Element e, float x, float y, float w, int n, float colW, float gap)
    {
        var s = e.Style;
        // Children as unbreakable pieces: block-level elements and runs of inline content. An element with
        // column-span: all ends the columns before it, spans the whole width, and starts a new set of columns after it.
        var pieces = new List<Captured>();
        List<Node>? run = null;
        void FlushRun()
        {
            if (run == null || run.Count == 0) return;
            bool content = false;
            foreach (var node in run)
                if (node is not TextNode t || !string.IsNullOrWhiteSpace(t.Text)) { content = true; break; }
            if (content) pieces.Add(CaptureRun(new List<Node>(run), e, colW));
            run.Clear();
        }
        float cy = y;
        foreach (var child in e.Children)
        {
            if (child is TextNode) { (run ??= new List<Node>()).Add(child); continue; }
            var ce = (Element)child;
            if (ce.Style == null || ce.Style.Display == Display.None || ce.Detached) continue;
            if (IsInlineLevel(ce)) { (run ??= new List<Node>()).Add(child); continue; }
            FlushRun();
            if (ce.Style.ColumnSpanAll && !ce.Style.Floated && !ce.Style.OutOfFlow)
            {
                cy = FlowWholeColumns(pieces, s, n, x, cy, colW, gap);
                pieces.Clear();
                var span = CaptureBox(ce, w, shrinkToFit: false);
                if (_paginate && !AtPageTop(cy) && cy + span.Height > FlowEnd(PageOf(cy)) + Eps && span.Height <= _grid.Height(PageOf(cy) + 1))
                    cy = NextPage(cy);
                Place(span, x, cy);
                cy += span.Height;
                continue;
            }
            pieces.Add(CaptureBox(ce, colW, shrinkToFit: false));
        }
        FlushRun();
        cy = FlowWholeColumns(pieces, s, n, x, cy, colW, gap);
        _lastBoxRight = Math.Max(_lastBoxRight, x + w);
        return cy;
    }

    /// <summary>Balances pieces over n columns from y, continuing on later pages as needed. Returns the bottom.</summary>
    private float FlowWholeColumns(List<Captured> pieces, ComputedStyle s, int n, float x, float y, float colW, float gap)
    {
        if (pieces.Count == 0) return y;
        float top = y;
        int next = 0;
        float bottom = y;
        while (next < pieces.Count)
        {
            Cancellation.ThrowIfCancellationRequested();
            CheckPageLimit(top);
            float balanced = BalancePieces(pieces, next, n);
            float room = _paginate ? FlowEnd(PageOf(top)) - top : float.PositiveInfinity;
            if (balanced <= room + Eps)
            {
                bottom = PlacePieces(pieces, ref next, n, balanced, x, top, colW, gap, fillAll: true, s);
                break;
            }
            if (!AtPageTop(top) && pieces[next].Height > room + Eps)
            {
                // Far down a flow the next page can start where this one does, as floats lose precision; the pieces
                // then go here, so that each pass places some.
                float below = NextPage(top);
                if (below > top)
                {
                    top = below;
                    continue;
                }
            }
            // Fill the columns to the bottom of this page, then carry on on the next one.
            PlacePieces(pieces, ref next, n, room, x, top, colW, gap, fillAll: false, s);
            top = _grid.Start(PageOf(top) + 1);
            bottom = top;
        }
        return bottom;
    }

    /// <summary>The smallest column height that fits pieces[start..] into n columns, keeping each piece whole.</summary>
    private static float BalancePieces(List<Captured> pieces, int start, int n)
    {
        float total = 0, tallest = 0;
        for (int i = start; i < pieces.Count; i++)
        {
            total += pieces[i].Height;
            tallest = Math.Max(tallest, pieces[i].Height);
        }
        bool Fits(float h)
        {
            int cols = 1;
            float cur = 0;
            for (int i = start; i < pieces.Count; i++)
            {
                float ph = pieces[i].Height;
                if (cur > 0 && cur + ph > h + Eps)
                {
                    if (++cols > n) return false;
                    cur = 0;
                }
                cur += ph;
            }
            return true;
        }
        float lo = Math.Max(tallest, total / n), hi = Math.Max(lo, total);
        if (Fits(lo)) return lo;
        for (int iter = 0; iter < 40 && hi - lo > 0.05f; iter++)
        {
            float mid = (lo + hi) / 2;
            if (Fits(mid)) hi = mid;
            else lo = mid;
        }
        return hi;
    }

    /// <summary>Places pieces into up to n columns of height h. Returns the bottom of the tallest column.</summary>
    private float PlacePieces(List<Captured> pieces, ref int next, int n, float h, float x, float top, float colW, float gap, bool fillAll, ComputedStyle s)
    {
        float maxBottom = top;
        int used = 0;
        for (int col = 0; col < n && next < pieces.Count; col++)
        {
            float cy = top;
            bool last = col == n - 1;
            while (next < pieces.Count)
            {
                var p = pieces[next];
                bool empty = cy == top;
                if (!empty && cy + p.Height > top + h + Eps && !(last && fillAll)) break;
                Place(p, x + col * (colW + gap), cy);
                cy += p.Height;
                next++;
            }
            if (cy > top) used = col + 1;
            maxBottom = Math.Max(maxBottom, cy);
        }
        // The rules run the height of the column row: the columns' height, or the page's rest when they fill it.
        ColumnRules(s, used, x, top, fillAll ? maxBottom : Math.Max(maxBottom, top + h), colW, gap, n, rtl: false);
        return maxBottom;
    }

    /// <summary>Min-content and max-content widths of a multi-column container's content box.</summary>
    private (float Min, float Max) ColumnsIntrinsic(Element e)
    {
        var s = e.Style;
        float min = 0, max = 0;
        List<Node>? run = null;
        void FlushRun()
        {
            if (run == null || run.Count == 0) return;
            var (mn, mx) = RunIntrinsic(run);
            min = Math.Max(min, mn);
            max = Math.Max(max, mx);
            run.Clear();
        }
        foreach (var child in e.Children)
        {
            if (child is TextNode) { (run ??= new List<Node>()).Add(child); continue; }
            var ce = (Element)child;
            if (ce.Style == null || ce.Style.Display == Display.None || ce.Detached) continue;
            if (IsInlineLevel(ce)) { (run ??= new List<Node>()).Add(child); continue; }
            FlushRun();
            var (cmn, cmx) = Intrinsic(ce);
            min = Math.Max(min, cmn);
            max = Math.Max(max, cmx);
        }
        FlushRun();
        float gap = s.ColumnGap is { IsPercent: false } g ? g.Value : s.FontSize;
        int n = s.ColumnCount > 0 ? s.ColumnCount : 1;
        if (!s.ColumnWidth.IsAuto) max = Math.Max(max, s.ColumnWidth.Value);
        return (min, n * max + (n - 1) * gap);
    }
}

/// <summary>
/// The fragments a multi-column box lays out its content in: its columns, one after another in a flow of their own,
/// with the box's pagination. The columns on one page form a row, all as tall as the room left on that page or the
/// box's height; a balanced last row is shorter. Rows past those given go on with the pages (or the columns) of the
/// grid around the box. When the box is not paginated, or its height ends the rows, the last row given takes every
/// further column: they overflow beside the box, as in Chrome.
/// </summary>
internal sealed class ColumnGrid : PageGrid
{
    /// <summary>The grid the box itself is laid out in: the pages, or the columns of a multi-column box around it.</summary>
    public readonly PageGrid Outer;

    /// <summary>The fragment of <see cref="Outer"/> the first row is on.</summary>
    public readonly int FirstPage;

    /// <summary>Where the first row starts in <see cref="Outer"/>.</summary>
    public readonly float Top;

    public readonly int Columns;

    /// <summary>The first fragment on no page, whose content and the content after it are not drawn.</summary>
    public int Hidden = int.MaxValue;

    /// <summary>The number of rows whose heights were given.</summary>
    public int GivenRows => _rowH.Length;

    private readonly float[] _rowH;          // the column height of each row given
    private readonly double[] _rowStart;     // where each row given starts in the grid's flow, and where the last ends
    private readonly bool _lastOverflows;    // the last row given takes all further columns
    private readonly float[] _tallestFrom;   // the tallest column in each row given or any after it

    public ColumnGrid(PageGrid outer, int firstPage, float top, int columns, float[] rows, bool lastOverflows)
        : base(outer.SetupOf(0), uniform: false)
    {
        Outer = outer;
        FirstPage = firstPage;
        Top = top;
        Columns = columns;
        _rowH = rows;
        _lastOverflows = lastOverflows;
        int m = rows.Length;
        _rowStart = new double[m + 1];
        for (int j = 0; j < m; j++) _rowStart[j + 1] = _rowStart[j] + (double)columns * Math.Max(0, rows[j]);
        _tallestFrom = new float[m + 1];
        _tallestFrom[m] = lastOverflows ? 0 : outer.MaxHeight;
        for (int j = m - 1; j >= 0; j--) _tallestFrom[j] = Math.Max(rows[j], _tallestFrom[j + 1]);
    }

    /// <summary>The height of fragment p of a grid itself (a page grid's pages are as tall as it says).</summary>
    public static float FragmentHeight(PageGrid grid, int p) => grid is ColumnGrid c ? c.OwnHeight(p) : grid.Height(p);

    // Rows go far enough for any flow the page limit allows; past that the arithmetic would overflow.
    private int MaxRow => int.MaxValue / 4 / Columns;

    public int RowOf(int k)
    {
        if (k <= 0) return 0;
        int row = k / Columns;
        return _lastOverflows ? Math.Min(row, _rowH.Length - 1) : row;
    }

    public long ColumnOf(int k) => k - (long)RowOf(k) * Columns;

    public int FirstFragmentOf(int row) => (int)Math.Min(int.MaxValue, (long)row * Columns);

    /// <summary>Whether no row follows this one: further columns overflow beside it.</summary>
    public bool IsLastRow(int row) => _lastOverflows && row >= _rowH.Length - 1;

    public float RowHeight(int row) => row < _rowH.Length ? _rowH[row] : FragmentHeight(Outer, FirstPage + row);

    /// <summary>Where a row starts in <see cref="Outer"/>: the first on the page of the box, the others at the top of the next.</summary>
    public float RowTop(int row) => row == 0 ? Top : Outer.Start(FirstPage + row);

    private double RowStart(int row)
    {
        int m = _rowH.Length;
        if (row <= m) return _rowStart[Math.Max(0, row)];
        return _rowStart[m] + (double)Columns * ((double)Outer.Start(FirstPage + row) - Outer.Start(FirstPage + m));
    }

    /// <summary>The height of fragment k's own column.</summary>
    public float OwnHeight(int k) => Math.Max(0, RowHeight(RowOf(k)));

    public override float Start(int page)
    {
        int row = RowOf(page);
        return (float)(RowStart(row) + (page - (double)row * Columns) * OwnHeight(page));
    }

    /// <summary>
    /// The tallest column from fragment k on. The layout asks this to know whether moving content on can help, and
    /// the next column of a row on a page's last part is no taller than this one, while the next page's are.
    /// </summary>
    public override float Height(int page)
    {
        int row = RowOf(page);
        return row < _rowH.Length ? _tallestFrom[row] : _tallestFrom[_rowH.Length];
    }

    /// <summary>
    /// The tallest column from fragment k on, as tall as content can still find. Past the rows given each column is
    /// as tall as the outer fragment it is in, and the later fragments of outer columns can be shorter than their
    /// first, which <see cref="Height"/> counts.
    /// </summary>
    public float TallestFrom(int page)
    {
        int row = RowOf(page);
        return row < _rowH.Length || Outer is not ColumnGrid outer ? Height(page) : outer.TallestFrom(FirstPage + row);
    }

    public override float MaxHeight => _tallestFrom[0];

    /// <summary>Images are made to fit pages, not columns.</summary>
    public override float MinHeight => Outer.MinHeight;

    public override int PageOf(float y)
    {
        double k = PageOf((double)y);
        return k >= int.MaxValue / 2 ? int.MaxValue / 2 : k <= int.MinValue / 2 ? int.MinValue / 2 : (int)k;
    }

    public override double PageOf(double y)
    {
        int m = _rowH.Length;
        int row;
        if (y < _rowStart[m] || _lastOverflows)
        {
            // The last row given that starts at or before y; rows of no height start where the next does.
            int lo = 0, hi = m - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_rowStart[mid] <= y) lo = mid;
                else hi = mid - 1;
            }
            row = lo;
        }
        else
        {
            double oy = Outer.Start(FirstPage + m) + (y - _rowStart[m]) / Columns;
            double p = Math.Min(Outer.PageOf(oy) - FirstPage, MaxRow);
            // The last row that starts at or before y. Row starts only grow, but rows of no height (in outer columns
            // of no height) start where the next does, and there can be as many as MaxRow of them: a search finds the
            // row in steps that double, where stepping one row at a time took seconds.
            int lo = m, hi = (int)Math.Max(m, p);
            if (RowStart(hi) <= y)
            {
                lo = hi;
                for (long step = 1; ; step *= 2)
                {
                    int next = (int)Math.Min(lo + step, MaxRow);
                    if (next == lo || RowStart(next) > y)
                    {
                        hi = next;
                        break;
                    }
                    lo = next;
                }
            }
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (RowStart(mid) <= y) lo = mid;
                else hi = mid - 1;
            }
            row = lo;
        }
        float h = RowHeight(row);
        // Content above the first row (a negative margin) is in the first column.
        double col = h > 0 ? Math.Max(0, Math.Floor((y - RowStart(row)) / h)) : 0;
        if (!IsLastRow(row)) col = Math.Min(col, Columns - 1);
        return (double)row * Columns + col;
    }

    /// <summary>Where a position in the grid is in <see cref="Outer"/>.</summary>
    public double ToOuter(double y)
    {
        double k = PageOf(y);
        int frag = k >= int.MaxValue / 2 ? int.MaxValue / 2 : k <= int.MinValue / 2 ? int.MinValue / 2 : (int)k;
        return RowTop(RowOf(frag)) + (y - Start(frag));
    }
}

using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// Lays out the styled tree into a flat list of <see cref="DrawOp"/>s. Pagination happens during layout:
/// atomic pieces (lines, images, table rows) that would straddle a page boundary are pushed to the next page.
/// </summary>
internal sealed partial class LayoutEngine
{
    private PageGrid _grid; // the pages, or while a multi-column box lays out its content, its columns
    private readonly ImageLoader _images;
    private List<DrawOp> _ops = new(256);
    private bool _paginate = true;
    private ListMarker? _marker;
    private int _currentListIndex;
    private float _lastBaseline = float.NaN;
    private float _lastBoxRight; // right border edge (plus nothing else) of the last block box laid out
    private float _lastBoxX, _lastBoxTop, _lastBoxW, _lastBoxH; // border box of the last block box laid out
    // The content height, vertical padding and borders of the last block box laid out, and the containing block height
    // it gave its children (null when that was not definite, or the box is replaced).
    private float _lastBoxContentH, _lastBoxFrameV;
    private CbHeights? _lastBoxChildCb;
    private readonly Dictionary<Element, (float Min, float Max)> _intrinsicCache = new();

    public readonly Dictionary<string, float> Anchors = new(StringComparer.Ordinal);

    public LayoutEngine(float pageContentHeight, ImageLoader images)
        : this(new PageGrid(new PageSetup { Height = pageContentHeight }), images)
    {
    }

    /// <summary>A layout whose pages have the flow heights of the grid's pages.</summary>
    public LayoutEngine(PageGrid pages, ImageLoader images)
    {
        _grid = pages;
        _images = images;
    }

    public List<DrawOp> Ops => _ops;

    /// <summary>The root element (html), whose border box positions the page canvas background.</summary>
    public Element? CanvasRoot { get; init; }

    /// <summary>The border box of <see cref="CanvasRoot"/> once laid out.</summary>
    public (float X, float Y, float W, float H)? CanvasRootBox { get; private set; }

    /// <summary>For a tagged PDF: the document's structure, whose slots the text, image and marker ops are tagged with.</summary>
    public StructureTree? Tags { get; init; }

    /// <summary>Stops the layout when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The most pages the flow may fill; checked as lines are placed, before their ops pile up.</summary>
    public int MaxPages { get; init; } = int.MaxValue;

    /// <summary>
    /// The part of the relayout budget that every document gets whatever its size (see <see cref="_relayoutBudget"/>),
    /// or what earlier layouts of the same document left of it.
    /// </summary>
    public long SharedRelayouts { get; init; } = BaseRelayouts;

    /// <summary>How much of <see cref="SharedRelayouts"/> the layout took: the box layouts past its share per element.</summary>
    public long SharedRelayoutsUsed => Math.Clamp(_boxLayouts - (_relayoutBudget - SharedRelayouts), 0, SharedRelayouts);

    /// <summary>Makes the content ops from <paramref name="start"/> on artifacts, for content drawn again (repeated table headers).</summary>
    private static void Untag(List<DrawOp> ops, int start = 0)
    {
        for (int i = start; i < ops.Count; i++)
        {
            ops[i].Tag = null;
            if (ops[i] is GroupOp g) Untag(g.Ops);
        }
    }

    private void CheckPageLimit(float y)
    {
        // In a column the limit applies to the page the column is on.
        var grid = _grid;
        double py = y;
        while (grid is ColumnGrid columns)
        {
            py = columns.ToOuter(py);
            grid = columns.Outer;
        }
        if (py >= (grid.Uniform ? (double)MaxPages * grid.Height(0) : grid.Start(MaxPages))) throw new PdfLimitException($"The document would have more than {MaxPages:N0} pages (PdfOptions.MaxPages).");
    }

    private sealed class ListMarker
    {
        public string? Text;
        public bool Custom; // the text comes from ::marker content or a string list-style-type: drawn as written
        public ListStyleType Type;
        public PdfImage? Image; // list-style-image: a raster image or an SVG drawing, ImageW by ImageH points
        public SvgImage? Svg;
        public CssGradient? Gradient;
        public float ImageW, ImageH;
        public ComputedStyle Style = null!;
        public float ContentX;
        public float ContentRight; // right to left, the marker goes after the content's right edge
        public bool Rtl;
        public bool Inside; // list-style-position: inside, so the marker starts the first line instead of hanging before it
        public ContentSlot? Tag;
    }

    public float Layout(Element document, float width)
    {
        _pageBox = RootPageBox(width);
        _rootWidth = width;
        // The initial containing block is the page area. When pages differ in height, a box resolves a percentage
        // chain that leads up to it against the page it starts on, so html, body { height: 100% } and a section of
        // height 100% fill whichever page the section starts on, as in Chrome.
        _cbHeight = new CbHeights(_grid.Height(0), _grid.Height(1), _grid.Height(2));
        _cbKind = 0;
        _relayoutBudget = SharedRelayouts + RelayoutsPerElement * CountElements(document);
        float bottom = LayoutFlow(document, 0, 0, width);
        _cbHeight = null;
        _pageBox = null;
        if (_floats.Any && _floats.Bottom > bottom) bottom = _floats.Bottom; // the root contains its floats
        FinishFootnotes();
        FinishRoot(width);
        // A footnote continued past the last page of content needs a page of its own.
        return Math.Max(bottom, FootnotesBottom());
    }

    /// <summary>Min-content and max-content border-box widths of a block-level element.</summary>
    public (float Min, float Max) MeasureIntrinsic(Element e) => Intrinsic(e);

    /// <summary>Lays out a running header or footer on its own, unpaginated, at y = 0. Returns its height including margins.</summary>
    public float LayoutBand(Element band, float width)
    {
        _paginate = false;
        _treeRoot = band;
        var s = band.Style;
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(width), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(width);
        float bottom = LayoutBlockBox(band, 0, mt, width, shrinkToFit: false) + mb;
        FinishRoot(width);
        return bottom;
    }

    // ------------------------------------------------------------------ pagination helpers

    private const float Eps = 0.01f;

    private int PageOf(float y) => _grid.PageOf(y + Eps);

    private bool AtPageTop(float y) => y - _grid.Start(PageOf(y)) < 0.5f;

    /// <summary>Returns the y at which a box of height h should start so that it does not straddle a page boundary.</summary>
    private float FitOnPage(float y, float h)
    {
        if (!_paginate) return y;
        int p = PageOf(y);
        _columnNotes?.Exact(p, y, h, _grid);
        // A page grid moves content at most once, to the next page. The columns of a multi-column box can be shorter
        // than later ones, so there content moves on until a column holds it, while a later one can.
        while (h <= (_grid is ColumnGrid c ? c.TallestFrom(p + 1) : _grid.Height(p + 1)))
        {
            float pageEnd = _grid.Start(p + 1);
            // The page's footnote area takes the bottom of its flow.
            float flowEnd = _footnoteReserve.Count > 0 || _edgeReserve.Count > 0 ? FlowEnd(p) : pageEnd;
            if (y + h <= flowEnd + Eps) return y;
            _columnNotes?.Break(p, y, y + h - flowEnd);
            y = pageEnd;
            if (_grid is not ColumnGrid) break;
            Cancellation.ThrowIfCancellationRequested();
            p++;
        }
        return y;
    }

    private float NextPage(float y)
    {
        if (!_paginate || AtPageTop(y)) return y;
        int p = PageOf(y);
        _columnNotes?.Break(p, y, 0);
        return _grid.Start(p + 1);
    }

    /// <summary>True when the ops draw nothing: only the empty groups that boxes reserve for their backgrounds.</summary>
    private static bool NothingDrawn(List<DrawOp> ops)
    {
        foreach (var op in ops)
            if (op is not GroupOp g || !NothingDrawn(g.Ops)) return false;
        return true;
    }

    private bool CrossesPage(float top, float bottom) => _paginate && bottom - top > Eps
        && (PageOf(top) != PageOf(bottom - 0.5f) || (_footnoteReserve.Count > 0 || _edgeReserve.Count > 0) && bottom > FlowEnd(PageOf(top)) + Eps);

    private void Truncate(int count, bool keepFloats = false)
    {
        if (_ops.Count > count) _ops.RemoveRange(count, _ops.Count - count);
        if (!keepFloats) _floats.Truncate(count);
        // keepFloats means the ops were moved into one op at count, not removed: their footnotes stay.
        if (_footnoteCalls.Count > 0) TruncateFootnotes(count, wrapped: keepFloats);
    }

    // ------------------------------------------------------------------ block formatting

    /// <summary>
    /// A container-type other than normal gives the box size containment on one or both axes. Containment does not
    /// apply to inline boxes and the inner boxes of tables, which are not query containers either.
    /// </summary>
    private static bool HasSizeContainment(ComputedStyle s)
        => s.ContainerType != ContainerType.Normal && s.Display is not (Display.Inline or Display.TableRow or Display.TableCell
            or Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup or Display.TableColumn or Display.TableColumnGroup);

    private static bool IsInlineLevel(Element e)
    {
        var d = e.Style.Display;
        if (e.Style.IsAtomicInline) return true;
        if (d != Display.Inline) return false;
        return !ContainsBlock(e);
    }

    private static bool ContainsBlock(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce || ce.Style == null || ce.Detached || ce.Style.Floated || ce.Style.OutOfFlow) continue;
            var d = ce.Style.Display;
            if (d == Display.None || ce.Style.IsAtomicInline) continue;
            if (d != Display.Inline) return true;
            if (ContainsBlock(ce)) return true;
        }
        return false;
    }

    private static float CollapseMargins(float a, float b)
    {
        if (a >= 0 && b >= 0) return Math.Max(a, b);
        if (a < 0 && b < 0) return Math.Min(a, b);
        return a + b;
    }

    /// <summary>Where a block box of a flow started, so that layout can rewind to it (break-after/before: avoid).</summary>
    private struct FlowMark
    {
        public int Index, OpsCount, ListIndex, CurrentListIndex;
        public float Y, PendingMargin, Top;
        public bool AnyContent;
        public ListMarker? Marker;
        public int FirstPage; // page of the box's first painted content, found when the box starts a keep chain
        public int BoxOps, BoxEnd; // the box's own ops, among which that content is looked for
    }

    /// <summary>
    /// The state of one <see cref="LayoutFlow"/> call, which its phases share by reference so that splitting the flow
    /// into methods costs no allocation: the position reached, the margin waiting to collapse with the next box, the
    /// list numbering and the keep-with-next chain that a box may have to rewind to.
    /// </summary>
    private struct FlowState
    {
        public Element Parent;
        public List<Node> Children;
        public float X, Y, W;
        public PageBox? Pages;          // the flow's content box on each kind of page, when pages differ in width
        public KeepProbe? Probe;        // the keep-with-next check of the flow around this box
        public List<FlowResume>? Marks; // where a later balancing pass could go on from
        public List<Node>? Run;         // the inline content collected since the last block box
        public bool InColumns;
        public float PendingMargin;
        public bool AnyContent;
        public bool AbsorbFirst;        // the parent has taken its first child's top margin as its own
        public bool FirstBlock;
        public int ListIndex;
        public bool ListReversed;       // an ol reversed counts down
        public int Index;               // the child being laid out, which a rewind moves back
        // Keep-with-next: the first box of a run of boxes that must not be separated from what follows them.
        public FlowMark Prev, Chain;
        public bool HasPrev, HasChain, PrevAvoidAfter;
        public int ForcedAt;            // the box that was moved to a new page to keep its chain together
        public bool AfterForcedBreak;   // the content so far ends with a forced page break
    }

    /// <summary>What the flow does after a phase: go on with the current child, go on with the next one, or stop.</summary>
    private enum FlowStep : byte { Proceed, Next, Stop }

    /// <summary>Lays out the children of a block container. Returns the bottom of the content (including the last child's bottom margin).</summary>
    private float LayoutFlow(Element parent, float x, float y, float w)
    {
        var f = BeginFlow(parent, x, y, w, out int end);
        var children = f.Children;
        for (; f.Index <= end; f.Index++)
        {
            Element? e = null;
            if (f.Index < end)
            {
                var child = children[f.Index];
                if (child is TextNode)
                {
                    (f.Run ??= RentRun()).Add(child);
                    continue;
                }
                e = (Element)child;
                var es = e.Style;
                if (es == null || es.Display == Display.None || e.Detached) continue;
                if (IsInlineLevel(e) || (es.Floated || es.OutOfFlow) && f.Run is { Count: > 0 })
                {
                    // Floats and absolutely positioned boxes among inline content are placed as the lines reach them.
                    (f.Run ??= RentRun()).Add(child);
                    continue;
                }
            }

            if (f.Run is { Count: > 0 })
            {
                var step = FlushFlowRun(ref f);
                if (step == FlowStep.Next) continue;
                if (step == FlowStep.Stop) break;
            }
            if (e == null) break;

            var s = e.Style;
            if (s.OutOfFlow)
            {
                // Its static position: where its top margin edge would be in the flow.
                float smt = s.MarginTop.Resolve(f.W);
                float sy = f.Y + CollapseMargins(f.PendingMargin, smt) - smt;
                QueueAbsolute(e, f.Pages != null ? OnPage(f.Pages, sy).X : f.X, sy);
                continue;
            }
            if (s.Floated)
            {
                var (fx, fw) = f.Pages != null ? OnPage(f.Pages, f.Y + f.PendingMargin) : (f.X, f.W);
                PlaceFloat(MeasureFloat(e, fw), f.Y + f.PendingMargin, fx, fw);
                continue;
            }
            if (LayoutFlowBox(ref f, e) == FlowStep.Stop) break;
        }
        _flowTrailing = f.PendingMargin;
        ReturnRun(f.Run);
        return f.Y + f.PendingMargin;
    }

    /// <summary>
    /// Starts a flow: takes what the caller handed it for this flow alone (the page geometry, the keep probe, the
    /// children range of a multi-column box and a balancing pass's resume point), and the list numbering.
    /// </summary>
    private FlowState BeginFlow(Element parent, float x, float y, float w, out int end)
    {
        var f = new FlowState { Parent = parent, Children = parent.Children, X = x, Y = y, W = w, ForcedAt = -1 };
        // The flow's content box on each kind of page, when pages differ in width: its boxes and lines take the
        // geometry of the page they are on.
        f.Pages = _pageBox;
        _pageBox = null;
        // The keep-with-next check of the flow around this box, which only needs to know where this flow's first content lands.
        f.Probe = _flowProbe;
        _flowProbe = null;
        // A multi-column box lays out the children between two column-span: all boxes as one flow.
        var range = _flowRange;
        _flowRange = null;
        var resume = _flowResume;
        _flowResume = null;
        f.Marks = _flowMarks;
        _flowMarks = null;
        f.InColumns = _grid is ColumnGrid;
        // The parent has taken its first child's top margin as its own (they collapse): don't apply it again.
        f.AbsorbFirst = _absorbTop;
        _absorbTop = false;
        f.FirstBlock = true;

        if (parent.Name == "ol")
        {
            // A reversed list counts down, from its start or else from its number of items.
            f.ListReversed = parent.HasAttribute("reversed");
            if (Invariant.TryParseInt(parent.GetAttribute("start"), out int start)) f.ListIndex = f.ListReversed ? start + 1 : start - 1;
            else if (f.ListReversed) f.ListIndex = CountListItems(parent) + 1;
        }

        if (resume is { } at)
        {
            // A balancing pass goes on from where an earlier pass recorded this point, with the content before it.
            f.Y = at.Mark.Y;
            f.PendingMargin = at.Mark.PendingMargin;
            f.AnyContent = at.Mark.AnyContent;
            f.ListIndex = at.Mark.ListIndex;
            _currentListIndex = at.Mark.CurrentListIndex;
            _marker = at.Mark.Marker;
            f.AbsorbFirst = f.FirstBlock = false;
        }

        end = range?.To ?? f.Children.Count;
        f.Index = resume?.Mark.Index ?? range?.From ?? 0;
        return f;
    }

    private static int CountListItems(Element list)
    {
        int n = 0;
        foreach (var c in list.Children)
            if (c is Element { Style.Display: Display.ListItem }) n++;
        return n;
    }

    /// <summary>
    /// Lays out the inline content collected before the current child as lines. Returns <see cref="FlowStep.Next"/>
    /// when the lines broke a keep-with-next chain and the flow rewound to it.
    /// </summary>
    private FlowStep FlushFlowRun(ref FlowState f)
    {
        var run = f.Run!;
        float before = f.Y;
        int runOps = _ops.Count;
        // The lines' content (inline blocks, percentages) is sized for the page the paragraph starts on; each
        // line then takes the width of its own page.
        float top = f.InColumns ? f.Y + f.PendingMargin : TruncatedPageMargin(ref f, f.Y + f.PendingMargin);
        var (ix, iw) = f.Pages != null ? OnPage(f.Pages, top) : (f.X, f.W);
        _inlinePageBox = f.Pages;
        f.Y = FlushInline(run, f.Parent, ix, top, iw, !f.AnyContent, out bool produced);
        run.Clear();
        if (produced)
        {
            f.PendingMargin = 0;
            f.AnyContent = true;
            f.AfterForcedBreak = false;
            if (f.HasChain && f.PrevAvoidAfter && KeepTogetherFails(f.Chain, runOps, before, f.ForcedAt))
            {
                RewindToChain(ref f);
                return FlowStep.Next;
            }
            f.HasChain = f.HasPrev = f.PrevAvoidAfter = false;
            if (f.Probe != null && FlowProbed(ref f)) return FlowStep.Stop;
        }
        else f.Y = before;
        return FlowStep.Proceed;
    }

    /// <summary>
    /// Lays out one block box of the flow, keeping it with the chain of boxes before it when breaks between them are
    /// avoided. Returns <see cref="FlowStep.Stop"/> when the flow's keep probe has its answer.
    /// </summary>
    private FlowStep LayoutFlowBox(ref FlowState f, Element e)
    {
        var s = e.Style;
        var mark = new FlowMark
        {
            Index = f.Index, OpsCount = _ops.Count, ListIndex = f.ListIndex, CurrentListIndex = _currentListIndex,
            Y = f.Y, PendingMargin = f.PendingMargin, AnyContent = f.AnyContent, Marker = _marker,
        };
        // A forced break wins over avoid.
        // In columns a box breaks to the next column; Chrome ignores page breaks there.
        bool breakBefore = f.InColumns ? s.ColumnBreakBefore : s.PageBreakBefore;
        bool breakAfter = f.InColumns ? s.ColumnBreakAfter : s.PageBreakAfter;
        bool joins = _paginate && f.HasPrev && (f.PrevAvoidAfter || s.AvoidBreakBefore || f.InColumns && s.AvoidColumnBreakBefore) && !breakBefore;
        if (!joins) f.HasChain = false;
        if (joins && !f.HasChain)
        {
            f.Chain = f.Prev;
            f.Chain.FirstPage = FirstContentPage(f.Chain.BoxOps, f.Chain.BoxEnd, f.Chain.Top);
            f.HasChain = true;
        }
        if (f.Marks != null && !joins && f.ForcedAt != f.Index && CanResumeAt(f.Y)) f.Marks.Add(new FlowResume(mark, _captureAnchors?.Count ?? 0));

        if (s.PageBreakBefore || s.PageBreakAfter) _forcedBreaks++;
        if (s.Display == Display.ListItem)
        {
            f.ListIndex += f.ListReversed ? -1 : 1;
            if (Invariant.TryParseInt(e.GetAttribute("value"), out int v)) f.ListIndex = v;
            _currentListIndex = f.ListIndex;
        }

        float top = FlowBoxTop(ref f, e, breakBefore, out float mt, out bool cleared);
        int boxOps = _ops.Count;
        float bottom = LayoutFlowBoxContent(ref f, e, ref top, joins, boxOps, out var passed);
        float trailing = _boxTrailing;
        if (joins && KeepTogetherFails(f.Chain, boxOps, top, f.ForcedAt))
        {
            RewindToChain(ref f);
            return FlowStep.Next;
        }
        float mb = CollapseMargins(s.MarginBottom.Resolve(f.W), trailing);
        if (bottom - top < Eps && CollapsesThrough(e) && !breakBefore && !breakAfter && !cleared)
        {
            // An empty box: its margins collapse with each other and with those around it. One that clears floats
            // stays below them, so what follows does too: that is how an empty clearfix div ends a row of floats.
            f.PendingMargin = CollapseMargins(CollapseMargins(f.PendingMargin, mt), mb);
            return FlowStep.Next;
        }
        f.AnyContent = true;
        f.PendingMargin = mb;
        f.Y = bottom;
        f.AfterForcedBreak = breakAfter;
        if (breakAfter)
        {
            f.Y = ForcedBreak(f.Y + f.PendingMargin);
            f.PendingMargin = 0;
        }

        mark.Top = top;
        // The page of its first content is only looked up when the next box joins it: finding it here scanned all the
        // ops below every box, which for deeply nested boxes is quadratic.
        mark.BoxOps = boxOps;
        mark.BoxEnd = _ops.Count;
        f.PrevAvoidAfter = _paginate && (s.AvoidBreakAfter || f.InColumns && s.AvoidColumnBreakAfter) && !breakAfter;
        if (!f.PrevAvoidAfter) f.HasChain = false;
        f.Prev = mark;
        f.HasPrev = !breakAfter;
        // The box stopped with the probe this flow handed it: its first content is past the chain's page, which
        // nothing after it can change, and laying out its siblings would only record their headings too early.
        if (passed is { Done: true } || f.Probe != null && FlowProbed(ref f)) return FlowStep.Stop;
        return FlowStep.Next;
    }

    /// <summary>
    /// The top of a block box's border edge in the flow: after its top margin, collapsed with the pending one, below
    /// the floats it clears and on a new page when a break is forced before it. <paramref name="cleared"/> tells
    /// whether clearing floats moved it down.
    /// </summary>
    private float FlowBoxTop(ref FlowState f, Element e, bool breakBefore, out float mt, out bool cleared)
    {
        var s = e.Style;
        // A box's top margin collapses with those of its first children (CSS 2.1 §8.3.1).
        mt = f.AbsorbFirst && f.FirstBlock && !f.AnyContent
            ? 0
            : CollapseMargins(s.MarginTop.Resolve(f.W), LeadingMargin(e, f.W));
        f.FirstBlock = false;
        float top = f.Y + CollapseMargins(f.PendingMargin, mt);
        if (f.InColumns) top = TruncatedMargin(f.Y, top);
        else if (!breakBefore) top = TruncatedPageMargin(ref f, top);
        float uncleared = top;
        if (s.ClearLeft || s.ClearRight) top = Cleared(s, top);
        cleared = top > uncleared;
        // A forced break before anything has been drawn (the first box of the document or of a named page's run)
        // would only leave an empty page: Chrome ignores it.
        if (f.ForcedAt == f.Index) top = NextPage(top);
        else if (breakBefore && !AtPageTop(f.Y) && (PageOf(top) > 0 || !NothingDrawn(_ops)))
        {
            // Margins before a forced break are cut off, but the box's own top margin after it stays, as it does
            // after a break-after on the box before (CSS Fragmentation 3 §5.2). Content that already ends at the top
            // of a page, after a break-after on the box before, needs no second break.
            float next = ForcedBreak(top);
            if (next != top) top = next + mt;
        }
        return top;
    }

    /// <summary>
    /// Where a box or line goes on the pages after the content of its flow ended at f.Y, given its top margin edge: a
    /// margin that reaches past the end of a page, or follows content that ended there, is cut off at the break, as
    /// Chrome cuts it (CSS Fragmentation 3 §5.2). After a forced break, and at the top of the box the flow is in, the
    /// margin stays.
    /// </summary>
    private float TruncatedPageMargin(ref FlowState f, float top)
    {
        float y = f.Y;
        if (!_paginate || top <= y) return top;
        int k = PageOf(y);
        if (PageOf(top) > k) return _grid.Start(k + 1);
        return k > 0 && f.AnyContent && !f.AfterForcedBreak && AtPageTop(y) ? y : top;
    }

    /// <summary>
    /// Lays out a block box of the flow at <paramref name="top"/>, beside the floats it avoids or with the keep probe
    /// that lets it stop early. <paramref name="passed"/> is this flow's own probe when the box took it over.
    /// </summary>
    private float LayoutFlowBoxContent(ref FlowState f, Element e, ref float top, bool joins, int boxOps, out KeepProbe? passed)
    {
        var (bx, bw) = f.Pages != null ? OnPage(f.Pages, top) : (f.X, f.W);
        passed = null;
        if (FloatsBelow(top) && AvoidsFloats(e))
        {
            // A box beside floats, such as a table beside the initial letter of the heading it is kept with, counts
            // towards the depth that chains move to like any other box kept with a chain. Without it, every level of
            // such tables nested in each other was laid out in full and then again on the next page when its chain
            // moved, so the work doubled with each level: 30 levels did not finish.
            if (joins) _joinDepth++;
            float besideBottom = LayoutBesideFloats(e, bx, ref top, bw);
            if (joins) _joinDepth--;
            return besideBottom;
        }

        // A box that must start on the page of the chain before it is only laid out until it is clear where its
        // first content lands, when that is a later page and the chain has to move anyway.
        // Nested deeper than chains move (see KeepTogetherFails), the chain stays whatever the probe finds, and the box
        // stopped early was laid out again in full: at every level, so the work doubled with each page. The same holds
        // once the relayout budget is spent.
        var childProbe = joins && f.Chain.Index != f.ForcedAt && !AtPageTop(f.Chain.Top) && _joinDepth < MaxKeptWholeNesting
                         && MayLayOutAgain && CanProbe(e)
            ? new KeepProbe(boxOps, f.Chain.FirstPage)
            : null;
        // A flow that is probed itself hands its probe to its first box, whose first content is this flow's.
        // Otherwise a box that only wraps the next level, such as a div around the next heading, is laid out in
        // full before the probe can stop, and each level again lays out everything below it twice.
        passed = childProbe == null && f.Probe != null && !f.HasPrev && CanProbe(e) && FirstContent(f.Probe) == null ? f.Probe : null;
        var markerBefore = _marker;
        int listBefore = _currentListIndex;
        _childPageBox = f.Pages;
        _childProbe = childProbe ?? passed;
        if (joins) _joinDepth++;
        float bottom = LayoutBlockBox(e, bx, top, bw, shrinkToFit: false);
        if (joins) _joinDepth--;
        _childProbe = null;
        if (childProbe is { Stopped: true } && !KeepTogetherFails(f.Chain, boxOps, top, f.ForcedAt))
        {
            // The chain stays after all, so the box needs all of its content: lay it out again in full.
            Truncate(boxOps);
            _marker = markerBefore;
            _currentListIndex = listBefore;
            _childPageBox = f.Pages;
            bottom = LayoutBlockBox(e, bx, top, bw, shrinkToFit: false);
        }
        return bottom;
    }

    /// <summary>Moves the flow back to the start of its keep-with-next chain, which then goes to the next page.</summary>
    private void RewindToChain(ref FlowState f)
    {
        RewindFlow(ref f, f.Chain);
        f.ForcedAt = f.Chain.Index;
        f.HasChain = f.HasPrev = f.PrevAvoidAfter = false;
    }

    /// <summary>Restores the flow to where a box started, dropping what was drawn and recorded after that point.</summary>
    private void RewindFlow(ref FlowState f, in FlowMark m)
    {
        Truncate(m.OpsCount);
        var marks = f.Marks;
        while (marks is { Count: > 0 } && marks[^1].Mark.Index >= m.Index) marks.RemoveAt(marks.Count - 1);
        if (f.Probe != null) f.Probe.ScanFrom = Math.Min(f.Probe.ScanFrom, m.OpsCount);
        f.Index = m.Index - 1;
        f.Y = m.Y;
        f.PendingMargin = m.PendingMargin;
        f.AnyContent = m.AnyContent;
        f.ListIndex = m.ListIndex;
        _currentListIndex = m.CurrentListIndex;
        _marker = m.Marker;
        f.Run?.Clear();
    }

    // Whether the flow can stop because the probe has its answer: true when its first content is on a later page
    // than the chain before it, which then moves to the next page and lays this flow out again. Once the first
    // content is on that page or an earlier one, the probe is done and the flow carries on as usual.
    private bool FlowProbed(ref FlowState f)
    {
        var p = f.Probe!;
        if (FirstContent(p) is not (var at, var fy)) return false;
        // A chain of this flow that can still move to the next page may take the first content with it.
        if (f.HasChain && f.Chain.OpsCount <= at && f.Chain.Index != f.ForcedAt && !AtPageTop(f.Chain.Top)) return false;
        if (f.HasPrev && f.Prev.OpsCount <= at && f.Prev.Index != f.ForcedAt && !AtPageTop(f.Prev.Top)
            && (f.PrevAvoidAfter || NextBoxAvoidsBreakBefore(f.Children, f.Index + 1))) return false;
        if (PageOf(fy) > p.KeepPage) return p.Stopped = true;
        if (p.StopsOnceAnswered) return p.Answered = true;
        f.Probe = null;
        return false;
    }

    /// <summary>
    /// A box laid out after a chain of boxes that must not be separated from it (break-after/before: avoid), whose
    /// layout can stop once its first content is placed on a later page than the chain's: the chain then moves to the
    /// next page and lays the box out again, so the rest of it would be thrown away. Without this, boxes nested in
    /// such chains lay out each level twice, which takes time exponential in the depth.
    /// </summary>
    private sealed class KeepProbe(int opsStart, int keepPage)
    {
        public readonly int OpsStart = opsStart; // where the box's ops start
        public readonly int KeepPage = keepPage; // the page of the chain's first content
        public int ScanFrom = opsStart;          // the ops before it hold no content
        public bool Stopped;                     // the box's layout stopped early
        public bool StopsOnceAnswered;           // also stop once the first content is on the keep page
        public bool Answered;                    // the layout stopped there, with the first content on the keep page
        public bool Edge;                        // the box moves to the next page when its first content is there
        public HashSet<DrawOp>? Chained;         // the backgrounds of the first children an edge probe was handed to
        public bool Done => Stopped || Answered;
    }

    private KeepProbe? _childProbe; // for the box LayoutFlow lays out next, taken by LayoutBlockBoxCore
    private KeepProbe? _flowProbe;  // for the flow of that box, taken by LayoutFlow

    /// <summary>
    /// Whether a box's first content is decided by its own flow alone, so that stopping its layout early leaves it
    /// where it is: a plain block whose ops are not moved, wrapped or laid out again after its content.
    /// </summary>
    /// <remarks>
    /// A list item qualifies: its marker is drawn with its first line, or at its top after its content when it has no
    /// line, which the scan for the first content reaches no earlier than the content itself. Without that, a list
    /// whose first item holds the next heading was laid out twice per level.
    /// <para>
    /// So does a table: it hands the probe to the flow of each cell until its first content is placed (see
    /// <see cref="LayoutRow"/>). What a table does to its cells afterwards only moves their content down, by
    /// vertical-align or by moving a row to the next page, or wraps it in a clip, which the scan still looks into; its
    /// own, its rows' and its cells' backgrounds and borders are not content. So once the first content is on a later
    /// page than the chain's, it stays there.
    /// </para>
    /// </remarks>
    private static bool CanProbe(Element e)
    {
        var s = e.Style;
        return !s.IsFlex && !s.IsGrid && !s.IsMultiColumn
               && !s.AvoidBreakInside && !MovesContent(s) && e.Control == null && !e.IsReplaced && e.Name != "hr";
    }

    /// <summary>
    /// Whether a box moves its content once laid out in a way that changes where it breaks: transforms do. Opacity,
    /// clips, masks, blend modes and filters only wrap the content in a layer where it is, which the scan for the first
    /// content still looks into, and a relative offset moves the content after its page breaks are made, as in Chrome.
    /// Without them, nested boxes with such effects or offsets each probed for their first content themselves, and
    /// each level laid out everything inside it once more.
    /// </summary>
    private static bool MovesContent(ComputedStyle s)
        => s.Position is not (Position.Static or Position.Relative) || s.Transform != null || s.Perspective != null;

    /// <summary>The first content op of a probed box: its index among the ops and where it starts.</summary>
    private (int At, float Y)? FirstContent(KeepProbe p)
    {
        for (int i = p.ScanFrom; i < _ops.Count; i++)
            if (FirstContentY(_ops[i]) is { } y) { p.ScanFrom = i; return (i, y); }
        // Ops of boxes that are complete do not change, so the next search starts after them.
        p.ScanFrom = _ops.Count;
        return null;
    }

    /// <summary>Whether the next block-level box among the children from <paramref name="from"/> asks to stay with the box before it.</summary>
    private static bool NextBoxAvoidsBreakBefore(List<Node> children, int from)
    {
        for (int i = from; i < children.Count; i++)
        {
            if (children[i] is not Element e || e.Style is not { } s || s.Display == Display.None || e.Detached
                || s.Floated || s.OutOfFlow || IsInlineLevel(e)) continue;
            return s.AvoidBreakBefore;
        }
        return false;
    }

    /// <summary>
    /// Whether the content laid out from <paramref name="opsStart"/> begins on a later page than the chain that must stay
    /// with it, and moving the chain to a new page would help.
    /// </summary>
    /// <remarks>
    /// Chains nested in more boxes that are themselves kept with a chain than <see cref="MaxKeptWholeNesting"/> stay
    /// where they are: moving a chain lays out everything in it again, at every level of such nesting, so the work
    /// doubled with each level. So do all chains once the relayout budget is spent (see <see cref="MayLayOutAgain"/>):
    /// headings left open, each holding the next, still moved their chains at each of those levels, and a page of 578
    /// such levels took 48 s.
    /// </remarks>
    private bool KeepTogetherFails(in FlowMark chain, int opsStart, float fallbackY, int forcedAt)
        => chain.Index != forcedAt && !AtPageTop(chain.Top) && _joinDepth < MaxKeptWholeNesting && MayLayOutAgain
           && FirstContentPage(opsStart, fallbackY) > chain.FirstPage;

    /// <summary>How many boxes that keep-with-next chains may move to the next page are being laid out around this point.</summary>
    private int _joinDepth;

    /// <summary>The page of the first text, image or drawing among the ops from <paramref name="from"/>.</summary>
    private int FirstContentPage(int from, float fallbackY) => FirstContentPage(from, _ops.Count, fallbackY);

    private int FirstContentPage(int from, int end, float fallbackY)
    {
        for (int i = from; i < end; i++)
            if (FirstContentY(_ops[i]) is { } fy) return PageOf(fy);
        return PageOf(fallbackY);
    }

    private static float? FirstContentY(DrawOp op)
    {
        switch (op)
        {
            case TextOp t: return t.LineTop;
            case ImageOp im: return im.Y;
            case SvgOp sv: return sv.Y;
            case GroupOp g:
                foreach (var o in g.Ops)
                    if (FirstContentY(o) is { } y) return y;
                return null;
            default: return null;
        }
    }

    /// <summary>
    /// The height of a containing block on each kind of page (the grid's first, odd and even pages). It is the same on
    /// all of them unless it comes from the page area through percentage heights and the pages differ in height.
    /// </summary>
    private readonly struct CbHeights(float first, float odd, float even)
    {
        public readonly float First = first, Odd = odd, Even = even;

        public CbHeights(float h) : this(h, h, h) { }

        public bool Uniform => First == Odd && Odd == Even;

        public float On(int kind) => kind == 0 ? First : kind == 1 ? Odd : Even;
    }

    /// <summary>
    /// The height of the containing block of the boxes laid out next, which their percentage heights resolve against,
    /// or null when it is not definite and percentage heights behave as auto (CSS 2.1 §10.5): the content height of a
    /// box whose height is definite, the page area for the root element, or the padding box of the containing block of
    /// an absolutely positioned box.
    /// </summary>
    private CbHeights? _cbHeight;

    /// <summary>
    /// The kind of page (see <see cref="PageBox.Kind"/>) the box or line being laid out starts on, which picks its
    /// containing block's height from <see cref="_cbHeight"/>. Content laid out on its own, unpaginated, keeps the kind
    /// of the page its container is on.
    /// </summary>
    private int _cbKind;

    /// <summary>
    /// The containing block height that the percentage heights of <paramref name="e"/> resolve against, or null. Intrinsic
    /// widths are measured without one, so that they stay the same wherever the box is, and a grid resolves its items'
    /// percentages against their grid areas itself.
    /// </summary>
    private float? HeightBasis(Element e, int kind)
        => _cbHeight is { } cb && !_measuring && (e.Parent?.Style is not { IsGrid: true } || e.Style.OutOfFlow) ? cb.On(kind) : null;

    private float? HeightBasis(Element e) => HeightBasis(e, _cbKind);

    /// <summary>Notes the kind of page that content laid out at <paramref name="y"/> starts on.</summary>
    private void NoteCbKind(float y)
    {
        if (_paginate && _cbHeight is { Uniform: false }) _cbKind = PageBox.Kind(PageOf(y));
    }

    /// <summary>
    /// Whether a child of <paramref name="e"/> has a height, min-height or max-height that is a percentage, which then
    /// depends on how tall <paramref name="e"/> is made. Only children count: a grandchild's percentage refers to its
    /// own parent, which is definite only through a percentage or length of its own.
    /// </summary>
    private static bool HasPercentHeightChild(Element e)
    {
        foreach (var c in e.Children)
            if (c is Element { Style: { } s } && (s.Height.IsPercent || s.MinHeight.IsPercent || s.MaxHeight.IsPercent)) return true;
        return false;
    }

    // Margin collapsing between a box and its children: set by LayoutBlockBoxCore and LayoutFlow for their callers.
    private bool _absorbTop;      // the box being laid out has taken its first child's top margin
    private float _flowTrailing;  // the bottom margin of the last child of the flow just laid out
    private float _boxTrailing;   // the bottom margin of its last child that the box just laid out passes on

    /// <summary>
    /// A box in normal flow whose margins can collapse with its children's (not a flex, grid, table or column box, nor a
    /// flex or grid item).
    /// </summary>
    private static bool IsPlainFlow(Element e)
    {
        var s = e.Style;
        return s.Display is Display.Block or Display.ListItem && !s.IsMultiColumn && !s.Floated && !s.OutOfFlow && !s.FormattingRoot
               && !e.IsReplaced && e.Name is not ("hr" or "fieldset") && !IsFlexOrGridItem(e);
    }

    /// <summary>
    /// True for a child of a flex or grid container, seen through display: contents. Such an item is a formatting
    /// context of its own, so its first and last children's margins stay inside it, as in Chrome.
    /// </summary>
    private static bool IsFlexOrGridItem(Element e)
    {
        var p = e.Parent;
        while (p is { Style.Contents: true }) p = p.Parent;
        return p?.Style is { } ps && (ps.IsFlex || ps.IsGrid);
    }

    // The root element's margins do not collapse with its children's (CSS 2.1 §8.3.1).
    private static bool IsRoot(Element e) => e.Parent is { Name: "#document" };

    private static bool CollapsesTop(Element e)
        => IsPlainFlow(e) && !IsRoot(e) && e.Style.BorderTop == 0 && e.Style.PaddingTop.Resolve(0) == 0 && !e.Style.PaddingTop.IsPercent;

    private static bool CollapsesBottom(Element e)
    {
        var s = e.Style;
        return IsPlainFlow(e) && !IsRoot(e) && s.BorderBottom == 0 && s.PaddingBottom.Resolve(0) == 0 && !s.PaddingBottom.IsPercent
               && s.Height.IsAuto && (s.MinHeight.IsAuto || s.MinHeight.Value <= 0);
    }

    private static bool CollapsesThrough(Element e) => CollapsesTop(e) && CollapsesBottom(e);

    /// <summary>The first child of a box whose top margin collapses with the box's, or null.</summary>
    private static Element? FirstBlockChild(Element e)
    {
        if (!CollapsesTop(e)) return null;
        foreach (var child in e.Children)
        {
            if (child is TextNode t)
            {
                if (string.IsNullOrWhiteSpace(t.Text) && IsCollapsible(e.Style)) continue;
                return null;
            }
            var c = (Element)child;
            if (c.Style == null || c.Style.Display == Display.None || c.Detached || c.Style.OutOfFlow || c.Style.Floated) continue;
            if (IsInlineLevel(c) || c.Style.PageBreakBefore || c.Style.ClearLeft || c.Style.ClearRight) return null;
            return c;
        }
        return null;
    }

    /// <summary>The collapsed top margin of a box's first children, which the box takes as part of its own.</summary>
    private static float LeadingMargin(Element e, float w)
    {
        var c = FirstBlockChild(e);
        return c == null ? 0 : CollapseMargins(c.Style.MarginTop.Resolve(w), LeadingMargin(c, w));
    }

    /// <summary>
    /// A box kept whole by break-inside: avoid, tried at the top of the next column from (x, y) at a width, in a grid
    /// whose next column has a height.
    /// </summary>
    private readonly record struct AvoidTrial(Element El, PageGrid Grid, float X, float Y, float W, float NextHeight);

    /// <summary>
    /// The trials of <see cref="AvoidTrial"/> that did not fit, after which the box was laid out again where it was. A
    /// box around it that goes back too lays it out there once more, and without this every level of such boxes in
    /// columns would repeat the trials of all the levels inside it, doubling the work per level.
    /// </summary>
    private HashSet<AvoidTrial>? _failedAvoidTrials;

    /// <summary>
    /// How many times a block-level box, a table cell or a run of inline content has been laid out, counting each time it
    /// was laid out again. Cells and inline runs count because they hold most of the work in a box of tables or text, and
    /// a run counts once more for every <see cref="CharactersPerLayout"/> characters of its text: a word of 80,000
    /// characters in 46 boxes nested in each other took as long as 2,300 layouts of the boxes around it would have
    /// alone, so each level laid it out again well within the budget, for 4.7 s.
    /// </summary>
    private long _boxLayouts;
    private const int CharactersPerLayout = 32;

    /// <summary>The box layouts so far (see <see cref="_boxLayouts"/>), by which a band laid out for every page is charged.</summary>
    public long BoxLayouts => _boxLayouts;

    /// <summary>
    /// The box layouts after which a box is no longer laid out again only to place a page break better: moved to the next
    /// page to keep it whole or to keep its edges with its content. Each such layout lays out everything inside the box
    /// again, and nested boxes that each try it multiply the work. Every limit on the depth of that still left nesting
    /// to the power of the limit, which a page of nested boxes reached; real documents stay far below the budget.
    /// A document is laid out run by run of named pages, again for the page numbers of target-counter() and again to
    /// shrink it to fit, and the base of the budget is for all of those layouts together (see
    /// <see cref="SharedRelayouts"/>), while the share per element is for each: with a base for each, a page of 460
    /// nested boxes kept whole in columns used most of it in each of its twelve layouts, for 4 s.
    /// </summary>
    private long _relayoutBudget = long.MaxValue;
    public const long BaseRelayouts = 250_000;
    private const int RelayoutsPerElement = 64;

    /// <summary>Whether a box may still be laid out again to place a page break better (see <see cref="_relayoutBudget"/>).</summary>
    private bool MayLayOutAgain => _boxLayouts < _relayoutBudget;

    private static long CountElements(Element e)
    {
        long n = 1;
        foreach (var c in e.Children)
            if (c is Element child) n += CountElements(child);
        return n;
    }

    /// <summary>How many boxes kept whole by break-inside: avoid are being laid out, one inside the other.</summary>
    private int _avoidDepth;

    /// <summary>
    /// The most boxes kept whole that a box in columns can be nested in and still be tried in the next column. Each
    /// level tries the levels inside it again at a new position, and balancing repeats that for each height it tries,
    /// so the work grew with the cube of the depth; deeper boxes break where they fall. On pages a box is only moved
    /// when it fits on the next one, which costs one more layout per level.
    /// </summary>
    private const int MaxKeptWholeNesting = 8;

    /// <summary>
    /// How many boxes kept whole, counting the box itself, a box in columns that looks taller than the next column can
    /// be nested in and still be tried there.
    /// </summary>
    private const int MaxTriedInColumnsNesting = 6;

    /// <summary>Lays out a block-level box whose top border edge is at <paramref name="y"/>. Returns its bottom border edge.</summary>
    private float LayoutBlockBox(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        Cancellation.ThrowIfCancellationRequested();
        CheckPageLimit(y);
        if (OrthogonalTurn(e) is int turn and not 0) return LayoutOrthogonal(e, x, y, availW, turn);
        var s = e.Style;
        bool table = s.Display == Display.Table;
        int opsStart = _ops.Count;
        var savedMarker = _marker;
        int savedListIndex = _currentListIndex;
        var probe = _childProbe;
        // The last content of the flow around the box stays its last unless the box has content of its own.
        float lastContent = _lastContentBottom;
        _lastContentBottom = float.NaN;
        // The containing block on each kind of page, when pages differ in width, which the box takes on.
        var pages = _childPageBox;
        _childPageBox = null;
        if (!table) _childPageBox = pages;
        // A box scaled to fit is drawn whole, so like a box kept whole it goes to the next page rather than across.
        bool avoidInside = s.AvoidBreakInside || s.ScaleToFit || s.AvoidColumnBreakInside && _grid is ColumnGrid;
        bool movable = _avoidDepth < MaxKeptWholeNesting || _grid is not ColumnGrid;
        if (avoidInside) _avoidDepth++;
        bool edges = !table && _paginate && _grid is not ColumnGrid && KeepsEdgesWithContent(e);
        // The box's layout stops at its first content when that lands on a later page: the box then goes there whole.
        // Stopping means laying the box out again, so past the budget its edges are looked at once it is laid out.
        var edgeProbe = edges && probe == null && _edgeProbesOff == 0 && !avoidInside && MayLayOutAgain && !AtPageTop(y)
                        && ProbesTopEdge(e)
            ? new KeepProbe(opsStart, PageOf(y)) { Edge = true }
            : null;
        if (edgeProbe != null) _childProbe = edgeProbe;
        float bottom = table ? LayoutTable(e, x, y, availW, pages) : LayoutBlockBoxCore(e, x, y, availW, shrinkToFit);
        // A first child laid out with its parent's probe goes along with the parent: its own top edge is not content.
        if (probe is { Edge: true } && opsStart < _ops.Count && _ops[opsStart] is GroupOp chained) (probe.Chained ??= new()).Add(chained);
        if (avoidInside && movable && CrossesPage(y, bottom) && !AtPageTop(y))
        {
            // A box split at the end of a column also measures the room it left there, so in columns one that is no
            // taller than the next column can look taller: it is tried there and kept only if it then fits whole.
            float nextHeight = _grid.Height(PageOf(y) + 1);
            bool fitsNext = bottom - y <= nextHeight;
            // A trial that failed here before would end with the box laid out where it is, which it just was.
            var trial = new AvoidTrial(e, _grid, x, y, availW, nextHeight);
            bool failedBefore = !fitsNext && _failedAvoidTrials?.Contains(trial) == true;
            // A box nested in more than six boxes kept whole in columns is tried there only when it fits: each box around
            // it is tried there too, and with each level trying the levels inside it again, the work doubled per level.
            if (!failedBefore && MayLayOutAgain && (fitsNext || _grid is ColumnGrid && _avoidDepth <= MaxTriedInColumnsNesting))
            {
                float oy = y, ox = x, oavailW = availW;
                int page = PageOf(y);
                Truncate(opsStart);
                _marker = savedMarker;
                float ny = NextPage(y);
                if (pages != null)
                {
                    (x, availW) = OnPage(pages, ny);
                    if (!table) _childPageBox = pages;
                }
                y = ny;
                bottom = table ? LayoutTable(e, x, ny, availW, pages) : LayoutBlockBoxCore(e, x, ny, availW, shrinkToFit);
                if (!CrossesPage(ny, bottom))
                {
                    // Columns taller by this much would have kept the whole box where it started.
                    _columnNotes?.Break(page, oy, oy + (bottom - ny) - FlowEnd(page));
                }
                else if (!fitsNext)
                {
                    (_failedAvoidTrials ??= new HashSet<AvoidTrial>()).Add(trial);
                    Truncate(opsStart);
                    _marker = savedMarker;
                    (y, x, availW) = (oy, ox, oavailW);
                    _childPageBox = table ? null : pages;
                    bottom = table ? LayoutTable(e, x, y, availW, pages) : LayoutBlockBoxCore(e, x, y, availW, shrinkToFit);
                }
            }
        }
        if (edges && probe is not { Done: true })
            bottom = KeepEdgesWithContent(e, ref x, ref y, ref availW, shrinkToFit, pages, opsStart, savedMarker, savedListIndex, edgeProbe, bottom);
        // Columns being balanced start at least as tall as the tallest box that must stay whole, as in Chrome, so
        // such a box is never split only because the first height tried was short.
        if (avoidInside && !CrossesPage(y, bottom)) _columnNotes?.Unbreakable(PageOf(y), bottom - y);
        // A box without content of its own, such as an empty one with a height, ends the content around it itself.
        if (float.IsNaN(_lastContentBottom)) _lastContentBottom = bottom - y > Eps ? bottom : lastContent;
        if (table) _boxTrailing = 0;
        if (s.Layered)
        {
            FinishLayered(e, opsStart, availW);
            // The box's ops are now one layer at opsStart: a probe it took scans that layer, not past where its ops ended.
            if (probe != null) probe.ScanFrom = Math.Min(probe.ScanFrom, opsStart);
        }
        if (avoidInside) _avoidDepth--;
        return bottom;
    }

    /// <summary>Starts the marker of a list item whose content box starts at <paramref name="cx"/>.</summary>
    // Kept out of LayoutBlockBoxCore, as is DrawReplaced, so that its stack frame stays small: nested boxes call it once
    // per level, and 256 levels of flex rows must fit in the 1.5 MB stack of a thread-pool thread on Windows.
    private void StartMarker(Element e, float cx, float contentW)
    {
        var s = e.Style;
        // ::marker { content } and list-style-type: "string" give the marker's own text, drawn as written.
        var custom = e.MarkerContent ?? s.ListStyleString;
        var marker = new ListMarker
        {
            Custom = custom != null, Type = s.ListStyleType,
            Style = e.MarkerStyle ?? s, ContentX = cx, ContentRight = cx + contentW, Rtl = s.Rtl, Tag = Tags?.MarkerOf(e),
            Inside = s.ListStylePositionInside,
        };
        // list-style-image wins over the type unless ::marker has content; an image that does not load falls back to the type.
        if (e.MarkerContent == null && s.ListStyleGradient is { } gradient)
        {
            marker.Gradient = gradient;
            marker.ImageW = marker.ImageH = DefaultMarkerImageSide(marker.Style);
        }
        else if (e.MarkerContent != null || s.ListStyleImage is not { } imageSrc || !LoadMarkerImage(marker, imageSrc, s.ImageOrientationNone))
            marker.Text = custom ?? s.ListCounterStyle?.MarkerText(_currentListIndex);
        if (marker.Text != null || marker.Image != null || marker.Svg != null || marker.Gradient != null || s.ListStyleType != ListStyleType.None)
            _marker = marker;
    }

    /// <summary>
    /// Draws an image or SVG element's picture in its content box at (<paramref name="cx"/>, <paramref name="cy"/>), with
    /// border and padding of <paramref name="left"/>, <paramref name="top"/>, <paramref name="right"/> and
    /// <paramref name="bottom"/> around it.
    /// </summary>
    private void DrawReplaced(Element e, PdfImage? image, SvgImage? svg, float cx, float cy, float imgW, float imgH,
        float left, float top, float right, float bottom)
    {
        var s = e.Style;
        if (image == null && e.Name == "svg")
        {
            _ops.Add(new SvgOp(cx, cy, imgW, imgH, svg!) { Tag = Tags?.Of(e) });
            return;
        }
        // Rounded corners clip the image to the content box's curve.
        var radii = BoxPainter.InnerRadii(BoxPainter.Radii(s, imgW + left + right, imgH + top + bottom), left, top, right, bottom);
        if (image != null) AddImage(s, image, _images.DensityOf(e), cx, cy, imgW, imgH, radii, Tags?.Of(e));
        if (svg != null) AddSvgImage(_ops, s, svg, cx, cy, imgW, imgH, radii, Tags?.Of(e));
    }

    private float LayoutBlockBoxCore(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        _boxLayouts++;
        var parentPages = _childPageBox;
        _childPageBox = null;
        var probe = _childProbe;
        _childProbe = null;
        var s = e.Style;
        if (e.Id != null) SetAnchor(e.Id, y);
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) SetAnchor(aname, y);

        float ml = s.MarginLeft.Resolve(availW), mr = s.MarginRight.Resolve(availW);
        float bl = s.BorderLeft, br = s.BorderRight, bt = s.BorderTop, bb = s.BorderBottom;
        float pl = s.PaddingLeft.Resolve(availW), pr = s.PaddingRight.Resolve(availW);
        float pt = s.PaddingTop.Resolve(availW), pb = s.PaddingBottom.Resolve(availW);
        float frame = bl + br + pl + pr;
        float frameV = bt + bb + pt + pb;
        NoteCbKind(y);
        float? cbH = HeightBasis(e);

        // Sizes imposed by a flex or grid container (border-box).
        bool hasWOverride = _widthOverride.TryGetValue(e, out float overrideW);
        bool hasHOverride = _heightOverride.TryGetValue(e, out float overrideH);

        bool isImage = e.IsPicture, isSvg = e.Name == "svg";
        float imgW = 0, imgH = 0;
        PdfImage? image = null;
        SvgImage? svg = null;
        float contentW, replacedExcess = 0;
        if (isImage || isSvg)
        {
            float room = hasWOverride ? overrideW - frame : availW - ml - mr - frame;
            // Percentages are of the containing block, not of what its frame and margins leave, so that a width of 100%
            // with a border or margin overflows the box, as in Chrome.
            if (isImage) image = ImageSize(e, room, out imgW, out imgH, out svg, availW);
            else svg = SvgSize(e, room, out imgW, out imgH, availW);
            // A flex or grid container that sized the box decides how far it reaches.
            replacedExcess = hasWOverride ? 0 : _replacedExcess;
            if (hasWOverride && imgW > 0 && Math.Abs(room - imgW) > Eps)
            {
                if (s.Height.IsAuto || s.Height.IsPercent && cbH == null) imgH *= Math.Max(0, room) / imgW;
                imgW = Math.Max(0, room);
            }
            // A height imposed by the container (a stretched or flexed item) is the picture's height, as in Chrome,
            // within its min-height and max-height.
            if (hasHOverride)
            {
                float boxFrame = s.BorderBox ? frameV : 0;
                imgH = overrideH - frameV;
                if (HeightOf(s.MaxHeight, cbH) is float maxH) imgH = Math.Min(imgH, maxH - boxFrame);
                if (MinHeightOf(s, cbH) is float minH) imgH = Math.Max(imgH, minH - boxFrame);
                imgH = Math.Max(0, imgH);
            }
            contentW = imgW;
        }
        else if (hasWOverride)
        {
            contentW = overrideW - frame;
        }
        else contentW = FlowContentWidth(e, availW, ml, mr, frame, frameV, shrinkToFit, cbH);
        if (contentW < 0) contentW = 0;

        float used = contentW + frame;
        ml = UsedMarginLeft(e, availW, used, ml, mr, shrinkToFit || hasWOverride || s.WidthKeyword != SizeKeyword.None);
        float bx = x + ml;

        int opsStart = _ops.Count;
        var group = new GroupOp { Reach = bx + used + replacedExcess };
        _ops.Add(group);

        // Positioned and transformed boxes are the containing block of their absolutely positioned descendants.
        CbFrame? cbFrame = null;
        // A filter or a perspective makes the box their containing block too, even for fixed ones, as a transform does.
        bool contains = s.Transform != null || s.Perspective != null || s.Filters != null;
        if (s.Positioned || contains) _frames.Add(cbFrame = new CbFrame { Transformed = contains });
        // Boxes that start a block formatting context keep their floats inside (CSS 2.1 §9.4.1).
        bool formattingRoot = shrinkToFit || hasWOverride || hasHOverride || s.Floated || s.OutOfFlow || s.FormattingRoot
                              || s.IsFlex || s.IsGrid || s.IsMultiColumn || s.Display is Display.TableCell or Display.TableCaption
                              || e.Name == "fieldset"
                              || e.Parent?.Style is { IsFlex: true } or { IsGrid: true };
        // On pages of different widths a box in normal flow is as wide on each page as that page makes it.
        var pages = parentPages != null && FollowsPages(e, shrinkToFit, hasWOverride || hasHOverride || isImage || isSvg, formattingRoot)
            ? PageGeometry(e, parentPages)
            : null;
        if (pages is { Varies: false }) pages = null;

        float cx = bx + bl + pl;
        float cy = y + bt + pt;

        // A fieldset's legend sits on its top border instead of in its content, which starts below both.
        var legend = e.Name == "fieldset" && !isImage && !isSvg ? RenderedLegend(e) : null;
        (float X, float Y, float W, float H)? borderGap = null;
        float borderOutset = 0, legendExtra = 0;
        if (legend != null)
        {
            float band = LayoutLegend(legend, cx, y, contentW, bt, out var gap, out borderOutset);
            borderGap = gap;
            cy = y + band + pt;
            legendExtra = band - bt;
            frameV += legendExtra;
        }
        // What a height leaves out of the content box: the frame for border-box sizing; otherwise, as in Chrome, the
        // part of a legend below the top border, which only a fieldset has.
        float sizing = s.BorderBox ? frameV : legendExtra;

        if (s.Display == Display.ListItem && (s.ListStyleType != ListStyleType.None || e.MarkerContent != null || s.ListStyleImage != null || s.ListStyleGradient != null))
            StartMarker(e, cx, contentW);

        float contentBottom;
        float trailing = 0;
        CbHeights? childCb = null;
        if (isImage || isSvg)
        {
            if (_paginate)
            {
                float fitted = FitOnPage(y, imgH + bt + bb + pt + pb);
                if (fitted != y)
                {
                    y = fitted;
                    cy = y + bt + pt;
                }
            }
            if (s.Visible && (image != null || svg != null)) DrawReplaced(e, image, svg, cx, cy, imgW, imgH, bl + pl, bt + pt, br + pr, bb + pb);
            contentBottom = cy + imgH;
            if (_paginate) _lastContentBottom = contentBottom;
        }
        else if (e.Name == "hr")
        {
            contentBottom = cy;
        }
        else
        {
            float? definiteH = null;
            if (hasHOverride) definiteH = Math.Max(0, overrideH - frameV);
            else if (ClampedHeight(s, cbH) is float h) definiteH = Math.Max(0, h - sizing);
            float minH = MinHeightOf(s, cbH) is float mnh ? Math.Max(0, mnh - sizing) : 0;
            float? ratioMin = RatioHeight(s, contentW, frame, frameV, hasHOverride, cbH);
            if (ratioMin is float rh) minH = Math.Max(minH, Math.Min(rh, MaxContentHeight(s, sizing, cbH)));
            // A box that is not a formatting context root shares its top and bottom margins with its children's.
            bool joinsChildren = !shrinkToFit && !hasWOverride && !hasHOverride;
            _absorbTop = joinsChildren && FirstBlockChild(e) != null;
            var outerFloats = formattingRoot ? EnterFormattingContext() : null;
            _pageBox = pages;
            _flowProbe = probe;
            var outerCb = _cbHeight;
            _cbHeight = hasHOverride && _indefiniteOverride is { Count: > 0 } indefinite && indefinite.Contains(e)
                ? null
                : ChildHeights(e, hasHOverride, definiteH, ratioMin != null ? minH : null, frameV);
            childCb = _cbHeight;
            // The legend was laid out already: the content leaves it out.
            if (legend != null) legend.Detached = true;
            try
            {
                contentBottom = s.ScaleToFit && FitHeight(e, definiteH, MaxContentHeight(s, sizing, cbH)) is float fitH
                    ? LayoutScaledToFit(e, cx, cy, contentW, definiteH, minH, fitH)
                    : LayoutContent(e, cx, cy, contentW, definiteH, minH);
            }
            finally { if (legend != null) legend.Detached = false; }
            _cbHeight = outerCb;
            _pageBox = null;
            if (outerFloats != null) contentBottom = LeaveFormattingContext(outerFloats, contentBottom);
            _absorbTop = false;
            if (joinsChildren && CollapsesBottom(e) && !s.IsFlex && !s.IsGrid)
            {
                trailing = _flowTrailing;
                contentBottom -= trailing;
            }
        }

        if (_marker != null && _marker.Style == s)
        {
            // List item without any line of text (e.g. it only holds a nested list): put the marker at the top.
            EmitMarker(cy + s.Font.Ascent * s.TextSize, cy);
        }

        // Size containment (container-type: size): the box is as tall as it would be without content.
        bool container = HasSizeContainment(s) && !isImage && !isSvg;
        float contentH = container && s.ContainerType == ContainerType.Size ? 0 : contentBottom - cy;
        if (hasHOverride && !isImage && !isSvg)
        {
            contentH = Math.Max(0, overrideH - frameV);
        }
        else if (HeightOf(s.Height, cbH) is float specifiedH)
        {
            float h = specifiedH - sizing;
            contentH = Math.Max(0, h);
        }
        else if (e.Control is { Multiline: true } textarea)
        {
            // A textarea with an auto height shows its rows whatever its content, with its padding and borders around
            // them whatever its box-sizing, as in Chrome.
            contentH = textarea.Rows * s.LineHeight;
        }
        // aspect-ratio with an auto height: at least the ratio's height; taller content still makes it grow, except in a
        // flex container, whose content overflows it as in Chrome.
        if (!isImage && !isSvg && RatioHeight(s, contentW, frame, frameV, hasHOverride, cbH) is float ratioH && (contentH < ratioH || s.IsFlex))
            contentH = ratioH;
        // max-height limits the box, and content beyond it overflows; min-height wins over max-height. Flex and grid
        // containers that size the box have applied both already.
        if (!isImage && !isSvg && !hasHOverride) contentH = Math.Min(contentH, MaxContentHeight(s, sizing, cbH));
        if (MinHeightOf(s, cbH) is float minHeight)
        {
            float mh = minHeight - sizing;
            if (contentH < mh) contentH = mh;
        }
        if (container && !_measuring)
        {
            // The next style pass evaluates container queries and cq units against this size.
            e.ContainerWidth = contentW;
            e.ContainerHeight = contentH;
        }

        float bottom = cy + contentH + pb + bb;
        // A legend taller than the top border moves the border down to its middle, and the background with it.
        float paintTop = y + borderOutset;
        if (pages != null && !SameOnPages(pages, y, bottom)) PaintOnPages(group, s, pages, paintTop, bottom, borderGap: borderGap);
        else BoxPainter.Paint(group, s, bx, paintTop, used, bottom - paintTop, _images, availW, borderGap: borderGap);
        if (BoxPainter.ClipsToText(s)) ClipBackgroundToText(group, opsStart + 1);
        if (e.Control is { } control) FinishControl(e, control, opsStart + 1, bx, y, used, bottom - y, contentBottom - cy, contentH);
        if (e == CanvasRoot) CanvasRootBox = (bx, y, used, bottom - y);
        if (s.IsFlex) NoteFlexStaticArea(e, contentW, contentH);
        if (cbFrame != null)
        {
            _frames.Remove(cbFrame);
            LayoutAbsolutes(cbFrame, bx + bl, y + bt, used - bl - br, bottom - y - bt - bb);
        }
        if ((s.ClipsX || s.ClipsY) && !isImage && !isSvg && _ops.Count > opsStart + 1)
            WrapClip(opsStart + 1, s, bx, y, used, bottom - y);
        _lastBoxRight = bx + used;
        _lastBoxX = bx;
        _lastBoxTop = y;
        _lastBoxW = used;
        _lastBoxH = bottom - y;
        _lastBoxContentH = contentH;
        _lastBoxFrameV = frameV;
        _lastBoxChildCb = childCb;
        if (e.Name == "a" && e.GetAttribute("href") is { Length: > 0 } href) _ops.Add(new LinkOp(bx, y, used, bottom - y, href) { Link = Tags?.ElementOf(e) });
        // A box taken out of a display: contents link links on its own.
        else if (e.ContentsHref != null) _ops.Add(new LinkOp(bx, y, used, bottom - y, e.ContentsHref));
        // A box whose layout stopped early is laid out again in full right away; recording it now would put it in the
        // outline ahead of the headings inside it that were not reached yet. A grid row is laid out to see where its
        // content starts before the rows above it are placed, so headings found then would come out of order.
        if (Headings != null && (s.BookmarkLevel ?? HeadingLevel(e.Name)) is int level and > 0 && probe is not { Done: true } && !_probingGridRow)
            RecordHeading(e, level, y, opsStart);
        if (e.LayoutKey != null) SetAnchor(e.LayoutKey, ContentTop(y, opsStart));
        _boxTrailing = trailing;
        return bottom;
    }

    /// <summary>
    /// The legend a fieldset renders on its top border: its first legend child that is shown and neither floated nor
    /// absolutely positioned, as in Chrome; any other legend is an ordinary box of its content.
    /// </summary>
    private static Element? RenderedLegend(Element fieldset)
    {
        foreach (var c in fieldset.Children)
            if (c is Element { Name: "legend", Detached: false, Style: { Display: not Display.None, Floated: false, OutOfFlow: false } } legend)
                return legend;
        return null;
    }

    /// <summary>
    /// Lays out a fieldset's legend over its top border, <paramref name="bt"/> thick, at the top <paramref name="y"/> of
    /// the fieldset's border box, as wide as its content and placed along the content box (x, w) by its auto margins
    /// or text-align, as Chrome does. A legend thinner than the border is centred in it. Returns how far below
    /// <paramref name="y"/> the padding box starts: below the border or the legend's bottom margin, whichever is lower.
    /// <paramref name="gap"/> is the legend's border box down to that border's bottom, where the border is not painted,
    /// and <paramref name="outset"/> how far a taller legend moves the border down, to its middle.
    /// </summary>
    private float LayoutLegend(Element legend, float x, float y, float w, float bt, out (float X, float Y, float W, float H) gap, out float outset)
    {
        var ls = legend.Style;
        int start = _ops.Count;
        float bottom = LayoutBlockBox(legend, x, y, w, shrinkToFit: true);
        float lx = _lastBoxX, lw = _lastBoxW, lh = bottom - y;
        float to = lx;
        // Auto margins have placed it already; otherwise the legend's own text-align does (the align attribute maps to it).
        if (!ls.MarginLeft.IsAuto && !ls.MarginRight.IsAuto)
        {
            to = ComputedStyle.Resolve(ls.TextAlign, ls.Rtl) switch
            {
                TextAlign.Center => x + (w - lw) / 2,
                TextAlign.Right => x + w - lw - ls.MarginRight.Resolve(w),
                _ => x + ls.MarginLeft.Resolve(w),
            };
        }
        float dy = Math.Max(0, (bt - lh) / 2);
        if (to != lx || dy > 0)
            for (int i = start; i < _ops.Count; i++) _ops[i].Offset(to - lx, dy);
        gap = (to, y, lw, Math.Max(lh, bt));
        outset = Math.Max(0, (lh - bt) / 2);
        // The legend's top margin does not move it; its bottom margin keeps the content away from it.
        return Math.Max(bt, dy + lh + ls.MarginBottom.Resolve(w));
    }

    /// <summary>
    /// The content width of a block box that is not replaced and has no size imposed by a flex or grid container, from
    /// its width, aspect-ratio and min- and max-width, in a containing block <paramref name="availW"/> wide and
    /// <paramref name="cbH"/> tall (null when that height is not definite).
    /// </summary>
    private float FlowContentWidth(Element e, float availW, float ml, float mr, float frame, float frameV, bool shrinkToFit, float? cbH)
    {
        var s = e.Style;
        float contentW;
        if (!s.Width.IsAuto)
        {
            contentW = s.Width.Resolve(availW);
            if (s.BorderBox) contentW -= frame;
        }
        else if (s.WidthKeyword != SizeKeyword.None)
        {
            // min-content, max-content and fit-content size the box by its content, as a shrink-to-fit box is sized,
            // except that max-content may overflow the containing block and min-content wraps at every chance.
            var (min, max) = Intrinsic(e);
            contentW = s.WidthKeyword switch
            {
                SizeKeyword.MinContent => min,
                SizeKeyword.MaxContent => max,
                _ => Math.Max(min, Math.Min(max, availW - ml - mr)),
            } - frame;
        }
        else if (s.AspectRatio > 0 && ClampedHeight(s, cbH) is float ratioFrom)
        {
            // aspect-ratio with a fixed height and an auto width: the width follows from the height. With min-width: auto
            // and visible overflow, content wider than that still widens the box (CSS Sizing 4 automatic minimum size).
            contentW = RatioWidth(s, ratioFrom, frame, frameV);
            if (s.MinWidth.IsAuto && !s.ClipsX) contentW = Math.Max(contentW, Intrinsic(e).Min - frame);
        }
        else
        {
            contentW = availW - ml - mr - frame;
            if (shrinkToFit)
            {
                var (_, max) = Intrinsic(e);
                contentW = Math.Min(contentW, max - frame);
            }
            // min-height and max-height carry over to the width through the ratio.
            if (s.AspectRatio > 0)
            {
                var (tmin, tmax) = TransferredWidthLimits(s, frame, frameV, cbH);
                contentW = Math.Max(Math.Min(contentW, tmax), tmin);
            }
        }
        if (!s.MaxWidth.IsAuto)
        {
            float mw = s.MaxWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW > mw) contentW = mw;
        }
        if (!s.MinWidth.IsAuto)
        {
            float mnw = s.MinWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW < mnw) contentW = mnw;
        }
        return contentW;
    }

    /// <summary>
    /// The used left margin of a block box <paramref name="used"/> wide: auto margins share the free space, and in a
    /// right-to-left containing block an over-constrained box keeps its right margin, so it sits at the right.
    /// </summary>
    private static float UsedMarginLeft(Element e, float availW, float used, float ml, float mr, bool sized)
    {
        var s = e.Style;
        // An inline-block's auto margins are zero: text-align places it on its line. Flex and grid items keep theirs.
        if (s.IsAtomicInline && !s.Floated && !s.OutOfFlow && e.Parent?.Style is not { IsFlex: true } and not { IsGrid: true }) return ml;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) return Math.Max(0, (availW - used) / 2);
        if (s.MarginLeft.IsAuto) return Math.Max(0, availW - used - mr);
        if (!sized && !s.Floated && !s.OutOfFlow && e.Parent?.Style is { Rtl: true }) return Math.Max(0, availW - used - mr);
        if (!s.Floated && !s.OutOfFlow) ml += LegacyBlockShift(e, availW - used - ml - mr);
        return ml;
    }

    /// <summary>
    /// How far a block box with fixed margins moves into the <paramref name="free"/> space beside it in a container
    /// aligned with -webkit-center or -webkit-right (an align attribute or the center element), as Chrome moves it.
    /// </summary>
    private static float LegacyBlockShift(Element e, float free)
    {
        if (!(free > 0) || e.Parent?.Style?.BlockAlign is not { } align) return 0;
        return align switch { TextAlign.Center => free / 2, TextAlign.Right => free, _ => 0 };
    }

    /// <summary>
    /// Moves the ops from <paramref name="start"/>, the content of the border box (x, y, w, h), into a clip to its
    /// padding box, rounded like its inner border edge.
    /// </summary>
    private void WrapClip(int start, ComputedStyle s, float x, float y, float w, float h)
    {
        float bl = s.BorderLeft, bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom;
        float px = x + bl, py = y + bt, pw = Math.Max(0, w - bl - br), ph = Math.Max(0, h - bt - bb);
        // Rounded corners only clip when both axes do.
        var radii = s.ClipsX && s.ClipsY ? BoxPainter.InnerRadii(BoxPainter.Radii(s, w, h), bl, bt, br, bb) : null;
        if (!s.ClipsX) { px -= ClipOp.Far; pw += 2 * ClipOp.Far; }
        if (!s.ClipsY) { py -= ClipOp.Far; ph += 2 * ClipOp.Far; }
        var clip = new ClipOp(px, py, pw, ph, radii) { OfViewport = IsViewportOverflow(s) };
        for (int i = start; i < _ops.Count; i++) clip.Ops.Add(_ops[i]);
        Truncate(start, keepFloats: true);
        _ops.Add(clip);
        _floats.Fold(start);
    }

    /// <summary>
    /// Whether a box's overflow is the one CSS gives the viewport: the root element's, or the body's when the root's is
    /// visible (CSS Overflow 3, 3.3).
    /// </summary>
    private bool IsViewportOverflow(ComputedStyle s)
        => CanvasRoot is { } root && (s == root.Style
            || !root.Style.ClipsX && !root.Style.ClipsY && root.Children.OfType<Element>().FirstOrDefault(c => c.Name == "body")?.Style == s);

    /// <summary>
    /// The containing block height a box gives its children when its own height is definite: a height that is a length
    /// or a percentage that resolves in turn, a size a flex or grid container gave it (<paramref name="definiteH"/>), or
    /// the height aspect-ratio gives its width (<paramref name="ratioH"/>, CSS Sizing 4 §5.1). Otherwise null, and their
    /// percentage heights behave as auto. A percentage of a containing block that differs between pages gives one that
    /// differs too, for the children to resolve against the page they start on.
    /// </summary>
    private CbHeights? ChildHeights(Element e, bool hasHOverride, float? definiteH, float? ratioH, float frameV)
    {
        if (definiteH is not float h) return ratioH is float r ? new CbHeights(r) : null;
        if (hasHOverride || _cbHeight is not { Uniform: false } cb || HeightBasis(e) == null) return new CbHeights(h);
        var s = e.Style;
        float ContentOn(int kind) => Math.Max(0, (ClampedHeight(s, cb.On(kind)) ?? 0) - (s.BorderBox ? frameV : 0));
        return new CbHeights(ContentOn(0), ContentOn(1), ContentOn(2));
    }

    /// <summary>Lays out the content box of a block container according to its display type.</summary>
    private float LayoutContent(Element e, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        if (s.IsFlex) return LayoutFlex(e, x, y, w, definiteH, minH);
        if (s.IsGrid) return LayoutGrid(e, x, y, w, definiteH, minH);
        if (s.IsMultiColumn) return LayoutColumns(e, x, y, w, definiteH);
        return LayoutFlow(e, x, y, w);
    }

    // ------------------------------------------------------------------ list markers

    /// <summary>
    /// Loads a list-style-image and sizes it as Chrome does: a raster image at its pixel size, an SVG drawing at its
    /// width and height, and a drawing without them at half the font's ascent (wide, with its viewBox's aspect ratio).
    /// </summary>
    private bool LoadMarkerImage(ListMarker m, string src, bool asStored)
    {
        var image = _images.Load(src, asStored);
        var svg = image == null ? _images.LoadSvg(src) : null;
        if (image == null && svg == null) return false;
        if (image != null)
        {
            if (image.Width <= 0 || image.Height <= 0) return false;
            m.Image = image;
            m.ImageW = image.Width * 0.75f;
            m.ImageH = image.Height * 0.75f;
            return true;
        }
        float side = DefaultMarkerImageSide(m.Style);
        float? ratio = svg!.HasViewBox && svg.ViewBoxWidth > 0 && svg.ViewBoxHeight > 0 ? svg.ViewBoxHeight / svg.ViewBoxWidth : null;
        if (svg.Width is { } w && svg.Height is { } h) (m.ImageW, m.ImageH) = (w, h);
        else if (svg.Width is { } w1 && ratio is { } r1) (m.ImageW, m.ImageH) = (w1, w1 * r1);
        else if (svg.Height is { } h1 && ratio is { } r2) (m.ImageW, m.ImageH) = (h1 / r2, h1);
        else if (ratio is { } r3) (m.ImageW, m.ImageH) = (side, side * r3);
        else (m.ImageW, m.ImageH) = (side, side);
        if (m.ImageW <= 0 || m.ImageH <= 0) return false;
        m.Svg = svg;
        return true;
    }

    /// <summary>The side of a marker image without a size of its own (a gradient, or an SVG file without one): half the font's ascent, in whole pixels.</summary>
    private static float DefaultMarkerImageSide(ComputedStyle s)
    {
        s.Font.LineMetrics(s.TextSize, out float ascentPt, out _);
        return MathF.Round(ascentPt / 0.75f) / 2 * 0.75f;
    }

    private void EmitMarker(float baseline, float lineTop)
    {
        var m = _marker!;
        _marker = null;
        var s = m.Style;
        if (!s.Visible) return;
        float size = s.TextSize;
        if (m.Type is ListStyleType.DisclosureOpen or ListStyleType.DisclosureClosed && m.Text == null && m.Image == null && m.Svg == null && m.Gradient == null)
        {
            // Chrome's disclosure triangles fill a square of 0.66 times the font size in whole pixels, standing on the
            // baseline in a marker box that ends 1 + 7px (the marker padding) before the content.
            int px = (int)(size / 0.75f * 0.66f + 0.001f);
            float triangle = px * 0.75f;
            float left = m.Inside ? (m.Rtl ? m.ContentRight + InsideMarkerWidth(m) - triangle : m.ContentX - InsideMarkerWidth(m))
                : m.Rtl ? m.ContentRight + 8 * 0.75f : m.ContentX - (px + 8) * 0.75f;
            _ops.Add(new SvgOp(left, baseline - triangle, triangle, triangle, DisclosureTriangle(m.Type == ListStyleType.DisclosureOpen, m.Rtl, s.Color)));
            return;
        }
        if (m.Image != null || m.Svg != null || m.Gradient != null)
        {
            // An image marker sits on the baseline, 7px (the marker padding) before the content.
            float ix = m.Rtl ? m.ContentRight + 5.25f : m.ContentX - 5.25f - m.ImageW, iy = baseline - m.ImageH;
            if (m.Image != null) _ops.Add(new ImageOp(ix, iy, m.ImageW, m.ImageH, m.Image));
            else if (m.Svg != null) _ops.Add(new SvgOp(ix, iy, m.ImageW, m.ImageH, m.Svg));
            else
            {
                // A gradient is painted as the only background layer of a box the marker's size.
                var group = new GroupOp();
                BoxPainter.Paint(group, new ComputedStyle { BackgroundImages = [new BackgroundImage { Gradient = m.Gradient }] }, ix, iy, m.ImageW, m.ImageH);
                _ops.AddRange(group.Ops);
            }
            return;
        }
        if (m.Text != null)
        {
            var font = s.Font;
            var text = m.Text;
            float gap = 0;
            if (!m.Custom)
            {
                // A counter style's suffix usually ends with a space ("1. "): it is the gap before the content, not drawn.
                int end = text.Length;
                while (end > 0 && text[end - 1] == ' ') end--;
                gap = (text.Length - end) * font.CharWidth(' ', size);
                text = text.Substring(0, end);
                if (text.Length == 0) return;
            }
            float tw = font.Measure(text, size) + s.LetterSpacing * text.Length;
            if (m.Rtl)
            {
                // Right to left the marker follows the content's right edge, its text in right-to-left order (".1").
                _ops.Add(new TextOp(m.ContentRight + gap, baseline, lineTop, font, size, s.Color, VisualMarkerText(text), 0, 0) { Tag = m.Tag });
                return;
            }
            _ops.Add(new TextOp(m.ContentX - gap - tw, baseline, lineTop, font, size, s.Color, text, 0, 0) { Tag = m.Tag });
            return;
        }
        // Chrome's symbol markers, in whole pixels from the font's rounded ascent: a square of side (2/3 ascent + 1) / 2,
        // 1px into a marker box that ends 1 + 7px (the marker padding) before the content.
        s.Font.LineMetrics(size, out float ascentPt, out _);
        int ascent = (int)MathF.Round(ascentPt / 0.75f), offset = ascent * 2 / 3, side = (offset + 1) / 2;
        float r = side * 0.75f / 2;
        float cxm = m.Inside ? (m.Rtl ? m.ContentRight + InsideMarkerWidth(m) - r : m.ContentX - InsideMarkerWidth(m) + r)
            : m.Rtl ? m.ContentRight + (offset + 8 - 1) * 0.75f - r : m.ContentX - (offset + 8 - 1) * 0.75f + r;
        float cym = baseline - (ascent - 3 * (ascent - offset) / 2) * 0.75f + r;
        switch (m.Type)
        {
            case ListStyleType.Disc:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: true));
                break;
            case ListStyleType.Circle:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: false));
                break;
            case ListStyleType.Square:
                _ops.Add(new RectOp(cxm - r, cym - r, 2 * r, 2 * r, s.Color));
                break;
        }
    }

    /// <summary>
    /// The width an inside marker takes at the start of its line: what <see cref="EmitMarker"/> draws before the content,
    /// with the gap after it.
    /// </summary>
    private static float InsideMarkerWidth(ListMarker m)
    {
        var s = m.Style;
        float size = s.FontSize;
        // Chrome's inside disclosure triangle and symbols start the line; the triangle takes 0.78125em and a space,
        // a symbol its side, 1px and a font size.
        if (m.Type is ListStyleType.DisclosureOpen or ListStyleType.DisclosureClosed && m.Text == null && m.Image == null && m.Svg == null && m.Gradient == null)
            return size * 0.78125f + s.Font.CharWidth(' ', size);
        if (m.Image != null || m.Svg != null || m.Gradient != null) return m.ImageW + 5.25f;
        if (m.Text != null)
        {
            var text = m.Text;
            float gap = 0;
            if (!m.Custom)
            {
                int end = text.Length;
                while (end > 0 && text[end - 1] == ' ') end--;
                gap = (text.Length - end) * s.Font.CharWidth(' ', size);
                text = text.Substring(0, end);
            }
            return text.Length == 0 ? 0 : s.Font.Measure(text, size) + s.LetterSpacing * text.Length + gap;
        }
        if (m.Type is not (ListStyleType.Disc or ListStyleType.Circle or ListStyleType.Square)) return 0;
        s.Font.LineMetrics(size, out float ascentPt, out _);
        int ascent = (int)MathF.Round(ascentPt / 0.75f);
        return ((ascent * 2 / 3 + 1) / 2 + 1) * 0.75f + size;
    }

    /// <summary>
    /// The disclosure-open and disclosure-closed markers: a triangle pointing down, or towards the inline end, in the
    /// unit square, with the corners Chrome uses.
    /// </summary>
    private static SvgImage DisclosureTriangle(bool open, bool rtl, uint color)
    {
        float[] points = open ? new[] { 0f, 0.07f, 1f, 0.07f, 0.5f, 0.93f }
            : rtl ? new[] { 1f, 0f, 0.14f, 0.5f, 1f, 1f }
            : new[] { 0f, 0f, 0.86f, 0.5f, 0f, 1f };
        var svg = new SvgImage { HasViewBox = true, ViewBoxWidth = 1, ViewBoxHeight = 1 };
        svg.Shapes.Add(new SvgShape
        {
            Verbs = new[] { SvgShape.MoveTo, SvgShape.LineTo, SvgShape.LineTo, SvgShape.Close }, Points = points, Fill = color,
            MinX = 0, MinY = 0, MaxX = 1, MaxY = 1,
        });
        return svg;
    }

    // ------------------------------------------------------------------ images

    /// <summary>Computes the used size of an image in points. Returns the decoded image, or null when it could not be loaded.</summary>
    private PdfImage? ImageSize(Element e, float availW, out float w, out float h) => ImageSize(e, availW, out w, out h, out _);

    /// <summary>
    /// Computes the used size of an image in points. Returns the decoded raster image, or null with the drawing in
    /// <paramref name="svg"/> when the source is an SVG file, or null for both when it could not be loaded.
    /// </summary>
    private PdfImage? ImageSize(Element e, float availW, out float w, out float h, out SvgImage? svg, float percentW = float.NaN)
    {
        var src = _images.SourceOf(e);
        var img = _images.Load(src, e.Style.ImageOrientationNone);
        svg = img == null ? _images.LoadSvg(src) : null;
        // A srcset candidate for 2x shows its pixels at half size.
        float density = _images.DensityOf(e);
        float iw = img != null ? img.Width * 0.75f / density : 0, ih = img != null ? img.Height * 0.75f / density : 0;
        if (svg != null)
        {
            (iw, ih) = SvgNaturalSize(svg, availW); // as for an inline svg element, as in Chrome
            iw /= density;
            ih /= density;
        }
        ReplacedSize(e, iw, ih, availW, out w, out h, percentW);
        return img;
    }

    /// <summary>The used width of an img or svg element in a containing block <paramref name="availW"/> wide, in points.</summary>
    private float ReplacedWidth(Element e, float availW)
    {
        float w;
        if (e.IsPicture) ImageSize(e, availW, out w, out _);
        else SvgSize(e, availW, out w, out _);
        return w;
    }

    /// <summary>Computes the used size of an inline &lt;svg&gt; in points and returns its parsed drawing.</summary>
    private SvgImage? SvgSize(Element e, float availW, out float w, out float h, float percentW = float.NaN)
    {
        if (!_images.Svgs.TryGetValue(e, out var svg)) _images.Svgs[e] = svg = SvgImage.Build(e, _images.SvgBudget, _images.LoadAny);
        var (iw, ih) = SvgNaturalSize(svg, availW);
        ReplacedSize(e, iw, ih, availW, out w, out h, percentW);
        return svg;
    }

    /// <summary>The size of an SVG drawing before CSS sizes it, in points.</summary>
    private (float W, float H) SvgNaturalSize(SvgImage svg, float availW)
    {
        if (svg.Width is { } sw && svg.Height is { } sh) return (sw, sh);
        if (svg.HasViewBox && svg.ViewBoxWidth > 0 && svg.ViewBoxHeight > 0)
        {
            // Only a viewBox: as wide as the container (or a fixed width), with the viewBox's aspect ratio.
            float ratio = svg.ViewBoxHeight / svg.ViewBoxWidth;
            if (svg.Width is { } w1) return (w1, w1 * ratio);
            if (svg.Height is { } h1) return (h1 / ratio, h1);
            float iw = _measuring || availW <= 0 || availW > 50000 ? 225 : availW;
            return (iw, iw * ratio);
        }
        return (svg.Width ?? 225, svg.Height ?? 112.5f); // 300 x 150 px, as in browsers
    }

    /// <summary>Whether aspect-ratio sizes the border box (box-sizing: border-box without "auto") rather than the content box.</summary>
    private static bool RatioOnBorderBox(ComputedStyle s) => s.BorderBox && !s.AspectRatioAuto;

    /// <summary>
    /// The value of a height, min-height or max-height in points: a length, or a percentage of the containing block's
    /// height <paramref name="cbH"/>. Null for auto and for a percentage when that height is not definite, which then
    /// behaves as auto (none for max-height, 0 for min-height).
    /// </summary>
    private static float? HeightOf(Length l, float? cbH)
        => l.IsAuto ? null : !l.IsPercent ? l.Value : cbH is float basis ? Math.Max(0, l.Resolve(basis)) : null;

    /// <summary>The min-height in points, or null when it is auto or a percentage that does not resolve.</summary>
    private static float? MinHeightOf(ComputedStyle s, float? cbH) => HeightOf(s.MinHeight, cbH);

    /// <summary>
    /// The height property limited by min-height and max-height, in the terms of box-sizing, when it is a length or a
    /// percentage of the definite containing block height <paramref name="cbH"/>; otherwise null.
    /// </summary>
    internal static float? ClampedHeight(ComputedStyle s, float? cbH = null)
    {
        if (HeightOf(s.Height, cbH) is not float h) return null;
        if (HeightOf(s.MaxHeight, cbH) is float max) h = Math.Min(h, max);
        if (HeightOf(s.MinHeight, cbH) is float min) h = Math.Max(h, min);
        return h;
    }

    /// <summary>The largest content height max-height allows, less <paramref name="sizing"/>, or infinity.</summary>
    private static float MaxContentHeight(ComputedStyle s, float sizing, float? cbH)
        => HeightOf(s.MaxHeight, cbH) is float max ? Math.Max(0, max - sizing) : float.PositiveInfinity;

    /// <summary>The content width aspect-ratio pairs with a height given in the terms of box-sizing.</summary>
    internal static float RatioWidth(ComputedStyle s, float height, float frame, float frameV)
    {
        float boxH = height + (s.BorderBox ? 0 : frameV);
        return Math.Max(0, RatioOnBorderBox(s) ? boxH * s.AspectRatio - frame : (boxH - frameV) * s.AspectRatio);
    }

    /// <summary>
    /// The limits min-height and max-height put on the content width of a box with aspect-ratio and no fixed height,
    /// carried over through the ratio (CSS Sizing 4 §5.1, transferred min and max sizes).
    /// </summary>
    internal static (float Min, float Max) TransferredWidthLimits(ComputedStyle s, float frame, float frameV, float? cbH = null)
    {
        float min = 0, max = float.PositiveInfinity;
        if (s.AspectRatio <= 0 || HeightOf(s.Height, cbH) != null) return (min, max);
        if (MinHeightOf(s, cbH) is float mn && mn > 0) min = RatioWidth(s, mn, frame, frameV);
        if (HeightOf(s.MaxHeight, cbH) is float mx) max = RatioWidth(s, mx, frame, frameV);
        // A max-height below min-height loses, as max-width does to min-width.
        return (min, Math.Max(min, max));
    }

    /// <summary>
    /// The content height aspect-ratio gives a box with an auto height (or a percentage height that does not resolve
    /// against <paramref name="cbH"/>) and the given content width, or null.
    /// </summary>
    private static float? RatioHeight(ComputedStyle s, float contentW, float frame, float frameV, bool hasHOverride, float? cbH)
    {
        if (s.AspectRatio <= 0 || hasHOverride || HeightOf(s.Height, cbH) != null) return null;
        float h = RatioOnBorderBox(s) ? (contentW + frame) / s.AspectRatio - frameV : contentW / s.AspectRatio;
        return Math.Max(0, h);
    }

    /// <summary>
    /// Draws an image of <paramref name="density"/> image pixels per CSS pixel into its content box (x, y, w, h), sized
    /// and placed by object-fit and object-position, and clipped
    /// to the box (and its rounded corners) where it reaches outside.
    /// </summary>
    private void AddImage(ComputedStyle s, PdfImage image, float density, float x, float y, float w, float h, CornerRadii? radii, ContentSlot? tag)
    {
        if (s.ObjectFit == ObjectFit.Fill || image.Width <= 0 || image.Height <= 0 || w <= 0 || h <= 0)
        {
            _ops.Add(new ImageOp(x, y, w, h, image) { Radii = radii, Tag = tag });
            return;
        }
        float nw = image.Width * 0.75f / density, nh = image.Height * 0.75f / density;
        float contain = Math.Min(w / nw, h / nh);
        float k = s.ObjectFit switch
        {
            ObjectFit.Contain => contain,
            ObjectFit.Cover => Math.Max(w / nw, h / nh),
            ObjectFit.ScaleDown => Math.Min(1, contain),
            _ => 1, // none
        };
        float dw = nw * k, dh = nh * k;
        float dx = x + s.ObjectPositionX.Resolve(w - dw), dy = y + s.ObjectPositionY.Resolve(h - dh);
        var op = new ImageOp(dx, dy, dw, dh, image) { Tag = tag };
        if (radii == null && dx >= x - Eps && dy >= y - Eps && dx + dw <= x + w + Eps && dy + dh <= y + h + Eps)
        {
            _ops.Add(op);
            return;
        }
        var clip = new ClipOp(x, y, w, h, radii);
        clip.Ops.Add(op);
        _ops.Add(clip);
    }

    /// <summary>
    /// Adds an SVG file drawn by an img element into its content box (x, y, w, h), as Chrome draws it: object-fit sizes
    /// the drawing's viewport (its concrete object size) from the file's natural size and ratio, object-position places
    /// that viewport, and the file's own viewBox and preserveAspectRatio then fit the drawing inside it. The drawing is
    /// clipped to the content box (and its rounded corners) where it reaches outside.
    /// </summary>
    private static void AddSvgImage(List<DrawOp> ops, ComputedStyle s, SvgImage svg, float x, float y, float w, float h, CornerRadii? radii,
        ContentSlot? tag)
    {
        var (cw, ch) = ConcreteObjectSize(s.ObjectFit, svg, w, h);
        float dx = x + s.ObjectPositionX.Resolve(w - cw), dy = y + s.ObjectPositionY.Resolve(h - ch);
        var op = new SvgOp(dx, dy, cw, ch, svg) { Tag = tag, StretchUserSpace = !svg.HasViewBox };
        if (radii == null && dx >= x - Eps && dy >= y - Eps && dx + cw <= x + w + Eps && dy + ch <= y + h + Eps)
        {
            ops.Add(op);
            return;
        }
        var clip = new ClipOp(x, y, w, h, radii);
        clip.Ops.Add(op);
        ops.Add(clip);
    }

    /// <summary>
    /// The concrete object size object-fit gives an SVG file in a w x h content box (CSS Images 3 §5.5). Its natural width
    /// and height are those of its width and height attributes, and its natural ratio theirs, else the viewBox's.
    /// </summary>
    private static (float W, float H) ConcreteObjectSize(ObjectFit fit, SvgImage svg, float w, float h)
    {
        float? nw = svg.Width is > 0 ? svg.Width : null, nh = svg.Height is > 0 ? svg.Height : null;
        float ratio = nw is float rw && nh is float rh ? rw / rh
            : svg.HasViewBox && svg.ViewBoxWidth > 0 && svg.ViewBoxHeight > 0 ? svg.ViewBoxWidth / svg.ViewBoxHeight : 0;
        (float, float) Fit(bool cover)
        {
            if (ratio <= 0 || w <= 0 || h <= 0) return (w, h);
            bool wide = w / h > ratio; // the box is wider than the drawing
            return wide != cover ? (h * ratio, h) : (w, w / ratio);
        }
        (float, float) Natural()
        {
            // The default sizing algorithm with the content box as the default object size.
            if (nw is float a && nh is float b) return (a, b);
            if (ratio > 0)
            {
                if (nw is float a1) return (a1, a1 / ratio);
                if (nh is float b1) return (b1 * ratio, b1);
                return Fit(cover: false);
            }
            return (nw ?? w, nh ?? h);
        }
        switch (fit)
        {
            case ObjectFit.Contain: return Fit(cover: false);
            case ObjectFit.Cover: return Fit(cover: true);
            case ObjectFit.None: return Natural();
            case ObjectFit.ScaleDown:
            {
                var none = Natural();
                var contain = Fit(cover: false);
                return none.Item1 <= contain.Item1 && none.Item2 <= contain.Item2 ? none : contain;
            }
            default: return (w, h);
        }
    }

    /// <summary>Used size of a replaced element from its intrinsic size and its CSS width, height and limits.</summary>
    /// <summary>
    /// How much wider than the page the replaced element <see cref="ReplacedSize"/> sized last would be, had it not been
    /// shrunk to the page: it still counts toward how far the content reaches, so that the page is shrunk to fit it
    /// instead, as in Chrome.
    /// </summary>
    private float _replacedExcess;

    private void ReplacedSize(Element e, float iw, float ih, float availW, out float w, out float h, float percentW = float.NaN)
    {
        var s = e.Style;
        float basisW = float.IsNaN(percentW) ? availW : percentW; // what percentages of the width are of
        float? cbH = HeightBasis(e);
        float? specifiedH = HeightOf(s.Height, cbH), maxH = HeightOf(s.MaxHeight, cbH);
        bool hasW = !s.Width.IsAuto && !(_measuring && s.Width.IsPercent), hasH = specifiedH != null;
        w = hasW ? s.Width.Resolve(Math.Max(basisW, 0)) : 0;
        h = specifiedH ?? 0;
        if (s.BorderBox)
        {
            if (hasW) w -= s.PaddingLeft.Resolve(basisW) + s.PaddingRight.Resolve(basisW) + s.BorderLeft + s.BorderRight;
            if (hasH) h -= s.PaddingTop.Resolve(basisW) + s.PaddingBottom.Resolve(basisW) + s.BorderTop + s.BorderBottom;
        }
        // aspect-ratio replaces the natural ratio, unless it says "auto" and the element has one.
        bool ratioOverride = s.AspectRatio > 0 && !(s.AspectRatioAuto && iw > 0 && ih > 0);
        float rw = ratioOverride ? s.AspectRatio : iw, rh = ratioOverride ? 1 : ih;
        if (hasW && !hasH) h = rw > 0 ? w * rh / rw : 0;
        else if (!hasW && hasH) w = rh > 0 ? h * rw / rh : 0;
        else if (!hasW && !hasH) { w = iw; h = ratioOverride ? iw / s.AspectRatio : ih; }

        if (!s.MaxWidth.IsAuto)
        {
            float mw = s.MaxWidth.Resolve(basisW);
            if (w > mw && w > 0) { h *= mw / w; w = mw; }
        }
        if (maxH is float mh && h > mh && h > 0)
        {
            w *= mh / h;
            h = mh;
        }
        // An image wider than its box overflows it, as in Chrome, but is never let run off the page: it shrinks the page
        // to fit instead, and only beyond that is it made smaller. Headers, footers and margin boxes have no page width
        // here and keep their images inside the box.
        float limit = _grid.SetupOf(_cbKind).ContentWidth is > 0 and var pageW ? pageW : availW;
        _replacedExcess = 0;
        if (limit > 0 && w > limit)
        {
            _replacedExcess = w - limit;
            h *= limit / w;
            w = limit;
        }
        if (_paginate && h > _grid.MinHeight) { w *= _grid.MinHeight / h; h = _grid.MinHeight; }
        if (w < 0) w = 0;
        if (h < 0) h = 0;
    }

    // ------------------------------------------------------------------ anchors, captured layout, size overrides

    private List<string>? _captureAnchors;
    private bool _capturing;
    private int _forcedBreaks; // forced page breaks seen; captured content without any can be reused when it fits on a page
    private readonly Dictionary<Element, float> _widthOverride = new();
    private readonly Dictionary<Element, float> _heightOverride = new();
    private HashSet<Element>? _indefiniteOverride; // heights imposed that do not make percentages inside resolve

    /// <summary>Headings in document order for the PDF outline, or null when no outline is wanted. Each heading's
    /// position is kept in <see cref="Anchors"/> under its key, so it follows the heading when content is moved.</summary>
    public List<OutlineHeading>? Headings;
    private Dictionary<Element, string>? _headingKeys;

    private static int HeadingLevel(string name)
        => name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6' ? name[1] - '0' : 0;

    private void RecordHeading(Element e, int level, float top, int opsStart)
    {
        _headingKeys ??= new Dictionary<Element, string>();
        if (!_headingKeys.TryGetValue(e, out var key))
        {
            var title = CollapseWhitespace(e.BookmarkLabel ?? e.InnerText());
            // A heading without text gets no bookmark, which is remembered too: a heading's text is that of everything
            // in it, and headings nested in each other were read again in full every time they were laid out.
            if (title.Length == 0)
            {
                _headingKeys[e] = "";
                return;
            }
            key = "\u0001h" + Headings!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _headingKeys[e] = key;
            Headings.Add(new OutlineHeading(title, level, key, e, e.Style.BookmarkClosed));
        }
        if (key.Length > 0) SetAnchor(key, ContentTop(top, opsStart));
    }

    /// <summary>Where a box starts for anchors: its top, or its first line of text when that moved to the next page.</summary>
    private float ContentTop(float top, int opsStart)
    {
        float y = top;
        if (_paginate)
            for (int i = opsStart; i < _ops.Count; i++)
                if (FirstContentY(_ops[i]) is { } fy) { if (PageOf(fy) > PageOf(top)) y = fy; break; }
        return y;
    }

    /// <summary>
    /// The text with each run of white space made one space and none at either end, as a regular expression
    /// replacing \s+ and a trim would give, without the cost of starting the regular expression engine on the first
    /// conversion.
    /// </summary>
    internal static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void SetAnchor(string id, float y)
    {
        Anchors[id] = y;
        // Inside captured content, the anchors move with it when it is placed (and footnote calls are placed then).
        if (_capturing) (_captureAnchors ??= new List<string>()).Add(id);
        // A footnote call in content that was laid out on its own and has now been moved into place.
        if (Footnotes != null && _paginate && _columnNotes == null && id.Length > 2 && id[0] == '\u0001' && id[1] == 'c') PlaceFootnoteCallAt(id, y);
    }

    /// <summary>
    /// The anchor ids noted in captured content, each once. Content placed twice in it (measured, then laid out) notes its
    /// anchors twice, and placing the capture then moved each copy, so an anchor moved twice as far, and the list doubled
    /// with each level of nesting: 25 levels of nested flex boxes noted one heading's anchor 30 million times.
    /// </summary>
    private static List<string>? Once(List<string>? ids)
    {
        if (ids is not { Count: > 1 }) return ids;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var once = ids.FindAll(seen.Add);
        return once.Count == ids.Count ? ids : once;
    }

    /// <summary>Content laid out on its own (unpaginated, at the origin) so that it can be measured and then moved into place.</summary>
    private sealed class Captured
    {
        private List<DrawOp>? _ops;

        /// <summary>The content, laid out again on first use for a box that was only measured (see <see cref="Known"/>).</summary>
        public List<DrawOp> Ops
        {
            get
            {
                if (Lay != null)
                {
                    var fresh = Lay();
                    Lay = null;
                    (_ops, AnchorIds, Absolutes) = (fresh.Ops, fresh.AnchorIds, fresh.Absolutes);
                }
                return _ops!;
            }
            set => _ops = value;
        }

        /// <summary>Lays out the content of a box whose measurements were known already, when it is needed after all.</summary>
        public Func<Captured>? Lay;

        /// <summary>
        /// Where the box was last placed. Placing moves the drawing operations themselves, so placing the box again (a
        /// flex line placed a second time) moves them by the difference only.
        /// </summary>
        public float PlacedX, PlacedY;

        /// <summary>
        /// The boxes captured while this one was laid out, outside further captures, with what they measured: when the
        /// box is laid out again on the pages, those boxes measure the same and need not be laid out to be measured.
        /// </summary>
        public Dictionary<BoxSource, Captured>? Known;

        /// <summary>The list item numbering before and after the box, which the box may have advanced.</summary>
        public int ListIndexBefore, ListIndexAfter;

        /// <summary>The containing block height the box's percentage heights resolved against, and the kind of page.</summary>
        public CbHeights? Cb;
        public int CbKind;

        /// <summary>Whether the flex lines in the box could still be laid out again when it was captured.</summary>
        public bool RepeatsOnPages;

        /// <summary>Whether its layout asked that (see <see cref="MayRepeatOnPages"/>), so that it may lay out otherwise without.</summary>
        public bool DependsOnRepeats;

        /// <summary>
        /// The breaks of the column layout pass the box was captured in, which hold the breaks it noted, and how many
        /// forced breaks they held then.
        /// </summary>
        public ColumnNotes? NotedIn;
        public int NotedForced;

        /// <summary>
        /// Whether it may measure otherwise where it is laid out less or more deeply nested (see <see cref="SamePass"/>).
        /// </summary>
        public bool DependsOnDepth;
        public List<string>? AnchorIds;
        public List<float>? AnchorYs; // where the anchors were in the box when it was captured
        public float Width, Height; // margin box
        public float Baseline = float.NaN;
        public bool ForcedBreaks;
        public BoxSource? Source; // how CaptureBox laid the box out, so that it can be laid out again on the pages
        public float BoxX, BoxY, BoxW, BoxH; // border box of the outermost box laid out
        public float ContentH, FrameV;        // content height and vertical padding and borders of that box
        public CbHeights? ChildCb;            // the containing block height that box gave its children
        public List<(Element E, float X, float Y)>? Absolutes; // absolutely positioned boxes still waiting for their containing block
    }

    /// <summary>
    /// Lays content out on its own. With <paramref name="logItems"/> the result keeps what the flex and grid items in it
    /// measured (see <see cref="Captured.Known"/>); <paramref name="known"/> are those of an earlier layout of the same box.
    /// </summary>
    private Captured Capture(Func<float> layout, bool logItems = false, Dictionary<BoxSource, Captured>? known = null)
    {
        var savedLog = _captureLog;
        bool savedLogging = _logItems;
        var savedKnown = _knownBoxes;
        _captureLog = null;
        _logItems = logItems;
        _knownBoxes = known;
        var savedOps = _ops;
        bool savedPaginate = _paginate;
        var savedMarker = _marker;
        float savedBaseline = _lastBaseline;
        var savedAnchors = _captureAnchors;
        bool savedCapturing = _capturing;
        float savedRight = _lastBoxRight;
        var savedFloats = EnterFormattingContext();
        var boundary = _boundaryPool.TryPop(out var pooled) ? pooled : new CbFrame { Boundary = true };
        _frames.Add(boundary);
        _lastBoxX = _lastBoxTop = _lastBoxW = _lastBoxH = 0;
        _lastBoxContentH = _lastBoxFrameV = 0;
        _lastBoxChildCb = null;
        _ops = new List<DrawOp>();
        _paginate = false;
        _marker = null;
        _lastBaseline = float.NaN;
        _captureAnchors = null;
        _capturing = true;
        _lastBoxRight = 0;
        int breaks = _forcedBreaks;
        long depthDependent = _depthDependentLayouts, repeatDependent = _repeatDependentLayouts;
        _captureDepth++;
        try
        {
            float bottom = layout();
            if (_floats.Any && _floats.Bottom > bottom) bottom = _floats.Bottom;
            var absolutes = boundary.Pending.Count > 0 ? boundary.TakePending() : null;
            return new Captured
            {
                Ops = _ops, AnchorIds = Once(_captureAnchors), Height = bottom, Width = _lastBoxRight, Baseline = _lastBaseline,
                ForcedBreaks = _forcedBreaks != breaks,
                BoxX = _lastBoxX, BoxY = _lastBoxTop, BoxW = _lastBoxW, BoxH = _lastBoxH,
                ContentH = _lastBoxContentH, FrameV = _lastBoxFrameV, ChildCb = _lastBoxChildCb,
                Absolutes = absolutes, Known = _captureLog, NotedIn = _columnNotes is { Paused: false } notes ? notes : null,
                NotedForced = _columnNotes?.ForcedCount ?? 0, DependsOnDepth = _depthDependentLayouts != depthDependent,
                DependsOnRepeats = _repeatDependentLayouts != repeatDependent,
            };
        }
        finally
        {
            _captureDepth--;
            // The boundary is the last frame, so it is looked for from the end: from the start, each of hundreds of
            // captures nested in each other went through all the frames around it.
            int frame = _frames.LastIndexOf(boundary);
            if (frame >= 0) _frames.RemoveAt(frame);
            boundary.TakePending();
            _boundaryPool.Push(boundary);
            LeaveFormattingContext(savedFloats, 0);
            _ops = savedOps;
            _paginate = savedPaginate;
            _marker = savedMarker;
            _lastBaseline = savedBaseline;
            _captureAnchors = savedAnchors;
            _capturing = savedCapturing;
            _lastBoxRight = savedRight;
            _captureLog = savedLog;
            _logItems = savedLogging;
            _knownBoxes = savedKnown;
        }
    }

    /// <summary>
    /// Gives the background layers of a box with background-clip: text (in <paramref name="background"/>) copies of the
    /// text the box draws from op <paramref name="from"/> on, its descendants' included, which then clip the background.
    /// </summary>
    private void ClipBackgroundToText(GroupOp background, int from) => ClipBackgroundToText(background, _ops, from, _ops.Count, float.NegativeInfinity, float.PositiveInfinity);

    /// <summary>
    /// Gives the text-clipped background layers in <paramref name="background"/> copies of the text drawn by ops
    /// <paramref name="from"/> to <paramref name="to"/> of <paramref name="ops"/> that starts between
    /// <paramref name="left"/> and <paramref name="right"/>: the text of one line fragment of an inline box.
    /// </summary>
    private static void ClipBackgroundToText(GroupOp background, List<DrawOp> ops, int from, int to, float left, float right)
    {
        List<DrawOp>? texts = null;
        void Collect(DrawOp op)
        {
            if (op is TextOp t)
            {
                if (!(t.X >= left && t.X < right)) return;
                var copy = (TextOp)t.Copy();
                copy.Tag = null; // the copies only clip: the text itself is drawn and tagged on its own
                copy.Color = CssColor.Black; // opaque, also when the text itself is transparent, as it usually is here
                (texts ??= new List<DrawOp>()).Add(copy);
            }
            else if (op is GroupOp g and not LayerOp { TextClip: true }) // a descendant's own clipping copies
                foreach (var o in g.Ops) Collect(o);
        }
        for (int i = from; i < to; i++) Collect(ops[i]);
        void Fill(GroupOp g)
        {
            foreach (var op in g.Ops)
            {
                if (op is LayerOp { TextClip: true } layer)
                {
                    // Without text there is nothing to paint the background into.
                    if (texts == null) layer.Ops.Clear();
                    else layer.Ops.InsertRange(0, texts.Select(t => t.Copy()));
                }
                else if (op is GroupOp inner) Fill(inner);
            }
        }
        Fill(background);
    }

    /// <summary>The arguments a box was captured with.</summary>
    private sealed record BoxSource(Element El, float OuterW, bool ShrinkToFit, float? BorderW, float? BorderH);

    /// <summary>Lays out a block-level box in a box of <paramref name="outerW"/> (its margin box), optionally with a forced border-box size.</summary>
    private Captured CaptureBox(Element e, float outerW, bool shrinkToFit, float? borderW = null, float? borderH = null, bool reuse = false,
        Dictionary<BoxSource, Captured>? inner = null)
    {
        var src = new BoxSource(e, outerW, shrinkToFit, borderW, borderH);
        if (!reuse) return CaptureBoxCore(src);
        Captured c;
        // In columns a box laid out again notes its breaks for balancing again, so only a box measured in the same pass
        // is reused there (see SamePass), and below MaxPlacedOnPagesInColumns any other; without the first, each nested
        // flex level laid out everything inside it again, in every balancing pass.
        if (_knownBoxes != null && FindKnown(_knownBoxes, src) is { } known && known.ListIndexBefore == _currentListIndex
            && !_measuring && Tags == null && (Footnotes == null || !HasFootnoteCalls(known))
            && (_columnNotes == null || SamePass(known) || _captureDepth >= MaxPlacedOnPagesInColumns)
            && SameContext(e, known))
        {
            // Measurements from another pass are taken only that deep, so the boxes around them may measure otherwise
            // where they are laid out less deeply nested.
            if (known.DependsOnDepth || _columnNotes != null && !SamePass(known)) _depthDependentLayouts++;
            if (known.DependsOnRepeats) _repeatDependentLayouts++;
            // Measured already where the box around it was captured: only its content may still be needed.
            c = Measured(known);
            // Its content is laid out as it was measured, so the items inside it take what they measured then too.
            // Without them, a stretched item laid out again inside each level of nested flex boxes laid out everything
            // inside it again when it was placed, which doubled the work per level.
            c.Lay = () =>
            {
                int list = _currentListIndex, breaks = _forcedBreaks;
                _currentListIndex = known.ListIndexBefore;
                try
                {
                    return CaptureBoxCore(src, known.Known);
                }
                finally
                {
                    _currentListIndex = list;
                    _forcedBreaks = breaks;
                }
            };
            _currentListIndex = known.ListIndexAfter;
            if (known.ForcedBreaks) _forcedBreaks++;
            // Laying it out would have left its anchors where they were in it, until it is placed.
            if (known.AnchorIds != null)
                for (int i = 0; i < known.AnchorIds.Count; i++) Anchors[known.AnchorIds[i]] = known.AnchorYs![i];
        }
        else c = CaptureBoxCore(src, inner);
        if (_logItems && !_measuring) (_captureLog ??= new Dictionary<BoxSource, Captured>()).TryAdd(src, Measured(c));
        return c;
    }

    /// <summary>
    /// Whether a box captured in columns was measured in the column pass in progress, as it would be measured now. It has
    /// then noted its breaks for balancing already, and noting the same breaks again changes nothing, unless a forced
    /// break noted since would cut its margins otherwise (see TruncatedMargin). It must also measure the same at any
    /// depth: boxes that took measurements from another pass, and columns inside it that laid a box out again where it
    /// landed, which they do only to a depth (see MaxPlacedOnPagesInColumns), may not. Columns that did neither measure
    /// the same at any depth: counting every multi-column box made a box with two columns of plain paragraphs at the
    /// bottom of 250 nested flex rows lay out everything below each of the eight levels laid out again above it.
    /// </summary>
    private bool SamePass(Captured known)
        => known.NotedIn == _columnNotes && known.NotedForced == _columnNotes!.ForcedCount && !known.DependsOnDepth;

    /// <summary>
    /// How many layouts so far may come out otherwise when nested less or more deeply: boxes laid out again where they
    /// landed in columns, columns whose balancing ran out of its budget, and boxes that took measurements from another
    /// column pass or hold boxes that did.
    /// </summary>
    private long _depthDependentLayouts;

    /// <summary>
    /// The box measured for <paramref name="src"/>, or for a width that differs only by rounding: grid areas are measured
    /// as the difference of track positions, which come out a few units in the last place apart when the grid is laid
    /// out again at another position. Without the match, every nested grid on the pages was measured again in full.
    /// </summary>
    private Captured? FindKnown(Dictionary<BoxSource, Captured> known, BoxSource src)
    {
        if (known.TryGetValue(src, out var exact)) return exact;
        if (known.Count <= KnownScanLimit) return NearMatch(known, src);
        // A large set is searched by element, in the same order, so deeply nested items do not scan it once per item.
        if (!_knownByElement.TryGetValue(known, out var byElement) || byElement.Count != known.Count)
            _knownByElement[known] = byElement = new KnownByElement(known);
        return byElement.Lists.TryGetValue(src.El, out var sameElement) ? NearMatch(sameElement, src) : null;

        static Captured? NearMatch(IEnumerable<KeyValuePair<BoxSource, Captured>> candidates, BoxSource src)
        {
            foreach (var (k, c) in candidates)
                if (k.El == src.El && k.ShrinkToFit == src.ShrinkToFit && k.BorderW == src.BorderW && k.BorderH == src.BorderH
                    && Math.Abs(k.OuterW - src.OuterW) <= 1e-5f * Math.Max(1, Math.Abs(src.OuterW)))
                    return c;
            return null;
        }
    }

    private const int KnownScanLimit = 16;
    private readonly Dictionary<Dictionary<BoxSource, Captured>, KnownByElement> _knownByElement = new(ReferenceEqualityComparer.Instance);

    /// <summary>The boxes of a set of known boxes grouped by element, each group in the set's order.</summary>
    private sealed class KnownByElement
    {
        public readonly Dictionary<Element, List<KeyValuePair<BoxSource, Captured>>> Lists = new();
        public readonly int Count;

        public KnownByElement(Dictionary<BoxSource, Captured> known)
        {
            Count = known.Count;
            foreach (var entry in known)
            {
                if (!Lists.TryGetValue(entry.Key.El, out var list)) Lists[entry.Key.El] = list = new List<KeyValuePair<BoxSource, Captured>>();
                list.Add(entry);
            }
        }
    }

    /// <summary>
    /// The flex and grid items captured so far in the box that <see cref="CaptureBox"/> is laying out, outside further
    /// captures, when <see cref="_logItems"/> is set.
    /// </summary>
    private Dictionary<BoxSource, Captured>? _captureLog;
    private bool _logItems;

    /// <summary>
    /// While a captured box is laid out again on the pages, the flex and grid items captured when it was captured, which
    /// measure the same now (see <see cref="Captured.Known"/>).
    /// </summary>
    private Dictionary<BoxSource, Captured>? _knownBoxes;

    /// <summary>
    /// Whether a captured box holds footnote calls. Those are placed, and their footnotes registered, as the box is laid
    /// out on the pages, so a box with calls is laid out again instead of taking its measurements; a box without them
    /// measures the same in a document with footnotes as in one without.
    /// </summary>
    private static bool HasFootnoteCalls(Captured c) => c.AnchorIds?.Exists(id => FootnoteSetup.TryParseCallKey(id, out _)) == true;

    /// <summary>What a captured box measured, without its content.</summary>
    private static Captured Measured(Captured c) => new()
    {
        Ops = [], Width = c.Width, Height = c.Height, Baseline = c.Baseline, ForcedBreaks = c.ForcedBreaks, Source = c.Source,
        BoxX = c.BoxX, BoxY = c.BoxY, BoxW = c.BoxW, BoxH = c.BoxH,
        ContentH = c.ContentH, FrameV = c.FrameV, ChildCb = c.ChildCb, Known = c.Known,
        ListIndexBefore = c.ListIndexBefore, ListIndexAfter = c.ListIndexAfter, Cb = c.Cb, CbKind = c.CbKind, RepeatsOnPages = c.RepeatsOnPages, DependsOnRepeats = c.DependsOnRepeats,
        AnchorIds = c.AnchorIds, AnchorYs = c.AnchorYs, NotedIn = c.NotedIn, NotedForced = c.NotedForced, DependsOnDepth = c.DependsOnDepth,
    };

    /// <summary>
    /// Whether <paramref name="e"/> lays out now as it did when <paramref name="known"/> was captured: flex lines may
    /// grow as deep as they could then, if its layout asked, and the box's own percentage heights resolve the same. Only
    /// the box's own can differ: its children's containing block is its own height, auto here.
    /// </summary>
    private bool SameContext(Element e, Captured known)
    {
        if (known.DependsOnRepeats && known.RepeatsOnPages != _placingOnPages < MaxRepeatedOnPages) return false;
        var s = e.Style;
        if (!s.Height.IsPercent && !s.MinHeight.IsPercent && !s.MaxHeight.IsPercent) return true;
        return known.CbKind == _cbKind && (known.Cb, _cbHeight) switch
        {
            (null, null) => true,
            ({ } a, { } b) => a.First == b.First && a.Odd == b.Odd && a.Even == b.Even,
            _ => false,
        };
    }

    private Captured CaptureBoxCore(BoxSource src, Dictionary<BoxSource, Captured>? inner = null)
    {
        var (e, outerW, shrinkToFit, borderW, borderH) = src;
        var s = e.Style;
        if (borderW.HasValue) _widthOverride[e] = borderW.Value;
        if (borderH.HasValue) _heightOverride[e] = borderH.Value;
        int listBefore = _currentListIndex;
        try
        {
            float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(outerW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(outerW);
            float mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(outerW);
            var c = Capture(() => LayoutBlockBox(e, 0, mt, outerW, shrinkToFit) + mb, logItems: true, inner);
            c.Width += mr;
            c.Source = src;
            c.ListIndexBefore = listBefore;
            c.ListIndexAfter = _currentListIndex;
            if (c.AnchorIds != null) c.AnchorYs = c.AnchorIds.ConvertAll(id => Anchors[id]);
            (c.Cb, c.CbKind, c.RepeatsOnPages) = (_cbHeight, _cbKind, _placingOnPages < MaxRepeatedOnPages);
            return c;
        }
        finally
        {
            if (borderW.HasValue) _widthOverride.Remove(e);
            if (borderH.HasValue) _heightOverride.Remove(e);
        }
    }

    /// <summary>Lays out a run of inline content (an anonymous block) at width <paramref name="w"/>.</summary>
    private Captured CaptureRun(List<Node> run, Element container, float w)
    {
        var c = Capture(() => FlushInline(run, container, 0, 0, w, true, out _));
        c.Width = w;
        return c;
    }

    /// <summary>
    /// Whether content captured without pagination can be used as is at <paramref name="top"/>: pagination only changes
    /// a layout when something crosses a page boundary or forces a break.
    /// </summary>
    private bool Reusable(Captured c, float top) => !_paginate || !c.ForcedBreaks && !CrossesPage(top, top + c.Height);

    /// <summary>
    /// Places a box captured with <see cref="CaptureBox"/> at (<paramref name="dx"/>, <paramref name="dy"/>) and returns
    /// its bottom margin edge. Captured content is not paginated, so a box that lands across a page boundary is laid out
    /// again where it lands: its lines and table rows then move to the next page, where a table repeats its header,
    /// instead of being cut in half, and the box may get taller.
    /// </summary>
    private float PlaceOnPages(Captured c, float dx, float dy)
    {
        // Each nested box laid out again lays out its own boxes again, so only the outer two levels may do that twice,
        // to give a stretched box its height back; deeper ones keep the height of their content.
        if (c.Source is not { } src || Reusable(c, dy))
        {
            Place(c, dx, dy);
            return dy + c.Height;
        }
        // In columns only the boxes measured in the same balancing pass can be reused inside (their breaks are noted for
        // balancing), so a level laid out again may lay out everything below it again. Deeper levels keep their measured
        // layout, so from here on the layout around this box depends on how deeply it is nested.
        if (_columnNotes != null)
        {
            _depthDependentLayouts++;
            if (_placingOnPages >= MaxPlacedOnPagesInColumns)
            {
                Place(c, dx, dy);
                return dy + c.Height;
            }
        }
        var e = src.El;
        var s = e.Style;
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(src.OuterW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(src.OuterW);
        if (src.BorderW is float bw) _widthOverride[e] = bw;
        int start = _ops.Count;
        var marker = _marker;
        int listIndex = _currentListIndex;
        _placingOnPages++;
        // The boxes inside measure as they did when this one was captured, unless it was given a height then.
        var pool = _knownBoxes;
        _knownBoxes = src.BorderH == null ? c.Known : null;
        try
        {
            float bottom = LayoutBlockBox(e, dx, dy + mt, src.OuterW, src.ShrinkToFit) + mb;
            if (src.BorderH is float bh && bottom - dy < c.Height - Eps && Repeats(_placingOnPages <= MaxRepeatedOnPages))
            {
                // A stretched box keeps the height it was given when its content still fits in it on the pages.
                Truncate(start);
                _marker = marker;
                _currentListIndex = listIndex;
                _heightOverride[e] = bh;
                _knownBoxes = null;
                try
                {
                    bottom = LayoutBlockBox(e, dx, dy + mt, src.OuterW, src.ShrinkToFit) + mb;
                }
                finally
                {
                    _heightOverride.Remove(e);
                }
            }
            return bottom;
        }
        finally
        {
            _knownBoxes = pool;
            _placingOnPages--;
            if (src.BorderW.HasValue) _widthOverride.Remove(e);
        }
    }

    /// <summary>How many boxes are being laid out again by <see cref="PlaceOnPages"/>, one inside the other.</summary>
    private int _placingOnPages;

    /// <summary>The depth up to which a box laid out again on the pages may be laid out a second time.</summary>
    private const int MaxRepeatedOnPages = 2;

    /// <summary>
    /// Whether a box laid out again on the pages may still be laid out a second time there, noting that the layouts in
    /// progress asked. A box captured where it could is reused where it cannot, and the other way round, only when its
    /// layout never asked (see <see cref="SameContext"/>). Few captures ask, since without pages a flex line grows only
    /// past a definite height; before, every box captured two levels deep on the pages was laid out again with
    /// everything inside it, at every level of nested flex rows.
    /// </summary>
    private bool MayRepeatOnPages() => Repeats(_placingOnPages < MaxRepeatedOnPages);

    private bool Repeats(bool may)
    {
        _repeatDependentLayouts++;
        return may;
    }

    /// <summary>How many layouts so far asked whether boxes may be laid out a second time on the pages.</summary>
    private long _repeatDependentLayouts;

    /// <summary>
    /// How many boxes, one inside the other, are laid out again where they land in columns. Boxes captured deeper than
    /// that in columns also reuse what they measured in another balancing pass, as they do on pages: without it, each
    /// level laid out everything below it again, so the work grew with the square of the depth.
    /// </summary>
    private const int MaxPlacedOnPagesInColumns = 8;

    /// <summary>How many layouts of content on its own (see <see cref="Capture"/>) are in progress, one inside the other.</summary>
    private int _captureDepth;

    /// <summary>
    /// The depth of captures from which placed groups move their ops only when those are next looked at (see
    /// <see cref="GroupOp.OffsetLater"/>). Adding the offsets up first can round a coordinate in its last bit, so
    /// shallower content, which is all a real document has, is moved as it always was.
    /// </summary>
    private const int LazyOffsetDepth = 32;

    /// <summary>Moves captured content to its final position and appends it to the output.</summary>
    private void Place(Captured c, float dx, float dy)
    {
        float moveX = dx - c.PlacedX, moveY = dy - c.PlacedY;
        (c.PlacedX, c.PlacedY) = (dx, dy);
        // Deep down, the content goes into one group the first time it is placed: each level around it then adds that
        // one op to its own content instead of all of them, which grew with the depth times the content.
        if (_captureDepth >= LazyOffsetDepth && c.Ops.Count > 1)
        {
            var group = new GroupOp();
            group.Ops.AddRange(c.Ops);
            c.Ops = [group];
        }
        foreach (var op in c.Ops)
        {
            if (op is GroupOp g && _captureDepth >= LazyOffsetDepth) g.OffsetLater(moveX, moveY);
            else op.Offset(moveX, moveY);
            _ops.Add(op);
        }
        // Its content ends with it, as far as the bottom edges of the boxes around it are concerned.
        if (_paginate) _lastContentBottom = dy + c.Height;
        if (c.AnchorIds != null)
            foreach (var id in c.AnchorIds)
                if (Anchors.TryGetValue(id, out float ay)) SetAnchor(id, ay + moveY);
        if (c.Absolutes != null)
            foreach (var (e, ax, ay) in c.Absolutes) QueueAbsolute(e, ax + dx, ay + dy);
    }
}

/// <summary>
/// A heading, or an element with a CSS bookmark-level, that becomes a PDF bookmark: its text, level (1 and up), the key
/// of its position in the anchors, its element (for the structure destination of a PDF/UA-2 bookmark) and whether it
/// starts closed (bookmark-state: closed).
/// </summary>
internal readonly record struct OutlineHeading(string Title, int Level, string Key, Element? Element = null, bool Closed = false);

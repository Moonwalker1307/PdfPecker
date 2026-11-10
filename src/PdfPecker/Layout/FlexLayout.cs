using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>Flexbox layout (CSS Flexible Box Layout Module Level 1, without baseline-in-column and visual reordering subtleties).</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>A flex or grid item: a child element, or a run of text that forms an anonymous item.</summary>
    private sealed class BoxItem
    {
        public Element? El;
        public List<Node>? Run;
        public ComputedStyle Style = null!;
        public float Ml, Mr, Mt, Mb;
        public bool AutoMl, AutoMr, AutoMt, AutoMb;
        public float Base, Hypo, MinMain, MaxMain, Main;
        public float Frame;   // main-axis padding and borders, which shrinking cannot take away
        public bool Frozen;
        public Captured? Box;
        // Grid placement (0-based).
        public int Row, Col, RowSpan = 1, ColSpan = 1;
        public bool RowFixed, ColFixed;

        public float OuterMain => Main + Ml + Mr;
        public bool IsReplaced => El != null && El.IsReplaced;
    }

    private readonly Dictionary<ComputedStyle, ComputedStyle> _anonStyles = new();

    private ComputedStyle AnonymousStyle(ComputedStyle parent)
    {
        if (!_anonStyles.TryGetValue(parent, out var s))
        {
            s = parent.CreateChild();
            s.Display = Display.Block;
            // In a legacy flexible box an anonymous item has the default -webkit-box-flex and ordinal group: it neither
            // grows nor shrinks, and keeps its place among the items of group 1.
            if (parent.LegacyBox) (s.FlexShrink, s.Order) = (0, 1);
            _anonStyles[parent] = s;
        }
        return s;
    }

    /// <summary>The in-flow children of a flex or grid container, in <c>order</c>.</summary>
    private List<BoxItem> CollectBoxItems(Element container, float basisW)
    {
        var items = new List<BoxItem>();
        List<Node>? run = null;
        void FlushRun()
        {
            if (run == null || run.Count == 0) return;
            bool content = false;
            // A line break alone still makes an item one line tall, as in Chrome.
            foreach (var n in run)
                if (n is TextNode t && !string.IsNullOrWhiteSpace(t.Text) || n is Element) { content = true; break; }
            if (content) items.Add(new BoxItem { Run = new List<Node>(run), Style = AnonymousStyle(container.Style) });
            run.Clear();
        }
        foreach (var c in container.Children)
        {
            if (c is TextNode) { (run ??= new List<Node>()).Add(c); continue; }
            var e = (Element)c;
            if (e.Style == null || e.Style.Display == Display.None || e.Detached) continue;
            // A line break between text stays in the text's anonymous item, as in Chrome: "a<br>b" is one item of two
            // lines, not three items.
            if (e.Name == "br" && !e.Style.OutOfFlow && !e.Style.Floated)
            {
                (run ??= new List<Node>()).Add(c);
                continue;
            }
            // A legacy flexible box does not blockify its children, so inline-level ones share an anonymous item with
            // the text around them, as in Chrome.
            if (container.Style.LegacyBox && !e.Style.IsBlockLevel && !e.Style.OutOfFlow)
            {
                (run ??= new List<Node>()).Add(c);
                continue;
            }
            FlushRun();
            // Absolutely positioned children are not items (css-flexbox §4.1, css-grid §9): they take no room.
            if (e.Style.OutOfFlow) continue;
            items.Add(new BoxItem { El = e, Style = e.Style });
        }
        FlushRun();

        bool ordered = false;
        foreach (var it in items) if (it.Style.Order != 0) { ordered = true; break; }
        if (ordered)
        {
            var sorted = items.Select((it, i) => (it, i)).OrderBy(p => p.it.Style.Order).ThenBy(p => p.i).Select(p => p.it).ToList();
            items = sorted;
        }

        foreach (var it in items)
        {
            if (it.El == null) continue;
            var s = it.Style;
            it.AutoMl = s.MarginLeft.IsAuto;
            it.AutoMr = s.MarginRight.IsAuto;
            it.AutoMt = s.MarginTop.IsAuto;
            it.AutoMb = s.MarginBottom.IsAuto;
            it.Ml = it.AutoMl ? 0 : s.MarginLeft.Resolve(basisW);
            it.Mr = it.AutoMr ? 0 : s.MarginRight.Resolve(basisW);
            it.Mt = it.AutoMt ? 0 : s.MarginTop.Resolve(basisW);
            it.Mb = it.AutoMb ? 0 : s.MarginBottom.Resolve(basisW);
        }
        return items;
    }

    /// <summary>
    /// Queues the absolutely positioned children of a flex or grid container. Their static position is the start of the
    /// container's content box, where a box placed in it on its own would start.
    /// </summary>
    private void QueueAbsoluteChildren(Element container, float x, float y)
    {
        foreach (var c in container.Children)
            if (c is Element { Style: { OutOfFlow: true, Display: not Display.None } } e && !e.Detached)
                QueueAbsolute(e, x, y);
    }

    /// <summary>Border-box min-content and max-content widths of an item.</summary>
    private (float Min, float Max) ItemIntrinsic(BoxItem it)
        => it.El != null ? Intrinsic(it.El) : RunIntrinsic(it.Run!);

    private static float HorizontalFrame(ComputedStyle s, float basis)
        => s.PaddingLeft.Resolve(basis) + s.PaddingRight.Resolve(basis) + s.BorderLeft + s.BorderRight;

    private static float VerticalFrame(ComputedStyle s, float basis)
        => s.PaddingTop.Resolve(basis) + s.PaddingBottom.Resolve(basis) + s.BorderTop + s.BorderBottom;

    private static float GapOf(Length? gap, float basis) => gap is { } g ? Math.Max(0, g.Resolve(basis)) : 0;

    /// <summary>
    /// Whether a flex line goes to the next page whole when it crosses a page boundary: as in Chrome, it does when one
    /// of its items would go there whole anyway, being kept whole or replaced. Otherwise the items break where they cross.
    /// </summary>
    private static bool LineMovesWhole(List<BoxItem> line)
    {
        foreach (var it in line)
            if (it.El != null && (it.Style.AvoidBreakInside || it.El.IsReplaced)) return true;
        return false;
    }

    /// <summary>Whether the break between two flex lines is forced, by an item of either line.</summary>
    private static bool ForcedBreakBetween(List<BoxItem> above, List<BoxItem> below)
    {
        foreach (var it in above)
            if (it.El != null && it.Style.PageBreakAfter) return true;
        foreach (var it in below)
            if (it.El != null && it.Style.PageBreakBefore) return true;
        return false;
    }

    /// <summary>
    /// Whether an item has to be laid out again when a container stretches it (otherwise stretching is invisible). A
    /// stretched item's height is definite (css-flexbox §9.4 step 11), so percentage heights inside it change too. A
    /// positioned item places its absolutely positioned children against its full height, and an outline, a shadow, a
    /// transform's origin, a clip or a filter take the item's size too, so without a background or border each of them
    /// still shows the stretch.
    /// </summary>
    private static bool NeedsRelayoutWhenStretched(BoxItem it)
    {
        if (it.El == null) return false;
        if (it.IsReplaced) return true; // an image stretched by an explicit stretch alignment scales to its new height
        var s = it.Style;
        return CssColor.IsVisible(s.BackgroundColor) || s.BackgroundImages != null || s.HasBorder || s.BorderImageSource != null || s.IsFlex || s.IsGrid
               || s.Positioned || s.Transform != null || s.HasEffects || s.BoxShadows != null || s.OutlineStyle != BorderStyle.None && s.OutlineWidth > 0
               || HasPercentHeightChild(it.El) || HasRepeatedChild(it.El);
    }

    /// <summary>
    /// Whether laying an item out again with border-box height <paramref name="borderH"/> would give the layout it
    /// already has. A box cannot be shorter than its padding and borders, so a percentage height that resolves to less
    /// than those leaves the box as it was, and so does a forced height that only differs from the box's by its min- or
    /// max-height but gives its content the same height. Laying such an item out again changes nothing, but each level
    /// of flex rows nested in it did the same for the levels inside it, which doubled the work per level.
    /// </summary>
    private static bool ForcedHeightKeepsLayout(BoxItem it, float borderH)
    {
        // A forced height gives the content box, and the children's containing block, this height, with min-height
        // applied after it; anything that sizes the box another way (a table, an image, an aspect ratio) is laid out again.
        var box = it.Box!;
        if (it.IsReplaced || it.Style.Display == Display.Table || it.Style.AspectRatio > 0) return false;
        float content = Math.Max(0, borderH - box.FrameV);
        return box.ContentH == content && box.ChildCb is { Uniform: true } cb && cb.First == content;
    }

    /// <summary>
    /// An item's align-self. normal stretches a flex item, images included (css-align-3 §6.1), but places a grid item
    /// with a natural ratio, such as an image, at the start (<paramref name="grid"/>).
    /// </summary>
    private static BoxAlign SelfAlign(BoxItem it, BoxAlign containerValue, bool grid = false)
    {
        var a = it.Style.AlignSelf == BoxAlign.Auto ? containerValue : it.Style.AlignSelf;
        if (a is BoxAlign.Normal or BoxAlign.Auto) a = grid && it.IsReplaced ? BoxAlign.Start : BoxAlign.Stretch;
        return a;
    }

    /// <summary>Space before the first item and between items for justify-content / align-content.</summary>
    private static void Distribute(BoxAlign align, float free, int count, out float before, out float between)
    {
        before = 0;
        between = 0;
        switch (align)
        {
            case BoxAlign.End: before = free; break;
            case BoxAlign.Center: before = free / 2; break;
            case BoxAlign.SpaceBetween:
                if (free > 0 && count > 1) between = free / (count - 1);
                break;
            case BoxAlign.SpaceAround:
                if (free > 0 && count > 0) { between = free / count; before = between / 2; }
                else before = free / 2;
                break;
            case BoxAlign.SpaceEvenly:
                if (free > 0 && count > 0) { between = free / (count + 1); before = between; }
                else before = free / 2;
                break;
        }
    }

    private static BoxAlign Mirror(BoxAlign a) => a switch
    {
        BoxAlign.Start or BoxAlign.Normal or BoxAlign.Stretch => BoxAlign.End,
        BoxAlign.End => BoxAlign.Start,
        _ => a,
    };

    // ------------------------------------------------------------------ flex container

    private float LayoutFlex(Element e, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        var items = CollectBoxItems(e, w);
        QueueAbsoluteChildren(e, x, y);
        if (items.Count == 0) return y;
        return s.IsRow ? LayoutFlexRow(e, items, x, y, w, definiteH, minH) : LayoutFlexColumn(e, items, x, y, w, definiteH, minH);
    }

    private float LayoutFlexRow(Element e, List<BoxItem> items, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        // Right to left, a row's main start is on the right: laid out like row-reverse.
        bool reverse = (s.FlexDirection == FlexDirection.RowReverse) != s.Rtl;
        var justify = s.JustifyContent;
        if (reverse)
        {
            items.Reverse();
            justify = Mirror(justify);
        }
        float colGap = GapOf(s.ColumnGap, w), rowGap = GapOf(s.RowGap, w);

        // Flex base sizes and hypothetical main sizes (border boxes).
        foreach (var it in items)
        {
            var st = it.Style;
            var (mn, mx) = ItemIntrinsic(it);
            float frame = it.El != null ? HorizontalFrame(st, w) : 0;
            // An image whose percentage height or max-height resolves against the container is as wide as that height
            // makes it, which its intrinsic widths, measured without the container, do not know.
            if (it.IsReplaced && (st.Height.IsPercent || st.MaxHeight.IsPercent) && HeightBasis(it.El!) != null)
                mn = mx = ReplacedWidth(it.El!, w) + frame;
            float basis;
            float? specified = null;
            if (it.El != null && !it.IsReplaced && !st.Width.IsAuto)
                specified = st.Width.Resolve(w) + (st.BorderBox ? 0 : frame);
            if (it.El != null && !st.FlexBasis.IsAuto) basis = st.FlexBasis.Resolve(w) + (st.BorderBox ? 0 : frame);
            else basis = specified ?? mx;
            it.Base = basis;
            it.Frame = frame;
            // min-width: auto means "not smaller than the content" for flex items: the min-content width of what is inside
            // the item, not of the item with its own width, or a fixed width could never shrink.
            if (specified.HasValue && it.El != null && st.Display != Display.Table) mn = ContentIntrinsic(it.El).Min + frame;
            // A scroll container's automatic minimum is zero (css-flexbox §4.5): its content scrolls, or here is clipped.
            float min = !st.MinWidth.IsAuto ? st.MinWidth.Resolve(w) + (st.BorderBox ? 0 : frame)
                : it.El != null && !it.IsReplaced && st.IsScrollContainer ? frame
                : Math.Min(mn, specified ?? mn);
            float max = !st.MaxWidth.IsAuto ? st.MaxWidth.Resolve(w) + (st.BorderBox ? 0 : frame) : float.PositiveInfinity;
            it.MinMain = min;
            it.MaxMain = Math.Max(min, max);
            it.Hypo = Math.Clamp(basis, it.MinMain, it.MaxMain);
        }

        // Lines.
        var lines = new List<List<BoxItem>>();
        if (s.FlexWrap == FlexWrap.NoWrap) lines.Add(items);
        else
        {
            var cur = new List<BoxItem>();
            float used = 0;
            foreach (var it in items)
            {
                float outer = it.Hypo + it.Ml + it.Mr;
                if (cur.Count > 0 && used + colGap + outer > w + Eps)
                {
                    lines.Add(cur);
                    cur = new List<BoxItem>();
                    used = 0;
                }
                used += (cur.Count > 0 ? colGap : 0) + outer;
                cur.Add(it);
            }
            if (cur.Count > 0) lines.Add(cur);
            if (s.FlexWrap == FlexWrap.WrapReverse) lines.Reverse();
        }

        foreach (var line in lines) ResolveFlexibleLengths(line, w, colGap);

        // Lay out each item at its main size to find its cross size.
        foreach (var it in items)
        {
            if (it.El != null) it.Box = CaptureBox(it.El, it.OuterMain, shrinkToFit: false, borderW: it.Main, reuse: true);
            else it.Box = CaptureRun(it.Run!, e, it.Main);
        }

        // Cross sizes of the lines.
        var lineCross = new float[lines.Count];
        var lineBaseline = new float[lines.Count];
        for (int li = 0; li < lines.Count; li++)
        {
            float maxCross = 0, maxAbove = 0, maxBelow = 0;
            foreach (var it in lines[li])
            {
                float h = it.Box!.Height;
                // An item with continue: discard ends at the end of the page the first line starts on, so it does not
                // make the line, and the items stretched to it, run on to the next page. Its own layout cuts it there;
                // here it was laid out away from the pages.
                if (li == 0 && _paginate && _grid is not ColumnGrid && it.El?.Style.ContinueDiscard == true)
                    h = Math.Min(h, Math.Max(0, FlowEnd(PageOf(y)) - y));
                if (SelfAlign(it, s.AlignItems) == BoxAlign.Baseline)
                {
                    float b = BaselineOf(it);
                    maxAbove = Math.Max(maxAbove, b);
                    maxBelow = Math.Max(maxBelow, h - b);
                }
                maxCross = Math.Max(maxCross, h);
            }
            lineCross[li] = Math.Max(maxCross, maxAbove + maxBelow);
            lineBaseline[li] = maxAbove;
        }
        float containerCross = definiteH ?? 0;
        if (lines.Count == 1)
        {
            if (definiteH.HasValue) lineCross[0] = definiteH.Value;
            else lineCross[0] = Math.Max(lineCross[0], minH);
        }

        // align-content: distribute the cross free space between the lines.
        float linesTotal = 0;
        foreach (var lc in lineCross) linesTotal += lc;
        linesTotal += rowGap * (lines.Count - 1);
        float crossBefore = 0, crossBetween = 0;
        float target = definiteH ?? Math.Max(minH, linesTotal);
        if (lines.Count > 1 && target > linesTotal + Eps)
        {
            float free = target - linesTotal;
            var ac = s.AlignContent;
            if (ac is BoxAlign.Normal or BoxAlign.Stretch)
            {
                for (int li = 0; li < lines.Count; li++) lineCross[li] += free / lines.Count;
            }
            else Distribute(ac, free, lines.Count, out crossBefore, out crossBetween);
        }
        _ = containerCross;

        float lineTop = y + crossBefore;
        float firstBaseline = float.NaN;
        // The gap decorations paint below the items; each line notes its top, its bottom and its items' edges for them.
        var rules = ReserveGapRules(s);
        var ruleLines = rules != null ? new List<(float Top, float Bottom, float[] Edges)>(lines.Count) : null;
        // The lowest content of the items, which the container's bottom edge follows on pages: the last item laid out
        // is not always the one that ends lowest.
        float lowestContent = float.NaN;
        for (int li = 0; li < lines.Count; li++)
        {
            var line = lines[li];
            float cross = lineCross[li];

            // An item laid out again across a page boundary may make the line taller; the line is then placed once more
            // with that height, so that the items stretched to it reach the new bottom. When they all reach it already at
            // their own height, as the only item of a line does when it is the one that grew, placing them again lays
            // them out as they are: in nested flex rows each level did that for everything inside it, which doubled the
            // work per level.
            int lineOps = _ops.Count;
            var lineMarker = _marker;
            int lineListIndex = _currentListIndex;
            float lineRight = _lastBoxRight, lineFirstBaseline = firstBaseline, lineLowestContent = lowestContent;
            float lineBottom;
            // A forced break between two lines is taken when an item of the line above asks for a break after it or
            // an item of this line for one before it (css-break §3.1): the line opens the next page.
            if (_paginate && li > 0 && ForcedBreakBetween(lines[li - 1], line)) lineTop = NextPage(lineTop);
            for (bool regrown = false; ; regrown = true)
            {
                // Stretched items get the line's cross size.
                foreach (var it in line)
                {
                    if (it.El == null) continue;
                    var st = it.Style;
                    float current = it.Box!.Height - it.Mt - it.Mb;
                    float? want = null;
                    var align = SelfAlign(it, s.AlignItems);
                    if (st.Height.IsPercent && definiteH.HasValue)
                        want = st.Height.Resolve(definiteH.Value) + (st.BorderBox ? 0 : VerticalFrame(st, w));
                    else if (align == BoxAlign.Stretch && st.Height.IsAuto && !it.AutoMt && !it.AutoMb)
                        // The line's cross size, also when the content is taller: a single line of a container with a
                        // definite height is that height, and the content overflows the stretched item (as in Chrome).
                        want = Math.Max(cross - it.Mt - it.Mb, VerticalFrame(st, w));
                    // Each level of flex rows nested in stretched items lays out the levels inside it once to measure
                    // them and once more at the stretched height, which doubles the work per level; past the relayout
                    // budget an item keeps the height it was measured at.
                    // An item measured at the line's height already is laid out again too when percentage heights
                    // inside it took its height as auto: stretched, it is definite (as when its min-height sizes the line).
                    if (want is { } h && (Math.Abs(h - current) > Eps ? regrown || NeedsRelayoutWhenStretched(it) || st.Height.IsPercent
                            : align == BoxAlign.Stretch && it.Box.Source is { BorderH: null } && HasPercentHeightChild(it.El))
                        && !ForcedHeightKeepsLayout(it, h) && MayLayOutAgain)
                        it.Box = CaptureBox(it.El, it.OuterMain, shrinkToFit: false, borderW: it.Main, borderH: h, reuse: true, inner: it.Box.Known);
                }

                if (_paginate && !regrown && LineMovesWhole(line)) lineTop = FitOnPage(lineTop, cross);

                // Main axis: auto margins first, then justify-content.
                float usedMain = colGap * (line.Count - 1);
                int autoMargins = 0;
                foreach (var it in line)
                {
                    usedMain += it.OuterMain;
                    if (it.AutoMl) autoMargins++;
                    if (it.AutoMr) autoMargins++;
                }
                float freeMain = w - usedMain;
                float before = 0, between = 0, autoShare = 0;
                if (autoMargins > 0 && freeMain > 0) autoShare = freeMain / autoMargins;
                else Distribute(justify, freeMain, line.Count, out before, out between);

                float cx = x + before;
                lineBottom = lineTop + cross;
                var edges = ruleLines != null ? new float[2 * line.Count] : null;
                int edge = 0;
                float stretchedBottom = float.PositiveInfinity; // the bottom of the shortest stretched item
                foreach (var it in line)
                {
                    if (edges != null) edges[edge++] = cx;
                    if (it.AutoMl) cx += autoShare;
                    var box = it.Box!;
                    float outerCross = box.Height;
                    float dy = 0;
                    if (it.AutoMt && it.AutoMb) dy = (cross - outerCross) / 2;
                    else if (it.AutoMt) dy = cross - outerCross;
                    else if (!it.AutoMb)
                    {
                        switch (SelfAlign(it, s.AlignItems))
                        {
                            case BoxAlign.End: dy = cross - outerCross; break;
                            case BoxAlign.Center: dy = (cross - outerCross) / 2; break;
                            case BoxAlign.Baseline: dy = lineBaseline[li] - BaselineOf(it); break;
                        }
                    }
                    // An item that lands across a page boundary is laid out again there and may get taller, which
                    // moves the next line down.
                    float bottom = PlaceOnPages(box, cx, lineTop + dy);
                    if (!float.IsNaN(_lastContentBottom) && !(_lastContentBottom <= lowestContent)) lowestContent = _lastContentBottom;
                    lineBottom = Math.Max(lineBottom, bottom);
                    // A stretched item that was given a height is laid out again with the new one, which its content may use.
                    if (it.El != null && SelfAlign(it, s.AlignItems) == BoxAlign.Stretch)
                        stretchedBottom = Math.Min(stretchedBottom, box.Source is { BorderH: null } ? bottom : float.NegativeInfinity);
                    if (float.IsNaN(firstBaseline) && !float.IsNaN(box.Baseline)) firstBaseline = lineTop + dy + box.Baseline;
                    cx += it.Main + it.Ml + it.Mr;
                    if (it.AutoMr) cx += autoShare;
                    if (edges != null) edges[edge++] = cx;
                    cx += between + colGap;
                    _lastBoxRight = Math.Max(_lastBoxRight, cx);
                }
                if (regrown || lineBottom - lineTop <= cross + Eps || stretchedBottom >= lineBottom - Eps || !MayRepeatOnPages())
                {
                    ruleLines?.Add((lineTop, lineBottom, edges!));
                    break;
                }
                Truncate(lineOps);
                _marker = lineMarker;
                _currentListIndex = lineListIndex;
                _lastBoxRight = lineRight;
                firstBaseline = lineFirstBaseline;
                lowestContent = lineLowestContent;
                cross = lineBottom - lineTop;
            }
            lineTop = lineBottom + rowGap + crossBetween;
        }
        if (rules != null) FlexRowGapRules(s, x, w, y, Math.Max(lineTop - rowGap - crossBetween, y + (definiteH ?? 0)), ruleLines!, reverse, rules);
        if (!float.IsNaN(firstBaseline)) _lastBaseline = firstBaseline;
        if (!float.IsNaN(lowestContent)) _lastContentBottom = lowestContent;
        return lineTop - rowGap - crossBetween;
    }

    private static float BaselineOf(BoxItem it)
    {
        var b = it.Box!;
        return float.IsNaN(b.Baseline) ? b.Height - it.Mb : b.Baseline;
    }

    /// <summary>Resolves flexible lengths (§9.7): grows or shrinks the items of one line to fill the main size.</summary>
    // Shrinking is weighted by the content-box flex base size, so padding and borders do not make an item shrink more.
    private static float InnerBase(BoxItem it) => Math.Max(0, it.Base - it.Frame);

    private static void ResolveFlexibleLengths(List<BoxItem> line, float avail, float gap)
    {
        float outerHypo = gap * (line.Count - 1);
        foreach (var it in line) outerHypo += it.Hypo + it.Ml + it.Mr;
        bool grow = outerHypo < avail;
        float Factor(BoxItem it) => grow ? it.Style.FlexGrow : it.Style.FlexShrink;

        foreach (var it in line)
        {
            it.Main = it.Hypo;
            it.Frozen = Factor(it) == 0 || grow && it.Base > it.Hypo || !grow && it.Base < it.Hypo;
        }

        float initialFree = float.NaN;
        var violation = new float[line.Count];
        for (int iteration = 0; iteration <= line.Count; iteration++)
        {
            float used = gap * (line.Count - 1);
            float factorSum = 0, scaledShrinkSum = 0;
            int unfrozen = 0;
            foreach (var it in line)
            {
                used += it.Ml + it.Mr + (it.Frozen ? it.Main : it.Base);
                if (it.Frozen) continue;
                unfrozen++;
                factorSum += Factor(it);
                scaledShrinkSum += it.Style.FlexShrink * InnerBase(it);
            }
            if (unfrozen == 0) break;
            float free = avail - used;
            if (float.IsNaN(initialFree)) initialFree = free;
            if (factorSum < 1 && Math.Abs(initialFree * factorSum) < Math.Abs(free)) free = initialFree * factorSum;

            float total = 0;
            for (int i = 0; i < line.Count; i++)
            {
                var it = line[i];
                violation[i] = 0;
                if (it.Frozen) continue;
                float target = grow
                    ? it.Base + (factorSum > 0 ? free * it.Style.FlexGrow / factorSum : 0)
                    : it.Base + (scaledShrinkSum > 0 ? free * it.Style.FlexShrink * InnerBase(it) / scaledShrinkSum : 0);
                float clamped = Math.Max(Math.Clamp(target, it.MinMain, it.MaxMain), it.Frame);
                violation[i] = clamped - target;
                total += violation[i];
                it.Main = clamped;
            }
            if (Math.Abs(total) < Eps)
            {
                foreach (var it in line) it.Frozen = true;
                break;
            }
            for (int i = 0; i < line.Count; i++)
            {
                var it = line[i];
                if (it.Frozen) continue;
                if (total > 0 && violation[i] > 0 || total < 0 && violation[i] < 0) it.Frozen = true;
            }
        }
    }

    private float LayoutFlexColumn(Element e, List<BoxItem> items, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        if (s.FlexWrap == FlexWrap.NoWrap || definiteH is not { } height || items.Count < 2 || _wrappedColumnDepth >= MaxWrappedColumnDepth)
            return LayoutFlexColumnLine(e, items, x, y, w, definiteH, minH);
        _wrappedColumnDepth++;
        try
        {
            return FlexColumnLines(e, items, w, height) is { } lines
                ? LayoutFlexColumnLines(e, lines, x, y, w, definiteH, minH)
                : LayoutFlexColumnLine(e, items, x, y, w, definiteH, minH);
        }
        finally
        {
            _wrappedColumnDepth--;
        }
    }

    /// <summary>
    /// How many wrapping column flex boxes inside each other break into lines. Each one lays its items out twice, once
    /// to measure them for the line breaks and once in their lines, so nested ones multiply the work; deeper ones are
    /// laid out in a single line, which keeps the work bounded.
    /// </summary>
    private const int MaxWrappedColumnDepth = 4;

    private int _wrappedColumnDepth;

    /// <summary>Lays out the lines of a wrapping column flex box side by side, each as wide as its widest item.</summary>
    private float LayoutFlexColumnLines(Element e, List<List<BoxItem>> lines, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        // Several columns side by side: each is laid out as a single-line column box as wide as its line.
        float colGap = GapOf(s.ColumnGap, w);
        var lineW = new float[lines.Count];
        float used = colGap * (lines.Count - 1);
        for (int li = 0; li < lines.Count; li++)
        {
            foreach (var it in lines[li]) lineW[li] = Math.Max(lineW[li], it.Hypo);
            used += lineW[li];
        }
        float before = 0, between = 0;
        if (w > used + Eps)
        {
            float free = w - used;
            if (s.AlignContent is BoxAlign.Normal or BoxAlign.Stretch)
                for (int li = 0; li < lines.Count; li++) lineW[li] += free / lines.Count;
            else Distribute(s.AlignContent, free, lines.Count, out before, out between);
        }
        // Lines run from the cross start, the right in a right-to-left box, and from the other side for wrap-reverse.
        bool fromRight = (s.FlexWrap == FlexWrap.WrapReverse) != s.Rtl;
        float lx = before, bottom = y;
        float firstBaseline = float.NaN;
        // The gap decorations paint below the items; each line notes where it is and its items' edges for them.
        var rules = ReserveGapRules(s);
        var ruleLines = rules != null ? new List<(float X, float W, List<float> Edges)>(lines.Count) : null;
        for (int li = 0; li < lines.Count; li++)
        {
            float left = fromRight ? x + w - lx - lineW[li] : x + lx;
            var edges = ruleLines != null ? new List<float>(2 * lines[li].Count) : null;
            ruleLines?.Add((left, lineW[li], edges!));
            bottom = Math.Max(bottom, LayoutFlexColumnLine(e, lines[li], left, y, lineW[li], definiteH, minH, edges));
            if (float.IsNaN(firstBaseline)) firstBaseline = _lastBaseline;
            lx += lineW[li] + colGap + between;
        }
        if (rules != null) FlexColumnGapRules(s, ruleLines!, s.FlexDirection == FlexDirection.ColumnReverse, y, y + definiteH!.Value, rules, (x, y, x + w, y + definiteH!.Value));
        if (!float.IsNaN(firstBaseline)) _lastBaseline = firstBaseline;
        return bottom;
    }


    /// <summary>
    /// Breaks the items of a wrapping column flex box <paramref name="height"/> tall into lines, with each item's
    /// hypothetical cross size (its fit-content border-box width plus margins) in <see cref="BoxItem.Hypo"/>. Returns
    /// null when everything fits in one line, which the single-line layout handles as before.
    /// </summary>
    private List<List<BoxItem>>? FlexColumnLines(Element e, List<BoxItem> items, float w, float height)
    {
        var s = e.Style;
        float gap = GapOf(s.RowGap, w);
        var lines = new List<List<BoxItem>>();
        var cur = new List<BoxItem>();
        float usedH = 0;
        foreach (var it in items)
        {
            var st = it.Style;
            float borderW;
            float? percentW = null;
            if (it.El != null && !st.Width.IsAuto && !it.IsReplaced && st.Width.IsPercent) borderW = (percentW = PercentBorderWidth(st, w)).Value;
            else if (it.El != null && !st.Width.IsAuto && !it.IsReplaced) borderW = st.Width.Resolve(w) + (st.BorderBox ? 0 : HorizontalFrame(st, w));
            else
            {
                var (mn, mx) = ItemIntrinsic(it);
                borderW = st.WidthKeyword == SizeKeyword.MaxContent ? mx : Math.Min(mx, Math.Max(mn, w - it.Ml - it.Mr));
            }
            it.Hypo = borderW + it.Ml + it.Mr;
            float h = Measure(it, e, it.Hypo, stretch: false, percentW).Height;
            if (it.El != null && !st.FlexBasis.IsAuto)
                h = Math.Max(h, st.FlexBasis.Resolve(height) + (st.BorderBox ? 0 : VerticalFrame(st, w)) + it.Mt + it.Mb);
            if (cur.Count > 0 && usedH + gap + h > height + Eps)
            {
                lines.Add(cur);
                cur = new List<BoxItem>();
                usedH = 0;
            }
            usedH += (cur.Count > 0 ? gap : 0) + h;
            cur.Add(it);
        }
        lines.Add(cur);
        return lines.Count > 1 ? lines : null;
    }

    /// <summary>
    /// Lays out one line of a column flex box, <paramref name="w"/> wide, from top to bottom. A line of a wrapping box
    /// notes the top and bottom of each item's margin box in <paramref name="itemEdges"/> for the box's gap decorations;
    /// a box of one line paints its row rules itself.
    /// </summary>
    private float LayoutFlexColumnLine(Element e, List<BoxItem> items, float x, float y, float w, float? definiteH, float minH,
        List<float>? itemEdges = null)
    {
        var s = e.Style;
        bool reverse = s.FlexDirection == FlexDirection.ColumnReverse;
        var justify = s.JustifyContent;
        if (reverse)
        {
            items.Reverse();
            justify = Mirror(justify);
        }
        float gap = GapOf(s.RowGap, w);

        // Cross axis (horizontal) placement of each item.
        var outerW = new float[items.Count];
        var offsetX = new float[items.Count];
        var stretch = new bool[items.Count];
        // The border widths of items whose width is a percentage of the container: laid out in a room as wide as
        // themselves, they would otherwise take the percentage of that room once more.
        var percentW = new float?[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var st = it.Style;
            var align = SelfAlign(it, s.AlignItems);
            bool autoH = it.AutoMl || it.AutoMr;
            if (align == BoxAlign.Stretch && !autoH && (it.El == null || st.Width.IsAuto && st.WidthKeyword == SizeKeyword.None))
            {
                stretch[i] = true;
                outerW[i] = w;
                if (it.IsReplaced) percentW[i] = StretchedReplacedWidth(it, w, w);
                continue;
            }
            float borderW;
            if (it.El != null && !st.Width.IsAuto && !it.IsReplaced && st.Width.IsPercent) borderW = (percentW[i] = PercentBorderWidth(st, w)).Value;
            else if (it.El != null && !st.Width.IsAuto && !it.IsReplaced) borderW = st.Width.Resolve(w) + (st.BorderBox ? 0 : HorizontalFrame(st, w));
            else if (st.WidthKeyword == SizeKeyword.MaxContent) borderW = ItemIntrinsic(it).Max;
            else borderW = Math.Min(ItemIntrinsic(it).Max, Math.Max(ItemIntrinsic(it).Min, w - it.Ml - it.Mr));
            float outer = borderW + it.Ml + it.Mr;
            outerW[i] = outer;
            float free = w - outer;
            if (it.AutoMl && it.AutoMr) offsetX[i] = free / 2;
            else if (it.AutoMl) offsetX[i] = free;
            else if (!it.AutoMr)
            {
                offsetX[i] = align switch
                {
                    BoxAlign.End => free,
                    BoxAlign.Center => free / 2,
                    _ => 0,
                };
            }
        }

        // Main axis: only measure first when there is free space to hand out.
        float? target = definiteH ?? (minH > 0 ? minH : (float?)null);
        bool anyGrow = false, anyAutoV = false, anyBasis = false;
        foreach (var it in items)
        {
            if (it.Style.FlexGrow > 0) anyGrow = true;
            if (it.AutoMt || it.AutoMb) anyAutoV = true;
            if (it.El != null && !it.Style.FlexBasis.IsAuto) anyBasis = true;
        }
        bool justifies = justify is not (BoxAlign.Normal or BoxAlign.Start or BoxAlign.Stretch);
        var forcedH = new float?[items.Count];
        var measured = new Captured?[items.Count];
        float startOffset = 0, between = 0;
        var autoBefore = new float[items.Count];
        var autoAfter = new float[items.Count];
        if (target.HasValue && (anyGrow || anyAutoV || justifies) || anyBasis)
        {
            var contentH = new float[items.Count];
            var hypo = new float[items.Count];
            float sum = gap * (items.Count - 1);
            float growSum = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var c = measured[i] = Measure(it, e, outerW[i], stretch[i], percentW[i]);
                contentH[i] = c.Height - it.Mt - it.Mb;
                float h = contentH[i];
                var st = it.Style;
                if (it.El != null && !st.FlexBasis.IsAuto && !(st.FlexBasis.IsPercent && definiteH == null))
                {
                    float frameV = VerticalFrame(st, w);
                    float basis = st.FlexBasis.Resolve(definiteH ?? 0) + (st.BorderBox ? 0 : frameV);
                    // min-height: auto keeps the content visible; a min-height of its own takes the place of that
                    // automatic minimum (css-flexbox §4.5), so the content may overflow a box flexed from a zero basis.
                    // A min-height of zero is the initial value here, so it still counts as auto.
                    float ownMin = st.MinHeight.IsAuto || st.MinHeight.IsPercent && definiteH == null ? 0 : st.MinHeight.Resolve(definiteH ?? 0);
                    float floor = ownMin > 0 ? Math.Max(frameV, ownMin + (st.BorderBox ? 0 : frameV)) : contentH[i];
                    h = Math.Max(basis, floor);
                }
                if (it.El != null && !st.MaxHeight.IsAuto && !st.MaxHeight.IsPercent)
                    h = Math.Min(h, Math.Max(contentH[i], st.MaxHeight.Value));
                hypo[i] = h;
                sum += h + it.Mt + it.Mb;
                if (it.El != null) growSum += st.FlexGrow;
            }
            float free = target.HasValue ? target.Value - sum : 0;
            if (free > Eps && growSum > 0)
            {
                float share = growSum < 1 ? free * growSum : free;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it.El == null || it.Style.FlexGrow <= 0) continue;
                    float extra = share * it.Style.FlexGrow / growSum;
                    float h = hypo[i] + extra;
                    if (!it.Style.MaxHeight.IsAuto && !it.Style.MaxHeight.IsPercent) h = Math.Min(h, Math.Max(contentH[i], it.Style.MaxHeight.Value));
                    free -= h - hypo[i];
                    hypo[i] = h;
                }
            }
            // An item whose flexed height is definite is laid out at it also when its own min-height or content
            // gave it that height already, so that the percentage heights inside resolve against it, as in Chrome.
            for (int i = 0; i < items.Count; i++)
                if (items[i].El is { } el && (Math.Abs(hypo[i] - contentH[i]) > Eps
                        || (definiteH != null || DefiniteBasis(items[i].Style)) && !items[i].IsReplaced && HasPercentHeightChild(el)))
                    forcedH[i] = hypo[i];

            if (free > Eps)
            {
                int autoCount = 0;
                foreach (var it in items) { if (it.AutoMt) autoCount++; if (it.AutoMb) autoCount++; }
                if (autoCount > 0)
                {
                    float share = free / autoCount;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].AutoMt) autoBefore[i] = share;
                        if (items[i].AutoMb) autoAfter[i] = share;
                    }
                }
                else Distribute(justify, free, items.Count, out startOffset, out between);
            }
            // Items overflow a box of a definite height on both sides when centred. A box with only a min-height grows
            // to hold them instead, so it has no negative free space to hand out, and its content stays below its top.
            else if (free < -Eps && definiteH != null && justify is BoxAlign.End or BoxAlign.Center) Distribute(justify, free, items.Count, out startOffset, out between);
        }

        float cy = y + startOffset;
        float firstBaseline = float.NaN;
        // The gap decorations paint below the items.
        var rules = itemEdges == null ? ReserveGapRules(s) : null;
        var edges = rules != null ? new List<float>(2 * items.Count) : itemEdges;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            float itemTop = cy;
            cy += autoBefore[i];
            if (measured[i] is { } m && forcedH[i] == null && !it.Style.PageBreakBefore && !it.Style.PageBreakAfter && Reusable(m, cy))
            {
                // Measured content that fits where it lands: no need to lay it out again.
                Place(m, x + offsetX[i], cy);
                if (float.IsNaN(firstBaseline) && !float.IsNaN(m.Baseline)) firstBaseline = cy + m.Baseline;
                cy += m.Height;
            }
            else if (it.El != null)
            {
                float top = cy + it.Mt;
                if (it.Style.PageBreakBefore)
                {
                    top = NextPage(top);
                    itemTop = top - it.Mt - autoBefore[i];
                }
                if (forcedH[i] is { } fh)
                {
                    _heightOverride[it.El] = fh;
                    // A flexed height is definite for the percentages inside the item when the container's height
                    // is (css-flexbox §9.8), and, as in Chrome, when the item's flex basis is: a length, or auto with a
                    // height of its own. A container with only a min-height then still gives a card body of
                    // flex: 1 1 0 a height its children can take a percentage of.
                    if (definiteH == null && !DefiniteBasis(it.Style)) (_indefiniteOverride ??= new HashSet<Element>()).Add(it.El);
                }
                float savedBaseline = _lastBaseline;
                _lastBaseline = float.NaN;
                float bottom;
                // The boxes inside measure as they did when the item was measured, as in a box laid out again by
                // PlaceOnPages, unless the item was given a height: else each nested column laid out again on the pages
                // measured everything inside it again, and the work grew with the square of the depth.
                var pool = _knownBoxes;
                if (measured[i] is { } known && forcedH[i] == null) _knownBoxes = known.Known;
                if (percentW[i] is { } pw) _widthOverride[it.El] = pw;
                try
                {
                    bottom = LayoutBlockBox(it.El, x + offsetX[i], top, outerW[i], shrinkToFit: !stretch[i]);
                }
                finally
                {
                    _knownBoxes = pool;
                    if (percentW[i].HasValue) _widthOverride.Remove(it.El);
                    if (forcedH[i].HasValue)
                    {
                        _heightOverride.Remove(it.El);
                        _indefiniteOverride?.Remove(it.El);
                    }
                }
                if (float.IsNaN(firstBaseline)) firstBaseline = _lastBaseline;
                if (float.IsNaN(_lastBaseline)) _lastBaseline = savedBaseline;
                cy = bottom + it.Mb;
                if (it.Style.PageBreakAfter) cy = NextPage(cy);
            }
            else
            {
                cy = _inline.FlushInline(it.Run!, e, x + offsetX[i], cy, outerW[i], true, out _);
                if (float.IsNaN(firstBaseline)) firstBaseline = _lastBaseline;
            }
            cy += autoAfter[i];
            if (edges != null)
            {
                edges.Add(itemTop);
                edges.Add(cy);
            }
            if (i < items.Count - 1) cy += gap + between;
        }
        if (rules != null) FlexColumnGapRules(s, [(x, w, edges!)], reverse, 0, 0, rules, (x, y, x + w, Math.Max(cy, y + (definiteH ?? 0))));
        if (!float.IsNaN(firstBaseline)) _lastBaseline = firstBaseline;
        return cy;
    }

    /// <summary>
    /// Whether a column flex item's flex basis is definite without the container's height: a length, or auto with a
    /// height that is a length. A percentage basis or height needs a container of definite height, and content sizes
    /// are never definite.
    /// </summary>
    private static bool DefiniteBasis(ComputedStyle st)
        => st.FlexBasis.IsAuto ? HeightOf(st.Height, null) != null : !st.FlexBasis.IsPercent;

    /// <summary>Measures an item laid out at the given outer width (unpaginated).</summary>
    private Captured Measure(BoxItem it, Element container, float outerW, bool stretch, float? borderW = null)
        => it.El != null ? CaptureBox(it.El, outerW, shrinkToFit: !stretch, borderW, reuse: true) : CaptureRun(it.Run!, container, outerW);

    /// <summary>
    /// The border-box width of an image or other replaced item stretched across <paramref name="outerW"/> by an explicit
    /// stretch alignment, as in Chrome: what its margins leave, within its min and max widths. Its height then follows
    /// from its ratio unless it has one of its own.
    /// </summary>
    private static float StretchedReplacedWidth(BoxItem it, float outerW, float basis)
    {
        var st = it.Style;
        float frame = HorizontalFrame(st, basis), boxFrame = st.BorderBox ? frame : 0;
        float content = outerW - it.Ml - it.Mr - frame;
        if (!st.MaxWidth.IsAuto) content = Math.Min(content, st.MaxWidth.Resolve(basis) - boxFrame);
        if (!st.MinWidth.IsAuto) content = Math.Max(content, st.MinWidth.Resolve(basis) - boxFrame);
        return Math.Max(0, content) + frame;
    }

    /// <summary>The border-box width of a column flex item whose width is a percentage of the container's width <paramref name="w"/>, within its min and max widths.</summary>
    private static float PercentBorderWidth(ComputedStyle s, float w)
    {
        float frame = HorizontalFrame(s, w);
        float contentW = s.Width.Resolve(w) - (s.BorderBox ? frame : 0);
        if (!s.MaxWidth.IsAuto) contentW = Math.Min(contentW, s.MaxWidth.Resolve(w) - (s.BorderBox ? frame : 0));
        if (!s.MinWidth.IsAuto) contentW = Math.Max(contentW, s.MinWidth.Resolve(w) - (s.BorderBox ? frame : 0));
        return Math.Max(0, contentW) + frame;
    }

    /// <summary>
    /// A row flex item's min-content and max-content contributions to its container's widths (css-flexbox §9.9.3), as
    /// in Chrome: an item that cannot grow contributes no more than its flex-basis, one that cannot shrink no less, and
    /// either stays within its min and max widths. A percentage basis, which has nothing to resolve against here, and an
    /// auto one, which is the width or the content, leave the item's own widths.
    /// </summary>
    private (float Min, float Max) RowItemContribution(BoxItem it)
    {
        var (mn, mx) = ItemIntrinsic(it);
        if (it.El == null) return (mn, mx);
        var st = it.Style;
        if (st.FlexBasis.IsAuto || st.FlexBasis.IsPercent || st.FlexGrow > 0 && st.FlexShrink > 0) return (mn, mx);
        float frame = HorizontalFrame(st, 0);
        float basis = st.FlexBasis.Value + (st.BorderBox ? 0 : frame);
        float lo = !st.MinWidth.IsAuto ? st.MinWidth.Resolve(0) + (st.BorderBox ? 0 : frame) : st.IsScrollContainer && !it.IsReplaced ? frame : mn;
        float hi = !st.MaxWidth.IsAuto && !st.MaxWidth.IsPercent ? st.MaxWidth.Value + (st.BorderBox ? 0 : frame) : float.PositiveInfinity;
        if (st.FlexGrow <= 0) (mn, mx) = (Math.Min(mn, basis), Math.Min(mx, basis));
        if (st.FlexShrink <= 0) (mn, mx) = (Math.Max(mn, basis), Math.Max(mx, basis));
        hi = Math.Max(lo, hi);
        return (Math.Clamp(mn, lo, hi), Math.Clamp(mx, lo, hi));
    }

    /// <summary>Min-content and max-content widths of a flex container's content box.</summary>
    private (float Min, float Max) FlexIntrinsic(Element e)
    {
        var s = e.Style;
        var items = CollectBoxItems(e, 0);
        float min = 0, max = 0;
        if (s.IsRow)
        {
            float gap = GapOf(s.ColumnGap, 0) * Math.Max(0, items.Count - 1);
            foreach (var it in items)
            {
                var (mn, mx) = RowItemContribution(it);
                float m = it.Ml + it.Mr;
                if (s.FlexWrap == FlexWrap.NoWrap) min += mn + m;
                else min = Math.Max(min, mn + m);
                max += mx + m;
            }
            if (s.FlexWrap == FlexWrap.NoWrap) min += gap;
            max += gap;
        }
        else
        {
            foreach (var it in items)
            {
                var (mn, mx) = ItemIntrinsic(it);
                min = Math.Max(min, mn + it.Ml + it.Mr);
                max = Math.Max(max, mx + it.Ml + it.Mr);
            }
        }
        return (min, Math.Max(min, max));
    }
}

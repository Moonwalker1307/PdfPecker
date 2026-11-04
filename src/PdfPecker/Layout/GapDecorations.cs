using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// Gap decorations (CSS Gaps 1) of grid and flex containers: a rule in the middle of each column and row gap, cut into
/// segments where items span the gap, where parts of it are hidden next to empty cells, and with rule-break:
/// intersection where gaps cross, with each end moved by its rule-inset. Chrome paints the rules just above the
/// container's background and below its items, so an item spanning a gap covers what passes behind it. On paper a gap
/// that falls on a page break disappears with its rule, and a rule across a page break is cut there and capped on each
/// page as at the container's edge. Multi-column rules stay in ColumnLayout.cs.
/// </summary>
internal sealed partial class LayoutEngine
{
    private const byte PortionOpen = 0, PortionBlocked = 1, PortionHidden = 2;

    /// <summary>
    /// The most grid cells whose gaps get decorations. Finding the items next to each part of a gap takes a flag per
    /// cell; a grid of more cells than this, which no real page has, is drawn without rules.
    /// </summary>
    private const int MaxDecoratedCells = 1 << 22;

    /// <summary>
    /// One gap of a container: where it lies across its own direction, its portions along it (the parts beside one
    /// track or line of items), and the junctions between them, where gaps of the other direction cross or meet it.
    /// Coordinates along the gap increase from its start; a gap that runs right to left keeps them mirrored about
    /// <see cref="FlipOrigin"/>.
    /// </summary>
    private sealed class DecorGap
    {
        public float Mid;           // the gap's centre: x of a column gap, y of a row gap
        public int Index;           // its place in placement order, which picks its values from the rule lists
        public bool Flip;           // the gap runs right to left
        public float FlipOrigin;    // physical = FlipOrigin - coordinate when it does
        public bool Suppressed;     // it falls on a page break and disappears
        public readonly List<float> Edges = new(2);    // the start and the end of each portion
        public readonly List<byte> States = new(1);    // open, spanned by an item, or hidden next to empty cells
        public readonly List<GapJunction> Junctions = new(0); // between portion i and i + 1
        public bool[]? Drawn;       // the portions a segment covers
        public List<int>? Runs;     // the first and the last portion of each segment

        public void Portion(float start, float end, byte state)
        {
            Edges.Add(start);
            Edges.Add(end);
            States.Add(state);
        }

        /// <summary>Mirrors a gap measured left to right so that it runs from the right, for a right-to-left container.</summary>
        public void Reverse(float origin)
        {
            Edges.Reverse();
            for (int i = 0; i < Edges.Count; i++) Edges[i] = origin - Edges[i];
            States.Reverse();
            Junctions.Reverse();
            Flip = true;
            FlipOrigin = origin;
        }
    }

    /// <summary>
    /// A junction of a gap: the portions of the crossing gaps next to it (up to two; Gap is -1 for none), and whether
    /// it is no junction at all, because items span the crossing gap on both sides or the crossing gap fell on a page
    /// break, so that the gap's rule always runs through it.
    /// </summary>
    private readonly record struct GapJunction(int Gap1, int Portion1, int Gap2, int Portion2, bool Merged);

    /// <summary>The rule values of one direction, resolved for a container.</summary>
    private sealed class RuleAxis
    {
        public GapValues<BorderStyle> Styles = null!;
        public GapValues<float> Widths = null!;
        public GapValues<uint?> Colors = null!;
        public RuleBreak Break;
        public RuleVisibility Visibility;
        public RuleInsets Insets;
        public int Count;            // the number of gaps, which lists with an auto repeat need

        public bool Paints(int gap) => Styles.For(gap, Count) != BorderStyle.None && Widths.For(gap, Count) > 0;
    }

    /// <summary>Whether a grid or flex container has a column or row rule that may paint, before any work is done for one.</summary>
    private static bool HasGapRules(ComputedStyle s)
    {
        var rules = s.GapRules;
        if (rules == null) return s.ColumnRuleStyle != BorderStyle.None && s.ColumnRuleWidth > 0;
        return rules.ColumnStyles != null || rules.RowStyles != null || s.ColumnRuleStyle != BorderStyle.None;
    }

    private static RuleAxis ColumnRuleAxis(ComputedStyle s, int count)
    {
        var rules = s.GapRules;
        return new RuleAxis
        {
            Styles = rules?.ColumnStyles ?? new GapValues<BorderStyle>(s.ColumnRuleStyle),
            Widths = rules?.ColumnWidths ?? new GapValues<float>(s.ColumnRuleWidth),
            Colors = rules?.ColumnColors ?? new GapValues<uint?>(s.ColumnRuleColor),
            Break = rules?.ColumnBreak ?? RuleBreak.Normal,
            Visibility = rules?.ColumnVisibility ?? RuleVisibility.Normal,
            Insets = rules?.ColumnInsets ?? RuleInsets.Zero,
            Count = count,
        };
    }

    private static RuleAxis RowRuleAxis(ComputedStyle s, int count)
    {
        var rules = s.GapRules;
        // The initial medium width is a length, which zoom scales as it does a border's.
        float medium = s.Zoom == 1 ? 1.5f : StyleResolver.SnapBorderWidth(1.5f * s.Zoom);
        return new RuleAxis
        {
            Styles = rules?.RowStyles ?? new GapValues<BorderStyle>(BorderStyle.None),
            Widths = rules?.RowWidths ?? new GapValues<float>(medium),
            Colors = rules?.RowColors ?? new GapValues<uint?>((uint?)null),
            Break = rules?.RowBreak ?? RuleBreak.Normal,
            Visibility = rules?.RowVisibility ?? RuleVisibility.Normal,
            Insets = rules?.RowInsets ?? RuleInsets.Zero,
            Count = count,
        };
    }

    /// <summary>
    /// Reserves the slot for a container's gap decorations before its items are placed, so that the items paint over
    /// them; null when the container has no rules.
    /// </summary>
    private GroupOp? ReserveGapRules(ComputedStyle s)
    {
        if (!HasGapRules(s)) return null;
        var slot = new GroupOp { Ink = true };
        _ops.Add(slot);
        return slot;
    }

    /// <summary>
    /// Whether a gap between content ending at <paramref name="before"/> and starting at <paramref name="after"/> falls
    /// on a page break, where it disappears (CSS Box Alignment §8.1).
    /// </summary>
    private bool GapOnBreak(float before, float after) => _paginate && PageOf(before - 0.5f) != PageOf(after);

    /// <summary>The state of a portion of a grid gap from the items beside it and the visibility of the rules.</summary>
    private static byte PortionState(bool spanned, bool before, bool after, RuleVisibility visibility) => spanned ? PortionBlocked
        : visibility switch
        {
            RuleVisibility.Around => before || after ? PortionOpen : PortionHidden,
            RuleVisibility.Between => before && after ? PortionOpen : PortionHidden,
            _ => PortionOpen,
        };

    /// <summary>
    /// Paints the rules of a container's column and row gaps into <paramref name="slot"/>. In a flex container the
    /// rules run through every crossing (rule-break: normal is none there), and every part of a gap is visible.
    /// </summary>
    private void PaintGapRules(ComputedStyle s, List<DecorGap> columns, List<DecorGap> rows, bool flex, GroupOp slot)
    {
        var col = ColumnRuleAxis(s, columns.Count);
        var row = RowRuleAxis(s, rows.Count);
        if (flex)
        {
            if (col.Break == RuleBreak.Normal) col.Break = RuleBreak.None;
            if (row.Break == RuleBreak.Normal) row.Break = RuleBreak.None;
        }
        FindRuns(columns, col);
        FindRuns(rows, row);
        var colGroup = new GroupOp();
        var rowGroup = new GroupOp();
        PaintRuns(columns, rows, col, row, vertical: true, s.Color, colGroup);
        PaintRuns(rows, columns, row, col, vertical: false, s.Color, rowGroup);
        // rule-overlap: the row rules paint over the column rules unless column-over-row asks for the opposite.
        bool columnOverRow = s.GapRules?.ColumnOverRow == true;
        if (colGroup.Ops.Count > 0 && !columnOverRow) slot.Ops.Add(colGroup);
        if (rowGroup.Ops.Count > 0) slot.Ops.Add(rowGroup);
        if (colGroup.Ops.Count > 0 && columnOverRow) slot.Ops.Add(colGroup);
    }

    /// <summary>
    /// Finds the segments of each gap (CSS Gaps §3.1.2): runs of portions that are not hidden, through the portions
    /// spanned by items as well with rule-break: none (the items paint over them), and otherwise only through open
    /// ones, broken at every junction with rule-break: intersection.
    /// </summary>
    private static void FindRuns(List<DecorGap> gaps, RuleAxis axis)
    {
        foreach (var gap in gaps)
        {
            gap.Drawn = null;
            gap.Runs = null;
            if (gap.Suppressed || gap.States.Count == 0 || !axis.Paints(gap.Index)) continue;
            int n = gap.States.Count;
            var drawn = new bool[n];
            var runs = new List<int>(2);
            bool Include(int p) => axis.Break == RuleBreak.None ? gap.States[p] != PortionHidden : gap.States[p] == PortionOpen;
            for (int i = 0; i < n; i++)
            {
                if (!Include(i)) continue;
                int first = i;
                while (i + 1 < n && Include(i + 1) && (axis.Break != RuleBreak.Intersection || gap.Junctions[i].Merged)) i++;
                for (int k = first; k <= i; k++) drawn[k] = true;
                runs.Add(first);
                runs.Add(i);
            }
            gap.Drawn = drawn;
            gap.Runs = runs;
        }
    }

    /// <summary>
    /// Paints the segments of one direction's gaps, each end moved by its inset (CSS Gaps §3.3): a junction inset where
    /// another segment meets the end, a cap inset elsewhere. Percentages are of the junction's size, which is zero at
    /// the container's edges, and overlap-join reaches into a junction to the far side of the crossing rule.
    /// </summary>
    private void PaintRuns(List<DecorGap> gaps, List<DecorGap> crossing, RuleAxis axis, RuleAxis crossAxis, bool vertical, uint currentColor, GroupOp into)
    {
        foreach (var gap in gaps)
        {
            if (gap.Runs is not { } runs) continue;
            var style = axis.Styles.For(gap.Index, axis.Count);
            float width = axis.Widths.For(gap.Index, axis.Count);
            uint color = axis.Colors.For(gap.Index, axis.Count) ?? currentColor;
            for (int r = 0; r < runs.Count; r += 2)
            {
                int first = runs[r], last = runs[r + 1];
                float start = gap.Edges[2 * first], end = gap.Edges[2 * last + 1];
                if (first > 0) start += JunctionInset(gap, first - 1, crossing, crossAxis, axis.Insets.JunctionStart, axis.Insets.CapStart, other: first - 1);
                else start += Inset(axis.Insets.CapStart, 0, 0, junction: false);
                if (last < gap.States.Count - 1) end -= JunctionInset(gap, last, crossing, crossAxis, axis.Insets.JunctionEnd, axis.Insets.CapEnd, other: last + 1);
                else end -= Inset(axis.Insets.CapEnd, 0, 0, junction: false);
                if (gap.Flip) (start, end) = (gap.FlipOrigin - end, gap.FlipOrigin - start);
                if (!(end > start)) continue;
                if (vertical) PaintVerticalRule(into, style, width, color, gap.Mid, start, end, axis.Insets);
                else BoxPainter.Side(into, style, width, color, start, gap.Mid - width / 2, end - start, width, horizontal: true);
            }
        }
    }

    /// <summary>
    /// The inset of a segment's end at junction <paramref name="j"/> of a gap: a junction end when the portion past it
    /// (<paramref name="other"/>) is drawn too, or a rule of a crossing gap meets it there; a cap end otherwise.
    /// </summary>
    private static float JunctionInset(DecorGap gap, int j, List<DecorGap> crossing, RuleAxis crossAxis, RuleInset junctionInset, RuleInset capInset, int other)
    {
        float size = gap.Edges[2 * j + 2] - gap.Edges[2 * j + 1];
        var junction = gap.Junctions[j];
        bool met = gap.Drawn![other], crossDrawn = false;
        float crossWidth = 0;
        void Cross(int g, int p)
        {
            if (g < 0 || crossing[g].Drawn is not { } d || (uint)p >= (uint)d.Length || !d[p]) return;
            crossDrawn = true;
            crossWidth = Math.Max(crossWidth, crossAxis.Widths.For(crossing[g].Index, crossAxis.Count));
        }
        Cross(junction.Gap1, junction.Portion1);
        Cross(junction.Gap2, junction.Portion2);
        return met || crossDrawn ? Inset(junctionInset, size, crossWidth, junction: true) : Inset(capInset, size, 0, junction: false);
    }

    private static float Inset(RuleInset inset, float size, float crossWidth, bool junction)
    {
        if (inset.OverlapJoin) return junction ? -(size + crossWidth) / 2 : 0;
        return inset.Length.Resolve(size);
    }

    /// <summary>
    /// Paints a column rule from <paramref name="top"/> to <paramref name="bottom"/>. Laid out on the pages, a rule
    /// that crosses a page break is cut at the end of each page's flow, and each piece is capped there as at the end of
    /// the container, with the cap insets (a percentage of no junction is zero).
    /// </summary>
    private void PaintVerticalRule(GroupOp into, BorderStyle style, float width, uint color, float mid, float top, float bottom, RuleInsets insets)
    {
        if (!CrossesPage(top, bottom))
        {
            BoxPainter.Side(into, style, width, color, mid - width / 2, top, width, bottom - top, horizontal: false);
            return;
        }
        float capStart = Inset(insets.CapStart, 0, 0, junction: false), capEnd = Inset(insets.CapEnd, 0, 0, junction: false);
        int page = PageOf(top);
        // A page holds at least a point of flow, so the pieces end within the pages the rule crosses.
        while (true)
        {
            float flowEnd = FlowEnd(page);
            float end = bottom <= flowEnd + Eps ? bottom : flowEnd - capEnd;
            if (end > top) BoxPainter.Side(into, style, width, color, mid - width / 2, top, width, end - top, horizontal: false);
            if (bottom <= flowEnd + Eps) return;
            page++;
            top = FlowStart(page) + capStart;
            if (top >= bottom) return;
            Cancellation.ThrowIfCancellationRequested();
        }
    }

    // ------------------------------------------------------------------ grid

    /// <summary>
    /// Paints a grid's gap decorations once its items are placed. A column gap runs from the top of the first row to
    /// the bottom of the last, with a portion beside each row and a junction at each row gap; a row gap likewise along
    /// the columns. Collapsed auto-fit tracks and the gutters beside them count as one gap. <paramref name="rowShift"/>
    /// is how far items that grew on the pages moved each row.
    /// </summary>
    private void GridGapRules(ref GridPass g, float[]? rowShift, GroupOp slot)
    {
        var s = g.S;
        int rowCount = g.RowCount, colCount = g.ColCount;
        if ((long)rowCount * colCount > MaxDecoratedCells) return;
        var cols = new List<int>(colCount);
        var rowList = new List<int>(rowCount);
        for (int c = 0; c < colCount; c++) if (!g.Cols[c].Collapsed) cols.Add(c);
        for (int r = 0; r < rowCount; r++) if (!g.Rows[r].Collapsed) rowList.Add(r);
        if (cols.Count < 2 && rowList.Count < 2 || cols.Count == 0 || rowList.Count == 0) return;

        // Which cells hold an item, and which items cross the line after a cell, to its right or below it.
        int cells = rowCount * colCount;
        var occupied = new bool[cells];
        var crossesRight = new bool[cells];
        var crossesDown = new bool[cells];
        long work = 0;
        foreach (var it in g.Items)
        {
            int r0 = Math.Max(0, it.Row), r1 = Math.Min(rowCount, it.Row + it.RowSpan) - 1;
            int c0 = Math.Max(0, it.Col), c1 = Math.Min(colCount, it.Col + it.ColSpan) - 1;
            if (r1 < r0 || c1 < c0) continue;
            // Items may overlap; past a few times the cells, the grid is drawn without rules rather than slowly.
            work += (long)(r1 - r0 + 1) * (c1 - c0 + 1);
            if (work > 4L * MaxDecoratedCells) return;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    int k = r * colCount + c;
                    occupied[k] = true;
                    if (c < c1) crossesRight[k] = true;
                    if (r < r1) crossesDown[k] = true;
                }
        }

        var rowY = g.RowY;
        var tracks = g.Rows;
        float Top(int r) => rowY[r] + (rowShift?[r] ?? 0);
        float Bottom(int r) => rowY[r] + (tracks[r].Collapsed ? 0 : tracks[r].Base) + (rowShift?[r + 1] ?? 0);
        // Right to left, the columns run from the right edge of the grid container (of the grid area for a subgrid).
        float origin = g.ColCtx?.Edges != null ? g.ColCtx.Edges[^1] : 2 * g.X + g.W;
        float Physical(float x) => s.Rtl ? origin - x : x;

        var colVis = s.GapRules?.ColumnVisibility ?? RuleVisibility.Normal;
        var rowVis = s.GapRules?.RowVisibility ?? RuleVisibility.Normal;
        var rowSuppressed = new bool[Math.Max(0, rowList.Count - 1)];
        var rowGaps = new List<DecorGap>(rowSuppressed.Length);
        for (int k = 0; k + 1 < rowList.Count; k++)
        {
            int ra = rowList[k], rb = rowList[k + 1];
            // The portions stay in placement order, which runs from the right in a right-to-left grid.
            var gap = new DecorGap
            {
                Mid = (Bottom(ra) + Top(rb)) / 2, Index = k, Flip = s.Rtl, FlipOrigin = origin,
                Suppressed = rowSuppressed[k] = GapOnBreak(Bottom(ra), Top(rb)),
            };
            for (int i = 0; i < cols.Count; i++)
            {
                int c = cols[i];
                if (i > 0)
                {
                    // Items above and below that both span the column gap leave no junction there.
                    int line = cols[i] - 1;
                    gap.Junctions.Add(new GapJunction(i - 1, k, i - 1, k + 1,
                        crossesRight[ra * colCount + line] && crossesRight[rb * colCount + line]));
                }
                gap.Portion(g.ColX[c], g.ColRight(c),
                    PortionState(crossesDown[(rb - 1) * colCount + c], occupied[ra * colCount + c], occupied[rb * colCount + c], rowVis));
            }
            rowGaps.Add(gap);
        }
        var colGaps = new List<DecorGap>(Math.Max(0, cols.Count - 1));
        for (int k = 0; k + 1 < cols.Count; k++)
        {
            int ca = cols[k], cb = cols[k + 1], line = cb - 1;
            var gap = new DecorGap { Mid = Physical((g.ColRight(ca) + g.ColX[cb]) / 2), Index = k };
            for (int i = 0; i < rowList.Count; i++)
            {
                int r = rowList[i];
                if (i > 0)
                {
                    // Items on both sides that span the row gap leave no junction, and neither does a row gap that
                    // fell on a page break: the rule runs on to the break, where it is cut.
                    int above = r - 1;
                    gap.Junctions.Add(new GapJunction(i - 1, k, i - 1, k + 1,
                        rowSuppressed[i - 1] || crossesDown[above * colCount + ca] && crossesDown[above * colCount + cb]));
                }
                gap.Portion(Top(r), Bottom(r),
                    PortionState(crossesRight[r * colCount + line], occupied[r * colCount + ca], occupied[r * colCount + cb], colVis));
            }
            colGaps.Add(gap);
        }
        PaintGapRules(s, colGaps, rowGaps, flex: false, slot);
    }

    // ------------------------------------------------------------------ flex

    /// <summary>
    /// Paints a row flex container's gap decorations: a column rule between the items of each line, as tall as the
    /// line, and a row rule between the lines across the container's width, with a junction wherever a column gap of
    /// the line above or below meets it. <paramref name="lines"/> holds each line from the top with the left and right
    /// edges of its items' margin boxes from the left; <paramref name="reverse"/> says the items were placed from the
    /// right. The values of a list go to the gaps in placement order across all the lines.
    /// </summary>
    private void FlexRowGapRules(ComputedStyle s, float x, float w, List<(float Top, float Bottom, float[] Edges)> lines, bool reverse, GroupOp slot)
    {
        bool wrapReverse = s.FlexWrap == FlexWrap.WrapReverse;
        int count = lines.Count;
        // The first column gap of each line in placement order, where wrap-reverse places the bottom line first.
        var firstGap = new int[count];
        int total = 0;
        for (int k = 0; k < count; k++)
        {
            int li = wrapReverse ? count - 1 - k : k;
            firstGap[li] = total;
            total += Math.Max(0, lines[li].Edges.Length / 2 - 1);
        }
        var colGaps = new List<DecorGap>(total);
        var lineGaps = new int[count]; // the index in colGaps of each line's leftmost gap
        for (int li = 0; li < count; li++)
        {
            var (top, bottom, edges) = lines[li];
            int n = edges.Length / 2;
            lineGaps[li] = colGaps.Count;
            for (int i = 0; i + 1 < n; i++)
            {
                var gap = new DecorGap { Mid = (edges[2 * i + 1] + edges[2 * i + 2]) / 2, Index = firstGap[li] + (reverse ? n - 2 - i : i) };
                gap.Portion(top, bottom, PortionOpen);
                colGaps.Add(gap);
            }
        }
        var rowGaps = new List<DecorGap>(Math.Max(0, count - 1));
        var spans = new List<(float Lo, float Hi, int Gap)>();
        for (int li = 0; li + 1 < count; li++)
        {
            var (_, bottom, above) = lines[li];
            var (top, _, below) = lines[li + 1];
            var gap = new DecorGap { Mid = (bottom + top) / 2, Index = wrapReverse ? count - 2 - li : li, Suppressed = GapOnBreak(bottom, top) };
            spans.Clear();
            for (int i = 0; i + 1 < above.Length / 2; i++) spans.Add((above[2 * i + 1], above[2 * i + 2], lineGaps[li] + i));
            for (int i = 0; i + 1 < below.Length / 2; i++) spans.Add((below[2 * i + 1], below[2 * i + 2], lineGaps[li + 1] + i));
            spans.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
            float at = x;
            for (int i = 0; i < spans.Count; i++)
            {
                // Column gaps that overlap form one junction, as wide as all of them together.
                var (lo, hi, g1) = spans[i];
                int g2 = -1;
                while (i + 1 < spans.Count && spans[i + 1].Lo <= hi + Eps)
                {
                    hi = Math.Max(hi, spans[++i].Hi);
                    if (g2 < 0) g2 = spans[i].Gap;
                }
                lo = Math.Clamp(lo, at, x + w);
                hi = Math.Clamp(hi, lo, x + w);
                gap.Portion(at, lo, PortionOpen);
                gap.Junctions.Add(new GapJunction(g1, 0, g2, 0, Merged: false));
                at = hi;
            }
            gap.Portion(at, x + w, PortionOpen);
            // A row runs from the inline start, the right in a right-to-left container.
            if (s.Rtl) gap.Reverse(2 * x + w);
            rowGaps.Add(gap);
        }
        PaintGapRules(s, colGaps, rowGaps, flex: true, slot);
    }

    /// <summary>
    /// Paints a column flex container's gap decorations: a row rule between the items of each line, as wide as the
    /// line, and with several lines a column rule between each two lines down the container's height
    /// (<paramref name="top"/> to <paramref name="bottom"/>), with a junction wherever a row gap of either line meets
    /// it. <paramref name="lines"/> holds each line in placement order with the top and bottom of its items' margin
    /// boxes from the top; <paramref name="reverse"/> says the items were placed from the bottom.
    /// </summary>
    private void FlexColumnGapRules(ComputedStyle s, List<(float X, float W, List<float> Edges)> lines, bool reverse, float top, float bottom, GroupOp slot)
    {
        var rowGaps = new List<DecorGap>();
        var lineGaps = new int[lines.Count];
        for (int li = 0; li < lines.Count; li++)
        {
            var (x, w, edges) = lines[li];
            int n = edges.Count / 2;
            lineGaps[li] = rowGaps.Count;
            for (int i = 0; i + 1 < n; i++)
            {
                float before = edges[2 * i + 1], after = edges[2 * i + 2];
                var gap = new DecorGap
                {
                    Mid = (before + after) / 2, Index = lineGaps[li] + (reverse ? n - 2 - i : i),
                    Suppressed = GapOnBreak(before, after),
                };
                gap.Portion(x, x + w, PortionOpen);
                if (s.Rtl) gap.Reverse(2 * x + w);
                rowGaps.Add(gap);
            }
        }
        var colGaps = new List<DecorGap>(Math.Max(0, lines.Count - 1));
        var spans = new List<(float Lo, float Hi, int Gap)>();
        for (int li = 0; li + 1 < lines.Count; li++)
        {
            var (x0, w0, e0) = lines[li];
            var (x1, w1, e1) = lines[li + 1];
            float mid = x1 >= x0 ? (x0 + w0 + x1) / 2 : (x1 + w1 + x0) / 2;
            var gap = new DecorGap { Mid = mid, Index = li };
            spans.Clear();
            for (int i = 0; i + 1 < e0.Count / 2; i++) spans.Add((e0[2 * i + 1], e0[2 * i + 2], lineGaps[li] + i));
            for (int i = 0; i + 1 < e1.Count / 2; i++) spans.Add((e1[2 * i + 1], e1[2 * i + 2], lineGaps[li + 1] + i));
            spans.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
            float at = top;
            for (int i = 0; i < spans.Count; i++)
            {
                var (lo, hi, g1) = spans[i];
                int g2 = -1;
                while (i + 1 < spans.Count && spans[i + 1].Lo <= hi + Eps)
                {
                    hi = Math.Max(hi, spans[++i].Hi);
                    if (g2 < 0) g2 = spans[i].Gap;
                }
                lo = Math.Clamp(lo, at, bottom);
                hi = Math.Clamp(hi, lo, bottom);
                gap.Portion(at, lo, PortionOpen);
                gap.Junctions.Add(new GapJunction(g1, 0, g2, 0, Merged: false));
                at = hi;
            }
            gap.Portion(at, bottom, PortionOpen);
            colGaps.Add(gap);
        }
        PaintGapRules(s, colGaps, rowGaps, flex: true, slot);
    }
}

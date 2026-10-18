using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// CSS Grid Level 2 subgrids: a grid item with <c>grid-template-columns: subgrid</c> (or rows) takes the tracks it
/// spans from its parent grid, and its items take part in sizing those tracks as if they were the parent's own. The
/// subgrid's margins, borders and padding count as extra margins on the items at its edges, and a gap that differs
/// from the parent's moves every inner line by half the difference, which is an extra margin on the items beside it.
/// </summary>
/// <remarks>
/// The parent hands the shared tracks down in a <see cref="SubgridContext"/> keyed by the subgrid element: the line
/// names, count and gap before the items are placed, and the track edges once they are sized. Columns are sized before
/// the items are measured, so a column subgrid is laid out once at the parent's column edges. Rows are sized from the
/// measured items, so a row subgrid is laid out twice: a measuring pass that reports its items' heights to the parent
/// in the <see cref="SubgridAxis.Contributions"/>, and a final pass at the parent's row edges, which reuses the boxes
/// the measuring pass kept so that nested subgrids cost no more than nested grids.
/// </remarks>
internal sealed partial class LayoutEngine
{
    /// <summary>What a subgrid takes from its parent grid on one axis.</summary>
    private sealed class SubgridAxis
    {
        public int Count;                                // the parent tracks spanned, which are all the subgrid's explicit tracks
        public float Gap;                                // the parent's gap, which the subgrid inherits when it sets none
        public Dictionary<string, List<int>>? Names;     // line names, renumbered from the subgrid's first line
        public float[]? Edges;                           // start and end of each spanned parent track, from the item's area start
        public List<(int Start, int Span, float Min, float Max)>? Contributions; // the measuring pass's row contributions, in subgrid lines
        public Captured?[]? Boxes;                       // the items measured in the measuring pass, for the final pass
        public float BoxesWidth;                         // the content width those boxes were measured at
    }

    private sealed class SubgridContext
    {
        public SubgridAxis? Cols, Rows;
    }

    /// <summary>The subgrids whose parent grid is being laid out, with the tracks they share.</summary>
    private readonly Dictionary<Element, SubgridContext> _subgrids = new();

    /// <summary>Subgrids nested deeper than this size the outer grid's columns as ordinary items, which bounds the work on hostile input.</summary>
    private const int MaxSubgridDepth = 32;

    /// <summary>Whether a grid item is a subgrid on the given axis; a subgrid in any other place falls back to <c>none</c>.</summary>
    private static bool IsSubgrid(BoxItem it, bool columns)
        => it.El != null && it.Style.IsGrid && (columns ? it.Style.GridTemplateColumns : it.Style.GridTemplateRows) is { Subgrid: true };

    /// <summary>The tracks a subgrid item takes from this grid on one axis, before they are sized.</summary>
    private static SubgridAxis SubgridAxisOf(BoxItem it, bool columns, float gap, Dictionary<string, List<int>>? names)
    {
        int start = columns ? it.Col : it.Row, count = columns ? it.ColSpan : it.RowSpan;
        var list = columns ? it.Style.GridTemplateColumns : it.Style.GridTemplateRows;
        return new SubgridAxis { Count = count, Gap = gap, Names = LineNames(list, 0, it.Style.GridTemplateAreas, columns, names, start, count) };
    }

    /// <summary>Keeps a subgrid item's area within the grid, so that its explicit tracks are the ones it really spans.</summary>
    private static void ClampSubgridSpan(BoxItem it, bool columns, int count)
    {
        if (columns) it.ColSpan = Math.Max(1, Math.Min(count, it.Col + it.ColSpan) - it.Col);
        else it.RowSpan = Math.Max(1, Math.Min(count, it.Row + it.RowSpan) - it.Row);
    }

    /// <summary>
    /// A subgrid has no implicit tracks on a subgridded axis: an item placed outside its explicit grid is clamped to
    /// the last track, and a span that reaches outside ends at the last line.
    /// </summary>
    private static void ClampToSubgrid(BoxItem it, bool columns, int limit)
    {
        ref int start = ref (columns ? ref it.Col : ref it.Row);
        ref int span = ref (columns ? ref it.ColSpan : ref it.RowSpan);
        if (start < 0) span = Math.Min(span, limit);
        else if (start >= limit) { start = limit - 1; span = 1; }
        else span = Math.Min(span, limit - start);
    }

    /// <summary>The subgrid's margin, border and padding before its first track (<paramref name="start"/>) or after its last, on one axis.</summary>
    private static float SubgridInset(BoxItem it, bool columns, bool start, float basis)
    {
        var s = it.Style;
        if (columns) return start ? it.Ml + s.BorderLeft + s.PaddingLeft.Resolve(basis) : it.Mr + s.BorderRight + s.PaddingRight.Resolve(basis);
        return start ? it.Mt + s.BorderTop + s.PaddingTop.Resolve(basis) : it.Mb + s.BorderBottom + s.PaddingBottom.Resolve(basis);
    }

    /// <summary>
    /// Maps a subgrid's items' track contributions to the lines of the grid that holds it: the subgrid's inset is an
    /// extra margin on the items at its edges, and a gap that differs from the parent's is half the difference on
    /// every item beside an inner line. An edge track no item reaches still gets the inset.
    /// </summary>
    private static void MapSubgridContributions(List<(int Start, int Span, float Min, float Max)> from, List<(int, int, float, float)> to,
        int start, int count, float insetStart, float insetEnd, float halfGapDiff)
    {
        bool first = false, last = false;
        foreach (var (s, span, min, max) in from)
        {
            bool atStart = s == 0, atEnd = s + span >= count;
            float extra = (atStart ? insetStart : halfGapDiff) + (atEnd ? insetEnd : halfGapDiff);
            to.Add((start + s, span, min + extra, max + extra));
            first |= atStart;
            last |= atEnd;
        }
        if (!first) to.Add((start, 1, insetStart, insetStart));
        if (!last) to.Add((start + count - 1, 1, insetEnd, insetEnd));
    }

    /// <summary>
    /// The items' contributions to the column tracks, in this grid's lines: an item's intrinsic widths with its
    /// margins, or for a column subgrid its items' contributions mapped through its edges.
    /// </summary>
    private void ColumnContributions(List<BoxItem> items, float basisW, float gap, Dictionary<string, List<int>>? colNames,
        Dictionary<string, List<int>>? rowNames, List<(int, int, float, float)> max, List<(int, int, float, float)>? min, int depth)
    {
        foreach (var it in items)
        {
            if (depth < MaxSubgridDepth && IsSubgrid(it, columns: true))
            {
                var subMax = new List<(int, int, float, float)>();
                var subMin = min != null ? new List<(int, int, float, float)>() : null;
                float subGap = SubgridColumnContributions(it, basisW, gap, colNames, rowNames, subMax, subMin, depth + 1);
                float insetL = SubgridInset(it, columns: true, start: true, basisW), insetR = SubgridInset(it, columns: true, start: false, basisW);
                MapSubgridContributions(subMax, max, it.Col, it.ColSpan, insetL, insetR, (subGap - gap) / 2);
                if (min != null) MapSubgridContributions(subMin!, min, it.Col, it.ColSpan, insetL, insetR, (subGap - gap) / 2);
                continue;
            }
            var (mn, mx) = ItemIntrinsic(it);
            max.Add((it.Col, it.ColSpan, mn + it.Ml + it.Mr, mx + it.Ml + it.Mr));
            min?.Add((it.Col, it.ColSpan, mn + it.Ml + it.Mr, mn + it.Ml + it.Mr));
        }
    }

    /// <summary>
    /// Places a column subgrid's items as its own layout will and collects their column contributions in the
    /// subgrid's lines, recursing into its subgrids. Returns the subgrid's column gap.
    /// </summary>
    private float SubgridColumnContributions(BoxItem sub, float basisW, float parentGap, Dictionary<string, List<int>>? parentColNames,
        Dictionary<string, List<int>>? parentRowNames, List<(int, int, float, float)> max, List<(int, int, float, float)>? min, int depth)
    {
        var e = sub.El!;
        var s = e.Style;
        var items = CollectBoxItems(e, basisW);
        float colGap = s.ColumnGap == null ? parentGap : GapOf(s.ColumnGap, basisW);
        var areas = s.GridTemplateAreas;
        var colNames = LineNames(s.GridTemplateColumns, 0, areas, columns: true, parentColNames, sub.Col, sub.ColSpan);
        bool rowSub = IsSubgrid(sub, columns: false);
        int explicitRows;
        Dictionary<string, List<int>>? rowNames;
        if (rowSub)
        {
            explicitRows = sub.RowSpan;
            rowNames = LineNames(s.GridTemplateRows, 0, areas, columns: false, parentRowNames, sub.Row, sub.RowSpan);
        }
        else
        {
            var rowDefs = ExpandTracks(s.GridTemplateRows, 0, GapOf(s.RowGap, 0), out _, out int rowRepCount);
            explicitRows = Math.Max(rowDefs.Count, areas?.Rows ?? 0);
            rowNames = LineNames(s.GridTemplateRows, rowRepCount, areas, columns: false);
        }
        PlaceGridItems(items, s, sub.ColSpan, explicitRows, colNames, rowNames, out _, out _, sub.ColSpan, rowSub ? sub.RowSpan : 0);
        ColumnContributions(items, basisW, colGap, colNames, rowNames, max, min, depth);
        return colGap;
    }

    /// <summary>
    /// The subgrid's tracks on a subgridded axis, from the parent's track edges relative to the item's area: the first
    /// and last tracks start and end at the subgrid's content box, and every inner line sits in the middle of the
    /// parent's gutter, half the subgrid's own gap from the line beside it. Fills <paramref name="positions"/> with the
    /// track starts and the end of the last one, from the area's start.
    /// </summary>
    private static GridTrack[] SubgridTracks(float[] edges, int count, float gap, float insetStart, float insetEnd, float[] positions)
    {
        var tracks = new GridTrack[count];
        float extent = edges[^1];
        for (int i = 0; i < count; i++)
        {
            float start = i == 0 ? insetStart : (edges[2 * i - 1] + edges[2 * i]) / 2 + gap / 2;
            float end = i == count - 1 ? extent - insetEnd : (edges[2 * i + 1] + edges[2 * i + 2]) / 2 - gap / 2;
            positions[i] = start;
            tracks[i] = new GridTrack { Def = AutoTrack, Base = Math.Max(0, end - start) };
        }
        positions[count] = positions[count - 1] + tracks[count - 1].Base;
        return tracks;
    }
}

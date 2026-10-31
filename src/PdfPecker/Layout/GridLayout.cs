using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// CSS grid layout: line-based and automatic placement (row flow, sparse), track sizing with fixed, percentage, fr,
/// auto, min-content, max-content and minmax() tracks, repeat() including auto-fill/auto-fit, gaps and box alignment.
/// Named lines and grid-template-areas are resolved to line numbers. Rows are paginated as units. Subgrids, which
/// share their parent's tracks, are in GridLayout.Subgrid.cs.
/// </summary>
internal sealed partial class LayoutEngine
{
    private sealed class GridTrack
    {
        public Track Def;
        public float Base, Limit;
        public bool Collapsed; // an empty auto-fit track
        public bool IsFlexible => Def.Max.Kind == TrackSizeKind.Fr;
    }

    private static readonly Track AutoTrack = new(new TrackSize(TrackSizeKind.Auto), new TrackSize(TrackSizeKind.Auto));

    /// <summary>Expands a track list for an available size, resolving auto-fill/auto-fit repetitions.</summary>
    private static List<Track> ExpandTracks(TrackList? list, float avail, float gap, out int repeatStart, out int repeatCount)
    {
        var result = new List<Track>();
        repeatStart = repeatCount = 0;
        if (list == null) return result;
        result.AddRange(list.Before);
        if (list.AutoRepeat is { } rep)
        {
            static float Fixed(TrackSize t, float avail) => t.Kind switch
            {
                TrackSizeKind.Fixed => t.Value,
                TrackSizeKind.Percent => t.ResolvePercent(avail),
                _ => -1,
            };
            float other = 0;
            foreach (var t in list.Before.Concat(list.After))
                other += Math.Max(0, Math.Max(Fixed(t.Min, avail), Fixed(t.Max, avail)));
            float each = 0;
            foreach (var t in rep)
            {
                float f = Fixed(t.Max, avail);
                if (f < 0) f = Fixed(t.Min, avail);
                each += Math.Max(f, 0);
            }
            int otherCount = list.Before.Length + list.After.Length;
            int count = 1;
            if (each > 0)
            {
                while (count < 1000)
                {
                    int tracks = otherCount + (count + 1) * rep.Length;
                    float total = other + (count + 1) * each + (tracks - 1) * gap;
                    if (total > avail + Eps) break;
                    count++;
                }
            }
            repeatStart = result.Count;
            repeatCount = count * rep.Length;
            for (int k = 0; k < count; k++) result.AddRange(rep);
        }
        result.AddRange(list.After);
        return result;
    }

    private static int LineToIndex(int line, int explicitCount) => line > 0 ? line - 1 : explicitCount + 1 + line;

    /// <summary>Resolves an item's start index and span on one axis; start is -1 when it is auto.</summary>
    private static void ResolveSpan(GridLine start, GridLine end, int explicitCount, Dictionary<string, List<int>>? names, out int index, out int span)
    {
        index = -1;
        span = 1;
        if (start.Line != 0) index = Math.Max(0, LineToIndex(start.Line, explicitCount));
        if (end.Line != 0)
        {
            int endIndex = Math.Max(0, LineToIndex(end.Line, explicitCount));
            if (index >= 0)
            {
                if (endIndex < index) (index, endIndex) = (endIndex, index);
                span = Math.Max(1, endIndex - index);
            }
            else if (start.Span > 0 && start.SpanName != null)
            {
                // "span name" before a definite end: back to the nth line of that name before the end.
                int startLine = NthNamedLine(names, start.SpanName, endIndex + 1, start.Span, forward: false);
                index = startLine > 0 ? startLine - 1 : 0;
                span = Math.Max(1, endIndex - index);
            }
            else
            {
                span = start.Span > 0 ? start.Span : 1;
                index = Math.Max(0, endIndex - span);
            }
        }
        else if (end.Span > 0)
        {
            span = end.Span;
            // "span name" after a definite start: on to the nth line of that name after the start.
            if (end.SpanName != null && index >= 0)
            {
                int endLine = NthNamedLine(names, end.SpanName, index + 1, end.Span, forward: true);
                // With too few lines of that name, the span ends past the explicit grid, as if they were all implicit.
                span = endLine > 0 ? Math.Max(1, endLine - 1 - index) : Math.Max(1, explicitCount + end.Span - index);
            }
        }
        else if (start.Span > 0) span = start.Span;
    }

    /// <summary>
    /// The 1-based number of the nth line named <paramref name="name"/> after (or before) line <paramref name="from"/>,
    /// or 0 when there are not that many.
    /// </summary>
    private static int NthNamedLine(Dictionary<string, List<int>>? names, string name, int from, int n, bool forward)
    {
        if (names == null || !names.TryGetValue(name, out var lines)) return 0;
        int count = 0;
        if (forward)
        {
            foreach (var line in lines)
                if (line > from && ++count == n) return line;
        }
        else
        {
            for (int k = lines.Count - 1; k >= 0; k--)
                if (lines[k] < from && ++count == n) return lines[k];
        }
        return 0;
    }

    /// <summary>
    /// 1-based line numbers by name on one axis: names from the track list, and name-start/name-end lines from
    /// grid-template-areas. Null when there are none. On a subgridded axis (<paramref name="subgridCount"/> tracks from
    /// the parent's line <paramref name="subgridStart"/>), the parent's names of the spanned lines come first,
    /// renumbered from the subgrid's first line, then the subgrid's own name lists.
    /// </summary>
    private static Dictionary<string, List<int>>? LineNames(TrackList? list, int repeatCount, GridAreas? areas, bool columns,
        Dictionary<string, List<int>>? parentNames = null, int subgridStart = 0, int subgridCount = 0)
    {
        Dictionary<string, List<int>>? names = null;
        void Add(string name, int line)
        {
            names ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
            if (!names.TryGetValue(name, out var lines)) names[name] = lines = new List<int>(1);
            if (!lines.Contains(line)) lines.Add(line);
        }
        if (subgridCount > 0)
        {
            if (parentNames != null)
                foreach (var (name, lines) in parentNames)
                    foreach (var line in lines)
                        if (line > subgridStart && line <= subgridStart + subgridCount + 1) Add(name, line - subgridStart);
            if (list != null)
            {
                var own = list.SubgridLineNames(subgridCount + 1);
                for (int i = 0; i < own.Length; i++)
                    if (own[i] is { } lineNames)
                        foreach (var name in lineNames) Add(name, i + 1);
            }
        }
        else if (list?.LineNames is { } ln)
            foreach (var (name, line, inAfter) in ln)
                Add(name, line + 1 + (inAfter ? list.Before.Length + repeatCount : 0));
        if (areas != null)
            foreach (var (name, a) in areas.Areas)
            {
                Add(name + "-start", (columns ? a.ColStart : a.RowStart) + 1);
                Add(name + "-end", (columns ? a.ColEnd : a.RowEnd) + 1);
            }
        if (names != null) foreach (var lines in names.Values) lines.Sort();
        return names;
    }

    /// <summary>Turns a named line into a line number: "a" means the a-start (or a-end) line of area a, else the line named a.</summary>
    private static GridLine ResolveName(GridLine gl, Dictionary<string, List<int>>? names, bool isStart)
    {
        if (gl.Name == null) return gl;
        if (names == null) return default;
        if (!names.TryGetValue(gl.Name + (isStart ? "-start" : "-end"), out var lines) && !names.TryGetValue(gl.Name, out lines)) return default;
        int n = gl.Line == 0 ? 1 : gl.Line;
        int index = n > 0 ? Math.Min(n, lines.Count) - 1 : Math.Max(0, lines.Count + n);
        return new GridLine(lines[index], 0);
    }

    /// <summary>
    /// Places the items in the grid (grid-auto-flow: row or column, sparse or dense). Returns the number of columns and
    /// rows. Column flow is row flow with the axes swapped. A subgridded axis has exactly <paramref name="colLimit"/>
    /// (or <paramref name="rowLimit"/>) tracks and the items are clamped to them.
    /// </summary>
    private static void PlaceGridItems(List<BoxItem> items, ComputedStyle s, int explicitCols, int explicitRows,
        Dictionary<string, List<int>>? colNames, Dictionary<string, List<int>>? rowNames, out int colCount, out int rowCount,
        int colLimit = 0, int rowLimit = 0)
    {
        bool columnFlow = s.GridAutoFlowColumn, dense = s.GridAutoFlowDense;
        foreach (var it in items)
        {
            var st = it.Style;
            ResolveSpan(ResolveName(st.GridColumnStart, colNames, true), ResolveName(st.GridColumnEnd, colNames, false), explicitCols, colNames, out int c, out int cs);
            ResolveSpan(ResolveName(st.GridRowStart, rowNames, true), ResolveName(st.GridRowEnd, rowNames, false), explicitRows, rowNames, out int r, out int rs);
            if (st.IsGrid && it.El != null)
            {
                // A subgrid whose placement leaves its span automatic spans the lines its own name lists name.
                if (st.GridTemplateColumns is { Subgrid: true } sc && st.GridColumnStart.IsAuto && st.GridColumnEnd.IsAuto) cs = Math.Max(1, sc.SubgridNamedLines - 1);
                if (st.GridTemplateRows is { Subgrid: true } sr && st.GridRowStart.IsAuto && st.GridRowEnd.IsAuto) rs = Math.Max(1, sr.SubgridNamedLines - 1);
            }
            it.Col = c; it.ColSpan = Math.Min(cs, 1000); it.ColFixed = c >= 0;
            it.Row = r; it.RowSpan = Math.Min(rs, 1000); it.RowFixed = r >= 0;
            if (colLimit > 0) ClampToSubgrid(it, columns: true, colLimit);
            if (rowLimit > 0) ClampToSubgrid(it, columns: false, rowLimit);
            if (columnFlow) Transpose(it);
        }
        int across = PlaceRowMajor(items, columnFlow ? explicitRows : explicitCols, dense, out int along);
        if (columnFlow)
        {
            foreach (var it in items) Transpose(it);
            colCount = Math.Max(explicitCols, along);
            rowCount = across;
        }
        else
        {
            colCount = across;
            rowCount = Math.Max(explicitRows, along);
        }
        if (colLimit > 0 || rowLimit > 0)
        {
            // Automatic placement may have run past the subgrid's tracks; what did is clamped to the last one.
            foreach (var it in items)
            {
                if (colLimit > 0) ClampToSubgrid(it, columns: true, colLimit);
                if (rowLimit > 0) ClampToSubgrid(it, columns: false, rowLimit);
            }
            if (colLimit > 0) colCount = colLimit;
            if (rowLimit > 0) rowCount = rowLimit;
        }

        static void Transpose(BoxItem it)
        {
            (it.Col, it.Row) = (it.Row, it.Col);
            (it.ColSpan, it.RowSpan) = (it.RowSpan, it.ColSpan);
            (it.ColFixed, it.RowFixed) = (it.RowFixed, it.ColFixed);
        }
    }

    /// <summary>
    /// Auto-placement along rows (CSS Grid 8.5): the number of columns is fixed by the explicit grid and the definite
    /// placements, and rows are added as needed. Returns the column count; <paramref name="rowCount"/> gets the rows used.
    /// </summary>
    private static int PlaceRowMajor(List<BoxItem> items, int explicitCols, bool dense, out int rowCount)
    {
        int colCount = Math.Max(1, explicitCols);
        foreach (var it in items) colCount = Math.Max(colCount, (it.ColFixed ? it.Col : 0) + it.ColSpan);
        colCount = Math.Min(colCount, 1000);

        var occupied = new List<bool[]>();
        int cols = colCount;
        // An item placed past the column limit would never find a free cell, so it moves back into the last columns.
        foreach (var it in items)
        {
            it.ColSpan = Math.Min(it.ColSpan, cols);
            if (it.ColFixed) it.Col = Math.Min(it.Col, cols - it.ColSpan);
        }
        bool Free(int row, int col, int rspan, int cspan)
        {
            if (col + cspan > cols) return false;
            for (int rr = row; rr < row + rspan; rr++)
            {
                if (rr >= occupied.Count) continue;
                for (int cc = col; cc < col + cspan; cc++) if (occupied[rr][cc]) return false;
            }
            return true;
        }
        void Occupy(BoxItem it)
        {
            while (occupied.Count < it.Row + it.RowSpan) occupied.Add(new bool[cols]);
            for (int rr = it.Row; rr < it.Row + it.RowSpan; rr++)
                for (int cc = it.Col; cc < Math.Min(cols, it.Col + it.ColSpan); cc++) occupied[rr][cc] = true;
        }

        // 1. Fully positioned items.
        foreach (var it in items)
            if (it.RowFixed && it.ColFixed) Occupy(it);
        // 2. Items locked to a row.
        foreach (var it in items)
        {
            if (!it.RowFixed || it.ColFixed) continue;
            int col = 0;
            while (!Free(it.Row, col, it.RowSpan, it.ColSpan) && col + it.ColSpan < cols) col++;
            it.Col = Math.Min(col, Math.Max(0, cols - it.ColSpan));
            Occupy(it);
        }
        // 3. Everything else. Sparse packing keeps a cursor that only moves forward; dense packing starts every search
        // from the first row, so later small items fill the holes left by earlier big ones.
        int curRow = 0, curCol = 0;
        foreach (var it in items)
        {
            if (it.RowFixed) continue;
            it.ColSpan = Math.Min(it.ColSpan, cols);
            if (dense) curRow = curCol = 0;
            if (it.ColFixed)
            {
                if (!dense && it.Col < curCol) curRow++;
                curCol = it.Col;
                while (!Free(curRow, it.Col, it.RowSpan, it.ColSpan)) curRow++;
                it.Row = curRow;
            }
            else
            {
                while (true)
                {
                    if (curCol + it.ColSpan > cols) { curRow++; curCol = 0; continue; }
                    if (Free(curRow, curCol, it.RowSpan, it.ColSpan)) break;
                    curCol++;
                }
                it.Row = curRow;
                it.Col = curCol;
            }
            Occupy(it);
            curCol = it.Col + it.ColSpan;
        }
        rowCount = occupied.Count;
        foreach (var it in items) rowCount = Math.Max(rowCount, it.Row + it.RowSpan);
        return colCount;
    }

    private static GridTrack[] MakeTracks(List<Track> explicitTracks, Track[]? autoTracks, int count)
    {
        var tracks = new GridTrack[count];
        for (int i = 0; i < count; i++)
        {
            Track def;
            if (i < explicitTracks.Count) def = explicitTracks[i];
            else if (autoTracks is { Length: > 0 }) def = autoTracks[(i - explicitTracks.Count) % autoTracks.Length];
            else def = AutoTrack;
            tracks[i] = new GridTrack { Def = def };
        }
        return tracks;
    }

    private static float FixedSize(TrackSize t, float basis, bool definite)
        => t.Kind switch
        {
            TrackSizeKind.Fixed => t.Value,
            TrackSizeKind.Percent when definite => t.ResolvePercent(basis),
            _ => -1,
        };

    /// <summary>
    /// Sizes tracks from item contributions (§11.5 simplified): fixed sizes, intrinsic sizes from single-span items, extra
    /// space for spanning items, maximisation up to the growth limits, then fr tracks share what is left.
    /// </summary>
    private static void SizeTracks(GridTrack[] tracks, float avail, bool definite, float gap,
        List<(int Start, int Span, float Min, float Max)> contributions, bool stretchAuto)
    {
        int n = tracks.Length;
        foreach (var t in tracks)
        {
            float fmin = FixedSize(t.Def.Min, avail, definite);
            float fmax = FixedSize(t.Def.Max, avail, definite);
            t.Base = fmin >= 0 ? fmin : 0;
            t.Limit = fmax >= 0 ? Math.Max(fmax, t.Base) : (t.IsFlexible ? t.Base : float.PositiveInfinity);
        }

        // Intrinsic sizes from single-span items.
        var hasLimit = new bool[n];
        foreach (var (start, span, min, max) in contributions)
        {
            if (span != 1 || start >= n) continue;
            var t = tracks[start];
            if (t.Collapsed) continue;
            switch (t.Def.Min.Kind)
            {
                case TrackSizeKind.Auto:
                case TrackSizeKind.MinContent:
                    t.Base = Math.Max(t.Base, min);
                    break;
                case TrackSizeKind.MaxContent:
                    t.Base = Math.Max(t.Base, max);
                    break;
                case TrackSizeKind.Percent when !definite:
                    t.Base = Math.Max(t.Base, min);
                    break;
            }
            switch (t.Def.Max.Kind)
            {
                case TrackSizeKind.Auto:
                case TrackSizeKind.MaxContent:
                    // fit-content(cap) grows no further than the cap, unless the min-content size needs more.
                    float grow = t.Def.Max.Value > 0 ? Math.Min(max, t.Def.Max.Value) : max;
                    t.Limit = hasLimit[start] ? Math.Max(t.Limit, grow) : grow;
                    hasLimit[start] = true;
                    break;
                case TrackSizeKind.MinContent:
                    t.Limit = hasLimit[start] ? Math.Max(t.Limit, min) : min;
                    hasLimit[start] = true;
                    break;
                case TrackSizeKind.Percent when !definite:
                case TrackSizeKind.Fr when !definite:
                    t.Limit = hasLimit[start] ? Math.Max(t.Limit, max) : max;
                    hasLimit[start] = true;
                    break;
            }
        }
        foreach (var t in tracks)
        {
            if (float.IsPositiveInfinity(t.Limit) && t.Def.Max.IsIntrinsic) t.Limit = t.Base;
            if (t.Limit < t.Base) t.Limit = t.Base;
        }

        // Spanning items: grow intrinsic tracks they cross so that the item fits.
        foreach (var (start, span, min, max) in contributions)
        {
            if (span <= 1 || start >= n) continue;
            int end = Math.Min(n, start + span);
            float current = gap * (end - start - 1), currentLimit = current;
            var growable = new List<GridTrack>();
            for (int i = start; i < end; i++)
            {
                current += tracks[i].Base;
                currentLimit += tracks[i].Limit;
                if (tracks[i].Def.Min.IsIntrinsic || tracks[i].IsFlexible) growable.Add(tracks[i]);
            }
            if (growable.Count == 0) continue;
            if (min > current)
                foreach (var t in growable) t.Base += (min - current) / growable.Count;
            if (max > currentLimit)
                foreach (var t in growable) if (!t.IsFlexible) t.Limit += (max - currentLimit) / growable.Count;
            foreach (var t in growable) if (t.Limit < t.Base) t.Limit = t.Base;
        }

        if (!definite)
        {
            // Indefinite space (max-content sizing): every track takes its growth limit, fr tracks their content.
            foreach (var t in tracks) if (!float.IsPositiveInfinity(t.Limit)) t.Base = Math.Max(t.Base, t.Limit);
            return;
        }

        float Used()
        {
            float u = 0;
            int visible = 0;
            foreach (var t in tracks) { if (t.Collapsed) continue; u += t.Base; visible++; }
            return u + gap * Math.Max(0, visible - 1);
        }

        // Maximise tracks: hand out free space up to the growth limits.
        float free = avail - Used();
        for (int pass = 0; pass < n + 1 && free > Eps; pass++)
        {
            var open = tracks.Where(t => !t.Collapsed && !t.IsFlexible && t.Limit > t.Base + Eps && !float.IsPositiveInfinity(t.Limit)).ToList();
            if (open.Count == 0) break;
            float share = free / open.Count;
            foreach (var t in open)
            {
                float add = Math.Min(share, t.Limit - t.Base);
                t.Base += add;
                free -= add;
            }
        }

        // Flexible tracks share the remaining space; tracks whose content needs more keep their base size.
        var flex = tracks.Where(t => t.IsFlexible && !t.Collapsed).ToList();
        if (flex.Count > 0)
        {
            float leftover = avail - gap * Math.Max(0, tracks.Count(t => !t.Collapsed) - 1);
            foreach (var t in tracks) if (!t.IsFlexible && !t.Collapsed) leftover -= t.Base;
            var inflexible = new HashSet<GridTrack>();
            for (int pass = 0; pass <= flex.Count; pass++)
            {
                float frSum = 0, space = leftover;
                foreach (var t in flex)
                {
                    if (inflexible.Contains(t)) space -= t.Base;
                    else frSum += t.Def.Max.Value;
                }
                if (frSum <= 0) break;
                float frSize = Math.Max(0, space) / Math.Max(frSum, 1);
                bool changed = false;
                foreach (var t in flex)
                {
                    if (inflexible.Contains(t)) continue;
                    if (t.Def.Max.Value * frSize < t.Base - Eps) { inflexible.Add(t); changed = true; }
                }
                if (changed) continue;
                foreach (var t in flex)
                    if (!inflexible.Contains(t)) t.Base = Math.Max(t.Base, t.Def.Max.Value * frSize);
                break;
            }
            free = avail - Used();
        }

        // justify-content: normal / stretch grows auto tracks to fill the container.
        if (stretchAuto && free > Eps)
        {
            var autos = tracks.Where(t => !t.Collapsed && t.Def.Max.Kind == TrackSizeKind.Auto).ToList();
            if (autos.Count > 0) foreach (var t in autos) t.Base += free / autos.Count;
        }
    }

    /// <summary>
    /// The state of one <see cref="LayoutGrid"/> call, which its phases share by reference: the container, its items,
    /// the subgrid contexts it takes tracks from, and the tracks and their positions once they are sized.
    /// </summary>
    private struct GridPass
    {
        public Element E;
        public ComputedStyle S;
        public float X, Y, W;
        public float? DefiniteH;
        public List<BoxItem> Items;
        public SubgridAxis? ColCtx, RowCtx;  // the parent grid's tracks on a subgridded axis
        public bool MeasuringRows, FinalRows; // the two passes of a row subgrid
        public bool HasSubgrids;              // some item is a subgrid of this grid
        public float ColGap, RowGap;
        public Dictionary<string, List<int>>? ColNames, RowNames;
        public int ColCount, RowCount;
        public GridTrack[] Cols, Rows;
        public float[] ColX, RowY;            // the start of each track, and the end of the last one
        public float[]? ColOffset;            // ColX from the grid's left edge, when the grid sized its own columns
        public bool[] StretchX;               // whether each item is stretched over its area's width

        /// <summary>
        /// The width of an item's grid area. It is taken from the column offsets rather than the positions, which far
        /// from the page origin a float rounds to steps of many pixels, so an item is measured and laid out at the same
        /// width wherever the grid is.
        /// </summary>
        public readonly float AreaWidth(BoxItem it)
        {
            int end = Math.Min(ColCount, it.Col + it.ColSpan);
            var x = ColOffset ?? ColX;
            return Math.Max(0, x[end - 1] + (Cols[end - 1].Collapsed ? 0 : Cols[end - 1].Base) - x[it.Col]);
        }

        public readonly float ColRight(int c) => ColX[c] + (Cols[c].Collapsed ? 0 : Cols[c].Base);
    }

    private float LayoutGrid(Element e, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        var items = CollectBoxItems(e, w);
        QueueAbsoluteChildren(e, x, y);
        // A subgrid's tracks on a subgridded axis come from the parent grid that is laying it out (GridLayout.Subgrid.cs).
        // Without a parent grid, subgrid falls back to none. The parent captures the subgrid at the origin of its grid
        // area, so the content box's x and y are the insets of the first tracks, and the area's extent is the edges' end.
        _subgrids.TryGetValue(e, out var ctx);
        var g = new GridPass { E = e, S = s, X = x, Y = y, W = w, DefiniteH = definiteH, Items = items };
        g.ColCtx = s.GridTemplateColumns?.Subgrid == true ? ctx?.Cols : null;
        g.RowCtx = s.GridTemplateRows?.Subgrid == true ? ctx?.Rows : null;
        g.MeasuringRows = g.RowCtx is { Edges: null };
        g.FinalRows = g.RowCtx is { Edges: not null };
        g.ColGap = g.ColCtx != null && s.ColumnGap == null ? g.ColCtx.Gap : GapOf(s.ColumnGap, w);
        g.RowGap = g.RowCtx != null && s.RowGap == null ? g.RowCtx.Gap : GapOf(s.RowGap, definiteH ?? 0);

        var colDefs = ExpandTracks(s.GridTemplateColumns, w, g.ColGap, out int repStart, out int repCount);
        var rowDefs = ExpandTracks(s.GridTemplateRows, definiteH ?? 0, g.RowGap, out _, out int rowRepCount);
        if (items.Count == 0)
        {
            if (g.MeasuringRows) g.RowCtx!.Contributions = new List<(int, int, float, float)>();
            return y;
        }
        PlaceGridArea(ref g, colDefs.Count, rowDefs.Count, repCount, rowRepCount);
        SizeGridColumns(ref g, colDefs, repStart, repCount);
        MeasureGridItems(ref g);

        g.RowY = new float[g.RowCount + 1];
        if (g.FinalRows)
        {
            float extent = g.RowCtx!.Edges![^1];
            g.Rows = SubgridTracks(g.RowCtx.Edges, g.RowCount, g.RowGap, y, definiteH is { } dh ? extent - y - dh : SubgridBottomInset(s, w), g.RowY);
        }
        else
        {
            var rowContrib = SizeGridRows(ref g, rowDefs, out float rowsTotal);
            if (g.MeasuringRows)
            {
                // The parent sizes the rows and lays the subgrid out again at them; the boxes wait for that pass.
                g.RowCtx!.Contributions = rowContrib;
                g.RowCtx.Boxes = new Captured?[items.Count];
                for (int i = 0; i < items.Count; i++) g.RowCtx.Boxes[i] = items[i].Box;
                g.RowCtx.BoxesWidth = w;
                return y + rowsTotal;
            }
            AlignGridContent(ref g, definiteH ?? Math.Max(minH, rowsTotal), rowsTotal, out float rowBefore, out float rowBetween);
            PositionGridRows(ref g, rowBefore, rowBetween);
        }

        var rowShift = PlaceGridItemsInAreas(ref g);
        NoteAbsoluteGridAreas(ref g, rowShift, g.ColCtx?.Count ?? Math.Max(colDefs.Count, s.GridTemplateAreas?.Columns ?? 0),
            g.RowCtx?.Count ?? Math.Max(rowDefs.Count, s.GridTemplateAreas?.Rows ?? 0));
        if (g.HasSubgrids)
            foreach (var it in items)
                if (it.El != null && it.Style.IsGrid) _subgrids.Remove(it.El);
        _lastBoxRight = Math.Max(_lastBoxRight, g.ColX[g.ColCount]);
        return Math.Max(g.RowY[g.RowCount] + (rowShift?[g.RowCount] ?? 0), y);
    }

    /// <summary>
    /// Notes the grid areas of the absolutely positioned children that are placed in the grid, which are their containing
    /// blocks when the grid is (CSS Grid §9.1). An edge whose line is auto, or not in the grid, is the padding edge, NaN.
    /// </summary>
    private void NoteAbsoluteGridAreas(ref GridPass g, float[]? rowShift, int explicitCols, int explicitRows)
    {
        foreach (var c in g.E.Children)
        {
            if (c is not Element { Style: { Position: Position.Absolute, Display: not Display.None } st } e || e.Detached) continue;
            if (st.GridColumnStart.IsAuto && st.GridColumnEnd.IsAuto && st.GridRowStart.IsAuto && st.GridRowEnd.IsAuto)
            {
                _gridAreas?.Remove(e);
                continue;
            }
            var (c0, c1) = AreaLines(ResolveName(st.GridColumnStart, g.ColNames, true), ResolveName(st.GridColumnEnd, g.ColNames, false), explicitCols, g.ColCount);
            var (r0, r1) = AreaLines(ResolveName(st.GridRowStart, g.RowNames, true), ResolveName(st.GridRowEnd, g.RowNames, false), explicitRows, g.RowCount);
            _gridAreas ??= new Dictionary<Element, (float, float, float, float)>(ReferenceEqualityComparer.Instance);
            _gridAreas[e] = (c0 < 0 ? float.NaN : g.ColX[c0],
                             c1 < 0 ? float.NaN : g.ColRight(c1 - 1),
                             r0 < 0 ? float.NaN : g.RowY[r0] + (rowShift?[r0] ?? 0),
                             r1 < 0 ? float.NaN : g.RowY[r1 - 1] + (rowShift?[r1 - 1] ?? 0) + g.Rows[r1 - 1].Base);
        }
    }

    /// <summary>
    /// The 0-based start and end lines of an absolutely positioned box's area on one axis, or -1 for an edge that is the
    /// padding edge: an auto line, or a line outside the grid's <paramref name="count"/> tracks.
    /// </summary>
    private static (int Start, int End) AreaLines(GridLine start, GridLine end, int explicitCount, int count)
    {
        int s = start.Line != 0 ? LineToIndex(start.Line, explicitCount) : -1;
        int e = end.Line != 0 ? LineToIndex(end.Line, explicitCount) : -1;
        if (s >= 0 && e < 0 && end.Span > 0) e = s + end.Span;
        else if (e >= 0 && s < 0 && start.Span > 0) s = e - start.Span;
        if (s >= 0 && e >= 0 && e < s) (s, e) = (e, s);
        if (s >= 0 && e == s) e = s + 1;
        return (s >= 0 && s < count ? s : -1, e >= 1 && e <= count ? e : -1);
    }

    /// <summary>Resolves the line names and places the items in the grid, which sets the number of tracks on each axis.</summary>
    private static void PlaceGridArea(ref GridPass g, int explicitCols, int explicitRows, int repCount, int rowRepCount)
    {
        var s = g.S;
        var areas = s.GridTemplateAreas;
        g.ColNames = g.ColCtx?.Names ?? LineNames(s.GridTemplateColumns, repCount, areas, columns: true);
        g.RowNames = g.RowCtx?.Names ?? LineNames(s.GridTemplateRows, rowRepCount, areas, columns: false);
        PlaceGridItems(g.Items, s, g.ColCtx?.Count ?? Math.Max(explicitCols, areas?.Columns ?? 0), g.RowCtx?.Count ?? Math.Max(explicitRows, areas?.Rows ?? 0),
            g.ColNames, g.RowNames, out g.ColCount, out g.RowCount, g.ColCtx?.Count ?? 0, g.RowCtx?.Count ?? 0);
        foreach (var it in g.Items)
        {
            if (IsSubgrid(it, columns: true)) { ClampSubgridSpan(it, columns: true, g.ColCount); g.HasSubgrids = true; }
            if (IsSubgrid(it, columns: false)) { ClampSubgridSpan(it, columns: false, g.RowCount); g.HasSubgrids = true; }
        }
    }

    /// <summary>
    /// Sizes the columns from the items' intrinsic widths and positions them with justify-content, or takes them from
    /// the parent grid on a column subgrid.
    /// </summary>
    private void SizeGridColumns(ref GridPass g, List<Track> colDefs, int repStart, int repCount)
    {
        var s = g.S;
        var items = g.Items;
        int colCount = g.ColCount;
        float colGap = g.ColGap;
        var colX = g.ColX = new float[colCount + 1];
        if (g.ColCtx?.Edges != null)
        {
            g.ColOffset = null;
            g.Cols = SubgridTracks(g.ColCtx.Edges, colCount, colGap, g.X, g.ColCtx.Edges[^1] - g.X - g.W, colX);
            return;
        }
        var cols = g.Cols = MakeTracks(colDefs, s.GridAutoColumns, colCount);
        if (s.GridTemplateColumns?.AutoFit == true)
        {
            for (int c = repStart; c < repStart + repCount && c < cols.Length; c++)
                cols[c].Collapsed = !items.Any(it => it.Col <= c && c < it.Col + it.ColSpan);
        }

        // Column sizes from the items' intrinsic widths.
        var contributions = new List<(int, int, float, float)>(items.Count);
        ColumnContributions(items, g.W, colGap, g.ColNames, g.RowNames, contributions, null, 0);
        SizeTracks(cols, g.W, definite: true, colGap, contributions, s.JustifyContent is BoxAlign.Normal or BoxAlign.Stretch);

        // Column positions, with justify-content.
        float used = 0;
        int visible = 0;
        foreach (var t in cols) if (!t.Collapsed) { used += t.Base; visible++; }
        used += colGap * Math.Max(0, visible - 1);
        Distribute(s.JustifyContent, g.W - used, visible, out float before, out float between);
        var offset = g.ColOffset = new float[colCount + 1];
        float cx = before;
        bool first = true;
        for (int c = 0; c < colCount; c++)
        {
            if (cols[c].Collapsed) { offset[c] = cx; continue; }
            if (!first) cx += colGap + between;
            first = false;
            offset[c] = cx;
            cx += cols[c].Base;
        }
        offset[colCount] = cx;
        for (int c = 0; c <= colCount; c++) colX[c] = g.X + offset[c];
    }

    /// <summary>
    /// Lays out every item at its area width to find the row heights, and hands each subgrid item the tracks it
    /// shares. A row subgrid's final pass reuses the boxes of its measuring pass, which were laid out at the same columns.
    /// </summary>
    private void MeasureGridItems(ref GridPass g)
    {
        var s = g.S;
        var items = g.Items;
        var stretchX = g.StretchX = new bool[items.Count];
        var rowCtx = g.RowCtx;
        var measured = g.FinalRows && rowCtx!.Boxes is { } kept && kept.Length == items.Count && rowCtx.BoxesWidth == g.W ? kept : null;
        if (g.FinalRows) rowCtx!.Boxes = null;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            float areaW = g.AreaWidth(it);
            var js = it.Style.JustifySelf == BoxAlign.Auto ? s.JustifyItems : it.Style.JustifySelf;
            if (js is BoxAlign.Normal or BoxAlign.Auto) js = it.IsReplaced ? BoxAlign.Start : BoxAlign.Stretch;
            stretchX[i] = js == BoxAlign.Stretch && !it.AutoMl && !it.AutoMr;
            if (g.HasSubgrids) ShareGridTracks(ref g, it, i);
            // An image stretched by an explicit justify-self or justify-items takes the area's width, as in Chrome.
            float? borderW = stretchX[i] && it.IsReplaced && it.Style.Width.IsAuto ? StretchedReplacedWidth(it, areaW, g.W) : null;
            it.Box = measured?[i] ?? Measure(it, g.E, areaW, stretchX[i], borderW);
        }
    }

    /// <summary>Hands a subgrid item the tracks it takes from this grid: the column edges, and the row names and gap.</summary>
    private void ShareGridTracks(ref GridPass g, BoxItem it, int i)
    {
        // A subgrid is stretched over its area on a subgridded axis, whatever its alignment and margins say.
        bool colSub = IsSubgrid(it, columns: true), rowSub = IsSubgrid(it, columns: false);
        if (colSub) g.StretchX[i] = true;
        if (!colSub && !rowSub) return;
        // The final pass of a row subgrid keeps the contexts of its measuring pass, with the boxes they hold.
        SubgridContext? sub = null;
        if (g.FinalRows) _subgrids.TryGetValue(it.El!, out sub);
        if (sub == null) _subgrids[it.El!] = sub = new SubgridContext();
        if (colSub)
        {
            sub.Cols ??= SubgridAxisOf(it, columns: true, g.ColGap, g.ColNames);
            var edges = new float[2 * it.ColSpan];
            for (int k = 0; k < it.ColSpan; k++)
            {
                edges[2 * k] = g.ColX[it.Col + k] - g.ColX[it.Col];
                edges[2 * k + 1] = g.ColRight(it.Col + k) - g.ColX[it.Col];
            }
            sub.Cols.Edges = edges;
        }
        if (rowSub) sub.Rows ??= SubgridAxisOf(it, columns: false, g.RowGap, g.RowNames);
    }

    /// <summary>
    /// Sizes the rows from the items' heights; a row subgrid is measured with auto rows and reports the heights to its
    /// parent. Returns the rows' contributions, and their total height with the gaps.
    /// </summary>
    private List<(int, int, float, float)> SizeGridRows(ref GridPass g, List<Track> rowDefs, out float rowsTotal)
    {
        var items = g.Items;
        int rowCount = g.RowCount;
        float rowGap = g.RowGap;
        var rows = g.Rows = g.MeasuringRows ? MakeTracks(new List<Track>(), null, rowCount) : MakeTracks(rowDefs, g.S.GridAutoRows, rowCount);
        var rowContrib = new List<(int, int, float, float)>(items.Count);
        foreach (var it in items)
        {
            if (g.HasSubgrids && IsSubgrid(it, columns: false) && _subgrids[it.El!].Rows is { Contributions: { } subRows } axis)
            {
                float subGap = it.Style.RowGap == null ? rowGap : GapOf(it.Style.RowGap, 0);
                MapSubgridContributions(subRows, rowContrib, it.Row, axis.Count, SubgridInset(it, false, true, g.W), SubgridInset(it, false, false, g.W), (subGap - rowGap) / 2);
                continue;
            }
            rowContrib.Add((it.Row, it.RowSpan, it.Box!.Height, it.Box.Height));
        }
        bool rowsDefinite = g.DefiniteH.HasValue && !g.MeasuringRows;
        foreach (var r in rows)
        {
            // fr rows only flex when the height is known; otherwise they size to their content.
            if (!rowsDefinite && r.IsFlexible) r.Def = AutoTrack;
        }
        SizeTracks(rows, g.DefiniteH ?? 0, rowsDefinite, rowGap, rowContrib, stretchAuto: false);

        rowsTotal = rowGap * Math.Max(0, rowCount - 1);
        foreach (var r in rows) rowsTotal += r.Base;
        return rowContrib;
    }

    /// <summary>Grows the auto rows to the container's height, or distributes the free space with align-content.</summary>
    private static void AlignGridContent(ref GridPass g, float targetH, float rowsTotal, out float rowBefore, out float rowBetween)
    {
        rowBefore = rowBetween = 0;
        if (targetH > rowsTotal + Eps)
        {
            float free = targetH - rowsTotal;
            if (g.S.AlignContent is BoxAlign.Normal or BoxAlign.Stretch)
            {
                var autos = g.Rows.Where(r => r.Def.Max.Kind == TrackSizeKind.Auto).ToList();
                foreach (var r in autos) r.Base += free / autos.Count;
            }
            else Distribute(g.S.AlignContent, free, g.RowCount, out rowBefore, out rowBetween);
        }
    }

    /// <summary>
    /// Positions the rows. A row that crosses a page boundary stays where it is and its items break across it, as in
    /// Chrome, unless one of them cannot: it does not break inside, or it would leave nothing of its content on this
    /// page. The row then moves to the next page whole.
    /// </summary>
    private void PositionGridRows(ref GridPass g, float rowBefore, float rowBetween)
    {
        var rows = g.Rows;
        var rowY = g.RowY;
        int rowCount = g.RowCount;
        float rowGap = g.RowGap;
        float ry = g.Y + rowBefore;
        // Keep each row with any item that starts in it and spans further rows. The items are gone through once, not
        // once per row: a grid of thousands of items laid out again and again spent seconds here.
        float[]? extents = null;
        bool[]? whole = null;
        if (_paginate)
        {
            extents = new float[rowCount];
            whole = new bool[rowCount];
            for (int r = 0; r < rowCount; r++) extents[r] = rows[r].Base;
            foreach (var it in g.Items)
            {
                int r = it.Row;
                if ((uint)r >= (uint)rowCount) continue;
                whole[r] |= !BreaksInside(it);
                if (it.RowSpan <= 1) continue;
                float span = rowGap * (it.RowSpan - 1);
                for (int k = r; k < Math.Min(rowCount, r + it.RowSpan); k++) span += rows[k].Base;
                extents[r] = Math.Max(extents[r], span);
            }
        }
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) ry += rowGap + rowBetween;
            float h = rows[r].Base;
            if (extents != null && whole != null)
            {
                float extent = extents[r];
                // A row taller than a page breaks wherever it starts.
                if (whole[r] || CrossesPage(ry, ry + extent) && !AtPageTop(ry) && extent <= _grid.Height(PageOf(ry) + 1) && !RowStartsOnPage(ref g, r, ry))
                    ry = FitOnPage(ry, extent);
            }
            rowY[r] = ry;
            ry += h;
        }
        rowY[rowCount] = ry;
    }

    /// <summary>
    /// Whether a grid item can break across pages: it is laid out again where it lands (see <see cref="PlaceOnPages"/>).
    /// Text runs, images, subgrids and boxes that avoid breaks inside them move with their row instead.
    /// </summary>
    private bool BreaksInside(BoxItem it)
    {
        if (it.El == null || it.IsReplaced || it.Box?.Source == null || IsSubgrid(it, columns: true) || IsSubgrid(it, columns: false)) return false;
        var s = it.Style;
        return !s.AvoidBreakInside && !(s.AvoidColumnBreakInside && _grid is ColumnGrid);
    }

    /// <summary>
    /// Whether every item that starts in row <paramref name="r"/>, at <paramref name="y"/>, puts some of its content on
    /// this page. Chrome moves the row to the next page when one would leave only an empty piece of itself here, such
    /// as a paragraph whose orphans do not fit. Each item is laid out on the pages to find out, and thrown away again;
    /// only the outer levels of nested boxes do that, so that the work stays linear in the depth.
    /// </summary>
    private bool RowStartsOnPage(ref GridPass g, int r, float y)
    {
        if (_probingGridRow || !MayRepeatOnPages() || _columnNotes != null) return true;
        int page = PageOf(y);
        foreach (var it in g.Items)
        {
            if (it.Row != r) continue;
            var c = it.Box!;
            var src = c.Source!;
            var e = src.El;
            float mt = e.Style.MarginTop.IsAuto ? 0 : e.Style.MarginTop.Resolve(src.OuterW);
            int start = _ops.Count, list = _currentListIndex, breaks = _forcedBreaks;
            var marker = _marker;
            var pool = _knownBoxes;
            if (src.BorderW is float bw) _widthOverride[e] = bw;
            // A plain block stops as soon as its first content is placed.
            var probe = CanProbe(e) ? new KeepProbe(start, page) { StopsOnceAnswered = true } : null;
            _childProbe = probe;
            _knownBoxes = src.BorderH == null ? c.Known : null;
            _probingGridRow = true;
            _placingOnPages++;
            // The item is placed later from where it was captured, moving its anchors from where that left them, so the
            // anchors the probe moves go back too; otherwise their bookmarks and links pointed where the probe left them.
            var anchors = c.AnchorIds?.ConvertAll(id => Anchors.TryGetValue(id, out float ay) ? ay : float.NaN);
            bool starts;
            try
            {
                LayoutBlockBox(e, g.ColX[it.Col], y + mt, src.OuterW, src.ShrinkToFit);
                starts = probe is not { Stopped: true } && FirstContentPage(start, y) <= page;
            }
            finally
            {
                _placingOnPages--;
                _probingGridRow = false;
                _knownBoxes = pool;
                _childProbe = null;
                if (src.BorderW.HasValue) _widthOverride.Remove(e);
                Truncate(start);
                (_currentListIndex, _forcedBreaks, _marker) = (list, breaks, marker);
                for (int i = 0; anchors != null && i < anchors.Count; i++)
                {
                    if (float.IsNaN(anchors[i])) Anchors.Remove(c.AnchorIds![i]);
                    else Anchors[c.AnchorIds![i]] = anchors[i];
                }
            }
            if (!starts) return false;
        }
        return true;
    }

    /// <summary>Whether the items of a grid row are being laid out on the pages only to see where their content starts.</summary>
    private bool _probingGridRow;

    /// <summary>
    /// Places each item in its grid area with box alignment. An item laid out again across a page boundary may get
    /// taller; the rows below it move down by as much. Items are placed in their order, which for auto-placed items is
    /// row by row, so an item placed earlier in a later row does not move. Returns how far each row moved, or null.
    /// </summary>
    private float[]? PlaceGridItemsInAreas(ref GridPass g)
    {
        var s = g.S;
        var items = g.Items;
        int rowCount = g.RowCount;
        float firstBaseline = float.NaN;
        float[]? rowShift = null;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var st = it.Style;
            int lastRow = Math.Min(rowCount, it.Row + it.RowSpan) - 1;
            float areaX = g.ColX[it.Col], areaW = g.AreaWidth(it);
            // Right to left, the columns run from the right edge of the grid container (of the grid area for a subgrid).
            if (s.Rtl) areaX = g.ColCtx?.Edges != null ? g.ColCtx.Edges[^1] - areaX - areaW : g.X + g.X + g.W - areaX - areaW;
            float shiftTop = rowShift?[it.Row] ?? 0, shiftBottom = rowShift?[lastRow] ?? 0;
            // The height is taken as a difference of row offsets, not of the area's edges: far from the top of the
            // document a float holds few fractional digits, and the edges came out a little apart from the items'
            // heights, so every stretched item was laid out again and the boxes inside could not reuse what they had
            // measured.
            float areaY = g.RowY[it.Row] + shiftTop;
            float areaH = g.RowY[lastRow] - g.RowY[it.Row] + (shiftBottom - shiftTop) + g.Rows[lastRow].Base;

            var align = SelfAlign(it, s.AlignItems, grid: true);
            var box = FitGridItemHeight(ref g, it, i, align, areaY, areaW, areaH, out bool rowSub);
            bool stretchX = g.StretchX[i];

            float outerH = box.Height, outerW = stretchX ? areaW : box.Width;
            float dx = 0, dy = 0;
            if (it.AutoMl && it.AutoMr) dx = (areaW - outerW) / 2;
            else if (it.AutoMl) dx = areaW - outerW;
            else if (!stretchX)
            {
                var js = st.JustifySelf == BoxAlign.Auto ? s.JustifyItems : st.JustifySelf;
                dx = js switch { BoxAlign.End => areaW - outerW, BoxAlign.Center => (areaW - outerW) / 2, _ => 0 };
            }
            if (rowSub) dy = 0;
            else if (it.AutoMt && it.AutoMb) dy = (areaH - outerH) / 2;
            else if (it.AutoMt) dy = areaH - outerH;
            else dy = align switch { BoxAlign.End => areaH - outerH, BoxAlign.Center => (areaH - outerH) / 2, _ => 0 };

            bool subgrid = it.El != null && it.Style.IsGrid && _subgrids.ContainsKey(it.El);
            if (subgrid || rowSub) Place(box, areaX + dx, areaY + dy);
            else
            {
                float grown = PlaceOnPages(box, areaX + dx, areaY + dy) - (areaY + areaH);
                if (grown > Eps)
                {
                    rowShift ??= new float[rowCount + 1];
                    for (int k = lastRow + 1; k <= rowCount; k++) rowShift[k] = Math.Max(rowShift[k], shiftBottom + grown);
                }
            }
            if (float.IsNaN(firstBaseline) && it.Row == 0 && !float.IsNaN(box.Baseline)) firstBaseline = areaY + dy + box.Baseline;
        }
        if (!float.IsNaN(firstBaseline)) _lastBaseline = firstBaseline;
        return rowShift;
    }

    /// <summary>
    /// Lays an item out again at the height its grid area gives it: stretched, a percentage of the area, or at the
    /// parent's row edges for a row subgrid (<paramref name="rowSub"/>). Returns the item's box.
    /// </summary>
    private Captured FitGridItemHeight(ref GridPass g, BoxItem it, int i, BoxAlign align, float areaY, float areaW, float areaH, out bool rowSub)
    {
        var st = it.Style;
        var box = it.Box!;
        rowSub = false;
        if (it.El == null) return box;
        float current = box.Height - it.Mt - it.Mb;
        float? want = null;
        if (g.HasSubgrids && IsSubgrid(it, columns: false) && _subgrids[it.El].Rows is { } axis)
        {
            // A row subgrid is laid out again at the parent's row edges, whatever its height and alignment say.
            rowSub = true;
            var edges = new float[2 * axis.Count];
            for (int k = 0; k < axis.Count; k++)
            {
                edges[2 * k] = g.RowY[it.Row + k] - areaY;
                edges[2 * k + 1] = g.RowY[it.Row + k] + g.Rows[it.Row + k].Base - areaY;
            }
            axis.Edges = edges;
            want = Math.Max(0, areaH - it.Mt - it.Mb);
        }
        else if (st.Height.IsPercent) want = st.Height.Resolve(areaH) + (st.BorderBox ? 0 : VerticalFrame(st, areaW));
        else if (align == BoxAlign.Stretch && st.Height.IsAuto && !it.AutoMt && !it.AutoMb) want = Math.Max(current, areaH - it.Mt - it.Mb);
        if (want is { } hh && (rowSub || Math.Abs(hh - current) > Eps && (NeedsRelayoutWhenStretched(it) || st.Height.IsPercent)))
        {
            float? bw = g.StretchX[i] && !it.IsReplaced ? null : box.Width - it.Ml - it.Mr;
            box = it.Box = CaptureBox(it.El, areaW, shrinkToFit: !g.StretchX[i], borderW: bw, borderH: hh, reuse: true, inner: box.Known);
        }
        return box;
    }

    /// <summary>A row subgrid's bottom inset when its height is not imposed: its margin, border and padding below the last track.</summary>
    private static float SubgridBottomInset(ComputedStyle s, float basis)
        => (s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(basis)) + s.BorderBottom + s.PaddingBottom.Resolve(basis);

    /// <summary>Min-content and max-content widths of a grid container's content box.</summary>
    private (float Min, float Max) GridIntrinsic(Element e)
    {
        var s = e.Style;
        var items = CollectBoxItems(e, 0);
        float gap = GapOf(s.ColumnGap, 0);
        var colDefs = ExpandTracks(s.GridTemplateColumns, 0, gap, out _, out int repCount);
        if (items.Count == 0)
        {
            float fixedSum = 0;
            foreach (var t in colDefs) fixedSum += Math.Max(0, FixedSize(t.Min, 0, false));
            return (fixedSum, fixedSum);
        }
        var areas = s.GridTemplateAreas;
        var colNames = LineNames(s.GridTemplateColumns, repCount, areas, columns: true);
        var rowNames = LineNames(s.GridTemplateRows, 0, areas, columns: false);
        PlaceGridItems(items, s, Math.Max(colDefs.Count, areas?.Columns ?? 0), areas?.Rows ?? 0, colNames, rowNames, out int colCount, out _);
        foreach (var it in items)
            if (IsSubgrid(it, columns: true)) ClampSubgridSpan(it, columns: true, colCount);
        var contributions = new List<(int, int, float, float)>(items.Count);
        var minContrib = new List<(int, int, float, float)>(items.Count);
        ColumnContributions(items, 0, gap, colNames, rowNames, contributions, minContrib, 0);
        float Sum(List<(int, int, float, float)> contrib)
        {
            var tracks = MakeTracks(colDefs, s.GridAutoColumns, colCount);
            SizeTracks(tracks, 0, definite: false, gap, contrib, stretchAuto: false);
            float total = gap * Math.Max(0, colCount - 1);
            foreach (var t in tracks) total += t.Base;
            return total;
        }
        float max = Sum(contributions);
        float min = Sum(minContrib);
        return (Math.Min(min, max), max);
    }
}

using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// CSS grid layout: line-based and automatic placement (row flow, sparse), track sizing with fixed, percentage, fr,
/// auto, min-content, max-content and minmax() tracks, repeat() including auto-fill/auto-fit, gaps and box alignment.
/// Named lines and grid-template-areas are resolved to line numbers. Rows are paginated as units.
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
    private static void ResolveSpan(GridLine start, GridLine end, int explicitCount, out int index, out int span)
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
            else
            {
                span = start.Span > 0 ? start.Span : 1;
                index = Math.Max(0, endIndex - span);
            }
        }
        else if (end.Span > 0) span = end.Span;
        else if (start.Span > 0) span = start.Span;
    }

    /// <summary>
    /// 1-based line numbers by name on one axis: names from the track list, and name-start/name-end lines from
    /// grid-template-areas. Null when there are none.
    /// </summary>
    private static Dictionary<string, List<int>>? LineNames(TrackList? list, int repeatCount, GridAreas? areas, bool columns)
    {
        Dictionary<string, List<int>>? names = null;
        void Add(string name, int line)
        {
            names ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
            if (!names.TryGetValue(name, out var lines)) names[name] = lines = new List<int>(1);
            if (!lines.Contains(line)) lines.Add(line);
        }
        if (list?.LineNames is { } ln)
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

    /// <summary>Places the items in the grid (row-major, sparse auto-placement). Returns the number of columns and rows.</summary>
    private static void PlaceGridItems(List<BoxItem> items, ComputedStyle s, int explicitCols, int explicitRows,
        Dictionary<string, List<int>>? colNames, Dictionary<string, List<int>>? rowNames, out int colCount, out int rowCount)
    {
        colCount = Math.Max(1, explicitCols);
        foreach (var it in items)
        {
            var st = it.Style;
            ResolveSpan(ResolveName(st.GridColumnStart, colNames, true), ResolveName(st.GridColumnEnd, colNames, false), explicitCols, out int c, out int cs);
            ResolveSpan(ResolveName(st.GridRowStart, rowNames, true), ResolveName(st.GridRowEnd, rowNames, false), explicitRows, out int r, out int rs);
            it.Col = c; it.ColSpan = Math.Min(cs, 1000); it.ColFixed = c >= 0;
            it.Row = r; it.RowSpan = Math.Min(rs, 1000); it.RowFixed = r >= 0;
            colCount = Math.Max(colCount, (c >= 0 ? c : 0) + it.ColSpan);
        }
        colCount = Math.Min(colCount, 1000);

        var occupied = new List<bool[]>();
        int cols = colCount;
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
        // 3. Everything else, with a cursor that only moves forward.
        int curRow = 0, curCol = 0;
        foreach (var it in items)
        {
            if (it.RowFixed) continue;
            it.ColSpan = Math.Min(it.ColSpan, cols);
            if (it.ColFixed)
            {
                if (it.Col < curCol) curRow++;
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
        rowCount = Math.Max(explicitRows, occupied.Count);
        foreach (var it in items) rowCount = Math.Max(rowCount, it.Row + it.RowSpan);
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
                    t.Limit = hasLimit[start] ? Math.Max(t.Limit, max) : max;
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

    private float LayoutGrid(Element e, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        var items = CollectBoxItems(e, w);
        float colGap = GapOf(s.ColumnGap, w);
        float rowGap = GapOf(s.RowGap, definiteH ?? 0);

        var colDefs = ExpandTracks(s.GridTemplateColumns, w, colGap, out int repStart, out int repCount);
        var rowDefs = ExpandTracks(s.GridTemplateRows, definiteH ?? 0, rowGap, out _, out int rowRepCount);
        if (items.Count == 0) return y;
        var areas = s.GridTemplateAreas;
        PlaceGridItems(items, s, Math.Max(colDefs.Count, areas?.Columns ?? 0), Math.Max(rowDefs.Count, areas?.Rows ?? 0),
            LineNames(s.GridTemplateColumns, repCount, areas, columns: true), LineNames(s.GridTemplateRows, rowRepCount, areas, columns: false),
            out int colCount, out int rowCount);

        var cols = MakeTracks(colDefs, s.GridAutoColumns, colCount);
        if (s.GridTemplateColumns?.AutoFit == true)
        {
            for (int c = repStart; c < repStart + repCount && c < cols.Length; c++)
                cols[c].Collapsed = !items.Any(it => it.Col <= c && c < it.Col + it.ColSpan);
        }

        // Column sizes from the items' intrinsic widths.
        var contributions = new List<(int, int, float, float)>(items.Count);
        foreach (var it in items)
        {
            var (mn, mx) = ItemIntrinsic(it);
            contributions.Add((it.Col, it.ColSpan, mn + it.Ml + it.Mr, mx + it.Ml + it.Mr));
        }
        SizeTracks(cols, w, definite: true, colGap, contributions, s.JustifyContent is BoxAlign.Normal or BoxAlign.Stretch);

        // Column positions, with justify-content.
        var colX = new float[colCount + 1];
        {
            float used = 0;
            int visible = 0;
            foreach (var t in cols) if (!t.Collapsed) { used += t.Base; visible++; }
            used += colGap * Math.Max(0, visible - 1);
            Distribute(s.JustifyContent, w - used, visible, out float before, out float between);
            float cx = x + before;
            bool first = true;
            for (int c = 0; c < colCount; c++)
            {
                if (cols[c].Collapsed) { colX[c] = cx; continue; }
                if (!first) cx += colGap + between;
                first = false;
                colX[c] = cx;
                cx += cols[c].Base;
            }
            colX[colCount] = cx;
        }
        float AreaWidth(BoxItem it)
        {
            int end = Math.Min(colCount, it.Col + it.ColSpan);
            return Math.Max(0, ColRight(end - 1) - colX[it.Col]);
        }
        float ColRight(int c) => colX[c] + (cols[c].Collapsed ? 0 : cols[c].Base);

        // Lay out every item at its area width to find the row heights.
        var stretchX = new bool[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            float areaW = AreaWidth(it);
            var js = it.Style.JustifySelf == BoxAlign.Auto ? s.JustifyItems : it.Style.JustifySelf;
            if (js is BoxAlign.Normal or BoxAlign.Auto) js = it.IsReplaced ? BoxAlign.Start : BoxAlign.Stretch;
            stretchX[i] = js == BoxAlign.Stretch && !it.AutoMl && !it.AutoMr;
            it.Box = Measure(it, e, areaW, stretchX[i]);
        }

        var rows = MakeTracks(rowDefs, s.GridAutoRows, rowCount);
        var rowContrib = new List<(int, int, float, float)>(items.Count);
        foreach (var it in items) rowContrib.Add((it.Row, it.RowSpan, it.Box!.Height, it.Box.Height));
        bool rowsDefinite = definiteH.HasValue;
        foreach (var r in rows)
        {
            // fr rows only flex when the height is known; otherwise they size to their content.
            if (!rowsDefinite && r.IsFlexible) r.Def = AutoTrack;
        }
        SizeTracks(rows, definiteH ?? 0, rowsDefinite, rowGap, rowContrib, stretchAuto: false);

        float rowsTotal = rowGap * Math.Max(0, rowCount - 1);
        foreach (var r in rows) rowsTotal += r.Base;
        float targetH = definiteH ?? Math.Max(minH, rowsTotal);
        float rowBefore = 0, rowBetween = 0;
        if (targetH > rowsTotal + Eps)
        {
            float free = targetH - rowsTotal;
            if (s.AlignContent is BoxAlign.Normal or BoxAlign.Stretch)
            {
                var autos = rows.Where(r => r.Def.Max.Kind == TrackSizeKind.Auto).ToList();
                foreach (var r in autos) r.Base += free / autos.Count;
            }
            else Distribute(s.AlignContent, free, rowCount, out rowBefore, out rowBetween);
        }

        // Row positions, paginating whole rows.
        var rowY = new float[rowCount + 1];
        float ry = y + rowBefore;
        for (int r = 0; r < rowCount; r++)
        {
            if (r > 0) ry += rowGap + rowBetween;
            float h = rows[r].Base;
            if (_paginate)
            {
                // Keep the row with any item that starts in it and spans further rows.
                float extent = h;
                foreach (var it in items)
                {
                    if (it.Row != r || it.RowSpan <= 1) continue;
                    float span = rowGap * (it.RowSpan - 1);
                    for (int k = r; k < Math.Min(rowCount, r + it.RowSpan); k++) span += rows[k].Base;
                    extent = Math.Max(extent, span);
                }
                ry = FitOnPage(ry, extent);
            }
            rowY[r] = ry;
            ry += h;
        }
        rowY[rowCount] = ry;

        // Final placement with box alignment.
        float firstBaseline = float.NaN;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var st = it.Style;
            int lastRow = Math.Min(rowCount, it.Row + it.RowSpan) - 1;
            float areaX = colX[it.Col], areaW = AreaWidth(it);
            float areaY = rowY[it.Row], areaH = rowY[lastRow] + rows[lastRow].Base - areaY;
            var box = it.Box!;

            var align = SelfAlign(it, s.AlignItems);
            if (it.El != null)
            {
                float current = box.Height - it.Mt - it.Mb;
                float? want = null;
                if (st.Height.IsPercent) want = st.Height.Resolve(areaH) + (st.BorderBox ? 0 : VerticalFrame(st, areaW));
                else if (align == BoxAlign.Stretch && st.Height.IsAuto && !it.AutoMt && !it.AutoMb) want = Math.Max(current, areaH - it.Mt - it.Mb);
                if (want is { } hh && Math.Abs(hh - current) > Eps && (NeedsRelayoutWhenStretched(it) || st.Height.IsPercent))
                {
                    float? bw = stretchX[i] ? null : box.Width - it.Ml - it.Mr;
                    box = it.Box = CaptureBox(it.El, areaW, shrinkToFit: !stretchX[i], borderW: bw, borderH: hh);
                }
            }

            float outerH = box.Height, outerW = stretchX[i] ? areaW : box.Width;
            float dx = 0, dy = 0;
            if (it.AutoMl && it.AutoMr) dx = (areaW - outerW) / 2;
            else if (it.AutoMl) dx = areaW - outerW;
            else if (!stretchX[i])
            {
                var js = st.JustifySelf == BoxAlign.Auto ? s.JustifyItems : st.JustifySelf;
                dx = js switch { BoxAlign.End => areaW - outerW, BoxAlign.Center => (areaW - outerW) / 2, _ => 0 };
            }
            if (it.AutoMt && it.AutoMb) dy = (areaH - outerH) / 2;
            else if (it.AutoMt) dy = areaH - outerH;
            else dy = align switch { BoxAlign.End => areaH - outerH, BoxAlign.Center => (areaH - outerH) / 2, _ => 0 };

            Place(box, areaX + dx, areaY + dy);
            if (float.IsNaN(firstBaseline) && it.Row == 0 && !float.IsNaN(box.Baseline)) firstBaseline = areaY + dy + box.Baseline;
        }
        _lastBoxRight = Math.Max(_lastBoxRight, colX[colCount]);
        if (!float.IsNaN(firstBaseline)) _lastBaseline = firstBaseline;
        return Math.Max(rowY[rowCount], y);
    }

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
        PlaceGridItems(items, s, Math.Max(colDefs.Count, areas?.Columns ?? 0), areas?.Rows ?? 0,
            LineNames(s.GridTemplateColumns, repCount, areas, columns: true), LineNames(s.GridTemplateRows, 0, areas, columns: false),
            out int colCount, out _);
        var contributions = new List<(int, int, float, float)>(items.Count);
        var minContrib = new List<(int, int, float, float)>(items.Count);
        foreach (var it in items)
        {
            var (mn, mx) = ItemIntrinsic(it);
            contributions.Add((it.Col, it.ColSpan, mn + it.Ml + it.Mr, mx + it.Ml + it.Mr));
            minContrib.Add((it.Col, it.ColSpan, mn + it.Ml + it.Mr, mn + it.Ml + it.Mr));
        }
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

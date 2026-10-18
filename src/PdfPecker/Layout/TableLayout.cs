using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>Automatic table layout (column widths from cell content), row pagination, intrinsic sizes.</summary>
internal sealed partial class LayoutEngine
{
    private sealed class CellModel
    {
        public Element Td = null!;
        public int Row, Col, ColSpan = 1, RowSpan = 1;
        public float Min, Max;          // border-box intrinsic widths
        public float FixedWidth = -1;   // specified absolute border-box width
        public float PercentWidth = -1;

        // Per layout pass.
        public float Top, NeededH, ContentH; // ContentH: what the content needs, without the cell's own height
        public int OpsStart, OpsEnd;
        public GroupOp? Group;
        public float X, W;
    }

    private sealed class RowModel
    {
        public Element Tr = null!;
        public readonly List<CellModel> Cells = new();     // cells starting in this row
        public readonly List<CellModel> Ending = new();    // cells (any start row) ending in this row
    }

    private sealed class TableModel
    {
        public readonly List<Element> Captions = new();
        public readonly List<RowModel> Rows = new();
        public int ColCount;
        public int HeadCount; // leading rows that come from <thead>
        public bool Rtl;      // direction: rtl, so the first column is on the right
        public float[] ColMin = Array.Empty<float>(), ColMax = Array.Empty<float>();
        public float[] ColFixed = Array.Empty<float>(), ColPct = Array.Empty<float>();
        public float[] ColSpecFixed = Array.Empty<float>(), ColSpecPct = Array.Empty<float>(); // from <col> only
        public float[]? RowExtra; // per layout pass: height each row gets on top of its content, to fill the table's height
        public bool DefiniteHeight; // per layout pass: the table's height is a length or a percentage that resolves
        public CellModel?[,] Grid = new CellModel?[0, 0]; // the cell covering each row and column
        public ComputedStyle TableStyle = null!;
    }

    private readonly Dictionary<Element, TableModel> _tables = new();

    /// <summary>
    /// How many layouts of table rows are in progress whose drawing is thrown away and only their heights are used:
    /// the rows a table with a height measures before sharing that height out, and the first layout of a row whose
    /// cells have percentage heights inside.
    /// </summary>
    private int _measuringRows;

    /// <summary>
    /// The keep-with-next probe of the table whose rows are being laid out, until its first content is placed (see
    /// <see cref="CanProbe"/>); null otherwise, and inside the cells, whose tables take a probe of their own.
    /// </summary>
    private KeepProbe? _rowProbe;

    private TableModel GetTableModel(Element table)
    {
        if (_tables.TryGetValue(table, out var m)) return m;
        m = new TableModel();
        var head = new List<Element>();
        var body = new List<Element>();
        var foot = new List<Element>();
        var cols = new List<Element>();
        CollectTableParts(table, m, head, body, foot, cols);
        m.HeadCount = head.Count;
        var trs = new List<Element>(head.Count + body.Count + foot.Count);
        trs.AddRange(head);
        trs.AddRange(body);
        trs.AddRange(foot);

        var occupied = new List<List<bool>>();
        bool IsOccupied(int r, int c) => r < occupied.Count && c < occupied[r].Count && occupied[r][c];
        void Occupy(int r, int c)
        {
            while (occupied.Count <= r) occupied.Add(new List<bool>());
            var row = occupied[r];
            while (row.Count <= c) row.Add(false);
            row[c] = true;
        }

        for (int r = 0; r < trs.Count; r++) m.Rows.Add(new RowModel { Tr = trs[r] });
        for (int r = 0; r < trs.Count; r++)
        {
            int col = 0;
            foreach (var child in trs[r].Children)
            {
                if (child is not Element td || td.Style == null || td.Style.Display != Display.TableCell) continue;
                while (IsOccupied(r, col)) col++;
                int colspan = Math.Clamp(ParseInt(td.GetAttribute("colspan"), 1), 1, 1000);
                int rowspan = ParseInt(td.GetAttribute("rowspan"), 1);
                if (rowspan <= 0 || r + rowspan > trs.Count) rowspan = trs.Count - r;
                var cell = new CellModel { Td = td, Row = r, Col = col, ColSpan = colspan, RowSpan = Math.Max(1, rowspan) };
                for (int rr = r; rr < r + cell.RowSpan; rr++)
                    for (int cc = col; cc < col + colspan; cc++) Occupy(rr, cc);
                m.Rows[r].Cells.Add(cell);
                m.Rows[r + cell.RowSpan - 1].Ending.Add(cell);
                col += colspan;
                if (col > m.ColCount) m.ColCount = col;
            }
        }

        m.TableStyle = table.Style;
        m.Grid = new CellModel?[m.Rows.Count, m.ColCount];
        foreach (var row in m.Rows)
            foreach (var cell in row.Cells)
                for (int rr = cell.Row; rr < cell.Row + cell.RowSpan && rr < m.Rows.Count; rr++)
                    for (int cc = cell.Col; cc < cell.Col + cell.ColSpan && cc < m.ColCount; cc++) m.Grid[rr, cc] = cell;

        int n = m.ColCount;
        m.ColMin = new float[n];
        m.ColMax = new float[n];
        m.ColFixed = new float[n];
        m.ColPct = new float[n];
        for (int c = 0; c < n; c++) { m.ColFixed[c] = -1; m.ColPct[c] = -1; }

        // <col width="..."> hints.
        int colIndex = 0;
        foreach (var col in cols)
        {
            int span = Math.Max(1, ParseInt(col.GetAttribute("span"), 1));
            var cs = col.Style;
            for (int k = 0; k < span && colIndex < n; k++, colIndex++)
            {
                if (cs.Width.Unit == LengthUnit.Percent) m.ColPct[colIndex] = cs.Width.Value;
                else if (!cs.Width.IsAuto && cs.Width.Unit != LengthUnit.Calc) m.ColFixed[colIndex] = cs.Width.Value;
            }
        }

        m.ColSpecFixed = (float[])m.ColFixed.Clone();
        m.ColSpecPct = (float[])m.ColPct.Clone();

        float hs = table.Style.BorderCollapse ? 0 : table.Style.BorderSpacingH;
        var spanning = new List<CellModel>();
        foreach (var row in m.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var cs = cell.Td.Style;
                var (mn, mx) = ContentIntrinsic(cell.Td);
                // With collapsed borders a cell owns half of each border.
                float borders = (cs.BorderLeft + cs.BorderRight) * (table.Style.BorderCollapse ? 0.5f : 1f);
                float frame = cs.PaddingLeft.Resolve(0) + cs.PaddingRight.Resolve(0) + borders;
                cell.Min = mn + frame;
                cell.Max = Math.Max(mx + frame, cell.Min);
                // A width mixing a percentage with other units, such as calc(50% - 10px), is auto, as in Chrome.
                if (cs.Width.Unit == LengthUnit.Percent) cell.PercentWidth = cs.Width.Value;
                else if (!cs.Width.IsAuto && cs.Width.Unit != LengthUnit.Calc)
                {
                    cell.FixedWidth = cs.Width.Value + (cs.BorderBox ? 0 : frame);
                    cell.Max = Math.Max(cell.Min, cell.FixedWidth);
                }
                if (cell.ColSpan == 1)
                {
                    int c = cell.Col;
                    m.ColMin[c] = Math.Max(m.ColMin[c], cell.Min);
                    m.ColMax[c] = Math.Max(m.ColMax[c], cell.Max);
                    if (cell.FixedWidth >= 0) m.ColFixed[c] = Math.Max(m.ColFixed[c], cell.FixedWidth);
                    if (cell.PercentWidth >= 0) m.ColPct[c] = Math.Max(m.ColPct[c], cell.PercentWidth);
                }
                else spanning.Add(cell);
            }
        }
        foreach (var cell in spanning)
        {
            float sumMin = (cell.ColSpan - 1) * hs, sumMax = sumMin;
            for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++) { sumMin += m.ColMin[c]; sumMax += m.ColMax[c]; }
            if (cell.Min > sumMin)
            {
                float add = (cell.Min - sumMin) / cell.ColSpan;
                for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++) m.ColMin[c] += add;
            }
            if (cell.Max > sumMax)
            {
                float add = (cell.Max - sumMax) / cell.ColSpan;
                for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++) m.ColMax[c] += add;
            }
        }
        for (int c = 0; c < n; c++)
        {
            if (m.ColFixed[c] >= 0) m.ColMax[c] = Math.Max(m.ColMin[c], m.ColFixed[c]);
            if (m.ColMax[c] < m.ColMin[c]) m.ColMax[c] = m.ColMin[c];
        }

        _tables[table] = m;
        return m;
    }

    private static void CollectTableParts(Element parent, TableModel m, List<Element> head, List<Element> body, List<Element> foot, List<Element> cols)
    {
        foreach (var child in parent.Children)
        {
            if (child is not Element e || e.Style == null) continue;
            switch (e.Style.Display)
            {
                case Display.TableCaption: m.Captions.Add(e); break;
                case Display.TableRow: body.Add(e); break;
                case Display.TableHeaderGroup: AddRows(e, head); break;
                case Display.TableFooterGroup: AddRows(e, foot); break;
                case Display.TableRowGroup: AddRows(e, body); break;
                case Display.TableColumn: cols.Add(e); break;
                case Display.TableColumnGroup:
                {
                    bool any = false;
                    foreach (var cc in e.Children)
                        if (cc is Element ce && ce.Style?.Display == Display.TableColumn) { cols.Add(ce); any = true; }
                    if (!any) cols.Add(e);
                    break;
                }
                case Display.None: break;
                default:
                    // Wrappers such as <form> around rows: look inside.
                    CollectTableParts(e, m, head, body, foot, cols);
                    break;
            }
        }

        static void AddRows(Element group, List<Element> rows)
        {
            foreach (var c in group.Children)
                if (c is Element tr && tr.Style?.Display == Display.TableRow) rows.Add(tr);
        }
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s, out int v) ? v : fallback;

    /// <summary>Distributes the available width over the columns, CSS auto table layout style.</summary>
    private static float[] AllocateColumns(TableModel m, float avail, bool widthSpecified)
    {
        int n = m.ColCount;
        var w = new float[n];
        float pctTotal = 0;
        for (int c = 0; c < n; c++)
        {
            if (m.ColPct[c] < 0) continue;
            w[c] = Math.Max(m.ColMin[c], m.ColPct[c] * Math.Max(avail, 0) / 100f);
            pctTotal += w[c];
        }
        float remaining = avail - pctTotal;
        float sumMin = 0, sumMax = 0, sumMaxFlexible = 0;
        int flexible = 0, autoCount = 0;
        for (int c = 0; c < n; c++)
        {
            if (m.ColPct[c] >= 0) continue;
            autoCount++;
            sumMin += m.ColMin[c];
            sumMax += m.ColMax[c];
            if (m.ColFixed[c] < 0) { sumMaxFlexible += m.ColMax[c]; flexible++; }
        }
        if (autoCount == 0) return w;

        float target = widthSpecified || sumMax > remaining ? remaining : sumMax;
        if (target >= sumMax)
        {
            float extra = target - sumMax;
            for (int c = 0; c < n; c++)
            {
                if (m.ColPct[c] >= 0) continue;
                w[c] = m.ColMax[c];
                if (extra <= 0) continue;
                if (flexible > 0)
                {
                    if (m.ColFixed[c] >= 0) continue;
                    w[c] += sumMaxFlexible > 0 ? extra * m.ColMax[c] / sumMaxFlexible : extra / flexible;
                }
                else
                {
                    w[c] += sumMax > 0 ? extra * m.ColMax[c] / sumMax : extra / autoCount;
                }
            }
        }
        else if (target >= sumMin)
        {
            float range = sumMax - sumMin;
            for (int c = 0; c < n; c++)
            {
                if (m.ColPct[c] >= 0) continue;
                w[c] = m.ColMin[c] + (range > 0 ? (m.ColMax[c] - m.ColMin[c]) * (target - sumMin) / range : 0);
            }
        }
        else
        {
            float scale = sumMin > 0 ? Math.Max(target, 0) / sumMin : 0;
            for (int c = 0; c < n; c++)
                if (m.ColPct[c] < 0) w[c] = m.ColMin[c] * scale;
        }
        return w;
    }

    /// <summary>
    /// Column widths for table-layout: fixed (CSS 2.1 §17.5.2.1): from the columns' and the first row's widths only,
    /// whatever the content. Columns without a width share what is left equally; when every column has a width, the
    /// rest is shared in proportion. Content that does not fit overflows its cell.
    /// </summary>
    private static float[] FixedColumns(TableModel m, float avail)
    {
        int n = m.ColCount;
        var w = new float[n];
        var set = new bool[n];
        avail = Math.Max(avail, 0);
        // <col> widths first, then the cells of the first row.
        for (int c = 0; c < n; c++)
        {
            if (m.ColSpecPct[c] >= 0) { w[c] = m.ColSpecPct[c] * avail / 100f; set[c] = true; }
            else if (m.ColSpecFixed[c] >= 0) { w[c] = m.ColSpecFixed[c]; set[c] = true; }
        }
        if (m.Rows.Count > 0)
        {
            foreach (var cell in m.Rows[0].Cells)
            {
                float cw = cell.PercentWidth >= 0 ? cell.PercentWidth * avail / 100f : cell.FixedWidth;
                if (cw < 0) continue;
                int first = cell.Col, end = Math.Min(n, cell.Col + cell.ColSpan);
                bool free = true;
                for (int c = first; c < end; c++) free &= !set[c];
                if (!free) continue;
                for (int c = first; c < end; c++) { w[c] = cw / (end - first); set[c] = true; }
            }
        }
        float used = 0;
        int unset = 0;
        for (int c = 0; c < n; c++) { if (set[c]) used += w[c]; else unset++; }
        float rest = avail - used;
        if (unset > 0)
        {
            for (int c = 0; c < n; c++) if (!set[c]) w[c] = Math.Max(0, rest / unset);
        }
        else if (rest > 0 && n > 0)
        {
            for (int c = 0; c < n; c++) w[c] += used > 0 ? rest * w[c] / used : rest / n;
        }
        return w;
    }

    /// <summary>The columns of a table on one kind of page: their left edges (and the right end) and widths.</summary>
    private sealed record TableColumns(float[] ColX, float[] Widths);

    private float LayoutTable(Element table, float x, float y, float availW, PageBox? pages = null)
    {
        var s = table.Style;
        var probe = _childProbe;
        _childProbe = null;
        var outerProbe = _rowProbe;
        _rowProbe = null;
        if (table.Id != null) SetAnchor(table.Id, y);
        if (table.LayoutKey != null) SetAnchor(table.LayoutKey, y);
        var m = GetTableModel(table);
        bool collapse = s.BorderCollapse;
        float hs = collapse ? 0 : s.BorderSpacingH, vs = collapse ? 0 : s.BorderSpacingV;
        var (bx, boxW, frameT, frameB, frameH, colX, widths) = TableGeometry(table, m, x, availW);
        // The table's own percentage height resolves against its containing block; what is inside it (captions, rows
        // and cells) does not see that block.
        float? cbH = HeightBasis(table);
        var outerCb = _cbHeight;
        _cbHeight = null;
        m.Rtl = s.Rtl;

        // On pages of different widths, as in Chrome, the table is sized again for each page and its rows take the
        // columns of the page they are on.
        TableColumns[]? pageColumns = null;
        PageBox? tableBox = null;
        if (pages != null)
        {
            pageColumns = new TableColumns[3];
            tableBox = new PageBox();
            for (int k = 0; k < 3; k++)
            {
                var g = TableGeometry(table, m, pages.X[k], pages.W[k]);
                pageColumns[k] = new TableColumns(g.ColX, g.Widths);
                tableBox.BX[k] = tableBox.X[k] = g.Bx;
                tableBox.BW[k] = tableBox.W[k] = g.BoxW;
                tableBox.AW[k] = pages.W[k];
            }
            if (!tableBox.Varies) pageColumns = null;
        }

        foreach (var caption in m.Captions)
        {
            float cmt = caption.Style.MarginTop.Resolve(boxW);
            y = LayoutBlockBox(caption, bx, y + cmt, boxW, shrinkToFit: false) + caption.Style.MarginBottom.Resolve(boxW);
        }

        float tableTop = y;
        var tableGroup = new GroupOp();
        _ops.Add(tableGroup);
        float cy = y + frameT + (m.Rows.Count > 0 ? vs : 0);

        m.RowExtra = null;
        float? tableH = TableHeight(s, boxW, frameH, frameT + frameB, cbH);
        m.DefiniteHeight = HeightOf(s.Height, cbH) != null;
        // Inside rows that are only measured, a table is as tall without sharing out its height over its rows (it is
        // at least that height either way), so it is not laid out twice for that: with tables nested in each other,
        // each level would double the work.
        if (tableH is float th && m.Rows.Count > 0 && _measuringRows == 0)
            m.RowExtra = RowExtras(m, th - frameT - frameB - vs, colX, widths, hs, vs, collapse);
        // A caption comes before the rows, and the probe is not handed to it. In columns that are being balanced, the
        // breaks of the rows that the probe would skip count for the balancing even when the layout is thrown away.
        _rowProbe = m.Captions.Count == 0 && _columnNotes == null ? probe : null;
        for (int r = 0; r < m.Rows.Count; r++)
        {
            cy = LayoutRow(m, r, cy, colX, widths, hs, vs, collapse, allowMove: true, pageColumns) + vs;
            // The first content is past the chain's page: the chain moves and lays the table out again, so the rest of
            // this layout would be thrown away.
            if (probe is { Stopped: true }) break;
        }
        _rowProbe = outerProbe;
        m.RowExtra = null;
        m.DefiniteHeight = false;
        _cbHeight = outerCb;

        float tableBottom = cy + frameB;
        if (tableH is float minBottom && tableBottom - tableTop < minBottom) tableBottom = tableTop + minBottom;
        int sides = collapse && m.Rows.Count > 0 ? TableSides(m) : BoxPainter.AllSides;
        if (pageColumns != null && !SameOnPages(tableBox!, tableTop, tableBottom))
            PaintOnPages(tableGroup, s, tableBox!, tableTop, tableBottom, sides, collapse);
        else BoxPainter.Paint(tableGroup, s, bx, tableTop, boxW, tableBottom - tableTop, _images, availW, sides, collapsedCell: collapse);
        _lastBoxRight = bx + boxW;
        _lastBoxX = bx;
        _lastBoxTop = tableTop;
        _lastBoxW = boxW;
        _lastBoxH = tableBottom - tableTop;
        return tableBottom;
    }

    /// <summary>
    /// A table's border box and columns in a containing block <paramref name="availW"/> wide at <paramref name="x"/>,
    /// with the top and bottom of its frame (border and padding, or half the collapsed borders).
    /// </summary>
    private (float Bx, float BoxW, float FrameT, float FrameB, float FrameH, float[] ColX, float[] Widths) TableGeometry(Element table, TableModel m, float x, float availW)
    {
        var s = table.Style;
        bool collapse = s.BorderCollapse;
        float hs = collapse ? 0 : s.BorderSpacingH;
        float ml = s.MarginLeft.Resolve(availW), mr = s.MarginRight.Resolve(availW);
        float frameL, frameR, frameT, frameB;
        if (collapse)
        {
            // The table's half borders are those of the collapsed outer borders: the widest of the table's own border
            // and the borders of the cells along that edge (CSS 2.1 §17.6.2), as Chrome lays them out.
            float t = s.BorderTop, b = s.BorderBottom, l = s.BorderLeft, r = s.BorderRight;
            if (m.Rows.Count > 0)
            {
                foreach (var c in m.Rows[0].Cells) t = Math.Max(t, c.Td.Style.BorderTop);
                foreach (var c in m.Rows[^1].Ending) b = Math.Max(b, c.Td.Style.BorderBottom);
                foreach (var c in m.Rows[0].Cells)
                {
                    if (c.Col == 0) l = Math.Max(l, c.Td.Style.BorderLeft);
                    if (c.Col + c.ColSpan == m.ColCount) r = Math.Max(r, c.Td.Style.BorderRight);
                }
            }
            frameL = l / 2; frameR = r / 2; frameT = t / 2; frameB = b / 2;
        }
        else
        {
            frameL = s.BorderLeft + s.PaddingLeft.Resolve(availW);
            frameR = s.BorderRight + s.PaddingRight.Resolve(availW);
            frameT = s.BorderTop + s.PaddingTop.Resolve(availW);
            frameB = s.BorderBottom + s.PaddingBottom.Resolve(availW);
        }
        int n = m.ColCount;
        float gridSpace = n > 0 ? (n + 1) * hs : 0; // an empty table has no border spacing, as in Chrome
        bool specified = !s.Width.IsAuto;
        float frameH = frameL + frameR, frameV = frameT + frameB;
        // aspect-ratio and an auto width: a fixed height (or a min-height wider than the content) sets the width as a
        // specified one would; a max-height only narrows the room the columns get. Neither makes the table narrower
        // than its columns' min-content widths, which the intrinsic widths already include.
        float ratioW = -1, ratioMax = float.PositiveInfinity;
        if (!specified && s.AspectRatio > 0)
        {
            var (tableMin, tableMax) = Intrinsic(table);
            if (ClampedHeight(s) is float h) ratioW = Math.Max(RatioWidth(s, h, frameH, frameV) + frameH, tableMin);
            else
            {
                var (tmin, tmax) = TransferredWidthLimits(s, frameH, frameV);
                ratioMax = Math.Max(tmax + frameH, tableMin);
                if (tmin > 0 && tableMax <= tmin + frameH + 0.01f) ratioW = tmin + frameH;
            }
        }
        float avail;
        if (specified || ratioW >= 0)
        {
            float tw = ratioW;
            if (specified)
            {
                tw = s.Width.Resolve(availW);
                if (!s.BorderBox) tw += frameH;
            }
            avail = tw - frameH - gridSpace;
            specified = true;
        }
        else avail = Math.Min(availW - ml - mr, ratioMax) - frameH - gridSpace;
        if (!s.MaxWidth.IsAuto) avail = Math.Min(avail, s.MaxWidth.Resolve(availW) - frameL - frameR - gridSpace);

        var widths = specified && s.TableLayoutFixed ? FixedColumns(m, avail) : AllocateColumns(m, avail, specified);
        float contentW = gridSpace;
        foreach (var cw in widths) contentW += cw;
        float boxW = contentW + frameL + frameR;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) ml = Math.Max(0, (availW - boxW) / 2);
        else if (s.MarginLeft.IsAuto) ml = Math.Max(0, availW - boxW - mr);
        else if (table.Parent?.Style is { Rtl: true } && !s.Floated && !s.OutOfFlow) ml = Math.Max(0, availW - boxW - mr);
        float bx = x + ml;

        var colX = new float[n + 1];
        float cxp = bx + frameL + hs;
        for (int c = 0; c < n; c++) { colX[c] = cxp; cxp += widths[c] + hs; }
        colX[n] = cxp;
        return (bx, boxW, frameT, frameB, frameH, colX, widths);
    }

    /// <summary>
    /// The border-box height a table is at least as tall as: its height, or the one aspect-ratio gives its width, limited
    /// by min-height and max-height; null when nothing sets one. Rows taller than that still make the table grow.
    /// </summary>
    private static float? TableHeight(ComputedStyle s, float boxW, float frameH, float frameV, float? cbH)
    {
        float extra = s.BorderBox ? 0 : frameV;
        float? h = null;
        if (HeightOf(s.Height, cbH) is float specified) h = specified + extra;
        else if (s.AspectRatio > 0)
        {
            // Chrome takes the table's content width for the width the ratio divides, whatever box-sizing says, and
            // applies the frame on top only when the ratio sizes the content box. This matches its output.
            float c = Math.Max(0, boxW - frameH);
            h = RatioOnBorderBox(s) ? c / s.AspectRatio : Math.Max(0, c - frameH) / s.AspectRatio + frameV;
        }
        if (h != null && HeightOf(s.MaxHeight, cbH) is float max) h = Math.Min(h.Value, max + extra);
        if (MinHeightOf(s, cbH) is float min && min > 0) h = Math.Max(h ?? 0, min + extra);
        return h;
    }

    /// <summary>
    /// When a table's height is more than its rows need, shares the difference out over the rows in proportion to their
    /// heights, as Chrome does (CSS 2.1 §17.5.3). Returns null when the rows already fill the table.
    /// </summary>
    private float[]? RowExtras(TableModel m, float available, float[] colX, float[] widths, float hs, float vs, bool collapse)
    {
        var heights = new float[m.Rows.Count];
        var measured = Capture(() =>
        {
            _measuringRows++;
            float top = 0;
            for (int r = 0; r < m.Rows.Count; r++)
            {
                float bottom = LayoutRow(m, r, top, colX, widths, hs, vs, collapse, allowMove: false);
                heights[r] = bottom - top;
                top = bottom + vs;
            }
            _measuringRows--;
            return top;
        });
        float extra = available - measured.Height;
        if (extra <= Eps) return null;
        float total = 0;
        foreach (var h in heights) total += h;
        var extras = new float[heights.Length];
        for (int r = 0; r < heights.Length; r++) extras[r] = total > Eps ? extra * heights[r] / total : extra / heights.Length;
        return extras;
    }

    /// <summary>Lays out one table row at <paramref name="rowTop"/> and returns its bottom. A row that would straddle a
    /// page boundary moves to the next page, where the table's header rows are repeated first.</summary>
    private float LayoutRow(TableModel m, int r, float rowTop, float[] colX, float[] widths, float hs, float vs, bool collapse, bool allowMove,
        TableColumns[]? pageColumns = null)
    {
        var row = m.Rows[r];
        int n = m.ColCount;
        int rowStartOps = _ops.Count;
        float rowBottom = 0;
        GroupOp rowGroup = null!;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (pageColumns != null) (colX, widths) = pageColumns[PageBox.Kind(PageOf(rowTop))];
            rowGroup = new GroupOp();
            _ops.Add(rowGroup);
            // As in Chrome, percentage heights in a cell whose height is definite (it has a fixed height, or the table
            // has one) resolve against the cell's final height, which is only known once the row is: the row is laid
            // out once to find it, with those percentages as auto, and then again. The first layout only measures.
            bool percent = _measuringRows == 0 && PercentCells(m, row);
            // The first layout of such a row is thrown away, so the probe stops here and the table is laid out in full.
            if (percent) _rowProbe = null;
            if (percent) _measuringRows++;
            foreach (var cell in row.Cells)
            {
                var probe = _rowProbe;
                LayoutCell(cell, rowTop, colX, widths, hs, collapse, m.Rtl, probe: probe);
                if (probe == null) continue;
                // Once the first content is placed, the probe has its answer. The cell's flow may have kept looking
                // while a chain in it could still move, but the cell is complete now. Past the chain's page, the table
                // stops, and so does the row, whose cells are not finished.
                if (!probe.Stopped && FirstContent(probe) is (_, var fy))
                {
                    probe.Stopped = PageOf(fy) > probe.KeepPage;
                    _rowProbe = null;
                }
                if (probe.Stopped) return rowTop;
            }
            if (percent) _measuringRows--;
            float rowH = RowHeight(m, r, shareOfTable: true);
            if (percent)
            {
                Truncate(rowStartOps);
                rowGroup = new GroupOp();
                _ops.Add(rowGroup);
                foreach (var cell in row.Cells)
                    LayoutCell(cell, rowTop, colX, widths, hs, collapse, m.Rtl, cell.RowSpan == 1 && IsDefiniteCell(m, cell) ? rowH : null);
                // Content that the percentages made taller than the row (with margins, say) still makes it grow.
                rowH = Math.Max(rowH, RowHeight(m, r, shareOfTable: false));
            }
            rowBottom = rowTop + rowH;
            foreach (var cell in row.Ending)
                rowBottom = Math.Max(rowBottom, cell.Top + cell.NeededH);

            bool move = allowMove && attempt == 0 && CrossesPage(rowTop, rowBottom) && rowBottom - rowTop <= _grid.Height(PageOf(rowTop) + 1) && !AtPageTop(rowTop);
            // A row that starts a column because the row before it ended right at the break gets the header too.
            bool atBreak = !move && allowMove && attempt == 0 && _grid is ColumnGrid && r > m.HeadCount && m.HeadCount > 0
                           && PageOf(rowTop) > 0 && AtPageTop(rowTop);
            if (move || atBreak)
            {
                // A row without content moved: the headers repeated before it are content that may be dropped again,
                // so the table is laid out in full.
                _rowProbe = null;
                Truncate(rowStartOps);
                if (move)
                {
                    _columnNotes?.Break(PageOf(rowTop), rowTop, rowBottom - FlowEnd(PageOf(rowTop)));
                    rowTop = NextPage(rowTop);
                }
                else _columnNotes?.Exact(PageOf(rowTop), rowTop, rowBottom - rowTop, _grid);
                if (r >= m.HeadCount)
                {
                    float headStart = rowTop;
                    int headOps = _ops.Count;
                    for (int h = 0; h < m.HeadCount; h++)
                        rowTop = LayoutRow(m, h, rowTop, colX, widths, hs, vs, collapse, allowMove: false, pageColumns) + vs;
                    // The structure has the header once: its repeats are artifacts.
                    if (Tags != null) Untag(_ops, headOps);
                    if (rowTop - headStart > _grid.Height(PageOf(headStart)) / 2)
                    {
                        // Headers too tall to repeat usefully: drop them again.
                        Truncate(rowStartOps);
                        rowTop = headStart;
                    }
                }
                continue;
            }
            break;
        }

        foreach (var cell in row.Ending) FinishCell(m, cell, rowBottom, collapse);

        var rs = row.Tr.Style;
        if (CssColor.IsVisible(rs.BackgroundColor) && row.Cells.Count + row.Ending.Count > 0)
            rowGroup.Ops.Add(new RectOp(colX[0], rowTop, colX[n] - hs - colX[0], rowBottom - rowTop, rs.BackgroundColor));
        return rowBottom;
    }

    /// <summary>
    /// The height of row <paramref name="r"/> from its cells laid out last and its own height, and with
    /// <paramref name="shareOfTable"/> the part of the table's height it gets on top.
    /// </summary>
    private static float RowHeight(TableModel m, int r, bool shareOfTable)
    {
        var row = m.Rows[r];
        float rowH = 0;
        foreach (var cell in row.Cells)
            if (cell.RowSpan == 1) rowH = Math.Max(rowH, cell.NeededH);
        var trs = row.Tr.Style;
        if (!trs.Height.IsAuto && !trs.Height.IsPercent) rowH = Math.Max(rowH, trs.Height.Value);
        if (shareOfTable && m.RowExtra != null) rowH = Math.Max(rowH, rowH + m.RowExtra[r]);
        return rowH;
    }

    /// <summary>
    /// Whether a cell of the row that spans only it is definite, its height fixed or the table's, and has children with
    /// percentage heights, which then resolve against the cell's height (a row's height alone does not make it definite).
    /// </summary>
    private static bool PercentCells(TableModel m, RowModel row)
    {
        foreach (var cell in row.Cells)
            if (cell.RowSpan == 1 && IsDefiniteCell(m, cell) && HasPercentHeightChild(cell.Td)) return true;
        return false;
    }

    private static bool IsDefiniteCell(TableModel m, CellModel cell) => m.DefiniteHeight || HeightOf(cell.Td.Style.Height, null) != null;

    /// <summary>
    /// Lays out a cell's content at <paramref name="rowTop"/>. <paramref name="rowHeight"/>, the height of the row once
    /// known, is given for a definite cell: the percentage heights of its content resolve against it.
    /// </summary>
    private void LayoutCell(CellModel cell, float rowTop, float[] colX, float[] widths, float hs, bool collapse, bool rtl, float? rowHeight = null,
        KeepProbe? probe = null)
    {
        var td = cell.Td;
        var cs = td.Style;
        if (td.Id != null) SetAnchor(td.Id, rowTop);
        float w = (cell.ColSpan - 1) * hs;
        for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++) w += widths[c];
        // Right to left, the columns run from the right: the cell's span is mirrored within the table.
        cell.X = rtl ? colX[0] + colX[^1] - hs - colX[cell.Col] - w : colX[cell.Col];
        cell.W = w;
        cell.Top = rowTop;

        float bl = collapse ? cs.BorderLeft / 2 : cs.BorderLeft, br = collapse ? cs.BorderRight / 2 : cs.BorderRight;
        float bt = collapse ? cs.BorderTop / 2 : cs.BorderTop, bb = collapse ? cs.BorderBottom / 2 : cs.BorderBottom;
        float pl = cs.PaddingLeft.Resolve(w), pr = cs.PaddingRight.Resolve(w), pt = cs.PaddingTop.Resolve(w), pb = cs.PaddingBottom.Resolve(w);

        cell.Group = new GroupOp();
        _ops.Add(cell.Group);
        cell.OpsStart = _ops.Count;
        float contentTop = rowTop + bt + pt;
        var outerFloats = EnterFormattingContext();
        var outerCb = _cbHeight;
        _cbHeight = rowHeight is float rh ? new CbHeights(Math.Max(0, rh - bt - pt - pb - bb)) : null;
        _flowProbe = probe;
        float contentBottom = LayoutFlow(td, cell.X + bl + pl, contentTop, Math.Max(0, w - bl - br - pl - pr));
        _cbHeight = outerCb;
        contentBottom = LeaveFormattingContext(outerFloats, contentBottom);
        cell.OpsEnd = _ops.Count;
        cell.NeededH = cell.ContentH = bt + pt + (contentBottom - contentTop) + pb + bb;
        if (!cs.Height.IsAuto && !cs.Height.IsPercent)
            cell.NeededH = Math.Max(cell.NeededH, cs.Height.Value + (cs.BorderBox ? 0 : bt + pt + pb + bb));
    }

    /// <summary>
    /// With collapsed borders, the sides of a cell that it draws: each shared border is drawn once, by the cell whose
    /// border wins (the wider one; on a tie the cell above or to the left), and an outer border by the cell unless the
    /// table's own border is wider. A row at the top of a page draws its top border again.
    /// </summary>
    private static int CollapsedSides(TableModel m, CellModel cell, bool atPageTop)
    {
        var s = cell.Td.Style;
        var t = m.TableStyle;
        int rows = m.Rows.Count, cols = m.ColCount;
        int r0 = cell.Row, r1 = cell.Row + cell.RowSpan - 1, c0 = cell.Col, c1 = cell.Col + cell.ColSpan - 1;
        int sides = 0;
        if (atPageTop || (r0 == 0 ? s.BorderTop >= t.BorderTop : s.BorderTop > (m.Grid[r0 - 1, c0]?.Td.Style.BorderBottom ?? 0)))
            sides |= BoxPainter.Top;
        if (r1 >= rows - 1 ? s.BorderBottom >= t.BorderBottom : s.BorderBottom >= (m.Grid[r1 + 1, c0]?.Td.Style.BorderTop ?? 0))
            sides |= BoxPainter.Bottom;
        if (c0 == 0 ? s.BorderLeft >= t.BorderLeft : s.BorderLeft > (m.Grid[r0, c0 - 1]?.Td.Style.BorderRight ?? 0))
            sides |= BoxPainter.Left;
        if (c1 >= cols - 1 ? s.BorderRight >= t.BorderRight : s.BorderRight >= (m.Grid[r0, c1 + 1]?.Td.Style.BorderLeft ?? 0))
            sides |= BoxPainter.Right;
        return sides;
    }

    /// <summary>The sides of a collapsed table's own border that are wider than the cells' borders along them.</summary>
    private static int TableSides(TableModel m)
    {
        var t = m.TableStyle;
        float top = 0, bottom = 0, left = 0, right = 0;
        int rows = m.Rows.Count, cols = m.ColCount;
        for (int c = 0; c < cols && rows > 0; c++)
        {
            top = Math.Max(top, m.Grid[0, c]?.Td.Style.BorderTop ?? 0);
            bottom = Math.Max(bottom, m.Grid[rows - 1, c]?.Td.Style.BorderBottom ?? 0);
        }
        for (int r = 0; r < rows && cols > 0; r++)
        {
            left = Math.Max(left, m.Grid[r, 0]?.Td.Style.BorderLeft ?? 0);
            right = Math.Max(right, m.Grid[r, cols - 1]?.Td.Style.BorderRight ?? 0);
        }
        int sides = 0;
        if (t.BorderTop > top) sides |= BoxPainter.Top;
        if (t.BorderBottom > bottom) sides |= BoxPainter.Bottom;
        if (t.BorderLeft > left) sides |= BoxPainter.Left;
        if (t.BorderRight > right) sides |= BoxPainter.Right;
        return sides;
    }

    private void FinishCell(TableModel m, CellModel cell, float rowBottom, bool collapse)
    {
        var cs = cell.Td.Style;
        float h = rowBottom - cell.Top;
        float shift = cs.VerticalAlign switch
        {
            // The content is aligned within the whole cell, also when the cell's height made it taller than the content.
            VerticalAlign.Middle => (h - cell.ContentH) / 2,
            VerticalAlign.Bottom or VerticalAlign.TextBottom => h - cell.ContentH,
            _ => 0,
        };
        if (shift > 0.01f)
            for (int i = cell.OpsStart; i < cell.OpsEnd && i < _ops.Count; i++) _ops[i].Offset(0, shift);

        if ((cs.ClipsX || cs.ClipsY) && cell.OpsEnd > cell.OpsStart && cell.OpsEnd <= _ops.Count) ClipCell(cell, h, collapse);

        float x = cell.X, y = cell.Top, w = cell.W;
        int sides = BoxPainter.AllSides;
        if (collapse)
        {
            sides = CollapsedSides(m, cell, AtPageTop(y));
            x -= cs.BorderLeft / 2;
            w += (cs.BorderLeft + cs.BorderRight) / 2;
            h += cs.BorderBottom / 2;
            // At the top of a page the outer half of the top border would land on the previous page.
            if (!AtPageTop(y))
            {
                y -= cs.BorderTop / 2;
                h += cs.BorderTop / 2;
            }
        }
        BoxPainter.Paint(cell.Group!, cs, x, y, w, h, _images, w, sides, collapsedCell: collapse);
    }

    /// <summary>
    /// Moves a cell's content into a clip to its padding box (overflow other than visible). Later cells keep their op
    /// indices: the clip takes the place of the first op and empty groups stand in for the rest.
    /// </summary>
    private void ClipCell(CellModel cell, float h, bool collapse)
    {
        var cs = cell.Td.Style;
        float bl = collapse ? cs.BorderLeft / 2 : cs.BorderLeft, br = collapse ? cs.BorderRight / 2 : cs.BorderRight;
        float bt = collapse ? cs.BorderTop / 2 : cs.BorderTop, bb = collapse ? cs.BorderBottom / 2 : cs.BorderBottom;
        float px = cell.X + bl, py = cell.Top + bt, pw = Math.Max(0, cell.W - bl - br), ph = Math.Max(0, h - bt - bb);
        var radii = cs.ClipsX && cs.ClipsY && !collapse ? BoxPainter.InnerRadii(BoxPainter.Radii(cs, cell.W, h), bl, bt, br, bb) : null;
        if (!cs.ClipsX) { px -= ClipOp.Far; pw += 2 * ClipOp.Far; }
        if (!cs.ClipsY) { py -= ClipOp.Far; ph += 2 * ClipOp.Far; }
        var clip = new ClipOp(px, py, pw, ph, radii);
        for (int i = cell.OpsStart; i < cell.OpsEnd; i++)
        {
            clip.Ops.Add(_ops[i]);
            _ops[i] = new GroupOp();
        }
        _ops[cell.OpsStart] = clip;
    }

    // ------------------------------------------------------------------ intrinsic widths

    private bool _measuring;

    /// <summary>Min-content and max-content border-box widths of a block-level element.</summary>
    private (float Min, float Max) Intrinsic(Element e)
    {
        if (_intrinsicCache.TryGetValue(e, out var cached)) return cached;
        var s = e.Style;
        float frame = s.PaddingLeft.Resolve(0) + s.PaddingRight.Resolve(0) + s.BorderLeft + s.BorderRight;
        (float, float) result;
        if (e.Name is "img" or "svg")
        {
            bool saved = _measuring;
            _measuring = true;
            float w;
            if (e.Name == "img") ImageSize(e, 100000, out w, out _);
            else SvgSize(e, 100000, out w, out _);
            _measuring = saved;
            result = s.Width.IsPercent ? (0, w + frame) : (w + frame, w + frame);
        }
        else if (s.Display == Display.Table)
        {
            var m = GetTableModel(e);
            float hs = s.BorderCollapse ? 0 : s.BorderSpacingH;
            float mn = (m.ColCount > 0 ? (m.ColCount + 1) * hs : 0) + frame, mx = mn;
            for (int c = 0; c < m.ColCount; c++)
            {
                mn += m.ColMin[c];
                mx += m.ColPct[c] >= 0 ? m.ColMax[c] : m.ColMax[c];
            }
            result = (mn, mx);
        }
        else if (HasSizeContainment(s))
        {
            // Inline-size containment: the content does not count towards the box's min- and max-content widths.
            result = (frame, frame);
        }
        else
        {
            var (mn, mx) = ContentIntrinsic(e);
            result = (mn + frame, mx + frame);
        }
        if (!s.Width.IsAuto && !s.Width.IsPercent && e.Name is not ("img" or "svg"))
        {
            float w = s.Width.Value + (s.BorderBox ? 0 : frame);
            result = (Math.Max(w, s.Display == Display.Table ? result.Item1 : 0), Math.Max(w, s.Display == Display.Table ? result.Item1 : 0));
        }
        else if (s.Width.IsAuto && s.AspectRatio > 0 && e.Name is not ("img" or "svg"))
        {
            float frameV = s.PaddingTop.Resolve(0) + s.PaddingBottom.Resolve(0) + s.BorderTop + s.BorderBottom;
            bool table = s.Display == Display.Table;
            if (ClampedHeight(s) is float h)
            {
                // aspect-ratio with a definite height: the width follows from the height. Content can only widen it while
                // min-width is auto and the overflow is visible (a table is never narrower than its columns).
                float w = RatioWidth(s, h, frame, frameV) + frame;
                float floor = table || s.MinWidth.IsAuto && !s.ClipsX ? result.Item1 : 0;
                result = (Math.Max(w, floor), Math.Max(w, floor));
            }
            else
            {
                // min-height and max-height carry over to the width through the ratio.
                var (tmin, tmax) = TransferredWidthLimits(s, frame, frameV);
                float mn = Math.Max(Math.Min(result.Item1, tmax + frame), tmin + frame);
                float mx = Math.Max(Math.Min(result.Item2, tmax + frame), tmin + frame);
                result = table ? (Math.Max(mn, result.Item1), Math.Max(mx, result.Item1)) : (mn, mx);
            }
        }
        if (!s.MaxWidth.IsAuto && !s.MaxWidth.IsPercent)
        {
            float mw = s.MaxWidth.Value + (s.BorderBox ? 0 : frame);
            result = (Math.Min(result.Item1, mw), Math.Min(result.Item2, mw));
        }
        _intrinsicCache[e] = result;
        return result;
    }

    /// <summary>Intrinsic widths of a run of inline content.</summary>
    private (float Min, float Max) RunIntrinsic(List<Node> nodes)
    {
        bool saved = _measuring;
        _measuring = true;
        _inlineDepth++;
        try
        {
            var st = CollectInline(nodes, 100000);
            var items = st.Items;
            float min = 0, max = 0, group = 0, line = 0;
            foreach (var it in items)
            {
                if (it.Kind == ItemKind.Break)
                {
                    min = Math.Max(min, group);
                    max = Math.Max(max, line);
                    group = line = 0;
                    continue;
                }
                if (it.Kind == ItemKind.Space && it.Breakable)
                {
                    min = Math.Max(min, group);
                    group = 0;
                    line += it.Width;
                    continue;
                }
                if (it.Kind == ItemKind.Float)
                {
                    min = Math.Max(min, it.FloatMin);
                    line += it.Width;
                    continue;
                }
                line += it.Width;
                if (it.Kind == ItemKind.Text && (it.Style.WordBreak == WordBreak.BreakAll || it.Style.OverflowWrap == OverflowWrap.Anywhere
                    || it.Style.LineBreak == LineBreak.Anywhere))
                {
                    // overflow-wrap: anywhere, word-break: break-all and line-break: anywhere let the word shrink down to
                    // its widest letter.
                    min = Math.Max(min, group);
                    group = 0;
                    var f = it.Style.Font;
                    for (int k = 0; k < it.Length; k++) min = Math.Max(min, f.CharWidth(it.Source[it.Start + k], it.Style.FontSize) + it.Style.LetterSpacing);
                    continue;
                }
                group += it.MinWidth >= 0 ? it.MinWidth : it.Width;
                if (it.BreakAfter != BreakAfter.None)
                {
                    min = Math.Max(min, group + (it.BreakAfter == BreakAfter.SoftHyphen ? HyphenWidth(it.Style) : 0));
                    group = 0;
                }
            }
            ReturnState(st);
            return (Math.Max(min, group), Math.Max(max, line));
        }
        finally
        {
            if (--_inlineDepth == 0) _itemPoolUsed = 0;
            _measuring = saved;
        }
    }

    /// <summary>Intrinsic widths of an element's content box.</summary>
    private (float Min, float Max) ContentIntrinsic(Element e)
    {
        var es = e.Style;
        if (es.IsFlex) return FlexIntrinsic(e);
        if (es.IsGrid) return GridIntrinsic(e);
        if (es.IsMultiColumn) return ColumnsIntrinsic(e);
        float min = 0, max = 0;
        List<Node>? run = null;
        bool saved = _measuring;
        _measuring = true;

        void FlushRun()
        {
            if (run == null || run.Count == 0) return;
            // The items come from the pool, which a paragraph laid out inside (an inline-block's) hands out again from
            // the start when it is done unless this run counts as open, and the words after the box then took the
            // place of the words before it.
            _inlineDepth++;
            var st = CollectInline(run, 100000);
            var items = st.Items;
            run.Clear();
            float group = 0, line = 0;
            foreach (var it in items)
            {
                if (it.Kind == ItemKind.Break)
                {
                    min = Math.Max(min, group);
                    max = Math.Max(max, line);
                    group = line = 0;
                    continue;
                }
                if (it.Kind == ItemKind.Space && it.Breakable)
                {
                    min = Math.Max(min, group);
                    group = 0;
                    line += it.Width;
                    continue;
                }
                if (it.Kind == ItemKind.Float)
                {
                    min = Math.Max(min, it.FloatMin);
                    line += it.Width;
                    continue;
                }
                group += it.MinWidth >= 0 ? it.MinWidth : it.Width;
                line += it.Width;
            }
            min = Math.Max(min, group);
            max = Math.Max(max, line);
            ReturnState(st);
            if (--_inlineDepth == 0) _itemPoolUsed = 0;
        }

        foreach (var child in e.Children)
        {
            if (child is TextNode) { (run ??= RentRun()).Add(child); continue; }
            var ce = (Element)child;
            if (ce.Style == null || ce.Style.Display == Display.None || ce.Detached) continue;
            if (ce.Style.OutOfFlow) continue;
            if (IsInlineLevel(ce) || ce.Style.Floated) { (run ??= RentRun()).Add(child); continue; }
            FlushRun();
            var (cmin, cmax) = Intrinsic(ce);
            var cs = ce.Style;
            float margins = (cs.MarginLeft.IsAuto || cs.MarginLeft.IsPercent ? 0 : cs.MarginLeft.Value)
                            + (cs.MarginRight.IsAuto || cs.MarginRight.IsPercent ? 0 : cs.MarginRight.Value);
            min = Math.Max(min, cmin + margins);
            max = Math.Max(max, cmax + margins);
        }
        FlushRun();
        ReturnRun(run);
        _measuring = saved;
        return (min, max);
    }
}

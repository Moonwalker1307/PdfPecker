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
        public float Top, NeededH;
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
        public float[] ColMin = Array.Empty<float>(), ColMax = Array.Empty<float>();
        public float[] ColFixed = Array.Empty<float>(), ColPct = Array.Empty<float>();
    }

    private readonly Dictionary<Element, TableModel> _tables = new();

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
                if (cs.Width.IsPercent) m.ColPct[colIndex] = cs.Width.Value;
                else if (!cs.Width.IsAuto) m.ColFixed[colIndex] = cs.Width.Value;
            }
        }

        float hs = table.Style.BorderCollapse ? 0 : table.Style.BorderSpacingH;
        var spanning = new List<CellModel>();
        foreach (var row in m.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var cs = cell.Td.Style;
                var (mn, mx) = ContentIntrinsic(cell.Td);
                float frame = cs.PaddingLeft.Resolve(0) + cs.PaddingRight.Resolve(0) + cs.BorderLeft + cs.BorderRight;
                cell.Min = mn + frame;
                cell.Max = Math.Max(mx + frame, cell.Min);
                if (cs.Width.IsPercent) cell.PercentWidth = cs.Width.Value;
                else if (!cs.Width.IsAuto)
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

    private float LayoutTable(Element table, float x, float y, float availW)
    {
        var s = table.Style;
        if (table.Id != null) SetAnchor(table.Id, y);
        var m = GetTableModel(table);
        bool collapse = s.BorderCollapse;
        float hs = collapse ? 0 : s.BorderSpacingH, vs = collapse ? 0 : s.BorderSpacingV;
        float ml = s.MarginLeft.Resolve(availW), mr = s.MarginRight.Resolve(availW);
        float frameL, frameR, frameT, frameB;
        if (collapse)
        {
            frameL = s.BorderLeft / 2; frameR = s.BorderRight / 2; frameT = s.BorderTop / 2; frameB = s.BorderBottom / 2;
        }
        else
        {
            frameL = s.BorderLeft + s.PaddingLeft.Resolve(availW);
            frameR = s.BorderRight + s.PaddingRight.Resolve(availW);
            frameT = s.BorderTop + s.PaddingTop.Resolve(availW);
            frameB = s.BorderBottom + s.PaddingBottom.Resolve(availW);
        }
        int n = m.ColCount;
        float gridSpace = (n + 1) * hs;
        bool specified = !s.Width.IsAuto;
        float avail;
        if (specified)
        {
            float tw = s.Width.Resolve(availW);
            if (!s.BorderBox) tw += frameL + frameR;
            avail = tw - frameL - frameR - gridSpace;
        }
        else avail = availW - ml - mr - frameL - frameR - gridSpace;
        if (!s.MaxWidth.IsAuto) avail = Math.Min(avail, s.MaxWidth.Resolve(availW) - frameL - frameR - gridSpace);

        var widths = AllocateColumns(m, avail, specified);
        float contentW = gridSpace;
        foreach (var cw in widths) contentW += cw;
        float boxW = contentW + frameL + frameR;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) ml = Math.Max(0, (availW - boxW) / 2);
        else if (s.MarginLeft.IsAuto) ml = Math.Max(0, availW - boxW - mr);
        float bx = x + ml;

        var colX = new float[n + 1];
        float cxp = bx + frameL + hs;
        for (int c = 0; c < n; c++) { colX[c] = cxp; cxp += widths[c] + hs; }
        colX[n] = cxp;

        foreach (var caption in m.Captions)
        {
            float cmt = caption.Style.MarginTop.Resolve(boxW);
            y = LayoutBlockBox(caption, bx, y + cmt, boxW, shrinkToFit: false) + caption.Style.MarginBottom.Resolve(boxW);
        }

        float tableTop = y;
        var tableGroup = new GroupOp();
        _ops.Add(tableGroup);
        float cy = y + frameT + vs;

        for (int r = 0; r < m.Rows.Count; r++)
            cy = LayoutRow(m, r, cy, colX, widths, hs, vs, collapse, allowMove: true) + vs;

        float tableBottom = cy + frameB;
        if (!s.Height.IsAuto && !s.Height.IsPercent && tableBottom - tableTop < s.Height.Value) tableBottom = tableTop + s.Height.Value;
        BoxPainter.Paint(tableGroup, s, bx, tableTop, boxW, tableBottom - tableTop);
        _lastBoxRight = bx + boxW;
        return tableBottom;
    }

    /// <summary>Lays out one table row at <paramref name="rowTop"/> and returns its bottom. A row that would straddle a
    /// page boundary moves to the next page, where the table's header rows are repeated first.</summary>
    private float LayoutRow(TableModel m, int r, float rowTop, float[] colX, float[] widths, float hs, float vs, bool collapse, bool allowMove)
    {
        var row = m.Rows[r];
        int n = m.ColCount;
        int rowStartOps = _ops.Count;
        float rowBottom = 0;
        GroupOp rowGroup = null!;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            rowGroup = new GroupOp();
            _ops.Add(rowGroup);
            foreach (var cell in row.Cells) LayoutCell(cell, rowTop, colX, widths, hs, collapse);

            float rowH = 0;
            foreach (var cell in row.Cells)
                if (cell.RowSpan == 1) rowH = Math.Max(rowH, cell.NeededH);
            var trs = row.Tr.Style;
            if (!trs.Height.IsAuto && !trs.Height.IsPercent) rowH = Math.Max(rowH, trs.Height.Value);
            rowBottom = rowTop + rowH;
            foreach (var cell in row.Ending)
                rowBottom = Math.Max(rowBottom, cell.Top + cell.NeededH);

            if (allowMove && attempt == 0 && CrossesPage(rowTop, rowBottom) && rowBottom - rowTop <= _pageH && !AtPageTop(rowTop))
            {
                Truncate(rowStartOps);
                rowTop = NextPage(rowTop);
                if (r >= m.HeadCount)
                {
                    float headStart = rowTop;
                    for (int h = 0; h < m.HeadCount; h++)
                        rowTop = LayoutRow(m, h, rowTop, colX, widths, hs, vs, collapse, allowMove: false) + vs;
                    if (rowTop - headStart > _pageH / 2)
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

        foreach (var cell in row.Ending) FinishCell(cell, rowBottom, collapse);

        var rs = row.Tr.Style;
        if (CssColor.IsVisible(rs.BackgroundColor) && row.Cells.Count + row.Ending.Count > 0)
            rowGroup.Ops.Add(new RectOp(colX[0], rowTop, colX[n] - hs - colX[0], rowBottom - rowTop, rs.BackgroundColor));
        return rowBottom;
    }

    private void LayoutCell(CellModel cell, float rowTop, float[] colX, float[] widths, float hs, bool collapse)
    {
        var td = cell.Td;
        var cs = td.Style;
        if (td.Id != null) SetAnchor(td.Id, rowTop);
        float w = (cell.ColSpan - 1) * hs;
        for (int c = cell.Col; c < cell.Col + cell.ColSpan; c++) w += widths[c];
        cell.X = colX[cell.Col];
        cell.W = w;
        cell.Top = rowTop;

        float bl = collapse ? cs.BorderLeft / 2 : cs.BorderLeft, br = collapse ? cs.BorderRight / 2 : cs.BorderRight;
        float bt = collapse ? cs.BorderTop / 2 : cs.BorderTop, bb = collapse ? cs.BorderBottom / 2 : cs.BorderBottom;
        float pl = cs.PaddingLeft.Resolve(w), pr = cs.PaddingRight.Resolve(w), pt = cs.PaddingTop.Resolve(w), pb = cs.PaddingBottom.Resolve(w);

        cell.Group = new GroupOp();
        _ops.Add(cell.Group);
        cell.OpsStart = _ops.Count;
        float contentTop = rowTop + bt + pt;
        float contentBottom = LayoutFlow(td, cell.X + bl + pl, contentTop, Math.Max(0, w - bl - br - pl - pr));
        cell.OpsEnd = _ops.Count;
        cell.NeededH = bt + pt + (contentBottom - contentTop) + pb + bb;
        if (!cs.Height.IsAuto && !cs.Height.IsPercent)
            cell.NeededH = Math.Max(cell.NeededH, cs.Height.Value + (cs.BorderBox ? 0 : bt + pt + pb + bb));
    }

    private void FinishCell(CellModel cell, float rowBottom, bool collapse)
    {
        var cs = cell.Td.Style;
        float h = rowBottom - cell.Top;
        float shift = cs.VerticalAlign switch
        {
            VerticalAlign.Middle => (h - cell.NeededH) / 2,
            VerticalAlign.Bottom or VerticalAlign.TextBottom => h - cell.NeededH,
            _ => 0,
        };
        if (shift > 0.01f)
            for (int i = cell.OpsStart; i < cell.OpsEnd && i < _ops.Count; i++) _ops[i].Offset(0, shift);

        float x = cell.X, y = cell.Top, w = cell.W;
        if (collapse)
        {
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
        BoxPainter.Paint(cell.Group!, cs, x, y, w, h);
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
            float mn = (m.ColCount + 1) * hs + frame, mx = mn;
            for (int c = 0; c < m.ColCount; c++)
            {
                mn += m.ColMin[c];
                mx += m.ColPct[c] >= 0 ? m.ColMax[c] : m.ColMax[c];
            }
            result = (mn, mx);
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
            var items = CollectInline(nodes, 100000);
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
                group += it.Width;
                line += it.Width;
            }
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
            var items = CollectInline(run, 100000);
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
                group += it.Width;
                line += it.Width;
            }
            min = Math.Max(min, group);
            max = Math.Max(max, line);
        }

        foreach (var child in e.Children)
        {
            if (child is TextNode) { (run ??= new List<Node>()).Add(child); continue; }
            var ce = (Element)child;
            if (ce.Style == null || ce.Style.Display == Display.None || ce.Detached) continue;
            if (IsInlineLevel(ce)) { (run ??= new List<Node>()).Add(child); continue; }
            FlushRun();
            var (cmin, cmax) = Intrinsic(ce);
            var cs = ce.Style;
            float margins = (cs.MarginLeft.IsAuto || cs.MarginLeft.IsPercent ? 0 : cs.MarginLeft.Value)
                            + (cs.MarginRight.IsAuto || cs.MarginRight.IsPercent ? 0 : cs.MarginRight.Value);
            min = Math.Max(min, cmin + margins);
            max = Math.Max(max, cmax + margins);
        }
        FlushRun();
        _measuring = saved;
        return (min, max);
    }
}

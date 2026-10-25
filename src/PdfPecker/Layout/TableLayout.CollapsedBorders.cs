using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// The collapsing border model (CSS 2.1 §17.6.2): which border wins where cells, rows, row groups and the table meet,
/// how wide each resolved border is, and who draws it.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Where a contending border comes from; on a tie of width and style the higher origin wins (CSS 2.1 §17.6.2.1).
    private const int TableOrigin = 0, GroupOrigin = 1, RowOrigin = 2, CellOrigin = 3;

    /// <summary>
    /// The border that wins one edge of a cell, built up by offering each border that meets there. A hidden border hides
    /// the edge; otherwise the wider border wins, then the style first in double, solid, dashed, dotted, ridge, outset,
    /// groove, inset, then the origin (cell, row, row group, table); a full tie goes to the border offered first.
    /// </summary>
    private struct EdgeWinner
    {
        public ComputedStyle? Style;
        public int Side, Origin;
        public bool Self, Hidden;
        private float _width; // the width of the winning border, kept rather than looked up again for every offer

        public void Offer(ComputedStyle? s, int side, int origin, bool self = false)
        {
            if (s == null || Hidden) return;
            if (IsHidden(s, side))
            {
                Hidden = true;
                Style = null;
                Self = false;
                return;
            }
            float width = BorderWidth(s, side);
            if (Style != null && !Beats(s, side, width, origin, Style, Side, _width, Origin)) return;
            Style = s;
            Side = side;
            Origin = origin;
            Self = self;
            _width = width;
        }

        /// <summary>The width of the resolved border: none when hidden.</summary>
        public readonly float Width => Style == null ? 0 : _width;
    }

    private static bool Beats(ComputedStyle a, int aSide, float aw, int aOrigin, ComputedStyle b, int bSide, float bw, int bOrigin)
    {
        if (aw != bw) return aw > bw;
        int ar = StyleRank(BorderStyleOf(a, aSide)), br = StyleRank(BorderStyleOf(b, bSide));
        return ar != br ? ar > br : aOrigin > bOrigin;
    }

    private static bool IsHidden(ComputedStyle s, int side) => (s.BorderHiddenSides & side) != 0;

    private static float BorderWidth(ComputedStyle s, int side) => side switch
    {
        BoxPainter.Top => s.BorderTop,
        BoxPainter.Right => s.BorderRight,
        BoxPainter.Bottom => s.BorderBottom,
        _ => s.BorderLeft,
    };

    private static BorderStyle BorderStyleOf(ComputedStyle s, int side) => side switch
    {
        BoxPainter.Top => s.BorderTopStyle,
        BoxPainter.Right => s.BorderRightStyle,
        BoxPainter.Bottom => s.BorderBottomStyle,
        _ => s.BorderLeftStyle,
    };

    private static int StyleRank(BorderStyle style) => style switch
    {
        BorderStyle.Double => 8,
        BorderStyle.Solid => 7,
        BorderStyle.Dashed => 6,
        BorderStyle.Dotted => 5,
        BorderStyle.Ridge => 4,
        BorderStyle.Outset => 3,
        BorderStyle.Groove => 2,
        BorderStyle.Inset => 1,
        _ => 0,
    };

    /// <summary>
    /// The border that wins side <paramref name="side"/> of a cell, at column <paramref name="k"/> of its span for the
    /// top and bottom or row <paramref name="k"/> for the left and right: the cell's own border, the facing border of the
    /// cell next to it, the borders of the rows and row groups that meet there and, at the table's edge, the table's.
    /// Rows and row groups have left and right borders only along the table's edge.
    /// </summary>
    private static EdgeWinner ResolveEdge(TableModel m, CellModel cell, int side, int k)
    {
        var s = cell.Td.Style;
        var t = m.TableStyle;
        int rows = m.Rows.Count, cols = m.ColCount;
        int r0 = cell.Row, r1 = Math.Min(rows, cell.Row + cell.RowSpan) - 1;
        int c0 = cell.Col, c1 = Math.Min(cols, cell.Col + cell.ColSpan) - 1;
        var edge = default(EdgeWinner);
        switch (side)
        {
            case BoxPainter.Top:
                // The cell above is offered first, so on a tie its bottom border wins.
                if (r0 > 0) edge.Offer(m.Grid[r0 - 1, k]?.Td.Style, BoxPainter.Bottom, CellOrigin);
                edge.Offer(s, BoxPainter.Top, CellOrigin, self: true);
                OfferRows(m, ref edge, r0 - 1, r0);
                if (r0 == 0) edge.Offer(t, BoxPainter.Top, TableOrigin);
                break;
            case BoxPainter.Bottom:
                edge.Offer(s, BoxPainter.Bottom, CellOrigin, self: true);
                if (r1 < rows - 1) edge.Offer(m.Grid[r1 + 1, k]?.Td.Style, BoxPainter.Top, CellOrigin);
                OfferRows(m, ref edge, r1, r1 + 1);
                if (r1 >= rows - 1) edge.Offer(t, BoxPainter.Bottom, TableOrigin);
                break;
            case BoxPainter.Left:
            {
                // Left and right are the physical sides: right to left, the first column is on the right.
                int next = m.Rtl ? c1 + 1 : c0 - 1;
                if (next >= 0 && next < cols) edge.Offer(m.Grid[k, next]?.Td.Style, BoxPainter.Right, CellOrigin);
                edge.Offer(s, BoxPainter.Left, CellOrigin, self: true);
                if (next < 0 || next >= cols) OfferOuter(m, ref edge, k, BoxPainter.Left);
                break;
            }
            default:
            {
                int next = m.Rtl ? c0 - 1 : c1 + 1;
                edge.Offer(s, BoxPainter.Right, CellOrigin, self: true);
                if (next >= 0 && next < cols) edge.Offer(m.Grid[k, next]?.Td.Style, BoxPainter.Left, CellOrigin);
                else OfferOuter(m, ref edge, k, BoxPainter.Right);
                break;
            }
        }
        return edge;
    }

    /// <summary>Offers the borders of rows <paramref name="above"/> and <paramref name="below"/> (either may be out of range) and of their row groups, where those start or end between them.</summary>
    private static void OfferRows(TableModel m, ref EdgeWinner edge, int above, int below)
    {
        int rows = m.Rows.Count;
        var up = above >= 0 && above < rows ? m.Rows[above] : null;
        var down = below >= 0 && below < rows ? m.Rows[below] : null;
        edge.Offer(up?.Tr.Style, BoxPainter.Bottom, RowOrigin);
        edge.Offer(down?.Tr.Style, BoxPainter.Top, RowOrigin);
        if (up?.Group != down?.Group)
        {
            edge.Offer(up?.Group?.Style, BoxPainter.Bottom, GroupOrigin);
            edge.Offer(down?.Group?.Style, BoxPainter.Top, GroupOrigin);
        }
    }

    /// <summary>Offers the left or right borders of row <paramref name="r"/>, its row group and the table.</summary>
    private static void OfferOuter(TableModel m, ref EdgeWinner edge, int r, int side)
    {
        var row = m.Rows[r];
        edge.Offer(row.Tr.Style, side, RowOrigin);
        edge.Offer(row.Group?.Style, side, GroupOrigin);
        edge.Offer(m.TableStyle, side, TableOrigin);
    }

    /// <summary>
    /// The width of the resolved collapsed borders round each cell, the widest along each side (none where hidden). A
    /// cell lays its content out inside half of these and its column is as wide as that needs, so a border set only on
    /// the cell above, on a row or on a row group still takes room in the cell, and a thick table border widens the
    /// first and last columns, as in Chrome.
    /// </summary>
    private static void ResolveCollapsedBorders(TableModel m)
    {
        int rows = m.Rows.Count, cols = m.ColCount;
        foreach (var row in m.Rows)
            foreach (var cell in row.Cells)
            {
                int r1 = Math.Min(rows, cell.Row + cell.RowSpan) - 1, c1 = Math.Min(cols, cell.Col + cell.ColSpan) - 1;
                float top = 0, bottom = 0, left = 0, right = 0;
                cell.SelfSides = cell.ForeignSides = 0;
                for (int c = cell.Col; c <= c1; c++)
                {
                    top = Math.Max(top, Resolved(m, cell, BoxPainter.Top, c));
                    bottom = Math.Max(bottom, Resolved(m, cell, BoxPainter.Bottom, c));
                }
                for (int r = cell.Row; r <= r1; r++)
                {
                    left = Math.Max(left, Resolved(m, cell, BoxPainter.Left, r));
                    right = Math.Max(right, Resolved(m, cell, BoxPainter.Right, r));
                }
                cell.CollapsedT = top;
                cell.CollapsedB = bottom;
                cell.CollapsedL = left;
                cell.CollapsedR = right;
            }
    }

    /// <summary>
    /// The width of the border that wins side <paramref name="side"/> of a cell at <paramref name="k"/>. At the cell's
    /// first column or row it also notes on the cell who owns that border, for <see cref="CollapsedSides"/> and
    /// <see cref="PaintForeignEdges"/>.
    /// </summary>
    private static float Resolved(TableModel m, CellModel cell, int side, int k)
    {
        var edge = ResolveEdge(m, cell, side, k);
        if (k == (side is BoxPainter.Top or BoxPainter.Bottom ? cell.Col : cell.Row))
        {
            if (edge.Self) cell.SelfSides |= side;
            if (edge.Style != null && edge.Origin != CellOrigin) cell.ForeignSides |= side;
        }
        return edge.Width;
    }

    /// <summary>
    /// The sides of a collapsed table's own border that it draws whole: those it wins beside every cell along them.
    /// Elsewhere the cells draw the borders that win along their edges (see <see cref="PaintForeignEdges"/>).
    /// </summary>
    private static int TableSides(TableModel m)
    {
        int rows = m.Rows.Count, cols = m.ColCount;
        int sides = 0;
        foreach (int side in (ReadOnlySpan<int>)[BoxPainter.Top, BoxPainter.Bottom, BoxPainter.Left, BoxPainter.Right])
        {
            bool horizontal = side is BoxPainter.Top or BoxPainter.Bottom;
            // A table whose rows hold no cells has no columns, and so no cell beside any side.
            int count = rows == 0 || cols == 0 ? 0 : horizontal ? cols : rows;
            bool whole = count > 0;
            for (int k = 0; k < count && whole; k++)
            {
                var cell = side switch
                {
                    BoxPainter.Top => m.Grid[0, k],
                    BoxPainter.Bottom => m.Grid[rows - 1, k],
                    BoxPainter.Left => m.Grid[k, m.Rtl ? cols - 1 : 0],
                    _ => m.Grid[k, m.Rtl ? 0 : cols - 1],
                };
                if (cell == null) continue;
                var edge = ResolveEdge(m, cell, side, k);
                whole = edge.Style != null && edge.Origin == TableOrigin;
            }
            if (whole) sides |= side;
        }
        return sides;
    }

    /// <summary>
    /// With collapsed borders, the sides of a cell that it draws: each shared border is drawn once, by the cell whose
    /// border wins (on a tie the cell above or to the left). A row at the top of a page draws its top border again.
    /// </summary>
    private static int CollapsedSides(CellModel cell, bool atPageTop) => cell.SelfSides | (atPageTop ? BoxPainter.Top : 0);

    /// <summary>
    /// How far the backgrounds of a row, its row group and the columns stay inside the collapsed borders that were drawn
    /// before them, at each side: half of each such border. A row's backgrounds are painted after the rows above it and
    /// the table's own border, so without this they would cover the inner half of those borders (all of it once snapped
    /// to the pixel grid); as in Chrome, the borders stay on top. The borders the row's own cells and later rows draw
    /// come after its backgrounds and cover them anyway.
    /// </summary>
    private sealed class EarlierBorders(float left, float right, float bottom)
    {
        public readonly float Left = left, Right = right, Bottom = bottom;
        // The cells starting in the row whose top border was drawn before, with half its width, by column: in a table
        // of thousands of columns most rows have few cells.
        public readonly List<(int Col, int Span, float Inset)> Tops = new();
    }

    /// <summary>
    /// The borders drawn before the backgrounds of row <paramref name="r"/> laid out at <paramref name="rowTop"/>: the
    /// sides the table draws whole, and the border above each cell that the cell above draws. At the top of a page the
    /// row's cells draw their top border again, so nothing above it is covered. Null when there is none.
    /// </summary>
    private EarlierBorders? BordersUnder(TableModel m, int r, float rowTop)
    {
        var t = m.TableStyle;
        int whole = m.WholeTableSides;
        var under = new EarlierBorders(
            (whole & BoxPainter.Left) != 0 ? t.BorderLeft / 2 : 0,
            (whole & BoxPainter.Right) != 0 ? t.BorderRight / 2 : 0,
            r == m.Rows.Count - 1 && (whole & BoxPainter.Bottom) != 0 ? t.BorderBottom / 2 : 0);
        if (!AtPageTop(rowTop))
            foreach (var cell in m.Rows[r].Cells)
                if (TopInset(m, cell) is var inset and > 0) under.Tops.Add((cell.Col, cell.ColSpan, inset));
        if (under.Tops.Count == 0 && under.Left == 0 && under.Right == 0 && under.Bottom == 0) return null;
        under.Tops.Sort(static (a, b) => a.Col.CompareTo(b.Col));
        return under;
    }

    /// <summary>Half the border above a cell when it was drawn before the cell's row: by the table or by the cell above.</summary>
    private static float TopInset(TableModel m, CellModel cell)
    {
        if (cell.Row == 0) return (m.WholeTableSides & BoxPainter.Top) != 0 ? m.TableStyle.BorderTop / 2 : 0;
        return ((cell.SelfSides | cell.ForeignSides) & BoxPainter.Top) == 0 ? cell.CollapsedT / 2 : 0;
    }

    /// <summary>
    /// Draws the border above a cell of a footer repeated at the bottom of a page where a cell of the table's last body
    /// row wins it, as Chrome does. That cell is pages away, and the body row now above the footer may have left the
    /// line to the row that moved to the next page, so otherwise no one would draw it.
    /// </summary>
    private static void PaintBorrowedTop(TableModel m, CellModel cell, int above)
    {
        if (!m.TableStyle.Visible || cell.Row == 0) return;
        // Along a cell spanning columns, the widest of the borders above it, across the cell, as its height counts it.
        EdgeWinner widest = default;
        bool drawn = above >= 0;
        for (int c = cell.Col, end = Math.Min(m.ColCount, cell.Col + cell.ColSpan); c < end; c++)
        {
            var edge = ResolveEdge(m, cell, BoxPainter.Top, c);
            if (edge.Width > widest.Width) widest = edge;
            // Where the cells of the row above drew their bottom borders, the line is there already.
            drawn = drawn && m.Grid[above, c] is { } up && up.Row + up.RowSpan - 1 == above && (up.SelfSides & BoxPainter.Bottom) != 0;
        }
        if (drawn) return;
        if (widest.Style is not { } s || widest.Self || widest.Origin != CellOrigin) return;
        float w = widest.Width;
        BoxPainter.PaintCollapsedBand(cell.Group!, s, widest.Side, BoxPainter.Top, cell.X, cell.Top - w / 2, cell.W, w);
    }

    /// <summary>
    /// Draws the borders of rows, row groups and the table that win along a cell's edges, centred on the grid lines as
    /// the cells' borders are: along its top, and along its bottom, left and right where those are the table's edge.
    /// A table side the table draws whole is left to it.
    /// </summary>
    private void PaintForeignEdges(TableModel m, CellModel cell, float rowBottom)
    {
        if (!m.TableStyle.Visible) return;
        int rows = m.Rows.Count, cols = m.ColCount;
        bool first = cell.Row == 0, last = cell.Row + cell.RowSpan >= rows;
        bool start = cell.Col == 0, end = cell.Col + cell.ColSpan >= cols;
        // Right to left, the first column is on the right.
        bool leftEdge = m.Rtl ? end : start, rightEdge = m.Rtl ? start : end;
        float x0 = cell.X - (leftEdge ? cell.CollapsedL / 2 : 0), x1 = cell.X + cell.W + (rightEdge ? cell.CollapsedR / 2 : 0);
        float y0 = cell.Top - (first && !AtPageTop(cell.Top) ? cell.CollapsedT / 2 : 0), y1 = rowBottom + (last ? cell.CollapsedB / 2 : 0);
        var group = cell.Group!;
        foreach (int side in (ReadOnlySpan<int>)[BoxPainter.Top, BoxPainter.Bottom, BoxPainter.Left, BoxPainter.Right])
        {
            bool edgeOfTable = side switch { BoxPainter.Top => first, BoxPainter.Bottom => last, BoxPainter.Left => leftEdge, _ => rightEdge };
            if (side != BoxPainter.Top && !edgeOfTable) continue;
            if (edgeOfTable && (m.WholeTableSides & side) != 0) continue;
            if ((cell.ForeignSides & side) == 0) continue;
            var edge = ResolveEdge(m, cell, side, side is BoxPainter.Top or BoxPainter.Bottom ? cell.Col : cell.Row);
            if (edge.Style is not { } s || edge.Origin == CellOrigin) continue;
            float w = edge.Width;
            switch (side)
            {
                case BoxPainter.Top:
                    BoxPainter.PaintCollapsedBand(group, s, edge.Side, side, x0, AtPageTop(cell.Top) ? cell.Top : cell.Top - w / 2, x1 - x0, w);
                    break;
                case BoxPainter.Bottom:
                    BoxPainter.PaintCollapsedBand(group, s, edge.Side, side, x0, rowBottom - w / 2, x1 - x0, w);
                    break;
                default:
                    float x = side == BoxPainter.Left ? cell.X - w / 2 : cell.X + cell.W - w / 2;
                    BoxPainter.PaintCollapsedBand(group, s, edge.Side, side, x, y0, w, y1 - y0);
                    break;
            }
        }
    }
}

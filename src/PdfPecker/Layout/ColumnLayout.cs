using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// Multi-column layout (column-count / column-width). The container's children are kept whole (as with
/// break-inside: avoid-column) and distributed over balanced columns; when the columns don't fit on the rest of the
/// page, they are filled to the bottom of the page and continue on the next one.
/// </summary>
internal sealed partial class LayoutEngine
{
    private int ColumnCountFor(ComputedStyle s, float w, float gap)
    {
        int n = s.ColumnCount;
        if (!s.ColumnWidth.IsAuto)
        {
            float cw = Math.Max(1, s.ColumnWidth.Value);
            int fit = Math.Max(1, (int)Math.Floor((w + gap) / (cw + gap)));
            n = n > 0 ? Math.Min(n, fit) : fit;
        }
        return Math.Max(1, n);
    }

    private float LayoutColumns(Element e, float x, float y, float w)
    {
        var s = e.Style;
        float gap = s.ColumnGap is { } g ? Math.Max(0, g.Resolve(w)) : s.FontSize; // normal = 1em
        int n = ColumnCountFor(s, w, gap);
        if (n <= 1) return LayoutFlow(e, x, y, w);
        float colW = Math.Max(0, (w - (n - 1) * gap) / n);

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
                cy = FlowColumns(pieces, s, n, x, cy, colW, gap);
                pieces.Clear();
                var span = CaptureBox(ce, w, shrinkToFit: false);
                if (_paginate && !AtPageTop(cy) && cy + span.Height > _grid.Start(PageOf(cy) + 1) + Eps && span.Height <= _grid.Height(PageOf(cy) + 1))
                    cy = NextPage(cy);
                Place(span, x, cy);
                cy += span.Height;
                continue;
            }
            pieces.Add(CaptureBox(ce, colW, shrinkToFit: false));
        }
        FlushRun();
        cy = FlowColumns(pieces, s, n, x, cy, colW, gap);
        _lastBoxRight = Math.Max(_lastBoxRight, x + w);
        return cy;
    }

    /// <summary>Balances pieces over n columns from y, continuing on later pages as needed. Returns the bottom.</summary>
    private float FlowColumns(List<Captured> pieces, ComputedStyle s, int n, float x, float y, float colW, float gap)
    {
        if (pieces.Count == 0) return y;
        float top = y;
        int next = 0;
        float bottom = y;
        while (next < pieces.Count)
        {
            float balanced = BalanceColumns(pieces, next, n);
            float room = _paginate ? _grid.Start(PageOf(top) + 1) - top : float.PositiveInfinity;
            if (balanced <= room + Eps)
            {
                bottom = PlaceColumns(pieces, ref next, n, balanced, x, top, colW, gap, fillAll: true, s);
                break;
            }
            if (!AtPageTop(top) && pieces[next].Height > room + Eps)
            {
                top = NextPage(top);
                continue;
            }
            // Fill the columns to the bottom of this page, then carry on on the next one.
            PlaceColumns(pieces, ref next, n, room, x, top, colW, gap, fillAll: false, s);
            top = _grid.Start(PageOf(top) + 1);
            bottom = top;
        }
        return bottom;
    }

    /// <summary>column-rule: a line in the middle of each gap between columns that hold content.</summary>
    private void ColumnRules(ComputedStyle s, int usedColumns, float x, float top, float bottom, float colW, float gap)
    {
        if (s.ColumnRuleStyle == BorderStyle.None || s.ColumnRuleWidth <= 0 || bottom <= top) return;
        float rw = s.ColumnRuleWidth, h = bottom - top;
        uint color = s.ColumnRuleColor ?? s.Color;
        for (int col = 1; col < usedColumns; col++)
        {
            float mid = x + col * (colW + gap) - gap / 2;
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

    /// <summary>The smallest column height that fits pieces[start..] into n columns, keeping each piece whole.</summary>
    private static float BalanceColumns(List<Captured> pieces, int start, int n)
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
    private float PlaceColumns(List<Captured> pieces, ref int next, int n, float h, float x, float top, float colW, float gap, bool fillAll, ComputedStyle s)
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
        ColumnRules(s, used, x, top, fillAll ? maxBottom : Math.Max(maxBottom, top + h), colW, gap);
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

using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// Page breaks at a box's edges, made as Chrome makes them: a page never ends between a box's top edge and its first
/// content, nor between its last content and its bottom edge. A box whose top border and padding are all that fit at
/// the end of a page goes to the next page whole, and a bottom border and padding that do not fit take the content
/// before them along, from the last place the content can break.
/// </summary>
internal sealed partial class LayoutEngine
{
    // Room kept free at the end of a page for the bottom edges of the box being laid out again, by page.
    private readonly Dictionary<int, float> _edgeReserve = new();

    // How many boxes are being laid out again with room kept for their bottom edges, one inside the other. The room on
    // a page covers the bottom edges of the boxes inside that end with the box too, so they do not keep room of their
    // own; a box inside that ends on another page does, up to a few levels deep, since each level lays out everything
    // inside it once more.
    private int _edgeDepth;
    private const int MaxEdgeNesting = 4;

    // How deep a box can be nested in boxes laid out again for their edges and still move to the next page with its
    // content. When a box goes there, the boxes around it whose only content it was follow it one by one, each laying
    // it out again on the next page, where boxes inside it can move once more: with each level allowed to move, the
    // work grew with the depth to the power of this limit.
    private const int MaxTopMoveNesting = 2;

    // Above 0 while a box whose first content is on a later page, but that draws something on its first page, is laid
    // out again: the boxes inside it do not probe for their first content.
    private int _edgeProbesOff;

    // The bottom of the last line or image laid out, which a box's bottom edges and the margins between them follow.
    private float _lastContentBottom = float.NaN;

    // Where the content ended that room for bottom edges did not move: the boxes around it, which end with it, need not
    // try. Nested boxes can end their content at two places in turn, so remembering only the latest let each level try
    // again what the one inside it had found stuck, and the work doubled with each level.
    private readonly HashSet<float> _stuckContentBottoms = new();

    /// <summary>The room at the end of a page kept for the bottom edges of the box around the content being laid out.</summary>
    private float EdgeReserve(int page)
        => _edgeReserve.Count > 0 && _grid is not ColumnGrid && _edgeReserve.TryGetValue(page, out float h) ? h : 0;

    /// <summary>
    /// A block-level box in a block flow, which decides its page breaks itself. Flex and grid items, floats, positioned
    /// boxes and table parts are placed by the box around them, multi-column boxes break into columns of their own,
    /// and replaced elements move to the next page whole already.
    /// </summary>
    private bool KeepsEdgesWithContent(Element e)
    {
        var s = e.Style;
        return s.Display is Display.Block or Display.ListItem or Display.Flex or Display.Grid
               && !s.Floated && !s.OutOfFlow && !s.IsMultiColumn && e.Control == null
               && !e.IsReplaced && e.Name != "hr"
               && e.Parent?.Style is not ({ IsFlex: true } or { IsGrid: true })
               && !_widthOverride.ContainsKey(e) && !_heightOverride.ContainsKey(e);
    }

    /// <summary>
    /// Whether a box that moves to the next page with its first content finds out where that content lands from a
    /// probe of its own flow, which stops its layout there. A flex or grid container places its content itself, a box
    /// that paints nothing and shares its top margin with its first child starts where that child does anyway, and a
    /// box kept whole is moved whole already.
    /// </summary>
    private static bool ProbesTopEdge(Element e)
    {
        var s = e.Style;
        return !s.IsFlex && !s.IsGrid && (!CollapsesTop(e) || PaintsBox(s));
    }

    /// <summary>Whether the box paints a background, border, outline or shadow of its own.</summary>
    private static bool PaintsBox(ComputedStyle s)
        => s.Visible && (CssColor.IsVisible(s.BackgroundColor) && !s.CanvasBackground || s.BackgroundImages != null && !s.CanvasBackground
                         || s.HasBorder || s.BorderImageSource != null || s.BoxShadows != null || s.OutlineStyle != BorderStyle.None);

    /// <summary>
    /// Lays the box just laid out at (<paramref name="x"/>, <paramref name="y"/>) out again when a page break falls at
    /// one of its edges: on the next page when it leaves nothing but its top edge on its first one, or with room kept
    /// for its bottom edge at the end of the page its content ends on. Returns the box's bottom.
    /// </summary>
    /// <remarks>
    /// A box laid out with an edge probe stopped at its first content when that landed on a later page, so it is laid
    /// out once more in full, on that page unless it drew something else before. Other boxes are looked at once they
    /// are laid out in full, and laying them out again on the next page is limited in depth as the room for bottom
    /// edges is, since each level lays out everything inside it again.
    /// </remarks>
    private float KeepEdgesWithContent(Element e, ref float x, ref float y, ref float availW, bool shrinkToFit, PageBox? pages,
        int opsStart, ListMarker? marker, int listIndex, KeepProbe? probe, float bottom)
    {
        if (probe is { Stopped: true })
        {
            if (!DrawsOnFirstPage(y, opsStart, probe.Chained))
            {
                MoveToNextPage(ref x, ref y, ref availW, pages);
                bottom = LayOutAgain(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex);
            }
            else
            {
                // The boxes inside that start before the first content would each stop at it again and be laid out
                // once more: they are looked at once laid out in full instead.
                _edgeProbesOff++;
                try { bottom = LayOutAgain(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex); }
                finally { _edgeProbesOff--; }
            }
            return KeepBottomEdge(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex, bottom, out _);
        }
        bottom = KeepBottomEdge(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex, bottom, out bool kept);
        // The probe found the first content on the box's first page; room kept for the bottom edge can move it on.
        if (probe != null && !kept || AtPageTop(y) || _edgeDepth >= MaxTopMoveNesting || !MayLayOutAgain || !LeavesOnlyTop(e, y, availW, bottom, opsStart))
            return bottom;
        MoveToNextPage(ref x, ref y, ref availW, pages);
        _edgeDepth++;
        try { bottom = LayOutAgain(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex); }
        finally { _edgeDepth--; }
        return KeepBottomEdge(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex, bottom, out _);
    }

    private void MoveToNextPage(ref float x, ref float y, ref float availW, PageBox? pages)
    {
        y = NextPage(y);
        if (pages != null) (x, availW) = OnPage(pages, y);
    }

    /// <summary>
    /// Whether the box leaves nothing but its top edge on its first page, so that a break there would separate that
    /// edge from all of its content. Chrome makes that break in a grid, before its first row, but moves a grid whose
    /// bottom edge is all that does not fit when the next page holds it whole, rather than break between its rows.
    /// </summary>
    private bool LeavesOnlyTop(Element e, float y, float availW, float bottom, int opsStart)
    {
        var s = e.Style;
        int page = PageOf(y);
        if (s.IsGrid)
        {
            float end = s.BorderBottom + s.PaddingBottom.Resolve(availW);
            return end > Eps && PageOf(bottom - end - 0.5f) == page && bottom > FlowEnd(page) + Eps
                   && bottom - y <= _grid.Height(page + 1);
        }
        // A box whose top margin collapses with its first child's and that paints nothing starts where that child
        // does, wherever it is placed.
        if (CollapsesTop(e) && NothingDrawn(((GroupOp)_ops[opsStart]).Ops)) return false;
        bool later = false;
        for (int i = opsStart + 1; i < _ops.Count; i++)
        {
            int drawn = DrawnPage(_ops[i], page, null);
            if (drawn == 0) return false;
            if (drawn > 0) later = true;
        }
        return later;
    }

    /// <summary>
    /// Whether the ops of the box from <paramref name="opsStart"/> draw anything on the page of its top, leaving out the
    /// backgrounds of the first children that go along with it.
    /// </summary>
    private bool DrawsOnFirstPage(float y, int opsStart, HashSet<DrawOp>? along)
    {
        int page = PageOf(y);
        for (int i = opsStart + 1; i < _ops.Count; i++)
            if (DrawnPage(_ops[i], page, along) == 0) return true;
        return false;
    }

    /// <summary>
    /// Whether an op draws on <paramref name="page"/> or before (0), only on later pages (1), or nothing (-1). A float
    /// is placed beside the line it is in and moves with it, so only its content counts, not its own background.
    /// </summary>
    private int DrawnPage(DrawOp op, int page, HashSet<DrawOp>? along)
    {
        float top;
        switch (op)
        {
            case GroupOp g:
                if (along != null && along.Contains(g)) return -1;
                int result = -1;
                for (int i = op is LayerOp { Order: LayerOp.FloatOrder } ? 1 : 0; i < g.Ops.Count; i++)
                {
                    int r = DrawnPage(g.Ops[i], page, along);
                    if (r == 0) return 0;
                    result = Math.Max(result, r);
                }
                return result;
            case TextOp t: top = t.LineTop; break;
            case ImageOp im: top = im.Y; break;
            case SvgOp sv: top = sv.Y; break;
            case RectOp r: top = r.Y; break;
            case BorderRectOp b: top = b.Y; break;
            case RoundRectOp rr: top = rr.Y; break;
            case BackgroundOp bg: top = bg.ClipY; break;
            case LineOp l: top = Math.Min(l.Y1, l.Y2); break;
            case CircleOp c: top = c.Cy - c.R; break;
            default: return -1;
        }
        return PageOf(top) <= page ? 0 : 1;
    }

    /// <summary>
    /// Lays out again, with room kept for what follows its last content, a box whose bottom border and padding do not
    /// fit on the page that content ends on. The content then breaks where it can before its end, and the bottom edge
    /// goes to the next page with what follows the break, as in Chrome. When that leaves the content where it was, or
    /// moves all of it off a box that starts at the top of a page, the box is laid out as before and its bottom edge
    /// goes to the next page alone.
    /// </summary>
    private float KeepBottomEdge(Element e, float x, float y, float availW, bool shrinkToFit, PageBox? pages, int opsStart,
        ListMarker? marker, int listIndex, float bottom, out bool kept)
    {
        kept = false;
        var s = e.Style;
        float last = _lastContentBottom;
        // A grid moves whole instead (see LeavesOnlyTop), and the items of a column flex container are cut at the page
        // end rather than moved, so room at its end would only move the content inside its last item.
        if (_edgeDepth >= MaxEdgeNesting || !MayLayOutAgain || s.IsGrid || s.IsFlex && !s.IsRow || !s.Height.IsAuto || float.IsNaN(last) || last <= y
            || s.BorderBottom + s.PaddingBottom.Resolve(availW) <= Eps) return bottom;
        int page = PageOf(last - 0.5f);
        // Bottom edges taller than what is left of a page take the content nowhere it fits better.
        if (bottom <= FlowEnd(page) + Eps || PageOf(bottom - 0.5f) > page + 1 || _edgeReserve.ContainsKey(page)
            || _stuckContentBottoms.Contains(last)) return bottom;

        // The room covers the bottom edges of this box and of the boxes inside it that end with it, and the margins
        // between them, which Chrome keeps on the page with the content too.
        _edgeReserve[page] = bottom - last;
        _edgeDepth++;
        float moved;
        try { moved = LayOutAgain(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex); }
        finally
        {
            _edgeDepth--;
            _edgeReserve.Remove(page);
        }
        if (PageOf(_lastContentBottom - 0.5f) > page && !(AtPageTop(y) && !DrawsOnFirstPage(y, opsStart, null)))
        {
            kept = true;
            return moved;
        }
        _stuckContentBottoms.Add(last);
        return LayOutAgain(e, x, y, availW, shrinkToFit, pages, opsStart, marker, listIndex);
    }

    /// <summary>Drops what the box drew and lays it out again from the same state.</summary>
    private float LayOutAgain(Element e, float x, float y, float availW, bool shrinkToFit, PageBox? pages, int opsStart,
        ListMarker? marker, int listIndex)
    {
        Truncate(opsStart);
        _marker = marker;
        _currentListIndex = listIndex;
        _childPageBox = pages;
        _lastContentBottom = float.NaN;
        return LayoutBlockBoxCore(e, x, y, availW, shrinkToFit);
    }
}

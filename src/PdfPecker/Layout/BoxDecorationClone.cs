using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Pdf;

namespace PdfPecker.Layout;

/// <summary>
/// box-decoration-break: clone for block boxes split across pages or columns, as Chrome prints it: every fragment of
/// the box is a whole box with its own borders, padding, rounded corners, shadow and background, and the repeated top
/// border and padding take room at the top of each fragment after the first, as the bottom ones do at the end of each
/// fragment before the last. Boxes with the default, slice, never reach this code.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// The room a box with box-decoration-break: clone keeps in the fragments its content goes on to: <see cref="Head"/>
    /// at the top of each fragment after <see cref="FirstPage"/>, and <see cref="Foot"/> at the end of each from it on.
    /// Boxes nested in each other each keep their own, and only in the grid they are laid out in: a multi-column box
    /// inside lays out its content in columns of its own.
    /// </summary>
    private sealed record CloneRoom(PageGrid Grid, int FirstPage, float Head, float Foot, CloneRoom? Outer);

    // The cloned boxes around the content being laid out, innermost first; null when there are none.
    private CloneRoom? _cloneRoom;

    /// <summary>The room at the top of a page that the cloned boxes around the content keep for their top edges.</summary>
    private float CloneHead(int page)
    {
        float h = 0;
        for (var r = _cloneRoom; r != null; r = r.Outer)
            if (page > r.FirstPage && ReferenceEquals(r.Grid, _grid)) h += r.Head;
        return h;
    }

    /// <summary>The room at the end of a page that the cloned boxes around the content keep for their bottom edges.</summary>
    private float CloneFoot(int page)
    {
        float h = 0;
        for (var r = _cloneRoom; r != null; r = r.Outer)
            if (page >= r.FirstPage && ReferenceEquals(r.Grid, _grid)) h += r.Foot;
        return h;
    }

    /// <summary>
    /// Whether a block box gives each of its fragments its own edges. Inline boxes clone their edges on lines
    /// instead, replaced elements and rules are never split, and a fieldset's legend sits on its first fragment only.
    /// </summary>
    private bool ClonesFragments(Element e, bool replaced)
    {
        var s = e.Style;
        return s.BoxDecorationClone && _paginate && !replaced && s.Display != Display.Inline && e.Name is not ("hr" or "fieldset");
    }

    /// <summary>
    /// The room a cloned box whose content starts at <paramref name="cy"/> keeps in its fragments, or null when it
    /// keeps none: when it has no top or bottom edges, or when its edges and those of the cloned boxes around it would
    /// together take half a fragment or more, which would leave the content too little room to go on anywhere, as with
    /// a table header too tall to repeat. Counting the boxes around it keeps many nested boxes from leaving their content
    /// a sliver of each page, and so from making pages without content.
    /// </summary>
    private CloneRoom? CloneRoomFor(float cy, float head, float foot)
    {
        if (head + foot <= Eps) return null;
        int first = PageOf(cy);
        float kept = CloneHead(first + 1) + CloneFoot(first + 1);
        return kept + head + foot < _grid.MinHeight / 2 ? new CloneRoom(_grid, first, head, foot, _cloneRoom) : null;
    }

    /// <summary>
    /// Where a box <paramref name="height"/> tall from <paramref name="top"/> ends in the flow, once it leaves the room
    /// of cloned edges at each break it crosses. For a cloned box, <paramref name="room"/> is its own and the height is
    /// its content's: as in Chrome, each fragment adds its padding and borders to it. For a box inside cloned boxes,
    /// such as one with a height of its own or a float, <paramref name="room"/> is null and the height is its border
    /// box's, which goes on below the edges of the boxes around it on the next page, as a box's height goes on under a
    /// table header repeated over a split row. <see cref="_cloneRoom"/> holds the boxes around the box, not the box itself.
    /// </summary>
    private float PastCloneRoom(CloneRoom? room, float top, float height)
    {
        float head = room?.Head ?? 0, foot = room?.Foot ?? 0;
        float end = top + height;
        for (int p = Math.Max(PageOf(top), room?.FirstPage ?? 0); end > FlowEnd(p) - foot + Eps; p++)
        {
            // A box far taller than a page meets the page limit rather than adding room for millions of pages.
            CheckPageLimit(end - 0.5f);
            end += FlowStart(p + 1) + head - (FlowEnd(p) - foot);
        }
        return end;
    }

    /// <summary>Whether the content being laid out is inside a cloned box that keeps room in the current grid.</summary>
    private bool InCloneRoom => _cloneRoom != null && ReferenceEquals(_cloneRoom.Grid, _grid);

    /// <summary>
    /// Paints a cloned box from <paramref name="top"/> to <paramref name="bottom"/> as one whole box per page or column
    /// it reaches. A fragment before the last goes down to the end of its fragment's flow, and one after the first
    /// starts at the top of it, below the edges of the cloned boxes around it, as in Chrome. Returns false when the box
    /// does not cross a break, and is painted as usual.
    /// </summary>
    private bool PaintClonedFragments(GroupOp group, ComputedStyle s, PageBox? pages, float x, float top, float w, float bottom, float basis)
    {
        if (!CrossesPage(top, bottom)) return false;
        CheckPageLimit(bottom - 0.5f);
        int first = PageOf(top), last = Math.Max(first, PageOf(bottom - 0.5f));
        // Background images are laid out on all the fragments' height together (see BoxPainter.PaintClonedFragment).
        float areaH = 0;
        if (s.BackgroundImages != null)
            for (int p = first; p <= last; p++) areaH += Math.Max(0, FragmentBottom(p, last, bottom) - FragmentTop(p, first, top));
        for (int p = first; p <= last; p++)
        {
            float ft = FragmentTop(p, first, top), fb = FragmentBottom(p, last, bottom);
            if (fb - ft <= Eps) continue;
            if (pages != null)
            {
                int k = PageBox.Kind(p);
                BoxPainter.PaintClonedFragment(group, s, pages.BX[k], ft, pages.BW[k], fb - ft, areaH, _images, pages.AW[k]);
            }
            else BoxPainter.PaintClonedFragment(group, s, x, ft, w, fb - ft, areaH, _images, basis);
        }
        return true;
    }

    private float FragmentTop(int page, int first, float top) => page == first ? top : FlowStart(page);

    private float FragmentBottom(int page, int last, float bottom) => page == last ? bottom : FlowEnd(page);

    /// <summary>
    /// Paints a box with box-decoration-break: slice inside a cloned box, which it crosses a break in, page by page:
    /// each part is cut where the cloned box's content ends on its page and starts on the next, so that it leaves the
    /// cloned box's edges free, as its fragments do in Chrome. Returns false when the box does not cross a break.
    /// </summary>
    private bool PaintSlicedInClone(GroupOp group, ComputedStyle s, PageBox? pages, float x, float top, float w, float bottom, float basis,
        (float X, float Y, float W, float H)? borderGap)
    {
        if (!CrossesPage(top, bottom)) return false;
        CheckPageLimit(bottom - 0.5f);
        int first = PageOf(top), last = Math.Max(first, PageOf(bottom - 0.5f));
        for (int p = first; p <= last; p++)
        {
            float ct = p == first ? _grid.Start(p) : FlowStart(p), cb = p == last ? _grid.Start(p + 1) : FlowEnd(p);
            if (cb - ct <= Eps) continue;
            var part = new ClipOp(-ClipOp.Far, ct, 2 * ClipOp.Far, cb - ct, null);
            if (pages != null)
            {
                int k = PageBox.Kind(p);
                BoxPainter.Paint(part, s, pages.BX[k], top, pages.BW[k], bottom - top, _images, pages.AW[k], borderGap: borderGap);
            }
            else BoxPainter.Paint(part, s, x, top, w, bottom - top, _images, basis, borderGap: borderGap);
            if (part.Ops.Count > 0) group.Ops.Add(part);
        }
        return true;
    }
}

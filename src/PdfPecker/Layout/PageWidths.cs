using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

// Pages of different widths. When @page :first, :left or :right rules give pages content areas of different widths,
// Chrome lays out the content on each page at that page's width: a paragraph that runs from a wide page onto a narrow
// one has longer lines on the first, and a box's auto or percentage width is resolved again on every page it spans.
// The flow here is one long strip, so the boxes in normal flow carry their content box on each kind of page (the
// grid's first, odd and even pages), and the lines and the boxes that start on a page use that page's geometry. A
// table is sized for each kind of page too, and each row takes the columns of its page. Other boxes (flex and grid
// containers, floats, images and the like) keep the width of the page they start on, as flex containers do in Chrome.
internal sealed partial class LayoutEngine
{
    /// <summary>The geometry of a block box in normal flow on each kind of page: the first, odd and even pages of the grid.</summary>
    private sealed class PageBox
    {
        public readonly float[] X = new float[3], W = new float[3];   // the content box
        public readonly float[] BX = new float[3], BW = new float[3]; // the border box
        public readonly float[] AW = new float[3];                    // the containing block's width

        public static int Kind(int page) => page <= 0 ? 0 : (page & 1) == 1 ? 1 : 2;

        /// <summary>Whether the content box differs between some kinds of page.</summary>
        public bool Varies => X[0] != X[1] || X[0] != X[2] || W[0] != W[1] || W[0] != W[2];

        /// <summary>Whether two pages give the box the same geometry.</summary>
        public bool Same(int a, int b)
        {
            int ka = Kind(a), kb = Kind(b);
            return ka == kb || X[ka] == X[kb] && W[ka] == W[kb] && BX[ka] == BX[kb] && BW[ka] == BW[kb];
        }
    }

    // The content box of the flow being laid out on each kind of page, when the pages differ in width. LayoutFlow takes
    // it as it starts and hands it on only to the boxes and lines of its own flow, through the two fields below, so that
    // the content of floats, tables and other boxes laid out on the way never sees it.
    private PageBox? _pageBox;
    private PageBox? _childPageBox;  // the containing block of the block box LayoutBlockBox is about to lay out
    private PageBox? _inlinePageBox; // the containing block of the lines FlushInline is about to lay out

    /// <summary>The root's content box on each kind of page, when the grid's pages differ in content width; else null.</summary>
    private PageBox? RootPageBox(float width)
    {
        if (!_paginate || !_grid.Varies) return null;
        float w0 = _grid.SetupOf(0).ContentWidth, w1 = _grid.SetupOf(1).ContentWidth, w2 = _grid.SetupOf(2).ContentWidth;
        if (w0 == w1 && w0 == w2) return null;
        var box = new PageBox();
        float[] widths = { w0, w1, w2 };
        for (int k = 0; k < 3; k++)
            box.W[k] = box.BW[k] = box.AW[k] = width + widths[k] - w0; // the layout's width is the first page's
        return box;
    }

    /// <summary>The x and width of a containing block on the page a y falls on.</summary>
    private (float X, float W) OnPage(PageBox box, float y)
    {
        int k = PageBox.Kind(PageOf(y));
        return (box.X[k], box.W[k]);
    }

    /// <summary>
    /// Whether a block box takes part in the per-page widths: a box in normal flow whose content is a plain block flow.
    /// The others keep the geometry of the page they start on.
    /// </summary>
    private static bool FollowsPages(Element e, bool shrinkToFit, bool sized, bool formattingRoot)
    {
        var s = e.Style;
        return !shrinkToFit && !sized && !formattingRoot && IsPlainFlow(e) && s.Transform == null && s.Filters == null && !s.HasEffects;
    }

    /// <summary>A block box's geometry on each kind of page, from its containing block's.</summary>
    private PageBox PageGeometry(Element e, PageBox parent)
    {
        var s = e.Style;
        var box = new PageBox();
        for (int k = 0; k < 3; k++)
        {
            float aw = parent.W[k];
            float ml = s.MarginLeft.Resolve(aw), mr = s.MarginRight.Resolve(aw);
            float pl = s.PaddingLeft.Resolve(aw), pr = s.PaddingRight.Resolve(aw);
            float frame = s.BorderLeft + s.BorderRight + pl + pr;
            float frameV = s.BorderTop + s.BorderBottom + s.PaddingTop.Resolve(aw) + s.PaddingBottom.Resolve(aw);
            float contentW = Math.Max(0, FlowContentWidth(e, aw, ml, mr, frame, frameV, shrinkToFit: false));
            float used = contentW + frame;
            ml = UsedMarginLeft(e, aw, used, ml, mr, sized: false);
            box.AW[k] = aw;
            box.BX[k] = parent.X[k] + ml;
            box.BW[k] = used;
            box.X[k] = box.BX[k] + s.BorderLeft + pl;
            box.W[k] = contentW;
        }
        return box;
    }

    /// <summary>
    /// Paints the background and borders of a box that spans pages on which it has different geometry: each page gets
    /// the box as wide as it is there, cut at the page's edges, so a break leaves it open as a single box would be.
    /// </summary>
    private void PaintOnPages(GroupOp group, ComputedStyle s, PageBox box, float y, float bottom, int sides = BoxPainter.AllSides, bool collapsedCell = false)
    {
        int first = PageOf(y), last = Math.Max(first, PageOf(bottom - 0.5f));
        for (int p = first; p <= last; p++)
        {
            int k = PageBox.Kind(p);
            float top = _grid.Start(p);
            var part = new ClipOp(-ClipOp.Far, top, 2 * ClipOp.Far, _grid.Start(p + 1) - top, null);
            BoxPainter.Paint(part, s, box.BX[k], y, box.BW[k], bottom - y, _images, box.AW[k], sides, collapsedCell);
            if (part.Ops.Count > 0) group.Ops.Add(part);
        }
    }

    /// <summary>Whether a box from y to bottom has the same geometry on every page it spans.</summary>
    private bool SameOnPages(PageBox box, float y, float bottom)
    {
        if (!CrossesPage(y, bottom)) return true;
        int first = PageOf(y), last = Math.Max(first, PageOf(bottom - 0.5f));
        for (int p = first + 1; p <= last; p++)
            if (!box.Same(first, p)) return false;
        return true;
    }
}

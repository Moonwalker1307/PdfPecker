using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// Vertical writing modes. A block whose writing mode turns it against its parent's (an orthogonal flow) is laid out as
/// horizontal text in a box with its height and width swapped, then turned a quarter turn into place. Lines therefore
/// run down the page (vertical-rl, vertical-lr, sideways-rl) or up it (sideways-lr) with every glyph turned, as Chrome
/// draws Latin text in those modes; upright CJK glyphs and the left-to-right line order of vertical-lr are not done.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// The quarter turn, in degrees clockwise, between a box's writing mode and its parent's: 0 when they agree, and for
    /// the root and body, whose writing mode is that of the page, and tables.
    /// </summary>
    private int OrthogonalTurn(Element e)
    {
        var s = e.Style;
        if (e.Parent?.Style is not { } ps || e.Name is "html" or "body" || s.Display == Display.Table || _turning.Contains(e)
            || _turning.Count >= MaxTurnedNesting) return 0;
        int turn = s.WritingModeTurn - ps.WritingModeTurn;
        return turn switch { 90 or -270 => 90, -90 or 270 => -90, _ => 0 };
    }

    /// <summary>
    /// How many turned boxes may be nested; deeper ones are laid out unturned. Each level is laid out once to measure it
    /// and once more where it is placed, so deep nesting of alternating writing modes multiplies the work.
    /// </summary>
    private const int MaxTurnedNesting = 8;

    /// <summary>The boxes being laid out turned, which inside that layout are ordinary boxes.</summary>
    private readonly HashSet<Element> _turning = new();

    /// <summary>
    /// Lays out a block-level box in an orthogonal flow at (<paramref name="x"/>, <paramref name="y"/>) and returns its
    /// bottom margin edge. Its height is the length of its lines: the height it is given, or else as long as its
    /// content wants, up to the page height; its width is the depth of its lines unless it is given one.
    /// </summary>
    private float LayoutOrthogonal(Element e, float x, float y, float availW, int turn)
    {
        var s = e.Style;
        float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(availW);
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(availW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(availW);
        var c = CaptureOrthogonal(e, turn, availW, _grid.Height(PageOf(y)));
        float lineLength = c.Width, depth = c.Height;
        float px = x + ml, py = y + mt;
        // The turned box is drawn whole, so one that fits on a page starts on the next page rather than across the break.
        if (_paginate && CrossesPage(py, py + lineLength) && !AtPageTop(y) && lineLength <= _grid.Height(PageOf(y) + 1))
            py = NextPage(y) + mt;

        int start = _ops.Count;
        Place(c, px, py);
        var layer = WrapLayer(start, 0, stackingContext: false);
        layer.Transform = Matrix.Rotate(turn);
        // The origin that turns the horizontal box at (px, py) into the box of the swapped size at the same place.
        float half = (turn > 0 ? depth : lineLength) / 2;
        layer.OriginX = px + half;
        layer.OriginY = py + half;

        (_lastBoxX, _lastBoxTop, _lastBoxW, _lastBoxH) = (px, py, depth, lineLength);
        _lastBoxRight = Math.Max(_lastBoxRight, px + depth);
        _lastBaseline = float.NaN;
        _lastContentBottom = py + lineLength;
        return py + lineLength + mb;
    }

    /// <summary>Min-content and max-content widths of a box in an orthogonal flow: the depth of its lines.</summary>
    private (float Min, float Max) OrthogonalIntrinsic(Element e, int turn)
    {
        float depth = CaptureOrthogonal(e, turn, 0, _grid.Height(0)).Height;
        return (depth, depth);
    }

    /// <summary>
    /// Lays out the box's content horizontally, with its height as the line length, in a style turned to match: the
    /// sides of its padding and borders follow the turn, and its margins stay outside.
    /// </summary>
    private Captured CaptureOrthogonal(Element e, int turn, float availW, float pageHeight)
    {
        var s = e.Style;
        float frameH = s.PaddingLeft.Resolve(availW) + s.PaddingRight.Resolve(availW) + s.BorderLeft + s.BorderRight;
        float frameV = s.PaddingTop.Resolve(availW) + s.PaddingBottom.Resolve(availW) + s.BorderTop + s.BorderBottom;
        // A flex or grid container's sizes are for the box as it stands on the page.
        bool hasW = _widthOverride.Remove(e, out float overrideW), hasH = _heightOverride.Remove(e, out float overrideH);
        float? lineLength = hasH ? overrideH
            : !s.Height.IsAuto && !s.Height.IsPercent ? s.Height.Value + (s.BorderBox ? 0 : frameV)
            : null;
        float? depth = hasW ? overrideW
            : !s.Width.IsAuto && (!s.Width.IsPercent || availW > 0) ? s.Width.Resolve(availW) + (s.BorderBox ? 0 : frameH)
            : null;
        // The widths measured for the box on the page are its depth; laid out turned, it measures its lines instead.
        bool measured = _intrinsicCache.Remove(e, out var pageWidths);
        e.Style = Turned(s, turn);
        _turning.Add(e);
        try
        {
            return CaptureBox(e, lineLength ?? pageHeight, shrinkToFit: lineLength == null, lineLength, depth);
        }
        finally
        {
            e.Style = s;
            _turning.Remove(e);
            _intrinsicCache.Remove(e);
            if (measured) _intrinsicCache[e] = pageWidths;
            if (hasW) _widthOverride[e] = overrideW;
            if (hasH) _heightOverride[e] = overrideH;
        }
    }

    /// <summary>
    /// The style a box in an orthogonal flow is laid out with: no margins (they are applied on the page), sizes left to
    /// the layout, and each side's padding and border taken from the side the turn brings it to.
    /// </summary>
    // The physical side (top 0, right 1, bottom 2, left 3) that each side of the turned box, in the same order, comes from.
    private static readonly int[] ClockwiseSides = { 1, 2, 3, 0 }, AnticlockwiseSides = { 3, 0, 1, 2 };

    private static ComputedStyle Turned(ComputedStyle s, int turn)
    {
        var t = s.Clone();
        t.MarginTop = t.MarginRight = t.MarginBottom = t.MarginLeft = Length.Zero;
        t.Width = t.Height = t.MinWidth = t.MaxWidth = t.MaxHeight = Length.Auto;
        t.MinHeight = Length.Zero;
        t.AspectRatio = 0;
        (t.OverflowX, t.OverflowY) = (s.OverflowY, s.OverflowX);
        // Turned clockwise, the horizontal box's left side becomes the top, its top the right, and so on; turned
        // anticlockwise, its left side becomes the bottom.
        var from = turn > 0 ? ClockwiseSides : AnticlockwiseSides;
        Length[] padding = { s.PaddingTop, s.PaddingRight, s.PaddingBottom, s.PaddingLeft };
        float[] widths = { s.BorderTopWidth, s.BorderRightWidth, s.BorderBottomWidth, s.BorderLeftWidth };
        BorderStyle[] styles = { s.BorderTopStyle, s.BorderRightStyle, s.BorderBottomStyle, s.BorderLeftStyle };
        uint?[] colors = { s.BorderTopColor, s.BorderRightColor, s.BorderBottomColor, s.BorderLeftColor };
        (t.PaddingTop, t.PaddingRight, t.PaddingBottom, t.PaddingLeft) = (padding[from[0]], padding[from[1]], padding[from[2]], padding[from[3]]);
        (t.BorderTopWidth, t.BorderRightWidth, t.BorderBottomWidth, t.BorderLeftWidth) = (widths[from[0]], widths[from[1]], widths[from[2]], widths[from[3]]);
        (t.BorderTopStyle, t.BorderRightStyle, t.BorderBottomStyle, t.BorderLeftStyle) = (styles[from[0]], styles[from[1]], styles[from[2]], styles[from[3]]);
        (t.BorderTopColor, t.BorderRightColor, t.BorderBottomColor, t.BorderLeftColor) = (colors[from[0]], colors[from[1]], colors[from[2]], colors[from[3]]);
        if (s.BorderRadii is { } radii)
        {
            // Corners turn the same way (top-left, top-right, bottom-right, bottom-left), with their two radii swapped.
            var turned = new Length[8];
            for (int k = 0; k < 4; k++)
            {
                int p = turn > 0 ? (k + 1) % 4 : (k + 3) % 4;
                turned[k * 2] = radii[p * 2 + 1];
                turned[k * 2 + 1] = radii[p * 2];
            }
            t.BorderRadii = turned;
        }
        return t;
    }

    /// <summary>
    /// Gives a table cell whose writing mode turns it against its row an anonymous block of that writing mode around its
    /// content, so that the cell itself stays in the table's grid and the block is laid out turned inside it.
    /// </summary>
    private static void WrapOrthogonalCell(Element td, ComputedStyle row)
    {
        var flow = td.Style.CreateChild();
        flow.Display = Display.Block;
        var block = new Element("::cell-flow") { Generated = true, Parent = td, Style = flow };
        foreach (var child in td.Children)
        {
            child.Parent = block;
            block.Children.Add(child);
        }
        td.Children.Clear();
        td.Children.Add(block);
        var cell = td.Style.Clone();
        cell.WritingMode = row.WritingMode;
        td.Style = cell;
    }
}

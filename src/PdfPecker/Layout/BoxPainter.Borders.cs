using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>The border sides: square ones side by side, uniform ones as one stroked rectangle, rounded ones as rings.</summary>
internal static partial class BoxPainter
{
    // Indexes of the sides for the 3D border styles, in the order of the border shorthand.
    private const int SideTop = 0, SideRight = 1, SideBottom = 2, SideLeft = 3;

    /// <summary>A double border thinner than this (three bands of three quarters of a pixel) is drawn solid.</summary>
    private const float MinDoubleWidth = 2.25f;

    /// <summary>
    /// A groove or ridge thinner than this (two pixels) has no room for its two shades; Chrome then draws it solid in its
    /// colour, unshaded, on every side.
    /// </summary>
    private const float MinGrooveWidth = 1.5f;

    /// <summary>One side of a border: its width, style and colour, with the border's colour already resolved.</summary>
    private readonly record struct BorderSide(float Width, BorderStyle Style, uint Color)
    {
        /// <summary>True when the side paints anything.</summary>
        public bool Shows => Width > 0 && Style != BorderStyle.None && CssColor.IsVisible(Color);

        /// <summary>True for a double border wide enough for its two bands.</summary>
        public bool IsDoubled => Style == BorderStyle.Double && Width >= MinDoubleWidth;

        public bool IsDashedOrDotted => Style is BorderStyle.Dashed or BorderStyle.Dotted;
    }

    /// <summary>The four sides of a box's border, indexed like the border shorthand (top, right, bottom, left).</summary>
    private readonly struct BorderSides
    {
        public readonly BorderSide Top, Right, Bottom, Left;

        public BorderSides(ComputedStyle s)
        {
            Top = new BorderSide(s.BorderTop, s.BorderTopStyle, s.BorderTopColor ?? s.Color);
            Right = new BorderSide(s.BorderRight, s.BorderRightStyle, s.BorderRightColor ?? s.Color);
            Bottom = new BorderSide(s.BorderBottom, s.BorderBottomStyle, s.BorderBottomColor ?? s.Color);
            Left = new BorderSide(s.BorderLeft, s.BorderLeftStyle, s.BorderLeftColor ?? s.Color);
        }

        public BorderSide this[int side] => side switch { SideTop => Top, SideRight => Right, SideBottom => Bottom, _ => Left };

        public int ShownCount => (Top.Shows ? 1 : 0) + (Right.Shows ? 1 : 0) + (Bottom.Shows ? 1 : 0) + (Left.Shows ? 1 : 0);
    }

    /// <summary>
    /// Paints side <paramref name="from"/> (<see cref="Top"/>, <see cref="Right"/>, <see cref="Bottom"/> or
    /// <see cref="Left"/>) of a box's border as the band (x, y, w, h) on side <paramref name="at"/> of a table cell: a
    /// row's, row group's or the table's border that wins a collapsed-border conflict there.
    /// </summary>
    public static void PaintCollapsedBand(GroupOp group, ComputedStyle s, int from, int at, float x, float y, float w, float h)
    {
        var side = new BorderSides(s)[SideIndex(from)];
        Side(group, Collapsed(side.Style, collapsed: true), side.Width, side.Color, x, y, w, h, horizontal: at is Top or Bottom, SideIndex(at));
    }

    private static int SideIndex(int side) => side switch { Top => SideTop, Right => SideRight, Bottom => SideBottom, _ => SideLeft };

    private static void PaintBorders(GroupOp group, ComputedStyle s, float x, float y, float w, float h, int sides, bool collapsedCell, CornerRadii? radii)
    {
        bool hasArea = w > 0 && h > 0;
        if (radii is { } r && hasArea)
        {
            PaintRoundedBorder(group, s, x, y, w, h, r);
            return;
        }
        bool wholeBox = sides == AllSides && !collapsedCell;
        // Sides of the 3D styles differ in shade, so they meet along mitred corners like the sides of a rounded border.
        if (wholeBox && hasArea && (Is3D(s.BorderTopStyle) || Is3D(s.BorderRightStyle) || Is3D(s.BorderBottomStyle) || Is3D(s.BorderLeftStyle)))
        {
            PaintRoundedBorder(group, s, x, y, w, h, default);
            return;
        }
        // Chrome fills collapsed table borders side by side, and strokes other boxes' uniform borders.
        if (wholeBox && IsUniformSolid(s, w, h))
        {
            group.Ops.Add(new BorderRectOp(x, y, w, h, s.BorderTop, s.BorderTopColor ?? s.Color));
            return;
        }
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        if ((sides & Top) != 0) Side(group, Collapsed(s.BorderTopStyle, collapsedCell), bt, s.BorderTopColor ?? s.Color, x, y, w, bt, horizontal: true, SideTop);
        if ((sides & Bottom) != 0) Side(group, Collapsed(s.BorderBottomStyle, collapsedCell), bb, s.BorderBottomColor ?? s.Color, x, y + h - bb, w, bb, horizontal: true, SideBottom);
        if ((sides & Left) != 0) Side(group, Collapsed(s.BorderLeftStyle, collapsedCell), bl, s.BorderLeftColor ?? s.Color, x, y, bl, h, horizontal: false, SideLeft);
        if ((sides & Right) != 0) Side(group, Collapsed(s.BorderRightStyle, collapsedCell), br, s.BorderRightColor ?? s.Color, x + w - br, y, br, h, horizontal: false, SideRight);
    }

    /// <summary>True when all four sides are solid, visible, of one width and colour, and leave room inside the box.</summary>
    private static bool IsUniformSolid(ComputedStyle s, float w, float h)
    {
        float bt = s.BorderTop;
        uint topColor = s.BorderTopColor ?? s.Color;
        return bt > 0 && bt == s.BorderRight && bt == s.BorderBottom && bt == s.BorderLeft && 2 * bt < w && 2 * bt < h
            && s.BorderTopStyle == BorderStyle.Solid && s.BorderRightStyle == BorderStyle.Solid
            && s.BorderBottomStyle == BorderStyle.Solid && s.BorderLeftStyle == BorderStyle.Solid
            && CssColor.IsVisible(topColor) && (s.BorderRightColor ?? s.Color) == topColor
            && (s.BorderBottomColor ?? s.Color) == topColor && (s.BorderLeftColor ?? s.Color) == topColor;
    }

    /// <summary>True for groove, ridge, inset and outset: the border styles drawn in two shades of their colour.</summary>
    public static bool Is3D(BorderStyle style) => style is BorderStyle.Groove or BorderStyle.Ridge or BorderStyle.Inset or BorderStyle.Outset;

    // In the collapsing border model inset means ridge and outset means groove (CSS 2.1 §17.6.2.1).
    private static BorderStyle Collapsed(BorderStyle style, bool collapsed)
        => !collapsed ? style : style switch { BorderStyle.Inset => BorderStyle.Ridge, BorderStyle.Outset => BorderStyle.Groove, _ => style };

    // How Chrome shades the 3D styles: the dark side loses this much of its brightest channel, and a colour below the
    // luminance threshold is too dark to darken, so its light side is lightened by the same amount instead.
    private const float ShadeAmount = 0.33f, LightenBelowLuminance = 0.0622f;
    private const uint BlackLightened = 0x545454;

    /// <summary>
    /// The colour of one side of a 3D border, as Chrome shades it: the top and left of an inset border (and the bottom
    /// and right of an outset one) are darkened; the other sides keep the colour, unless it is too dark to show a
    /// difference, when they are lightened instead. A groove is an inset outer half with an outset inner half, a ridge
    /// the reverse.
    /// </summary>
    public static uint Shade3D(BorderStyle style, int side, uint color, bool innerHalf = false)
    {
        if (style is BorderStyle.Groove or BorderStyle.Ridge)
            style = (style == BorderStyle.Groove) != innerHalf ? BorderStyle.Inset : BorderStyle.Outset;
        if (style is not (BorderStyle.Inset or BorderStyle.Outset)) return color;
        bool darken = (side is SideTop or SideLeft) == (style == BorderStyle.Inset);
        uint a = color & 0xFF000000;
        float r = (color >> 16 & 0xFF) / 255f, g = (color >> 8 & 0xFF) / 255f, b = (color & 0xFF) / 255f;
        float v = Math.Max(r, Math.Max(g, b));
        if (darken)
        {
            float k = v > 0 ? Math.Max(0, (v - ShadeAmount) / v) : 0;
            return a | Channel(r * k) << 16 | Channel(g * k) << 8 | Channel(b * k);
        }
        // How dark the colour looks; the weights and the threshold reproduce which colours Chrome lightens (grey up to
        // #363636, red up to #7f0000, green up to #004000, and nearly every blue).
        float lum = 0.215f * MathF.Pow(r, 1.8f) + 0.73f * MathF.Pow(g, 1.8f) + 0.055f * MathF.Pow(b, 1.8f);
        if (lum >= LightenBelowLuminance) return color;
        float m = Math.Min(1, v + ShadeAmount) / Math.Max(v, 1e-6f);
        if (v == 0) return a | BlackLightened;
        return a | Channel(r * m) << 16 | Channel(g * m) << 8 | Channel(b * m);

        static uint Channel(float c) => (uint)Math.Clamp(c * 255.999f, 0, 255);
    }

    /// <summary>
    /// Paints the borders of a box with rounded corners: one ring when the sides are alike, else each side as the part of
    /// the ring between the lines from its outer to its inner corners.
    /// </summary>
    private static void PaintRoundedBorder(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii r)
    {
        var sides = new BorderSides(s);
        float bt = sides.Top.Width, br = sides.Right.Width, bb = sides.Bottom.Width, bl = sides.Left.Width;
        float ix = x + bl, iy = y + bt, iw = Math.Max(0, w - bl - br), ih = Math.Max(0, h - bt - bb);
        var inner = r.Inset(bt, br, bb, bl);
        int shown = sides.ShownCount;
        if (shown == 0) return;
        bool sameColor = true, allSolid = true, alike = shown == 4, anyDouble = false;
        uint first = 0;
        for (int i = 0, n = 0; i < 4; i++)
        {
            var side = sides[i];
            if (!side.Shows) continue;
            if (n++ == 0) first = side.Color;
            else if (side.Color != first) sameColor = false;
            if (side.Style is not (BorderStyle.Solid or BorderStyle.Double)) allSolid = false;
            if (side.Width != sides.Top.Width || side.Style != sides.Top.Style || side.Color != sides.Top.Color) alike = false;
            if (side.IsDoubled) anyDouble = true;
        }
        if (alike && sides.Top.IsDashedOrDotted && 2 * bt < w && 2 * bt < h)
        {
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, first) { StrokeWidth = bt, Style = sides.Top.Style });
            return;
        }
        if (sameColor && allSolid && !anyDouble)
        {
            // Sides without a border have zero width, so one ring covers exactly the borders that show.
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, first).WithHole(ix, iy, iw, ih, inner));
            return;
        }
        // A double border is two rings, each a third of the border's width, with the middle third left open; the
        // dividing curves follow the corners inset by a third and by two thirds of the widths.
        float ta = bt / 3, ra = br / 3, ba = bb / 3, la = bl / 3;
        RoundRectOp OuterThird(uint color, float[]? clip)
            => new RoundRectOp(x, y, w, h, r, color) { Clip = clip }
                .WithHole(x + la, y + ta, Math.Max(0, w - la - ra), Math.Max(0, h - ta - ba), r.Inset(ta, ra, ba, la));
        RoundRectOp InnerThird(uint color, float[]? clip)
            => new RoundRectOp(x + 2 * la, y + 2 * ta, Math.Max(0, w - 2 * la - 2 * ra), Math.Max(0, h - 2 * ta - 2 * ba),
                    r.Inset(2 * ta, 2 * ra, 2 * ba, 2 * la) ?? default, color) { Clip = clip }
                .WithHole(ix, iy, iw, ih, inner);
        if (alike && anyDouble)
        {
            group.Ops.Add(OuterThird(first, null));
            group.Ops.Add(InnerThird(first, null));
            return;
        }
        // Each side is the ring cut along the lines from the outer corners through the inner ones, continued inward past
        // the curves (as Chrome splits the corners). Every op gets a polygon of its own, as ops offset their clip in place.
        var corners = new MitreCorners(x, y, w, h, bt, br, bb, bl);
        for (int i = 0; i < 4; i++)
        {
            var side = sides[i];
            if (!side.Shows) continue;
            if (side.IsDashedOrDotted && 2 * side.Width < w && 2 * side.Width < h)
            {
                // Dashes along the middle of this side's border, cut to its part of the ring.
                group.Ops.Add(new RoundRectOp(x, y, w, h, r, side.Color) { StrokeWidth = side.Width, Style = side.Style, Clip = corners.SidePolygon(i) });
                continue;
            }
            if (side.Style is BorderStyle.Groove or BorderStyle.Ridge && side.Width >= MinGrooveWidth)
            {
                // The outer and inner halves of the ring, each in its own shade.
                float th = bt / 2, rh = br / 2, bh = bb / 2, lh = bl / 2;
                group.Ops.Add(new RoundRectOp(x, y, w, h, r, Shade3D(side.Style, i, side.Color)) { Clip = corners.SidePolygon(i) }
                    .WithHole(x + lh, y + th, Math.Max(0, w - lh - rh), Math.Max(0, h - th - bh), r.Inset(th, rh, bh, lh)));
                group.Ops.Add(new RoundRectOp(x + lh, y + th, Math.Max(0, w - lh - rh), Math.Max(0, h - th - bh), r.Inset(th, rh, bh, lh) ?? default,
                        Shade3D(side.Style, i, side.Color, innerHalf: true)) { Clip = corners.SidePolygon(i) }
                    .WithHole(ix, iy, iw, ih, inner));
                continue;
            }
            if (side.Style is BorderStyle.Inset or BorderStyle.Outset)
            {
                group.Ops.Add(new RoundRectOp(x, y, w, h, r, Shade3D(side.Style, i, side.Color)) { Clip = corners.SidePolygon(i) }.WithHole(ix, iy, iw, ih, inner));
                continue;
            }
            if (side.IsDoubled)
            {
                group.Ops.Add(OuterThird(side.Color, corners.SidePolygon(i)));
                group.Ops.Add(InnerThird(side.Color, corners.SidePolygon(i)));
                continue;
            }
            group.Ops.Add(new RoundRectOp(x, y, w, h, r, side.Color) { Clip = corners.SidePolygon(i) }.WithHole(ix, iy, iw, ih, inner));
        }
    }

    /// <summary>
    /// The lines along which the sides of a border with differing sides meet: from each outer corner of the box through
    /// its inner corner, continued inward by half the box's smaller dimension so that they reach past the corner curves.
    /// </summary>
    private readonly struct MitreCorners
    {
        private readonly float _x0, _y0, _x1, _y1;
        private readonly (float X, float Y) _topLeft, _topRight, _bottomRight, _bottomLeft;

        public MitreCorners(float x, float y, float w, float h, float top, float right, float bottom, float left)
        {
            _x0 = x; _y0 = y; _x1 = x + w; _y1 = y + h;
            float reach = Math.Min(w, h) / 2;
            (float X, float Y) Toward(float cx, float cy, float dx, float dy)
            {
                float k = reach / Math.Max(Math.Max(dx, dy), 0.01f);
                return (cx + Math.Sign(x + w / 2 - cx) * dx * k, cy + Math.Sign(y + h / 2 - cy) * dy * k);
            }
            _topLeft = Toward(_x0, _y0, left, top);
            _topRight = Toward(_x1, _y0, right, top);
            _bottomRight = Toward(_x1, _y1, right, bottom);
            _bottomLeft = Toward(_x0, _y1, left, bottom);
        }

        /// <summary>A new polygon (x, y pairs) of the part of the box that belongs to <paramref name="side"/>.</summary>
        public float[] SidePolygon(int side) => side switch
        {
            SideTop => [_x0, _y0, _x1, _y0, _topRight.X, _topRight.Y, _topLeft.X, _topLeft.Y],
            SideRight => [_x1, _y0, _x1, _y1, _bottomRight.X, _bottomRight.Y, _topRight.X, _topRight.Y],
            SideBottom => [_x1, _y1, _x0, _y1, _bottomLeft.X, _bottomLeft.Y, _bottomRight.X, _bottomRight.Y],
            _ => [_x0, _y1, _x0, _y0, _topLeft.X, _topLeft.Y, _bottomLeft.X, _bottomLeft.Y],
        };
    }

    /// <summary>
    /// Paints one side of a border as the rectangle (x, y, w, h). <paramref name="which"/> (0 top, 1 right, 2 bottom,
    /// 3 left) shades the 3D styles; without it they are drawn solid, as is a groove or ridge too thin for two shades.
    /// </summary>
    public static void Side(GroupOp group, BorderStyle style, float width, uint color, float x, float y, float w, float h, bool horizontal, int which = -1)
    {
        if (width <= 0 || style == BorderStyle.None || !CssColor.IsVisible(color)) return;
        switch (style)
        {
            case BorderStyle.Groove or BorderStyle.Ridge when which >= 0 && width >= MinGrooveWidth:
                PaintGroove(group, style, which, color, x, y, w, h, horizontal);
                break;
            case BorderStyle.Inset or BorderStyle.Outset when which >= 0:
                group.Ops.Add(new RectOp(x, y, w, h, Shade3D(style, which, color)));
                break;
            case BorderStyle.Dashed:
            case BorderStyle.Dotted:
                if (horizontal) group.Ops.Add(new LineOp(x, y + h / 2, x + w, y + h / 2, h, color, style == BorderStyle.Dotted));
                else group.Ops.Add(new LineOp(x + w / 2, y, x + w / 2, y + h, w, color, style == BorderStyle.Dotted));
                break;
            case BorderStyle.Double when width >= MinDoubleWidth:
                PaintDouble(group, color, x, y, w, h, horizontal);
                break;
            default:
                group.Ops.Add(new RectOp(x, y, w, h, color));
                break;
        }
    }

    /// <summary>A groove or ridge side: two halves in their shades, the half nearer the outside of the box first.</summary>
    private static void PaintGroove(GroupOp group, BorderStyle style, int which, uint color, float x, float y, float w, float h, bool horizontal)
    {
        uint outer = Shade3D(style, which, color), inner = Shade3D(style, which, color, innerHalf: true);
        bool outerFirst = which is SideTop or SideLeft;
        uint firstHalf = outerFirst ? outer : inner, secondHalf = outerFirst ? inner : outer;
        if (horizontal)
        {
            group.Ops.Add(new RectOp(x, y, w, h / 2, firstHalf));
            group.Ops.Add(new RectOp(x, y + h / 2, w, h / 2, secondHalf));
        }
        else
        {
            group.Ops.Add(new RectOp(x, y, w / 2, h, firstHalf));
            group.Ops.Add(new RectOp(x + w / 2, y, w / 2, h, secondHalf));
        }
    }

    /// <summary>A double side: two bands of a third of the width each, with the middle third left open.</summary>
    private static void PaintDouble(GroupOp group, uint color, float x, float y, float w, float h, bool horizontal)
    {
        if (horizontal)
        {
            float t = h / 3;
            group.Ops.Add(new RectOp(x, y, w, t, color));
            group.Ops.Add(new RectOp(x, y + h - t, w, t, color));
        }
        else
        {
            float t = w / 3;
            group.Ops.Add(new RectOp(x, y, t, h, color));
            group.Ops.Add(new RectOp(x + w - t, y, t, h, color));
        }
    }
}

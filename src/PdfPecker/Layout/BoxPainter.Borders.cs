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

    /// <summary>A side with more dashes than this keeps the plain pattern instead of a fitted one.</summary>
    private const float MaxBorderDashes = 10000;

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

        /// <summary>Four alike sides, as an outline has.</summary>
        public BorderSides(BorderSide all) => Top = Right = Bottom = Left = all;

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
            PaintRoundedBorder(group, new BorderSides(s), x, y, w, h, r);
            return;
        }
        bool wholeBox = sides == AllSides && !collapsedCell;
        // Sides of the 3D styles differ in shade, so they meet along mitred corners like the sides of a rounded border.
        if (wholeBox && hasArea && (Is3D(s.BorderTopStyle) || Is3D(s.BorderRightStyle) || Is3D(s.BorderBottomStyle) || Is3D(s.BorderLeftStyle)))
        {
            PaintRoundedBorder(group, new BorderSides(s), x, y, w, h, default);
            return;
        }
        // Solid sides of different colours meet along mitred corners too, as Acid2's nose shows.
        if (wholeBox && hasArea && SolidSidesDiffer(s))
        {
            PaintRoundedBorder(group, new BorderSides(s), x, y, w, h, default);
            return;
        }
        // Chrome fills collapsed table borders side by side, and strokes other boxes' uniform borders.
        if (wholeBox && IsUniformSolid(s, w, h))
        {
            group.Ops.Add(new BorderRectOp(x, y, w, h, s.BorderTop, s.BorderTopColor ?? s.Color));
            return;
        }
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        bool trim = wholeBox && SharesTranslucentCorners(new BorderSides(s));
        if ((sides & Top) != 0) Side(group, Collapsed(s.BorderTopStyle, collapsedCell), bt, s.BorderTopColor ?? s.Color, x, y, w, bt, horizontal: true, SideTop);
        if ((sides & Bottom) != 0) Side(group, Collapsed(s.BorderBottomStyle, collapsedCell), bb, s.BorderBottomColor ?? s.Color, x, y + h - bb, w, bb, horizontal: true, SideBottom);
        if ((sides & Left) != 0) Side(group, Collapsed(s.BorderLeftStyle, collapsedCell), bl, s.BorderLeftColor ?? s.Color, x, y, bl, h, horizontal: false, SideLeft, trim);
        if ((sides & Right) != 0) Side(group, Collapsed(s.BorderRightStyle, collapsedCell), br, s.BorderRightColor ?? s.Color, x + w - br, y, br, h, horizontal: false, SideRight, trim);
    }

    /// <summary>
    /// True when all four sides are the same translucent dashes or dots: the corners, where a horizontal and a vertical
    /// side both start with a dash or a dot, would then show darker than the rest, so the vertical sides leave them out.
    /// </summary>
    private static bool SharesTranslucentCorners(in BorderSides sides)
    {
        var top = sides.Top;
        return top.Shows && top.IsDashedOrDotted && (top.Color >> 24) != 0xFF
            && sides.Right == top && sides.Bottom == top && sides.Left == top;
    }

    /// <summary>True when the sides that show are all solid and two that meet at a corner differ in colour.</summary>
    private static bool SolidSidesDiffer(ComputedStyle s)
    {
        var sides = new BorderSides(s);
        bool differ = false;
        for (int i = 0; i < 4; i++)
        {
            var side = sides[i];
            if (!side.Shows) continue;
            if (side.Style != BorderStyle.Solid) return false;
            var next = sides[(i + 1) % 4];
            if (next.Shows && next.Color != side.Color) differ = true;
        }
        return differ;
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

    // How Chrome shades the 3D styles: the dark side loses this much of its brightest channel and the light side gains
    // it. A colour as dark as #202020 or darker (by squared distance from black) is lightened first and then shaded from
    // there, and one nearer white than #ebebeb keeps its own colour on the light side.
    private const float ShadeAmount = 0.33f;
    private const int TooDark = 3 * 0x20 * 0x20, TooLight = 3 * (0xFF - 0xEB) * (0xFF - 0xEB);

    /// <summary>
    /// The colour of one side of a 3D border, as Chrome 153 shades it: the top and left of an inset border (and the
    /// bottom and right of an outset one) are darkened, the other sides lightened, each by scaling the colour so that its
    /// brightest channel moves by a third. A groove is an inset outer half with an outset inner half, a ridge the
    /// reverse.
    /// </summary>
    public static uint Shade3D(BorderStyle style, int side, uint color, bool innerHalf = false)
    {
        if (style is BorderStyle.Groove or BorderStyle.Ridge)
            style = (style == BorderStyle.Groove) != innerHalf ? BorderStyle.Inset : BorderStyle.Outset;
        if (style is not (BorderStyle.Inset or BorderStyle.Outset)) return color;
        bool darken = (side is SideTop or SideLeft) == (style == BorderStyle.Inset);
        int ri = (int)(color >> 16 & 0xFF), gi = (int)(color >> 8 & 0xFF), bi = (int)(color & 0xFF);
        if (ri * ri + gi * gi + bi * bi <= TooDark)
        {
            // Too dark to darken: both sides start from the colour lightened once.
            color = Scale(color, lighten: true);
            return darken ? color : Scale(color, lighten: true);
        }
        if (!darken && (0xFF - ri) * (0xFF - ri) + (0xFF - gi) * (0xFF - gi) + (0xFF - bi) * (0xFF - bi) < TooLight) return color;
        return Scale(color, !darken);

        static uint Scale(uint color, bool lighten)
        {
            float r = (color >> 16 & 0xFF) / 255f, g = (color >> 8 & 0xFF) / 255f, b = (color & 0xFF) / 255f;
            float v = Math.Max(r, Math.Max(g, b));
            // Black has no channel to scale; Chrome lightens it to a third of white.
            if (v == 0) return lighten ? color & 0xFF000000 | 0x545454 : color;
            float m = lighten ? Math.Min(1, v + ShadeAmount) / v : Math.Max(0, v - ShadeAmount) / v;
            return color & 0xFF000000 | Channel(r * m) << 16 | Channel(g * m) << 8 | Channel(b * m);
        }

        static uint Channel(float c) => (uint)Math.Clamp(c * 255.999f, 0, 255);
    }

    /// <summary>
    /// Paints the borders of a box with rounded corners: one ring when the sides are alike, else each side as the part of
    /// the ring between the lines from its outer to its inner corners.
    /// </summary>
    private static void PaintRoundedBorder(GroupOp group, in BorderSides sides, float x, float y, float w, float h, CornerRadii r)
    {
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
    /// its inner corner, continued inward by half the box's smaller dimension so that they reach past the corner curves,
    /// and at least to the inner corner.
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
                // At least to the inner corner: borders wider than half the box, around little or no content, meet there.
                float k = Math.Max(reach / Math.Max(Math.Max(dx, dy), 0.01f), 1);
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
    public static void Side(GroupOp group, BorderStyle style, float width, uint color, float x, float y, float w, float h, bool horizontal, int which = -1,
        bool trimCorners = false)
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
            {
                var line = horizontal
                    ? new LineOp(x, y + h / 2, x + w, y + h / 2, h, color, style == BorderStyle.Dotted)
                    : new LineOp(x + w / 2, y, x + w / 2, y + h, w, color, style == BorderStyle.Dotted);
                FitDashes(line, width, horizontal ? w : h, trimCorners);
                group.Ops.Add(line);
                break;
            }
            case BorderStyle.Double when width >= MinDoubleWidth:
                PaintDouble(group, color, x, y, w, h, horizontal);
                break;
            default:
                group.Ops.Add(new RectOp(x, y, w, h, color));
                break;
        }
    }

    /// <summary>
    /// Gives a dashed or dotted border side the pattern Chrome draws: dashes of two widths with gaps of one (three and two
    /// below three pixels), or round dots a width apart, with the gaps spread so that the side starts and ends on a dash
    /// or a dot, which then fills the corner. With <paramref name="trimCorners"/> the side leaves its two corners to the
    /// sides that meet it there, keeping the pattern where it was.
    /// </summary>
    private static void FitDashes(LineOp line, float width, float length, bool trimCorners)
    {
        bool dotted = line.Dotted;
        float dash = dotted ? width : width * (width >= 3 * CssPixel ? 2 : 3), gap = dotted ? width : width * (width >= 3 * CssPixel ? 1 : 2);
        if (!(length > 0) || length / (dash + gap) > MaxBorderDashes) return;
        float fewest = MathF.Floor((length + gap) / (dash + gap));
        if (fewest >= 2)
        {
            // The number of dashes that leaves the gap closest to the wanted one.
            float fewGap = (length - fewest * dash) / (fewest - 1), manyGap = (length - (fewest + 1) * dash) / fewest;
            gap = manyGap <= 0 || MathF.Abs(fewGap - gap) < MathF.Abs(manyGap - gap) ? fewGap : manyGap;
        }
        else gap = Math.Max(gap, length);
        bool vertical = line.X1 == line.X2;
        if (dotted)
        {
            // Round caps on zero-length dashes, from the centre of the first dot to the centre of the last; the step is a
            // hair short so that rounding never drops the last dot.
            float step = dash + gap, inset = width / 2 + (trimCorners && fewest >= 3 ? step : 0);
            Shorten(line, vertical, inset, inset);
            line.DashGap = step * (1 - 1e-4f);
            return;
        }
        line.DashLength = dash;
        line.DashGap = gap;
        if (trimCorners)
        {
            Shorten(line, vertical, width, width);
            line.DashPhase = width;
        }

        static void Shorten(LineOp line, bool vertical, float start, float end)
        {
            if (vertical) { line.Y1 += start; line.Y2 -= end; }
            else { line.X1 += start; line.X2 -= end; }
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

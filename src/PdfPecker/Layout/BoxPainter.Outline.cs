using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>The outline: a border-like ring outside the border box that takes no room in the layout.</summary>
internal static partial class BoxPainter
{
    /// <summary>
    /// outline-style: auto draws Chrome's focus ring whatever the outline's width: two pixels wide, centred on the
    /// outline-offset line, with corners rounded by four pixels where the box has none of its own.
    /// </summary>
    private const float FocusRingWidth = 2 * CssPixel, FocusRingRadius = 4 * CssPixel;

    /// <summary>
    /// Paints the outline around a border box: outside it, outline-offset away, following its rounded corners. It takes
    /// no room in the layout, and its sides are drawn as a border's are, so its corners and dashes match Chrome's.
    /// </summary>
    public static void PaintOutline(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii)
    {
        uint color = s.OutlineColor ?? s.Color;
        float width = s.OutlineAuto ? FocusRingWidth : s.OutlineWidth, off = s.OutlineOffset;
        if (width <= 0 || !CssColor.IsVisible(color) || w <= 0 || h <= 0) return;
        float grow = s.OutlineAuto ? off + width / 2 : off + width;
        float ox = x - grow, oy = y - grow, ow = w + 2 * grow, oh = h + 2 * grow;
        if (ow <= 0 || oh <= 0) return;
        var style = s.OutlineStyle;
        bool roomInside = 2 * width < ow && 2 * width < oh;
        if (s.OutlineAuto)
        {
            var ring = radii?.Outset(grow) ?? new CornerRadii(FocusRingRadius, FocusRingRadius, FocusRingRadius, FocusRingRadius,
                FocusRingRadius, FocusRingRadius, FocusRingRadius, FocusRingRadius);
            ring = CornerRadii.Fit(ring.TLx, ring.TLy, ring.TRx, ring.TRy, ring.BRx, ring.BRy, ring.BLx, ring.BLy, ow, oh) ?? default;
            group.Ops.Add(new RoundRectOp(ox, oy, ow, oh, ring, color)
                .WithHole(ox + width, oy + width, Math.Max(0, ow - 2 * width), Math.Max(0, oh - 2 * width), ring.Inset(width, width, width, width)));
            return;
        }
        var sides = new BorderSides(new BorderSide(width, style, color));
        if (radii is { } r)
        {
            PaintRoundedBorder(group, sides, ox, oy, ow, oh, r.Outset(grow));
            return;
        }
        if (roomInside && (Is3D(style) || sides.Top.IsDoubled))
        {
            // Mitred corners, like a box's border in these styles.
            PaintRoundedBorder(group, sides, ox, oy, ow, oh, default);
            return;
        }
        if (style == BorderStyle.Solid && roomInside)
        {
            group.Ops.Add(new BorderRectOp(ox, oy, ow, oh, width, color));
            return;
        }
        bool trim = roomInside && SharesTranslucentCorners(sides);
        Side(group, style, width, color, ox, oy, ow, width, horizontal: true, SideTop);
        Side(group, style, width, color, ox, oy + oh - width, ow, width, horizontal: true, SideBottom);
        Side(group, style, width, color, ox, oy, width, oh, horizontal: false, SideLeft, trim);
        Side(group, style, width, color, ox + ow - width, oy, width, oh, horizontal: false, SideRight, trim);
    }

    /// <summary>How many line fragments of one inline box the outline joins into one shape; past it each is outlined alone.</summary>
    internal const int MaxMergedOutlineFragments = 128;

    /// <summary>
    /// Paints the outline of an inline box that wraps over several lines as Chrome does: one shape around all of its
    /// line fragments together, the outer edge of which is the edge of the union of their rectangles grown by the
    /// outline, with each corner (the concave ones as well) rounded when the box or the focus ring has rounded corners.
    /// </summary>
    public static void PaintMergedOutline(GroupOp group, ComputedStyle s, ReadOnlySpan<(float X, float Y, float W, float H)> rects, CornerRadii? radii)
    {
        uint color = s.OutlineColor ?? s.Color;
        float width = s.OutlineAuto ? FocusRingWidth : s.OutlineWidth, off = s.OutlineOffset;
        if (width <= 0 || !CssColor.IsVisible(color)) return;
        float grow = s.OutlineAuto ? off + width / 2 : off + width;
        float outer = radii is { } r ? r.TLx + grow : s.OutlineAuto ? FocusRingRadius : 0;
        var style = s.OutlineAuto ? BorderStyle.Solid : s.OutlineStyle;
        foreach (var loop in UnionLoops(rects, grow))
            PaintOutlineLoop(group, loop, style, width, color, outer > 0 ? Math.Max(0, outer - width / 2) : 0);
    }

    /// <summary>
    /// Paints the outline inside one loop of the outer edge, <paramref name="width"/> wide; <paramref name="radius"/> is
    /// the corner radius along the middle of the outline, or 0 for square corners.
    /// </summary>
    private static void PaintOutlineLoop(GroupOp group, float[] edge, BorderStyle style, float width, uint color, float radius)
    {
        int n = edge.Length / 2;
        var centre = Inset(edge, width / 2);
        switch (style)
        {
            case BorderStyle.None:
                return;
            case BorderStyle.Double when width >= MinDoubleWidth:
                Stroke(group, Inset(edge, width / 6), LoopRadii(edge, radius, width / 3), color, width / 3, style, null);
                Stroke(group, Inset(edge, 5 * width / 6), LoopRadii(edge, radius, -width / 3), color, width / 3, style, null);
                return;
            case BorderStyle.Dashed or BorderStyle.Dotted when radius <= 0:
            {
                // Each edge on its own, its dashes fitted to start and end at its corners, as a box's sides are.
                bool trim = (color >> 24) != 0xFF;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float ax = centre[2 * i], ay = centre[2 * i + 1], bx = centre[2 * j], by = centre[2 * j + 1];
                    if (Math.Abs(ay - by) < Math.Abs(ax - bx))
                        Side(group, style, width, color, Math.Min(ax, bx) - width / 2, ay - width / 2, Math.Abs(bx - ax) + width, width, horizontal: true, -1);
                    else
                        Side(group, style, width, color, ax - width / 2, Math.Min(ay, by) - width / 2, width, Math.Abs(by - ay) + width, horizontal: false, -1, trim);
                }
                return;
            }
            case BorderStyle.Groove or BorderStyle.Ridge or BorderStyle.Inset or BorderStyle.Outset:
            {
                // Each edge in the shade of the side it faces, up to the lines that halve its corners.
                bool halves = style is BorderStyle.Groove or BorderStyle.Ridge && width >= MinGrooveWidth;
                var outerHalf = halves ? Inset(edge, width / 4) : centre;
                var innerHalf = halves ? Inset(edge, 3 * width / 4) : null;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    float dx = centre[2 * j] - centre[2 * i], dy = centre[2 * j + 1] - centre[2 * i + 1];
                    int side = Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? SideTop : SideBottom) : (dy > 0 ? SideRight : SideLeft);
                    var clip = Wedge(centre, i, width + radius);
                    EdgeStroke(group, outerHalf, LoopRadii(edge, radius, halves ? width / 4 : 0), i, Shade3D(style, side, color), halves ? width / 2 : width, clip);
                    if (innerHalf != null)
                        EdgeStroke(group, innerHalf, LoopRadii(edge, radius, -width / 4), i, Shade3D(style, side, color, innerHalf: true), width / 2, clip);
                }
                return;
            }
            default:
                Stroke(group, centre, LoopRadii(edge, radius, 0), color, width, style is BorderStyle.Dashed or BorderStyle.Dotted ? style : BorderStyle.Solid, null);
                return;
        }

        // Edge i with the corners at its ends, from the point before it to the point after: all of the loop that its
        // part of the page reaches, which keeps a long loop from being written out once for every edge.
        static void EdgeStroke(GroupOp group, float[] loop, float[]? radii, int i, uint color, float width, float[] clip)
        {
            int n = loop.Length / 2;
            var part = new float[8];
            var partRadii = radii == null ? null : new float[4];
            for (int k = 0; k < 4; k++)
            {
                int at = (i + n - 1 + k) % n;
                part[2 * k] = loop[2 * at];
                part[2 * k + 1] = loop[2 * at + 1];
                if (partRadii != null && k is 1 or 2) partRadii[k] = radii![at];
            }
            group.Ops.Add(new PathOp([part], partRadii == null ? null : [partRadii], color) { StrokeWidth = width, Open = true, Clip = (float[])clip.Clone() });
        }

        // Each op gets its own points, which moving the op moves.
        static void Stroke(GroupOp group, float[] loop, float[]? radii, uint color, float width, BorderStyle style, float[]? clip)
            => group.Ops.Add(new PathOp([(float[])loop.Clone()], radii == null ? null : [radii], color)
                { StrokeWidth = width, Style = style, Clip = (float[]?)clip?.Clone() });
    }

    /// <summary>
    /// The corner radii of a loop <paramref name="outward"/> out from the middle of the outline: larger on its convex
    /// corners and smaller on its concave ones, so that the rings stay the same distance apart; null when square.
    /// </summary>
    private static float[]? LoopRadii(float[] edge, float radius, float outward)
    {
        if (radius <= 0) return null;
        int n = edge.Length / 2;
        var radii = new float[n];
        for (int i = 0; i < n; i++) radii[i] = Math.Max(0, radius + (Turn(edge, i) > 0 ? outward : -outward));
        return radii;
    }

    /// <summary>
    /// How a loop turns at point i: above zero where it turns right on the page, which, as the loops run with the shape
    /// on their right, is a convex corner of the shape.
    /// </summary>
    private static float Turn(float[] loop, int i)
    {
        int n = loop.Length / 2, p = (i + n - 1) % n, q = (i + 1) % n;
        float ax = loop[2 * i] - loop[2 * p], ay = loop[2 * i + 1] - loop[2 * p + 1];
        float bx = loop[2 * q] - loop[2 * i], by = loop[2 * q + 1] - loop[2 * i + 1];
        return ax * by - ay * bx;
    }

    /// <summary>The direction from point i to point j of a loop, of length one.</summary>
    private static (float X, float Y) Direction(float[] loop, int i, int j)
    {
        float dx = loop[2 * j] - loop[2 * i], dy = loop[2 * j + 1] - loop[2 * i + 1], d = MathF.Sqrt(dx * dx + dy * dy);
        return d > 0 ? (dx / d, dy / d) : (0, 0);
    }

    /// <summary>A loop of straight edges with each edge moved <paramref name="distance"/> into the shape, on its right.</summary>
    private static float[] Inset(float[] loop, float distance)
    {
        int n = loop.Length / 2;
        var inset = new float[loop.Length];
        for (int i = 0; i < n; i++)
        {
            var (bx, by) = Bisector(loop, i);
            inset[2 * i] = loop[2 * i] + distance * bx;
            inset[2 * i + 1] = loop[2 * i + 1] + distance * by;
        }
        return inset;
    }

    /// <summary>
    /// The sum of the inward normals of the two edges at point i of a loop: the way the point moves, per unit, when both
    /// edges move into the shape, and the line that halves the corner.
    /// </summary>
    private static (float X, float Y) Bisector(float[] loop, int i)
    {
        int n = loop.Length / 2;
        var (ax, ay) = Direction(loop, (i + n - 1) % n, i);
        var (bx, by) = Direction(loop, i, (i + 1) % n);
        return (-ay - by, ax + bx);
    }

    /// <summary>
    /// The part of the page that belongs to edge i of a loop: between the lines that halve its two corners, reaching
    /// <paramref name="reach"/> to either side of the loop, but no further than the lines can go without crossing.
    /// </summary>
    private static float[] Wedge(float[] loop, int i, float reach)
    {
        int n = loop.Length / 2, j = (i + 1) % n;
        float length = Length(loop, i, j);
        float ri = Math.Min(reach, Math.Min(length, Length(loop, (i + n - 1) % n, i)) / 2);
        float rj = Math.Min(reach, Math.Min(length, Length(loop, j, (j + 1) % n)) / 2);
        var (ix, iy) = Bisector(loop, i);
        var (jx, jy) = Bisector(loop, j);
        float px = loop[2 * i], py = loop[2 * i + 1], qx = loop[2 * j], qy = loop[2 * j + 1];
        return [px - ri * ix, py - ri * iy, qx - rj * jx, qy - rj * jy, qx + rj * jx, qy + rj * jy, px + ri * ix, py + ri * iy];

        static float Length(float[] loop, int a, int b)
        {
            float dx = loop[2 * b] - loop[2 * a], dy = loop[2 * b + 1] - loop[2 * a + 1];
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>
    /// The edge of the union of rectangles each grown by <paramref name="grow"/>, as closed loops of corner points that
    /// run clockwise on the page around each piece and counter-clockwise around each hole, so the shape is always on the
    /// right.
    /// </summary>
    private static List<float[]> UnionLoops(ReadOnlySpan<(float X, float Y, float W, float H)> rects, float grow)
    {
        var xs = new List<float>(2 * rects.Length);
        var ys = new List<float>(2 * rects.Length);
        foreach (var r in rects)
        {
            if (!(r.W + 2 * grow > 0 && r.H + 2 * grow > 0)) continue;
            xs.Add(r.X - grow); xs.Add(r.X + r.W + grow);
            ys.Add(r.Y - grow); ys.Add(r.Y + r.H + grow);
        }
        var loops = new List<float[]>();
        if (xs.Count == 0) return loops;
        Distinct(xs);
        Distinct(ys);
        int nx = xs.Count, ny = ys.Count, cw = nx - 1, ch = ny - 1;
        // Which cells of the grid the lines of the rectangles make are covered.
        var filled = new bool[cw * ch];
        foreach (var r in rects)
        {
            if (!(r.W + 2 * grow > 0 && r.H + 2 * grow > 0)) continue;
            int i0 = Nearest(xs, r.X - grow), i1 = Nearest(xs, r.X + r.W + grow), j0 = Nearest(ys, r.Y - grow), j1 = Nearest(ys, r.Y + r.H + grow);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++) filled[j * cw + i] = true;
        }
        // The cell sides between a covered cell and an empty one, each going out of a grid point in one of four
        // directions (0 right, 1 down, 2 left, 3 up) with the covered cell on its right.
        var outgoing = new byte[nx * ny];
        for (int j = 0; j < ch; j++)
            for (int i = 0; i < cw; i++)
            {
                if (!filled[j * cw + i]) continue;
                if (j == 0 || !filled[(j - 1) * cw + i]) outgoing[j * nx + i] |= 1;
                if (i == cw - 1 || !filled[j * cw + i + 1]) outgoing[j * nx + i + 1] |= 2;
                if (j == ch - 1 || !filled[(j + 1) * cw + i]) outgoing[(j + 1) * nx + i + 1] |= 4;
                if (i == 0 || !filled[j * cw + i - 1]) outgoing[(j + 1) * nx + i] |= 8;
            }
        var points = new List<float>();
        var dirs = new List<int>();
        var at = new List<int>();
        for (int start = 0; start < outgoing.Length; start++)
            while (outgoing[start] != 0)
            {
                at.Clear();
                dirs.Clear();
                int point = start, dir = System.Numerics.BitOperations.TrailingZeroCount(outgoing[start]);
                do
                {
                    at.Add(point);
                    dirs.Add(dir);
                    outgoing[point] &= (byte)~(1 << dir);
                    point += dir switch { 0 => 1, 1 => nx, 2 => -1, _ => -nx };
                    if (point == start) break;
                    // Where two pieces touch at a point, turning right keeps them apart.
                    int next = -1;
                    foreach (int turn in (ReadOnlySpan<int>)[1, 0, 3])
                        if ((outgoing[point] & 1 << ((dir + turn) & 3)) != 0) { next = (dir + turn) & 3; break; }
                    if (next < 0) break;
                    dir = next;
                } while (true);
                points.Clear();
                for (int k = 0; k < at.Count; k++)
                {
                    if (dirs[k] == dirs[(k + at.Count - 1) % at.Count]) continue;
                    points.Add(xs[at[k] % nx]);
                    points.Add(ys[at[k] / nx]);
                }
                if (points.Count >= 8) loops.Add([.. points]);
            }
        return loops;

        static void Distinct(List<float> values)
        {
            values.Sort();
            int k = 0;
            for (int i = 0; i < values.Count; i++)
                if (k == 0 || values[i] - values[k - 1] > 0.01f) values[k++] = values[i];
            values.RemoveRange(k, values.Count - k);
        }

        static int Nearest(List<float> values, float v)
        {
            int i = values.BinarySearch(v);
            if (i >= 0) return i;
            i = ~i;
            if (i == values.Count || (i > 0 && v - values[i - 1] < values[i] - v)) i--;
            return i;
        }
    }
}

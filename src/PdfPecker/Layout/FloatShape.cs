namespace PdfPecker.Layout;

/// <summary>
/// The float area of a float with shape-outside, in flow coordinates: how much of a line's band the shape, grown by
/// shape-margin, covers. It follows Chrome's shapes: circles, ellipses, rounded boxes and polygons are exact, and
/// images, gradients and path() are scanned into one interval per row of CSS pixels.
/// </summary>
internal sealed class FloatShape
{
    private const float Px = 0.75f; // one CSS pixel in points

    private enum ShapeKind : byte { Ellipse, Box, Polygon, Raster }

    private ShapeKind _kind;
    private float _margin;            // shape-margin, in points
    private float _cx, _cy, _rx, _ry; // ellipse: centre and radii, the margin included
    private float _x, _y, _w, _h;     // box: the rectangle, the margin included; raster: the origin of its pixel rows
    private float[]? _radii;          // box: horizontal and vertical radius of each corner from the top left, clockwise
    private float[]? _points;         // polygon: x and y of each corner
    private ShapeRows? _rows;         // raster: the rows, the margin included

    /// <summary>The top and bottom of the shape with its margin: lines outside them pass the float by.</summary>
    public float Top { get; private set; }
    public float Bottom { get; private set; }

    public static FloatShape Ellipse(float cx, float cy, float rx, float ry, float margin)
    {
        rx += margin;
        ry += margin;
        return new FloatShape { _kind = ShapeKind.Ellipse, _margin = margin, _cx = cx, _cy = cy, _rx = rx, _ry = ry, Top = cy - ry, Bottom = cy + ry };
    }

    /// <summary>A box with rounded corners; shape-margin grows it and every corner's radii, as in Chrome.</summary>
    public static FloatShape Box(float x, float y, float w, float h, CornerRadii? radii, float margin)
    {
        var r = radii is { } c ? new[] { c.TLx, c.TLy, c.TRx, c.TRy, c.BRx, c.BRy, c.BLx, c.BLy } : new float[8];
        if (margin > 0)
        {
            x -= margin;
            y -= margin;
            w += 2 * margin;
            h += 2 * margin;
            for (int i = 0; i < 8; i++) r[i] += margin;
        }
        return new FloatShape { _kind = ShapeKind.Box, _margin = margin, _x = x, _y = y, _w = w, _h = h, _radii = r, Top = y, Bottom = y + h };
    }

    public static FloatShape Polygon(float[] points, float margin)
    {
        float top = float.MaxValue, bottom = float.MinValue;
        for (int i = 1; i < points.Length; i += 2)
        {
            top = Math.Min(top, points[i]);
            bottom = Math.Max(bottom, points[i]);
        }
        // Fewer than three corners make an empty shape, which no line wraps around.
        if (points.Length < 6) top = bottom = 0;
        else { top -= margin; bottom += margin; }
        return new FloatShape { _kind = ShapeKind.Polygon, _margin = margin, _points = points, Top = top, Bottom = bottom };
    }

    /// <summary>Rows of pixels from (x, y) in points, their margin already added.</summary>
    public static FloatShape Raster(ShapeRows rows, float x, float y)
    {
        var shape = new FloatShape { _kind = ShapeKind.Raster, _x = x, _y = y, _rows = rows };
        if (rows.BoundsTop < rows.BoundsBottom)
        {
            shape.Top = y + rows.BoundsTop * Px;
            shape.Bottom = y + rows.BoundsBottom * Px;
        }
        else shape.Top = shape.Bottom = y;
        return shape;
    }

    /// <summary>
    /// The part of the band from <paramref name="y1"/> to <paramref name="y2"/> that the shape covers, from
    /// <paramref name="left"/> to <paramref name="right"/>; false when the shape leaves the whole band to the lines.
    /// </summary>
    public bool Excluded(float y1, float y2, out float left, out float right)
    {
        left = right = 0;
        // Whether the line reaches the shape's bounds at all; a line of no height counts at the shape's top.
        if (!(Bottom > Top) || !(y1 < Bottom && y2 > Top || y2 == y1 && y1 == Top)) return false;
        return _kind switch
        {
            ShapeKind.Ellipse => EllipseInterval(y1, y2, out left, out right),
            ShapeKind.Box => BoxInterval(y1, y2, out left, out right),
            ShapeKind.Polygon => PolygonInterval(y1, y2, out left, out right),
            _ => RasterInterval(y1, y2, out left, out right),
        };
    }

    private bool EllipseInterval(float y1, float y2, out float left, out float right)
    {
        left = right = 0;
        if (_rx <= 0 || _ry <= 0 || y2 < _cy - _ry || y1 >= _cy + _ry) return false;
        // The widest point of the ellipse in the band: its middle, or the band's edge nearest to it.
        float xi = _rx;
        if (y1 > _cy || y2 < _cy)
        {
            float dy = y1 > _cy ? y1 - _cy : y2 - _cy;
            xi = _rx * MathF.Sqrt(Math.Max(0, 1 - dy * dy / (_ry * _ry)));
        }
        left = _cx - xi;
        right = _cx + xi;
        return true;
    }

    private bool BoxInterval(float y1, float y2, out float left, out float right)
    {
        var r = _radii!;
        float x0 = _x, x1 = _x + _w, top = _y, bottom = _y + _h;
        left = x0;
        right = x1;
        if (_w <= 0 || _h <= 0) return false;
        bool rounded = false;
        foreach (float v in r) rounded |= v > 0;
        if (!rounded) return true;
        // The corners' rectangles: top left, top right, bottom right, bottom left.
        float tlBottom = top + r[1], trBottom = top + r[3], brTop = bottom - r[5], blTop = bottom - r[7];
        float topCorners = Math.Max(tlBottom, trBottom), bottomCorners = Math.Min(blTop, brTop);
        if (topCorners <= bottomCorners && y1 <= topCorners && y2 >= bottomCorners) return true;
        // As in Chrome: a side is straight in the band when the band spans that side's corners; otherwise the widest
        // points are where the band's edges cross the corners.
        float l = x1, rt = x0;
        if (y1 <= tlBottom && y2 >= blTop) l = x0;
        if (y1 <= trBottom && y2 >= brTop) rt = x1;
        foreach (float y in stackalloc[] { y1, y2 })
        {
            if (y < top || y > bottom) continue;
            l = Math.Min(l, BoxLeftAt(y));
            rt = Math.Max(rt, BoxRightAt(y));
        }
        left = l;
        right = Math.Max(l, rt);
        return true;
    }

    private float BoxLeftAt(float y)
    {
        var r = _radii!;
        float top = _y, bottom = _y + _h;
        if (r[0] > 0 && r[1] > 0 && y >= top && y < top + r[1])
            return _x + r[0] - CornerIntercept(top + r[1] - y, r[0], r[1]);
        if (r[6] > 0 && r[7] > 0 && y >= bottom - r[7] && y <= bottom)
            return _x + r[6] - CornerIntercept(y - (bottom - r[7]), r[6], r[7]);
        return _x;
    }

    private float BoxRightAt(float y)
    {
        var r = _radii!;
        float top = _y, bottom = _y + _h, x1 = _x + _w;
        if (r[2] > 0 && r[3] > 0 && y >= top && y <= top + r[3])
            return x1 - r[2] + CornerIntercept(top + r[3] - y, r[2], r[3]);
        if (r[4] > 0 && r[5] > 0 && y >= bottom - r[5] && y <= bottom)
            return x1 - r[4] + CornerIntercept(y - (bottom - r[5]), r[4], r[5]);
        return x1;
    }

    /// <summary>How far a corner's curve reaches across, <paramref name="dy"/> from its straight side's end.</summary>
    private static float CornerIntercept(float dy, float rx, float ry) => rx * MathF.Sqrt(Math.Max(0, 1 - dy * dy / (ry * ry)));

    /// <summary>
    /// Chrome's polygon interval: the x range of every edge that crosses the band, and with a margin, of each edge moved
    /// out and in by the margin and of a circle round each corner.
    /// </summary>
    private bool PolygonInterval(float y1, float y2, out float left, out float right)
    {
        var p = _points!;
        left = float.NaN;
        right = float.NaN;
        float m = _margin;
        int n = p.Length / 2;
        for (int i = 0; i < n; i++)
        {
            float ax = p[2 * i], ay = p[2 * i + 1];
            int j = i + 1 < n ? i + 1 : 0;
            float bx = p[2 * j], by = p[2 * j + 1];
            if (ay == by) continue;
            if (Math.Max(ay, by) < y1 - m || Math.Min(ay, by) > y2 + m) continue;
            if (m == 0)
            {
                ClippedEdge(ax, ay, bx, by, y1, y2, ref left, ref right);
                continue;
            }
            // The outward normal of the edge (the inward one is its opposite); which side is out does not matter, as
            // both offsets are taken.
            float dx = bx - ax, dy = by - ay, nx, ny;
            if (dx == 0) { nx = dy > 0 ? 1 : -1; ny = 0; }
            else
            {
                float len = MathF.Sqrt(dx * dx + dy * dy);
                nx = dy / len;
                ny = -dx / len;
            }
            ClippedEdge(ax + nx * m, ay + ny * m, bx + nx * m, by + ny * m, y1, y2, ref left, ref right);
            ClippedEdge(ax - nx * m, ay - ny * m, bx - nx * m, by - ny * m, y1, y2, ref left, ref right);
            ClippedCircle(ax, ay, m, y1, y2, ref left, ref right);
            ClippedCircle(bx, by, m, y1, y2, ref left, ref right);
        }
        // An interval of no width is empty, as in Chrome.
        return left < right;
    }

    private static void Unite(float a, float b, ref float left, ref float right)
    {
        if (float.IsNaN(left)) { left = a; right = b; return; }
        left = Math.Min(left, a);
        right = Math.Max(right, b);
    }

    /// <summary>The x range of the edge from (ax, ay) to (bx, by) between the heights y1 and y2.</summary>
    private static void ClippedEdge(float ax, float ay, float bx, float by, float y1, float y2, ref float left, ref float right)
    {
        float minY = Math.Min(ay, by), maxY = Math.Max(ay, by);
        if (!(y2 >= minY && y1 <= maxY) || y1 == maxY && minY <= y1 || y2 == minY && maxY >= y2) return;
        if (y1 <= minY && y2 >= maxY)
        {
            Unite(Math.Min(ax, bx), Math.Max(ax, bx), ref left, ref right);
            return;
        }
        // The ends of the edge's part in the band.
        float topX = ay < by ? ax : bx, bottomX = ay < by ? bx : ax;
        float xa = minY < y1 ? XAt(ax, ay, bx, by, y1) : topX;
        float xb = maxY > y2 ? XAt(ax, ay, bx, by, y2) : bottomX;
        Unite(Math.Min(xa, xb), Math.Max(xa, xb), ref left, ref right);
    }

    private static float XAt(float ax, float ay, float bx, float by, float y) => ax == bx ? ax : ax + (y - ay) * (bx - ax) / (by - ay);

    /// <summary>The x range of the circle round (cx, cy) between the heights y1 and y2.</summary>
    private static void ClippedCircle(float cx, float cy, float r, float y1, float y2, ref float left, ref float right)
    {
        if (y1 >= cy + r || y2 <= cy - r) return;
        if (cy >= y1 && cy <= y2) { Unite(cx - r, cx + r, ref left, ref right); return; }
        float dy = (y2 < cy ? y2 : y1) - cy;
        float xi = r * MathF.Sqrt(Math.Max(0, 1 - dy * dy / (r * r)));
        Unite(cx - xi, cx + xi, ref left, ref right);
    }

    private bool RasterInterval(float y1, float y2, out float left, out float right)
    {
        var rows = _rows!;
        // Rows are whole pixels, and the band's edges are cut to whole pixels as Chrome cuts them.
        int r1 = (int)((y1 - _y) / Px), r2 = (int)((y2 - _y) / Px);
        left = right = 0;
        if (r2 < rows.BoundsTop || r1 >= rows.BoundsBottom) return false;
        r1 = Math.Max(r1, rows.BoundsTop);
        r2 = Math.Min(r2, rows.BoundsBottom);
        int x1 = int.MaxValue, x2 = int.MinValue;
        for (int r = r1; r < r2 || r == r1; r++)
        {
            var (a, b) = rows.Row(r);
            if (a > b) continue;
            x1 = Math.Min(x1, a);
            x2 = Math.Max(x2, b);
        }
        // Rows inside the bounds with nothing in them still give Chrome's undefined interval, from -1 to -2 pixels,
        // which the float's margin box then clamps.
        if (x1 > x2) { x1 = -1; x2 = -2; }
        left = _x + x1 * Px;
        right = _x + x2 * Px;
        return true;
    }
}

/// <summary>
/// A shape scanned into rows of CSS pixels: the first and last pixel in the shape on each row, from a top row that may be
/// above the shape's origin. A row with nothing in it holds a start after its end.
/// </summary>
internal sealed class ShapeRows
{
    private readonly int[] _x1, _x2;

    /// <summary>The row of index 0.</summary>
    public readonly int First;

    /// <summary>The rows that have something in them, from the first to after the last.</summary>
    public int BoundsTop { get; private set; }
    public int BoundsBottom { get; private set; }

    public ShapeRows(int first, int count)
    {
        First = first;
        _x1 = new int[count];
        _x2 = new int[count];
        Array.Fill(_x1, int.MaxValue);
        Array.Fill(_x2, int.MinValue);
    }

    public int Count => _x1.Length;

    public (int X1, int X2) Row(int y)
    {
        int i = y - First;
        return i >= 0 && i < _x1.Length ? (_x1[i], _x2[i]) : (int.MaxValue, int.MinValue);
    }

    /// <summary>Widens row <paramref name="y"/> to take in the pixels from <paramref name="x1"/> to <paramref name="x2"/>.</summary>
    public void Unite(int y, int x1, int x2)
    {
        int i = y - First;
        if (i < 0 || i >= _x1.Length || x1 > x2) return;
        if (x1 < _x1[i]) _x1[i] = x1;
        if (x2 > _x2[i]) _x2[i] = x2;
    }

    /// <summary>Works out the bounds once every row is set.</summary>
    public ShapeRows Seal()
    {
        int top = 0, bottom = 0;
        bool any = false;
        for (int i = 0; i < _x1.Length; i++)
        {
            if (_x1[i] > _x2[i]) continue;
            if (!any) top = i + First;
            bottom = i + First + 1;
            any = true;
        }
        BoundsTop = top;
        BoundsBottom = bottom;
        return this;
    }

    /// <summary>
    /// The rows grown by a margin of <paramref name="margin"/> whole pixels, as Chrome grows them: each row's interval
    /// spreads to the rows within the margin, narrowing like a circle, and stops at a row that already covers it. The
    /// result keeps to the same rows.
    /// </summary>
    public ShapeRows WithMargin(int margin)
    {
        if (margin <= 0) return this;
        var result = new ShapeRows(First, Count);
        var reach = new int[margin + 1];
        for (int d = 0; d <= margin; d++) reach[d] = (int)Math.Sqrt((double)margin * margin - (double)d * d);
        int end = First + Count;
        for (int y = BoundsTop; y < BoundsBottom; y++)
        {
            var (x1, x2) = Row(y);
            if (x1 > x2) continue;
            int from = Math.Max(First, y - margin), to = Math.Min(end, y + margin + 1);
            for (int my = y - 1; my >= from; my--)
            {
                var (a, b) = Row(my);
                if (my > BoundsTop && a <= x1 && b >= x2) break;
                int dx = reach[y - my];
                result.Unite(my, x1 - dx, x2 + dx);
            }
            result.Unite(y, x1 - margin, x2 + margin);
            for (int my = y + 1; my < to; my++)
            {
                var (a, b) = Row(my);
                if (my < BoundsBottom && a <= x1 && b >= x2) break;
                int dx = reach[my - y];
                result.Unite(my, x1 - dx, x2 + dx);
            }
        }
        return result.Seal();
    }
}

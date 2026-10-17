namespace PdfPecker.Rendering.Graphics;

internal enum LineCap : byte { Butt = 0, Round = 1, Square = 2 }
internal enum LineJoin : byte { Miter = 0, Round = 1, Bevel = 2 }

/// <summary>
/// Converts stroked polylines into fill polygons: one quad per segment plus join and cap pieces, all with the same
/// orientation so that a non-zero fill unions them without seams.
/// </summary>
internal sealed class Stroker
{
    /// <summary>
    /// The most outline points the dashes of one stroke may make, each dash a quad and its two caps. The dash array's
    /// numbers and the path's length are both up to the file, so a few bytes can ask for billions of dashes; past this,
    /// the stroke is drawn solid, as Skia does past a million dashes. At some thirty bytes a point on the way through
    /// the rasterizer this is about thirty megabytes: 250,000 dashes with butt caps, or some 30,000 round dots ten
    /// points wide at 300 dpi, far more than a page shows.
    /// </summary>
    internal const int MaxDashPoints = 1_000_000;

    private readonly FlatPath _dashed = new();
    private float[] _xs = new float[256], _ys = new float[256];

    /// <summary>
    /// Strokes <paramref name="src"/> into fill polygons in <paramref name="dst"/>. A dash pattern whose period is
    /// shorter than <paramref name="minDashPeriod"/> cannot show its gaps at the output resolution, so it is stroked
    /// solid, the way it looks from a distance.
    /// </summary>
    /// <returns>How many outline points the dashes made, which the caller charges to its work budget; 0 if not dashed.</returns>
    public int Stroke(FlatPath src, FlatPath dst, float width, LineCap cap, LineJoin join, float miterLimit,
        float[]? dash, float dashPhase, float tolerance, float minDashPeriod)
    {
        dst.Clear();
        float hw = width / 2;
        if (!(hw > 0)) return 0;
        int dashPoints = 0;
        if (dash != null && dash.Length > 0)
        {
            float total = 0;
            bool valid = true;
            foreach (var d in dash) { if (d < 0 || float.IsNaN(d)) valid = false; total += d; }
            if (valid && total > 0)
            {
                // An odd array repeats with on and off swapped, so its period is twice its sum and every entry is a dash once.
                float period = dash.Length % 2 == 1 ? total * 2 : total;
                int dashesPerPeriod = dash.Length % 2 == 1 ? dash.Length : dash.Length / 2;
                // Each contour may start and end with a partial period.
                double dashes = Length(src) / period * dashesPerPeriod + (src.ContourCount + 1.0) * dashesPerPeriod;
                double points = dashes * (4 + 2 * CapPoints(cap, hw, tolerance));
                if (period >= minDashPeriod && points <= MaxDashPoints)
                {
                    Dash(src, _dashed, dash, dashPhase, hw);
                    src = _dashed;
                    dashPoints = (int)points;
                }
            }
        }
        for (int c = 0; c < src.ContourCount; c++)
        {
            int s = src.Starts[c], e = src.ContourEnd(c);
            StrokeContour(src, s, e, src.Closed[c], dst, hw, cap, join, miterLimit, tolerance);
        }
        return dashPoints;
    }

    /// <summary>The outline points of one cap, as <see cref="Cap"/> makes them.</summary>
    private static int CapPoints(LineCap cap, float hw, float tol) => cap switch
    {
        LineCap.Round => ArcSegments(hw, MathF.PI, tol) + 2,
        LineCap.Square => 4,
        _ => 0,
    };

    /// <summary>The length of all of a path's contours, closing segments included.</summary>
    private static double Length(FlatPath src)
    {
        double length = 0;
        for (int c = 0; c < src.ContourCount; c++)
        {
            int s = src.Starts[c], e = src.ContourEnd(c);
            int n = e - s;
            int segs = n < 2 ? 0 : src.Closed[c] ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                int a = s + i, b = s + (i + 1) % n;
                double dx = src.Pts[b * 2] - src.Pts[a * 2], dy = src.Pts[b * 2 + 1] - src.Pts[a * 2 + 1];
                length += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        return length;
    }

    private void StrokeContour(FlatPath src, int s, int e, bool closed, FlatPath dst, float hw, LineCap cap, LineJoin join,
        float miterLimit, float tol)
    {
        // Copy without consecutive duplicates.
        int n = 0;
        if (_xs.Length < e - s + 1) { _xs = new float[(e - s + 1) * 2]; _ys = new float[_xs.Length]; }
        float eps2 = hw * hw * 1e-10f;
        for (int i = s; i < e; i++)
        {
            float x = src.Pts[i * 2], y = src.Pts[i * 2 + 1];
            if (n > 0)
            {
                float dx = x - _xs[n - 1], dy = y - _ys[n - 1];
                if (dx * dx + dy * dy <= eps2) continue;
            }
            _xs[n] = x;
            _ys[n] = y;
            n++;
        }
        if (closed && n > 1)
        {
            float dx = _xs[n - 1] - _xs[0], dy = _ys[n - 1] - _ys[0];
            if (dx * dx + dy * dy <= eps2) n--;
        }
        if (n == 0) return;
        if (n == 1)
        {
            // Zero-length subpath: only round and square caps paint.
            float x = _xs[0], y = _ys[0];
            if (cap == LineCap.Round) Circle(dst, x, y, hw, tol);
            else if (cap == LineCap.Square) Poly(dst, x - hw, y - hw, x + hw, y - hw, x + hw, y + hw, x - hw, y + hw);
            return;
        }
        if (n == 2) closed = false;

        int segs = closed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % n;
            float ax = _xs[i], ay = _ys[i], bx = _xs[j], by = _ys[j];
            float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len <= 0) continue;
            float nx = -dy / len * hw, ny = dx / len * hw;
            Poly(dst, ax + nx, ay + ny, bx + nx, by + ny, bx - nx, by - ny, ax - nx, ay - ny);
        }
        // Joins.
        int first = closed ? 0 : 1, last = closed ? n : n - 1;
        for (int i = first; i < last; i++)
        {
            int prev = (i - 1 + n) % n, next = (i + 1) % n;
            Join(dst, _xs[prev], _ys[prev], _xs[i], _ys[i], _xs[next], _ys[next], hw, join, miterLimit, tol);
        }
        if (!closed)
        {
            Cap(dst, _xs[1], _ys[1], _xs[0], _ys[0], hw, cap, tol);
            Cap(dst, _xs[n - 2], _ys[n - 2], _xs[n - 1], _ys[n - 1], hw, cap, tol);
        }
    }

    private static void Join(FlatPath dst, float ax, float ay, float px, float py, float bx, float by, float hw, LineJoin join,
        float miterLimit, float tol)
    {
        float d0x = px - ax, d0y = py - ay, l0 = MathF.Sqrt(d0x * d0x + d0y * d0y);
        float d1x = bx - px, d1y = by - py, l1 = MathF.Sqrt(d1x * d1x + d1y * d1y);
        if (l0 <= 0 || l1 <= 0) return;
        d0x /= l0; d0y /= l0; d1x /= l1; d1y /= l1;
        float cross = d0x * d1y - d0y * d1x, dot = d0x * d1x + d0y * d1y;
        if (MathF.Abs(cross) < 1e-6f && dot > 0) return; // straight on
        float side = cross > 0 ? -1 : 1;
        float n0x = -d0y * hw * side, n0y = d0x * hw * side, n1x = -d1y * hw * side, n1y = d1x * hw * side;
        if (join == LineJoin.Round)
        {
            // A bevel is indistinguishable from the arc for small turns.
            float halfTurn = MathF.Acos(Math.Clamp(dot, -1, 1)) / 2;
            if (hw * (1 - MathF.Cos(halfTurn)) <= tol)
            {
                Poly(dst, px, py, px + n0x, py + n0y, px + n1x, py + n1y);
                return;
            }
            Arc(dst, px, py, hw, MathF.Atan2(n0y, n0x), MathF.Atan2(n1y, n1x), tol);
            return;
        }
        if (join == LineJoin.Miter)
        {
            float cosHalf = MathF.Sqrt(MathF.Max(0, (1 + dot) / 2));
            if (cosHalf > 1e-4f && 1 / cosHalf <= miterLimit)
            {
                float mx = n0x + n1x, my = n0y + n1y;
                float scale = 1 / (1 + dot);
                float tx = px + mx * scale, ty = py + my * scale;
                Poly(dst, px, py, px + n0x, py + n0y, tx, ty, px + n1x, py + n1y);
                return;
            }
        }
        Poly(dst, px, py, px + n0x, py + n0y, px + n1x, py + n1y);
    }

    private static void Cap(FlatPath dst, float fromX, float fromY, float x, float y, float hw, LineCap cap, float tol)
    {
        if (cap == LineCap.Butt) return;
        float dx = x - fromX, dy = y - fromY, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return;
        dx /= len; dy /= len;
        float nx = -dy * hw, ny = dx * hw;
        if (cap == LineCap.Square)
        {
            float ex = dx * hw, ey = dy * hw;
            Poly(dst, x + nx, y + ny, x + nx + ex, y + ny + ey, x - nx + ex, y - ny + ey, x - nx, y - ny);
            return;
        }
        // Semicircle from +n through the direction to -n.
        float a0 = MathF.Atan2(ny, nx);
        ArcSweep(dst, x, y, hw, a0, -MathF.PI, tol);
    }

    private static void Circle(FlatPath dst, float cx, float cy, float r, float tol)
    {
        int segs = ArcSegments(r, 2 * MathF.PI, tol);
        dst.BeginContour();
        for (int i = 0; i < segs; i++)
        {
            float a = i * 2 * MathF.PI / segs;
            dst.Add(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
        }
        dst.CloseContour();
    }

    /// <summary>A pie slice from angle a0 to a1 the short way round.</summary>
    private static void Arc(FlatPath dst, float cx, float cy, float r, float a0, float a1, float tol)
    {
        float sweep = a1 - a0;
        while (sweep > MathF.PI) sweep -= 2 * MathF.PI;
        while (sweep < -MathF.PI) sweep += 2 * MathF.PI;
        ArcSweep(dst, cx, cy, r, a0, sweep, tol);
    }

    private static void ArcSweep(FlatPath dst, float cx, float cy, float r, float a0, float sweep, float tol)
    {
        int segs = ArcSegments(r, MathF.Abs(sweep), tol);
        int start = dst.Count;
        dst.BeginContour();
        dst.Add(cx, cy);
        for (int i = 0; i <= segs; i++)
        {
            float a = a0 + sweep * i / segs;
            dst.Add(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
        }
        dst.CloseContour();
        Orient(dst, start);
    }

    private static int ArcSegments(float r, float sweep, float tol)
    {
        float step = r <= tol ? MathF.PI / 2 : 2 * MathF.Acos(MathF.Max(-1, 1 - tol / r));
        if (!(step > 0.01f)) step = 0.01f;
        return Math.Clamp((int)MathF.Ceiling(sweep / step), 2, 256);
    }

    private static void Poly(FlatPath dst, float x0, float y0, float x1, float y1, float x2, float y2)
    {
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        dst.BeginContour();
        dst.Add(x0, y0);
        if (area >= 0) { dst.Add(x1, y1); dst.Add(x2, y2); }
        else { dst.Add(x2, y2); dst.Add(x1, y1); }
        dst.CloseContour();
    }

    private static void Poly(FlatPath dst, float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
    {
        float area = (x0 * y1 - x1 * y0) + (x1 * y2 - x2 * y1) + (x2 * y3 - x3 * y2) + (x3 * y0 - x0 * y3);
        dst.BeginContour();
        if (area >= 0)
        {
            dst.Add(x0, y0); dst.Add(x1, y1); dst.Add(x2, y2); dst.Add(x3, y3);
        }
        else
        {
            dst.Add(x3, y3); dst.Add(x2, y2); dst.Add(x1, y1); dst.Add(x0, y0);
        }
        dst.CloseContour();
    }

    /// <summary>Reverses the contour starting at point <paramref name="start"/> if its signed area is negative.</summary>
    private static void Orient(FlatPath dst, int start)
    {
        float area = 0;
        int n = dst.Count - start;
        for (int i = 0; i < n; i++)
        {
            int a = start + i, b = start + (i + 1) % n;
            area += dst.Pts[a * 2] * dst.Pts[b * 2 + 1] - dst.Pts[b * 2] * dst.Pts[a * 2 + 1];
        }
        if (area >= 0) return;
        for (int i = 0, j = n - 1; i < j; i++, j--)
        {
            int a = (start + i) * 2, b = (start + j) * 2;
            (dst.Pts[a], dst.Pts[b]) = (dst.Pts[b], dst.Pts[a]);
            (dst.Pts[a + 1], dst.Pts[b + 1]) = (dst.Pts[b + 1], dst.Pts[a + 1]);
        }
    }

    // ------------------------------------------------------------------ dashes

    private static void Dash(FlatPath src, FlatPath dst, float[] dash, float phase, float hw)
    {
        dst.Clear();
        float total = 0;
        foreach (var d in dash) total += d;
        if (dash.Length % 2 == 1) total *= 2; // an odd array repeats with on/off swapped
        float Len(int i) => dash[i % dash.Length];
        int count = dash.Length % 2 == 1 ? dash.Length * 2 : dash.Length;

        for (int c = 0; c < src.ContourCount; c++)
        {
            int s = src.Starts[c], e = src.ContourEnd(c);
            int n = e - s;
            if (n < 1) continue;
            // Starting dash state.
            float p = phase % total;
            if (p < 0) p += total;
            int idx = 0;
            for (int guard = 0; p > 0 && p >= Len(idx) && guard < 10000; guard++)
            {
                p -= Len(idx);
                idx = (idx + 1) % count;
            }
            float remaining = Len(idx) - p;
            bool on = idx % 2 == 0;
            bool drawing = false;
            int segs = src.Closed[c] ? n : n - 1;
            if (n == 1) segs = 0;
            for (int i = 0; i < segs; i++)
            {
                int a = s + i, b = s + (i + 1) % n;
                float ax = src.Pts[a * 2], ay = src.Pts[a * 2 + 1], bx = src.Pts[b * 2], by = src.Pts[b * 2 + 1];
                float dx = bx - ax, dy = by - ay, len = MathF.Sqrt(dx * dx + dy * dy);
                if (len <= 0) continue;
                float ux = dx / len, uy = dy / len;
                float t = 0;
                while (t < len)
                {
                    float step = MathF.Min(remaining, len - t);
                    if (on)
                    {
                        if (!drawing)
                        {
                            dst.BeginContour();
                            dst.Add(ax + ux * t, ay + uy * t);
                            drawing = true;
                        }
                        if (step <= 0)
                        {
                            // Zero-length dash: a dot with a direction, so caps know which way to point.
                            float tiny = hw * 1e-3f;
                            dst.Add(ax + ux * (t + tiny), ay + uy * (t + tiny));
                        }
                        else dst.Add(ax + ux * (t + step), ay + uy * (t + step));
                    }
                    t += step;
                    remaining -= step;
                    if (remaining <= 1e-6f)
                    {
                        on = !on;
                        idx = (idx + 1) % count;
                        remaining = Len(idx);
                        drawing = false;
                    }
                }
            }
        }
    }
}

namespace PdfPecker.Layout;

/// <summary>
/// The corner-shape of a box's four corners (top-left, top-right, bottom-right, bottom-left) as superellipse
/// parameters: 1 is round, 2 squircle, 0 bevel, -1 scoop, +∞ square and -∞ notch (CSS Borders 4 §7). A curve with a
/// parameter k ≥ 0 is the superellipse |x|^(2^k) + |y|^(2^k) = 1 around the corner's inner point; one with k &lt; 0 is the
/// curve of -k around the box corner, so that it bends inward. The shape also keeps the radii it was made with and how
/// far the edge it describes lies inside that outer edge, because Chrome 153 draws the inner border edge, a padding box
/// clip or a shadow's spread of a corner flatter than round as the outer curve moved along its normal, which is not a
/// smaller curve of the same kind.
/// </summary>
internal sealed class CornerShape
{
    // Parameters at or past these are drawn as a square corner and as a notch: the curve is then within a fraction of a
    // percent of them.
    private const float SquareAt = 8, NotchAt = -8;

    /// <summary>The superellipse parameter of each corner.</summary>
    public readonly float[] K;
    /// <summary>The outer radii, as <see cref="CornerRadii"/> keeps them: x and y of each corner in turn.</summary>
    public readonly float[] Outer;
    /// <summary>How far the edge lies inside the outer edge on the left, top, right and bottom; negative outside it.</summary>
    public readonly float Left, Top, Right, Bottom;

    public CornerShape(float[] k, float[] outer, float left = 0, float top = 0, float right = 0, float bottom = 0)
    {
        K = k;
        Outer = outer;
        (Left, Top, Right, Bottom) = (left, top, right, bottom);
    }

    /// <summary>True when every corner is round, which needs no shape.</summary>
    public static bool AllRound(float[] k) => k[0] == 1 && k[1] == 1 && k[2] == 1 && k[3] == 1;

    /// <summary>The parameter of a corner keyword, or null when the word is none of them.</summary>
    public static float? Keyword(string lower) => lower switch
    {
        "round" => 1,
        "squircle" => 2,
        "bevel" => 0,
        "scoop" => -1,
        "notch" => float.NegativeInfinity,
        "square" => float.PositiveInfinity,
        _ => null,
    };

    /// <summary>True when a corner with parameter <paramref name="k"/> is drawn square whatever its radius.</summary>
    public static bool IsSquare(float k) => k >= SquareAt;

    /// <summary>True when no corner has a radius.</summary>
    public bool OuterZero
    {
        get
        {
            for (int i = 0; i < 4; i++)
                if (Outer[2 * i] > 0 && Outer[2 * i + 1] > 0 && !IsSquare(K[i])) return false;
            return true;
        }
    }

    /// <summary>This shape with its edge moved further in by the given distances (out for negative ones).</summary>
    public CornerShape Moved(float left, float top, float right, float bottom)
        => new(K, Outer, Left + left, Top + top, Right + right, Bottom + bottom);

    /// <summary>This shape with its radii and distances multiplied by <paramref name="f"/>.</summary>
    public CornerShape Scaled(float f)
    {
        var outer = new float[8];
        for (int i = 0; i < 8; i++) outer[i] = Outer[i] * f;
        return new CornerShape(K, outer, Left * f, Top * f, Right * f, Bottom * f);
    }

    /// <summary>
    /// The outline of the box (x, y, w, h) whose edge this shape describes, as a closed polygon clockwise from the end
    /// of the top-left corner's curve on the left edge, in the box's coordinates.
    /// </summary>
    public List<(float X, float Y)> Polygon(float x, float y, float w, float h)
    {
        // The outer box this edge was moved in from.
        float ox0 = x - Left, oy0 = y - Top, ox1 = x + w + Right, oy1 = y + h + Bottom;
        var points = new List<(float X, float Y)>(64);
        // Each corner is worked out as a top-left one and mirrored into place; a mirror in one axis reverses the order.
        AddCorner(points, ox0, oy0, 1, 1, Outer[0], Outer[1], K[0], Left, Top, reverse: false);
        AddCorner(points, ox1, oy0, -1, 1, Outer[2], Outer[3], K[1], Right, Top, reverse: true);
        AddCorner(points, ox1, oy1, -1, -1, Outer[4], Outer[5], K[2], Right, Bottom, reverse: false);
        AddCorner(points, ox0, oy1, 1, -1, Outer[6], Outer[7], K[3], Left, Bottom, reverse: true);
        return points;
    }

    /// <summary>The length of the polygon's outline.</summary>
    public static float Perimeter(List<(float X, float Y)> points)
    {
        float length = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[(i + 1) % points.Count];
            length += MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }
        return length;
    }

    /// <summary>
    /// Adds one corner: worked out with the box corner at the origin, the box to the right of and below it, running from
    /// the left edge to the top edge, then placed at (ox, oy) with the axes turned by the signs sx and sy.
    /// </summary>
    private static void AddCorner(List<(float X, float Y)> points, float ox, float oy, float sx, float sy, float rx, float ry, float k,
        float wx, float wy, bool reverse)
    {
        var corner = CanonicalCorner(rx, ry, k, wx, wy);
        if (reverse) corner.Reverse();
        foreach (var (cx, cy) in corner) points.Add((ox + sx * cx, oy + sy * cy));
    }

    /// <summary>
    /// The points of a top-left corner whose outer curve has radii rx and ry and parameter k, offset into the box by wx
    /// across and wy down (out of it when negative), from the left edge to the top edge.
    /// </summary>
    internal static List<(float X, float Y)> CanonicalCorner(float rx, float ry, float k, float wx, float wy)
    {
        var square = new List<(float X, float Y)>(1) { (wx, wy) };
        if (!(rx > 0 && ry > 0) || IsSquare(k) || float.IsNaN(k)) return square;
        if (k >= 1)
        {
            // Round and fuller corners keep their curve inside the box the inner edges leave, with the radii less the
            // widths, as border-radius does and as Chrome draws a squircle's inner edge.
            float a = rx - wx, b = ry - wy;
            if (!(a > 0 && b > 0)) return square;
            var inner = Curve(a, b, k);
            for (int i = 0; i < inner.Count; i++) inner[i] = (inner[i].X + wx, inner[i].Y + wy);
            return inner;
        }

        List<(float X, float Y)> offset;
        if (k <= NotchAt)
        {
            // A notch: in along the left edge's radius, across, and out to the top edge, each part moved in by its width.
            offset = [(0, ry + wy), (rx + wx, ry + wy), (rx + wx, 0)];
        }
        else
        {
            // Flatter and concave corners move their curve along its normal (the box lies to the right of the direction
            // of travel), by the left width where the curve leaves the left edge and the top width where it meets the
            // top edge, in between as far across as the point is: so a bevel stays a straight line, parallel to the
            // outer one when the widths agree, as Chrome 153 draws it.
            var curve = Curve(rx, ry, k);
            offset = new List<(float X, float Y)>(curve.Count);
            for (int i = 0; i < curve.Count; i++)
            {
                var (px, py) = curve[i];
                var (ax, ay) = curve[Math.Max(0, i - 1)];
                var (bx, by) = curve[Math.Min(curve.Count - 1, i + 1)];
                float tx = bx - ax, ty = by - ay, len = MathF.Sqrt(tx * tx + ty * ty);
                if (!(len > 0)) { offset.Add((px, py)); continue; }
                float width = wx + (wy - wx) * Math.Clamp(px / rx, 0, 1);
                offset.Add((px - ty / len * width, py + tx / len * width));
            }
        }
        if (wx == 0 && wy == 0) return offset;
        return Fit(offset, wx, wy);
    }

    /// <summary>The outer curve of a corner from the left edge (0, ry) to the top edge (rx, 0).</summary>
    private static List<(float X, float Y)> Curve(float rx, float ry, float k)
    {
        int count = Segments(Math.Max(rx, ry));
        var curve = new List<(float X, float Y)>(count + 1);
        // Points at even angles about the curve's centre: x^e + y^e = 1 in polar form, with the exponent e = 2^|k|.
        float e = MathF.Pow(2, Math.Abs(k));
        for (int i = 0; i <= count; i++)
        {
            float phi = MathF.PI / 2 * i / count;
            float c = i == count ? 0 : MathF.Cos(phi), s = i == 0 ? 0 : MathF.Sin(phi);
            float rho = MathF.Pow(MathF.Pow(c, e) + MathF.Pow(s, e), -1 / e);
            if (!float.IsFinite(rho)) rho = 1;
            // Convex curves are centred on the corner's inner point, concave ones on the box corner.
            curve.Add(k >= 0 ? (rx - rx * c * rho, ry - ry * s * rho) : (rx * s * rho, ry * c * rho));
        }
        return curve;
    }

    /// <summary>
    /// Cuts an offset curve to the box inside the straight inner edges (x ≥ wx, y ≥ wy), and carries an end that stops
    /// short of its edge on along its direction to that edge, so that the curve joins both edges.
    /// </summary>
    private static List<(float X, float Y)> Fit(List<(float X, float Y)> offset, float wx, float wy)
    {
        const float eps = 1e-3f;
        bool Inside((float X, float Y) p) => p.X >= wx - eps && p.Y >= wy - eps;
        var kept = new List<(float X, float Y)>(offset.Count + 2);
        for (int i = 0; i < offset.Count; i++)
        {
            var p = offset[i];
            if (i > 0)
            {
                var q = offset[i - 1];
                if (Inside(p) != Inside(q) && Crossing(q, p, wx, wy) is { } c) kept.Add(c);
            }
            if (Inside(p)) kept.Add(p);
        }
        if (kept.Count == 0) return [(wx, wy)];

        // The start joins the left edge (x = wx) and the end the top edge (y = wy).
        var first = kept[0];
        if (first.X > wx + eps)
        {
            var next = kept.Count > 1 ? kept[1] : first;
            float dx = first.X - next.X, dy = first.Y - next.Y;
            float y = dx < -eps ? first.Y + dy * (wx - first.X) / dx : float.NaN;
            kept.Insert(0, first.Y <= wy + eps ? (wx, wy) : float.IsFinite(y) && y >= wy ? (wx, y) : (wx, first.Y));
        }
        var last = kept[^1];
        if (last.Y > wy + eps)
        {
            var prev = kept.Count > 1 ? kept[^2] : last;
            float dx = last.X - prev.X, dy = last.Y - prev.Y;
            float x = dy < -eps ? last.X + dx * (wy - last.Y) / dy : float.NaN;
            kept.Add(last.X <= wx + eps ? (wx, wy) : float.IsFinite(x) && x >= wx ? (x, wy) : (last.X, wy));
        }
        return kept;
    }

    /// <summary>Where the segment from a to b crosses the edge of the region x ≥ wx, y ≥ wy, if it does.</summary>
    private static (float X, float Y)? Crossing((float X, float Y) a, (float X, float Y) b, float wx, float wy)
    {
        (float X, float Y)? best = null;
        float bestT = float.MaxValue;
        void Try(float t)
        {
            if (!(t >= 0 && t <= 1)) return;
            float x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
            if (x < wx - 1e-3f || y < wy - 1e-3f) return;
            // Leaving the region, the first crossing; entering it, the last.
            bool fromInside = a.X >= wx && a.Y >= wy;
            float order = fromInside ? t : -t;
            if (order < bestT) { bestT = order; best = (Math.Max(x, wx), Math.Max(y, wy)); }
        }
        if (b.X != a.X) Try((wx - a.X) / (b.X - a.X));
        if (b.Y != a.Y) Try((wy - a.Y) / (b.Y - a.Y));
        return best;
    }

    /// <summary>Line segments for a quarter curve of the given radius in points: fine enough to look smooth in print.</summary>
    private static int Segments(float radius) => Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(Math.Max(radius, 0)) * 3), 8, 64);
}

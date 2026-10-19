namespace PdfPecker.Svg;

/// <summary>
/// Distances along an SVG path: its length, and the point and direction at a distance from its start, which text on a
/// path is placed by. Each curve is measured along a few chords, and a point on a chord is mapped back to the curve,
/// so the direction there is the curve's own tangent rather than the chord's. A move starts a new subpath without
/// adding to the length, as browsers measure it.
/// </summary>
internal sealed class SvgPathMeasure
{
    /// <summary>The chords each curve is measured along.</summary>
    public const int Chords = 16;

    /// <summary>
    /// The most path commands a measure is made for. Text on a longer path is not drawn, so a path cannot make every
    /// glyph search a huge list of segments.
    /// </summary>
    public const int MaxVerbs = 20_000;

    private readonly struct Segment
    {
        public readonly double Start;   // distance from the path's start
        public readonly float Length;
        public readonly int Curve;      // index of the curve's start point in the point array, or -1 for a line
        public readonly float X0, Y0, X1, Y1; // the line's ends, or the chord's parameter range in T0 = X0 and T1 = X1

        public Segment(double start, float length, int curve, float x0, float y0, float x1, float y1)
        {
            Start = start;
            Length = length;
            Curve = curve;
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }
    }

    private readonly List<Segment> _segments = new();
    private readonly float[] _curves; // per curve: start point and three control points

    /// <summary>The length of the path.</summary>
    public float Length { get; }

    /// <summary>The path's last point, where text that follows text on the path continues.</summary>
    public float EndX { get; }

    /// <summary>The path's last point, where text that follows text on the path continues.</summary>
    public float EndY { get; }

    /// <summary>The bounds of the path's points, curve control points included.</summary>
    public float MinX { get; } = float.MaxValue;
    public float MinY { get; } = float.MaxValue;
    public float MaxX { get; } = float.MinValue;
    public float MaxY { get; } = float.MinValue;

    public SvgPathMeasure(PathBuilder path)
    {
        var pts = path.Points;
        for (int i = 0; i + 1 < pts.Count; i += 2)
        {
            MinX = Math.Min(MinX, pts[i]); MaxX = Math.Max(MaxX, pts[i]);
            MinY = Math.Min(MinY, pts[i + 1]); MaxY = Math.Max(MaxY, pts[i + 1]);
        }
        var curves = new List<float>();
        Length = (float)Walk(path, _segments, curves, out float endX, out float endY);
        _curves = curves.ToArray();
        EndX = endX;
        EndY = endY;
    }

    /// <summary>The length of a path, measured as a measure would measure it but without keeping its segments.</summary>
    public static float LengthOf(PathBuilder path) => (float)Walk(path, null, null, out _, out _);

    private static double Walk(PathBuilder path, List<Segment>? segments, List<float>? curves, out float x, out float y)
    {
        double length = 0;
        float startX = 0, startY = 0;
        x = y = 0;
        int pi = 0;
        var pts = path.Points;
        foreach (var verb in path.Verbs)
        {
            switch (verb)
            {
                case SvgShape.MoveTo:
                    x = startX = pts[pi++];
                    y = startY = pts[pi++];
                    break;
                case SvgShape.LineTo:
                    length += Line(segments, length, x, y, pts[pi], pts[pi + 1]);
                    x = pts[pi++];
                    y = pts[pi++];
                    break;
                case SvgShape.CurveTo:
                {
                    int c = curves?.Count ?? 0;
                    if (curves != null)
                    {
                        curves.Add(x); curves.Add(y);
                        for (int k = 0; k < 6; k++) curves.Add(pts[pi + k]);
                    }
                    float px = x, py = y;
                    for (int i = 1; i <= Chords; i++)
                    {
                        float t = i / (float)Chords;
                        Bezier(x, y, pts[pi], pts[pi + 1], pts[pi + 2], pts[pi + 3], pts[pi + 4], pts[pi + 5], t, out float qx, out float qy);
                        double d = Distance(px, py, qx, qy);
                        segments?.Add(new Segment(length, (float)d, c, (i - 1) / (float)Chords, 0, t, 0));
                        length += d;
                        px = qx;
                        py = qy;
                    }
                    x = pts[pi + 4];
                    y = pts[pi + 5];
                    pi += 6;
                    break;
                }
                case SvgShape.Close:
                    length += Line(segments, length, x, y, startX, startY);
                    x = startX;
                    y = startY;
                    break;
            }
        }
        return length;
    }

    private static double Line(List<Segment>? segments, double start, float x0, float y0, float x1, float y1)
    {
        double d = Distance(x0, y0, x1, y1);
        segments?.Add(new Segment(start, (float)d, -1, x0, y0, x1, y1));
        return d;
    }

    /// <summary>
    /// The point at a distance along the path and the unit tangent there. False when the distance is off the path or
    /// the path has no length.
    /// </summary>
    public bool At(float distance, out float x, out float y, out float cos, out float sin)
    {
        x = y = sin = 0;
        cos = 1;
        if (!(distance >= 0 && distance <= Length) || Length <= 0) return false;
        // The last segment that starts at or before the distance and has a length.
        int lo = 0, hi = _segments.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_segments[mid].Start <= distance) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        while (found > 0 && _segments[found].Length <= 0) found--;
        if (found < 0) return false;
        var s = _segments[found];
        float f = s.Length > 0 ? (float)Math.Clamp((distance - s.Start) / s.Length, 0, 1) : 0;
        float dx, dy;
        if (s.Curve < 0)
        {
            x = s.X0 + (s.X1 - s.X0) * f;
            y = s.Y0 + (s.Y1 - s.Y0) * f;
            dx = s.X1 - s.X0;
            dy = s.Y1 - s.Y0;
        }
        else
        {
            var c = _curves;
            int k = s.Curve;
            float t = s.X0 + (s.X1 - s.X0) * f;
            Bezier(c[k], c[k + 1], c[k + 2], c[k + 3], c[k + 4], c[k + 5], c[k + 6], c[k + 7], t, out x, out y);
            // The derivative of the cubic; where it vanishes (a control point on an end), the chord gives the direction.
            float u = 1 - t;
            dx = 3 * (u * u * (c[k + 2] - c[k]) + 2 * u * t * (c[k + 4] - c[k + 2]) + t * t * (c[k + 6] - c[k + 4]));
            dy = 3 * (u * u * (c[k + 3] - c[k + 1]) + 2 * u * t * (c[k + 5] - c[k + 3]) + t * t * (c[k + 7] - c[k + 5]));
            if (dx * dx + dy * dy < 1e-12f)
            {
                Bezier(c[k], c[k + 1], c[k + 2], c[k + 3], c[k + 4], c[k + 5], c[k + 6], c[k + 7], s.X0, out float ax, out float ay);
                Bezier(c[k], c[k + 1], c[k + 2], c[k + 3], c[k + 4], c[k + 5], c[k + 6], c[k + 7], s.X1, out float bx, out float by);
                dx = bx - ax;
                dy = by - ay;
            }
        }
        float n = MathF.Sqrt(dx * dx + dy * dy);
        if (n > 0 && float.IsFinite(n))
        {
            cos = dx / n;
            sin = dy / n;
        }
        return float.IsFinite(x) && float.IsFinite(y);
    }

    private static void Bezier(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3, float t, out float x, out float y)
    {
        float u = 1 - t;
        float a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        x = a * x0 + b * x1 + c * x2 + d * x3;
        y = a * y0 + b * y1 + c * y2 + d * y3;
    }

    private static double Distance(float x0, float y0, float x1, float y1) => Math.Sqrt((double)(x1 - x0) * (x1 - x0) + (double)(y1 - y0) * (y1 - y0));
}

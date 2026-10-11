namespace PdfPecker.Rendering.Graphics;

/// <summary>A path in user space, as built by the content stream path operators.</summary>
internal sealed class PathData
{
    public const byte MoveTo = 0, LineTo = 1, CubicTo = 2, Close = 3;

    public readonly List<byte> Verbs = new(16);
    public readonly List<double> Points = new(32);
    private double _startX, _startY, _curX, _curY;
    private bool _hasCurrent;

    // Set while the path is exactly one rectangle from "re" (the most common path by far).
    public bool IsSingleRect;
    public double RectX, RectY, RectW, RectH;

    public bool IsEmpty => Verbs.Count == 0;
    public bool HasCurrentPoint => _hasCurrent;
    public double CurrentX => _curX;
    public double CurrentY => _curY;

    public void Clear()
    {
        Verbs.Clear();
        Points.Clear();
        _hasCurrent = false;
        IsSingleRect = false;
    }

    public void Move(double x, double y)
    {
        // Consecutive moves: the last one wins.
        if (Verbs.Count > 0 && Verbs[^1] == MoveTo)
        {
            Points[^2] = x;
            Points[^1] = y;
        }
        else
        {
            Verbs.Add(MoveTo);
            Points.Add(x);
            Points.Add(y);
        }
        _startX = _curX = x;
        _startY = _curY = y;
        _hasCurrent = true;
        IsSingleRect = false;
    }

    public void Line(double x, double y)
    {
        if (!_hasCurrent) { Move(x, y); return; }
        Verbs.Add(LineTo);
        Points.Add(x);
        Points.Add(y);
        _curX = x;
        _curY = y;
        IsSingleRect = false;
    }

    public void Cubic(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        if (!_hasCurrent) Move(x1, y1);
        Verbs.Add(CubicTo);
        Points.Add(x1); Points.Add(y1);
        Points.Add(x2); Points.Add(y2);
        Points.Add(x3); Points.Add(y3);
        _curX = x3;
        _curY = y3;
        IsSingleRect = false;
    }

    public void ClosePath()
    {
        if (!_hasCurrent || Verbs.Count == 0) return;
        if (Verbs[^1] == Close) return;
        Verbs.Add(Close);
        _curX = _startX;
        _curY = _startY;
    }

    public void Rect(double x, double y, double w, double h)
    {
        bool first = Verbs.Count == 0;
        Move(x, y);
        Line(x + w, y);
        Line(x + w, y + h);
        Line(x, y + h);
        ClosePath();
        if (first)
        {
            IsSingleRect = true;
            RectX = x; RectY = y; RectW = w; RectH = h;
        }
    }

    public PathData Clone()
    {
        var p = new PathData();
        p.Verbs.AddRange(Verbs);
        p.Points.AddRange(Points);
        p.IsSingleRect = IsSingleRect;
        p.RectX = RectX; p.RectY = RectY; p.RectW = RectW; p.RectH = RectH;
        return p;
    }

    /// <summary>Appends this path transformed by <paramref name="m"/> to <paramref name="target"/>.</summary>
    public void AppendTransformed(PathData target, in Matrix m)
    {
        int pi = 0;
        foreach (var v in Verbs)
        {
            switch (v)
            {
                case MoveTo:
                {
                    var (x, y) = m.Apply(Points[pi], Points[pi + 1]);
                    target.Move(x, y);
                    pi += 2;
                    break;
                }
                case LineTo:
                {
                    var (x, y) = m.Apply(Points[pi], Points[pi + 1]);
                    target.Line(x, y);
                    pi += 2;
                    break;
                }
                case CubicTo:
                {
                    var (x1, y1) = m.Apply(Points[pi], Points[pi + 1]);
                    var (x2, y2) = m.Apply(Points[pi + 2], Points[pi + 3]);
                    var (x3, y3) = m.Apply(Points[pi + 4], Points[pi + 5]);
                    target.Cubic(x1, y1, x2, y2, x3, y3);
                    pi += 6;
                    break;
                }
                case Close:
                    target.ClosePath();
                    break;
            }
        }
    }
}

/// <summary>A flattened path: polylines (float coordinates), each optionally closed.</summary>
internal sealed class FlatPath
{
    public float[] Pts = new float[256]; // x, y interleaved
    public int Count; // number of points
    public readonly List<int> Starts = new(8); // index of each contour's first point
    public readonly List<bool> Closed = new(8);

    public float MinX, MinY, MaxX, MaxY;

    public void Clear()
    {
        Count = 0;
        Starts.Clear();
        Closed.Clear();
        MinX = MinY = float.MaxValue;
        MaxX = MaxY = float.MinValue;
    }

    public FlatPath() => Clear();

    public int ContourCount => Starts.Count;
    public int ContourEnd(int c) => c + 1 < Starts.Count ? Starts[c + 1] : Count;

    public void BeginContour()
    {
        // Drop a previous contour with no points.
        if (Starts.Count > 0 && Starts[^1] == Count)
        {
            Closed[^1] = false;
            return;
        }
        Starts.Add(Count);
        Closed.Add(false);
    }

    public void CloseContour()
    {
        if (Closed.Count > 0) Closed[^1] = true;
    }

    public void Add(float x, float y)
    {
        if (Count * 2 + 2 > Pts.Length) Array.Resize(ref Pts, Pts.Length * 2);
        Pts[Count * 2] = x;
        Pts[Count * 2 + 1] = y;
        Count++;
        if (x < MinX) MinX = x;
        if (x > MaxX) MaxX = x;
        if (y < MinY) MinY = y;
        if (y > MaxY) MaxY = y;
    }

    /// <summary>Flattens <paramref name="path"/> after transforming it by <paramref name="m"/>, with the given tolerance (in output units).</summary>
    public void Flatten(PathData path, in Matrix m, float tolerance)
    {
        Clear();
        var pts = path.Points;
        int pi = 0;
        double cx = 0, cy = 0;
        bool open = false;
        foreach (var v in path.Verbs)
        {
            switch (v)
            {
                case PathData.MoveTo:
                {
                    var (x, y) = m.Apply(pts[pi], pts[pi + 1]);
                    pi += 2;
                    BeginContour();
                    Add((float)x, (float)y);
                    cx = x; cy = y;
                    open = true;
                    break;
                }
                case PathData.LineTo:
                {
                    var (x, y) = m.Apply(pts[pi], pts[pi + 1]);
                    pi += 2;
                    if (!open) { BeginContour(); Add((float)cx, (float)cy); open = true; }
                    Add((float)x, (float)y);
                    cx = x; cy = y;
                    break;
                }
                case PathData.CubicTo:
                {
                    var (x1, y1) = m.Apply(pts[pi], pts[pi + 1]);
                    var (x2, y2) = m.Apply(pts[pi + 2], pts[pi + 3]);
                    var (x3, y3) = m.Apply(pts[pi + 4], pts[pi + 5]);
                    pi += 6;
                    if (!open) { BeginContour(); Add((float)cx, (float)cy); open = true; }
                    AddCubic(cx, cy, x1, y1, x2, y2, x3, y3, tolerance);
                    cx = x3; cy = y3;
                    break;
                }
                case PathData.Close:
                    if (open)
                    {
                        CloseContour();
                        // A new segment after close starts from the contour's first point.
                        int s = Starts[^1];
                        cx = Pts[s * 2];
                        cy = Pts[s * 2 + 1];
                        open = false;
                    }
                    break;
            }
        }
        if (Starts.Count > 0 && Starts[^1] == Count) { Starts.RemoveAt(Starts.Count - 1); Closed.RemoveAt(Closed.Count - 1); }
    }

    public void AddCubic(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, float tolerance)
    {
        double ddx = Math.Max(Math.Abs(x0 - 2 * x1 + x2), Math.Abs(x1 - 2 * x2 + x3));
        double ddy = Math.Max(Math.Abs(y0 - 2 * y1 + y2), Math.Abs(y1 - 2 * y2 + y3));
        double dd = Math.Sqrt(ddx * ddx + ddy * ddy);
        int n = (int)Math.Ceiling(Math.Sqrt(0.75 * dd / tolerance));
        if (double.IsNaN(dd)) return;
        n = Math.Clamp(n, 1, 256);
        double dt = 1.0 / n;
        for (int i = 1; i < n; i++)
        {
            double t = i * dt, mt = 1 - t;
            double a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
            Add((float)(a * x0 + b * x1 + c * x2 + d * x3), (float)(a * y0 + b * y1 + c * y2 + d * y3));
        }
        Add((float)x3, (float)y3);
    }

    public void Transform(in Matrix m)
    {
        MinX = MinY = float.MaxValue;
        MaxX = MaxY = float.MinValue;
        for (int i = 0; i < Count; i++)
        {
            double x = Pts[i * 2], y = Pts[i * 2 + 1];
            float nx = (float)(m.A * x + m.C * y + m.E), ny = (float)(m.B * x + m.D * y + m.F);
            Pts[i * 2] = nx;
            Pts[i * 2 + 1] = ny;
            if (nx < MinX) MinX = nx;
            if (nx > MaxX) MaxX = nx;
            if (ny < MinY) MinY = ny;
            if (ny > MaxY) MaxY = ny;
        }
    }
}

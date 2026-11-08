namespace PdfPecker.Barcodes.Reading;

/// <summary>A point in image pixels, or in modules of a symbol.</summary>
internal readonly record struct Point2(double X, double Y)
{
    public static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>The z component of the cross product of b - a and c - a: positive when a, b, c turn clockwise on screen.</summary>
    public static double Cross(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}

/// <summary>
/// The projective transform that takes one quadrilateral to another, which maps the modules of a symbol seen at an
/// angle onto the pixels of the image.
/// </summary>
internal readonly struct Perspective
{
    private readonly double _a11, _a12, _a13, _a21, _a22, _a23, _a31, _a32, _a33;

    private Perspective(double a11, double a21, double a31, double a12, double a22, double a32, double a13, double a23, double a33)
    {
        _a11 = a11; _a12 = a12; _a13 = a13;
        _a21 = a21; _a22 = a22; _a23 = a23;
        _a31 = a31; _a32 = a32; _a33 = a33;
    }

    /// <summary>The transform taking the corners p0..p3 to q0..q3, in the same order.</summary>
    public static Perspective Between(Point2 p0, Point2 p1, Point2 p2, Point2 p3, Point2 q0, Point2 q1, Point2 q2, Point2 q3)
        => FromUnitSquare(q0, q1, q2, q3).Times(FromUnitSquare(p0, p1, p2, p3).Adjoint());

    public Point2 Map(double x, double y)
    {
        double d = _a13 * x + _a23 * y + _a33;
        return new Point2((_a11 * x + _a21 * y + _a31) / d, (_a12 * x + _a22 * y + _a32) / d);
    }

    /// <summary>Whether every coefficient is a finite number, which a degenerate quadrilateral breaks.</summary>
    public bool IsFinite => double.IsFinite(_a11) && double.IsFinite(_a12) && double.IsFinite(_a13) && double.IsFinite(_a21)
        && double.IsFinite(_a22) && double.IsFinite(_a23) && double.IsFinite(_a31) && double.IsFinite(_a32) && double.IsFinite(_a33);

    private static Perspective FromUnitSquare(Point2 p0, Point2 p1, Point2 p2, Point2 p3)
    {
        double dx3 = p0.X - p1.X + p2.X - p3.X, dy3 = p0.Y - p1.Y + p2.Y - p3.Y;
        if (dx3 == 0 && dy3 == 0)
            return new Perspective(p1.X - p0.X, p2.X - p1.X, p0.X, p1.Y - p0.Y, p2.Y - p1.Y, p0.Y, 0, 0, 1);
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y;
        double denominator = dx1 * dy2 - dx2 * dy1;
        double a13 = (dx3 * dy2 - dx2 * dy3) / denominator, a23 = (dx1 * dy3 - dx3 * dy1) / denominator;
        return new Perspective(p1.X - p0.X + a13 * p1.X, p3.X - p0.X + a23 * p3.X, p0.X,
                               p1.Y - p0.Y + a13 * p1.Y, p3.Y - p0.Y + a23 * p3.Y, p0.Y, a13, a23, 1);
    }

    private Perspective Adjoint() => new(
        _a22 * _a33 - _a23 * _a32, _a23 * _a31 - _a21 * _a33, _a21 * _a32 - _a22 * _a31,
        _a13 * _a32 - _a12 * _a33, _a11 * _a33 - _a13 * _a31, _a12 * _a31 - _a11 * _a32,
        _a12 * _a23 - _a13 * _a22, _a13 * _a21 - _a11 * _a23, _a11 * _a22 - _a12 * _a21);

    private Perspective Times(Perspective o) => new(
        _a11 * o._a11 + _a21 * o._a12 + _a31 * o._a13, _a11 * o._a21 + _a21 * o._a22 + _a31 * o._a23, _a11 * o._a31 + _a21 * o._a32 + _a31 * o._a33,
        _a12 * o._a11 + _a22 * o._a12 + _a32 * o._a13, _a12 * o._a21 + _a22 * o._a22 + _a32 * o._a23, _a12 * o._a31 + _a22 * o._a32 + _a32 * o._a33,
        _a13 * o._a11 + _a23 * o._a12 + _a33 * o._a13, _a13 * o._a21 + _a23 * o._a22 + _a33 * o._a23, _a13 * o._a31 + _a23 * o._a32 + _a33 * o._a33);

    /// <summary>
    /// Reads a grid of <paramref name="columns"/> x <paramref name="rows"/> modules at their centres, through a transform
    /// from module coordinates to pixels. Null when the grid leaves the image by more than a pixel, or the image's sampling
    /// budget is spent.
    /// </summary>
    public bool[,]? Sample(BinaryImage image, int columns, int rows)
    {
        if (!IsFinite || !image.Spend((long)columns * rows)) return null;
        var grid = new bool[rows, columns];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                var p = Map(x + 0.5, y + 0.5);
                int px = (int)Math.Floor(p.X), py = (int)Math.Floor(p.Y);
                if (px < -1 || py < -1 || px > image.Width || py > image.Height) return null;
                grid[y, x] = image.Get(Math.Clamp(px, 0, image.Width - 1), Math.Clamp(py, 0, image.Height - 1));
            }
        }
        return grid;
    }
}

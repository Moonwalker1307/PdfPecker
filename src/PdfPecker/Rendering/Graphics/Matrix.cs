namespace PdfPecker.Rendering.Graphics;

/// <summary>A PDF affine transform [a b c d e f]: x' = a*x + c*y + e, y' = b*x + d*y + f.</summary>
internal readonly struct Matrix
{
    public readonly double A, B, C, D, E, F;

    public Matrix(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix FromArray(double[]? m)
        => m is { Length: >= 6 } ? new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]) : Identity;

    /// <summary>this × other: applies this first, then <paramref name="o"/>.</summary>
    public Matrix Multiply(in Matrix o) => new(
        A * o.A + B * o.C,
        A * o.B + B * o.D,
        C * o.A + D * o.C,
        C * o.B + D * o.D,
        E * o.A + F * o.C + o.E,
        E * o.B + F * o.D + o.F);

    public static Matrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public double Determinant => A * D - B * C;

    public bool TryInvert(out Matrix inv)
    {
        double det = Determinant;
        if (Math.Abs(det) < 1e-14 || double.IsNaN(det)) { inv = Identity; return false; }
        double id = 1 / det;
        inv = new Matrix(D * id, -B * id, -C * id, A * id, (C * F - D * E) * id, (B * E - A * F) * id);
        return true;
    }

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    public (double X, double Y) ApplyVector(double x, double y) => (A * x + C * y, B * x + D * y);

    /// <summary>Average linear scale factor (square root of the area scale).</summary>
    public double MeanScale => Math.Sqrt(Math.Abs(Determinant));

    /// <summary>Largest and smallest stretch of a unit vector (singular values).</summary>
    public (double Max, double Min) ScaleRange()
    {
        double s1 = A * A + B * B + C * C + D * D;
        double s2 = Math.Sqrt(Math.Max(0, (A * A + B * B - C * C - D * D) * (A * A + B * B - C * C - D * D) + 4 * (A * C + B * D) * (A * C + B * D)));
        return (Math.Sqrt((s1 + s2) / 2), Math.Sqrt(Math.Max(0, (s1 - s2) / 2)));
    }

    public bool IsAxisAligned => Math.Abs(B) < 1e-9 && Math.Abs(C) < 1e-9;

    public override string ToString() => $"[{A} {B} {C} {D} {E} {F}]";
}

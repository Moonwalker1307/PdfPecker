namespace PdfPecker.Layout;

/// <summary>
/// The radii of a box's four rounded corners (border-radius), in points: horizontal and vertical radius of the top-left,
/// top-right, bottom-right and bottom-left corner.
/// </summary>
internal readonly struct CornerRadii
{
    public readonly float TLx, TLy, TRx, TRy, BRx, BRy, BLx, BLy;

    public CornerRadii(float topLeftX, float topLeftY, float topRightX, float topRightY, float bottomRightX, float bottomRightY, float bottomLeftX, float bottomLeftY)
    {
        (TLx, TLy) = Corner(topLeftX, topLeftY);
        (TRx, TRy) = Corner(topRightX, topRightY);
        (BRx, BRy) = Corner(bottomRightX, bottomRightY);
        (BLx, BLy) = Corner(bottomLeftX, bottomLeftY);
    }

    /// <summary>A corner's radii, never negative; a corner with a zero radius on either axis is square.</summary>
    private static (float X, float Y) Corner(float x, float y)
    {
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        return x == 0 || y == 0 ? (0, 0) : (x, y);
    }

    /// <summary>True when every corner is square.</summary>
    public bool IsZero => TLx == 0 && TRx == 0 && BRx == 0 && BLx == 0;

    /// <summary>
    /// The radii of a w × h box: scaled down together when adjacent radii would overlap (CSS Backgrounds 3 §5.5).
    /// Null when every corner is square.
    /// </summary>
    public static CornerRadii? Fit(float topLeftX, float topLeftY, float topRightX, float topRightY, float bottomRightX, float bottomRightY,
        float bottomLeftX, float bottomLeftY, float w, float h)
    {
        var r = new CornerRadii(topLeftX, topLeftY, topRightX, topRightY, bottomRightX, bottomRightY, bottomLeftX, bottomLeftY);
        if (r.IsZero || w <= 0 || h <= 0) return null;
        float f = 1;
        if (r.TLx + r.TRx > 0) f = Math.Min(f, w / (r.TLx + r.TRx));
        if (r.BLx + r.BRx > 0) f = Math.Min(f, w / (r.BLx + r.BRx));
        if (r.TLy + r.BLy > 0) f = Math.Min(f, h / (r.TLy + r.BLy));
        if (r.TRy + r.BRy > 0) f = Math.Min(f, h / (r.TRy + r.BRy));
        return f < 1 ? r.Scale(f) : r;
    }

    /// <summary>Every radius multiplied by <paramref name="f"/>.</summary>
    public CornerRadii Scale(float f) => new(TLx * f, TLy * f, TRx * f, TRy * f, BRx * f, BRy * f, BLx * f, BLy * f);

    /// <summary>The radii of a box inset by the given widths from this one (the padding edge inside a border); null when square.</summary>
    public CornerRadii? Inset(float top, float right, float bottom, float left)
    {
        var r = new CornerRadii(TLx - left, TLy - top, TRx - right, TRy - top, BRx - right, BRy - bottom, BLx - left, BLy - bottom);
        return r.IsZero ? null : r;
    }

    /// <summary>The radii of a box grown by <paramref name="d"/> on every side (a box-shadow's spread); square corners stay square.</summary>
    public CornerRadii Outset(float d)
    {
        static float Grown(float r, float d) => r > 0 ? Math.Max(0, r + d) : 0;
        return new(Grown(TLx, d), Grown(TLy, d), Grown(TRx, d), Grown(TRy, d), Grown(BRx, d), Grown(BRy, d), Grown(BLx, d), Grown(BLy, d));
    }
}

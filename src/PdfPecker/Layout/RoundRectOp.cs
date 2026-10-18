using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// A rounded rectangle (X, Y, W, H) with corner radii <see cref="R"/>: filled, or with <see cref="StrokeWidth"/> stroked
/// along its outline (dashed or dotted when <see cref="Style"/> says so). With a hole, the part inside the hole is left
/// out, which makes a rounded border ring or a shadow outside its box. With <see cref="Clip"/>, only the part inside that
/// polygon is painted: one side of a border whose sides differ.
/// </summary>
internal sealed class RoundRectOp : DrawOp
{
    public float X, Y, W, H;
    public CornerRadii R;
    public uint Color;
    public bool HasHole;
    public float HoleX, HoleY, HoleW, HoleH;
    public CornerRadii HoleR;
    public float StrokeWidth;
    public BorderStyle Style = BorderStyle.Solid;
    /// <summary>The clip polygon as x, y pairs; the op owns the array and moves it with <see cref="Offset"/>.</summary>
    public float[]? Clip;

    public RoundRectOp(float x, float y, float w, float h, CornerRadii r, uint color)
    {
        X = x; Y = y; W = w; H = h;
        R = r;
        Color = color;
    }

    /// <summary>Leaves the rounded rectangle (x, y, w, h) with radii <paramref name="r"/> (square when null) unpainted.</summary>
    public RoundRectOp WithHole(float x, float y, float w, float h, CornerRadii? r)
    {
        HasHole = true;
        HoleX = x; HoleY = y; HoleW = w; HoleH = h;
        HoleR = r ?? default;
        return this;
    }

    protected override void OwnArrays() => Clip = (float[]?)Clip?.Clone();

    public override void Offset(float dx, float dy)
    {
        X += dx; Y += dy;
        HoleX += dx; HoleY += dy;
        if (Clip == null) return;
        for (int i = 0; i < Clip.Length; i += 2)
        {
            Clip[i] += dx;
            Clip[i + 1] += dy;
        }
    }
}

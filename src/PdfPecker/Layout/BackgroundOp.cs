using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// A background layer: an image or a gradient drawn as one tile at (TileX, TileY, TileW, TileH), repeated every StepX /
/// StepY along the axes that repeat, and clipped to the painting area (ClipX, ClipY, ClipW, ClipH). A gradient's geometry
/// is relative to the tile's top-left corner.
/// </summary>
internal sealed class BackgroundOp : DrawOp
{
    public float ClipX, ClipY, ClipW, ClipH;
    public float TileX, TileY, TileW, TileH;
    public float StepX, StepY;
    public bool RepeatX, RepeatY;
    public PdfImage? Image;
    /// <summary>An SVG file, drawn as vectors in each tile.</summary>
    public SvgImage? Svg;
    public Gradient? Gradient;
    /// <summary>Rounded corners of the clip (border-radius).</summary>
    public CornerRadii? ClipRadii;
    /// <summary>
    /// Where the whole image lies relative to the tile's top-left corner, when the tile shows only part of it (a slice of
    /// a border image); null when the image fills the tile. A tile never paints outside itself.
    /// </summary>
    public (float X, float Y, float W, float H)? Inner;
    /// <summary>background-blend-mode as a PDF name; null for normal.</summary>
    public string? BlendMode;

    public override void Offset(float dx, float dy)
    {
        ClipX += dx; ClipY += dy;
        TileX += dx; TileY += dy;
    }

    /// <summary>The flow rectangle actually painted: the clip, narrowed to the tile's row or column on axes that do not repeat.</summary>
    public (float X0, float Y0, float X1, float Y1) Painted()
    {
        float x0 = ClipX, y0 = ClipY, x1 = ClipX + ClipW, y1 = ClipY + ClipH;
        if (!RepeatX) { x0 = Math.Max(x0, TileX); x1 = Math.Min(x1, TileX + TileW); }
        if (!RepeatY) { y0 = Math.Max(y0, TileY); y1 = Math.Min(y1, TileY + TileH); }
        return (x0, y0, x1, y1);
    }

    /// <summary>True when the painted rectangle has an area, so the op is worth keeping.</summary>
    public bool PaintsAnything()
    {
        var (x0, y0, x1, y1) = Painted();
        return x1 > x0 && y1 > y0;
    }
}

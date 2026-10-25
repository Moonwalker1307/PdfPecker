using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>The PDF graphics state (copied on q, restored on Q).</summary>
internal sealed class GraphicsState
{
    public Matrix Ctm = Matrix.Identity;
    public ClipRegion Clip = null!;
    // When a page is run for its text: the boxes of the subpaths of each clip in force, in points on the page, which
    // tell whether a fill paints its middle or only its edge.
    public (double X0, double Y0, double X1, double Y1)[][]? ClipBoxes;

    public ColorSpace FillSpace = ColorSpace.Gray, StrokeSpace = ColorSpace.Gray;
    public uint FillRgb, StrokeRgb; // packed R | G << 8 | B << 16
    public PdfObject? FillPattern, StrokePattern;
    public float[] FillComps = { 0 }, StrokeComps = { 0 };

    public float LineWidth = 1;
    public LineCap Cap;
    public LineJoin Join;
    public float MiterLimit = 10;
    public float[]? Dash;
    public float DashPhase;

    public float FillAlpha = 1, StrokeAlpha = 1;
    public BlendMode Blend;
    public byte[]? SoftMask;

    public PdfFont? Font;
    public double FontSize = 1, CharSpacing, WordSpacing, HScale = 1, Leading, Rise;
    public int TextRender;

    public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
}

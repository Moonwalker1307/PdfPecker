namespace PdfPecker.Rendering.Graphics;

/// <summary>Produces premultiplied colors for a run of pixels (images, shadings, patterns).</summary>
internal interface IShader
{
    void Shade(int y, int x0, Span<uint> colors);
}

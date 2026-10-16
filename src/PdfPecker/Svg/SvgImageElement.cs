using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;

namespace PdfPecker.Svg;

// The image element: an embedded raster image or SVG drawing.
internal sealed partial class SvgImage
{
    private int _imageDepth;

    /// <summary>
    /// Draws an image element: the referenced raster image or SVG drawing, fitted into the element's viewport by its
    /// preserveAspectRatio and clipped to that viewport.
    /// </summary>
    private void RenderImage(Element e, ComputedStyle s, Matrix ctm, float opacity)
    {
        // An SVG image can embed SVG images; a few levels are plenty, and the limit stops a drawing embedding itself.
        if (_load == null || !s.Visible || _imageDepth >= 4) return;
        var href = (e.GetAttribute("href") ?? e.GetAttribute("xlink:href"))?.Trim();
        if (string.IsNullOrEmpty(href)) return;
        object? source;
        _imageDepth++;
        try { source = _load(href); }
        finally { _imageDepth--; }

        // The content box: the raster's pixels, or the drawing's viewBox (else its own size in user units, unscaled).
        float cx = 0, cy = 0, cw, ch;
        bool scales = true;
        switch (source)
        {
            case PdfImage raster:
                cw = raster.Width;
                ch = raster.Height;
                break;
            case SvgImage drawing when drawing.Shapes.Count > 0:
                if (drawing.HasViewBox)
                {
                    (cx, cy, cw, ch) = (drawing.ViewBoxX, drawing.ViewBoxY, drawing.ViewBoxWidth, drawing.ViewBoxHeight);
                }
                else
                {
                    (cw, ch) = drawing.Viewport();
                    scales = false;
                }
                break;
            default:
                return;
        }
        if (!(cw > 0 && ch > 0)) return;

        // A missing width or height comes from the image, keeping its aspect ratio (SVG 2 auto sizing).
        float x = Num(e, "x"), y = Num(e, "y");
        float w = Num(e, "width", float.NaN), h = Num(e, "height", float.NaN);
        if (float.IsNaN(w) && float.IsNaN(h)) { w = cw; h = ch; }
        else if (float.IsNaN(w)) w = h * cw / ch;
        else if (float.IsNaN(h)) h = w * ch / cw;
        if (!(w > 0 && h > 0)) return;

        float sx = 1, sy = 1, tx = 0, ty = 0;
        bool slice = false;
        if (scales)
        {
            var (align, isSlice) = AspectRatio(e.GetAttribute("preserveaspectratio"));
            slice = isSlice;
            sx = w / cw;
            sy = h / ch;
            if (align != "none")
            {
                float scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
                sx = sy = scale;
                float extraX = w - cw * scale, extraY = h - ch * scale;
                tx = align.StartsWith("xmid", StringComparison.Ordinal) ? extraX / 2 : align.StartsWith("xmax", StringComparison.Ordinal) ? extraX : 0;
                ty = align.EndsWith("ymid", StringComparison.Ordinal) ? extraY / 2 : align.EndsWith("ymax", StringComparison.Ordinal) ? extraY : 0;
            }
        }
        var contentToRoot = Matrix.Translate(-cx, -cy).Then(Matrix.Scale(sx, sy)).Then(Matrix.Translate(x + tx, y + ty)).Then(ctm);

        var (verbs, points) = RectOutline(x, y, w, h, ctm);
        var shape = new SvgShape { Kind = SvgShape.KindImage, ImageAlpha = (byte)MathF.Round(Math.Clamp(opacity, 0, 1) * 255) };
        if (shape.ImageAlpha == 0) return;
        if (source is PdfImage image)
        {
            shape.Image = image;
            // The image's unit square onto its content box, first row at the top (user space has y down).
            shape.ImageMatrix = new Matrix(cw, 0, 0, -ch, 0, ch).Then(contentToRoot);
        }
        else
        {
            shape.Children = ((SvgImage)source).Shapes;
            shape.ImageMatrix = contentToRoot;
        }
        Bounds(points, out shape.MinX, out shape.MinY, out shape.MaxX, out shape.MaxY);

        // A sliced raster or any SVG drawing can paint outside the viewport: clip it.
        bool clip = slice || source is SvgImage;
        if (clip) Shapes.Add(new SvgShape { Kind = SvgShape.KindClip, Verbs = verbs, Points = points });
        Shapes.Add(shape);
        if (clip) Shapes.Add(new SvgShape { Kind = SvgShape.KindEndClip });
    }

    /// <summary>The align value (lower case) and slice flag of a preserveAspectRatio attribute; xMidYMid meet by default.</summary>
    private static (string Align, bool Slice) AspectRatio(string? value)
    {
        if (value == null) return ("xmidymid", false);
        var parts = value.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
        int i = parts.Length > 0 && parts[0] == "defer" ? 1 : 0;
        string align = parts.Length > i ? parts[i] : "xmidymid";
        return (align, parts.Length > i + 1 && parts[i + 1] == "slice");
    }
}

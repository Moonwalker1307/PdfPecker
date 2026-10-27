using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>The HTML content of an inline SVG's foreignObject elements, laid out as blocks and drawn with the SVG.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// How deep foreignObject content is laid out inside other foreignObject content. A use element can draw an
    /// ancestor svg again, so without a limit an SVG inside its own foreignObject would be laid out forever.
    /// </summary>
    private const int MaxForeignObjectDepth = 4;

    private int _foreignObjectDepth;

    /// <summary>
    /// Adds an inline SVG drawing in the box (x, y, w, h) to <paramref name="ops"/>, with the content of its
    /// foreignObject elements painted between its shapes in document order.
    /// </summary>
    private void AddSvgDrawing(List<DrawOp> ops, SvgImage svg, float x, float y, float w, float h, ContentSlot? tag)
    {
        if (svg.ForeignObjects is not { } objects || _foreignObjectDepth >= MaxForeignObjectDepth || !(w > 0 && h > 0))
        {
            ops.Add(new SvgOp(x, y, w, h, svg) { Tag = tag });
            return;
        }
        svg.UserSpaceToBox(w, h, false, out float sx, out float sy, out float tx, out float ty, out float vx, out float vy);
        var toBox = Matrix.Translate(-vx, -vy).Then(Matrix.Scale(sx, sy)).Then(Matrix.Translate(tx, ty));
        int from = 0;
        foreach (var fo in objects)
        {
            if (fo.ShapeIndex < 0) continue;
            if (fo.ShapeIndex > from)
            {
                ops.Add(new SvgOp(x, y, w, h, svg.Part(from, fo.ShapeIndex)) { Tag = tag });
                from = fo.ShapeIndex;
            }
            AddForeignObject(ops, fo, toBox, x, y, w, h);
        }
        if (from < svg.Shapes.Count) ops.Add(new SvgOp(x, y, w, h, svg.Part(from, svg.Shapes.Count)) { Tag = tag });
        // Content inside a group, clip, mask or filter goes over the drawing, without those effects.
        foreach (var fo in objects)
            if (fo.ShapeIndex < 0) AddForeignObject(ops, fo, toBox, x, y, w, h);
    }

    /// <summary>
    /// Lays out a foreignObject's content as a block as wide as the element, clipped to the element's box, and draws it
    /// through the element's transform and the SVG's viewBox mapping, inside the SVG's box (x, y, w, h).
    /// </summary>
    private void AddForeignObject(List<DrawOp> ops, SvgForeignObject fo, Matrix toBox, float x, float y, float w, float h)
    {
        if (!(fo.Opacity > 0)) return;
        // The content is laid out in CSS pixels of the element's user space, as points, from the box's top-left corner.
        float fw = fo.Width * 0.75f, fh = fo.Height * 0.75f;
        Captured content;
        _foreignObjectDepth++;
        float lastBottom = _lastContentBottom;
        try { content = Capture(() => LayoutContent(fo.Element, 0, 0, fw, fh, 0)); }
        finally
        {
            _foreignObjectDepth--;
            _lastContentBottom = lastBottom;
        }
        if (content.Ops.Count == 0) return;
        var clip = new ClipOp(x, y, fw, fh, null);
        foreach (var op in content.Ops)
        {
            op.Offset(x, y);
            clip.Ops.Add(op);
        }
        var layer = new LayerOp
        {
            StackingContext = true,
            Opacity = fo.Opacity,
            Transform = Matrix.Scale(1 / 0.75f, 1 / 0.75f).Then(Matrix.Translate(fo.X, fo.Y)).Then(fo.ToRoot).Then(toBox),
            OriginX = x,
            OriginY = y,
        };
        layer.Ops.Add(clip);
        // The SVG's own box clips what it draws.
        var viewport = new ClipOp(x, y, w, h, null);
        viewport.Ops.Add(layer);
        ops.Add(viewport);
        _hasLayers = true;
    }
}

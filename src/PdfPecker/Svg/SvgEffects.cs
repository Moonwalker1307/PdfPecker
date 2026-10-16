using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Svg;

// Patterns, markers, masks and filters.
internal sealed partial class SvgImage
{
    /// <summary>Draws into a fresh list and returns it, leaving the drawing's own shapes as they were.</summary>
    private List<SvgShape> Capture(Action draw)
    {
        var saved = Shapes;
        Shapes = new List<SvgShape>();
        try
        {
            draw();
            return Shapes;
        }
        finally { Shapes = saved; }
    }

    /// <summary>The root viewport in user units: the viewBox, or the width and height (300 × 150 by default).</summary>
    private (float W, float H) Viewport()
        => (HasViewBox ? ViewBoxWidth : (Width ?? 300 * 0.75f) / 0.75f, HasViewBox ? ViewBoxHeight : (Height ?? 150 * 0.75f) / 0.75f);

    /// <summary>
    /// An x, y, width or height attribute of a mask, filter or pattern: a fraction of the bounding box for
    /// objectBoundingBox units, else user units, with percentages of the viewport length given.
    /// </summary>
    private static float UnitLength(string? text, string fallback, bool bbox, float viewport)
    {
        var v = (text ?? fallback).Trim();
        bool percent = v.EndsWith("%", StringComparison.Ordinal);
        if (percent) v = v[..^1];
        else if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) v = v[..^2];
        if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
        {
            if (text == null) return 0;
            return UnitLength(null, fallback, bbox, viewport);
        }
        return percent ? f / 100 * (bbox ? 1 : viewport) : f;
    }

    private static bool IsUserSpace(string? units) => string.Equals(units?.Trim(), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
    private static bool IsBoundingBox(string? units) => string.Equals(units?.Trim(), "objectBoundingBox", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The bounding box, in the user space of <paramref name="ctm"/>, of the shapes drawn from <paramref name="from"/> on.
    /// It is taken in root space and mapped back, which is exact without rotation or skew.
    /// </summary>
    private bool ElementBox(int from, Matrix ctm, out float x, out float y, out float w, out float h)
    {
        x = y = w = h = 0;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = from; i < Shapes.Count; i++)
        {
            var sh = Shapes[i];
            if (sh.Kind is SvgShape.KindPath or SvgShape.KindText or SvgShape.KindFilter or SvgShape.KindImage)
            {
                minX = Math.Min(minX, sh.MinX); minY = Math.Min(minY, sh.MinY);
                maxX = Math.Max(maxX, sh.MaxX); maxY = Math.Max(maxY, sh.MaxY);
            }
        }
        if (!(maxX >= minX) || !(maxY >= minY) || !ctm.TryInvert(out var inv)) return false;
        float x0 = minX, y0 = minY, x1 = maxX, y1 = maxY;
        inv.Apply(ref x0, ref y0);
        inv.Apply(ref x1, ref y1);
        x = Math.Min(x0, x1);
        y = Math.Min(y0, y1);
        w = Math.Abs(x1 - x0);
        h = Math.Abs(y1 - y0);
        return true;
    }

    /// <summary>The corners of the rectangle (x, y, w, h) mapped by <paramref name="m"/>, as a closed path.</summary>
    private static (byte[] Verbs, float[] Points) RectOutline(float x, float y, float w, float h, Matrix m)
    {
        var pts = new[] { x, y, x + w, y, x + w, y + h, x, y + h };
        for (int i = 0; i < 8; i += 2) m.Apply(ref pts[i], ref pts[i + 1]);
        return (new[] { SvgShape.MoveTo, SvgShape.LineTo, SvgShape.LineTo, SvgShape.LineTo, SvgShape.Close }, pts);
    }

    /// <summary>
    /// The transform from a viewBox (x, y, w, h) to a viewport of the given size at the origin, by preserveAspectRatio.
    /// </summary>
    private static Matrix ViewBoxTransform(IReadOnlyList<float> vb, float width, float height, string? preserveAspectRatio)
    {
        float sx = width / vb[2], sy = height / vb[3];
        string align = "xmidymid";
        bool slice = false;
        if (preserveAspectRatio != null)
        {
            var parts = preserveAspectRatio.Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int k = parts.Length > 0 && parts[0] == "defer" ? 1 : 0;
            if (parts.Length > k) align = parts[k];
            if (parts.Length > k + 1) slice = parts[k + 1] == "slice";
        }
        float tx = 0, ty = 0;
        if (align != "none")
        {
            float scale = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = scale;
            float extraX = width - vb[2] * scale, extraY = height - vb[3] * scale;
            tx = align.StartsWith("xmid", StringComparison.Ordinal) ? extraX / 2 : align.StartsWith("xmax", StringComparison.Ordinal) ? extraX : 0;
            ty = align.EndsWith("ymid", StringComparison.Ordinal) ? extraY / 2 : align.EndsWith("ymax", StringComparison.Ordinal) ? extraY : 0;
        }
        return Matrix.Translate(-vb[0], -vb[1]).Then(Matrix.Scale(sx, sy)).Then(Matrix.Translate(tx, ty));
    }

    /// <summary>The first element of a pattern's href chain that has child elements (the one whose content is drawn).</summary>
    private Element? ContentOf(Element pattern)
    {
        for (int depth = 0; pattern != null && depth < 8; depth++)
        {
            if (pattern.Children.Any(c => c is Element)) return pattern;
            var parent = Reference(pattern.GetAttribute("href") ?? pattern.GetAttribute("xlink:href"));
            if (parent == null || parent.Name != "pattern") break;
            pattern = parent;
        }
        return null;
    }

    // ------------------------------------------------------------------ patterns

    /// <summary>
    /// The tile of a pattern painting a shape whose bounds (in its own user space) are given, or null when the pattern
    /// paints nothing.
    /// </summary>
    private SvgPattern? BuildPattern(Element p, float minX, float minY, float maxX, float maxY, Matrix ctm)
    {
        if (_patternDepth >= 4) return null;
        bool bbox = !IsUserSpace(GradientAttr(p, "patternunits"));
        bool contentBox = IsBoundingBox(GradientAttr(p, "patterncontentunits"));
        float bw = maxX - minX, bh = maxY - minY;
        if ((bbox || contentBox) && !(bw > 0 && bh > 0)) return null;
        var (vw, vh) = Viewport();
        float x = UnitLength(GradientAttr(p, "x"), "0", bbox, vw), y = UnitLength(GradientAttr(p, "y"), "0", bbox, vh);
        float w = UnitLength(GradientAttr(p, "width"), "0", bbox, vw), h = UnitLength(GradientAttr(p, "height"), "0", bbox, vh);
        if (bbox)
        {
            x = minX + x * bw;
            y = minY + y * bh;
            w *= bw;
            h *= bh;
        }
        if (!(w > 0 && h > 0) || float.IsInfinity(w) || float.IsInfinity(h)) return null;
        var transform = GradientAttr(p, "patterntransform") is { } pt ? ParseTransform(pt) : Matrix.Identity;
        // The content is placed relative to the tile's top-left corner.
        Matrix content = Matrix.Identity;
        var vb = ParseNumbers(GradientAttr(p, "viewbox"));
        if (vb.Count == 4 && vb[2] > 0 && vb[3] > 0) content = ViewBoxTransform(vb, w, h, GradientAttr(p, "preserveaspectratio"));
        else if (contentBox) content = Matrix.Scale(bw, bh);
        var source = ContentOf(p);
        if (source == null) return null;
        _patternDepth++;
        List<SvgShape> shapes;
        try
        {
            shapes = Capture(() =>
            {
                foreach (var c in source.Children) if (c is Element ce) Render(ce, content, 1, 1);
            });
        }
        finally { _patternDepth--; }
        if (shapes.Count == 0) return null;
        return new SvgPattern { Shapes = shapes, Width = w, Height = h, ToRoot = Matrix.Translate(x, y).Then(transform).Then(ctm) };
    }

    // ------------------------------------------------------------------ markers

    private struct Vertex
    {
        public float X, Y;
        public float InX, InY, OutX, OutY; // directions of the segments arriving and leaving; zero when there are none
    }

    /// <summary>Draws the markers of a path, line, polyline or polygon at its vertices.</summary>
    private void RenderMarkers(ComputedStyle s, Matrix ctm, float opacity, PathBuilder path, int depth)
    {
        if (s.SvgMarkerStart == null && s.SvgMarkerMid == null && s.SvgMarkerEnd == null) return;
        var start = Reference(s.SvgMarkerStart) is { Name: "marker" } ms ? ms : null;
        var mid = Reference(s.SvgMarkerMid) is { Name: "marker" } mm ? mm : null;
        var end = Reference(s.SvgMarkerEnd) is { Name: "marker" } me ? me : null;
        if (start == null && mid == null && end == null) return;
        var vertices = Vertices(path);
        if (vertices.Count == 0) return;
        for (int i = 0; i < vertices.Count; i++)
        {
            var marker = i == 0 ? start : i == vertices.Count - 1 ? end : mid;
            if (marker != null) RenderMarker(marker, vertices[i], i == 0, s.SvgStrokeWidth, ctm, opacity, depth);
            if (i == 0 && vertices.Count == 1 && end != null) RenderMarker(end, vertices[0], false, s.SvgStrokeWidth, ctm, opacity, depth);
        }
    }

    /// <summary>
    /// The vertices of a path: the start of every subpath and the end of every command. A closed subpath gets a vertex
    /// back at its start, and its first vertex also takes the direction of the closing segment.
    /// </summary>
    private static List<Vertex> Vertices(PathBuilder path)
    {
        var result = new List<Vertex>();
        var verbs = path.Verbs;
        var pts = path.Points;
        int pi = 0, endIndex = 0;
        int subStart = -1;               // index in result of the current subpath's first vertex
        float sx = 0, sy = 0, cx = 0, cy = 0;
        float cmdInX = 0, cmdInY = 0, cmdOutX = 0, cmdOutY = 0; // tangents of the current command
        bool inCommand = false;

        void SetOut(float dx, float dy)
        {
            if (result.Count == 0) return;
            var v = result[^1];
            if (v.OutX == 0 && v.OutY == 0) { v.OutX = dx; v.OutY = dy; result[^1] = v; }
        }

        for (int k = 0; k < verbs.Count; k++)
        {
            switch (verbs[k])
            {
                case SvgShape.MoveTo:
                    cx = sx = pts[pi]; cy = sy = pts[pi + 1]; pi += 2;
                    subStart = result.Count;
                    result.Add(new Vertex { X = cx, Y = cy });
                    inCommand = false;
                    break;
                case SvgShape.LineTo:
                {
                    float x = pts[pi], y = pts[pi + 1]; pi += 2;
                    if (!inCommand) { cmdOutX = x - cx; cmdOutY = y - cy; inCommand = true; }
                    cmdInX = x - cx; cmdInY = y - cy;
                    cx = x; cy = y;
                    break;
                }
                case SvgShape.CurveTo:
                {
                    float x1 = pts[pi], y1 = pts[pi + 1], x2 = pts[pi + 2], y2 = pts[pi + 3], x = pts[pi + 4], y = pts[pi + 5]; pi += 6;
                    if (!inCommand)
                    {
                        (cmdOutX, cmdOutY) = x1 != cx || y1 != cy ? (x1 - cx, y1 - cy) : x2 != cx || y2 != cy ? (x2 - cx, y2 - cy) : (x - cx, y - cy);
                        inCommand = true;
                    }
                    (cmdInX, cmdInY) = x != x2 || y != y2 ? (x - x2, y - y2) : x != x1 || y != y1 ? (x - x1, y - y1) : (x - cx, y - cy);
                    cx = x; cy = y;
                    break;
                }
                default: // close: a line back to the subpath's start
                {
                    if (subStart < 0) break;
                    if (inCommand) FinishCommand();
                    float dx = sx - cx, dy = sy - cy;
                    SetOut(dx, dy);
                    var first = result[subStart];
                    var v = new Vertex { X = sx, Y = sy, InX = dx, InY = dy, OutX = first.OutX, OutY = first.OutY };
                    if (dx == 0 && dy == 0) { v.InX = result[^1].InX; v.InY = result[^1].InY; }
                    first.InX = v.InX; first.InY = v.InY;
                    result[subStart] = first;
                    result.Add(v);
                    cx = sx; cy = sy;
                    inCommand = false;
                    break;
                }
            }
            // A command ends after its last verb: with path data, where the parser said; else at every verb.
            bool commandEnds = path.Ends == null || (endIndex < path.Ends.Count && path.Ends[endIndex] == k + 1);
            if (path.Ends != null) while (endIndex < path.Ends.Count && path.Ends[endIndex] <= k + 1) endIndex++;
            if (commandEnds && inCommand) FinishCommand();
        }
        if (inCommand) FinishCommand();
        return result;

        void FinishCommand()
        {
            SetOut(cmdOutX, cmdOutY);
            result.Add(new Vertex { X = cx, Y = cy, InX = cmdInX, InY = cmdInY });
            inCommand = false;
        }
    }

    /// <summary>Draws one marker at a vertex (SVG 1.1 §11.6).</summary>
    private void RenderMarker(Element marker, Vertex v, bool isStart, float strokeWidth, Matrix ctm, float opacity, int depth)
    {
        if (depth > 24 || marker.Style == null || marker.Style.Display == Display.None) return;
        float mw = Num(marker, "markerwidth", 3), mh = Num(marker, "markerheight", 3);
        if (!(mw > 0 && mh > 0)) return;
        float angle = 0;
        var orient = marker.GetAttribute("orient")?.Trim().ToLowerInvariant();
        if (orient is "auto" or "auto-start-reverse")
        {
            angle = Direction(v);
            if (isStart && orient == "auto-start-reverse") angle += 180;
        }
        else if (orient != null)
        {
            var o = orient.EndsWith("deg", StringComparison.Ordinal) ? orient[..^3] : orient;
            if (float.TryParse(o, NumberStyles.Float, CultureInfo.InvariantCulture, out float deg)) angle = deg;
        }
        float scale = IsUserSpace(marker.GetAttribute("markerunits")) ? 1 : strokeWidth;
        if (!(scale > 0)) return;
        var vb = ParseNumbers(marker.GetAttribute("viewbox"));
        var content = vb.Count == 4 && vb[2] > 0 && vb[3] > 0
            ? ViewBoxTransform(vb, mw, mh, marker.GetAttribute("preserveaspectratio")) : Matrix.Identity;
        // The reference point, in the content's coordinates, lands on the vertex.
        float rx = Num(marker, "refx"), ry = Num(marker, "refy");
        content.Apply(ref rx, ref ry);
        var viewport = Matrix.Translate(-rx, -ry).Then(Matrix.Scale(scale, scale)).Then(Matrix.Rotate(angle))
            .Then(Matrix.Translate(v.X, v.Y)).Then(ctm);
        int start = Shapes.Count;
        foreach (var c in marker.Children) if (c is Element ce) Render(ce, content.Then(viewport), opacity, depth + 1);
        if (Shapes.Count == start) return;
        if (marker.GetAttribute("overflow")?.Trim().ToLowerInvariant() is "visible" or "auto") return;
        // The marker's viewport clips its content.
        var (verbs, points) = RectOutline(0, 0, mw, mh, viewport);
        Shapes.Insert(start, new SvgShape { Kind = SvgShape.KindClip, Verbs = verbs, Points = points });
        Shapes.Add(new SvgShape { Kind = SvgShape.KindEndClip });
    }

    /// <summary>The angle, in degrees, a marker with orient="auto" takes at a vertex: halfway between the segments.</summary>
    private static float Direction(Vertex v)
    {
        float inLen = MathF.Sqrt(v.InX * v.InX + v.InY * v.InY), outLen = MathF.Sqrt(v.OutX * v.OutX + v.OutY * v.OutY);
        float dx, dy;
        if (inLen > 0 && outLen > 0)
        {
            dx = v.InX / inLen + v.OutX / outLen;
            dy = v.InY / inLen + v.OutY / outLen;
            if (MathF.Abs(dx) < 1e-6f && MathF.Abs(dy) < 1e-6f) { dx = v.InX; dy = v.InY; } // a reversal: follow the incoming segment
        }
        else if (inLen > 0) { dx = v.InX; dy = v.InY; }
        else if (outLen > 0) { dx = v.OutX; dy = v.OutY; }
        else return 0;
        return MathF.Atan2(dy, dx) * 180 / MathF.PI;
    }

    // ------------------------------------------------------------------ masks

    /// <summary>
    /// Wraps the shapes drawn from <paramref name="start"/> on in a mask: they show through the mask content's
    /// luminance (or alpha), inside the mask region. A mask that cannot apply hides the content.
    /// </summary>
    private void ApplyMask(Element mask, Matrix ctm, int start, int depth)
    {
        bool bbox = !IsUserSpace(mask.GetAttribute("maskunits"));
        bool contentBox = IsBoundingBox(mask.GetAttribute("maskcontentunits"));
        bool hasBox = ElementBox(start, ctm, out float bx, out float by, out float bw, out float bh) && bw > 0 && bh > 0;
        if ((bbox || contentBox) && !hasBox)
        {
            Shapes.RemoveRange(start, Shapes.Count - start);
            return;
        }
        var (vw, vh) = Viewport();
        float x = UnitLength(mask.GetAttribute("x"), "-10%", bbox, vw), y = UnitLength(mask.GetAttribute("y"), "-10%", bbox, vh);
        float w = UnitLength(mask.GetAttribute("width"), "120%", bbox, vw), h = UnitLength(mask.GetAttribute("height"), "120%", bbox, vh);
        if (bbox)
        {
            x = bx + x * bw;
            y = by + y * bh;
            w *= bw;
            h *= bh;
        }
        if (!(w > 0 && h > 0))
        {
            Shapes.RemoveRange(start, Shapes.Count - start);
            return;
        }
        var contentCtm = contentBox ? Matrix.Scale(bw, bh).Then(Matrix.Translate(bx, by)).Then(ctm) : ctm;
        _maskDepth++;
        List<SvgShape> content;
        try
        {
            content = Capture(() =>
            {
                foreach (var c in mask.Children) if (c is Element ce) Render(ce, contentCtm, 1, depth + 1);
            });
        }
        finally { _maskDepth--; }
        if (content.Count == 0)
        {
            Shapes.RemoveRange(start, Shapes.Count - start);
            return;
        }
        var (verbs, points) = RectOutline(x, y, w, h, ctm);
        Shapes.Insert(start, new SvgShape
        {
            Kind = SvgShape.KindMask, Verbs = verbs, Points = points, Children = content, MaskAlpha = mask.Style?.SvgMaskAlpha == true,
        });
        Shapes.Add(new SvgShape { Kind = SvgShape.KindEndMask });
    }

    // ------------------------------------------------------------------ filters

    /// <summary>
    /// Replaces the shapes drawn from <paramref name="start"/> on with one filtered shape, drawn as an image of the
    /// filter region. Returns false when the filter leaves nothing to draw.
    /// </summary>
    private bool ApplyFilter(Element filter, Matrix ctm, int start)
    {
        bool bbox = !IsUserSpace(filter.GetAttribute("filterunits"));
        bool primitiveBox = IsBoundingBox(filter.GetAttribute("primitiveunits"));
        bool hasBox = ElementBox(start, ctm, out float bx, out float by, out float bw, out float bh);
        var (vw, vh) = Viewport();
        float x = UnitLength(filter.GetAttribute("x"), "-10%", bbox, vw), y = UnitLength(filter.GetAttribute("y"), "-10%", bbox, vh);
        float w = UnitLength(filter.GetAttribute("width"), "120%", bbox, vw), h = UnitLength(filter.GetAttribute("height"), "120%", bbox, vh);
        if (bbox)
        {
            if (!hasBox || !(bw > 0 && bh > 0)) return Drop(start);
            x = bx + x * bw;
            y = by + y * bh;
            w *= bw;
            h *= bh;
        }
        if (!(w > 0 && h > 0)) return Drop(start);
        var effect = SvgFilter.Parse(filter, ctm, primitiveBox, (bx, by, bw, bh), (x, y, w, h), (vw, vh));
        if (effect == null) return Drop(start);
        // feImage draws a file or an element of this drawing: its shapes are rendered with the filter's other inputs.
        foreach (var p in effect.Primitives)
        {
            if (p.Kind != SvgFilter.Kind.Image || p.Element is not { } image) continue;
            var href = (image.GetAttribute("href") ?? image.GetAttribute("xlink:href"))?.Trim();
            _filterDepth++; // an element can draw itself through its own filter's feImage
            try
            {
                p.Shapes = Capture(() =>
                {
                    if (href != null && href.StartsWith("#", StringComparison.Ordinal))
                    {
                        if (Reference(href) is { } target) Render(target, ctm, 1, 1);
                    }
                    else DrawImage(href, p.UX, p.UY, p.UW, p.UH, image.GetAttribute("preserveaspectratio"), ctm, 1);
                });
            }
            finally { _filterDepth--; }
            p.Element = null;
        }
        // The filter region in root space: the bounds of its transformed corners.
        var (_, corners) = RectOutline(x, y, w, h, ctm);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < 8; i += 2)
        {
            minX = Math.Min(minX, corners[i]); maxX = Math.Max(maxX, corners[i]);
            minY = Math.Min(minY, corners[i + 1]); maxY = Math.Max(maxY, corners[i + 1]);
        }
        effect.X = minX;
        effect.Y = minY;
        effect.W = maxX - minX;
        effect.H = maxY - minY;
        var children = Shapes.GetRange(start, Shapes.Count - start);
        var shape = new SvgShape { Kind = SvgShape.KindFilter, Children = children, Filter = effect };
        // Bounds of the content itself, for the bounding boxes of clip paths and masks around the filter.
        shape.MinX = shape.MinY = float.MaxValue;
        shape.MaxX = shape.MaxY = float.MinValue;
        foreach (var c in children)
        {
            if (c.Kind is not (SvgShape.KindPath or SvgShape.KindText or SvgShape.KindFilter or SvgShape.KindImage)) continue;
            shape.MinX = Math.Min(shape.MinX, c.MinX); shape.MinY = Math.Min(shape.MinY, c.MinY);
            shape.MaxX = Math.Max(shape.MaxX, c.MaxX); shape.MaxY = Math.Max(shape.MaxY, c.MaxY);
        }
        if (shape.MinX > shape.MaxX) { shape.MinX = minX; shape.MinY = minY; shape.MaxX = maxX; shape.MaxY = maxY; }
        Shapes.RemoveRange(start, Shapes.Count - start);
        Shapes.Add(shape);
        return true;
    }

    private bool Drop(int start)
    {
        Shapes.RemoveRange(start, Shapes.Count - start);
        return false;
    }
}

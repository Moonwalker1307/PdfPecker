using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Layout;

/// <summary>shape-outside (CSS Shapes 1): the shape that the lines beside a float wrap around, as Chrome builds it.</summary>
internal sealed partial class LayoutEngine
{
    // A shape scanned row by row (an image, a gradient or path()) may cover at most this many CSS pixels, and its float
    // at most this many rows; past either, the lines wrap around the float's margin box. Scanning costs one step per
    // pixel, and growing the rows by shape-margin one per row and pixel of margin, which is held to the second bound.
    private const long MaxShapePixels = 4_000_000;
    private const int MaxShapeRows = 20_000;
    private const int MaxShapeMarginPixels = 1024;

    private const float Px = 0.75f; // one CSS pixel in points

    // Scanned shapes by float, margin box size and containing block width: a float laid out again keeps its shape.
    private readonly Dictionary<(Element, float, float, float), ShapeRows?> _shapeRows = new();

    /// <summary>
    /// The float area of a float whose margin box is at (<paramref name="x"/>, <paramref name="y"/>), w × h, in a
    /// containing block <paramref name="cbW"/> wide; null when it is the margin box itself.
    /// </summary>
    private FloatShape? FloatShapeOf(Element e, float x, float y, float w, float h, float cbW)
    {
        var s = e.Style;
        if (s.ShapeOutside is not { } outside) return null;
        float ml = Margin(s.MarginLeft, cbW), mr = Margin(s.MarginRight, cbW), mt = Margin(s.MarginTop, cbW), mb = Margin(s.MarginBottom, cbW);
        float pl = s.PaddingLeft.Resolve(cbW), pr = s.PaddingRight.Resolve(cbW), pt = s.PaddingTop.Resolve(cbW), pb = s.PaddingBottom.Resolve(cbW);
        float margin = Math.Max(0, s.ShapeMargin.Resolve(cbW));
        // The reference box; an image is laid out in the content box.
        var box = outside.Image != null ? ShapeBox.ContentBox : outside.Shape!.Box;
        float rx = x, ry = y, rw = w, rh = h;
        if (box != ShapeBox.MarginBox) { rx += ml; ry += mt; rw -= ml + mr; rh -= mt + mb; }
        if (box is ShapeBox.PaddingBox or ShapeBox.ContentBox) { rx += s.BorderLeft; ry += s.BorderTop; rw -= s.BorderLeft + s.BorderRight; rh -= s.BorderTop + s.BorderBottom; }
        if (box == ShapeBox.ContentBox) { rx += pl; ry += pt; rw -= pl + pr; rh -= pt + pb; }
        rw = Math.Max(0, rw);
        rh = Math.Max(0, rh);
        if (outside.Image is { } image)
        {
            var key = (e, w, h, cbW);
            if (!_shapeRows.TryGetValue(key, out var rows))
            {
                // The margin box in pixels from the content box's top-left corner, which the rows keep to.
                int top = (int)MathF.Round(-(mt + s.BorderTop + pt) / Px);
                int boxW = (int)MathF.Round(w / Px), boxH = (int)MathF.Round(h / Px);
                rows = ImageRows(e, image, rw, rh, top, boxH, s.ShapeImageThreshold);
                if (rows != null) rows = rows.WithMargin(Math.Min(MarginPixels(margin), (int)(Math.Max(boxW, boxH) * MathF.Sqrt(2)))).Seal();
                _shapeRows[key] = rows;
            }
            return rows == null ? null : FloatShape.Raster(rows, rx, ry);
        }
        var shape = outside.Shape!;
        switch (shape.Kind)
        {
            case ClipShape.ShapeKind.Box:
            {
                var radii = BoxPainter.Radii(s, rw, rh);
                if (radii == null && margin == 0 && box == ShapeBox.MarginBox) return null;
                return FloatShape.Box(rx, ry, rw, rh, radii, margin);
            }
            case ClipShape.ShapeKind.Inset:
            {
                var (ix, iy, iw, ih, radii) = shape.ResolveInset(rw, rh);
                return FloatShape.Box(rx + ix, ry + iy, iw, ih, radii, margin);
            }
            case ClipShape.ShapeKind.Circle:
            case ClipShape.ShapeKind.Ellipse:
            {
                var (cx, cy, erx, ery) = shape.ResolveEllipse(rw, rh);
                return FloatShape.Ellipse(rx + cx, ry + cy, erx, ery, margin);
            }
            case ClipShape.ShapeKind.Polygon:
            {
                var points = shape.ResolvePolygon(rw, rh);
                for (int i = 0; i + 1 < points.Length; i += 2) { points[i] += rx; points[i + 1] += ry; }
                return FloatShape.Polygon(points, margin);
            }
            default:
            {
                var key = (e, w, h, cbW);
                if (!_shapeRows.TryGetValue(key, out var rows))
                {
                    rows = PathRows(shape.PathData!, out int width);
                    if (rows != null) rows = rows.WithMargin(Math.Min(MarginPixels(margin), (int)(Math.Max(rows.Count, width) * MathF.Sqrt(2))));
                    _shapeRows[key] = rows;
                }
                return rows == null ? null : FloatShape.Raster(rows, rx, ry);
            }
        }
    }

    private static float Margin(Length l, float cbW) => l.IsAuto ? 0 : l.Resolve(cbW);

    /// <summary>shape-margin in whole pixels, rounded up as Chrome rounds it.</summary>
    private static int MarginPixels(float margin) => (int)Math.Min(MaxShapeMarginPixels, MathF.Ceiling(margin / Px - 0.001f));

    /// <summary>
    /// A path() shape scanned as Chrome rasterizes it, with anti-aliasing and every touched pixel in the shape: each row
    /// runs from the first to the last pixel any edge crosses. Null when it is too large to scan.
    /// </summary>
    private static ShapeRows? PathRows(Svg.PathBuilder builder, out int width)
    {
        width = 0;
        var path = new PathData();
        int pi = 0;
        var pts = builder.Points;
        foreach (var verb in builder.Verbs)
        {
            switch (verb)
            {
                case Svg.SvgShape.MoveTo: path.Move(pts[pi], pts[pi + 1]); pi += 2; break;
                case Svg.SvgShape.LineTo: path.Line(pts[pi], pts[pi + 1]); pi += 2; break;
                case Svg.SvgShape.CurveTo: path.Cubic(pts[pi], pts[pi + 1], pts[pi + 2], pts[pi + 3], pts[pi + 4], pts[pi + 5]); pi += 6; break;
                default: path.ClosePath(); break;
            }
        }
        var flat = new FlatPath();
        flat.Flatten(path, Rendering.Graphics.Matrix.Identity, 0.05f);
        if (flat.Count == 0 || !(flat.MaxX > 0) || !(flat.MaxY > 0)) return null;
        width = (int)MathF.Ceiling(flat.MaxX);
        int height = (int)MathF.Ceiling(flat.MaxY);
        if (height > MaxShapeRows || (long)width * height > MaxShapePixels) return null;
        var rows = new ShapeRows(0, height);
        for (int c = 0; c < flat.ContourCount; c++)
        {
            int start = flat.Starts[c], end = flat.ContourEnd(c);
            // Every contour is filled, so it is closed back to its first point.
            for (int k = start; k < end; k++)
            {
                int next = k + 1 < end ? k + 1 : start;
                ScanEdge(rows, flat.Pts[k * 2], flat.Pts[k * 2 + 1], flat.Pts[next * 2], flat.Pts[next * 2 + 1], width);
            }
        }
        return rows.Seal();
    }

    /// <summary>Adds to each row the pixels the edge from (x0, y0) to (x1, y1) crosses there.</summary>
    private static void ScanEdge(ShapeRows rows, float x0, float y0, float x1, float y1, int width)
    {
        float top = Math.Min(y0, y1), bottom = Math.Max(y0, y1);
        int r0 = Math.Max(0, (int)MathF.Floor(top)), r1 = Math.Min(rows.Count - 1, (int)MathF.Ceiling(bottom) - 1);
        for (int r = r0; r <= r1; r++)
        {
            float a = Math.Max(top, r), b = Math.Min(bottom, r + 1);
            if (b < a) continue;
            float xa = y0 == y1 ? x0 : x0 + (a - y0) * (x1 - x0) / (y1 - y0);
            float xb = y0 == y1 ? x1 : x0 + (b - y0) * (x1 - x0) / (y1 - y0);
            float lo = Math.Max(0, Math.Min(xa, xb)), hi = Math.Min(width, Math.Max(xa, xb));
            if (hi < lo) continue;
            int p0 = (int)MathF.Floor(lo), p1 = Math.Max(p0 + 1, (int)MathF.Ceiling(hi));
            rows.Unite(r, p0, Math.Min(width, p1));
        }
    }

    /// <summary>
    /// The rows of an image or gradient whose alpha is above <paramref name="threshold"/>, in pixels from the content
    /// box's top-left corner. The image is drawn as Chrome draws it for a shape: an img element's own image where
    /// object-fit puts it, any other image at its natural size from the content box's corner, and a gradient over the
    /// content box. Rows outside the content box and the margin box are left out. Null when the image cannot be used.
    /// </summary>
    private ShapeRows? ImageRows(Element e, BackgroundImage image, float cw, float ch, int marginTop, int marginHeight, float threshold)
    {
        if (marginHeight <= 0 || marginHeight > MaxShapeRows) return null;
        PdfImage? bitmap = null;
        float ix = 0, iy = 0, iw, ih;
        if (image.Url is { } url)
        {
            bitmap = _images.Load(url);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return null;
            iw = bitmap.Width * Px;
            ih = bitmap.Height * Px;
            if (e.IsPicture && _images.Load(e.GetAttribute("src")) is { Width: > 0, Height: > 0 } own)
                (ix, iy, iw, ih) = ObjectFitRect(e.Style, own, cw, ch);
        }
        else
        {
            iw = cw;
            ih = ch;
        }
        int x0 = (int)MathF.Round(ix / Px), y0 = (int)MathF.Round(iy / Px);
        int w = (int)MathF.Round((ix + iw) / Px) - x0, h = (int)MathF.Round((iy + ih) / Px) - y0;
        if (w <= 0 || h <= 0 || (long)w * h > MaxShapePixels) return null;
        byte cut = (byte)(threshold * 255);
        int contentH = (int)MathF.Floor(ch / Px + 0.001f);
        int from = Math.Max(0, Math.Max(marginTop, y0)), to = Math.Min(contentH, Math.Min(y0 + h, marginTop + marginHeight));
        var rows = new ShapeRows(marginTop, marginHeight);
        if (bitmap != null)
        {
            if (!bitmap.TryAlpha(out var alpha)) return null;
            for (int y = from; y < to; y++)
            {
                if (alpha == null)
                {
                    // An opaque image is in the shape wherever it is drawn.
                    if (cut < 255) rows.Unite(y, x0, x0 + w);
                    continue;
                }
                int sy = Math.Min(bitmap.Height - 1, (int)((y - y0 + 0.5f) * bitmap.Height / h));
                ScanRow(rows, y, x0, w, cut, sx => alpha[sy * bitmap.Width + Math.Min(bitmap.Width - 1, (int)((sx + 0.5f) * bitmap.Width / w))]);
            }
        }
        else
        {
            var g = image.Gradient!.Resolve(cw, ch, 0xFFFFFFFF);
            if (!g.Transform.TryInvert(out var toGradient)) return null;
            for (int y = from; y < to; y++)
                ScanRow(rows, y, x0, w, cut, sx => GradientAlpha(g, toGradient, (x0 + sx + 0.5f) * Px, (y + 0.5f) * Px));
        }
        return rows.Seal();
    }

    /// <summary>Sets row <paramref name="y"/> to run from the first to the last of its w pixels whose alpha is above the cut.</summary>
    private static void ScanRow(ShapeRows rows, int y, int x0, int w, byte cut, Func<int, byte> alphaAt)
    {
        int first = -1, last = -1;
        for (int sx = 0; sx < w; sx++)
        {
            if (alphaAt(sx) <= cut) continue;
            if (first < 0) first = sx;
            last = sx;
        }
        if (first >= 0) rows.Unite(y, x0 + first, x0 + last + 1);
    }

    /// <summary>Where object-fit and object-position draw an image in a content box w × h, from its top-left corner.</summary>
    private static (float X, float Y, float W, float H) ObjectFitRect(ComputedStyle s, PdfImage image, float w, float h)
    {
        if (s.ObjectFit == ObjectFit.Fill || w <= 0 || h <= 0) return (0, 0, w, h);
        float nw = image.Width * Px, nh = image.Height * Px;
        float contain = Math.Min(w / nw, h / nh);
        float k = s.ObjectFit switch
        {
            ObjectFit.Contain => contain,
            ObjectFit.Cover => Math.Max(w / nw, h / nh),
            ObjectFit.ScaleDown => Math.Min(1, contain),
            _ => 1, // none
        };
        float dw = nw * k, dh = nh * k;
        return (s.ObjectPositionX.Resolve(w - dw), s.ObjectPositionY.Resolve(h - dh), dw, dh);
    }

    /// <summary>The alpha, 0 to 255, of a gradient at (x, y) in its box.</summary>
    private static byte GradientAlpha(Gradient g, Svg.Matrix toGradient, float x, float y)
    {
        if (g.Alphas is not { } alphas) return 255;
        toGradient.Apply(ref x, ref y);
        float t;
        switch (g.Kind)
        {
            case GradientKind.Radial:
            {
                float d = MathF.Sqrt((x - g.X1) * (x - g.X1) + (y - g.Y1) * (y - g.Y1));
                t = g.R1 > g.R0 ? (d - g.R0) / (g.R1 - g.R0) : 1;
                break;
            }
            case GradientKind.Conic:
            {
                // Clockwise from the start angle, 0 being up.
                float deg = MathF.Atan2(x - g.X0, g.Y0 - y) * 180 / MathF.PI - g.From;
                t = ((deg % 360) + 360) % 360 / 360;
                if (g.RepeatPeriod > 0) t = (t - g.RepeatStart) / g.RepeatPeriod;
                break;
            }
            default:
            {
                float dx = g.X1 - g.X0, dy = g.Y1 - g.Y0, len = dx * dx + dy * dy;
                t = len > 0 ? ((x - g.X0) * dx + (y - g.Y0) * dy) / len : 0;
                break;
            }
        }
        if (g.Spread == GradientSpread.Repeat || g.Kind == GradientKind.Conic && g.RepeatPeriod > 0) t -= MathF.Floor(t);
        var offsets = g.Offsets;
        if (offsets.Length == 0) return 255;
        if (!(t > offsets[0])) return (byte)alphas[0];
        for (int i = 1; i < offsets.Length; i++)
        {
            if (t > offsets[i]) continue;
            float span = offsets[i] - offsets[i - 1];
            float f = span > 0 ? (t - offsets[i - 1]) / span : 1;
            return (byte)MathF.Round(alphas[i - 1] + (alphas[i] - (float)alphas[i - 1]) * f);
        }
        return (byte)alphas[^1];
    }
}

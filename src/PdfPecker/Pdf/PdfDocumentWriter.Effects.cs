using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>The pixels of HTML filters with blur() or drop-shadow(), and of blurred text-shadows.</summary>
internal sealed partial class PdfDocumentWriter
{
    // The flow range of the page whose flowing content is being written; NaN while writing anything else.
    private float _flowTop = float.NaN, _flowBottom = float.NaN;

    // The filtered image of each layer, made once and drawn on every page the layer reaches: its /Xr number (0 when
    // there is nothing to draw), its flow rectangle, and the flow rectangle of the content it was made from.
    private readonly Dictionary<LayerOp, (int Index, float X, float Y, float W, float H, float ContentTop, float ContentBottom)> _layerRasters
        = new(ReferenceEqualityComparer.Instance);

    // The images of blurred text-shadows by what they show, relative to the content's bounds: the same words in the same
    // font under the same shadows are made once, wherever they are.
    private Dictionary<string, (int Index, float Dx, float Dy, float W, float H, float ContentBottom)>? _shadowImages;
    private Dictionary<TextFont, int>? _shadowFonts;

    /// <summary>
    /// Draws the filtered pixels of a layer: the shadows under its content, or the blurred content. Returns false when
    /// the filter is beyond its work budget and was left out.
    /// </summary>
    private bool WriteLayerRaster(ByteBuffer b, LayerOp layer, float pageTop)
    {
        if (!_layerRasters.TryGetValue(layer, out var r)) _layerRasters[layer] = r = layer.TextShadow ? RasterizeShadow(layer) : RasterizeLayer(layer);
        if (r.Index < 0) return false;
        if (r.Index == 0) return true;
        SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
        b.Ascii("q ");
        // Content that goes on to another page is in the image too: keep to this page's part of the flow.
        if (!float.IsNaN(_flowTop) && (r.ContentTop < _flowTop - 0.5f || r.ContentBottom > _flowBottom + 0.5f))
        {
            float top = Math.Max(r.Y, _flowTop), bottom = Math.Min(r.Y + r.H, _flowBottom);
            if (bottom <= top) { b.Ascii("Q\n"); return true; }
            b.Num(PX(r.X)).Sp().Num(PY(bottom, pageTop)).Sp().Num(r.W).Sp().Num(bottom - top).Ascii(" re W n ");
        }
        b.Num(r.W).Ascii(" 0 0 ").Num(r.H).Sp().Num(PX(r.X)).Sp().Num(PY(r.Y + r.H, pageTop)).Ascii(" cm /Xr").Int(r.Index).Ascii(" Do Q\n");
        return true;
    }

    /// <summary>
    /// Renders a layer's content to pixels through a one-page PDF of its own, runs the layer's filter on them, and keeps
    /// the result as an image for /Xr.
    /// </summary>
    private (int, float, float, float, float, float, float) RasterizeLayer(LayerOp layer)
    {
        var f = layer.Raster!;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var op in layer.Ops) Bounds(op, ref x0, ref y0, ref x1, ref y1);
        if (!(x1 > x0 && y1 > y0)) return default;
        // The filter region: the content grown by how far the blurs and shadows reach.
        float rx = x0 + f.X, ry = y0 + f.Y, rw = x1 - x0 + f.W, rh = y1 - y0 + f.H;
        if (!(rw > 0 && rh > 0) || ry + rh <= 1 || rw > 1e5f || rh > 1e6f) return default;
        float density = layer.RasterDensity > 0 ? layer.RasterDensity : FilterPixelsPerPoint;
        double pw = Math.Ceiling(rw * density), ph = Math.Ceiling(rh * density);
        if (pw * ph > MaxFilterPixels)
        {
            double k = Math.Sqrt(MaxFilterPixels / (pw * ph));
            pw = Math.Max(1, Math.Floor(pw * k));
            ph = Math.Max(1, Math.Floor(ph * k));
        }
        int w = (int)pw, h = (int)ph;
        if (!WithinRenderBudget(f, layer.Ops, w, h)) return (-1, 0, 0, 0, 0, 0, 0); // drawn without the filter
        // A page of w × h points shows the flow rectangle (rx, ry, rw, rh) scaled to fill it, one point per pixel: the
        // margins move the flow's origin off the page (and keep all of it on the first page), and a layer scales it.
        var page = new PageSetup { Width = w, Height = h, MarginLeft = -rx, MarginTop = -ry, MarginBottom = -1e6f };
        var scaled = new LayerOp { Transform = Matrix.Scale(w / rw, h / rh), OriginX = rx, OriginY = ry };
        scaled.Ops.AddRange(layer.Ops);
        byte[] pdf;
        try
        {
            var writer = new PdfDocumentWriter(page, compress: false, CompressionLevel.NoCompression) { Cancellation = Cancellation, _filterBudget = _filterBudget, _gradientBudget = _gradientBudget };
            pdf = writer.Write(new List<DrawOp> { scaled }, ry + rh, new Dictionary<string, float>(), 0, null, null, null, null, null);
        }
        catch (PdfLimitException) { return default; }
        if (f.TextShadows)
        {
            // Only the shape of the text matters to its shadows.
            var rendered = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Width = w, Height = h, Transparent = true }, Cancellation);
            if (rendered.Width != w || rendered.Height != h) return default;
            var px = rendered.Premultiplied;
            var alpha = new float[px.Length];
            for (int i = 0; i < px.Length; i++) alpha[i] = (px[i] >> 24) / 255f;
            if (f.ApplyShadows(alpha, w, h, w / rw, h / rh, Cancellation) is not var (rgb, mask)) return default;
            _rasters.Add(new PdfImage
            {
                Width = w, Height = h, Data = PdfImage.DeflatePredicted(rgb, w, h, 3, out string colorParms), DecodeParms = colorParms,
                SMask = new PdfImage
                {
                    Width = w, Height = h, ColorSpace = "/DeviceGray", Data = PdfImage.DeflatePredicted(mask, w, h, 1, out string alphaParms), DecodeParms = alphaParms,
                },
            });
            return (_rasters.Count, rx, ry, rw, rh, y0, y1);
        }
        var source = RenderPremultiplied(pdf, w, h);
        if (source == null) return default;
        var image = FilterImage(f.Apply(source, w, h, w / rw, h / rh, Cancellation), w, h);
        if (image == null) return default;
        _rasters.Add(image);
        return (_rasters.Count, rx, ry, rw, rh, y0, y1);
    }

    // A canvas pixel is cleared, drawn, composited and for a mask turned into mask values: about twice the work of a
    // pixel of a filter primitive.
    private const long CanvasWorkPerPixel = 2;

    /// <summary>
    /// Whether a filter may run on w × h pixels of ops rendered through a page of their own, counting its work against the
    /// document's budget when it may. Besides the filter's own work, the renderer paints every transparency group and
    /// soft mask in the ops on a canvas of its own the size of the whole image, so hundreds of masked boxes nested in
    /// each other cost hundreds of full images. A filter whose canvases alone are beyond the document's budget never runs.
    /// </summary>
    private bool WithinRenderBudget(SvgFilter f, List<DrawOp> ops, int w, int h)
    {
        long canvasWork = OffscreenCanvases(ops) * w * h * CanvasWorkPerPixel;
        return canvasWork <= SvgFilter.MaxDocumentWork && f.WithinBudget(w, h) && _filterBudget.TrySpend(f.DocumentWork(w, h) + canvasWork);
    }

    /// <summary>How many offscreen canvases the renderer paints for the ops: one per transparency group and soft mask.</summary>
    private static long OffscreenCanvases(List<DrawOp> ops)
    {
        long n = 0;
        foreach (var op in ops)
        {
            if (op is LayerOp l) n += (l.NeedsGroup ? 1 : 0) + (l.Mask?.Length ?? 0) + (l.MaskFill != null ? 1 : 0);
            if (op is GroupOp g) n += OffscreenCanvases(g.Ops);
        }
        return n;
    }

    /// <summary>
    /// The image of a layer of blurred text-shadows, shared with every earlier layer that holds the same text and
    /// decorations at the same places relative to each other under the same filter.
    /// </summary>
    private (int, float, float, float, float, float, float) RasterizeShadow(LayerOp layer)
    {
        var key = ShadowKey(layer, out float x0, out float y0);
        _shadowImages ??= new Dictionary<string, (int, float, float, float, float, float)>(StringComparer.Ordinal);
        if (key != null && _shadowImages.TryGetValue(key, out var shared))
            return shared.Index == 0 ? default : (shared.Index, x0 + shared.Dx, y0 + shared.Dy, shared.W, shared.H, y0, y0 + shared.ContentBottom);
        var r = RasterizeLayer(layer);
        if (key != null) _shadowImages[key] = (r.Item1, r.Item2 - x0, r.Item3 - y0, r.Item4, r.Item5, r.Item7 - y0);
        return r;
    }

    /// <summary>
    /// What a layer of text-shadows draws, as a string, with positions relative to its bounds' top-left corner
    /// (<paramref name="x0"/>, <paramref name="y0"/>); null when it holds something else.
    /// </summary>
    private string? ShadowKey(LayerOp layer, out float x0, out float y0)
    {
        float bx1 = float.MinValue, by1 = float.MinValue;
        x0 = y0 = float.MaxValue;
        foreach (var op in layer.Ops) Bounds(op, ref x0, ref y0, ref bx1, ref by1);
        if (!(bx1 > x0 && by1 > y0)) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(layer.RasterDensity.ToString("R", inv)).Append('|');
        foreach (var p in layer.Raster!.Primitives)
            sb.Append((int)p.Kind).Append(',').Append(p.Sx.ToString("R", inv)).Append(',').Append(p.Dx.ToString("R", inv)).Append(',')
                .Append(p.Dy.ToString("R", inv)).Append(',').Append(p.Color).Append(';');
        string Rel(float v, float origin) => MathF.Round(v - origin, 2).ToString("R", inv);
        foreach (var op in layer.Ops)
        {
            switch (op)
            {
                case TextOp t:
                    _shadowFonts ??= new Dictionary<TextFont, int>(ReferenceEqualityComparer.Instance);
                    if (!_shadowFonts.TryGetValue(t.Font, out int font)) _shadowFonts[t.Font] = font = _shadowFonts.Count;
                    sb.Append("|T").Append(font).Append(',').Append(t.Size.ToString("R", inv)).Append(',').Append(Rel(t.X, x0)).Append(',')
                        .Append(Rel(t.Baseline, y0)).Append(',').Append(t.WordSpacing.ToString("R", inv)).Append(',')
                        .Append(t.CharSpacing.ToString("R", inv)).Append(',').Append(t.Rtl ? 'r' : 'l').Append(',').Append(t.Text.Length).Append(':').Append(t.Text);
                    break;
                case RectOp r:
                    sb.Append("|R").Append(Rel(r.X, x0)).Append(',').Append(Rel(r.Y, y0)).Append(',').Append(Rel(r.W, 0)).Append(',').Append(Rel(r.H, 0));
                    break;
                case LineOp l:
                    sb.Append("|L").Append(Rel(l.X1, x0)).Append(',').Append(Rel(l.Y1, y0)).Append(',').Append(Rel(l.X2, x0)).Append(',')
                        .Append(Rel(l.Y2, y0)).Append(',').Append(Rel(l.Width, 0)).Append(l.Dotted ? 'd' : l.Solid ? 's' : 'a')
                        .Append(Rel(l.DashLength, 0)).Append(',').Append(Rel(l.DashGap, 0));
                    break;
                default:
                    return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>Grows the rectangle to take in what an op paints, in flow coordinates (generously for text).</summary>
    private static void Bounds(DrawOp op, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        void Add(float x, float y, float w, float h, ref float ax0, ref float ay0, ref float ax1, ref float ay1)
        {
            if (!(w > 0 && h > 0)) return;
            ax0 = Math.Min(ax0, x); ay0 = Math.Min(ay0, y);
            ax1 = Math.Max(ax1, x + w); ay1 = Math.Max(ay1, y + h);
        }
        switch (op)
        {
            case RectOp r: Add(r.X, r.Y, r.W, r.H, ref x0, ref y0, ref x1, ref y1); break;
            case BorderRectOp br: Add(br.X, br.Y, br.W, br.H, ref x0, ref y0, ref x1, ref y1); break;
            case RoundRectOp rr:
            {
                float grow = rr.StrokeWidth / 2;
                Add(rr.X - grow, rr.Y - grow, rr.W + 2 * grow, rr.H + 2 * grow, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case BackgroundOp bg:
            {
                var (bx0, by0, bx1, by1) = bg.Painted();
                Add(bx0, by0, bx1 - bx0, by1 - by0, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case LineOp l:
                Add(Math.Min(l.X1, l.X2) - l.Width, Math.Min(l.Y1, l.Y2) - l.Width, Math.Abs(l.X2 - l.X1) + 2 * l.Width,
                    Math.Abs(l.Y2 - l.Y1) + 2 * l.Width, ref x0, ref y0, ref x1, ref y1);
                break;
            case TextOp t:
            {
                // Glyphs can reach past their advance widths and above the em box: pad by part of the font size.
                float width = t.Font.Measure(t.Text, t.Size) + Math.Abs(t.CharSpacing) * t.Text.Length + Math.Abs(t.WordSpacing) * 8;
                Add(t.X - t.Size * 0.5f, t.Baseline - t.Size * 1.25f, width + t.Size, t.Size * 1.75f, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case ImageOp im: Add(im.X, im.Y, im.W, im.H, ref x0, ref y0, ref x1, ref y1); break;
            case SvgOp sv: Add(sv.X, sv.Y, sv.W, sv.H, ref x0, ref y0, ref x1, ref y1); break;
            case CircleOp c: Add(c.Cx - c.R - 1, c.Cy - c.R - 1, 2 * c.R + 2, 2 * c.R + 2, ref x0, ref y0, ref x1, ref y1); break;
            case ClipOp clip:
            {
                float cx0 = float.MaxValue, cy0 = float.MaxValue, cx1 = float.MinValue, cy1 = float.MinValue;
                foreach (var o in clip.Ops) Bounds(o, ref cx0, ref cy0, ref cx1, ref cy1);
                cx0 = Math.Max(cx0, clip.X); cy0 = Math.Max(cy0, clip.Y);
                cx1 = Math.Min(cx1, clip.X + clip.W); cy1 = Math.Min(cy1, clip.Y + clip.H);
                Add(cx0, cy0, cx1 - cx0, cy1 - cy0, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case LayerOp layer:
            {
                float lx0 = float.MaxValue, ly0 = float.MaxValue, lx1 = float.MinValue, ly1 = float.MinValue;
                foreach (var o in layer.Ops) Bounds(o, ref lx0, ref ly0, ref lx1, ref ly1);
                if (!(lx1 > lx0 && ly1 > ly0)) break;
                if (layer.Raster is { } inner) { lx0 += inner.X; ly0 += inner.Y; lx1 += inner.X + inner.W; ly1 += inner.Y + inner.H; }
                if (layer.Transform is { } m)
                {
                    // The corners about the transform origin.
                    float tx0 = float.MaxValue, ty0 = float.MaxValue, tx1 = float.MinValue, ty1 = float.MinValue;
                    foreach (var (cx, cy) in new[] { (lx0, ly0), (lx1, ly0), (lx0, ly1), (lx1, ly1) })
                    {
                        float px = cx - layer.OriginX, py = cy - layer.OriginY;
                        m.Apply(ref px, ref py);
                        px += layer.OriginX;
                        py += layer.OriginY;
                        tx0 = Math.Min(tx0, px); ty0 = Math.Min(ty0, py); tx1 = Math.Max(tx1, px); ty1 = Math.Max(ty1, py);
                    }
                    (lx0, ly0, lx1, ly1) = (tx0, ty0, tx1, ty1);
                }
                Add(lx0, ly0, lx1 - lx0, ly1 - ly0, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case GroupOp g:
                foreach (var o in g.Ops) Bounds(o, ref x0, ref y0, ref x1, ref y1);
                break;
        }
    }
}

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
        var (w, h) = FilterPixels(pw, ph);
        if (!WithinRenderBudget(f, layer.Ops, w, h, w * (double)h / (rw * rh))) return (-1, 0, 0, 0, 0, 0, 0); // drawn without the filter
        // A page of w × h points shows the flow rectangle (rx, ry, rw, rh) scaled to fill it, one point per pixel: the
        // margins move the flow's origin off the page (and keep all of it on the first page), and a layer scales it.
        var page = new PageSetup { Width = w, Height = h, MarginLeft = -rx, MarginTop = -ry, MarginBottom = -1e6f };
        var scaled = new LayerOp { Transform = Matrix.Scale(w / rw, h / rh), OriginX = rx, OriginY = ry };
        scaled.Ops.AddRange(layer.Ops);
        byte[] pdf;
        try
        {
            var writer = new PdfDocumentWriter(page, compress: false, CompressionLevel.NoCompression, _context);
            pdf = writer.Write(new List<DrawOp> { scaled }, ry + rh, new Dictionary<string, float>(), 0, null, null, null, null, null);
        }
        catch (PdfLimitException) { return default; }
        if (f.TextShadows)
        {
            // Only the shape of the text matters to its shadows.
            var rendered = PdfRenderer.Open(pdf).RenderPageExact(0, w, h, new ImageOptions { Transparent = true }, Cancellation);
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

    // A shape is rasterized and composited over every pixel of its bounds, and a clip is made into a mask over every
    // pixel of its rectangle: measured at about twice the work of a pixel of a filter primitive.
    private const long FillWorkPerPixel = 2;

    // A gradient is evaluated again at every pixel it paints: measured at about four times the work of a pixel of a
    // filter primitive.
    private const long GradientWorkPerPixel = 4;

    /// <summary>
    /// Whether a filter may run on w × h pixels of ops rendered through a page of their own, counting its work against the
    /// document's budget when it may. Besides the filter's own work, the renderer paints every transparency group and
    /// soft mask in the ops on a canvas of its own the size of the whole image, so hundreds of masked boxes nested in
    /// each other cost hundreds of full images, and every background gradient may cover the whole image too. Every shape
    /// and clip is filled over its own area, at <paramref name="pixelsPerArea"/> pixels per square point;
    /// <paramref name="imageWork"/> is the work of the images drawn in the ops. A filter whose canvases, gradients,
    /// fills and images alone are beyond the document's budget never runs.
    /// </summary>
    private bool WithinRenderBudget(SvgFilter f, List<DrawOp> ops, int w, int h, double pixelsPerArea, long imageWork = 0)
    {
        long canvasWork = RenderWorkPerPixel(ops) * w * h + FillPixels(ops, pixelsPerArea, (long)w * h) * FillWorkPerPixel + imageWork;
        return canvasWork <= SvgFilter.MaxDocumentWork && f.WithinBudget(w, h) && FilterBudget.TrySpend(f.DocumentWork(w, h) + canvasWork);
    }

    /// <summary>
    /// The work for each pixel of the image of what the renderer paints over all of it for the ops: an offscreen canvas
    /// for every transparency group and soft mask, and the shading of every background gradient. A gradient with
    /// translucent stops is painted twice, its colours through a soft mask that holds its alphas, so hundreds of them
    /// in one blurred box took seconds while the budget counted none of it.
    /// </summary>
    private static long RenderWorkPerPixel(List<DrawOp> ops)
    {
        long n = 0;
        foreach (var op in ops)
        {
            if (op is LayerOp l) n += ((l.NeedsGroup ? 1 : 0) + (l.Mask?.Length ?? 0) + (l.MaskFill != null ? 1 : 0)) * CanvasWorkPerPixel;
            if (op is BackgroundOp { Gradient: { } g }) n += g.Alphas != null ? CanvasWorkPerPixel + 2 * GradientWorkPerPixel : GradientWorkPerPixel;
            if (op is GroupOp group) n += RenderWorkPerPixel(group.Ops);
        }
        return n;
    }

    /// <summary>
    /// The pixels the renderer fills for the ops, each shape over its bounds and each clip over its rectangle, at most the
    /// whole image (<paramref name="imagePixels"/>) each. Hundreds of inset box shadows in one blurred box are thousands of
    /// rings, each as large as the box, which took seconds while the budget counted only the image once.
    /// </summary>
    private static long FillPixels(List<DrawOp> ops, double pixelsPerArea, long imagePixels)
    {
        long n = 0;
        foreach (var op in ops)
        {
            double area;
            if (op is GroupOp group)
            {
                n += FillPixels(group.Ops, pixelsPerArea, imagePixels);
                if (op is not ClipOp clip) continue;
                area = (double)Math.Max(0, clip.W) * Math.Max(0, clip.H);
            }
            else if (op is BackgroundOp { Gradient: not null }) continue; // counted over the whole image already
            else
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                Bounds(op, ref x0, ref y0, ref x1, ref y1);
                if (!(x1 > x0 && y1 > y0)) continue;
                area = (double)(x1 - x0) * (y1 - y0);
            }
            n += (long)Math.Min(area * pixelsPerArea, imagePixels);
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
                        .Append(Rel(l.DashLength, 0)).Append(',').Append(Rel(l.DashGap, 0)).Append(',').Append(Rel(l.DashPhase, 0));
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
            case PathOp path:
            {
                float grow = path.StrokeWidth / 2;
                Add(path.X0 - grow, path.Y0 - grow, path.X1 - path.X0 + 2 * grow, path.Y1 - path.Y0 + 2 * grow, ref x0, ref y0, ref x1, ref y1);
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
                Add(t.X - t.Size * 0.5f, t.Baseline - t.Size * (t.UpsideDown ? 0.5f : 1.25f), width + t.Size, t.Size * 1.75f, ref x0, ref y0, ref x1, ref y1);
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
                if (layer.Transform is { } m) Transformed(layer, m, ref lx0, ref ly0, ref lx1, ref ly1);
                Add(lx0, ly0, lx1 - lx0, ly1 - ly0, ref x0, ref y0, ref x1, ref y1);
                break;
            }
            case GroupOp g:
                foreach (var o in g.Ops) Bounds(o, ref x0, ref y0, ref x1, ref y1);
                break;
        }
    }

    /// <summary>The box round the four corners of a box that the layer's transform turns about its origin.</summary>
    private static void Transformed(LayerOp layer, Matrix m, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        float tx0 = float.MaxValue, ty0 = float.MaxValue, tx1 = float.MinValue, ty1 = float.MinValue;
        foreach (var (cx, cy) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
        {
            float px = cx - layer.OriginX, py = cy - layer.OriginY;
            m.Apply(ref px, ref py);
            px += layer.OriginX;
            py += layer.OriginY;
            tx0 = Math.Min(tx0, px); ty0 = Math.Min(ty0, py); tx1 = Math.Max(tx1, px); ty1 = Math.Max(ty1, py);
        }
        (x0, y0, x1, y1) = (tx0, ty0, tx1, ty1);
    }

    // The font runs and glyphs of a text op whose ink PaintBounds measures.
    private readonly List<FontRun> _inkRuns = new();
    private readonly List<ShapedGlyph> _inkGlyphs = new();

    /// <summary>
    /// Grows the rectangle, in flow coordinates, to take in everything the placed items can paint: the bounding box of
    /// the transparency group or soft mask that draws them. A viewer gives a group a buffer of that size, so a box the
    /// size of the plane costs a page-sized buffer for every small translucent box. Returns false when it is not known:
    /// a filter or a backdrop filter paints pixels beyond what its content covers.
    /// </summary>
    private bool PaintBounds(List<Placed> items, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        foreach (var item in items)
        {
            switch (item.Op)
            {
                case LinkOp:
                    break;
                case LayerBlock lb:
                {
                    var layer = lb.Layer;
                    if (layer.Raster != null || layer.Backdrop != null || layer.Transform3D != null) return false;
                    float lx0 = float.MaxValue, ly0 = float.MaxValue, lx1 = float.MinValue, ly1 = float.MinValue;
                    if (!PaintBounds(lb.Items, ref lx0, ref ly0, ref lx1, ref ly1)) return false;
                    if (!(lx1 > lx0 && ly1 > ly0)) break;
                    if (layer.Transform is { } m) Transformed(layer, m, ref lx0, ref ly0, ref lx1, ref ly1);
                    Grow(lx0, ly0, lx1, ly1, ref x0, ref y0, ref x1, ref y1);
                    break;
                }
                case ClipBlock cb:
                {
                    float cx0 = float.MaxValue, cy0 = float.MaxValue, cx1 = float.MinValue, cy1 = float.MinValue;
                    if (!PaintBounds(cb.Items, ref cx0, ref cy0, ref cx1, ref cy1)) return false;
                    // The clip's edges are snapped to whole pixels.
                    var c = cb.Clip;
                    Grow(Math.Max(cx0, c.X - 1), Math.Max(cy0, c.Y - 1), Math.Min(cx1, c.X + c.W + 1), Math.Min(cy1, c.Y + c.H + 1),
                        ref x0, ref y0, ref x1, ref y1);
                    break;
                }
                case FieldBlock fb:
                    if (!PaintBounds(fb.Items, ref x0, ref y0, ref x1, ref y1)) return false;
                    break;
                case TextOp t:
                {
                    // The generous box of the whole run, and each glyph's own box where shaping moved it further.
                    var ink = new GlyphBoxes { Op = t, X0 = float.MaxValue, Y0 = float.MaxValue, X1 = float.MinValue, Y1 = float.MinValue };
                    Bounds(t, ref ink.X0, ref ink.Y0, ref ink.X1, ref ink.Y1);
                    InlineLayout.VisitGlyphs(t, ref ink, _inkRuns, _inkGlyphs);
                    // The outline, and the stroke that widens a synthetic bold, reach out past the glyphs.
                    float grow = t.StrokeWidth + t.Size * 0.1f;
                    Grow(ink.X0 - grow, ink.Y0 - grow, ink.X1 + grow, ink.Y1 + grow, ref x0, ref y0, ref x1, ref y1);
                    break;
                }
                case PathOp path:
                {
                    // Corners are mitred, up to the default limit of ten half widths.
                    float grow = path.StrokeWidth * 5;
                    Grow(path.X0 - grow, path.Y0 - grow, path.X1 + grow, path.Y1 + grow, ref x0, ref y0, ref x1, ref y1);
                    break;
                }
                case RectOp or BorderRectOp or RoundRectOp or BackgroundOp or LineOp or ImageOp or SvgOp or CircleOp:
                    Bounds(item.Op, ref x0, ref y0, ref x1, ref y1);
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static void Grow(float ax0, float ay0, float ax1, float ay1, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        if (!(ax1 > ax0 && ay1 > ay0)) return;
        x0 = Math.Min(x0, ax0); y0 = Math.Min(y0, ay0);
        x1 = Math.Max(x1, ax1); y1 = Math.Max(y1, ay1);
    }

    /// <summary>
    /// The bounding box of a group that draws the items, as the PDF array of its form in the stream's space: what they
    /// paint, grown by two points and out to whole points for the pixels a viewer touches at the edges. The plane when
    /// that is not known, or when they paint nothing, which keeps such a group as it was.
    /// </summary>
    private string GroupBox(List<Placed> items, float pageTop)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        if (!PaintBounds(items, ref x0, ref y0, ref x1, ref y1) || !(x1 > x0 && y1 > y0)) return PlaneBox;
        var b = new ByteBuffer(64);
        b.Ascii("[").Num(MathF.Floor(PX(x0 - 2))).Sp().Num(MathF.Floor(PY(y1 + 2, pageTop))).Sp()
            .Num(MathF.Ceiling(PX(x1 + 2))).Sp().Num(MathF.Ceiling(PY(y0 - 2, pageTop))).Ascii("]");
        return System.Text.Encoding.ASCII.GetString(b.Span);
    }

    /// <summary>The bounding box that takes in the whole plane, for content whose ink is not known.</summary>
    private const string PlaneBox = "[-32767 -32767 32767 32767]";

    /// <summary>
    /// Grows a box, in flow coordinates, round each glyph of a text op: the font's bounding box at the glyph's origin,
    /// raised as shaping raised it, and never less than the generous box <see cref="Bounds"/> gives text, in case a
    /// font's box is wrong.
    /// </summary>
    private struct GlyphBoxes : InlineLayout.IGlyphVisitor
    {
        public TextOp Op;
        public float X0, Y0, X1, Y1;

        public void Glyph(FontProgram program, int gid, string text, int start, float x, float dy, float skew, float scale)
        {
            float em = program.UnitsPerEm;
            Add(x, dy, program.XMin / em, program.YMin / em, program.XMax / em, program.YMax / em, program.Advance1000(gid) / 1000f, skew,
                Op.Size * scale);
        }

        public void Glyph(StandardFont font, char c, string text, int start, float x) => Add(x, 0, 0, 0, 0, 0, font.CharWidth(c, 1), 0, Op.Size);

        private void Add(float x, float dy, float left, float bottom, float right, float top, float advance, float skew, float size)
        {
            if (!(size > 0)) return;
            left = Math.Min(left, -0.5f);
            right = Math.Max(right, advance + 0.5f);
            bottom = Math.Min(bottom, -0.5f);
            top = Math.Max(top, 1.25f);
            // A synthetic italic slants the glyph right above the baseline and left below it.
            right += skew * top;
            left += skew * bottom;
            X0 = Math.Min(X0, x + left * size);
            X1 = Math.Max(X1, x + right * size);
            // Text drawn upside down about its baseline has its ink, and its raise, on the other side.
            if (Op.UpsideDown)
            {
                Y0 = Math.Min(Y0, Op.Baseline + dy + bottom * size);
                Y1 = Math.Max(Y1, Op.Baseline + dy + top * size);
            }
            else
            {
                Y0 = Math.Min(Y0, Op.Baseline - dy - top * size);
                Y1 = Math.Max(Y1, Op.Baseline - dy - bottom * size);
            }
        }
    }
}

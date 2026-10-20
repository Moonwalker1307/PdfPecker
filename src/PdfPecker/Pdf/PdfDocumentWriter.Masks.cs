using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>CSS masks (mask-image) and backdrop-filter on HTML boxes.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Adds the soft mask of a layer's mask-image layers on this page and returns the number of the graphics state
    /// that sets it. The mask is an alpha mask built from the layers bottom up: each layer's mask values (its alpha, or
    /// for a luminance layer its luminance through a luminosity mask) combine with the mask below by its mask-composite,
    /// with nested soft masks doing the Porter-Duff operators: intersect paints the layer through the mask below,
    /// subtract through the inverse of it, and exclude adds both the other way round.
    /// </summary>
    private int AddLayerMask(MaskLayerOps[] layers, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        // Everything the mask covers, for the full rectangles that make luminance and inverse masks.
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var layer in layers)
            foreach (var op in layer.Ops) Bounds(op, ref x0, ref y0, ref x1, ref y1);
        if (!(x1 > x0 && y1 > y0)) return AddMaskForm(Array.Empty<byte>(), alpha: true);
        var area = new ByteBuffer(64);
        area.Num(PX(x0 - 1)).Sp().Num(PY(y1 + 1, pageTop)).Sp().Num(x1 - x0 + 2).Sp().Num(y1 - y0 + 2).Ascii(" re f");
        string rect = System.Text.Encoding.ASCII.GetString(area.Span);

        byte[]? below = null;
        int belowForm = 0;
        for (int i = layers.Length - 1; i >= 0; i--)
        {
            var paint = MaskLayerContent(layers[i], rect, pageTop, imageIds);
            if (below == null)
            {
                below = paint;
                continue;
            }
            if (belowForm == 0) belowForm = AddPlainForm(below);
            var b = new ByteBuffer(paint.Length + 64);
            switch (layers[i].Composite)
            {
                case MaskComposite.Add:
                    b.Ascii("/Fx").Int(belowForm).Ascii(" Do\n").Bytes(paint);
                    break;
                // The layer is a group of its own under the mask below: a soft mask it sets itself (a gradient's alphas)
                // would otherwise replace that mask in the graphics state rather than add to it.
                case MaskComposite.Intersect:
                    b.Ascii("q /s").Int(AddMaskForm(FormDo(belowForm), alpha: true)).Ascii(" gs /Fx").Int(AddPlainForm(paint)).Ascii(" Do Q\n");
                    break;
                case MaskComposite.Subtract:
                    b.Ascii("q /s").Int(InverseMask(belowForm, rect)).Ascii(" gs /Fx").Int(AddPlainForm(paint)).Ascii(" Do Q\n");
                    break;
                default:
                {
                    // Exclude: the layer where the mask below is not, and the mask below where the layer is not.
                    int layerForm = AddPlainForm(paint);
                    b.Ascii("q /s").Int(InverseMask(belowForm, rect)).Ascii(" gs /Fx").Int(layerForm).Ascii(" Do Q\n");
                    b.Ascii("q /s").Int(InverseMask(layerForm, rect)).Ascii(" gs /Fx").Int(belowForm).Ascii(" Do Q\n");
                    break;
                }
            }
            below = b.Span.ToArray();
            belowForm = 0;
        }
        return AddMaskForm(below!, alpha: true);
    }

    /// <summary>What one mask layer paints, as a stream whose alpha is the layer's mask values.</summary>
    private byte[] MaskLayerContent(MaskLayerOps layer, string rect, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var ops = new List<Placed>();
        foreach (var op in layer.Ops) Flatten(op, ops);
        var content = new ByteBuffer(256);
        var saved = SaveState();
        var pageMc = _mc;
        var open = _mcOpen;
        _mc = null;
        _mcOpen = null;
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
        _textFont = null;
        _textSize = -1;
        _tw = _tc = float.NaN;
        _tr = -1;
        _anyAlpha = true;
        var streamSpace = StartStreamSpace();
        foreach (var item in ops)
            if (item.Op is not LinkOp) WriteOp(content, item, pageTop, imageIds);
        EndText(content);
        _streamSpace = streamSpace;
        _mc = pageMc;
        _mcOpen = open;
        RestoreState(saved);
        if (!layer.Luminance) return content.Span.ToArray();
        // Luminance: black painted through a luminosity mask of the layer has the layer's luminance as its alpha.
        int lum = AddMaskForm(content.Span.ToArray(), alpha: false);
        var b = new ByteBuffer(96);
        b.Ascii("q /s").Int(lum).Ascii(" gs 0 g ").Ascii(rect).Ascii(" Q\n");
        return b.Span.ToArray();
    }

    /// <summary>A luminosity mask that is one minus the alpha of the form numbered <paramref name="form"/>.</summary>
    private int InverseMask(int form, string rect)
    {
        int alpha = AddMaskForm(FormDo(form), alpha: true);
        var b = new ByteBuffer(160);
        b.Ascii("1 g ").Ascii(rect).Ascii("\nq /s").Int(alpha).Ascii(" gs 0 g ").Ascii(rect).Ascii(" Q\n");
        return AddMaskForm(b.Span.ToArray(), alpha: false);
    }

    private static byte[] FormDo(int form)
    {
        var b = new ByteBuffer(16);
        b.Ascii("/Fx").Int(form).Ascii(" Do\n");
        return b.Span.ToArray();
    }

    /// <summary>Adds a transparency group that only mask streams draw, and returns its /Fx number.</summary>
    private int AddPlainForm(byte[] content)
    {
        _forms.Add(content);
        _formIsolated.Add(false);
        _formStreams.Add(null);
        return _forms.Count;
    }

    /// <summary>Adds a soft mask, alpha or luminosity, whose group draws <paramref name="content"/>; returns its /s number.</summary>
    private int AddMaskForm(byte[] content, bool alpha)
    {
        string dict = "/Type /XObject /Subtype /Form /BBox [-32767 -32767 32767 32767] /Group << /S /Transparency /CS /DeviceRGB >> /Resources "
            + _resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R";
        _masks.Add((content, dict, alpha));
        return _masks.Count;
    }

    // ------------------------------------------------------------------ backdrop-filter

    // The filtered backdrops, by layer, shared with the writers that render content for this one: each is made once and
    // drawn on every page its box reaches. A null image draws nothing.
    private Dictionary<LayerOp, (PdfImage? Image, float X, float Y, float W, float H)> _backdrops = new(ReferenceEqualityComparer.Instance);

    // The canvas of the page being written, which is behind everything in its flow: its background colour, the canvas
    // ops (in page coordinates) and the flow y at the top of the page.
    private (uint Background, List<Placed>? Canvas, float PageTop) _pageCanvas;

    /// <summary>The most boxes with a backdrop-filter a document filters; the others are drawn without it.</summary>
    internal const int MaxBackdrops = 64;

    /// <summary>
    /// Draws the filtered backdrop of a layer: what is painted behind its border box, rendered to pixels, filtered, and
    /// drawn inside the border box with its rounded corners. Behind the box are the ops painted before it, up to the
    /// nearest layer that is a group of its own (translucent, blended, clipped, masked or filtered), as in Chrome.
    /// </summary>
    private void WriteBackdrop(ByteBuffer b, LayerOp layer, float pageTop)
    {
        if (!_backdrops.TryGetValue(layer, out var r)) _backdrops[layer] = r = RasterizeBackdrop(layer);
        if (r.Image is not { } image) return;
        if (!_rasterNumbers.TryGetValue(image, out int n))
        {
            _rasters.Add(image);
            _rasterNumbers[image] = n = _rasters.Count;
        }
        EndText(b);
        if (_mc != null) { EndMarked(b); b.Ascii("/Artifact BMC\n"); }
        SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
        b.Ascii("q ");
        float top = layer.BackdropY, bottom = layer.BackdropY + layer.BackdropH;
        if (!float.IsNaN(_flowTop) && (top < _flowTop - 0.5f || bottom > _flowBottom + 0.5f))
        {
            // The part of a box that goes on to another page is drawn there.
            float pt = Math.Max(top, _flowTop), pb = Math.Min(bottom, _flowBottom);
            b.Num(PX(r.X)).Sp().Num(PY(pb, pageTop)).Sp().Num(r.W).Sp().Num(Math.Max(0, pb - pt)).Ascii(" re W n ");
        }
        // The border box on whole CSS pixels, like the box's background.
        float bx0 = SnapPx(layer.BackdropX), bx1 = SnapPx(layer.BackdropX + layer.BackdropW), by0 = SnapPx(top), by1 = SnapPx(bottom);
        if (layer.BackdropRadii is { } radii && !radii.IsZero) RoundRectPath(b, bx0, by0, bx1 - bx0, by1 - by0, radii, pageTop);
        else b.Num(PX(bx0)).Sp().Num(PY(by1, pageTop)).Sp().Num(bx1 - bx0).Sp().Num(by1 - by0).Ascii(" re ");
        b.Ascii("W n ");
        b.Num(r.W).Ascii(" 0 0 ").Num(r.H).Sp().Num(PX(r.X)).Sp().Num(PY(r.Y + r.H, pageTop)).Ascii(" cm /Xr").Int(n).Ascii(" Do Q\n");
        if (_mc != null) b.Ascii("EMC\n");
    }

    /// <summary>The filtered backdrop of a layer as an image over a flow rectangle; no image when there is none to draw.</summary>
    private (PdfImage?, float, float, float, float) RasterizeBackdrop(LayerOp layer)
    {
        var f = layer.Backdrop!;
        if (!(layer.BackdropW > 0 && layer.BackdropH > 0) || !_filterBudget.TryBackdrop()) return default;
        var behind = BackdropOps(layer);
        if (behind == null) return default;
        // The border box grown by how far a blur reaches, so that its edges blur with what is around them.
        float rx = layer.BackdropX + f.X, ry = layer.BackdropY + f.Y, rw = layer.BackdropW + f.W, rh = layer.BackdropH + f.H;
        if (!(rw > 0 && rh > 0) || rw > 1e5f || rh > 1e6f) return default;
        double pw = Math.Ceiling(rw * FilterPixelsPerPoint), ph = Math.Ceiling(rh * FilterPixelsPerPoint);
        if (pw * ph > MaxFilterPixels)
        {
            double k = Math.Sqrt(MaxFilterPixels / (pw * ph));
            pw = Math.Max(1, Math.Floor(pw * k));
            ph = Math.Max(1, Math.Floor(ph * k));
        }
        int w = (int)pw, h = (int)ph;
        if (!f.WithinBudget(w, h) || !_filterBudget.TrySpend(f.DocumentWork(w, h))) return default;
        var pdf = RenderFlow(behind, rx, ry, rw, rh);
        if (pdf == null) return default;
        var source = RenderPremultiplied(pdf, w, h);
        if (source == null) return default;
        var image = FilterImage(f.Apply(source, w, h, w / rw, h / rh, Cancellation), w, h);
        return (image, rx, ry, rw, rh);
    }

    /// <summary>
    /// A one-page PDF of the flow rectangle (rx, ry, rw, rh) of the ops, page size in points, for rendering at w × h
    /// pixels; null when the ops reach a limit. The renderer scales the page, not a transform: shading patterns keep to
    /// the page's default space, so a scaled layer would leave its gradients at their own size.
    /// </summary>
    private byte[]? RenderFlow(List<DrawOp> ops, float rx, float ry, float rw, float rh)
    {
        // The margins move the flow's origin off the page, and keep all of it on the first page.
        var page = new PageSetup { Width = rw, Height = rh, MarginLeft = -rx, MarginTop = -ry, MarginBottom = -1e6f };
        try
        {
            var writer = new PdfDocumentWriter(page, compress: false, CompressionLevel.NoCompression)
            {
                Cancellation = Cancellation, _filterBudget = _filterBudget, _gradientBudget = _gradientBudget, _backdrops = _backdrops,
            };
            return writer.Write(ops, ry + rh, new Dictionary<string, float>(), 0, null, null, null, null, null);
        }
        catch (PdfLimitException) { return null; }
    }

    /// <summary>
    /// What is painted behind a layer, in the layer's own space (inside its transform and its ancestors'): the ops
    /// before it in painting order, from the nearest ancestor layer that is a backdrop root, or from the page's canvas.
    /// Transforms of the layers in between are undone around what lies outside them. Null when the layer is not in
    /// this writer's flow, or a transform cannot be undone.
    /// </summary>
    private List<DrawOp>? BackdropOps(LayerOp target)
    {
        List<(List<DrawOp> List, int Index)>? path = null;
        foreach (var section in _sections)
            if ((path = PathTo(section.Ops, target)) != null) break;
        if (path == null) return null;
        // Each level k > 0 is the ops of the op at level k - 1's index.
        DrawOp Container(int k) => path[k - 1].List[path[k - 1].Index];
        int start = 0;
        for (int k = 1; k < path.Count; k++)
            if (Container(k) is LayerOp l && IsBackdropRoot(l)) start = k;
        var acc = new List<DrawOp>();
        if (start == 0) AddCanvas(acc);
        acc.AddRange(path[start].List.GetRange(0, path[start].Index));
        for (int k = start + 1; k < path.Count; k++)
        {
            var before = path[k].List.GetRange(0, path[k].Index);
            switch (Container(k))
            {
                case LayerOp { Transform: { } m } transformed:
                    if (Undo(acc, m, transformed.OriginX, transformed.OriginY) is not { } undone) return null;
                    acc = undone;
                    acc.AddRange(before);
                    break;
                case ClipOp clip:
                {
                    var copy = (ClipOp)clip.Copy();
                    copy.Ops.AddRange(before);
                    acc.Add(copy);
                    break;
                }
                default:
                    acc.AddRange(before);
                    break;
            }
        }
        return target.Transform is { } own ? Undo(acc, own, target.OriginX, target.OriginY) : acc;
    }

    /// <summary>Ops wrapped in the inverse of a transform about an origin; null when it has no inverse.</summary>
    private static List<DrawOp>? Undo(List<DrawOp> ops, Matrix m, float originX, float originY)
    {
        if (!m.TryInvert(out var inverse)) return null;
        var layer = new LayerOp { Transform = inverse, OriginX = originX, OriginY = originY };
        layer.Ops.AddRange(ops);
        return new List<DrawOp> { layer };
    }

    /// <summary>Whether a layer is painted as a group of its own, which a backdrop-filter inside it cannot see out of.</summary>
    private static bool IsBackdropRoot(LayerOp l)
        => l.Opacity < 1 || l.BlendMode != null || l.ClipVerbs != null || l.Raster != null || l.Mask != null || l.Backdrop != null
           || l.MaskFill != null || l.TextClip;

    /// <summary>The page's background colour and canvas, moved from page coordinates into the flow.</summary>
    private void AddCanvas(List<DrawOp> acc)
    {
        var (background, canvas, pageTop) = _pageCanvas;
        // The paper is white under everything, and filters see it: invert() turns an empty page black, as in Chrome.
        acc.Add(new RectOp(-1e5f, pageTop - 1e5f, 2e5f, 2e5f, CssColor.IsVisible(background) ? CssColor.FlattenOnWhite(background) : 0xFFFFFFFFu));
        if (canvas is not { Count: > 0 }) return;
        // Canvas ops have y from the top of the content area; the flow starts below the running header band.
        var moved = new LayerOp { Transform = Matrix.Translate(0, pageTop - _page.BandTop) };
        foreach (var placed in canvas)
            moved.Ops.Add(placed.Op switch
            {
                LayerBlock lb => lb.Layer,
                ClipBlock cb => cb.Clip,
                FieldBlock fb => fb.Field,
                var op => op,
            });
        acc.Add(moved);
    }

    /// <summary>The path to an op in a tree of groups: at each level, a list and the index in it; null when it is not there.</summary>
    private static List<(List<DrawOp> List, int Index)>? PathTo(List<DrawOp> root, DrawOp target)
    {
        var path = new List<(List<DrawOp> List, int Index)> { (root, 0) };
        while (path.Count > 0)
        {
            var (list, i) = path[^1];
            if (i >= list.Count)
            {
                path.RemoveAt(path.Count - 1);
                if (path.Count > 0) path[^1] = (path[^1].List, path[^1].Index + 1);
                continue;
            }
            var op = list[i];
            if (ReferenceEquals(op, target)) return path;
            if (op is GroupOp { Ops.Count: > 0 } group) path.Add((group.Ops, 0));
            else path[^1] = (list, i + 1);
        }
        return null;
    }
}

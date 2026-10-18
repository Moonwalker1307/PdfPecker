using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>Page content streams: graphics state, draw operations, rounded boxes, backgrounds and gradients.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>After Q: the colours and text state set inside are gone, so the next op writes its own.</summary>
    private void InvalidateState()
    {
        _fill = 1;
        _stroke = 1;
        _textFont = null;
        _textSize = -1;
        _tw = _tc = float.NaN;
        _tr = -1;
        _lineWidth = -1;
        if (_anyAlpha) _fillAlpha = _strokeAlpha = -1;
    }

    private void ResetState()
    {
        _fill = CssColor.Black;
        _inText = false;
        _textFont = null;
        _textSize = 0;
        _tw = 0;
        _tc = 0;
        _tr = 0;
        _stroke = CssColor.Black;
        _lineWidth = 1;
        _fillAlpha = _strokeAlpha = 255;
    }

    private float PX(float x) => _page.MarginLeft + x;

    private static float SnapPx(float pt) => MathF.Round(pt / 0.75f, MidpointRounding.AwayFromZero) * 0.75f;
    private float PY(float y, float pageTop) => _page.Height - _yOrigin - (y - pageTop);

    private void SetFill(ByteBuffer b, uint color)
    {
        SetFillAlpha(b, color);
        if (color == _fill) return;
        _fill = color;
        WriteColor(b, color);
        b.Ascii(" rg\n");
    }

    private void SetStroke(ByteBuffer b, uint color)
    {
        SetStrokeAlpha(b, color);
        if (color == _stroke) return;
        _stroke = color;
        WriteColor(b, color);
        b.Ascii(" RG\n");
    }

    /// <summary>Sets the fill alpha to the alpha of <paramref name="color"/> (0xAARRGGBB) through an ExtGState.</summary>
    private void SetFillAlpha(ByteBuffer b, uint color)
    {
        int a = (int)(color >> 24);
        if (a == _fillAlpha || _noAlpha) return;
        _fillAlpha = a;
        _anyAlpha = true;
        _fillAlphas.Add(a);
        b.Ascii("/a").Int(a).Ascii(" gs\n");
    }

    private void SetStrokeAlpha(ByteBuffer b, uint color)
    {
        int a = (int)(color >> 24);
        if (a == _strokeAlpha || _noAlpha) return;
        _strokeAlpha = a;
        _anyAlpha = true;
        _strokeAlphas.Add(a);
        b.Ascii("/A").Int(a).Ascii(" gs\n");
    }

    /// <summary>The tracked graphics state, saved at q and put back at the matching Q.</summary>
    private (uint Fill, uint Stroke, float LineWidth, int FillAlpha, int StrokeAlpha) SaveState()
        => (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha);

    private void RestoreState((uint Fill, uint Stroke, float LineWidth, int FillAlpha, int StrokeAlpha) s)
        => (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = s;

    private static void WriteColor(ByteBuffer b, uint color)
    {
        b.Num(((color >> 16) & 0xFF) / 255f).Sp().Num(((color >> 8) & 0xFF) / 255f).Sp().Num((color & 0xFF) / 255f);
    }

    private void EndText(ByteBuffer b)
    {
        if (!_inText) return;
        b.Ascii("ET\n");
        _inText = false;
    }

    private void WriteOp(ByteBuffer b, in Placed placed, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        // Over the pixels of a blurred box only its text is written, and none of the shadows of text in it.
        if (_invisibleText && placed.Op is not (TextOp or ClipBlock or LayerBlock { Layer.TextShadow: false })) return;
        if (_mc != null)
        {
            // Marked content nests inside the q/Q of clips and layers, which open their own sequences.
            if (placed.Op is LayerBlock or ClipBlock) EndMarked(b);
            else Mark(b, placed.Op.Tag);
        }
        switch (placed.Op)
        {
            case TextOp t:
            {
                if (!_inText)
                {
                    b.Ascii("BT\n");
                    _inText = true;
                    _textFont = null;
                }
                SetFill(b, t.Color);
                if (_tw != t.WordSpacing) { _tw = t.WordSpacing; b.Num(t.WordSpacing).Ascii(" Tw\n"); }
                if (_tc != t.CharSpacing) { _tc = t.CharSpacing; b.Num(t.CharSpacing).Ascii(" Tc\n"); }
                // Chrome puts baselines on whole CSS pixels (horizontal positions stay fractional).
                float y = PY(pageTop + SnapPx(t.Baseline - pageTop), pageTop);
                WriteTextLine(b, t, PX(t.X), y);
                break;
            }
            case RectOp r:
            {
                EndText(b);
                SetFill(b, r.Color);
                // Chrome paints boxes on whole CSS pixels: each edge rounds to the nearest pixel of the page.
                // Text decorations only snap vertically.
                float x0 = r.IsDecoration ? r.X : SnapPx(r.X), x1 = r.IsDecoration ? r.X + r.W : SnapPx(r.X + r.W);
                float top = pageTop + SnapPx(placed.Y0 - pageTop), bottom = pageTop + SnapPx(placed.Y1 - pageTop);
                if (x1 <= x0) { x0 = r.X; x1 = r.X + r.W; }
                if (bottom <= top) { top = placed.Y0; bottom = placed.Y1; }
                b.Num(PX(x0)).Sp().Num(PY(bottom, pageTop)).Sp().Num(x1 - x0).Sp().Num(bottom - top).Ascii(" re f\n");
                break;
            }
            case BorderRectOp br:
            {
                EndText(b);
                // Snapped like the edges of a filled box, then stroked along the middle of the border.
                float x0 = SnapPx(br.X), x1 = SnapPx(br.X + br.W);
                float top = pageTop + SnapPx(placed.Y0 - pageTop), bottom = pageTop + SnapPx(placed.Y1 - pageTop);
                float lw = br.Width, half = lw / 2;
                if (x1 - x0 <= lw || bottom - top <= lw)
                {
                    SetFill(b, br.Color);
                    b.Num(PX(x0)).Sp().Num(PY(bottom, pageTop)).Sp().Num(x1 - x0).Sp().Num(bottom - top).Ascii(" re f\n");
                    break;
                }
                SetStroke(b, br.Color);
                if (_lineWidth != lw) { _lineWidth = lw; b.Num(lw).Ascii(" w\n"); }
                b.Num(PX(x0 + half)).Sp().Num(PY(bottom - half, pageTop)).Sp().Num(x1 - x0 - lw).Sp().Num(bottom - top - lw).Ascii(" re S\n");
                break;
            }
            case BackgroundOp bg:
                EndText(b);
                if (bg.BlendMode is { } mode)
                {
                    // background-blend-mode: the layer blends with the layers and colour already painted in its group.
                    var saved = SaveState();
                    _layerBlends.Add(mode);
                    b.Ascii("q /b").Ascii(mode).Ascii(" gs\n");
                    WriteBackground(b, bg, placed, pageTop, imageIds);
                    b.Ascii("Q\n");
                    RestoreState(saved);
                }
                else WriteBackground(b, bg, placed, pageTop, imageIds);
                break;
            case RoundRectOp rr:
                EndText(b);
                WriteRoundRect(b, rr, placed, pageTop);
                break;
            case LayerBlock { Layer.MaskFill: { } maskFill } mb:
                EndText(b);
                WriteMaskedFill(b, mb, maskFill, pageTop, imageIds);
                break;
            case LayerBlock tb:
                WriteLayer(b, tb, pageTop, imageIds);
                break;
            case FieldBlock fb:
                // A widget is written on its own; a field that cannot be one is drawn, checked or not.
                if (_fieldIds.ContainsKey(fb) || fb.Field.Kind is ControlKind.CheckBox or ControlKind.Radio && !fb.Field.Checked) break;
                foreach (var item in fb.Items)
                    if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                break;
            case ClipBlock cb:
            {
                EndText(b);
                var c = cb.Clip;
                // Edges on whole CSS pixels, like the box's background and borders.
                float x0 = c.X, x1 = c.X + c.W, y0 = c.Y, y1 = c.Y + c.H;
                if (c.W < ClipOp.Far) { x0 = SnapPx(x0); x1 = SnapPx(x1); }
                if (c.H < ClipOp.Far) { y0 = SnapPx(y0); y1 = SnapPx(y1); }
                b.Ascii("q ");
                if (_mc != null) b.Ascii("/Artifact BMC ");
                if (c.Radii is { } r && !r.IsZero) RoundRectPath(b, x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0), r, pageTop);
                else b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(Math.Max(0, x1 - x0)).Sp().Num(Math.Max(0, y1 - y0)).Ascii(" re ");
                b.Ascii("W n\n");
                if (_mc != null) b.Ascii("EMC\n");
                foreach (var item in cb.Items)
                    if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                EndText(b);
                EndMarked(b);
                b.Ascii("Q\n");
                InvalidateState();
                break;
            }
            case LineOp l:
            {
                EndText(b);
                var saved = SaveState();
                b.Ascii("q ");
                SetStroke(b, l.Color);
                b.Num(l.Width).Ascii(" w ");
                if (l.Solid) b.Ascii("1 J ");
                else if (l.Dotted) b.Ascii("1 J [0 ").Num(l.Width * 2).Ascii("] 0 d ");
                else b.Ascii("[").Num(l.Width * 3).Sp().Num(l.Width * 3).Ascii("] 0 d ");
                float y1 = l.X1 == l.X2 ? placed.Y0 : l.Y1, y2 = l.X1 == l.X2 ? placed.Y1 : l.Y2;
                b.Num(PX(l.X1)).Sp().Num(PY(y1, pageTop)).Ascii(" m ").Num(PX(l.X2)).Sp().Num(PY(y2, pageTop)).Ascii(" l S Q\n");
                RestoreState(saved);
                break;
            }
            case ImageOp im:
            {
                EndText(b);
                SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
                b.Ascii("q ");
                if (im.Radii is { } ir) { RoundRectPath(b, im.X, im.Y, im.W, im.H, ir, pageTop); b.Ascii("W n "); }
                b.Num(im.W).Ascii(" 0 0 ").Num(im.H).Sp().Num(PX(im.X)).Sp().Num(PY(im.Y + im.H, pageTop))
                    .Ascii(" cm /Im").Int(imageIds[im.Image]).Ascii(" Do Q\n");
                break;
            }
            case SvgOp sv:
                EndText(b);
                WriteSvg(b, sv.Image, PX(sv.X), PY(sv.Y, pageTop), sv.W, sv.H, sv.StretchUserSpace);
                break;
            case CircleOp c:
            {
                EndText(b);
                float cx = PX(c.Cx), cy = PY(c.Cy, pageTop), r = c.R, k = r * 0.5523f;
                var saved = SaveState();
                if (c.Filled) SetFill(b, c.Color);
                else
                {
                    b.Ascii("q ");
                    SetStroke(b, c.Color);
                    b.Ascii("0.75 w "); // 1px, as Chrome strokes a circle marker
                }
                b.Num(cx + r).Sp().Num(cy).Ascii(" m ");
                b.Num(cx + r).Sp().Num(cy + k).Sp().Num(cx + k).Sp().Num(cy + r).Sp().Num(cx).Sp().Num(cy + r).Ascii(" c ");
                b.Num(cx - k).Sp().Num(cy + r).Sp().Num(cx - r).Sp().Num(cy + k).Sp().Num(cx - r).Sp().Num(cy).Ascii(" c ");
                b.Num(cx - r).Sp().Num(cy - k).Sp().Num(cx - k).Sp().Num(cy - r).Sp().Num(cx).Sp().Num(cy - r).Ascii(" c ");
                b.Num(cx + k).Sp().Num(cy - r).Sp().Num(cx + r).Sp().Num(cy - k).Sp().Num(cx + r).Sp().Num(cy).Ascii(" c ");
                b.Ascii(c.Filled ? "f\n" : "S Q\n");
                if (!c.Filled) RestoreState(saved);
                break;
            }
        }
    }

    /// <summary>
    /// Draws a layer's part on this page inside q/Q: with the layer's transform and clip-path, and, when it is
    /// translucent or blended, as a transparency group.
    /// </summary>
    private void WriteLayer(ByteBuffer b, LayerBlock tb, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        EndText(b);
        b.Ascii("q ");
        if (tb.Layer.Transform is { } m)
        {
            // The layer's y-down transform about its origin, mapped into PDF space (y up).
            float px = PX(tb.Layer.OriginX), py = PY(tb.Layer.OriginY, pageTop);
            float e = px - m.A * px + m.C * py + m.E, f = py + m.B * px - m.D * py - m.F;
            b.Num6(m.A).Sp().Num6(-m.B).Sp().Num6(-m.C).Sp().Num6(m.D).Sp().Num(e).Sp().Num(f).Ascii(" cm\n");
        }
        if (tb.Layer.ClipVerbs is { } clipVerbs)
        {
            // clip-path: the outline in the layer's own space, so that a transform turns it with the content.
            if (_mc != null) b.Ascii("/Artifact BMC ");
            WriteFlowPath(b, clipVerbs, tb.Layer.ClipPoints!, pageTop);
            b.Ascii(tb.Layer.ClipEvenOdd ? "W* n\n" : "W n\n");
            if (_mc != null) b.Ascii("EMC\n");
        }
        if (tb.Layer.NeedsGroup && !_invisibleText) WriteLayerGroup(b, tb, pageTop, imageIds);
        else
        {
            WriteLayerContent(b, tb, pageTop, imageIds);
            EndText(b);
            EndMarked(b);
        }
        b.Ascii("Q\n");
        InvalidateState();
    }

    /// <summary>
    /// Writes a translucent or blended layer's content as a transparency group, painted as a whole at its opacity and
    /// with its blend mode, so that its own overlapping parts do not show through each other.
    /// </summary>
    private void WriteLayerGroup(ByteBuffer b, LayerBlock tb, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var group = new ByteBuffer(4096);
        _fill = _stroke = 1;
        _lineWidth = -1;
        _textFont = null;
        _textSize = -1;
        _tw = _tc = float.NaN;
        _tr = -1;
        _fillAlpha = _strokeAlpha = -1; // a group starts at alpha 1, but not every viewer resets it
        _anyAlpha = true;
        // A transparency group is a stream of its own, with its own marked-content numbers.
        var pageMc = _mc;
        if (pageMc != null) _mc = new McStream(pageMc.Page);
        WriteLayerContent(group, tb, pageTop, imageIds);
        EndText(group);
        EndMarked(group);
        _forms.Add(group.Span.ToArray());
        _formIsolated.Add(tb.Layer.BlendMode != null || tb.Layer.Isolated);
        _formStreams.Add(_mc);
        _mc = pageMc;
        int alpha = (int)MathF.Round(Math.Clamp(tb.Layer.Opacity, 0, 1) * 255);
        if (tb.Layer.BlendMode is { } blend)
        {
            _blendStates.Add((alpha, blend));
            b.Ascii("/g").Int(alpha).Ascii(blend);
        }
        else
        {
            _groupAlphas.Add(alpha);
            b.Ascii("/g").Int(alpha);
        }
        b.Ascii(" gs /Fx").Int(_forms.Count).Ascii(" Do\n");
    }

    /// <summary>Writes a path given in flow coordinates (SvgShape verbs) in the page's PDF space.</summary>
    private void WriteFlowPath(ByteBuffer b, byte[] verbs, float[] pts, float pageTop)
    {
        int k = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case SvgShape.MoveTo: b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Ascii(" m "); k += 2; break;
                case SvgShape.LineTo: b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Ascii(" l "); k += 2; break;
                case SvgShape.CurveTo:
                    b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Sp().Num(PX(pts[k + 2])).Sp().Num(PY(pts[k + 3], pageTop)).Sp()
                        .Num(PX(pts[k + 4])).Sp().Num(PY(pts[k + 5], pageTop)).Ascii(" c ");
                    k += 6;
                    break;
                default: b.Ascii("h "); break;
            }
        }
        // An empty outline clips everything away.
        if (verbs.Length == 0) b.Ascii("0 0 m h ");
    }

    /// <summary>The content of a layer: its ops, or for a filter that runs on pixels, the filtered image under or in place of them.</summary>
    private void WriteLayerContent(ByteBuffer b, LayerBlock tb, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var layer = tb.Layer;
        if (layer.Raster != null && !_invisibleText)
        {
            EndText(b);
            if (_mc != null) { EndMarked(b); b.Ascii("/Artifact BMC\n"); }
            bool filtered = WriteLayerRaster(b, layer, pageTop);
            if (_mc != null) b.Ascii("EMC\n");
            // A blur beyond its work budget leaves the content as it is; shadows beyond it are left out.
            if (layer.RasterReplaces && (filtered || layer.TextShadow))
            {
                if (layer.TextShadow) return;
                // The image hides the text in it from search and selection: the same text again, invisible, brings it back.
                _invisibleText = true;
                try
                {
                    foreach (var item in tb.Items)
                        if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                    EndText(b);
                }
                finally { _invisibleText = false; }
                return;
            }
        }
        foreach (var item in tb.Items)
            if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
    }

    /// <summary>
    /// Paints a colour through a luminosity soft mask made of a layer's part on this page (<see cref="LayerOp.MaskFill"/>):
    /// the grey levels the layer's ops paint are how much of the colour covers what is below. One fill through one mask
    /// is composited once per pixel, so a blurred shadow's many bands do not add up rounding errors in a viewer.
    /// </summary>
    private void WriteMaskedFill(ByteBuffer b, LayerBlock block, uint color, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        if (_invisibleText) return;
        // What the mask covers on this page: the layer's ops, cut to their parts on it, a little grown for snapping.
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        float top = float.MaxValue, bottom = float.MinValue;
        foreach (var item in block.Items)
        {
            Bounds(item.Op, ref x0, ref y0, ref x1, ref y1);
            if (item.Y1 > item.Y0) { top = Math.Min(top, item.Y0); bottom = Math.Max(bottom, item.Y1); }
        }
        if (bottom > top) { y0 = Math.Max(y0, top); y1 = Math.Min(y1, bottom); }
        if (!(x1 > x0 && y1 > y0)) return;
        x0 -= 1; y0 -= 1; x1 += 1; y1 += 1;
        float left = PX(x0), right = PX(x1), lower = PY(y1, pageTop), upper = PY(y0, pageTop);

        // The mask is a stream of its own, with no marked content since it is not page content. It starts from the
        // graphics state of whatever it masks, so nothing about its colours is known.
        var content = new ByteBuffer(1024);
        var saved = SaveState();
        var pageMc = _mc;
        var open = _mcOpen;
        _mc = null;
        _mcOpen = null;
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
        _anyAlpha = true;
        foreach (var item in block.Items)
            if (item.Op is not LinkOp) WriteOp(content, item, pageTop, imageIds);
        EndText(content);
        _mc = pageMc;
        _mcOpen = open;
        RestoreState(saved);
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [").Num(left).Sp().Num(lower).Sp().Num(right).Sp().Num(upper)
            .Ascii("] /Group << /S /Transparency /CS /DeviceRGB >> /Resources ").Int(_resourcesId).Ascii(" 0 R");
        _masks.Add((content.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span), false));

        Mark(b, null); // the shadow is decoration
        var state = SaveState();
        b.Ascii("q /s").Int(_masks.Count).Ascii(" gs\n");
        SetFill(b, color);
        b.Num(left).Sp().Num(lower).Sp().Num(right - left).Sp().Num(upper - lower).Ascii(" re f Q\n");
        RestoreState(state);
    }

    /// <summary>Appends the outline of a rounded rectangle given in flow coordinates, as a closed path.</summary>
    private void RoundRectPath(ByteBuffer b, float x, float y, float w, float h, in CornerRadii r, float pageTop)
    {
        const float k = 0.5523f; // a quarter ellipse as one cubic Bézier
        float r0 = x + w, b0 = y + h;
        void Line(float px, float py) => b.Num(PX(px)).Sp().Num(PY(py, pageTop)).Ascii(" l ");
        // A corner from (ax, ay) to (bx, by) around the box corner (cx, cy).
        void Corner(float ax, float ay, float cx, float cy, float bx, float by)
        {
            if (ax == bx && ay == by) return;
            b.Num(PX(ax + k * (cx - ax))).Sp().Num(PY(ay + k * (cy - ay), pageTop)).Sp()
                .Num(PX(bx + k * (cx - bx))).Sp().Num(PY(by + k * (cy - by), pageTop)).Sp()
                .Num(PX(bx)).Sp().Num(PY(by, pageTop)).Ascii(" c ");
        }
        b.Num(PX(x + r.TLx)).Sp().Num(PY(y, pageTop)).Ascii(" m ");
        Line(r0 - r.TRx, y);
        Corner(r0 - r.TRx, y, r0, y, r0, y + r.TRy);
        Line(r0, b0 - r.BRy);
        Corner(r0, b0 - r.BRy, r0, b0, r0 - r.BRx, b0);
        Line(x + r.BLx, b0);
        Corner(x + r.BLx, b0, x, b0, x, b0 - r.BLy);
        Line(x, y + r.TLy);
        Corner(x, y + r.TLy, x, y, x + r.TLx, y);
        b.Ascii("h\n");
    }

    /// <summary>Draws a rounded rectangle, ring or shadow, limited to the part of it on this page.</summary>
    private void WriteRoundRect(ByteBuffer b, RoundRectOp rr, in Placed placed, float pageTop)
    {
        // Edges on whole CSS pixels, like the edges of square boxes.
        float x0 = SnapPx(rr.X), x1 = SnapPx(rr.X + rr.W), y0 = SnapPx(rr.Y), y1 = SnapPx(rr.Y + rr.H);
        if (x1 <= x0) { x0 = rr.X; x1 = rr.X + rr.W; }
        if (y1 <= y0) { y0 = rr.Y; y1 = rr.Y + rr.H; }
        var saved = SaveState();
        b.Ascii("q ");
        if (placed.Y0 > rr.Y + 0.01f || placed.Y1 < rr.Y + rr.H - 0.01f)
        {
            // Split by a page break: only this page's slice.
            b.Num(PX(x0 - 1)).Sp().Num(PY(placed.Y1, pageTop)).Sp().Num(x1 - x0 + 2).Sp().Num(placed.Y1 - placed.Y0).Ascii(" re W n\n");
        }
        if (rr.Clip is { Length: >= 6 } poly)
        {
            b.Num(PX(poly[0])).Sp().Num(PY(poly[1], pageTop)).Ascii(" m ");
            for (int i = 2; i < poly.Length; i += 2) b.Num(PX(poly[i])).Sp().Num(PY(poly[i + 1], pageTop)).Ascii(" l ");
            b.Ascii("h W n\n");
        }
        if (rr.HasHole)
        {
            // Everything around the outer shape but the hole: the outer shape is then painted without it.
            float hx0 = SnapPx(rr.HoleX), hx1 = SnapPx(rr.HoleX + rr.HoleW), hy0 = SnapPx(rr.HoleY), hy1 = SnapPx(rr.HoleY + rr.HoleH);
            if (hx1 <= hx0) { hx0 = rr.HoleX; hx1 = rr.HoleX + rr.HoleW; }
            if (hy1 <= hy0) { hy0 = rr.HoleY; hy1 = rr.HoleY + rr.HoleH; }
            float ax = Math.Min(x0, hx0) - 1, ay = Math.Min(y0, hy0) - 1, bx = Math.Max(x1, hx1) + 1, by = Math.Max(y1, hy1) + 1;
            b.Num(PX(ax)).Sp().Num(PY(by, pageTop)).Sp().Num(bx - ax).Sp().Num(by - ay).Ascii(" re\n");
            if (hx1 > hx0 && hy1 > hy0) RoundRectPath(b, hx0, hy0, hx1 - hx0, hy1 - hy0, rr.HoleR, pageTop);
            b.Ascii("W* n\n");
        }
        if (rr.StrokeWidth > 0)
        {
            // Stroked along the middle of the border.
            float half = rr.StrokeWidth / 2;
            SetStroke(b, rr.Color);
            b.Num(rr.StrokeWidth).Ascii(" w ");
            if (rr.Style == BorderStyle.Dotted) b.Ascii("1 J [0 ").Num(rr.StrokeWidth * 2).Ascii("] 0 d\n");
            else if (rr.Style == BorderStyle.Dashed) b.Ascii("[").Num(rr.StrokeWidth * 3).Sp().Num(rr.StrokeWidth * 3).Ascii("] 0 d\n");
            RoundRectPath(b, x0 + half, y0 + half, x1 - x0 - rr.StrokeWidth, y1 - y0 - rr.StrokeWidth,
                rr.R.Inset(half, half, half, half) ?? default, pageTop);
            b.Ascii("S Q\n");
        }
        else
        {
            SetFill(b, rr.Color);
            RoundRectPath(b, x0, y0, x1 - x0, y1 - y0, rr.R, pageTop);
            b.Ascii("f Q\n");
        }
        RestoreState(saved);
    }

    /// <summary>Draws one background layer: a single clipped tile, or a tiling pattern filling the painted area.</summary>
    private void WriteBackground(ByteBuffer b, BackgroundOp bg, in Placed placed, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var (x0, y0, x1, y1) = bg.Painted();
        y0 = Math.Max(y0, placed.Y0);
        y1 = Math.Min(y1, placed.Y1);
        if (!(x1 > x0 && y1 > y0)) return;
        SetFillAlpha(b, CssColor.Black); // images and patterns are painted with the fill alpha
        if (bg.ClipRadii is { } cr)
        {
            // Rounded corners: clip to the rounded painting area, then paint as usual.
            b.Ascii("q ");
            RoundRectPath(b, SnapPx(bg.ClipX), SnapPx(bg.ClipY), SnapPx(bg.ClipX + bg.ClipW) - SnapPx(bg.ClipX),
                SnapPx(bg.ClipY + bg.ClipH) - SnapPx(bg.ClipY), cr, pageTop);
            b.Ascii("W n\n");
            bg.ClipRadii = null;
            try { WriteBackground(b, bg, placed, pageTop, imageIds); }
            finally { bg.ClipRadii = cr; }
            b.Ascii("Q\n");
            return;
        }
        float tx = PX(bg.TileX), tw = bg.TileW, th = bg.TileH;
        // The image inside the tile, relative to the tile's top-left corner: all of it unless the tile shows a slice.
        var (ix, iy, iw, ih) = bg.Inner ?? (0, 0, tw, th);
        const float eps = 0.01f;
        bool single = (!bg.RepeatX || bg.TileX <= x0 + eps && bg.TileX + tw >= x1 - eps)
                      && (!bg.RepeatY || bg.TileY <= y0 + eps && bg.TileY + th >= y1 - eps);
        if (single && bg.Gradient is { } g)
        {
            // Gradient space is the tile; the pattern maps it to the page.
            var toPage = new Matrix(1, 0, 0, -1, tx, PY(bg.TileY, pageTop));
            float gx0 = x0 - bg.TileX, gy0 = y0 - bg.TileY, gx1 = x1 - bg.TileX, gy1 = y1 - bg.TileY;
            b.Ascii("q ");
            var channel = GradientChannel.Flattened;
            if (g.Alphas != null)
            {
                // Translucent stops: the unblended colours, through a soft mask holding the alphas.
                channel = GradientChannel.Straight;
                b.Ascii("/s").Int(AddSoftMask(g, toPage, gx0, gy0, gx1, gy1, PX(x0), PY(y1, pageTop), x1 - x0, y1 - y0)).Ascii(" gs ");
            }
            int name = AddPattern(g, toPage, gx0, gy0, gx1, gy1, channel);
            b.Ascii("/Pattern cs /P").Int(name).Ascii(" scn ");
            b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
            return;
        }
        if (bg.Svg is { } svg)
        {
            if (single)
            {
                b.Ascii("q ").Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re W n\n");
                WriteSlicedSvg(b, svg, tx + ix, PY(bg.TileY + iy, pageTop), iw, ih, bg.Inner != null);
                b.Ascii("Q\n");
                return;
            }
            // Repeated: a tiling pattern whose cell draws the SVG image once. The cell starts from the default
            // graphics state and uses the shared resources (fonts, alpha states, gradients).
            var svgCell = new ByteBuffer(256);
            var saved = SaveState();
            (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (CssColor.Black, CssColor.Black, 1, 255, 255);
            WriteSlicedSvg(svgCell, svg, ix, th - iy, iw, ih, bg.Inner != null);
            RestoreState(saved);
            var svgDict = new ByteBuffer(256);
            svgDict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(tw).Sp().Num(th)
                .Ascii("] /XStep ").Num(Math.Max(bg.StepX, tw)).Ascii(" /YStep ").Num(Math.Max(bg.StepY, th))
                .Ascii(" /Matrix [1 0 0 1 ").Num(tx).Sp().Num(PY(bg.TileY + th, pageTop)).Ascii("] /Resources ").Int(_resourcesId).Ascii(" 0 R");
            int svgTile = _tilings.Count + 1;
            _tilings.Add((svgTile, svgCell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(svgDict.Span)));
            b.Ascii("q /Pattern cs /T").Int(svgTile).Ascii(" scn ");
            b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
            return;
        }
        if (single)
        {
            b.Ascii("q ").Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re W n ")
                .Num(iw).Ascii(" 0 0 ").Num(ih).Sp().Num(tx + ix).Sp().Num(PY(bg.TileY + iy + ih, pageTop))
                .Ascii(" cm /Im").Int(imageIds[bg.Image!]).Ascii(" Do Q\n");
            return;
        }

        // A tiling pattern whose cell is the tile, its origin at the tile's bottom-left corner.
        var cell = new ByteBuffer(64);
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(tw).Sp().Num(th)
            .Ascii("] /XStep ").Num(Math.Max(bg.StepX, tw)).Ascii(" /YStep ").Num(Math.Max(bg.StepY, th))
            .Ascii(" /Matrix [1 0 0 1 ").Num(tx).Sp().Num(PY(bg.TileY + th, pageTop)).Ascii("] /Resources ");
        if (bg.Gradient is { Alphas: not null } ag)
        {
            // Translucent stops: each tile paints the unblended colours through a soft mask holding the alphas, both
            // placed in the cell's space (y up), and taken from the shared resources since the mask is an object of its own.
            var toCell = new Matrix(1, 0, 0, -1, 0, th);
            int mask = AddSoftMask(ag, toCell, 0, 0, tw, th, 0, 0, tw, th);
            int colors = AddPattern(ag, toCell, 0, 0, tw, th, GradientChannel.Straight);
            cell.Ascii("/s").Int(mask).Ascii(" gs /Pattern cs /P").Int(colors).Ascii(" scn 0 0 ").Num(tw).Sp().Num(th).Ascii(" re f\n");
            dict.Int(_resourcesId).Ascii(" 0 R");
        }
        else if (bg.Gradient is { } tg)
        {
            var m = tg.Transform;
            cell.Ascii("1 0 0 -1 0 ").Num(th).Ascii(" cm ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
                .Ascii(" cm /Sh1 sh\n");
            dict.Ascii("<< /Shading << /Sh1 ");
            tg.WriteShading(dict, 0, 0, tw, th);
            dict.Ascii(" >> >>");
        }
        else
        {
            // The written image: PDF/A may have replaced a CMYK image with an RGB copy.
            var image = _imageList[imageIds[bg.Image!] - 1];
            cell.Num(iw).Ascii(" 0 0 ").Num(ih).Sp().Num(ix).Sp().Num(th - iy - ih).Ascii(" cm /Im1 Do\n");
            dict.Ascii("<< /XObject << /Im1 ").Int(image.ObjectNumber).Ascii(" 0 R >> >>");
        }
        int n = _tilings.Count + 1;
        _tilings.Add((n, cell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span)));
        b.Ascii("q /Pattern cs /T").Int(n).Ascii(" scn ");
        b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
    }

    /// <summary>
    /// Draws an SVG image into (left, top, w, h). An SVG file without a viewBox keeps its scale whatever box it is drawn
    /// into, but the slices of a border image (<paramref name="sliced"/>) scale the image as a picture, so it is drawn at
    /// its own size and the whole drawing scaled to the box.
    /// </summary>
    private void WriteSlicedSvg(ByteBuffer b, SvgImage svg, float left, float top, float w, float h, bool sliced)
    {
        if (sliced && !svg.HasViewBox && svg.Width is { } nw && svg.Height is { } nh && nw > 0 && nh > 0 && (w != nw || h != nh))
        {
            b.Ascii("q ").Num6(w / nw).Ascii(" 0 0 ").Num6(h / nh).Sp().Num(left).Sp().Num(top).Ascii(" cm\n");
            WriteSvg(b, svg, 0, 0, nw, nh);
            b.Ascii("Q\n");
            InvalidateState();
            return;
        }
        WriteSvg(b, svg, left, top, w, h);
    }

    /// <summary>Adds a shading pattern to the shared resources and returns its number.</summary>
    private int AddPattern(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened)
    {
        int n = ++_patternCount;
        _patterns.Ascii(" /P").Int(n).Sp();
        g.WritePattern(_patterns, toPage, minX, minY, maxX, maxY, channel);
        return n;
    }

    /// <summary>
    /// Adds a luminosity soft mask holding a gradient's alphas over the rectangle (x, y, w, h) of user space, and returns
    /// the number of the graphics state that sets it.
    /// </summary>
    private int AddSoftMask(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY, float x, float y, float w, float h)
    {
        var content = new ByteBuffer(64);
        content.Ascii("/Pattern cs /M scn ").Num(x).Sp().Num(y).Sp().Num(w).Sp().Num(h).Ascii(" re f\n");
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [").Num(x).Sp().Num(y).Sp().Num(x + w).Sp().Num(y + h)
            .Ascii("] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Pattern << /M ");
        g.WritePattern(dict, toPage, minX, minY, maxX, maxY, GradientChannel.Alpha);
        dict.Ascii(" >> >>");
        _masks.Add((content.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span), false));
        return _masks.Count;
    }
}

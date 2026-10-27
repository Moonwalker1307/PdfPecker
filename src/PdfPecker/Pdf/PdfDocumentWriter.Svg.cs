using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>SVG drawings: shapes, paint, masks, patterns, embedded images and filters.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Draws an SVG drawing in the box whose top-left corner is (<paramref name="left"/>, <paramref name="top"/>) in PDF
    /// space: clipped to the box, with the viewBox mapped by preserveAspectRatio.
    /// </summary>
    private void WriteSvg(ByteBuffer b, SvgImage img, float left, float top, float w, float h, bool stretch = false)
    {
        if (img.Shapes.Count == 0 || w <= 0 || h <= 0) return;
        img.UserSpaceToBox(w, h, stretch, out float sx, out float sy, out float tx, out float ty, out float vx, out float vy);

        var saved = SaveState();
        b.Ascii("q ").Num(left).Sp().Num(top - h).Sp().Num(w).Sp().Num(h).Ascii(" re W n\n");
        // User space (y down) to PDF space (y up).
        var toPage = new Matrix(sx, 0, 0, -sy, left + tx - vx * sx, top - ty + vy * sy);
        b.Num(sx).Ascii(" 0 0 ").Num(-sy).Sp().Num(toPage.E).Sp().Num(toPage.F).Ascii(" cm\n");
        WriteSvgShapes(b, img.Shapes, toPage);
        b.Ascii("Q\n");
        RestoreState(saved);
    }

    /// <summary>
    /// Writes SVG shapes in the drawing's user space (already set up by a cm). <paramref name="toPage"/> maps that space
    /// to the default space of the stream, where shading and tiling patterns are placed.
    /// </summary>
    private void WriteSvgShapes(ByteBuffer b, List<SvgShape> shapes, Matrix toPage)
    {
        var states = new Stack<(uint, uint, float, int, int)>();
        foreach (var shape in shapes)
        {
            switch (shape.Kind)
            {
                case SvgShape.KindClip:
                    states.Push(SaveState());
                    b.Ascii("q\n");
                    WritePath(b, shape);
                    b.Ascii(shape.EvenOdd ? "W* n\n" : "W n\n");
                    continue;
                case SvgShape.KindMask:
                {
                    // The soft mask's coordinates are those in effect when it is set: the stream's default space, where
                    // its patterns are placed too. Then back to user space for the masked shapes.
                    int mask = AddSvgMask(shape, toPage);
                    states.Push(SaveState());
                    b.Ascii("q ");
                    if (toPage.TryInvert(out var back)) WriteMatrix(b, back).Ascii(" cm ");
                    b.Ascii("/s").Int(mask).Ascii(" gs ");
                    WriteMatrix(b, toPage).Ascii(" cm\n");
                    continue;
                }
                case SvgShape.KindEndClip:
                case SvgShape.KindEndMask:
                    b.Ascii("Q\n");
                    if (states.Count > 0) RestoreState(states.Pop());
                    continue;
                case SvgShape.KindFilter:
                    WriteSvgFilter(b, shape, toPage);
                    continue;
                case SvgShape.KindImage:
                    WriteSvgImage(b, shape, toPage);
                    continue;
                case SvgShape.KindText:
                    if (TranslucentGradient(shape)) { WriteSoftMaskedSvgShape(b, shape, toPage); continue; }
                    WriteSvgPaint(b, shape, toPage);
                    WriteSvgText(b, shape, shape.HasFill ? (shape.HasStroke ? 2 : 0) : 1);
                    EndSvgDash(b, shape);
                    continue;
            }
            if (TranslucentGradient(shape)) { WriteSoftMaskedSvgShape(b, shape, toPage); continue; }
            WritePath(b, shape);
            WriteSvgPaint(b, shape, toPage);
            if (shape.HasFill && shape.HasStroke) b.Ascii(shape.EvenOdd ? "B*\n" : "B\n");
            else if (shape.HasFill) b.Ascii(shape.EvenOdd ? "f*\n" : "f\n");
            else b.Ascii("S\n");
            EndSvgDash(b, shape);
        }
        // Unbalanced groups (none are made, but a cut-off drawing must not leave the stream's state saved).
        while (states.Count > 0)
        {
            b.Ascii("Q\n");
            RestoreState(states.Pop());
        }
    }

    /// <summary>Writes an SVG text run with the text rendering mode given (3 draws nothing but keeps the text selectable).</summary>
    private void WriteSvgText(ByteBuffer b, SvgShape shape, int mode)
    {
        if (shape.TextPath != null)
        {
            WriteSvgPathText(b, shape, mode);
            return;
        }
        var m = shape.TextMatrix;
        var svgFont = Leaf(shape.Font!);
        if (svgFont is EmbeddedFont svgEmbedded) WriteFontName(b.Ascii("BT "), Subset(svgEmbedded.Program, SvgTextColor(shape)), 0);
        else b.Ascii("BT /F").Int(((StandardFont)shape.Font!).Index + 1);
        b.Sp().Num(shape.FontSize).Ascii(" Tf ").Int(mode).Ascii(" Tr 0 Tw ")
            .Num(shape.CharSpacing).Ascii(" Tc ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
            .Ascii(" Tm ");
        if (svgFont is EmbeddedFont svgFontEmbedded)
            WriteGlyphHex(b, svgFontEmbedded, Subset(svgFontEmbedded.Program, SvgTextColor(shape)), shape.Text!, shape.FontSize, shape.Rtl, shape.CharSpacing);
        else WriteStandardText(b, shape.Font!, shape.Rtl ? Text.Bidi.VisualText(shape.Text!) : shape.Text!);
        b.Ascii(" ET\n");
    }

    /// <summary>
    /// Writes an SVG text run along a path, each glyph with its own text matrix, so the text stays selectable. As the SVG
    /// specification says, a glyph's midpoint is put on the path at its distance along it and the glyph turned with the
    /// path's direction there; a glyph whose midpoint is off either end of the path is not drawn. Shaped text keeps its
    /// shaping: the run is shaped as a whole and its glyphs placed one by one.
    /// </summary>
    private void WriteSvgPathText(ByteBuffer b, SvgShape shape, int mode)
    {
        var path = shape.TextPath!;
        var font = Leaf(shape.Font!);
        float size = shape.FontSize, pen = shape.PathStart;
        if (font is EmbeddedFont embedded) WriteFontName(b.Ascii("BT "), Subset(embedded.Program, SvgTextColor(shape)), 0);
        else b.Ascii("BT /F").Int(((StandardFont)font).Index + 1);
        b.Sp().Num(size).Ascii(" Tf ").Int(mode).Ascii(" Tr 0 Tw 0 Tc\n");
        if (font is EmbeddedFont ef)
        {
            var subset = Subset(ef.Program, SvgTextColor(shape));
            int page = 0;
            bool positioned = ef.Shape(shape.Text!, 0, shape.Text!.Length, _glyphs, shape.Rtl);
            if (shape.Rtl) _glyphs.Reverse();
            bool skipNotdef = NoNotdef;
            int prev = -1;
            for (int i = 0; i < _glyphs.Count; i++)
            {
                var g = _glyphs[i];
                float advance, dx = 0, dy = 0;
                if (positioned)
                {
                    advance = g.Advance;
                    dx = g.Dx;
                    dy = g.Dy;
                }
                else
                {
                    if (ef.HasKerning && prev >= 0) pen += (shape.Rtl ? ef.Program.Kern1000(g.Gid, prev) : ef.Program.Kern1000(prev, g.Gid)) * size / 1000f;
                    prev = g.Gid;
                    advance = ef.Program.Advance1000(g.Gid);
                }
                int cid = subset.Cid(_glyphs, i, shape.Text!);
                if (!(cid == 0 && skipNotdef) && PlaceOnPath(b, shape, pen, advance * size / 1000f, dx * size / 1000f, dy * size / 1000f))
                {
                    if (subset.Color != null && cid >> 8 != page)
                    {
                        // A colour font's glyphs are spread over Type 3 fonts of 256 glyphs each.
                        page = cid >> 8;
                        WriteFontName(b, subset, page);
                        b.Sp().Num(size).Ascii(" Tf ");
                    }
                    b.Byte((byte)'<');
                    if (subset.Color != null) b.Byte(HexDigits[(cid >> 4) & 15]).Byte(HexDigits[cid & 15]);
                    else Hex4(b, cid);
                    b.Ascii("> Tj\n");
                }
                pen += advance * size / 1000f + shape.CharSpacing;
            }
        }
        else
        {
            var text = shape.Rtl ? Text.Bidi.VisualText(shape.Text!) : shape.Text!;
            for (int i = 0; i < text.Length; i++)
            {
                if (i > 0) pen += font.KernUnits(text[i - 1], text[i]) * size / 1000f;
                float advance = font.MeasureUnits(text, i, 1) * size / 1000f;
                if (PlaceOnPath(b, shape, pen, advance, 0, 0))
                {
                    WriteTextBytes(b, text.Substring(i, 1));
                    b.Ascii(" Tj\n");
                }
                pen += advance + shape.CharSpacing;
            }
        }
        b.Ascii("ET\n");
    }

    /// <summary>
    /// Sets the text matrix of a glyph on an SVG text path: its midpoint at <paramref name="pen"/> plus half its advance
    /// along the path, turned with the path, shifted off it by the shape's shift and moved by the glyph's own offset.
    /// False, writing nothing, when the midpoint is off the path.
    /// </summary>
    private static bool PlaceOnPath(ByteBuffer b, SvgShape shape, float pen, float advance, float dx, float dy)
    {
        if (!shape.TextPath!.At(pen + advance / 2, out float x, out float y, out float cos, out float sin)) return false;
        // Along the tangent (cos, sin) and the normal (-sin, cos), which points down from text drawn left to right.
        float along = dx - advance / 2, off = shape.PathShift - dy;
        float ox = x + along * cos - off * sin, oy = y + along * sin + off * cos;
        var m = new Matrix(cos, sin, sin, -cos, ox, oy).Then(shape.TextMatrix);
        b.Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F).Ascii(" Tm ");
        return true;
    }

    /// <summary>The text runs drawn by filtered content, written as invisible text over the filter's image.</summary>
    private void WriteInvisibleSvgText(ByteBuffer b, List<SvgShape> shapes, int depth)
    {
        if (depth > 16) return;
        foreach (var shape in shapes)
        {
            if (shape.Kind == SvgShape.KindText) WriteSvgText(b, shape, 3);
            else if (shape.Kind == SvgShape.KindFilter && shape.Children != null) WriteInvisibleSvgText(b, shape.Children, depth + 1);
        }
    }

    private static ByteBuffer WriteMatrix(ByteBuffer b, Matrix m)
        => b.Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F);

    /// <summary>
    /// Adds the soft mask of an SVG mask: a transparency group drawing the mask content inside the mask region, in the
    /// stream's default space. Returns the number of the graphics state that sets it.
    /// </summary>
    private int AddSvgMask(SvgShape mask, Matrix toPage)
    {
        var content = new ByteBuffer(256);
        var saved = SaveState();
        // The group starts from the graphics state of the masked content: nothing about its colours is known.
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
        _anyAlpha = true;
        var streamSpace = StartStreamSpace();
        WriteMatrix(content, toPage).Ascii(" cm\n");
        WritePath(content, mask);
        content.Ascii("W n\n");
        WriteSvgShapes(content, mask.Children!, toPage);
        _streamSpace = streamSpace;
        RestoreState(saved);
        string dict = "/Type /XObject /Subtype /Form /BBox [-32767 -32767 32767 32767] /Group << /S /Transparency /CS /DeviceRGB >> /Resources "
            + _resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R";
        _masks.Add((content.Span.ToArray(), dict, mask.MaskAlpha));
        return _masks.Count;
    }

    /// <summary>Adds a tiling pattern repeating an SVG pattern's tile and returns its number.</summary>
    private int AddSvgTiling(SvgPattern pattern, Matrix toPage)
    {
        var cell = new ByteBuffer(256);
        var saved = SaveState();
        // A pattern cell starts from the default graphics state and uses the shared resources.
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (CssColor.Black, CssColor.Black, 1, 255, 255);
        var streamSpace = StartStreamSpace();
        WriteSvgShapes(cell, pattern.Shapes, Matrix.Identity);
        _streamSpace = streamSpace;
        RestoreState(saved);
        var m = InStream(pattern.ToRoot.Then(toPage));
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(pattern.Width).Sp().Num(pattern.Height)
            .Ascii("] /XStep ").Num(pattern.Width).Ascii(" /YStep ").Num(pattern.Height).Ascii(" /Matrix [");
        WriteMatrix(dict, m).Ascii("] /Resources ").Int(_resourcesId).Ascii(" 0 R");
        int n = _tilings.Count + 1;
        _tilings.Add((n, cell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span)));
        return n;
    }

    /// <summary>
    /// Draws an SVG image element: a raster image as an image XObject (one per image, however often it is drawn), or an
    /// embedded SVG drawing's shapes in their own space. A translucent drawing is a transparency group, so that its
    /// overlapping parts do not show through each other.
    /// </summary>
    private void WriteSvgImage(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        if (shape.Image is { } source)
        {
            if (!_rasterNumbers.TryGetValue(source, out int n))
            {
                // A copy: the same image may also be drawn by an img element, numbered with the page images.
                var image = Conformance != PdfConformance.None ? source.ToRgb() : source;
                n = image == null ? 0 : _rasters.Count + 1;
                if (image != null) _rasters.Add(image.Copy());
                _rasterNumbers[source] = n;
            }
            if (n == 0) return;
            SetFillAlpha(b, (uint)shape.ImageAlpha << 24); // an image is painted with the fill alpha
            b.Ascii("q ");
            WriteMatrix(b, shape.ImageMatrix).Ascii(" cm /Xr").Int(n).Ascii(" Do Q\n");
            return;
        }
        if (shape.Children is not { Count: > 0 } children) return;
        var inner = shape.ImageMatrix.Then(toPage);
        var saved = SaveState();
        if (shape.ImageAlpha == 255 && shape.BlendMode == null && !shape.Isolated)
        {
            b.Ascii("q ");
            WriteMatrix(b, shape.ImageMatrix).Ascii(" cm\n");
            WriteSvgShapes(b, children, inner);
            b.Ascii("Q\n");
            RestoreState(saved);
            return;
        }
        var group = new ByteBuffer(1024);
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1); // a group starts from unknown colours
        _anyAlpha = true;
        // The group's default space is the drawing's user space, where it is drawn: toPage leads back out of it.
        var streamSpace = _streamSpace;
        _streamSpace = toPage.TryInvert(out var back) ? back : null;
        WriteMatrix(group, shape.ImageMatrix).Ascii(" cm\n");
        WriteSvgShapes(group, children, inner);
        _streamSpace = streamSpace;
        RestoreState(saved);
        _forms.Add(group.Span.ToArray());
        _formIsolated.Add(shape.Isolated);
        _formStreams.Add(null);
        if (shape.BlendMode is { } blend)
        {
            _blendStates.Add((shape.ImageAlpha, blend));
            b.Ascii("q /g").Int(shape.ImageAlpha).Ascii(blend).Ascii(" gs /Fx").Int(_forms.Count).Ascii(" Do Q\n");
            return;
        }
        _groupAlphas.Add(shape.ImageAlpha);
        b.Ascii("q /g").Int(shape.ImageAlpha).Ascii(" gs /Fx").Int(_forms.Count).Ascii(" Do Q\n");
    }

    /// <summary>The resolution of SVG filter results: pixels per point (2 is 144 dpi), and the most pixels one may have.</summary>
    private const float FilterPixelsPerPoint = 2;
    private const long MaxFilterPixels = 4_000_000;

    /// <summary>
    /// The pixels of a filter result pw × ph pixels wide and high, scaled down to at most <see cref="MaxFilterPixels"/>.
    /// A region far longer than it is wide keeps one pixel across, so its length is capped on its own: scaling both
    /// sides alike left a region of 66 × 10¹¹ points one pixel wide and tens of millions of pixels long.
    /// </summary>
    private static (int W, int H) FilterPixels(double pw, double ph)
    {
        if (pw * ph > MaxFilterPixels)
        {
            double k = Math.Sqrt(MaxFilterPixels / (pw * ph));
            pw = Math.Max(1, Math.Floor(pw * k));
            ph = Math.Max(1, Math.Floor(ph * k));
            if (pw * ph > MaxFilterPixels)
            {
                if (pw <= ph) ph = Math.Floor(MaxFilterPixels / pw);
                else pw = Math.Floor(MaxFilterPixels / ph);
            }
        }
        return ((int)pw, (int)ph);
    }

    // The pixels per point of this writer's SVG filter results. A writer that renders the content of a filter has pages
    // measured in that filter's pixels already, so a filter inside it needs one pixel per point; with two, every level
    // of nested filters doubled the resolution of the one around it, up to the pixel cap at each level.
    private float _svgFilterDensity = FilterPixelsPerPoint;

    // The work the document's filters have spent, shared with the writers that render filtered content for this one.
    private SvgFilterBudget _filterBudget = new();

    // The pieces the document's tiled repeating gradients have written, shared in the same way.
    private GradientTilingBudget _gradientBudget = new();

    /// <summary>
    /// Draws filtered SVG content: the content is rendered to pixels over the filter region, the filter runs on them,
    /// and the result is drawn as an image with an alpha mask.
    /// </summary>
    private void WriteSvgFilter(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        var f = shape.Filter!;
        if (!(f.W > 0 && f.H > 0)) return;
        // Pixels per root unit, from the size of a root unit in the stream's default space.
        float kx = MathF.Sqrt(toPage.A * toPage.A + toPage.B * toPage.B) * _svgFilterDensity;
        float ky = MathF.Sqrt(toPage.C * toPage.C + toPage.D * toPage.D) * _svgFilterDensity;
        double pw = Math.Ceiling(f.W * kx), ph = Math.Ceiling(f.H * ky);
        if (!(pw >= 1 && ph >= 1)) return;
        var (w, h) = FilterPixels(pw, ph);
        if (!f.WithinBudget(w, h) || !_filterBudget.TrySpend(f.DocumentWork(w, h)))
        {
            // Too much work: the content is drawn without the filter, as a filter that can not run is left out.
            WriteSvgShapes(b, shape.Children!, toPage);
            return;
        }
        var source = RasterizeSvg(shape.Children!, f.X, f.Y, f.W, f.H, w, h);
        if (source == null) return;
        var image = FilterImage(f.Apply(source, w, h, w / f.W, h / f.H, Cancellation, shapes => RasterizeSvg(shapes, f.X, f.Y, f.W, f.H, w, h)), w, h);
        if (image == null) return;
        _rasters.Add(image);
        SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
        // The image's unit square onto the region, its first row at the top (user space has y down).
        b.Ascii("q ").Num(f.W).Ascii(" 0 0 ").Num(-f.H).Sp().Num(f.X).Sp().Num(f.Y + f.H).Ascii(" cm /Xr").Int(_rasters.Count).Ascii(" Do Q\n");
        // The image hides the text in it from search and selection: the same text again, invisible, brings it back.
        WriteInvisibleSvgText(b, shape.Children!, 0);
    }

    /// <summary>A filter's result (premultiplied sRGB floats) as an RGB image with an alpha mask; null when it is all transparent.</summary>
    private static PdfImage? FilterImage(float[] result, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool any = false;
        byte r = 0, g = 0, bl = 0;
        for (int i = 0, o = 0; i < w * h; i++, o += 3)
        {
            float a = Math.Clamp(result[i * 4 + 3], 0, 1);
            byte a8 = (byte)MathF.Round(a * 255);
            if (a8 > 0)
            {
                any = true;
                alpha[i] = a8;
                r = (byte)MathF.Round(Math.Clamp(result[i * 4] / a, 0, 1) * 255);
                g = (byte)MathF.Round(Math.Clamp(result[i * 4 + 1] / a, 0, 1) * 255);
                bl = (byte)MathF.Round(Math.Clamp(result[i * 4 + 2] / a, 0, 1) * 255);
            }
            // A transparent pixel keeps the colour before it, which costs nothing to compress where black would.
            rgb[o] = r;
            rgb[o + 1] = g;
            rgb[o + 2] = bl;
        }
        if (!any) return null;
        var colorData = PdfImage.DeflatePredicted(rgb, w, h, 3, out string colorParms);
        var alphaData = PdfImage.DeflatePredicted(alpha, w, h, 1, out string alphaParms);
        return new PdfImage
        {
            Width = w, Height = h, Data = colorData, DecodeParms = colorParms,
            SMask = new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceGray", Data = alphaData, DecodeParms = alphaParms },
        };
    }

    /// <summary>
    /// Renders SVG shapes over the root-space rectangle (x, y, w, h) into a pixels × pixels image, through a one-page
    /// PDF of their own. Returns premultiplied sRGB floats (RGBA), or null when the shapes cannot be rendered.
    /// </summary>
    private float[]? RasterizeSvg(List<SvgShape> shapes, float x, float y, float w, float h, int pixelsW, int pixelsH)
    {
        var drawing = new SvgImage { HasViewBox = true, ViewBoxX = x, ViewBoxY = y, ViewBoxWidth = w, ViewBoxHeight = h, Align = "none" };
        drawing.Shapes.AddRange(shapes);
        var page = new PageSetup { Width = pixelsW, Height = pixelsH };
        byte[] pdf;
        try
        {
            var writer = new PdfDocumentWriter(page, compress: false, CompressionLevel.NoCompression) { Cancellation = Cancellation, _svgFilterDensity = 1, _filterBudget = _filterBudget, _gradientBudget = _gradientBudget };
            pdf = writer.Write(new List<DrawOp> { new SvgOp(0, 0, pixelsW, pixelsH, drawing) }, pixelsH,
                new Dictionary<string, float>(), 0, null, null, null, null, null);
        }
        catch (PdfLimitException) { return null; }
        return RenderPremultiplied(pdf, pixelsW, pixelsH);
    }

    /// <summary>The first page of a PDF rendered at pixelsW × pixelsH on transparency, as premultiplied sRGB floats (RGBA).</summary>
    private float[]? RenderPremultiplied(byte[] pdf, int pixelsW, int pixelsH)
    {
        var rendered = PdfRenderer.Open(pdf).RenderPageExact(0, pixelsW, pixelsH, new ImageOptions { Transparent = true }, Cancellation);
        var px = rendered.Premultiplied;
        var result = new float[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i];
            if (p == 0) continue;
            result[i * 4] = (p & 255) / 255f;
            result[i * 4 + 1] = ((p >> 8) & 255) / 255f;
            result[i * 4 + 2] = ((p >> 16) & 255) / 255f;
            result[i * 4 + 3] = (p >> 24) / 255f;
        }
        return result;
    }

    private static void WritePath(ByteBuffer b, SvgShape shape)
    {
        var pts = shape.Points;
        int k = 0;
        foreach (var verb in shape.Verbs)
        {
            switch (verb)
            {
                case SvgShape.MoveTo: b.Num(pts[k]).Sp().Num(pts[k + 1]).Ascii(" m\n"); k += 2; break;
                case SvgShape.LineTo: b.Num(pts[k]).Sp().Num(pts[k + 1]).Ascii(" l\n"); k += 2; break;
                case SvgShape.CurveTo:
                    b.Num(pts[k]).Sp().Num(pts[k + 1]).Sp().Num(pts[k + 2]).Sp().Num(pts[k + 3]).Sp().Num(pts[k + 4]).Sp().Num(pts[k + 5]).Ascii(" c\n");
                    k += 6;
                    break;
                default: b.Ascii("h\n"); break;
            }
        }
    }

    /// <summary>Whether a shape is painted with a gradient that has translucent stops, which needs a soft mask.</summary>
    private static bool TranslucentGradient(SvgShape shape)
        => shape.FillGradient is { Alphas: not null } || shape.HasStroke && shape.StrokeGradient is { Alphas: not null };

    /// <summary>
    /// Paints a shape whose fill or stroke is a gradient with translucent stops: the fill and then the stroke, each in
    /// a state of its own where a luminosity soft mask holds its gradient's alphas and the pattern its unblended colours.
    /// The soft mask is set in user space, so its gradient is placed by the gradient's own transform.
    /// </summary>
    private void WriteSoftMaskedSvgShape(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        // The area the soft mask covers: the shape's bounds grown by how far a stroke (and, for text, the glyphs) can
        // reach past them, since outside it the mask hides everything.
        float grow = (shape.HasStroke ? shape.StrokeWidth * Math.Max(1, shape.MiterLimit) / 2 : 0) + 1;
        if (shape.Kind == SvgShape.KindText) grow += (shape.MaxY - shape.MinY) / 2;
        float minX = shape.MinX - grow, minY = shape.MinY - grow, maxX = shape.MaxX + grow, maxY = shape.MaxY + grow;
        if (!(maxX > minX && maxY > minY)) return;
        for (int part = 0; part < 2; part++)
        {
            bool fill = part == 0;
            if (fill ? !shape.HasFill : !shape.HasStroke) continue;
            var saved = SaveState();
            b.Ascii("q ");
            if ((fill ? shape.FillGradient : shape.StrokeGradient) is { Alphas: not null } g)
            {
                b.Ascii("/s").Int(AddSoftMask(g, Matrix.Identity, minX, minY, maxX, maxY, minX, minY, maxX - minX, maxY - minY)).Ascii(" gs\n");
                int pattern = AddPattern(g, toPage, minX, minY, maxX, maxY, GradientChannel.Straight);
                if (fill)
                {
                    SetFillAlpha(b, CssColor.Black);
                    b.Ascii("/Pattern cs /P").Int(pattern).Ascii(" scn\n");
                    _fill = 1;
                }
                else
                {
                    SetStrokeAlpha(b, CssColor.Black);
                    b.Ascii("/Pattern CS /P").Int(pattern).Ascii(" SCN ");
                    _stroke = 1;
                }
            }
            else if (fill) WriteSvgFill(b, shape, toPage);
            else WriteSvgStroke(b, shape, toPage);
            if (!fill) WriteSvgLineStyle(b, shape);
            if (shape.Kind == SvgShape.KindText) WriteSvgText(b, shape, fill ? 0 : 1);
            else
            {
                WritePath(b, shape);
                b.Ascii(fill ? (shape.EvenOdd ? "f*\n" : "f\n") : "S\n");
            }
            b.Ascii("Q\n");
            RestoreState(saved);
        }
    }

    /// <summary>Sets the fill and stroke of an SVG shape: colours, or shading patterns for gradients.</summary>
    private void WriteSvgPaint(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        WriteSvgFill(b, shape, toPage);
        if (!shape.HasStroke) return;
        WriteSvgStroke(b, shape, toPage);
        WriteSvgLineStyle(b, shape);
    }

    /// <summary>Sets the fill of an SVG shape: a colour, or a shading or tiling pattern.</summary>
    private void WriteSvgFill(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        if (shape.FillGradient is { } fg)
        {
            SetFillAlpha(b, CssColor.Black); // the gradient's stops carry the opacity
            b.Ascii("/Pattern cs /P").Int(AddPattern(fg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" scn\n");
            _fill = 1; // not a colour: the next colour must be written
        }
        else if (shape.FillPattern is { } fp)
        {
            int tile = AddSvgTiling(fp, toPage);
            SetFillAlpha(b, (uint)fp.Alpha << 24);
            b.Ascii("/Pattern cs /T").Int(tile).Ascii(" scn\n");
            _fill = 1;
        }
        else if (shape.Fill is { } fill) SetFill(b, fill);
    }

    /// <summary>Sets the stroke paint of an SVG shape: a colour, or a shading or tiling pattern.</summary>
    private void WriteSvgStroke(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        if (shape.StrokeGradient is { } sg)
        {
            SetStrokeAlpha(b, CssColor.Black);
            b.Ascii("/Pattern CS /P").Int(AddPattern(sg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" SCN ");
            _stroke = 1;
        }
        else if (shape.StrokePattern is { } spat)
        {
            int tile = AddSvgTiling(spat, toPage);
            SetStrokeAlpha(b, (uint)spat.Alpha << 24);
            b.Ascii("/Pattern CS /T").Int(tile).Ascii(" SCN ");
            _stroke = 1;
        }
        else SetStroke(b, shape.Stroke!.Value);
    }

    /// <summary>Sets the width, caps, joins, miter limit and dash pattern of an SVG shape's stroke.</summary>
    private void WriteSvgLineStyle(ByteBuffer b, SvgShape shape)
    {
        _lineWidth = -1;
        b.Num(shape.StrokeWidth).Ascii(" w ").Int((int)shape.LineCap).Ascii(" J ").Int((int)shape.LineJoin).Ascii(" j ")
            .Num(shape.MiterLimit).Ascii(" M\n");
        if (shape.Dash is not { } dash) return;
        b.Ascii("[");
        for (int i = 0; i < dash.Length; i++)
        {
            if (i > 0) b.Sp();
            b.Num(dash[i]);
        }
        b.Ascii("] ").Num(shape.DashPhase).Ascii(" d\n");
    }

    /// <summary>
    /// Puts back the solid line after a dashed SVG stroke. The dash pattern is set only for the shapes that have one, so
    /// the content of solid strokes stays as it was before dashes were supported.
    /// </summary>
    private static void EndSvgDash(ByteBuffer b, SvgShape shape)
    {
        if (shape.Dash != null && shape.HasStroke) b.Ascii("[] 0 d\n");
    }
}

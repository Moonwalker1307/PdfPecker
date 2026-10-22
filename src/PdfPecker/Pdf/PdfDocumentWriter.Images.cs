using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>Streams, images and the watermark.</summary>
internal sealed partial class PdfDocumentWriter
{
    // Reused for every compressed stream, so compressing a page does not allocate a new buffer each time.
    private MemoryStream? _deflated;

    private void WriteStream(int id, ReadOnlySpan<byte> data, bool compress, string? extraDict)
    {
        ReadOnlySpan<byte> body = data;
        bool filtered = false;
        if (compress && data.Length > 64)
        {
            var ms = _deflated ??= new MemoryStream(Math.Max(4096, data.Length / 3 + 64));
            ms.SetLength(0);
            using (var z = new ZLibStream(ms, _level, leaveOpen: true)) z.Write(data);
            body = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            filtered = true;
        }
        WriteStreamObject(id, body, filtered, extraDict);
    }

    /// <summary>Writes a stream object whose data is already compressed (filtered) or meant to stay as it is.</summary>
    private void WriteStreamObject(int id, ReadOnlySpan<byte> body, bool filtered, string? extraDict)
    {
        byte[]? encrypted = _crypt?.Encrypt(body);
        if (encrypted != null) body = encrypted;
        Begin(id).Ascii("<< /Length ").Int(body.Length);
        if (filtered) _out.Ascii(" /Filter /FlateDecode");
        if (extraDict != null) _out.Sp().Ascii(extraDict);
        _out.Ascii(" >>\nstream\n");
        _out.Bytes(body);
        _out.Ascii("\nendstream");
        End();
    }

    private void WriteImage(PdfImage img)
    {
        string colorSpace = img.ColorSpace;
        // An indexed colour space carries its palette as a hex string, which must be encrypted like any other string.
        if (_crypt != null && colorSpace.IndexOf('<') is int lt and >= 0)
        {
            int gt = colorSpace.IndexOf('>', lt);
            var palette = System.Convert.FromHexString(colorSpace.AsSpan(lt + 1, gt - lt - 1));
            var sb = new ByteBuffer(64 + palette.Length * 2);
            sb.Ascii(img.ColorSpace.Substring(0, lt));
            PdfSyntax.Hex(sb, _crypt.Encrypt(palette), upper: true);
            sb.Ascii(img.ColorSpace.Substring(gt + 1));
            colorSpace = System.Text.Encoding.ASCII.GetString(sb.Span);
        }
        var data = _crypt != null ? _crypt.Encrypt(img.Data) : img.Data;
        Begin(img.ObjectNumber).Ascii("<< /Type /XObject /Subtype /Image /Width ").Int(img.Width).Ascii(" /Height ").Int(img.Height)
            .Ascii(" /ColorSpace ").Ascii(colorSpace).Ascii(" /BitsPerComponent ").Int(img.BitsPerComponent)
            .Ascii(" /Filter ").Ascii(img.Filter);
        if (img.DecodeParms != null) _out.Ascii(" /DecodeParms ").Ascii(img.DecodeParms);
        if (img.Decode != null) _out.Ascii(" /Decode ").Ascii(img.Decode);
        if (img.SMask != null) _out.Ascii(" /SMask ").Int(img.SMask.ObjectNumber).Ascii(" 0 R");
        _out.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
        End();
    }

    /// <summary>Writes the images the pages draw, each followed by its soft mask.</summary>
    private void WriteImages(WriteState doc)
    {
        foreach (var img in doc.Images)
        {
            WriteImage(img);
            if (img.SMask != null) WriteImage(img.SMask);
        }
    }

    /// <summary>
    /// Numbers and writes the images of SVG and HTML filter results and of SVG image elements, which only writing the
    /// pages makes.
    /// </summary>
    private void WriteFilterImages(WriteState doc)
    {
        foreach (var raster in _rasters)
        {
            raster.ObjectNumber = doc.Next++;
            if (raster.SMask != null) raster.SMask.ObjectNumber = doc.Next++;
            WriteImage(raster);
            if (raster.SMask != null) WriteImage(raster.SMask);
        }
    }

    /// <summary>
    /// Converts the CMYK images to RGB for PDF/A: an sRGB output intent does not cover DeviceCMYK. A converted image
    /// keeps the /Im number of the image it replaces.
    /// </summary>
    private static void ConvertCmykImages(WriteState doc)
    {
        var images = doc.Images;
        var imageIds = doc.ImageIds;
        for (int i = 0; i < images.Count; i++)
        {
            var rgb = images[i].ToRgb();
            if (rgb == null || ReferenceEquals(rgb, images[i])) continue;
            foreach (var key in imageIds.Where(kv => ReferenceEquals(kv.Key, images[i])).Select(kv => kv.Key).ToList())
                imageIds[rgb] = imageIds[key];
            images[i] = rgb;
        }
    }

    /// <summary>
    /// The watermark text as one TextOp per line, centred on the origin of a rotated coordinate system whose
    /// cosine and sine are returned; the form XObject moves that origin to the page centre.
    /// </summary>
    private (List<TextOp> Lines, float Cos, float Sin) LayoutWatermarkText(PdfWatermark wm, TextFont font)
    {
        float W = _page.Width, H = _page.Height;
        var lines = wm.Text!.Replace("\r\n", "\n").Split('\n');
        double a = (wm.Angle ?? Math.Atan2(H, W) * 180 / Math.PI) * Math.PI / 180;
        float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
        const float lead = 1.15f, cap = 0.72f;
        float ls = float.IsFinite(wm.LetterSpacing) ? Math.Clamp(wm.LetterSpacing, -100, 100) : 0;
        if (ls != 0) font = font.WithoutLigatures(); // as browsers set letter-spaced text
        int maxUnits = 1, maxChars = 0;
        foreach (var line in lines)
        {
            int units = font.MeasureUnits(line, 0, line.Length);
            if (units >= maxUnits) { maxUnits = units; maxChars = line.Length; }
        }
        float size = wm.FontSize;
        if (size <= 0)
        {
            // As large as fits in 80% of the page with the text block rotated; letter spacing does not scale.
            float tw = maxUnits / 1000f, th = lead * (lines.Length - 1) + cap, lsw = ls * maxChars;
            float ac = Math.Abs(c), asn = Math.Abs(sn);
            size = Math.Min((0.8f * W - lsw * ac) / (tw * ac + th * asn), (0.8f * H - lsw * asn) / (tw * asn + th * ac));
            size = Math.Clamp(size, 4, 400);
        }
        uint color = CssColor.TryParse(wm.Color ?? "", CssColor.Black, out var parsed) ? parsed : 0xFF808080;
        float blockH = size * (lead * (lines.Length - 1) + cap);
        var ops = new List<TextOp>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            // Centred on the letters: the spacing after the last one is left out.
            float xl = -(font.Measure(lines[i], size) + ls * Math.Max(0, lines[i].Length - 1)) / 2;
            float yl = blockH / 2 - cap * size - i * lead * size;
            ops.Add(new TextOp(xl, yl, 0, font, size, color, lines[i], 0, ls));
        }
        return (ops, c, sn);
    }

    /// <summary>
    /// Makes the watermark ready to draw: decodes its image, or takes its SVG drawing, and lays out its text, collecting
    /// the fonts and the image they use. A watermark with nothing to draw is left out.
    /// </summary>
    private void PrepareWatermark(WriteState doc)
    {
        var wm = Watermark;
        if (wm == null) return;
        if (wm.Image != null && (doc.WatermarkImage = PdfImage.FromBytes(wm.Image)) == null)
        {
            doc.WatermarkSvg = WatermarkSvg ?? throw new ArgumentException("The watermark image is not a PNG, JPEG, GIF, WebP, BMP, TIFF or SVG image this library can read.");
            CollectSvgFonts(doc.WatermarkSvg, doc.UsedFonts);
        }
        if (doc.WatermarkImage != null) AddImage(doc, doc.WatermarkImage);
        if (!string.IsNullOrEmpty(wm.Text))
        {
            doc.WatermarkText = LayoutWatermarkText(wm, WatermarkFont ?? StandardFont.Get(FontFamily.Helvetica, wm.Bold, false));
            foreach (var line in doc.WatermarkText.Value.Lines) CollectFonts(line, doc.UsedFonts);
        }
        if (doc.WatermarkText != null || doc.WatermarkImage != null || doc.WatermarkSvg != null) doc.Watermark = wm;
    }

    /// <summary>Writes the watermark's form XObject, with the fonts of the shared resources.</summary>
    private void WriteWatermark(WriteState doc)
    {
        var fontDict = new ByteBuffer(128);
        WriteFontEntries(fontDict, doc);
        WriteWatermark(doc.WatermarkId, doc.Watermark!, doc.WatermarkText, fontDict, doc.WatermarkImage, doc.WatermarkSvg, doc.WatermarkSvgId);
    }

    /// <summary>The watermark as a form XObject covering the page, drawn by every page's content stream.</summary>
    private void WriteWatermark(int id, PdfWatermark wm, (List<TextOp> Lines, float Cos, float Sin)? text, ByteBuffer fontDict, PdfImage? image,
        SvgImage? svg, int svgId)
    {
        float W = _page.Width, H = _page.Height, cx = W / 2, cy = H / 2;
        float opacity = Math.Clamp(wm.Opacity, 0, 1);
        var b = new ByteBuffer(512);
        ResetState();
        _noAlpha = true; // the watermark's own resources have no alpha states: its opacity applies to all of it
        b.Ascii("q /Gw gs\n");
        if (image != null)
        {
            float iw = wm.ImageWidth > 0 ? wm.ImageWidth : W / 2;
            float ih = iw * image.Height / Math.Max(1, image.Width);
            double a = (wm.Angle ?? 0) * Math.PI / 180;
            float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            b.Num(iw * c).Sp().Num(iw * sn).Sp().Num(-ih * sn).Sp().Num(ih * c).Sp()
                .Num(cx - iw * c / 2 + ih * sn / 2).Sp().Num(cy - iw * sn / 2 - ih * c / 2).Ascii(" cm /Iw Do\n");
            b.Ascii("Q q /Gw gs\n");
        }
        else if (svg != null)
        {
            // The drawing is a transparency group of its own with the shared resources: its own translucent parts keep
            // their alpha, and the watermark's opacity applies to the group as a whole.
            float iw = wm.ImageWidth > 0 ? wm.ImageWidth : W / 2;
            float ih = iw * SvgAspect(svg);
            double a = (wm.Angle ?? 0) * Math.PI / 180;
            float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            b.Num(c).Sp().Num(sn).Sp().Num(-sn).Sp().Num(c).Sp()
                .Num(cx - iw * c / 2 + ih * sn / 2).Sp().Num(cy - iw * sn / 2 - ih * c / 2).Ascii(" cm /Iw Do\n");
            b.Ascii("Q q /Gw gs\n");
            var drawing = new ByteBuffer(1024);
            var saved = SaveState();
            _noAlpha = false;
            (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
            _anyAlpha = true;
            var streamSpace = StartStreamSpace();
            WriteSvg(drawing, svg, 0, ih, iw, ih);
            _streamSpace = streamSpace;
            RestoreState(saved);
            _noAlpha = true;
            WriteStream(svgId, drawing.Span, _compress, "/Type /XObject /Subtype /Form /BBox [0 0 " + Fmt(iw) + " " + Fmt(ih)
                + "] /Group << /S /Transparency >> /Resources " + _resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R");
        }
        if (text is { } t)
        {
            b.Num(t.Cos).Sp().Num(t.Sin).Sp().Num(-t.Sin).Sp().Num(t.Cos).Sp().Num(cx).Sp().Num(cy).Ascii(" cm\nBT\n");
            _fill = 0; // force the colour to be written
            foreach (var line in t.Lines)
            {
                SetFill(b, line.Color);
                if (_tc != line.CharSpacing) { _tc = line.CharSpacing; b.Num(line.CharSpacing).Ascii(" Tc\n"); }
                WriteTextLine(b, line, line.X, line.Baseline);
            }
            b.Ascii("ET\n");
        }
        b.Ascii("Q\n");
        _noAlpha = false;

        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [0 0 ").Num(W).Sp().Num(H).Ascii("] /Resources << /ExtGState << /Gw << /Type /ExtGState /ca ")
            .Num(opacity).Ascii(" /CA ").Num(opacity).Ascii(" >> >>");
        if (text != null) dict.Ascii(" /Font <<").Bytes(fontDict.Span).Ascii(" >>");
        if (image != null) dict.Ascii(" /XObject << /Iw ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        else if (svg != null) dict.Ascii(" /XObject << /Iw ").Int(svgId).Ascii(" 0 R >>");
        dict.Ascii(" >>");
        WriteStream(id, b.Span, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    /// <summary>Height over width of an SVG drawing: from its width and height, else its viewBox, else 300 × 150.</summary>
    private static float SvgAspect(SvgImage svg)
    {
        if (svg.Width is > 0 and var w && svg.Height is > 0 and var h) return h / w;
        if (svg.HasViewBox) return svg.ViewBoxHeight / svg.ViewBoxWidth;
        return 0.5f;
    }

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

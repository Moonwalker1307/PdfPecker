using PdfPecker.Rendering;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

internal sealed partial class PdfCompactWriter
{
    /// <summary>The JPEG quality of a downsampled JPEG when <see cref="PdfCompactOptions.ImageQuality"/> is not set.</summary>
    private const int DefaultJpegQuality = 85;

    // Limits on the walk that finds where images are drawn: forms drawn inside forms, how many forms are expanded and how
    // many operators are read in all. A walk that hits one gives up, and then no image is downsampled.
    private const int MaxFormDepth = 12;
    private const int MaxFormExpansions = 10_000;
    private const long MaxOperators = 20_000_000;

    /// <summary>
    /// The image downsampled to <see cref="PdfCompactOptions.MaxImageDpi"/> where it is drawn larger than that, and
    /// recompressed as JPEG at <see cref="PdfCompactOptions.ImageQuality"/> (a downsampled JPEG stays a JPEG; other
    /// downsampled images stay lossless unless a quality is set), or null when the image is not one this changes. Only
    /// 8-bit greyscale and RGB images qualify; masks, images with colour key masks or decode arrays, and other colour
    /// spaces keep their exact samples, as the rest of the file relies on them.
    /// </summary>
    private PdfStream? RecompressImage(int key, PdfStream s, Dictionary<int, double>? placements)
    {
        if (_file.GetName(s, "Subtype") != "Image" || _file.GetBool(s, "ImageMask", false) || _maskImages.Contains(key)) return null;
        if (s.ContainsKey("Decode") || _file.Resolve(s["Mask"]) is PdfArray || s.ContainsKey("SMaskInData")) return null;
        if (_file.GetInt(s, "BitsPerComponent", 0) != 8) return null;
        int colors = ColorComponents(s["ColorSpace"]);
        bool gray = colors == 1, isJpeg = FilterNames(s) is { Count: > 0 } chain && chain[^1] == "DCTDecode";
        if (colors is not (1 or 3) || !IsGrayOrRgb(s["ColorSpace"])) return null;

        int width = _file.GetInt(s, "Width", 0), height = _file.GetInt(s, "Height", 0);
        double scale = 1;
        if (placements != null && placements.TryGetValue(key, out double dpi) && dpi > _options.MaxImageDpi!.Value)
            scale = _options.MaxImageDpi.Value / dpi;
        int newWidth = Math.Max(1, (int)Math.Round(width * scale)), newHeight = Math.Max(1, (int)Math.Round(height * scale));
        bool downsample = newWidth < width || newHeight < height;
        if (!downsample && _options.ImageQuality == null) return null;

        DecodedImage? image;
        try
        {
            // As an inline image the decoder stops before the masks, which stay objects of their own.
            image = PdfImageDecoder.Decode(_file, s, s.RawData, alreadyDecoded: false, resources: null, inline: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
        if (image?.Pixels == null || image.Width != width || image.Height != height) return null;
        var pixels = downsample ? Resample(image.Pixels, width, height, newWidth, newHeight) : image.Pixels;

        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("DecodeParms");
        entries.Remove("DL");
        entries["Width"] = PdfNumber.Int(newWidth);
        entries["Height"] = PdfNumber.Int(newHeight);
        if (_options.ImageQuality != null || isJpeg)
        {
            int quality = _options.ImageQuality ?? DefaultJpegQuality;
            if (newWidth > JpegEncoder.MaxDimension || newHeight > JpegEncoder.MaxDimension) return null;
            var jpeg = gray ? JpegEncoder.EncodeGray(pixels, newWidth, newHeight, quality) : JpegEncoder.Encode(pixels, newWidth, newHeight, quality, 0xFFFFFF);
            entries["Filter"] = PdfName.Get("DCTDecode");
            return new PdfStream(entries, jpeg);
        }
        var samples = Samples(pixels, gray);
        var layout = new SampleLayout(newWidth, newHeight, colors, 8);
        entries["Filter"] = PdfName.Get("FlateDecode");
        entries["DecodeParms"] = PredictorParms(layout);
        return new PdfStream(entries, Deflate(PngPredict(samples, layout.RowBytes, layout.BytesPerPixel)));
    }

    /// <summary>Whether a colour space is DeviceGray, DeviceRGB or an ICC profile of one or three components.</summary>
    private bool IsGrayOrRgb(PdfObject? colorSpace) => _file.Resolve(colorSpace) switch
    {
        PdfName { Value: "DeviceGray" or "DeviceRGB" } => true,
        PdfArray { Count: 2 } a => _file.Resolve(a[0]) is PdfName { Value: "ICCBased" },
        _ => false,
    };

    /// <summary>The 8-bit samples of opaque pixels: one grey byte (the red channel) or three RGB bytes per pixel.</summary>
    private static byte[] Samples(uint[] pixels, bool gray)
    {
        var samples = new byte[pixels.Length * (gray ? 1 : 3)];
        for (int i = 0, j = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            samples[j++] = (byte)p;
            if (gray) continue;
            samples[j++] = (byte)(p >> 8);
            samples[j++] = (byte)(p >> 16);
        }
        return samples;
    }

    /// <summary>
    /// Shrinks opaque pixels to <paramref name="newWidth"/> × <paramref name="newHeight"/>, each new pixel the average of
    /// the source pixels it covers.
    /// </summary>
    private static uint[] Resample(uint[] pixels, int width, int height, int newWidth, int newHeight)
    {
        var result = new uint[newWidth * newHeight];
        for (int y = 0; y < newHeight; y++)
        {
            int y0 = (int)((long)y * height / newHeight), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / newHeight));
            for (int x = 0; x < newWidth; x++)
            {
                int x0 = (int)((long)x * width / newWidth), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * width / newWidth));
                long r = 0, g = 0, b = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        uint p = pixels[sy * width + sx];
                        r += p & 0xFF;
                        g += (p >> 8) & 0xFF;
                        b += (p >> 16) & 0xFF;
                    }
                long n = (long)(x1 - x0) * (y1 - y0), half = n / 2;
                result[y * newWidth + x] = 0xFF000000 | (uint)((b + half) / n) << 16 | (uint)((g + half) / n) << 8 | (uint)((r + half) / n);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ where images are drawn

    /// <summary>
    /// The lowest resolution, in pixels per inch, at which each image is drawn on any page or annotation appearance, by
    /// image object number; null when the walk had to give up, so that no image is downsampled on partial knowledge.
    /// Images drawn only where the walk does not look (patterns, Type 3 glyphs) are not in the map and keep their size.
    /// </summary>
    private Dictionary<int, double>? ScanImagePlacements()
    {
        var scan = new PlacementScan(_file);
        try
        {
            var pages = new Stack<(PdfDict Node, PdfDict? Resources, int Depth)>();
            var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
            if (_file.GetDict(_file.Catalog, "Pages") is { } root) pages.Push((root, null, 0));
            while (pages.Count > 0)
            {
                var (node, inherited, depth) = pages.Pop();
                if (depth > 64 || !seen.Add(node)) continue;
                var resources = _file.GetDict(node, "Resources") ?? inherited;
                if (_file.GetArray(node, "Kids") is { } kids)
                {
                    for (int i = kids.Count - 1; i >= 0; i--)
                        if (_file.Resolve(kids[i]) is PdfDict kid) pages.Push((kid, resources, depth + 1));
                    continue;
                }
                scan.Content(PageContent(node), resources, Matrix.Identity, 0);
                if (_file.GetArray(node, "Annots") is { } annots)
                    foreach (var a in annots.Items)
                        if (_file.Resolve(a) is PdfDict annot) scan.Appearance(annot, resources);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or PdfLimitException)
        {
            return null;
        }
        return scan.Complete ? scan.Lowest : null;
    }

    /// <summary>A page's content streams, decoded and joined by line feeds; damaged ones are left out.</summary>
    private byte[] PageContent(PdfDict page)
    {
        var parts = new List<byte[]>();
        switch (_file.Resolve(page["Contents"]))
        {
            case PdfStream s:
                parts.Add(PlacementScan.TryDecode(_file, s));
                break;
            case PdfArray a:
                foreach (var item in a.Items)
                    if (_file.Resolve(item) is PdfStream part) parts.Add(PlacementScan.TryDecode(_file, part));
                break;
        }
        int length = parts.Sum(p => p.Length + 1);
        var joined = new byte[length];
        int at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(joined, at);
            at += p.Length;
            joined[at++] = (byte)'\n';
        }
        return joined;
    }

    /// <summary>Follows content streams and forms, tracking the transformation, and records where each image is drawn.</summary>
    private sealed class PlacementScan(PdfFile file)
    {
        public Dictionary<int, double> Lowest { get; } = new();

        /// <summary>False once a limit cut the walk short.</summary>
        public bool Complete { get; private set; } = true;

        private int _expansions;
        private long _operators;
        private readonly Dictionary<PdfStream, byte[]> _forms = new(ReferenceEqualityComparer.Instance);

        public static byte[] TryDecode(PdfFile file, PdfStream s)
        {
            try
            {
                return file.DecodeStream(s);
            }
            catch (Exception ex) when (ex is NotSupportedException or PdfFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                SwallowedErrors.Record(ex);
                return Array.Empty<byte>();
            }
        }

        /// <summary>The normal appearance of an annotation, drawn into its rectangle.</summary>
        public void Appearance(PdfDict annot, PdfDict? pageResources)
        {
            var appearance = file.Resolve(file.GetDict(annot, "AP")?["N"]);
            var rect = file.GetNumbers(annot, "Rect");
            if (rect is not { Length: 4 }) return;
            var forms = new List<PdfStream>();
            if (appearance is PdfStream single) forms.Add(single);
            else if (appearance is PdfDict states)
                foreach (var v in states.Entries.Values)
                    if (file.Resolve(v) is PdfStream state) forms.Add(state);
            foreach (var form in forms)
            {
                // The form's bounding box, transformed by its matrix, is scaled into the rectangle (PDF 12.5.5).
                var bbox = file.GetNumbers(form, "BBox");
                var matrix = FormMatrix(file.GetNumbers(form, "Matrix"));
                if (bbox is not { Length: 4 }) continue;
                double w = Math.Abs(bbox[2] - bbox[0]) * Math.Max(Math.Abs(matrix.A), Math.Abs(matrix.C));
                double h = Math.Abs(bbox[3] - bbox[1]) * Math.Max(Math.Abs(matrix.B), Math.Abs(matrix.D));
                if (w <= 0 || h <= 0) continue;
                var fit = new Matrix(Math.Abs(rect[2] - rect[0]) / w, 0, 0, Math.Abs(rect[3] - rect[1]) / h, 0, 0);
                Form(form, pageResources, fit, 0);
            }
        }

        /// <summary>Reads a content stream: q, Q and cm change the transformation, and Do draws an image or a form.</summary>
        public void Content(byte[] content, PdfDict? resources, Matrix ctm, int depth)
        {
            var stack = new Stack<Matrix>();
            var parser = new ContentParser(content);
            while (parser.Next())
            {
                if (++_operators > MaxOperators)
                {
                    Complete = false;
                    return;
                }
                switch (parser.Op)
                {
                    case "q":
                        if (stack.Count < 1024) stack.Push(ctm);
                        break;
                    case "Q":
                        if (stack.Count > 0) ctm = stack.Pop();
                        break;
                    case "cm" when parser.Operands.Count == 6 && parser.Operands.All(o => o.Kind == OperandKind.Number):
                        var o = parser.Operands;
                        ctm = new Matrix(o[0].Num, o[1].Num, o[2].Num, o[3].Num, o[4].Num, o[5].Num).Multiply(ctm);
                        break;
                    case "Do" when parser.Operands.Count == 1 && parser.Operands[0].Name is { } name:
                        Draw(file.GetDict(resources, "XObject")?[name], resources, ctm, depth);
                        break;
                }
                if (!Complete) return;
            }
        }

        private void Draw(PdfObject? xobject, PdfDict? resources, Matrix ctm, int depth)
        {
            if (file.Resolve(xobject) is not PdfStream s) return;
            switch (file.GetName(s, "Subtype"))
            {
                case "Image" when xobject is PdfRef r:
                    // The image fills the unit square, so its sides are as long as the transformed unit vectors.
                    double across = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), down = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D);
                    int width = file.GetInt(s, "Width", 0), height = file.GetInt(s, "Height", 0);
                    if (across <= 0 || down <= 0 || width <= 0 || height <= 0) return;
                    double dpi = Math.Min(width * 72 / across, height * 72 / down);
                    if (!double.IsFinite(dpi)) return;
                    if (!Lowest.TryGetValue(r.Num, out double lowest) || dpi < lowest) Lowest[r.Num] = dpi;
                    break;
                case "Form":
                    Form(s, resources, ctm, depth);
                    break;
            }
        }

        private void Form(PdfStream form, PdfDict? resources, Matrix ctm, int depth)
        {
            if (depth >= MaxFormDepth || ++_expansions > MaxFormExpansions)
            {
                Complete = false;
                return;
            }
            var matrix = FormMatrix(file.GetNumbers(form, "Matrix"));
            // A form drawn many times is decoded once.
            if (!_forms.TryGetValue(form, out var content)) _forms[form] = content = TryDecode(file, form);
            Content(content, file.GetDict(form, "Resources") ?? resources, matrix.Multiply(ctm), depth + 1);
        }

        // Unlike the renderer, which takes the first six numbers of a longer array, a form matrix of any length but six is
        // ignored here, as it always was, so the images this scan resamples stay the same.
        private static Matrix FormMatrix(double[]? numbers) => numbers is { Length: 6 } ? Matrix.FromArray(numbers) : Matrix.Identity;
    }
}

namespace PdfPecker.Images;

/// <summary>An image ready to be written as a PDF image XObject.</summary>
internal sealed partial class PdfImage
{
    public int Width, Height;
    public string ColorSpace = DeviceRgb; // a PDF object (name or array)
    public int BitsPerComponent = 8;
    public string Filter = FlateFilter;
    public string? DecodeParms;
    public string? Decode;
    public byte[] Data = Array.Empty<byte>();
    public PdfImage? SMask;
    public int ObjectNumber; // assigned by the writer

    private const string DeviceRgb = "/DeviceRGB", DeviceGray = "/DeviceGray", DeviceCmyk = "/DeviceCMYK";
    private const string FlateFilter = "/FlateDecode", DctFilter = "/DCTDecode";

    // Adobe CMYK JPEGs store their values inverted; this Decode array turns them back.
    private const string InvertedCmykDecode = "[1 0 1 0 1 0 1 0]";

    /// <summary>The largest image, in pixels, that is decoded (100 megapixels, as for PDF rendering).</summary>
    private const long MaxPixels = Rendering.Images.PdfImageDecoder.MaxPixels;

    /// <summary>A copy for another document: it shares the pixel data, which is never changed, but not the object number.</summary>
    public PdfImage Copy()
    {
        var copy = (PdfImage)MemberwiseClone();
        copy.ObjectNumber = 0;
        copy.SMask = SMask?.Copy();
        return copy;
    }

    /// <summary>Decodes JPEG, PNG, GIF, WebP, BMP or TIFF data. Returns null for unsupported or corrupt images.</summary>
    public static PdfImage? FromBytes(byte[] data)
    {
        try
        {
            if (IsJpeg(data)) return FromJpeg(data);
            if (IsPng(data)) return FromPng(data);
            if (GifDecoder.IsGif(data)) return FromPixels(GifDecoder.Decode(data, MaxPixels));
            if (WebPDecoder.IsWebP(data)) return FromPixels(WebPDecoder.Decode(data, MaxPixels));
            if (BmpDecoder.IsBmp(data))
                return BmpDecoder.Wrapped(data) is { } wrapped && !BmpDecoder.IsBmp(wrapped) ? FromBytes(wrapped) : FromPixels(BmpDecoder.Decode(data, MaxPixels));
            if (TiffDecoder.IsTiff(data)) return TiffDecoder.Decode(data, MaxPixels);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or EndOfStreamException or OverflowException)
        {
        }
        return null;
    }

    /// <summary>Decoded RGB pixels, with a soft mask when some of them are not opaque.</summary>
    internal static PdfImage? FromPixels(DecodedImage? decoded)
    {
        if (decoded is not { } d) return null;
        var image = new PdfImage { Width = d.Width, Height = d.Height, BitsPerComponent = 8, ColorSpace = DeviceRgb, Data = Deflate(d.Rgb) };
        if (d.Alpha != null && HasTransparency(d.Alpha))
            image.SMask = new PdfImage { Width = d.Width, Height = d.Height, BitsPerComponent = 8, ColorSpace = DeviceGray, Data = Deflate(d.Alpha) };
        return image;
    }

    private static bool HasTransparency(byte[] alpha) => alpha.AsSpan().IndexOfAnyExcept((byte)255) >= 0;

    /// <summary>
    /// Cuts the given rectangles (in pixels) out of this image as images of their own: the slices of a border image.
    /// Drawing a slice out of the whole image instead would let a viewer's smoothing blend in the pixels around it. A
    /// rectangle with no pixels gives null; the whole result is null when the image's encoding cannot be cut here
    /// (CMYK, other than 8 bits per component, or an unusual filter).
    /// </summary>
    public PdfImage?[]? Cut((int X, int Y, int W, int H)[] rects)
    {
        try
        {
            if (Samples() is not { } color) return null;
            byte[]? alpha = null;
            if (SMask != null)
            {
                if (SMask.Width != Width || SMask.Height != Height || SMask.Samples() is not { Colors: 1 } mask) return null;
                alpha = mask.Data;
            }
            var result = new PdfImage?[rects.Length];
            for (int i = 0; i < rects.Length; i++)
            {
                var (x, y, w, h) = rects[i];
                if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > Width || y + h > Height) continue;
                var image = new PdfImage { Width = w, Height = h, ColorSpace = ColorSpace, Data = Deflate(CopyRect(color.Data, color.Colors, x, y, w, h)) };
                if (alpha != null) image.SMask = new PdfImage { Width = w, Height = h, ColorSpace = DeviceGray, Data = Deflate(CopyRect(alpha, 1, x, y, w, h)) };
                result[i] = image;
            }
            return result;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or FormatException or EndOfStreamException)
        {
            return null;
        }
    }

    private byte[] CopyRect(byte[] pixels, int colors, int x, int y, int w, int h)
    {
        var part = new byte[w * h * colors];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(pixels, ((y + row) * Width + x) * colors, part, row * w * colors, w * colors);
        return part;
    }

    /// <summary>The image's samples, 8 bits each and one to three per pixel, without row filters; null when not supported.</summary>
    private (byte[] Data, int Colors)? Samples()
    {
        if (BitsPerComponent != 8 || Decode != null || (long)Width * Height > MaxPixels) return null;
        int colors = ColorSpace == DeviceRgb ? 3 : ColorSpace == DeviceGray || ColorSpace.StartsWith("[/Indexed", StringComparison.Ordinal) ? 1 : 0;
        if (colors == 0) return null;
        long size = (long)Width * Height * colors;
        if (Filter == DctFilter)
        {
            if (Rendering.Images.JpegDecoder.Decode(Data) is not { } jpg || jpg.Width != Width || jpg.Height != Height
                || jpg.Components != colors || jpg.Data.Length < size) return null;
            return (jpg.Data, colors);
        }
        if (Filter != FlateFilter) return null;
        if (DecodeParms == null)
        {
            var raw = Inflate(Data, size);
            return raw.Length >= size ? (raw, colors) : null;
        }
        // The PNG row filters of an image passed through from a PNG file.
        if (!DecodeParms.StartsWith("<< /Predictor 15 ", StringComparison.Ordinal)) return null;
        int stride = Width * colors;
        var filtered = Inflate(Data, (stride + 1L) * Height);
        UnfilterInPlace(filtered, stride, Height, colors);
        var pixels = new byte[size];
        for (int row = 0; row < Height; row++) Buffer.BlockCopy(filtered, row * (stride + 1) + 1, pixels, row * stride, stride);
        return (pixels, colors);
    }

    /// <summary>
    /// A DeviceRGB copy of a CMYK JPEG, for PDF/A files whose sRGB output intent does not allow DeviceCMYK.
    /// Returns the image itself when it is not CMYK, or null when it cannot be decoded.
    /// </summary>
    public PdfImage? ToRgb()
    {
        if (ColorSpace != DeviceCmyk) return this;
        if (Filter != DctFilter) return null;
        var jpg = Rendering.Images.JpegDecoder.Decode(Data);
        if (jpg == null || jpg.Value.Components != 4) return null;
        var (cmyk, w, h, _) = jpg.Value;
        bool inverted = Decode != null;
        var rgb = new byte[w * h * 3];
        // Photographs repeat colours a lot and the conversion is costly, so converted colours are kept, up to a bound.
        const int MaxCachedColors = 65536;
        var cache = new Dictionary<int, int>();
        for (int i = 0, o = 0; i + 3 < cmyk.Length && o + 2 < rgb.Length; i += 4, o += 3)
        {
            int key = cmyk[i] << 24 | cmyk[i + 1] << 16 | cmyk[i + 2] << 8 | cmyk[i + 3];
            if (!cache.TryGetValue(key, out int packed))
            {
                float c = cmyk[i] / 255f, m = cmyk[i + 1] / 255f, y = cmyk[i + 2] / 255f, k = cmyk[i + 3] / 255f;
                if (inverted) { c = 1 - c; m = 1 - m; y = 1 - y; k = 1 - k; }
                var (r, g, b) = Rendering.Content.DeviceCmyk.Convert(c, m, y, k);
                packed = r << 16 | g << 8 | b;
                if (cache.Count < MaxCachedColors) cache[key] = packed;
            }
            rgb[o] = (byte)(packed >> 16);
            rgb[o + 1] = (byte)(packed >> 8);
            rgb[o + 2] = (byte)packed;
        }
        return new PdfImage { Width = w, Height = h, ColorSpace = DeviceRgb, BitsPerComponent = 8, Data = Deflate(rgb), SMask = SMask };
    }

    /// <summary>
    /// The image's colours as 8-bit RGB, without its soft mask. The renderer decodes it from a one-image PDF, so that
    /// every form the writer embeds (JPEG, PNG rows with predictors, palettes, grey, CMYK) is read as viewers read it.
    /// Null when it cannot be decoded.
    /// </summary>
    public (byte[] Rgb, int Width, int Height)? DecodeRgb()
    {
        var pdf = new Pdf.ByteBuffer(Data.Length + 1024);
        var objects = new Pdf.PdfXref();
        pdf.Ascii("%PDF-1.4\n");
        objects.Begin(pdf, 1).Ascii("<< /Type /XObject /Subtype /Image /Width ").Int(Width).Ascii(" /Height ").Int(Height)
            .Ascii(" /ColorSpace ").Ascii(ColorSpace).Ascii(" /BitsPerComponent ").Int(BitsPerComponent);
        if (Filter.Length > 0) pdf.Ascii(" /Filter ").Ascii(Filter);
        if (DecodeParms != null) pdf.Ascii(" /DecodeParms ").Ascii(DecodeParms);
        if (Decode != null) pdf.Ascii(" /Decode ").Ascii(Decode);
        pdf.Ascii(" /Length ").Int(Data.Length).Ascii(" >>\nstream\n").Bytes(Data).Ascii("\nendstream");
        Pdf.PdfXref.End(pdf);
        objects.Begin(pdf, 2).Ascii("<< /Type /Catalog /Pages 3 0 R >>");
        Pdf.PdfXref.End(pdf);
        objects.Begin(pdf, 3).Ascii("<< /Type /Pages /Count 0 /Kids [] >>");
        Pdf.PdfXref.End(pdf);
        long xref = pdf.Length;
        objects.WriteTable(pdf, 4);
        Pdf.PdfXref.WriteTrailer(pdf, 4, 2, 0, 0, default, default, upperHexId: false, xref);
        try
        {
            var file = Rendering.Parsing.PdfFile.Open(pdf.Span.ToArray(), null);
            if (file.GetObject(1) is not Rendering.Parsing.PdfStream stream) return null;
            var decoded = Rendering.Images.PdfImageDecoder.Decode(file, stream, Array.Empty<byte>(), alreadyDecoded: false, null, inline: false);
            if (decoded?.Pixels is not { } px) return null;
            var rgb = new byte[px.Length * 3];
            for (int i = 0, o = 0; i < px.Length; i++, o += 3)
            {
                uint p = px[i];
                rgb[o] = (byte)p;
                rgb[o + 1] = (byte)(p >> 8);
                rgb[o + 2] = (byte)(p >> 16);
            }
            return (rgb, decoded.Width, decoded.Height);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException
                                       or FormatException or Rendering.Parsing.PdfFormatException)
        {
            return null;
        }
    }
}

using PdfPecker.Rendering.Images;

namespace PdfPecker.Images;

/// <summary>
/// Decodes image files to <see cref="ImagePlanes"/> for <see cref="ImageToPixels"/>, giving the pixels that
/// <see cref="ImageToPdf"/> would embed and the renderer would draw, without the PDF in between. Where
/// <see cref="PdfImage.FromBytes"/> already makes 8-bit planes (GIF, WebP, BMP, TIFF and PNG with transparency) they are
/// taken before they are compressed; JPEG and PNG of 8-bit grey or RGB are decoded straight to their samples; the rest
/// (CMYK JPEG, palette PNG and PNG of other depths) go through the renderer's image decoder, as a page would.
/// </summary>
internal static class PixelDecoder
{
    /// <summary>The largest frame count reported, as far as a TIFF directory chain is followed.</summary>
    public const int MaxFrames = 1 << 16;

    /// <summary>The number of frames the data has: the pages of a TIFF file, 1 for the other formats, 0 when it is none of them.</summary>
    public static int FrameCount(byte[] data)
    {
        if (TiffDecoder.IsTiff(data)) return TiffDirectory.Count(data, MaxFrames);
        return IsJpeg(data) || IsPng(data) || GifDecoder.IsGif(data) || WebPDecoder.IsWebP(data) || BmpDecoder.IsBmp(data) ? 1 : 0;
    }

    /// <summary>
    /// Decodes frame <paramref name="frame"/> (a TIFF page; 0 for the other formats). Null when the data is not a
    /// readable image or has more than <paramref name="maxPixels"/> pixels.
    /// </summary>
    public static ImagePlanes? Decode(byte[] data, int frame, bool applyOrientation, long maxPixels)
    {
        maxPixels = Math.Min(maxPixels, PdfImageDecoder.MaxPixels);
        if (TiffDecoder.IsTiff(data)) return TiffDecoder.DecodePixels(data, maxPixels, frame);
        if (frame != 0) return null;
        if (IsJpeg(data))
        {
            var planes = Jpeg(data, maxPixels);
            return applyOrientation ? planes?.Oriented(ExifReader.ReadOrientation(data)) : planes;
        }
        if (IsPng(data)) return PdfImage.PngPlanes(data, maxPixels) ?? ThroughRenderer(PdfImage.FromBytes(data), maxPixels);
        if (GifDecoder.IsGif(data)) return FromDecoded(GifDecoder.Decode(data, maxPixels));
        if (WebPDecoder.IsWebP(data)) return FromDecoded(WebPDecoder.Decode(data, maxPixels));
        if (BmpDecoder.IsBmp(data))
        {
            // A bitmap may hold a PNG or JPEG file, which is decoded as that file would be.
            return BmpDecoder.Wrapped(data) is { } wrapped && !BmpDecoder.IsBmp(wrapped)
                ? Decode(wrapped, 0, applyOrientation, maxPixels)
                : FromDecoded(BmpDecoder.Decode(data, maxPixels));
        }
        return null;
    }

    private static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsPng(byte[] data) => data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G';

    private static ImagePlanes? FromDecoded(DecodedImage? decoded)
        => decoded is { } d ? new ImagePlanes(d.Width, d.Height, d.Rgb, 3, d.Alpha) : null;

    /// <summary>A grey or RGB JPEG decoded to its samples, as the renderer reads them; CMYK through the renderer.</summary>
    private static ImagePlanes? Jpeg(byte[] data, long maxPixels)
    {
        // Without turning, the image is the file's own data with the size and colour space of its frame header.
        if (PdfImage.FromBytes(data, asStored: true) is not { } header || (long)header.Width * header.Height > maxPixels) return null;
        if (header.ColorSpace == PdfImage.DeviceCmyk) return ThroughRenderer(header, maxPixels);
        if (JpegDecoder.Decode(data) is not { } jpeg || (long)jpeg.Width * jpeg.Height > maxPixels) return null;
        if (jpeg.Components is not (1 or 3) || jpeg.Data.Length < (long)jpeg.Width * jpeg.Height * jpeg.Components)
            return ThroughRenderer(header, maxPixels);
        var samples = jpeg.Data.Length == jpeg.Width * jpeg.Height * jpeg.Components ? jpeg.Data : jpeg.Data[..(jpeg.Width * jpeg.Height * jpeg.Components)];
        return new ImagePlanes(jpeg.Width, jpeg.Height, samples, jpeg.Components, null);
    }

    /// <summary>An image as the renderer decodes it from a PDF, for the forms that are not decoded here.</summary>
    private static ImagePlanes? ThroughRenderer(PdfImage? image, long maxPixels)
    {
        if (image == null || (long)image.Width * image.Height > maxPixels || image.DecodeRgb() is not { } color) return null;
        var (rgb, width, height) = color;
        byte[]? alpha = null;
        if (image.SMask?.DecodeRgb() is { } mask && mask.Width == width && mask.Height == height) alpha = EveryThird(mask.Rgb, width * height);
        return image.ColorSpace == PdfImage.DeviceGray
            ? new ImagePlanes(width, height, EveryThird(rgb, width * height), 1, alpha)
            : new ImagePlanes(width, height, rgb, 3, alpha);
    }

    /// <summary>The red samples of RGB data: the grey levels of a grey image read as RGB.</summary>
    private static byte[] EveryThird(byte[] rgb, int count)
    {
        var gray = new byte[count];
        for (int i = 0; i < count; i++) gray[i] = rgb[i * 3];
        return gray;
    }
}

using System.Runtime.CompilerServices;

namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    // An enlarged pixel is written as this many image pixels per point it covers on the page (288 per inch), which is
    // finer than print and screen at ordinary zoom, so a viewer's smoothing only softens a thin rim of each pixel.
    private const float PixelatedSamplesPerPoint = 4;
    private const int MaxPixelatedFactor = 32;
    private const long MaxPixelatedPixels = 16_000_000;

    private static readonly ConditionalWeakTable<PdfImage, Dictionary<int, PdfImage>> PixelatedImages = new();

    /// <summary>
    /// The image for image-rendering: pixelated and crisp-edges when it is drawn <paramref name="drawnWidth"/> by
    /// <paramref name="drawnHeight"/> points: each pixel repeated as a block of image pixels, so that it keeps hard edges
    /// in every viewer. PDF viewers smooth enlarged images as they like, and Chrome prints them without saying how, so
    /// this is the one way a PDF shows a QR code or pixel art sharp everywhere. The image itself is returned when it is
    /// not enlarged, would grow too large, or cannot be decoded here.
    /// </summary>
    public PdfImage Pixelated(float drawnWidth, float drawnHeight)
    {
        if (Width <= 0 || Height <= 0 || !(drawnWidth > 0) || !(drawnHeight > 0)) return this;
        float perPixel = Math.Min(drawnWidth / Width, drawnHeight / Height);
        // An image drawn at no more than one CSS pixel per pixel is not enlarged, and looks the same either way.
        if (!float.IsFinite(perPixel) || perPixel <= 0.75f) return this;
        int factor = (int)Math.Min(MathF.Ceiling(perPixel * PixelatedSamplesPerPoint), MaxPixelatedFactor);
        while (factor >= 2 && (long)Width * Height * factor * factor > MaxPixelatedPixels) factor--;
        if (factor < 2) return this;

        Dictionary<int, PdfImage> byFactor;
        lock (PixelatedImages)
        {
            byFactor = PixelatedImages.GetOrCreateValue(this);
            if (byFactor.TryGetValue(factor, out var done)) return done;
        }
        var result = Enlarged(factor) ?? this;
        // Each image is drawn at only a few sizes, so a handful of enlargements of it are kept; more are rare.
        lock (PixelatedImages)
        {
            if (byFactor.Count < 8) byFactor[factor] = result;
        }
        return result;
    }

    /// <summary>The image with each pixel repeated factor times across and down; null when it cannot be decoded here.</summary>
    private PdfImage? Enlarged(int factor)
    {
        try
        {
            byte[] color;
            int colors;
            string colorSpace = ColorSpace;
            if (Samples() is { } samples)
            {
                (color, colors) = samples;
            }
            else if (ColorSpace != DeviceCmyk && DecodeRgb() is var (rgb, w, h) && w == Width && h == Height)
            {
                // Fewer than 8 bits per sample (a 1-bit QR code PNG, say): read as viewers read it.
                (color, colors, colorSpace) = (rgb, 3, DeviceRgb);
            }
            else return null;
            byte[]? alpha = null;
            if (SMask != null)
            {
                if (SMask.Width != Width || SMask.Height != Height || SMask.Samples() is not { Colors: 1 } mask) return null;
                alpha = mask.Data;
            }
            int width = Width * factor, height = Height * factor;
            var image = new PdfImage
            {
                Width = width, Height = height, ColorSpace = colorSpace,
                Data = Deflate(Repeat(color, colors, Width, Height, factor)),
            };
            if (alpha != null)
                image.SMask = new PdfImage { Width = width, Height = height, ColorSpace = DeviceGray, Data = Deflate(Repeat(alpha, 1, Width, Height, factor)) };
            return image;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or FormatException or EndOfStreamException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    /// <summary>Repeats each pixel of a width x height image of the given samples per pixel as a factor x factor block.</summary>
    private static byte[] Repeat(byte[] pixels, int colors, int width, int height, int factor)
    {
        int stride = width * colors, outStride = stride * factor;
        var result = new byte[(long)outStride * height * factor];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * factor * outStride;
            var row = result.AsSpan(rowStart, outStride);
            for (int x = 0; x < width; x++)
            {
                var pixel = pixels.AsSpan((y * width + x) * colors, colors);
                for (int k = 0; k < factor; k++) pixel.CopyTo(row.Slice((x * factor + k) * colors));
            }
            for (int k = 1; k < factor; k++) row.CopyTo(result.AsSpan(rowStart + k * outStride, outStride));
        }
        return result;
    }
}

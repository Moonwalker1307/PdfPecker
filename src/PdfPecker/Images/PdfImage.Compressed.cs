using System.Runtime.CompilerServices;
using PdfPecker.Rendering;

namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    /// <summary>The JPEG quality of a downsampled JPEG when no quality is given, as <see cref="PdfCompactor"/> uses.</summary>
    private const int DefaultJpegQuality = 85;

    private static readonly ConditionalWeakTable<PdfImage, Dictionary<(int Width, int Height, int Quality), PdfImage>> CompressedImages = new();

    /// <summary>
    /// The image for -pdfpecker-image-quality and -pdfpecker-image-max-resolution when it is drawn
    /// <paramref name="drawnWidth"/> by <paramref name="drawnHeight"/> points: downsampled to
    /// <paramref name="maxDpi"/> where it is drawn denser than that, and written as a JPEG at <paramref name="quality"/>
    /// (a downsampled JPEG stays a JPEG, at 85 when no quality is given; other downsampled images stay lossless). The
    /// soft mask is downsampled with the colours and stays lossless. The image itself is returned when nothing changes,
    /// when the result would not be smaller, or when it cannot be decoded here (CMYK and ICC-based colours keep theirs).
    /// </summary>
    public PdfImage Compressed(float drawnWidth, float drawnHeight, int quality, float maxDpi)
    {
        if (Width <= 0 || Height <= 0) return this;
        int width = Width, height = Height;
        if (maxDpi > 0 && drawnWidth > 0 && drawnHeight > 0 && float.IsFinite(drawnWidth) && float.IsFinite(drawnHeight))
        {
            // Each direction is judged on its own, so an image stretched one way keeps the pixels it shows that way.
            width = (int)Math.Min(Width, Math.Max(1, Math.Ceiling(drawnWidth / 72.0 * maxDpi)));
            height = (int)Math.Min(Height, Math.Max(1, Math.Ceiling(drawnHeight / 72.0 * maxDpi)));
        }
        bool downsample = width < Width || height < Height;
        if (!downsample && quality <= 0) return this;
        if (quality <= 0 && IsDct) quality = DefaultJpegQuality;

        var key = (width, height, quality);
        Dictionary<(int, int, int), PdfImage> versions;
        lock (CompressedImages)
        {
            versions = CompressedImages.GetOrCreateValue(this);
            if (versions.TryGetValue(key, out var done)) return done;
        }
        var result = Recompressed(width, height, quality) ?? this;
        // Each image is drawn at only a few sizes, so a handful of versions of it are kept; more are rare.
        lock (CompressedImages)
        {
            if (versions.Count < 8) versions[key] = result;
        }
        return result;
    }

    /// <summary>
    /// The image at width x height pixels, as a JPEG at <paramref name="quality"/> or, with no quality, lossless; null when
    /// it cannot be decoded here, is too large to encode, or the result is not smaller than the image as it is.
    /// </summary>
    private PdfImage? Recompressed(int width, int height, int quality)
    {
        try
        {
            byte[] color;
            int colors;
            if (ColorSpace is DeviceRgb or DeviceGray && Samples() is { } samples)
            {
                (color, colors) = samples;
            }
            else if (ColorSpace is not DeviceCmyk && !ColorSpace.Contains("ICCBased", StringComparison.Ordinal)
                     && DecodeRgb() is var (rgb, w, h) && w == Width && h == Height)
            {
                // A palette, fewer than 8 bits per sample or a decode array: read as viewers read it.
                (color, colors) = (rgb, 3);
            }
            else return null;
            byte[]? alpha = null;
            if (SMask != null)
            {
                if (SMask.Width != Width || SMask.Height != Height || SMask.Samples() is not { Colors: 1 } mask) return null;
                alpha = mask.Data;
            }
            if (width != Width || height != Height)
            {
                color = Resample(color, colors, Width, Height, width, height);
                if (alpha != null) alpha = Resample(alpha, 1, Width, Height, width, height);
            }

            var image = new PdfImage { Width = width, Height = height, ColorSpace = colors == 1 ? DeviceGray : DeviceRgb };
            if (quality > 0)
            {
                if (width > JpegEncoder.MaxDimension || height > JpegEncoder.MaxDimension) return null;
                var pixels = Pack(color, colors);
                image.Data = colors == 1 ? JpegEncoder.EncodeGray(pixels, width, height, quality) : JpegEncoder.Encode(pixels, width, height, quality, 0xFFFFFF);
                image.Filter = DctFilter;
            }
            else
            {
                image.Data = DeflatePredicted(color, width, height, colors, out string parms);
                image.DecodeParms = parms;
            }
            if (alpha != null)
            {
                image.SMask = new PdfImage { Width = width, Height = height, ColorSpace = DeviceGray };
                image.SMask.Data = DeflatePredicted(alpha, width, height, 1, out string maskParms);
                image.SMask.DecodeParms = maskParms;
            }
            // As QuestPDF does, the smaller of the two is kept: a JPEG of a small flat logo can be larger than its PNG.
            long before = Data.LongLength + (SMask?.Data.LongLength ?? 0), after = image.Data.LongLength + (image.SMask?.Data.LongLength ?? 0);
            return after < before ? image : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or FormatException or EndOfStreamException
                                       or NotSupportedException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    /// <summary>Opaque 0xAABBGGRR pixels of one (grey) or three (RGB) samples per pixel, as the JPEG encoder takes them.</summary>
    private static uint[] Pack(byte[] samples, int colors)
    {
        var pixels = new uint[samples.Length / colors];
        for (int i = 0, j = 0; i < pixels.Length; i++, j += colors)
        {
            uint r = samples[j], g = colors == 1 ? r : samples[j + 1], b = colors == 1 ? r : samples[j + 2];
            pixels[i] = 0xFF000000 | b << 16 | g << 8 | r;
        }
        return pixels;
    }

    /// <summary>
    /// Shrinks width x height samples of <paramref name="colors"/> per pixel to newWidth x newHeight, each new pixel the
    /// average of the source pixels it covers.
    /// </summary>
    private static byte[] Resample(byte[] samples, int colors, int width, int height, int newWidth, int newHeight)
    {
        var result = new byte[newWidth * newHeight * colors];
        Span<long> sums = stackalloc long[3];
        for (int y = 0; y < newHeight; y++)
        {
            int y0 = (int)((long)y * height / newHeight), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / newHeight));
            for (int x = 0; x < newWidth; x++)
            {
                int x0 = (int)((long)x * width / newWidth), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * width / newWidth));
                sums.Clear();
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = (sy * width + x0) * colors;
                    for (int sx = x0; sx < x1; sx++, row += colors)
                        for (int c = 0; c < colors; c++) sums[c] += samples[row + c];
                }
                long n = (long)(x1 - x0) * (y1 - y0), half = n / 2;
                int at = (y * newWidth + x) * colors;
                for (int c = 0; c < colors; c++) result[at + c] = (byte)((sums[c] + half) / n);
            }
        }
        return result;
    }
}

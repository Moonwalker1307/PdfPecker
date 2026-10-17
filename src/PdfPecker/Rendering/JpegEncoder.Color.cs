namespace PdfPecker.Rendering;

internal static partial class JpegEncoder
{
    // JFIF's RGB to YCbCr conversion in 16-bit fixed point (0.299, 0.587 and 0.114 for Y, and so on), as libjpeg does.
    // Cb and Cr are offset by 128, and the rounding term of each is one less than a half, which libjpeg uses so that
    // the largest value stays below 256.
    private const int FixedPointBits = 16;
    private const int FixedPointHalf = 1 << (FixedPointBits - 1);
    private const int ChromaOffset = 128 << FixedPointBits;

    /// <summary>The Y, Cb and Cr samples, one plane each, padded to whole MCUs.</summary>
    private sealed class Planes(byte[] y, byte[] cb, byte[] cr)
    {
        public byte[] Y { get; } = y;
        public byte[] Cb { get; set; } = cb;
        public byte[] Cr { get; set; } = cr;
    }

    /// <summary>The colour of one source pixel over the matte, in YCbCr.</summary>
    private readonly record struct YCbCr(byte Y, byte Cb, byte Cr);

    /// <summary>
    /// Converts the pixels into full-resolution planes padded to whole MCUs by repeating the last column and the last
    /// row.
    /// </summary>
    private static Planes ToYCbCr(uint[] pixels, Frame frame, uint matte)
    {
        int stride = frame.LumaWidth, paddedHeight = frame.LumaHeight;
        var planes = new Planes(new byte[stride * paddedHeight], new byte[stride * paddedHeight], new byte[stride * paddedHeight]);
        for (int row = 0; row < frame.Height; row++)
        {
            ConvertRow(pixels, row * frame.Width, frame.Width, matte,
                planes.Y.AsSpan(row * stride, stride), planes.Cb.AsSpan(row * stride, stride), planes.Cr.AsSpan(row * stride, stride));
        }
        int lastRow = (frame.Height - 1) * stride;
        for (int row = frame.Height; row < paddedHeight; row++)
        {
            planes.Y.AsSpan(lastRow, stride).CopyTo(planes.Y.AsSpan(row * stride));
            planes.Cb.AsSpan(lastRow, stride).CopyTo(planes.Cb.AsSpan(row * stride));
            planes.Cr.AsSpan(lastRow, stride).CopyTo(planes.Cr.AsSpan(row * stride));
        }
        return planes;
    }

    /// <summary>
    /// Converts one row of <paramref name="width"/> pixels and pads the plane rows with the colour of the last one
    /// (zeros for an empty row).
    /// </summary>
    private static void ConvertRow(uint[] pixels, int source, int width, uint matte, Span<byte> y, Span<byte> cb, Span<byte> cr)
    {
        // Rendered pages are mostly runs of one colour, so a pixel equal to the one before reuses its conversion.
        uint previous = 0;
        var color = default(YCbCr);
        for (int x = 0; x < width; x++)
        {
            uint p = pixels[source + x];
            if (x == 0 || p != previous)
            {
                color = Convert(p, matte);
                previous = p;
            }
            y[x] = color.Y;
            cb[x] = color.Cb;
            cr[x] = color.Cr;
        }
        for (int x = width; x < y.Length; x++)
        {
            y[x] = color.Y;
            cb[x] = color.Cb;
            cr[x] = color.Cr;
        }
    }

    /// <summary>Converts a premultiplied 0xAABBGGRR pixel, composited onto the 0xRRGGBB matte.</summary>
    private static YCbCr Convert(uint pixel, uint matte)
    {
        int r = (int)(pixel & 255), g = (int)((pixel >> 8) & 255), b = (int)((pixel >> 16) & 255), alpha = (int)(pixel >> 24);
        if (alpha != 255)
        {
            // Premultiplied colour over the matte is c + matte * (1 - alpha), clamped because a colour that is not
            // valid premultiplied colour may exceed its alpha.
            int uncovered = 255 - alpha;
            r = Math.Min(255, r + OverMatte((int)(matte >> 16) & 255, uncovered));
            g = Math.Min(255, g + OverMatte((int)(matte >> 8) & 255, uncovered));
            b = Math.Min(255, b + OverMatte((int)matte & 255, uncovered));
        }
        return new YCbCr(
            (byte)((19595 * r + 38470 * g + 7471 * b + FixedPointHalf) >> FixedPointBits),
            (byte)((-11059 * r - 21709 * g + 32768 * b + ChromaOffset + FixedPointHalf - 1) >> FixedPointBits),
            (byte)((32768 * r - 27439 * g - 5329 * b + ChromaOffset + FixedPointHalf - 1) >> FixedPointBits));
    }

    /// <summary>The share of a matte channel that shows through where a pixel leaves <paramref name="uncovered"/>/255.</summary>
    private static int OverMatte(int matteChannel, int uncovered) => (matteChannel * uncovered + 127) / 255;

    /// <summary>Halves a plane in both directions by averaging each 2x2 square.</summary>
    private static byte[] Downsample(byte[] plane, int width, int height)
    {
        int outWidth = width / 2, outHeight = height / 2;
        var output = new byte[outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            int top = 2 * y * width, bottom = top + width, destination = y * outWidth;
            for (int x = 0; x < outWidth; x++, top += 2, bottom += 2)
            {
                // The rounding alternates between 1 and 2 across a row, as in libjpeg, so no direction is favoured.
                int bias = 1 + (x & 1);
                output[destination + x] = (byte)((plane[top] + plane[top + 1] + plane[bottom] + plane[bottom + 1] + bias) >> 2);
            }
        }
        return output;
    }
}

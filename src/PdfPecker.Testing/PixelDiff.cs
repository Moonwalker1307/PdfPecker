using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPecker.Testing;

/// <summary>The result of comparing two page images pixel by pixel.</summary>
internal sealed class PageDifference
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>The number of pixels that differ by more than the tolerance, counting area only one page covers.</summary>
    public required long DifferentPixels { get; init; }

    /// <summary>The diff image as RGB: the actual page faded, with the different pixels in magenta.</summary>
    public required byte[] Rgb { get; init; }
}

/// <summary>Compares rendered pages and writes the images that show where they differ.</summary>
internal static class PixelDiff
{
    /// <summary>
    /// Compares two straight RGBA images. Pages of different sizes are compared over the larger of the two: area that
    /// only one of them covers counts as different, so a page that grew or shrank shows where.
    /// </summary>
    public static PageDifference Compare(byte[]? expected, int expectedWidth, int expectedHeight,
        byte[]? actual, int actualWidth, int actualHeight, int tolerance)
    {
        int width = Math.Max(expectedWidth, actualWidth), height = Math.Max(expectedHeight, actualHeight);
        var rgb = new byte[checked(width * height * 3)];
        long different = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inExpected = expected is not null && x < expectedWidth && y < expectedHeight;
                bool inActual = actual is not null && x < actualWidth && y < actualHeight;
                int o = (y * width + x) * 3;
                bool differs;
                if (inExpected && inActual)
                {
                    int e = (y * expectedWidth + x) * 4, a = (y * actualWidth + x) * 4;
                    differs = ChannelDifference(expected!, e, actual!, a) > tolerance;
                }
                else differs = inExpected || inActual;

                if (differs)
                {
                    different++;
                    rgb[o] = 255; rgb[o + 1] = 0; rgb[o + 2] = 255;
                    continue;
                }
                // The unchanged page stays faintly visible in grey, so a change can be found on the page.
                int grey = 255;
                if (inActual)
                {
                    int a = (y * actualWidth + x) * 4;
                    grey = Composite(actual!, a);
                }
                byte faded = (byte)(255 - (255 - grey) / 4);
                rgb[o] = faded; rgb[o + 1] = faded; rgb[o + 2] = faded;
            }
        }
        return new PageDifference { Width = width, Height = height, DifferentPixels = different, Rgb = rgb };
    }

    /// <summary>The largest difference of the two pixels over the colour channels and alpha.</summary>
    private static int ChannelDifference(byte[] expected, int e, byte[] actual, int a)
    {
        int max = 0;
        for (int c = 0; c < 4; c++) max = Math.Max(max, Math.Abs(expected[e + c] - actual[a + c]));
        return max;
    }

    /// <summary>The pixel's luminance over white, 0 to 255.</summary>
    private static int Composite(byte[] rgba, int i)
    {
        int alpha = rgba[i + 3];
        int luminance = (rgba[i] * 77 + rgba[i + 1] * 150 + rgba[i + 2] * 29) >> 8;
        return (luminance * alpha + 255 * (255 - alpha) + 127) / 255;
    }
}

/// <summary>
/// Writes 8-bit RGB PNG files. PdfPecker encodes the page images it renders itself; this writes the diff images,
/// which are built here, without making the library's encoder public.
/// </summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(byte[] rgb, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = width * 3;
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // no filter
                z.Write(rgb, y * stride, stride);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        header[10] = header[11] = header[12] = 0; // deflate, adaptive filtering, no interlace
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        uint crc = Update(Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

using System.Buffers.Binary;
using System.IO.Compression;
using PdfPecker.Images;
using PdfPecker.Rendering;

namespace PdfPecker.Tests;

/// <summary>
/// The PNG encoder's vectorised Paeth filter, the undoing of every filter, the encoder's chunk CRCs and a decode of what
/// it writes, at row lengths that end in the scalar tail of the vector loops.
/// </summary>
public class PngEncoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Vectorised_Paeth_filter_matches_the_predictor_byte_by_byte(int bytesPerPixel)
    {
        var random = new Random(bytesPerPixel);
        for (int length = 0; length <= 160; length++)
        {
            var row = new byte[length];
            var above = new byte[length];
            random.NextBytes(row);
            random.NextBytes(above);
            var filtered = new byte[length];
            PngFilters.ApplyPaeth(row, above, bytesPerPixel, filtered);
            for (int i = 0; i < length; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int aboveLeft = i >= bytesPerPixel ? above[i - bytesPerPixel] : 0;
                Assert.Equal((byte)(row[i] - PngFilters.PaethPredictor(left, above[i], aboveLeft)), filtered[i]);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Undoing_each_filter_matches_its_definition_byte_by_byte(int bytesPerPixel)
    {
        var random = new Random(100 + bytesPerPixel);
        for (int length = 0; length <= 80; length++)
        {
            for (byte filter = 0; filter <= 5; filter++)
            {
                var row = new byte[length];
                var above = new byte[length];
                random.NextBytes(row);
                random.NextBytes(above);
                var undone = (byte[])row.Clone();
                PngFilters.Undo(filter, undone, above, bytesPerPixel);
                // The definition, byte by byte from the left, reading the bytes already undone; type 5 does not exist.
                var expected = (byte[])row.Clone();
                for (int i = 0; i < length; i++)
                {
                    int left = i >= bytesPerPixel ? expected[i - bytesPerPixel] : 0;
                    int aboveLeft = i >= bytesPerPixel ? above[i - bytesPerPixel] : 0;
                    expected[i] += filter switch
                    {
                        PngFilters.Sub => (byte)left,
                        PngFilters.Up => above[i],
                        PngFilters.Average => (byte)((left + above[i]) >> 1),
                        PngFilters.Paeth => (byte)PngFilters.PaethPredictor(left, above[i], aboveLeft),
                        _ => 0,
                    };
                }
                Assert.Equal(expected, undone);
            }
        }
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(37, 9, true)]
    [InlineData(37, 9, false)]
    [InlineData(250, 17, false)]
    public void Encoded_pixels_and_chunk_crcs_read_back(int width, int height, bool opaque)
    {
        // Noise leaves a lot of residue, so the encoder tries Paeth as well as Sub and Up.
        var random = new Random(width * 31 + height);
        var pixels = new uint[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint alpha = opaque ? 255u : (uint)random.Next(256);
            uint r = (uint)random.Next((int)alpha + 1), g = (uint)random.Next((int)alpha + 1), b = (uint)random.Next((int)alpha + 1);
            pixels[i] = alpha << 24 | b << 16 | g << 8 | r;
        }
        if (width > 4) Array.Copy(pixels, 0, pixels, width, width); // an equal row, coded as Up
        var png = PngEncoder.Encode(pixels, width, height);

        var data = new MemoryStream();
        for (int at = 8; at < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var typeAndData = png.AsSpan(at + 4, 4 + length);
            Assert.Equal(ReferenceCrc(typeAndData), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)));
            if (typeAndData[..4].SequenceEqual("IDAT"u8)) data.Write(typeAndData[4..]);
            at += 12 + length;
        }

        int bytesPerPixel = opaque ? 3 : 4, stride = width * bytesPerPixel;
        using var zlib = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var bytes = raw.ToArray();
        Assert.Equal((stride + 1) * height, bytes.Length);
        var above = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            var row = bytes.AsSpan(y * (stride + 1) + 1, stride);
            PngFilters.Undo(bytes[y * (stride + 1)], row, above, bytesPerPixel);
            for (int x = 0; x < width; x++)
            {
                uint p = pixels[y * width + x], alpha = p >> 24;
                for (int c = 0; c < 3; c++)
                {
                    uint channel = (p >> (8 * c)) & 255;
                    uint expected = opaque || alpha == 255 || alpha == 0 ? channel : Math.Min(255, (channel * 255 + alpha / 2) / alpha);
                    Assert.Equal(expected, row[x * bytesPerPixel + c]);
                }
                if (!opaque) Assert.Equal(alpha, row[x * 4 + 3]);
            }
            row.CopyTo(above);
        }
    }

    private static uint ReferenceCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}

using System.Buffers.Binary;

namespace PdfPecker.Images;

/// <summary>
/// Decodes the first frame of a GIF image (GIF87a or GIF89a) to RGB samples and an alpha channel, as browsers show a
/// GIF that cannot animate.
/// </summary>
internal static class GifDecoder
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControlLabel = 0xF9;

    private const int HeaderLength = 6;
    private const int ScreenDescriptorLength = 7;
    private const int ImageDescriptorLength = 9;
    private const int GraphicControlLength = 5; // block size, flags, 2-byte delay, transparent index
    private const int GraphicControlMinBlockSize = 4;

    private const int HasColorTable = 0x80;
    private const int Interlaced = 0x40;
    private const int ColorTableSizeMask = 0x07;
    private const int HasTransparentColor = 0x01;

    private const int MinLzwCodeSize = 1;
    private const int MaxLzwCodeSize = 11;

    public static bool IsGif(ReadOnlySpan<byte> data)
        => data.Length >= HeaderLength
        && data[..4].SequenceEqual("GIF8"u8)
        && data[4] is (byte)'7' or (byte)'9'
        && data[5] == (byte)'a';

    /// <summary>The first frame on the logical screen, or null when the data is not a readable GIF.</summary>
    public static DecodedImage? Decode(byte[] data, long maxPixels)
    {
        if (!IsGif(data)) return null;
        var cursor = new SpanCursor(data, HeaderLength);
        if (!cursor.TryRead(ScreenDescriptorLength, out var screen)) return null;
        int screenWidth = BinaryPrimitives.ReadUInt16LittleEndian(screen);
        int screenHeight = BinaryPrimitives.ReadUInt16LittleEndian(screen[2..]);
        byte screenFlags = screen[4];

        ReadOnlySpan<byte> globalPalette = default;
        if (!TryReadColorTable(ref cursor, screenFlags, ref globalPalette)) return null;

        int transparentIndex = -1;
        while (cursor.TryReadByte(out byte blockType))
        {
            switch (blockType)
            {
                case Trailer:
                    return null; // before any image
                case ExtensionIntroducer:
                    if (!cursor.TryReadByte(out byte label)) return null;
                    if (label == GraphicControlLabel) transparentIndex = ReadTransparentIndex(cursor) ?? transparentIndex;
                    SkipSubBlocks(ref cursor);
                    continue;
                case ImageSeparator:
                    return DecodeFrame(ref cursor, globalPalette, screenWidth, screenHeight, transparentIndex, maxPixels);
                default:
                    return null;
            }
        }
        return null;
    }

    private static DecodedImage? DecodeFrame(ref SpanCursor cursor, ReadOnlySpan<byte> globalPalette,
        int screenWidth, int screenHeight, int transparentIndex, long maxPixels)
    {
        if (!cursor.TryRead(ImageDescriptorLength, out var descriptor)) return null;
        int left = BinaryPrimitives.ReadUInt16LittleEndian(descriptor);
        int top = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[6..]);
        byte frameFlags = descriptor[8];

        var palette = globalPalette;
        if (!TryReadColorTable(ref cursor, frameFlags, ref palette)) return null;
        if (palette.IsEmpty || width == 0 || height == 0 || cursor.AtEnd) return null;
        bool interlaced = (frameFlags & Interlaced) != 0;

        // The canvas is the logical screen, grown to hold the frame when a writer left the screen size out.
        int canvasWidth = Math.Max(screenWidth, left + width), canvasHeight = Math.Max(screenHeight, top + height);
        long canvasPixels = (long)canvasWidth * canvasHeight;
        if (canvasPixels > maxPixels || (long)width * height > maxPixels) return null;

        cursor.TryReadByte(out byte minCodeSize);
        if (minCodeSize is < MinLzwCodeSize or > MaxLzwCodeSize) return null;
        // n codes of LZW data make at most about n * n / 2 pixels (4096 per code once the table is full): a few
        // bytes cannot fill a huge canvas, so it is not allocated for them.
        long codes = SubBlockDataLength(cursor) * 8L / (minCodeSize + 1);
        long producible = codes <= 4096 ? codes * (codes + 3) / 2 : 4096 * codes;
        if (canvasPixels > 1 << 22 && canvasPixels > 4 * producible) return null;

        var indices = new byte[width * height];
        int decoded = new GifLzwDecoder(cursor, minCodeSize).Decode(indices);
        if (decoded == 0) return null; // a frame with no pixels at all is damaged; a short one shows what it has

        var rgb = new byte[canvasWidth * canvasHeight * 3];
        var alpha = new byte[canvasWidth * canvasHeight]; // outside the frame and on the transparent index: see-through
        int colorCount = palette.Length / 3;
        for (int row = 0, i = 0; i < decoded; row++)
        {
            int y = interlaced ? InterlacedRow(row, height) : row;
            int canvasIndex = (top + y) * canvasWidth + left;
            for (int x = 0; x < width && i < decoded; x++, i++, canvasIndex++)
            {
                int index = indices[i];
                if (index == transparentIndex || index >= colorCount) continue;
                palette.Slice(index * 3, 3).CopyTo(rgb.AsSpan(canvasIndex * 3, 3));
                alpha[canvasIndex] = 255;
            }
        }
        return new DecodedImage(canvasWidth, canvasHeight, rgb, alpha);
    }

    /// <summary>
    /// Reads the color table announced by <paramref name="flags"/>, if any, into <paramref name="palette"/>. Returns
    /// false when the table runs past the end of the data.
    /// </summary>
    private static bool TryReadColorTable(ref SpanCursor cursor, byte flags, ref ReadOnlySpan<byte> palette)
    {
        if ((flags & HasColorTable) == 0) return true;
        int entries = 2 << (flags & ColorTableSizeMask);
        if (!cursor.TryRead(entries * 3, out var table)) return false;
        palette = table;
        return true;
    }

    /// <summary>
    /// The transparent color index of the graphic control extension whose block starts at the cursor: -1 when the
    /// block says there is none, null when the block is too short or cut off and should be ignored.
    /// </summary>
    private static int? ReadTransparentIndex(SpanCursor cursor)
    {
        if (!cursor.TryRead(GraphicControlLength, out var block) || block[0] < GraphicControlMinBlockSize) return null;
        return (block[1] & HasTransparentColor) != 0 ? block[4] : -1;
    }

    /// <summary>The number of data bytes in the sub-blocks starting at the cursor.</summary>
    private static long SubBlockDataLength(SpanCursor cursor)
    {
        long total = 0;
        while (cursor.TryReadByte(out byte length) && length != 0)
        {
            total += Math.Min(length, cursor.Remaining);
            cursor.Skip(length);
        }
        return total;
    }

    private static void SkipSubBlocks(ref SpanCursor cursor)
    {
        while (cursor.TryReadByte(out byte length) && length != 0) cursor.Skip(length);
    }

    /// <summary>The image row stored at position <paramref name="row"/> of an interlaced frame (passes of every 8th, 8th, 4th and 2nd row).</summary>
    private static int InterlacedRow(int row, int height)
    {
        int pass1 = (height + 7) / 8, pass2 = (height + 3) / 8, pass3 = (height + 1) / 4;
        if (row < pass1) return row * 8;
        row -= pass1;
        if (row < pass2) return row * 8 + 4;
        row -= pass2;
        if (row < pass3) return row * 4 + 2;
        return (row - pass3) * 2 + 1;
    }
}

/// <summary>Decoded pixels: 3 bytes of RGB per pixel, and an optional alpha byte per pixel.</summary>
internal readonly record struct DecodedImage(int Width, int Height, byte[] Rgb, byte[]? Alpha);

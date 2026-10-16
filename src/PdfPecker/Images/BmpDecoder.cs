using System.Buffers.Binary;
using System.Numerics;

namespace PdfPecker.Images;

/// <summary>
/// Decodes Windows and OS/2 bitmaps (BMP): 1, 2, 4 and 8-bit palettes, 16, 24 and 32-bit pixels with or without bit
/// field masks, RLE4 and RLE8 compression, and the PNG or JPEG data a bitmap can wrap. Alpha follows the browsers: a
/// 32-bit bitmap is transparent where it has an alpha mask, or alpha bytes that are not all zero.
/// </summary>
internal static class BmpDecoder
{
    private const int FileHeaderLength = 14;
    private const int DataOffsetField = 10;
    // The file header plus the fields of the smallest (OS/2 1.x core) info header.
    private const int MinLength = 26;

    // Info header sizes. OS/2 2.x headers may stop anywhere after the bit count, at 16 bytes or more.
    private const int CoreHeaderLength = 12;
    private const int MinInfoHeaderLength = 16;
    private const int InfoHeaderLength = 40;
    private const int V2HeaderLength = 52; // adds the red, green and blue masks
    private const int V3HeaderLength = 56; // adds the alpha mask
    private const int Os2V2HeaderLength = 64;

    // Field offsets inside the info header, which starts after the file header.
    private const int HeaderSizeField = 0;
    private const int WidthField = 4;
    private const int HeightField = 8;
    private const int BitCountField = 14;
    private const int CompressionField = 16;
    private const int ColorsUsedField = 32;
    private const int MasksField = 40;
    private const int CoreWidthField = 4;
    private const int CoreHeightField = 6;
    private const int CoreBitCountField = 10;

    private const int MaxPaletteEntries = 256;

    private enum Compression : uint
    {
        Rgb = 0,
        Rle8 = 1,
        Rle4 = 2,
        BitFields = 3,
        Jpeg = 4,
        Png = 5,
        AlphaBitFields = 6,
    }

    // The RLE escape codes that follow a zero count.
    private const byte EndOfLine = 0;
    private const byte EndOfBitmap = 1;
    private const byte Delta = 2;

    public static bool IsBmp(ReadOnlySpan<byte> data) => data.Length >= MinLength && data.StartsWith("BM"u8);

    /// <summary>The PNG or JPEG data that a bitmap with compression 4 or 5 holds, or null.</summary>
    public static byte[]? Wrapped(byte[] data)
    {
        if (!IsBmp(data)) return null;
        var info = data.AsSpan(FileHeaderLength);
        if (BinaryPrimitives.ReadInt32LittleEndian(info[HeaderSizeField..]) < InfoHeaderLength
            || data.Length < FileHeaderLength + InfoHeaderLength) return null;
        var compression = (Compression)BinaryPrimitives.ReadUInt32LittleEndian(info[CompressionField..]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DataOffsetField));
        if (compression is not (Compression.Jpeg or Compression.Png) || offset >= data.Length) return null;
        return data.AsSpan((int)offset).ToArray();
    }

    public static DecodedImage? Decode(byte[] data, long maxPixels)
    {
        if (!IsBmp(data)) return null;
        int dataOffset = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(DataOffsetField)), int.MaxValue);
        if (!TryReadHeader(data, out var header)) return null;
        int width = header.Width, height = header.Height, bitsPerPixel = header.BitsPerPixel;
        if (width <= 0 || height <= 0 || (long)width * height > maxPixels) return null;

        // The palette, or the masks of a version 1 header, follow the info header. A huge header size wraps this
        // offset round, which the palette read below relies on to fail as it always has.
        int afterHeader = FileHeaderLength + header.Size;
        var masks = default(BitMasks);
        if (header.Compression is Compression.BitFields or Compression.AlphaBitFields
            && !TryReadMasks(data, header, ref afterHeader, out masks)) return null;
        if (header.Compression is Compression.Rgb or Compression.BitFields or Compression.AlphaBitFields)
            masks = masks.WithDefaultColors(bitsPerPixel);

        byte[]? palette = null;
        if (bitsPerPixel <= 8)
        {
            if (bitsPerPixel is not (1 or 2 or 4 or 8)) return null;
            palette = ReadPalette(data, header, afterHeader, dataOffset);
            if (palette == null) return null;
        }
        else if (bitsPerPixel is not (16 or 24 or 32)) return null;
        if (dataOffset <= 0 || dataOffset >= data.Length) return null;

        // A few bytes cannot fill a huge bitmap (RLE makes at most 255 pixels from 2 bytes, plain rows need their bytes),
        // so it is not allocated for them.
        bool runLength = header.Compression is Compression.Rle8 or Compression.Rle4;
        long pixels = (long)width * height;
        long dataBytes = data.Length - dataOffset;
        if (pixels > 1 << 22 && pixels > dataBytes * (runLength ? 128 : 16)) return null;

        if (runLength)
        {
            int expectedBits = header.Compression == Compression.Rle8 ? 8 : 4;
            if (palette == null || bitsPerPixel != expectedBits) return null;
            var canvas = new RleCanvas(width, height, header.TopDown, palette);
            DecodeRle(data.AsSpan(dataOffset), header.Compression == Compression.Rle8, ref canvas);
            return new DecodedImage(width, height, canvas.Rgb, canvas.Alpha);
        }
        // JPEG and PNG data is read through Wrapped; the other values are not defined.
        if (header.Compression is not (Compression.Rgb or Compression.BitFields or Compression.AlphaBitFields)) return null;

        var rgb = new byte[width * height * 3];
        var alpha = DecodeRows(data, dataOffset, header, palette, masks, rgb);
        return new DecodedImage(width, height, rgb, alpha);
    }

    /// <summary>
    /// Reads the size, bit depth and compression from either a core header or a Windows or OS/2 2.x info header.
    /// Returns false for headers that are cut off or use an OS/2 compression that is not read.
    /// </summary>
    private static bool TryReadHeader(ReadOnlySpan<byte> data, out BmpHeader header)
    {
        header = default;
        var info = data[FileHeaderLength..];
        int size = BinaryPrimitives.ReadInt32LittleEndian(info[HeaderSizeField..]);
        int width, height, bitsPerPixel;
        var compression = Compression.Rgb;
        uint colorsUsed = 0;
        bool core = size == CoreHeaderLength;
        if (core)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(info[CoreWidthField..]);
            height = BinaryPrimitives.ReadInt16LittleEndian(info[CoreHeightField..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(info[CoreBitCountField..]);
        }
        else
        {
            // The sum is left unchecked on purpose: a header size near int.MaxValue wraps round and passes, as it did.
            if (size < MinInfoHeaderLength || FileHeaderLength + size > data.Length
                || data.Length < FileHeaderLength + InfoHeaderLength) return false;
            width = BinaryPrimitives.ReadInt32LittleEndian(info[WidthField..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(info[HeightField..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(info[BitCountField..]);
            if (size >= InfoHeaderLength)
            {
                compression = (Compression)BinaryPrimitives.ReadUInt32LittleEndian(info[CompressionField..]);
                colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(info[ColorsUsedField..]);
            }
            // OS/2 2.x headers number their compressions differently: there 3 is Huffman and 4 is RLE24, neither read.
            if (size == Os2V2HeaderLength && compression is Compression.BitFields or Compression.Jpeg) return false;
        }
        if (height == int.MinValue) return false; // it has no positive counterpart
        header = new BmpHeader(size, core, width, Math.Abs(height), height < 0, bitsPerPixel, compression, colorsUsed);
        return true;
    }

    /// <summary>
    /// Reads the bit field masks: in the header from version 2 on, else right after a 40-byte header, where they push
    /// the palette back. Returns false when they run past the end of the data.
    /// </summary>
    private static bool TryReadMasks(ReadOnlySpan<byte> data, in BmpHeader header, ref int afterHeader, out BitMasks masks)
    {
        masks = default;
        int maskAt = header.Size >= V2HeaderLength ? FileHeaderLength + MasksField : afterHeader;
        int maskCount = header.Compression == Compression.AlphaBitFields || header.Size >= V3HeaderLength ? 4 : 3;
        if (maskAt + maskCount * 4 > data.Length) return false;
        var fields = data[maskAt..];
        masks = new BitMasks(
            new ChannelMask(BinaryPrimitives.ReadUInt32LittleEndian(fields)),
            new ChannelMask(BinaryPrimitives.ReadUInt32LittleEndian(fields[4..])),
            new ChannelMask(BinaryPrimitives.ReadUInt32LittleEndian(fields[8..])),
            new ChannelMask(maskCount == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]) : 0));
        if (header.Size == InfoHeaderLength) afterHeader += maskCount * 4;
        return true;
    }

    /// <summary>
    /// The palette as 256 RGB triples: BGR triples for the core header, else BGRx quads, as many as the header names
    /// and fit before the pixel data. Returns null when there is no room for any.
    /// </summary>
    private static byte[]? ReadPalette(ReadOnlySpan<byte> data, in BmpHeader header, int start, int dataOffset)
    {
        int entrySize = header.IsCore ? 3 : 4;
        int count = header.ColorsUsed is > 0 and <= MaxPaletteEntries ? (int)header.ColorsUsed : 1 << header.BitsPerPixel;
        count = Math.Min(count, Math.Max(0, (Math.Min(dataOffset, data.Length) - start) / entrySize));
        if (count <= 0) return null;
        var palette = new byte[MaxPaletteEntries * 3];
        for (int i = 0; i < count; i++)
        {
            // Indexed one byte at a time so that an offset wrapped round by a huge header size fails as it always has.
            int entry = start + i * entrySize;
            palette[i * 3] = data[entry + 2];
            palette[i * 3 + 1] = data[entry + 1];
            palette[i * 3 + 2] = data[entry];
        }
        return palette;
    }

    /// <summary>
    /// Uncompressed rows, bottom-up unless the height was negative, each padded to 4 bytes. A short file shows the
    /// pixels it has and leaves the rest transparent black. Returns the alpha plane, or null when the pixels carry none.
    /// </summary>
    private static byte[]? DecodeRows(ReadOnlySpan<byte> data, int offset, in BmpHeader header, byte[]? palette,
        in BitMasks masks, byte[] rgb)
    {
        int width = header.Width, height = header.Height, bitsPerPixel = header.BitsPerPixel;
        long stride = ((long)width * bitsPerPixel + 31) / 32 * 4;
        // Plain 32-bit pixels have a fourth byte that most writers leave at zero: it is alpha only when some pixel sets it.
        bool guessAlpha = bitsPerPixel == 32 && masks.Alpha.IsEmpty
            && masks.Red.Value == 0xFF0000 && masks.Green.Value == 0xFF00 && masks.Blue.Value == 0xFF;
        var alpha = !masks.Alpha.IsEmpty || guessAlpha ? new byte[width * height] : null;
        bool anyAlpha = false;
        for (int row = 0; row < height; row++)
        {
            long start = offset + row * stride;
            if (start >= data.Length) break;
            int y = header.TopDown ? row : height - 1 - row;
            // A row reads on into the following rows' bytes, as far as the end of the data, so only the end limits it.
            var source = data[(int)start..];
            var rgbRow = rgb.AsSpan(y * width * 3, width * 3);
            var alphaRow = alpha == null ? Span<byte>.Empty : alpha.AsSpan(y * width, width);
            if (palette != null) DecodeIndexedRow(source, bitsPerPixel, palette, rgbRow, alphaRow);
            else if (bitsPerPixel == 24) DecodeBgrRow(source, rgbRow, alphaRow);
            else anyAlpha |= DecodeMaskedRow(source, bitsPerPixel / 8, masks, guessAlpha, rgbRow, alphaRow);
        }
        return guessAlpha && !anyAlpha ? null : alpha;
    }

    /// <summary>Palette indices packed from the high bits of each byte down.</summary>
    private static void DecodeIndexedRow(ReadOnlySpan<byte> source, int bitsPerPixel, byte[] palette, Span<byte> rgb, Span<byte> alpha)
    {
        int width = rgb.Length / 3;
        int available = (int)Math.Min(width, (8L * source.Length + bitsPerPixel - 1) / bitsPerPixel);
        int indexMask = (1 << bitsPerPixel) - 1;
        for (int x = 0; x < available; x++)
        {
            long bit = (long)x * bitsPerPixel;
            int index = (source[(int)(bit >> 3)] >> (8 - bitsPerPixel - (int)(bit & 7))) & indexMask;
            int color = index * 3;
            rgb[x * 3] = palette[color];
            rgb[x * 3 + 1] = palette[color + 1];
            rgb[x * 3 + 2] = palette[color + 2];
            if (!alpha.IsEmpty) alpha[x] = 255;
        }
    }

    private static void DecodeBgrRow(ReadOnlySpan<byte> source, Span<byte> rgb, Span<byte> alpha)
    {
        int width = rgb.Length / 3;
        int available = Math.Min(width, source.Length / 3);
        for (int x = 0, i = 0; x < available; x++, i += 3)
        {
            rgb[i] = source[i + 2];
            rgb[i + 1] = source[i + 1];
            rgb[i + 2] = source[i];
            if (!alpha.IsEmpty) alpha[x] = 255;
        }
    }

    /// <summary>16 or 32-bit little-endian pixels split by the bit field masks. Returns whether any alpha is not zero.</summary>
    private static bool DecodeMaskedRow(ReadOnlySpan<byte> source, int bytesPerPixel, in BitMasks masks, bool guessAlpha,
        Span<byte> rgb, Span<byte> alpha)
    {
        int width = rgb.Length / 3;
        int available = Math.Min(width, source.Length / bytesPerPixel);
        bool anyAlpha = false;
        for (int x = 0; x < available; x++)
        {
            var bytes = source[(x * bytesPerPixel)..];
            uint pixel = bytesPerPixel == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            rgb[x * 3] = masks.Red.Extract(pixel);
            rgb[x * 3 + 1] = masks.Green.Extract(pixel);
            rgb[x * 3 + 2] = masks.Blue.Extract(pixel);
            if (alpha.IsEmpty) continue;
            byte a = guessAlpha ? (byte)(pixel >> 24) : masks.Alpha.Extract(pixel);
            alpha[x] = a;
            anyAlpha |= a != 0;
        }
        return anyAlpha;
    }

    /// <summary>
    /// RLE8 or RLE4 data: pairs of a count and a value, where a zero count escapes to an end of line, the end of the
    /// bitmap, a jump, or a literal run padded to 16 bits. Damaged data ends the decoding with what it has.
    /// </summary>
    private static void DecodeRle(ReadOnlySpan<byte> data, bool rle8, ref RleCanvas canvas)
    {
        var cursor = new SpanCursor(data);
        while (!canvas.IsFull && cursor.TryRead(2, out var pair))
        {
            int count = pair[0];
            byte value = pair[1];
            if (count > 0)
            {
                for (int i = 0; i < count; i++) canvas.Put(rle8 ? value : Nibble(value, i));
                continue;
            }
            switch (value)
            {
                case EndOfLine:
                    canvas.NextLine();
                    break;
                case EndOfBitmap:
                    return;
                case Delta:
                    if (!cursor.TryRead(2, out var offset)) return;
                    canvas.Move(offset[0], offset[1]);
                    break;
                default:
                {
                    // A literal run of `value` pixels, whose bytes are padded to an even count.
                    int length = rle8 ? value : (value + 1) / 2;
                    if (!cursor.TryRead(length, out var run)) return;
                    for (int i = 0; i < value; i++) canvas.Put(rle8 ? run[i] : Nibble(run[i / 2], i));
                    cursor.Skip(length & 1);
                    break;
                }
            }
        }
    }

    /// <summary>The <paramref name="i"/>th pixel of an RLE4 byte: the high nibble first.</summary>
    private static int Nibble(byte pair, int i) => (i & 1) == 0 ? pair >> 4 : pair & 0x0F;

    private readonly record struct BmpHeader(
        int Size, bool IsCore, int Width, int Height, bool TopDown, int BitsPerPixel, Compression Compression, uint ColorsUsed);

    private readonly record struct BitMasks(ChannelMask Red, ChannelMask Green, ChannelMask Blue, ChannelMask Alpha)
    {
        /// <summary>
        /// The masks with the standard colors filled in when none were given: 5 bits each for 16-bit pixels, 8 bits
        /// each for 32-bit pixels.
        /// </summary>
        public BitMasks WithDefaultColors(int bitsPerPixel)
        {
            if (!Red.IsEmpty || !Green.IsEmpty || !Blue.IsEmpty) return this;
            return bitsPerPixel switch
            {
                16 => this with { Red = new(0x7C00), Green = new(0x03E0), Blue = new(0x001F) },
                32 => this with { Red = new(0xFF0000), Green = new(0x00FF00), Blue = new(0x0000FF) },
                _ => this,
            };
        }
    }

    /// <summary>One bit field mask, with the shift and width that scale its channel to 8 bits.</summary>
    private readonly struct ChannelMask
    {
        private readonly int shift;
        private readonly int bits;

        public ChannelMask(uint value)
        {
            Value = value;
            if (value == 0) return;
            shift = BitOperations.TrailingZeroCount(value);
            bits = BitOperations.PopCount(value >> shift);
        }

        public uint Value { get; }

        public bool IsEmpty => Value == 0;

        public byte Extract(uint pixel)
        {
            if (Value == 0) return 0;
            uint channel = (pixel & Value) >> shift;
            if (bits >= 8) return (byte)(channel >> (bits - 8));
            // Narrow channels are scaled with rounding, so that the largest value becomes 255.
            uint max = (1u << bits) - 1;
            return (byte)((channel * 255 + max / 2) / max);
        }
    }

    /// <summary>
    /// The pixels an RLE stream writes to, with its current position. Pixels the data skips with a jump or leaves out at
    /// the end of a line stay transparent, as in browsers.
    /// </summary>
    private struct RleCanvas
    {
        private readonly int width;
        private readonly int height;
        private readonly bool topDown;
        private readonly byte[] palette;
        private int x;
        private int row;

        public RleCanvas(int width, int height, bool topDown, byte[] palette)
        {
            this.width = width;
            this.height = height;
            this.topDown = topDown;
            this.palette = palette;
            Rgb = new byte[width * height * 3];
            Alpha = new byte[width * height];
        }

        public byte[] Rgb { get; }

        public byte[] Alpha { get; }

        public readonly bool IsFull => row >= height;

        /// <summary>Writes one pixel and moves right; a pixel past the right edge or the last row is dropped.</summary>
        public void Put(int index)
        {
            if (x < width && row < height)
            {
                int pixel = (topDown ? row : height - 1 - row) * width + x;
                Rgb[pixel * 3] = palette[index * 3];
                Rgb[pixel * 3 + 1] = palette[index * 3 + 1];
                Rgb[pixel * 3 + 2] = palette[index * 3 + 2];
                Alpha[pixel] = 255;
            }
            x++;
        }

        public void NextLine()
        {
            x = 0;
            row++;
        }

        public void Move(int right, int down)
        {
            x += right;
            row += down;
        }
    }
}

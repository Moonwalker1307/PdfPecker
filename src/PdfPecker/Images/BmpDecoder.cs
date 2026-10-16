using System.Buffers.Binary;

namespace PdfPecker.Images;

/// <summary>
/// Decodes Windows and OS/2 bitmaps (BMP): 1, 2, 4 and 8-bit palettes, 16, 24 and 32-bit pixels with or without bit
/// field masks, RLE4 and RLE8 compression, and the PNG or JPEG data a bitmap can wrap. Alpha follows the browsers: a
/// 32-bit bitmap is transparent where it has an alpha mask, or alpha bytes that are not all zero.
/// </summary>
internal static class BmpDecoder
{
    public static bool IsBmp(byte[] d) => d.Length >= 26 && d[0] == 'B' && d[1] == 'M';

    /// <summary>The PNG or JPEG data that a bitmap with compression 4 or 5 holds, or null.</summary>
    public static byte[]? Wrapped(byte[] d)
    {
        if (!IsBmp(d) || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(14)) < 40 || d.Length < 54) return null;
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(30));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(10));
        if (compression is not (4 or 5) || offset >= d.Length) return null;
        return d.AsSpan((int)offset).ToArray();
    }

    public static DecodedImage? Decode(byte[] d, long maxPixels)
    {
        if (!IsBmp(d)) return null;
        int dataOffset = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(10)), int.MaxValue);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(14));
        int width, height, bpp;
        uint compression = 0, colorsUsed = 0;
        uint redMask = 0, greenMask = 0, blueMask = 0, alphaMask = 0;
        bool core = headerSize == 12;
        if (core)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(18));
            height = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(20));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(24));
        }
        else
        {
            if (headerSize < 16 || 14 + headerSize > d.Length || d.Length < 14 + 40) return null;
            width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
            height = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
            bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28));
            if (headerSize >= 40)
            {
                compression = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(30));
                colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(46));
            }
            // OS/2 2.x headers (64 bytes) number their compressions differently; Huffman and RLE24 are not read.
            if (headerSize == 64 && compression is 3 or 4) return null;
        }
        bool topDown = height < 0;
        if (height == int.MinValue) return null;
        height = Math.Abs(height);
        if (width <= 0 || height <= 0 || (long)width * height > maxPixels) return null;

        // Bit field masks: in the header from version 2 on, else right after a 40-byte header.
        int afterHeader = 14 + headerSize;
        if (compression is 3 or 6)
        {
            int maskAt = headerSize >= 52 ? 54 : afterHeader;
            int masks = compression == 6 || headerSize >= 56 ? 4 : 3;
            if (maskAt + masks * 4 > d.Length) return null;
            redMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(maskAt));
            greenMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(maskAt + 4));
            blueMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(maskAt + 8));
            if (masks == 4) alphaMask = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(maskAt + 12));
            if (headerSize == 40) afterHeader += compression == 6 ? 16 : 12;
        }
        if (compression is 0 or 3 or 6 && bpp == 16 && redMask == 0 && greenMask == 0 && blueMask == 0)
            (redMask, greenMask, blueMask) = (0x7C00, 0x03E0, 0x001F);
        if (compression is 0 or 3 or 6 && bpp == 32 && redMask == 0 && greenMask == 0 && blueMask == 0)
            (redMask, greenMask, blueMask) = (0xFF0000, 0x00FF00, 0x0000FF);

        // The palette: BGR triples for the core header, else BGRx quads.
        byte[]? palette = null; // RGB triples
        if (bpp <= 8)
        {
            if (bpp is not (1 or 2 or 4 or 8)) return null;
            int entrySize = core ? 3 : 4;
            int count = colorsUsed > 0 && colorsUsed <= 256 ? (int)colorsUsed : 1 << bpp;
            count = Math.Min(count, Math.Max(0, (Math.Min(dataOffset, d.Length) - afterHeader) / entrySize));
            if (count <= 0) return null;
            palette = new byte[256 * 3];
            for (int i = 0; i < count; i++)
            {
                int p = afterHeader + i * entrySize;
                palette[i * 3] = d[p + 2];
                palette[i * 3 + 1] = d[p + 1];
                palette[i * 3 + 2] = d[p];
            }
        }
        else if (bpp is not (16 or 24 or 32)) return null;
        if (dataOffset <= 0 || dataOffset >= d.Length) return null;
        // A few bytes cannot fill a huge bitmap (RLE makes at most 255 pixels from 2 bytes, plain rows need their bytes),
        // so it is not allocated for them.
        long dataBytes = d.Length - dataOffset;
        long pixels = (long)width * height;
        if (pixels > 1 << 22 && pixels > dataBytes * (compression is 1 or 2 ? 128 : 16)) return null;

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        bool hasAlpha;
        if (compression is 1 or 2)
        {
            if (palette == null || (compression == 1 ? bpp != 8 : bpp != 4)) return null;
            DecodeRle(d, dataOffset, width, height, topDown, compression == 1, palette, rgb, alpha);
            hasAlpha = true;
        }
        else if (compression is 0 or 3 or 6)
        {
            hasAlpha = DecodeRows(d, dataOffset, width, height, topDown, bpp, palette, redMask, greenMask, blueMask, alphaMask, rgb, alpha);
        }
        else return null;
        return new DecodedImage(width, height, rgb, hasAlpha ? alpha : null);
    }

    /// <summary>Uncompressed rows, padded to 4 bytes. Returns whether the pixels carry alpha.</summary>
    private static bool DecodeRows(byte[] d, int offset, int width, int height, bool topDown, int bpp, byte[]? palette,
        uint redMask, uint greenMask, uint blueMask, uint alphaMask, byte[] rgb, byte[] alpha)
    {
        long stride = ((long)width * bpp + 31) / 32 * 4;
        var (rs, rb) = MaskShift(redMask);
        var (gs, gb) = MaskShift(greenMask);
        var (bs, bb) = MaskShift(blueMask);
        var (aShift, aBits) = MaskShift(alphaMask);
        // Plain 32-bit pixels have a fourth byte that most writers leave at zero: it is alpha only when some pixel sets it.
        bool guessAlpha = bpp == 32 && alphaMask == 0 && redMask == 0xFF0000 && greenMask == 0xFF00 && blueMask == 0xFF;
        bool anyAlpha = false;
        for (int row = 0; row < height; row++)
        {
            long start = offset + (long)row * stride;
            if (start >= d.Length) break; // a short file shows the rows it has; the rest stays transparent black
            int y = topDown ? row : height - 1 - row;
            int o = y * width;
            for (int x = 0; x < width; x++, o++)
            {
                byte r, g, b, a = 255;
                if (palette != null)
                {
                    long bit = (long)x * bpp;
                    long at = start + bit / 8;
                    if (at >= d.Length) break;
                    int index = (d[at] >> (8 - bpp - (int)(bit % 8))) & ((1 << bpp) - 1);
                    r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                }
                else if (bpp == 24)
                {
                    long at = start + x * 3L;
                    if (at + 2 >= d.Length) break;
                    b = d[at]; g = d[at + 1]; r = d[at + 2];
                }
                else
                {
                    long at = start + x * (bpp / 8L);
                    if (at + bpp / 8 - 1 >= d.Length) break;
                    uint v = bpp == 16 ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at));
                    r = Channel(v, redMask, rs, rb);
                    g = Channel(v, greenMask, gs, gb);
                    b = Channel(v, blueMask, bs, bb);
                    if (alphaMask != 0) a = Channel(v, alphaMask, aShift, aBits);
                    else if (guessAlpha) a = (byte)(v >> 24);
                }
                rgb[o * 3] = r; rgb[o * 3 + 1] = g; rgb[o * 3 + 2] = b;
                alpha[o] = a;
                if (a != 0) anyAlpha = true;
            }
        }
        if (guessAlpha && !anyAlpha)
        {
            Array.Fill(alpha, (byte)255);
            return false;
        }
        return alphaMask != 0 || guessAlpha;
    }

    private static (int Shift, int Bits) MaskShift(uint mask)
    {
        if (mask == 0) return (0, 0);
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        return (shift, System.Numerics.BitOperations.PopCount(mask >> shift));
    }

    /// <summary>A masked channel scaled to 8 bits.</summary>
    private static byte Channel(uint v, uint mask, int shift, int bits)
    {
        if (mask == 0) return 0;
        uint c = (v & mask) >> shift;
        if (bits >= 8) return (byte)(c >> (bits - 8));
        uint max = (1u << bits) - 1;
        return (byte)((c * 255 + max / 2) / max);
    }

    /// <summary>
    /// RLE8 or RLE4 data. Pixels the data skips with a delta or leaves out at the end of a line stay transparent, as in
    /// browsers.
    /// </summary>
    private static void DecodeRle(byte[] d, int pos, int width, int height, bool topDown, bool rle8, byte[] palette, byte[] rgb, byte[] alpha)
    {
        int x = 0, row = 0;
        void Put(int index)
        {
            if (x < width && row < height)
            {
                int y = topDown ? row : height - 1 - row;
                int o = y * width + x;
                rgb[o * 3] = palette[index * 3]; rgb[o * 3 + 1] = palette[index * 3 + 1]; rgb[o * 3 + 2] = palette[index * 3 + 2];
                alpha[o] = 255;
            }
            x++;
        }
        while (pos + 1 < d.Length && row < height)
        {
            int count = d[pos], value = d[pos + 1];
            pos += 2;
            if (count > 0)
            {
                for (int i = 0; i < count; i++) Put(rle8 ? value : (i & 1) == 0 ? value >> 4 : value & 15);
                continue;
            }
            switch (value)
            {
                case 0: x = 0; row++; break; // end of line
                case 1: return; // end of bitmap
                case 2: // delta
                    if (pos + 1 >= d.Length) return;
                    x += d[pos];
                    row += d[pos + 1];
                    pos += 2;
                    break;
                default: // absolute run of `value` pixels, padded to a 16-bit boundary
                {
                    int bytes = rle8 ? value : (value + 1) / 2;
                    if (pos + bytes > d.Length) return;
                    for (int i = 0; i < value; i++) Put(rle8 ? d[pos + i] : (i & 1) == 0 ? d[pos + i / 2] >> 4 : d[pos + i / 2] & 15);
                    pos += (bytes + 1) & ~1;
                    break;
                }
            }
        }
    }
}

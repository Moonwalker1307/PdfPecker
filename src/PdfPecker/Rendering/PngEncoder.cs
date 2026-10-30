using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using PdfPecker.Images;

namespace PdfPecker.Rendering;

/// <summary>Writes 8-bit RGB or RGBA PNG files (adaptive row filters, zlib via the BCL).</summary>
internal static class PngEncoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // The IHDR chunk: width and height, then one byte each for the bit depth, the colour type, and the compression,
    // filter and interlace methods, which are always 0 (deflate, adaptive filters, no interlacing).
    private const int HeaderLength = 13;
    private const int BitDepthOffset = 8;
    private const int ColorTypeOffset = 9;
    private const byte BitDepth = 8;

    private enum ColorType : byte
    {
        Rgb = 2,
        Rgba = 6,
    }

    // Each filtered row starts with its filter type. None and Average are never chosen (see RowFilter).
    private enum FilterType : byte
    {
        Sub = 1,
        Up = 2,
        Paeth = 4,
    }

    private const uint OpaqueAlpha = 0xFF000000;
    private const int ChunkLengthSize = 4;
    private const int ChunkTypeSize = 4;
    private const uint CrcPolynomial = 0xEDB88320; // the reflected CRC-32 polynomial of ISO 3309
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Converts premultiplied 0xAABBGGRR pixels to straight R, G, B, A bytes.</summary>
    public static byte[] ToStraightRgba(uint[] px)
    {
        // 0xAABBGGRR is R, G, B, A in memory (little-endian): opaque and fully transparent pixels are already right.
        var bytes = new byte[px.Length * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        UnpremultiplyTranslucent(px, bytes);
        return bytes;
    }

    /// <summary>Rewrites the translucent pixels of <paramref name="bytes"/>, a copy of <paramref name="pixels"/>, with straight colour.</summary>
    private static void UnpremultiplyTranslucent(ReadOnlySpan<uint> pixels, Span<byte> bytes)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint alpha = pixels[i] >> 24;
            if (alpha != 255 && alpha != 0) Unpremultiply(pixels[i], bytes.Slice(i * 4));
        }
    }

    private static void Unpremultiply(uint color, Span<byte> rgba)
    {
        uint alpha = color >> 24;
        rgba[0] = UnpremultiplyChannel(color & 255, alpha);
        rgba[1] = UnpremultiplyChannel((color >> 8) & 255, alpha);
        rgba[2] = UnpremultiplyChannel((color >> 16) & 255, alpha);
        rgba[3] = (byte)alpha;
    }

    // A channel above its alpha is not valid premultiplied colour; it is clamped rather than wrapped.
    private static byte UnpremultiplyChannel(uint channel, uint alpha) => (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels. Fully opaque images are written as RGB.</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, CompressionLevel level = CompressionLevel.Fastest)
    {
        bool opaque = pixels.AsSpan().IndexOfAnyExceptInRange(OpaqueAlpha, uint.MaxValue) < 0;
        var output = new MemoryStream(width * height / 2 + 1024);
        output.Write(Signature);
        WriteHeader(output, width, height, opaque ? ColorType.Rgb : ColorType.Rgba);

        var imageData = new MemoryStream(width * height / 2 + 1024);
        using (var zlib = new ZLibStream(imageData, level, leaveOpen: true))
            WriteRows(zlib, pixels, width, height, opaque);
        WriteChunk(output, "IDAT"u8, imageData.GetBuffer().AsSpan(0, (int)imageData.Length));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteHeader(Stream output, int width, int height, ColorType colorType)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[BitDepthOffset] = BitDepth;
        header[ColorTypeOffset] = (byte)colorType;
        WriteChunk(output, "IHDR"u8, header);
    }

    /// <summary>Converts each row to RGB or straight RGBA bytes, filters it and compresses it.</summary>
    private static void WriteRows(Stream zlib, uint[] pixels, int width, int height, bool opaque)
    {
        int bytesPerPixel = opaque ? 3 : 4;
        var filter = new RowFilter(width * bytesPerPixel, bytesPerPixel);
        for (int y = 0; y < height; y++)
        {
            var row = pixels.AsSpan(y * width, width);
            if (opaque) ToRgb(row, filter.Current);
            else ToStraightRgba(row, filter.Current);
            zlib.Write(filter.FilterCurrent(firstRow: y == 0));
            filter.NextRow();
        }
    }

    private static void ToRgb(ReadOnlySpan<uint> row, Span<byte> rgb)
    {
        for (int x = 0, d = 0; x < row.Length; x++, d += 3)
        {
            uint p = row[x];
            rgb[d] = (byte)p;
            rgb[d + 1] = (byte)(p >> 8);
            rgb[d + 2] = (byte)(p >> 16);
        }
    }

    private static void ToStraightRgba(ReadOnlySpan<uint> row, Span<byte> rgba)
    {
        MemoryMarshal.AsBytes(row).CopyTo(rgba);
        UnpremultiplyTranslucent(row, rgba);
    }

    /// <summary>
    /// The current and previous rows and a buffer for each filter that is tried. The filter is picked by the usual
    /// minimum-sum-of-absolute-differences heuristic over Sub, Up and Paeth (None and Average rarely win).
    /// </summary>
    private sealed class RowFilter
    {
        private readonly int bytesPerPixel;
        private byte[] previous;
        private byte[] current;
        private readonly byte[] sub;
        private readonly byte[] up;
        private readonly byte[] paeth;

        public RowFilter(int stride, int bytesPerPixel)
        {
            this.bytesPerPixel = bytesPerPixel;
            previous = new byte[stride];
            current = new byte[stride];
            sub = new byte[stride + 1];
            up = new byte[stride + 1];
            paeth = new byte[stride + 1];
        }

        /// <summary>The unfiltered bytes of the row being encoded.</summary>
        public byte[] Current => current;

        public void NextRow() => (previous, current) = (current, previous);

        /// <summary>The current row with its best filter applied, filter type first.</summary>
        public byte[] FilterCurrent(bool firstRow)
        {
            // A row equal to the previous one is Up-filtered to zeros without trying the others.
            if (!firstRow && current.AsSpan().SequenceEqual(previous))
            {
                up[0] = (byte)FilterType.Up;
                up.AsSpan(1).Clear();
                return up;
            }
            long subSum = ApplySub();
            if (firstRow || subSum == 0) return sub;
            long upSum = ApplyUp();
            byte[] best = upSum < subSum ? up : sub;
            long bestSum = Math.Min(upSum, subSum);
            // Paeth only when the cheap filters leave a lot of residue (photographic content).
            if (bestSum > current.Length * 4 && ApplyPaeth() < bestSum) best = paeth;
            return best;
        }

        /// <summary>Sub: each byte minus the same channel of the pixel to its left. Returns the residue.</summary>
        private long ApplySub()
        {
            int n = current.Length;
            sub[0] = (byte)FilterType.Sub;
            current.AsSpan(0, bytesPerPixel).CopyTo(sub.AsSpan(1));
            Subtract(current.AsSpan(bytesPerPixel), current.AsSpan(0, n - bytesPerPixel), sub.AsSpan(1 + bytesPerPixel));
            return SumAbs(sub.AsSpan(1));
        }

        /// <summary>Up: each byte minus the byte above it. Returns the residue.</summary>
        private long ApplyUp()
        {
            up[0] = (byte)FilterType.Up;
            Subtract(current, previous, up.AsSpan(1));
            return SumAbs(up.AsSpan(1));
        }

        /// <summary>Paeth: each byte minus the nearest of left, above and above-left. Returns the residue.</summary>
        private long ApplyPaeth()
        {
            paeth[0] = (byte)FilterType.Paeth;
            var output = paeth.AsSpan(1);
            long sum = 0;
            // The first pixel has no left or above-left neighbour, so its prediction is the byte above.
            for (int i = 0; i < bytesPerPixel; i++)
            {
                byte v = (byte)(current[i] - previous[i]);
                output[i] = v;
                sum += Abs(v);
            }
            for (int i = bytesPerPixel; i < current.Length; i++)
            {
                byte v = (byte)(current[i] - PngFilters.PaethPredictor(current[i - bytesPerPixel], previous[i], previous[i - bytesPerPixel]));
                output[i] = v;
                sum += Abs(v);
            }
            return sum;
        }
    }

    private static void Subtract(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> difference)
    {
        int i = 0, n = difference.Length;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                (new Vector<byte>(a.Slice(i)) - new Vector<byte>(b.Slice(i))).CopyTo(difference.Slice(i));
        }
        for (; i < n; i++) difference[i] = (byte)(a[i] - b[i]);
    }

    /// <summary>Sum of the filtered bytes read as signed values' magnitudes.</summary>
    private static long SumAbs(ReadOnlySpan<byte> filtered)
    {
        // A ushort lane holds the sum of up to 255 iterations of two magnitudes of at most 128 each.
        const int IterationsPerFlush = 255;
        long sum = 0;
        int i = 0, n = filtered.Length;
        if (Vector.IsHardwareAccelerated)
        {
            var lanes = Vector<ushort>.Zero;
            int iterations = 0;
            for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
            {
                var v = new Vector<byte>(filtered.Slice(i));
                var magnitude = Vector.Min(v, Vector<byte>.Zero - v); // |signed byte|, 0..128
                Vector.Widen(magnitude, out var low, out var high);
                lanes += low + high;
                if (++iterations == IterationsPerFlush)
                {
                    sum += Sum(lanes);
                    lanes = Vector<ushort>.Zero;
                    iterations = 0;
                }
            }
            sum += Sum(lanes);
        }
        for (; i < n; i++) sum += Abs(filtered[i]);
        return sum;
    }

    private static long Sum(Vector<ushort> lanes)
    {
        Vector.Widen(lanes, out var low, out var high);
        return Vector.Sum(low + high);
    }

    /// <summary>The magnitude of a filtered byte read as a signed value.</summary>
    private static int Abs(byte v) => v < 128 ? v : 256 - v;

    /// <summary>Writes a chunk: big-endian length, type, data and the CRC of type and data.</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[ChunkLengthSize + ChunkTypeSize];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        type.CopyTo(header[ChunkLengthSize..]);
        output.Write(header);
        output.Write(data);
        uint crc = UpdateCrc(UpdateCrc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        output.Write(trailer);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        var table = CrcTable;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? CrcPolynomial ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

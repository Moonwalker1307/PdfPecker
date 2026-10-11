using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PdfPecker.Rendering;

/// <summary>Writes 8-bit RGB or RGBA PNG files (adaptive row filters, zlib via the BCL).</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Converts premultiplied 0xAABBGGRR pixels to straight R, G, B, A bytes.</summary>
    public static byte[] ToStraightRgba(uint[] px)
    {
        // 0xAABBGGRR is R, G, B, A in memory (little-endian): opaque pixels are already right.
        var bytes = new byte[px.Length * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        for (int i = 0; i < px.Length; i++)
        {
            uint a = px[i] >> 24;
            if (a != 255 && a != 0) Unpremultiply(px[i], bytes.AsSpan(i * 4));
        }
        return bytes;
    }

    private static void Unpremultiply(uint c, Span<byte> o)
    {
        uint a = c >> 24;
        o[0] = (byte)Math.Min(255, ((c & 255) * 255 + a / 2) / a);
        o[1] = (byte)Math.Min(255, (((c >> 8) & 255) * 255 + a / 2) / a);
        o[2] = (byte)Math.Min(255, (((c >> 16) & 255) * 255 + a / 2) / a);
        o[3] = (byte)a;
    }

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels. Fully opaque images are written as RGB.</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, CompressionLevel level = CompressionLevel.Fastest)
    {
        bool opaque = true;
        foreach (uint p in pixels)
            if (p < 0xFF000000) { opaque = false; break; }
        int bpp = opaque ? 3 : 4;
        int stride = width * bpp;

        var output = new MemoryStream(width * height / 2 + 1024);
        output.Write(stackalloc byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = (byte)(opaque ? 2 : 6); // color type: RGB or RGBA
        ihdr[10] = ihdr[11] = ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        var idat = new MemoryStream(width * height / 2 + 1024);
        using (var z = new ZLibStream(idat, level, leaveOpen: true))
        {
            var prev = new byte[stride];
            var cur = new byte[stride];
            var filtered = new byte[5][];
            for (int f = 0; f < 5; f++) filtered[f] = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * width, width);
                if (opaque)
                {
                    for (int x = 0, d = 0; x < row.Length; x++, d += 3)
                    {
                        uint p = row[x];
                        cur[d] = (byte)p;
                        cur[d + 1] = (byte)(p >> 8);
                        cur[d + 2] = (byte)(p >> 16);
                    }
                }
                else
                {
                    MemoryMarshal.AsBytes(row).CopyTo(cur);
                    for (int x = 0; x < row.Length; x++)
                    {
                        uint a = row[x] >> 24;
                        if (a != 255 && a != 0) Unpremultiply(row[x], cur.AsSpan(x * 4));
                    }
                }
                var best = FilterRow(cur, prev, bpp, y == 0, filtered);
                z.Write(best);
                (prev, cur) = (cur, prev);
            }
        }
        WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>
    /// Picks a row filter by the usual minimum-sum-of-absolute-differences heuristic over Sub, Up and Paeth (None and
    /// Average rarely win). A row equal to the previous one is Up-filtered to zeros without trying the others.
    /// </summary>
    private static byte[] FilterRow(byte[] cur, byte[] prev, int bpp, bool firstRow, byte[][] outBufs)
    {
        int n = cur.Length;
        if (!firstRow && cur.AsSpan().SequenceEqual(prev))
        {
            var z = outBufs[2];
            z[0] = 2;
            z.AsSpan(1, n).Clear();
            return z;
        }
        // Sub: cur[i] - cur[i - bpp].
        var sub = outBufs[1];
        sub[0] = 1;
        cur.AsSpan(0, bpp).CopyTo(sub.AsSpan(1));
        Subtract(cur.AsSpan(bpp), cur.AsSpan(0, n - bpp), sub.AsSpan(1 + bpp));
        if (firstRow) return sub;
        long subSum = SumAbs(sub.AsSpan(1));
        if (subSum == 0) return sub;
        // Up: cur[i] - prev[i].
        var up = outBufs[2];
        up[0] = 2;
        Subtract(cur, prev, up.AsSpan(1));
        long upSum = SumAbs(up.AsSpan(1));
        byte[] best = upSum < subSum ? up : sub;
        long bestSum = Math.Min(upSum, subSum);
        // Paeth only when the cheap filters leave a lot of residue (photographic content).
        if (bestSum > n * 4)
        {
            var o = outBufs[4];
            o[0] = 4;
            long sum = 0;
            for (int i = 0; i < bpp; i++)
            {
                byte v = (byte)(cur[i] - prev[i]);
                o[i + 1] = v;
                sum += Abs(v);
            }
            for (int i = bpp; i < n; i++)
            {
                byte v = (byte)(cur[i] - Paeth(cur[i - bpp], prev[i], prev[i - bpp]));
                o[i + 1] = v;
                sum += Abs(v);
            }
            if (sum < bestSum) best = o;
        }
        return best;
    }

    private static void Subtract(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0, n = dst.Length;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            for (; i + vs <= n; i += vs)
                (new Vector<byte>(a.Slice(i)) - new Vector<byte>(b.Slice(i))).CopyTo(dst.Slice(i));
        }
        for (; i < n; i++) dst[i] = (byte)(a[i] - b[i]);
    }

    /// <summary>Sum of the filtered bytes read as signed values' magnitudes.</summary>
    private static long SumAbs(ReadOnlySpan<byte> d)
    {
        long sum = 0;
        int i = 0, n = d.Length;
        if (Vector.IsHardwareAccelerated)
        {
            int vs = Vector<byte>.Count;
            var acc = Vector<ushort>.Zero;
            int inAcc = 0;
            for (; i + vs <= n; i += vs)
            {
                var v = new Vector<byte>(d.Slice(i));
                var mag = Vector.Min(v, Vector<byte>.Zero - v); // |signed byte|, 0..128
                Vector.Widen(mag, out var lo, out var hi);
                acc += lo + hi;
                if (++inAcc == 255)
                {
                    sum += SumUShorts(acc);
                    acc = Vector<ushort>.Zero;
                    inAcc = 0;
                }
            }
            sum += SumUShorts(acc);
        }
        for (; i < n; i++) sum += Abs(d[i]);
        return sum;
    }

    private static long SumUShorts(Vector<ushort> v)
    {
        Vector.Widen(v, out var lo, out var hi);
        return Vector.Sum(lo + hi);
    }

    private static int Abs(byte v) => v < 128 ? v : 256 - v;

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        for (int i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
        s.Write(header);
        s.Write(data);
        uint crc = Crc(0xFFFFFFFF, header[4..]);
        crc = Crc(crc, data) ^ 0xFFFFFFFF;
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        var t = CrcTable;
        foreach (byte b in data) crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}

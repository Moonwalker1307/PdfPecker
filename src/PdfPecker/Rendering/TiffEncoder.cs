using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PdfPecker.Rendering;

/// <summary>
/// Writes baseline TIFF 6.0 files of one or more pages: RGB (with alpha when the page has transparency), 8-bit grey or
/// bilevel, uncompressed or compressed with LZW, Deflate, PackBits or CCITT Group 4.
/// </summary>
internal static class TiffEncoder
{
    /// <summary>One page, compressed and ready to be placed in a file.</summary>
    internal sealed class Page
    {
        public int Width, Height, SamplesPerPixel, BitsPerSample, Photometric, Compression, RowsPerStrip;
        public bool Predictor, Alpha;
        public double DpiX, DpiY;
        public byte[][] Strips = Array.Empty<byte[]>();
    }

    // Uncompressed strips of about this size keep LZW and Deflate effective while a reader only needs one strip at a time.
    private const int StripBytes = 64 * 1024;

    /// <summary>Converts and compresses a page. <paramref name="matte"/> (0xRRGGBB) is what grey and bilevel pages are flattened onto.</summary>
    public static Page EncodePage(uint[] premultiplied, int width, int height, double dpiX, double dpiY,
        TiffCompression compression, TiffColorMode mode, uint matte, int threshold)
    {
        if (compression == TiffCompression.CcittGroup4) mode = TiffColorMode.BlackAndWhite;
        var page = new Page { Width = width, Height = height, DpiX = dpiX, DpiY = dpiY, Compression = CompressionTag(compression) };
        if (mode == TiffColorMode.BlackAndWhite)
        {
            // Fax convention: 0 is white, so the bits read the same whether or not a reader honours the photometric tag
            // for Group 4 data.
            page.SamplesPerPixel = 1;
            page.BitsPerSample = 1;
            page.Photometric = 0;
            var black = new byte[width * height];
            for (int i = 0; i < black.Length; i++)
                black[i] = (byte)(Luminance(premultiplied[i], matte) < threshold ? 1 : 0);
            if (compression == TiffCompression.CcittGroup4)
            {
                // One strip: each Group 4 strip restarts from a white reference row, which only costs bits.
                page.RowsPerStrip = height;
                page.Strips = new[] { CcittFaxEncoder.EncodeGroup4(black, width, height) };
                return page;
            }
            int rowBytes = (width + 7) / 8;
            var packed = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
                for (int x = 0, o = y * width; x < width; x++)
                    if (black[o + x] != 0) packed[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            Split(page, packed, rowBytes, compression);
            return page;
        }

        page.BitsPerSample = 8;
        page.Predictor = compression is TiffCompression.Lzw or TiffCompression.Deflate;
        byte[] samples;
        if (mode == TiffColorMode.Grayscale)
        {
            page.SamplesPerPixel = 1;
            page.Photometric = 1;
            samples = new byte[width * height];
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)Luminance(premultiplied[i], matte);
        }
        else
        {
            bool opaque = true;
            foreach (uint p in premultiplied)
                if (p < 0xFF000000) { opaque = false; break; }
            page.Photometric = 2;
            page.Alpha = !opaque;
            page.SamplesPerPixel = opaque ? 3 : 4;
            if (opaque)
            {
                samples = new byte[width * height * 3];
                for (int i = 0, o = 0; i < premultiplied.Length; i++, o += 3)
                {
                    uint p = premultiplied[i];
                    samples[o] = (byte)p; samples[o + 1] = (byte)(p >> 8); samples[o + 2] = (byte)(p >> 16);
                }
            }
            else samples = PngEncoder.ToStraightRgba(premultiplied);
        }
        int stride = width * page.SamplesPerPixel;
        if (page.Predictor)
        {
            // Horizontal differencing, right to left so each sample is taken from its still unchanged left neighbour.
            int spp = page.SamplesPerPixel;
            for (int y = 0; y < height; y++)
                for (int i = y * stride + stride - 1, start = y * stride + spp; i >= start; i--)
                    samples[i] -= samples[i - spp];
        }
        Split(page, samples, stride, compression);
        return page;
    }

    private static int CompressionTag(TiffCompression c) => c switch
    {
        TiffCompression.None => 1,
        TiffCompression.CcittGroup4 => 4,
        TiffCompression.Lzw => 5,
        TiffCompression.Deflate => 8,
        TiffCompression.PackBits => 32773,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown TIFF compression."),
    };

    /// <summary>Rec. 601 luma of a pixel flattened onto the matte, 0 to 255.</summary>
    private static int Luminance(uint p, uint matte)
    {
        uint inv = 255 - (p >> 24);
        int r = (int)((p & 255) + (((matte >> 16) & 255) * inv + 127) / 255);
        int g = (int)(((p >> 8) & 255) + (((matte >> 8) & 255) * inv + 127) / 255);
        int b = (int)(((p >> 16) & 255) + ((matte & 255) * inv + 127) / 255);
        return Math.Min(255, (r * 299 + g * 587 + b * 114 + 500) / 1000);
    }

    private static void Split(Page page, byte[] data, int rowBytes, TiffCompression compression)
    {
        int rows = Math.Max(1, StripBytes / Math.Max(1, rowBytes));
        rows = Math.Min(rows, page.Height);
        page.RowsPerStrip = rows;
        int count = (page.Height + rows - 1) / rows;
        page.Strips = new byte[count][];
        for (int s = 0; s < count; s++)
        {
            int start = s * rows * rowBytes;
            var strip = data.AsSpan(start, Math.Min(rows * rowBytes, data.Length - start));
            page.Strips[s] = compression switch
            {
                TiffCompression.Lzw => Lzw(strip),
                TiffCompression.Deflate => Deflate(strip),
                TiffCompression.PackBits => PackBits(strip, rowBytes),
                _ => strip.ToArray(),
            };
        }
    }

    /// <summary>Writes the pages as one file, in order.</summary>
    /// <exception cref="NotSupportedException">The file would pass 4 GB, the limit of classic TIFF.</exception>
    public static byte[] Write(IReadOnlyList<Page> pages)
    {
        if (pages.Count == 0) throw new ArgumentException("A TIFF file needs at least one page.", nameof(pages));
        var software = Encoding.ASCII.GetBytes("PdfPecker\0");
        // Layout per page: strip data, then the extra values that do not fit in a tag (padded to even offsets), then
        // the directory. The header points at the first directory and each directory at the next.
        long size = 8;
        var ifdAt = new long[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            foreach (var s in p.Strips) size += s.Length;
            size += size & 1;
            size += ExtraBytes(p, software.Length);
            ifdAt[i] = size;
            size += 2 + TagCount(p, pages.Count > 1) * 12 + 4;
        }
        if (size > uint.MaxValue)
            throw new NotSupportedException("The TIFF file would be larger than 4 GB, the limit of the format. Use a lower Dpi, compression or fewer pages.");

        var o = new byte[size];
        o[0] = (byte)'I'; o[1] = (byte)'I'; o[2] = 42;
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), (uint)ifdAt[0]);
        long at = 8;
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            var offsets = new long[p.Strips.Length];
            for (int s = 0; s < p.Strips.Length; s++)
            {
                offsets[s] = at;
                p.Strips[s].CopyTo(o, at);
                at += p.Strips[s].Length;
            }
            at += at & 1;

            var tags = new List<(int Tag, int Type, long Count, long Value)>();
            void Short(int tag, int value) => tags.Add((tag, 3, 1, value));
            void Long(int tag, long value) => tags.Add((tag, 4, 1, value));
            // Values longer than four bytes go in the space before the directory, and the tag holds their offset.
            long Extra(ReadOnlySpan<byte> bytes)
            {
                long where = at;
                bytes.CopyTo(o.AsSpan((int)at));
                at += bytes.Length + (bytes.Length & 1);
                return where;
            }
            long Rational(double v)
            {
                Span<byte> b = stackalloc byte[8];
                // Hundredths keep fractional resolutions from a fitted Width or Height.
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Round(v * 100));
                BinaryPrimitives.WriteUInt32LittleEndian(b[4..], 100);
                return Extra(b);
            }
            long Longs(long[] values)
            {
                if (values.Length == 1) return values[0];
                var b = new byte[values.Length * 4];
                for (int k = 0; k < values.Length; k++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(k * 4), (uint)values[k]);
                return Extra(b);
            }

            if (pages.Count > 1) Long(254, 2); // NewSubfileType: one page of a multi-page document
            Long(256, p.Width);
            Long(257, p.Height);
            if (p.SamplesPerPixel == 1) Short(258, p.BitsPerSample);
            else
            {
                var b = new byte[p.SamplesPerPixel * 2];
                for (int k = 0; k < p.SamplesPerPixel; k++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(k * 2), (ushort)p.BitsPerSample);
                tags.Add((258, 3, p.SamplesPerPixel, p.SamplesPerPixel == 2 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : Extra(b)));
            }
            Short(259, p.Compression);
            Short(262, p.Photometric);
            tags.Add((273, 4, offsets.Length, Longs(offsets)));
            Short(277, p.SamplesPerPixel);
            Long(278, p.RowsPerStrip);
            tags.Add((279, 4, p.Strips.Length, Longs(p.Strips.Select(s => (long)s.Length).ToArray())));
            tags.Add((282, 5, 1, Rational(p.DpiX)));
            tags.Add((283, 5, 1, Rational(p.DpiY)));
            Short(284, 1); // PlanarConfiguration: chunky
            Short(296, 2); // ResolutionUnit: inch
            tags.Add((297, 3, 2, (uint)i | (uint)pages.Count << 16)); // PageNumber: this page, of how many
            tags.Add((305, 2, software.Length, software.Length <= 4 ? 0 : Extra(software)));
            if (p.Predictor) Short(317, 2);
            if (p.Alpha) Short(338, 2); // ExtraSamples: unassociated alpha
            if (p.Compression == 4) Long(293, 0); // T6Options: no uncompressed mode
            tags.Sort((x, y) => x.Tag.CompareTo(y.Tag));

            if (at != ifdAt[i]) throw new InvalidOperationException("TIFF layout mismatch.");
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan((int)at), (ushort)tags.Count);
            at += 2;
            foreach (var (tag, type, count, value) in tags)
            {
                var e = o.AsSpan((int)at, 12);
                BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)tag);
                BinaryPrimitives.WriteUInt16LittleEndian(e[2..], (ushort)type);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)count);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)value);
                at += 12;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan((int)at), i + 1 < pages.Count ? (uint)ifdAt[i + 1] : 0);
            at += 4;
        }
        return o;
    }

    private static int TagCount(Page p, bool multiPage)
        => 15 + (multiPage ? 1 : 0) + (p.Predictor ? 1 : 0) + (p.Alpha ? 1 : 0) + (p.Compression == 4 ? 1 : 0);

    private static long ExtraBytes(Page p, int softwareLength)
    {
        static long Even(long n) => n + (n & 1);
        long n = 16; // the two resolution rationals
        if (p.SamplesPerPixel > 2) n += Even(p.SamplesPerPixel * 2L);
        if (p.Strips.Length > 1) n += 2 * Even(p.Strips.Length * 4L);
        if (softwareLength > 4) n += Even(softwareLength);
        return n;
    }

    // ------------------------------------------------------------------ compressors

    /// <summary>
    /// TIFF LZW: 9 to 12-bit codes, most significant bit first, a clear code first and whenever the table fills, and
    /// the code width growing one code early, as libtiff writes it.
    /// </summary>
    internal static byte[] Lzw(ReadOnlySpan<byte> data)
    {
        const int Clear = 256, End = 257, First = 258, MaxCode = 4094;
        var output = new List<byte>(data.Length / 2 + 16);
        ulong bits = 0;
        int count = 0, width = 9;
        void Put(int code)
        {
            bits = bits << width | (uint)code;
            count += width;
            while (count >= 8) { count -= 8; output.Add((byte)(bits >> count)); }
        }

        // The table maps (prefix code, next byte) to a code, with open addressing in a power-of-two hash table.
        const int HashSize = 1 << 13;
        var keys = new int[HashSize];
        var codes = new short[HashSize];
        void Reset() => Array.Fill(keys, -1);
        Reset();
        int next = First;
        Put(Clear);
        if (data.IsEmpty) { Put(End); return Flush(); }
        int prefix = data[0];
        for (int i = 1; i < data.Length; i++)
        {
            byte c = data[i];
            int key = prefix << 8 | c;
            int h = (int)((uint)key * 0x9E3779B1u >> 19);
            while (keys[h] != -1 && keys[h] != key) h = (h + 1) & (HashSize - 1);
            if (keys[h] == key) { prefix = codes[h]; continue; }
            Put(prefix);
            keys[h] = key;
            codes[h] = (short)next++;
            if (next == MaxCode)
            {
                Put(Clear);
                Reset();
                next = First;
                width = 9;
            }
            else if (next > (1 << width) - 1) width++;
            prefix = c;
        }
        Put(prefix);
        // The decoder adds an entry for this last code too, so the end code may already need the wider width.
        next++;
        if (next == MaxCode) { Put(Clear); width = 9; }
        else if (next > (1 << width) - 1) width++;
        Put(End);
        return Flush();

        byte[] Flush()
        {
            if (count > 0) output.Add((byte)(bits << (8 - count)));
            return output.ToArray();
        }
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        var ms = new MemoryStream(data.Length / 4 + 64);
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>PackBits per row, as TIFF requires: runs of 2 or more equal bytes become repeat codes, the rest literals.</summary>
    private static byte[] PackBits(ReadOnlySpan<byte> data, int rowBytes)
    {
        var o = new MemoryStream(data.Length / 2 + 16);
        for (int row = 0; row < data.Length; row += rowBytes)
        {
            var r = data.Slice(row, Math.Min(rowBytes, data.Length - row));
            int i = 0;
            while (i < r.Length)
            {
                int run = 1;
                while (i + run < r.Length && run < 128 && r[i + run] == r[i]) run++;
                if (run >= 2)
                {
                    o.WriteByte((byte)(1 - run));
                    o.WriteByte(r[i]);
                    i += run;
                    continue;
                }
                int start = i;
                while (i < r.Length && i - start < 128 && !(i + 1 < r.Length && r[i + 1] == r[i])) i++;
                if (i == start) i++;
                o.WriteByte((byte)(i - start - 1));
                o.Write(r[start..i]);
            }
        }
        return o.ToArray();
    }
}

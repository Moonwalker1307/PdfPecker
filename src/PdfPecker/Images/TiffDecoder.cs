using System.IO.Compression;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Images;

/// <summary>
/// Decodes the first image of a TIFF file: bilevel, grey, palette, RGB and CMYK pixels of 1 to 16 bits, in strips or
/// tiles, chunky or planar, uncompressed or compressed with LZW, Deflate, PackBits, CCITT fax (Group 3 and 4) or JPEG,
/// with the horizontal predictor and an alpha channel. Bilevel images stay 1 bit per pixel.
/// </summary>
internal static class TiffDecoder
{
    public static bool IsTiff(byte[] d)
        => d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] == 42));

    private readonly record struct Entry(int Type, long Count, long ValueAt);

    private sealed class Reader
    {
        public readonly byte[] D;
        public readonly bool Little;
        public Reader(byte[] d) { D = d; Little = d[0] == 'I'; }

        public int U16(long at) => at + 2 > D.Length ? 0 : Little ? D[at] | D[at + 1] << 8 : D[at] << 8 | D[at + 1];

        public long U32(long at) => at + 4 > D.Length ? 0
            : Little ? (uint)(D[at] | D[at + 1] << 8 | D[at + 2] << 16 | D[at + 3] << 24) : (uint)(D[at] << 24 | D[at + 1] << 16 | D[at + 2] << 8 | D[at + 3]);

        /// <summary>The values of a tag as numbers (BYTE, SHORT or LONG), at most <paramref name="max"/> of them.</summary>
        public long[] Values(Entry e, int max = 1 << 20)
        {
            int size = e.Type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 => 4, _ => 0 };
            if (size == 0 || e.Count <= 0 || e.Count > max) return Array.Empty<long>();
            long at = size * e.Count <= 4 ? e.ValueAt : U32(e.ValueAt);
            if (at < 0 || at + size * e.Count > D.Length) return Array.Empty<long>();
            var v = new long[e.Count];
            for (int i = 0; i < v.Length; i++)
                v[i] = size == 1 ? D[at + i] : size == 2 ? U16(at + i * 2L) : U32(at + i * 4L);
            return v;
        }

        public byte[] Bytes(Entry e)
        {
            if (e.Count <= 0 || e.Count > D.Length) return Array.Empty<byte>();
            long at = e.Count <= 4 ? e.ValueAt : U32(e.ValueAt);
            return at < 0 || at + e.Count > D.Length ? Array.Empty<byte>() : D.AsSpan((int)at, (int)e.Count).ToArray();
        }
    }

    public static PdfImage? Decode(byte[] d, long maxPixels)
    {
        if (!IsTiff(d)) return null;
        var r = new Reader(d);
        long ifd = r.U32(4);
        if (ifd < 8 || ifd + 2 > d.Length) return null;
        int count = r.U16(ifd);
        var tags = new Dictionary<int, Entry>();
        for (int i = 0; i < count; i++)
        {
            long at = ifd + 2 + i * 12L;
            if (at + 12 > d.Length) break;
            tags[r.U16(at)] = new Entry(r.U16(at + 2), r.U32(at + 4), at + 8);
        }
        long One(int tag, long fallback) => tags.TryGetValue(tag, out var e) && r.Values(e, 1) is { Length: 1 } v ? v[0] : fallback;
        long[] Many(int tag) => tags.TryGetValue(tag, out var e) ? r.Values(e) : Array.Empty<long>();

        long width = One(256, 0), height = One(257, 0);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue || width * height > maxPixels) return null;
        int w = (int)width, h = (int)height;
        int spp = (int)One(277, 1);
        var bitsList = Many(258);
        int bps = bitsList.Length > 0 ? (int)bitsList[0] : 1;
        int compression = (int)One(259, 1);
        int photometric = (int)One(262, spp >= 3 ? 2 : 1);
        bool planar = One(284, 1) == 2;
        int predictor = (int)One(317, 1);
        int sampleFormat = (int)One(339, 1);
        bool reverseBits = One(266, 1) == 2;
        if (spp is < 1 or > 8 || bps is not (1 or 2 or 4 or 8 or 16) || sampleFormat is not (1 or 2 or 4)) return null;
        foreach (var b in bitsList) if (b != bps) return null;

        int colors = photometric switch { 0 or 1 or 3 => 1, 2 or 6 => 3, 5 => 4, _ => -1 };
        if (colors < 0 || spp < colors) return null;
        if (photometric == 6 && compression != 7) return null; // YCbCr outside JPEG: subsampled, rare
        var extra = Many(338);
        bool alpha = spp > colors && extra.Length > 0 && extra[0] is 1 or 2;
        bool premultiplied = alpha && extra[0] == 1;

        // Chunks: strips of whole rows, or tiles.
        bool tiled = tags.ContainsKey(322);
        int chunkW = tiled ? (int)One(322, 0) : w;
        int chunkH = tiled ? (int)One(323, 0) : (int)Math.Clamp(One(278, h), 1, h);
        if (chunkW <= 0 || chunkH <= 0 || (long)chunkW * chunkH > maxPixels) return null;
        // Tiles are multiples of 16 pixels, so one never needs to be more than 15 pixels wider or taller than the
        // image; a bigger one would only be allocated and decoded to be cut away.
        if (tiled && (chunkW > (w + 15L) / 16 * 16 || chunkH > (h + 15L) / 16 * 16)) return null;
        var offsets = Many(tiled ? 324 : 273);
        var byteCounts = Many(tiled ? 325 : 279);
        int across = (w + chunkW - 1) / chunkW, down = (h + chunkH - 1) / chunkH;
        int planes = planar ? spp : 1, chunkSamples = planar ? 1 : spp;
        if (offsets.Length < (long)across * down * planes) return null;

        long planeRowBytes = ((long)w * chunkSamples * bps + 7) / 8;
        long rawBytes = planeRowBytes * h * planes;
        if (rawBytes > int.MaxValue) return null;
        // No compression here makes more than about 2000 bytes from one: a few bytes cannot fill a huge image, so it is
        // not allocated for them.
        if ((long)w * h > 1 << 22 && rawBytes > d.Length * 2000L) return null;
        // Every chunk decodes to at most its own size, so this is all the chunks together can make. Tiles that cross
        // the right and bottom edges pad the image, but never to more than twice its size, rounded up to whole tiles.
        long paddedBytes = (((w + 15L) / 16 * 16 * chunkSamples * bps + 7) / 8) * ((h + 15L) / 16 * 16) * planes;
        long chunkBytes = ((long)chunkW * chunkSamples * bps + 7) / 8 * chunkH * across * down * planes;
        if (chunkBytes > 4 * paddedBytes) return null;
        // Chunks may share data, as blank tiles often do, but a thousand pointing at one large blob would copy and decode
        // it a thousand times: what they read together stays in proportion to the file and the image.
        long readBudget = d.Length + 4 * chunkBytes;
        var plane = new byte[planes][];
        for (int p = 0; p < planes; p++) plane[p] = new byte[planeRowBytes * h];
        var tables = tags.TryGetValue(347, out var jt) ? r.Bytes(jt) : null;
        bool ycbcrJpeg = false;

        int chunkRowBytes = (int)(((long)chunkW * chunkSamples * bps + 7) / 8);
        for (int p = 0; p < planes; p++)
        for (int cy = 0; cy < down; cy++)
        for (int cx = 0; cx < across; cx++)
        {
            int index = (p * down + cy) * across + cx;
            long start = offsets[index];
            long length = index < byteCounts.Length ? byteCounts[index] : d.Length - start;
            if (start <= 0 || start >= d.Length) continue; // a missing chunk stays blank
            length = Math.Min(length, d.Length - start);
            if ((readBudget -= Math.Max(length, 0)) < 0) return null;
            var raw = d.AsSpan((int)start, (int)length).ToArray();
            int rows = Math.Min(chunkH, h - cy * chunkH);
            int expected = chunkRowBytes * chunkH;
            byte[]? data;
            switch (compression)
            {
                case 1: data = raw; break;
                case 5:
                    if (raw.Length > 1 && raw[0] == 0 && (raw[1] & 1) != 0) return null; // old-style LZW, bits in the other order
                    data = Filters.Lzw(raw, true, expected); // strips may share one blob, so each stops at its own size
                    break;
                case 8 or 32946: data = Inflate(raw, expected); break;
                case 32773: data = PackBits(raw, expected); break;
                case 2 or 3 or 4:
                {
                    if (bps != 1 || chunkSamples != 1) return null;
                    if (reverseBits) for (int i = 0; i < raw.Length; i++) raw[i] = ReverseBits(raw[i]);
                    long options = compression == 3 ? One(292, 0) : 0;
                    int k = compression == 4 ? -1 : compression == 3 && (options & 1) != 0 ? 1 : 0;
                    // Fax data codes white runs as 0 bits and black runs as 1; the photometric interpretation then says
                    // what 0 and 1 are, as for any other bilevel image.
                    data = CcittFaxDecoder.Decode(raw, k, chunkW, chunkH, compression == 2, blackIs1: true);
                    break;
                }
                case 7:
                {
                    if (bps != 8) return null;
                    var jpeg = raw;
                    if (tables is { Length: > 4 } && raw.Length > 2)
                    {
                        // An abbreviated stream: the shared tables go in after its start-of-image marker.
                        jpeg = new byte[2 + tables.Length - 4 + raw.Length - 2];
                        jpeg[0] = 0xFF; jpeg[1] = 0xD8;
                        Array.Copy(tables, 2, jpeg, 2, tables.Length - 4);
                        Array.Copy(raw, 2, jpeg, tables.Length - 2, raw.Length - 2);
                    }
                    var decoded = JpegDecoder.Decode(jpeg, colorTransform: photometric == 6);
                    if (decoded is not { } j || j.Components != chunkSamples && !(photometric == 6 && j.Components == 3)) return null;
                    ycbcrJpeg |= photometric == 6;
                    data = new byte[expected];
                    int jRow = j.Width * j.Components, copyRow = Math.Min(jRow, chunkRowBytes);
                    for (int y = 0; y < Math.Min(chunkH, j.Height); y++) Array.Copy(j.Data, y * jRow, data, y * chunkRowBytes, copyRow);
                    break;
                }
                default: return null;
            }
            if (data == null) continue;
            if (predictor == 2 && compression is not (7 or 2 or 3 or 4))
            {
                if (bps is not (8 or 16)) return null;
                UndoPredictor(data, chunkRowBytes, chunkSamples, bps, r.Little);
            }
            else if (predictor == 3) return null; // floating point

            // Into the plane: the chunk's rows, cut at the image's right and bottom edges.
            long xBytes = (long)cx * chunkW * chunkSamples * bps / 8;
            int copy = (int)Math.Min(chunkRowBytes, planeRowBytes - xBytes);
            for (int y = 0; y < rows; y++)
            {
                long from = (long)y * chunkRowBytes;
                if (from >= data.Length) break;
                Array.Copy(data, from, plane[p], (cy * chunkH + y) * planeRowBytes + xBytes, (int)Math.Min(copy, data.Length - from));
            }
        }
        if (ycbcrJpeg) photometric = 2;

        // Bilevel: kept at 1 bit, white or black as the photometric interpretation says.
        if (bps == 1 && spp == 1 && photometric is 0 or 1)
            return new PdfImage
            {
                Width = w, Height = h, ColorSpace = "/DeviceGray", BitsPerComponent = 1, Data = PdfImage.Deflate(plane[0]),
                Decode = photometric == 0 ? "[1 0]" : null,
            };

        int Sample(int x, int y, int s)
        {
            var buf = planar ? plane[s] : plane[0];
            int sx = planar ? x : x * spp + s;
            long row = y * planeRowBytes;
            switch (bps)
            {
                case 8: return buf[row + sx];
                case 16: return r.Little ? buf[row + sx * 2 + 1] << 8 | buf[row + sx * 2] : buf[row + sx * 2] << 8 | buf[row + sx * 2 + 1];
                default:
                {
                    long bit = (long)sx * bps;
                    return (buf[row + bit / 8] >> (8 - bps - (int)(bit % 8))) & ((1 << bps) - 1);
                }
            }
        }
        int max = (1 << bps) - 1;
        byte Scale(int v) => bps == 16 ? (byte)(v >> 8) : bps == 8 ? (byte)v : (byte)(v * 255 / max);

        byte[]? palette = null;
        if (photometric == 3)
        {
            var map = Many(320);
            int entries = 1 << bps;
            if (map.Length < entries * 3) return null;
            palette = new byte[entries * 3];
            for (int i = 0; i < entries; i++)
                for (int c = 0; c < 3; c++) palette[i * 3 + c] = (byte)(map[c * entries + i] >> 8);
        }

        var a = alpha ? new byte[w * h] : null;
        if (photometric is 0 or 1 && palette == null)
        {
            var gray = new byte[w * h];
            for (int y = 0, o = 0; y < h; y++)
                for (int x = 0; x < w; x++, o++)
                {
                    byte v = Scale(Sample(x, y, 0));
                    if (a != null) v = Unpremultiply(v, a[o] = Scale(Sample(x, y, 1)), premultiplied);
                    gray[o] = photometric == 0 ? (byte)(255 - v) : v;
                }
            var image = new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceGray", Data = PdfImage.Deflate(gray) };
            if (a != null && Array.Exists(a, v => v != 255))
                image.SMask = new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceGray", Data = PdfImage.Deflate(a) };
            return image;
        }

        var rgb = new byte[w * h * 3];
        for (int y = 0, o = 0; y < h; y++)
            for (int x = 0; x < w; x++, o++)
            {
                byte cr, cg, cb;
                if (palette != null)
                {
                    int i = Sample(x, y, 0);
                    (cr, cg, cb) = (palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);
                }
                else if (photometric == 5)
                {
                    var (rr, gg, bb) = Rendering.Content.DeviceCmyk.Convert(Scale(Sample(x, y, 0)) / 255f, Scale(Sample(x, y, 1)) / 255f,
                        Scale(Sample(x, y, 2)) / 255f, Scale(Sample(x, y, 3)) / 255f);
                    (cr, cg, cb) = ((byte)rr, (byte)gg, (byte)bb);
                }
                else (cr, cg, cb) = (Scale(Sample(x, y, 0)), Scale(Sample(x, y, 1)), Scale(Sample(x, y, 2)));
                if (a != null)
                {
                    byte av = a[o] = Scale(Sample(x, y, colors));
                    cr = Unpremultiply(cr, av, premultiplied);
                    cg = Unpremultiply(cg, av, premultiplied);
                    cb = Unpremultiply(cb, av, premultiplied);
                }
                rgb[o * 3] = cr; rgb[o * 3 + 1] = cg; rgb[o * 3 + 2] = cb;
            }
        return PdfImage.FromPixels(new DecodedImage(w, h, rgb, a));
    }

    private static byte Unpremultiply(byte c, byte a, bool premultiplied)
        => !premultiplied || a == 255 ? c : a == 0 ? (byte)0 : (byte)Math.Min(255, (c * 255 + a / 2) / a);

    private static byte ReverseBits(byte b)
    {
        b = (byte)((b & 0xF0) >> 4 | (b & 0x0F) << 4);
        b = (byte)((b & 0xCC) >> 2 | (b & 0x33) << 2);
        return (byte)((b & 0xAA) >> 1 | (b & 0x55) << 1);
    }

    private static byte[] Inflate(byte[] raw, int expected)
    {
        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        var output = new byte[expected];
        int n = 0;
        try
        {
            while (n < expected)
            {
                int got = z.Read(output, n, expected - n);
                if (got == 0) break;
                n += got;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { } // a damaged chunk keeps what was decoded
        return output;
    }

    private static byte[] PackBits(byte[] raw, int expected)
    {
        var output = new byte[expected];
        int o = 0, i = 0;
        while (i < raw.Length && o < expected)
        {
            int n = (sbyte)raw[i++];
            if (n >= 0)
            {
                int len = Math.Min(Math.Min(n + 1, raw.Length - i), expected - o);
                Array.Copy(raw, i, output, o, len);
                i += n + 1;
                o += len;
            }
            else if (n != -128 && i < raw.Length)
            {
                int len = Math.Min(1 - n, expected - o);
                output.AsSpan(o, len).Fill(raw[i++]);
                o += len;
            }
        }
        return output;
    }

    /// <summary>Undoes horizontal differencing: each sample was stored as the difference from the one to its left.</summary>
    private static void UndoPredictor(byte[] data, int rowBytes, int samples, int bps, bool little)
    {
        for (int row = 0; row + rowBytes <= data.Length; row += rowBytes)
        {
            if (bps == 8)
            {
                for (int i = row + samples; i < row + rowBytes; i++) data[i] += data[i - samples];
                continue;
            }
            int step = samples * 2;
            for (int i = row + step; i + 1 < row + rowBytes; i += 2)
            {
                int prev = little ? data[i - step] | data[i - step + 1] << 8 : data[i - step] << 8 | data[i - step + 1];
                int cur = little ? data[i] | data[i + 1] << 8 : data[i] << 8 | data[i + 1];
                int v = (prev + cur) & 0xFFFF;
                if (little) { data[i] = (byte)v; data[i + 1] = (byte)(v >> 8); }
                else { data[i] = (byte)(v >> 8); data[i + 1] = (byte)v; }
            }
        }
    }
}

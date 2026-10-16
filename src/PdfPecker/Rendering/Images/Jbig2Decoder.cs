using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>
/// JBIG2 (ITU-T T.88) decoder for JBIG2Decode image streams in the embedded organization PDF uses: generic regions
/// (arithmetic and MMR, typical prediction, adaptive templates), refinement regions, symbol dictionaries and text regions
/// (arithmetic and Huffman coding, refinement and aggregation), pattern dictionaries and halftone regions, standard and
/// custom Huffman tables, and striped pages of unknown height.
/// </summary>
internal sealed class Jbig2Decoder
{
    // ------------------------------------------------------------------ limits

    private const int MaxSymbols = 1 << 20;
    private const long MaxWork = 1L << 28; // pixels allocated, decoded and drawn, against hostile input
    private const int MaxFill = 256; // arithmetic decoding far past the end of the data means the data is damaged

    private long _work;
    private long _imagePixels = PdfImageDecoder.MaxPixels; // the size of the image in the PDF, which bounds a region

    private void Work(long n)
    {
        _work += n;
        if (_work > MaxWork) throw new InvalidDataException("JBIG2 image takes too much work to decode.");
        if ((_work & 0xFFFFF) < n) PdfFile.CheckCancel();
    }

    // ------------------------------------------------------------------ bitmaps

    private static void CheckData(MqDecoder? mq, BitReader? br = null)
    {
        if (mq != null && mq.FillBytes > MaxFill || br != null && br.Pos > br.End + MaxFill)
            throw new InvalidDataException("JBIG2 data ended early.");
    }

    private Bitmap NewBitmap(int w, int h)
    {
        Work((long)w * h + 16);
        return new Bitmap(w, h);
    }

    private Bitmap Crop(Bitmap b, int x, int y, int w, int h)
    {
        Work((long)w * h + 16);
        return b.Crop(x, y, w, h);
    }

    internal sealed class Bitmap
    {
        public readonly int W, H;
        public readonly byte[] P; // one byte per pixel, 1 = black

        public Bitmap(int w, int h)
        {
            if (w < 0 || h < 0 || (long)w * h > PdfImageDecoder.MaxPixels) throw new InvalidDataException("JBIG2 bitmap too large.");
            W = w;
            H = h;
            PdfFile.ChargeDecoded((long)w * h + 32);
            P = new byte[w * h];
        }

        public int Get(int x, int y) => (uint)x < (uint)W && (uint)y < (uint)H ? P[y * W + x] : 0;

        public void Fill(int v)
        {
            if (v != 0) Array.Fill(P, (byte)1);
        }

        /// <summary>Combines <paramref name="src"/> into this bitmap at (x, y): 0 OR, 1 AND, 2 XOR, 3 XNOR, 4 REPLACE.</summary>
        public void Compose(Bitmap src, int x, int y, int op)
        {
            int sx0 = Math.Max(0, -x), sy0 = Math.Max(0, -y);
            int sx1 = (int)Math.Min(src.W, (long)W - x), sy1 = (int)Math.Min(src.H, (long)H - y);
            for (int sy = sy0; sy < sy1; sy++)
            {
                int s = sy * src.W, d = (sy + y) * W + x;
                for (int sx = sx0; sx < sx1; sx++)
                {
                    int a = P[d + sx], b = src.P[s + sx];
                    P[d + sx] = op switch
                    {
                        0 => (byte)(a | b),
                        1 => (byte)(a & b),
                        2 => (byte)(a ^ b),
                        3 => (byte)(1 - (a ^ b)),
                        _ => (byte)b,
                    };
                }
            }
        }

        public Bitmap Crop(int x, int y, int w, int h)
        {
            var r = new Bitmap(w, h);
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                    r.P[yy * w + xx] = (byte)Get(x + xx, y + yy);
            return r;
        }
    }

    // ------------------------------------------------------------------ segments

    private sealed class Segment
    {
        public int Number, Type, Page;
        public int[] Refs = Array.Empty<int>();
        public byte[] Data = Array.Empty<byte>();
        public int Start, End;
    }

    private sealed class RegionInfo
    {
        public int W, H, X, Y, Op;
    }

    private readonly Dictionary<int, Bitmap[]> _symbols = new();
    private readonly Dictionary<int, Bitmap[]> _patterns = new();
    private readonly Dictionary<int, HuffTable> _tables = new();
    private readonly Dictionary<int, (Bitmap Bmp, RegionInfo Info)> _regions = new();
    private Bitmap? _page;
    private int _pageDefault;

    /// <summary>
    /// Decodes a JBIG2 image to packed 1-bit rows of <paramref name="width"/> x <paramref name="height"/> pixels with 1 =
    /// white, as the JBIG2Decode filter delivers them. Returns null when nothing can be decoded.
    /// </summary>
    public static byte[]? Decode(byte[] data, byte[]? globals, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > PdfImageDecoder.MaxPixels) return null;
        var dec = new Jbig2Decoder { _imagePixels = (long)width * height };
        try
        {
            if (globals != null) dec.Run(globals, height);
            dec.Run(data, height);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException
                                       || (ex is InvalidDataException && !ex.Message.StartsWith("The page", StringComparison.Ordinal)))
        {
            // Damaged data: show what was decoded so far.
        }
        var page = dec._page;
        if (page == null) return null;
        int rowBytes = (width + 7) / 8;
        var o = new byte[rowBytes * height];
        Array.Fill(o, (byte)0xFF);
        int h = Math.Min(height, page.H), w = Math.Min(width, page.W);
        for (int y = 0; y < h; y++)
        {
            int s = y * page.W, d = y * rowBytes;
            for (int x = 0; x < w; x++)
                if (page.P[s + x] != 0) o[d + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
        }
        return o;
    }

    private void Run(byte[] d, int pdfHeight)
    {
        int pos = 0;
        while (pos + 11 <= d.Length)
        {
            var seg = new Segment { Data = d };
            seg.Number = (int)ReadU32(d, pos);
            int flags = d[pos + 4];
            seg.Type = flags & 0x3F;
            pos += 5;
            int rts = d[pos];
            int count = rts >> 5;
            if (count == 7)
            {
                if (pos + 4 > d.Length) return;
                count = (int)(ReadU32(d, pos) & 0x1FFFFFFF);
                if (count > 1 << 16) return;
                pos += 4 + (count + 8) / 8;
            }
            else pos++;
            int refSize = seg.Number <= 256 ? 1 : seg.Number <= 65536 ? 2 : 4;
            seg.Refs = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (pos + refSize > d.Length) return;
                seg.Refs[i] = refSize == 1 ? d[pos] : refSize == 2 ? d[pos] << 8 | d[pos + 1] : (int)ReadU32(d, pos);
                pos += refSize;
            }
            bool bigPage = (flags & 0x40) != 0;
            if (pos + (bigPage ? 4 : 1) + 4 > d.Length) return;
            seg.Page = bigPage ? (int)ReadU32(d, pos) : d[pos];
            pos += bigPage ? 4 : 1;
            uint len = ReadU32(d, pos);
            pos += 4;
            seg.Start = pos;
            if (len == 0xFFFFFFFF)
            {
                if (seg.Type is not (36 or 38 or 39)) return;
                seg.End = FindGenericEnd(d, pos);
            }
            else seg.End = (int)Math.Min(d.Length, pos + (long)len);
            pos = seg.End;
            if (seg.Type == 51) break; // end of file
            ProcessSegment(seg, pdfHeight);
        }
    }

    private static int FindGenericEnd(byte[] d, int start)
    {
        // An immediate generic region of unknown length ends with 0xFF 0xAC (arithmetic) or 0x00 0x00 (MMR),
        // followed by a 4-byte row count.
        int p = start + 18;
        if (p >= d.Length) return d.Length;
        bool mmr = (d[p - 1] & 1) != 0;
        for (int i = p; i + 1 < d.Length; i++)
        {
            if (mmr ? d[i] == 0 && d[i + 1] == 0 : d[i] == 0xFF && d[i + 1] == 0xAC)
                return Math.Min(d.Length, i + 6);
        }
        return d.Length;
    }

    private static uint ReadU32(byte[] d, int p) =>
        p + 3 < d.Length ? (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]) : 0;

    private static int ReadS32(byte[] d, int p) => (int)ReadU32(d, p);

    private static int ReadU16(byte[] d, int p) => p + 1 < d.Length ? d[p] << 8 | d[p + 1] : 0;

    private static RegionInfo ReadRegionInfo(byte[] d, int p) => new()
    {
        W = (int)Math.Min(ReadU32(d, p), int.MaxValue),
        H = (int)Math.Min(ReadU32(d, p + 4), int.MaxValue),
        X = ReadS32(d, p + 8),
        Y = ReadS32(d, p + 12),
        Op = p + 16 < d.Length ? d[p + 16] & 7 : 0,
    };

    private void ProcessSegment(Segment seg, int pdfHeight)
    {
        PdfFile.CheckCancel();
        switch (seg.Type)
        {
            case 0:
                _symbols[seg.Number] = DecodeSymbolDictionary(seg);
                break;
            case 4 or 6 or 7:
                PlaceRegion(seg, DecodeTextRegionSegment(seg, out var ti), ti);
                break;
            case 16:
                _patterns[seg.Number] = DecodePatternDictionary(seg);
                break;
            case 20 or 22 or 23:
                PlaceRegion(seg, DecodeHalftoneRegion(seg, out var hi), hi);
                break;
            case 36 or 38 or 39:
                PlaceRegion(seg, DecodeGenericRegionSegment(seg, out var gi), gi);
                break;
            case 40 or 42 or 43:
            {
                // A refinement of the page itself is drawn back with the region's own operator, as other readers do.
                PlaceRegion(seg, DecodeRefinementSegment(seg, out var ri), ri);
                break;
            }
            case 48:
                PageInfo(seg, pdfHeight);
                break;
            case 53:
                _tables[seg.Number] = ReadTableSegment(seg);
                break;
            // 49 end of page, 50 end of stripe (pages of unknown height take the PDF image height), 52 profiles and
            // 62 extensions: nothing to do.
        }
    }

    private void PageInfo(Segment seg, int pdfHeight)
    {
        var d = seg.Data;
        int p = seg.Start;
        if (seg.End - p < 19) throw new InvalidDataException("Short JBIG2 page information.");
        uint w = ReadU32(d, p), h = ReadU32(d, p + 4);
        int flags = d[p + 16];
        _pageDefault = (flags >> 2) & 1;
        if (h == 0xFFFFFFFF) h = (uint)Math.Max(1, pdfHeight);
        if (w == 0 || w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("Bad JBIG2 page size.");
        _page = NewBitmap((int)w, (int)h);
        _page.Fill(_pageDefault);
    }

    private void PlaceRegion(Segment seg, Bitmap? bmp, RegionInfo info)
    {
        if (bmp == null) return;
        if (seg.Type is 4 or 20 or 36 or 40)
        {
            _regions[seg.Number] = (bmp, info);
            return;
        }
        if (_page == null) return;
        Work((long)bmp.W * bmp.H);
        _page.Compose(bmp, info.X, info.Y, info.Op);
    }

    // ------------------------------------------------------------------ arithmetic integer decoding (Annex A)

    private sealed class IntContext
    {
        public readonly byte[] Cx = new byte[512];
    }

    /// <summary>Decodes an integer; returns false for OOB.</summary>
    private static bool DecodeInt(MqDecoder mq, IntContext ic, out int value)
    {
        var cx = ic.Cx;
        int prev = 1;
        int Bit()
        {
            int b = mq.Decode(cx, prev);
            prev = prev < 256 ? prev << 1 | b : ((prev << 1 | b) & 511) | 256;
            return b;
        }
        int s = Bit();
        long v;
        if (Bit() == 0) v = ReadBits(2);
        else if (Bit() == 0) v = ReadBits(4) + 4;
        else if (Bit() == 0) v = ReadBits(6) + 20;
        else if (Bit() == 0) v = ReadBits(8) + 84;
        else if (Bit() == 0) v = ReadBits(12) + 340;
        else v = ReadBits(32) + 4436;
        long ReadBits(int n)
        {
            long r = 0;
            for (int i = 0; i < n; i++) r = r << 1 | (long)Bit();
            return r;
        }
        if (s == 1 && v == 0)
        {
            value = 0;
            return false;
        }
        v = s == 1 ? -v : v;
        value = (int)Math.Clamp(v, int.MinValue, int.MaxValue);
        return true;
    }

    private static int DecodeIaid(MqDecoder mq, byte[] cx, int codeLen)
    {
        int prev = 1;
        for (int i = 0; i < codeLen; i++) prev = prev << 1 | mq.Decode(cx, prev);
        return prev - (1 << codeLen);
    }

    // ------------------------------------------------------------------ generic region decoding (6.2)

    private static readonly int[] TpgdContexts = { 0x9B25, 0x0795, 0x00E5, 0x0195 };

    private Bitmap DecodeGenericArith(MqDecoder mq, byte[] cx, int w, int h, int template, bool tpgdon, sbyte[] at, Bitmap? skip = null)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h);
        bool ltp = false;
        // The default adaptive pixel positions allow a faster row-buffered context for template 0.
        bool nominal0 = template == 0 && at.Length >= 8 && at[0] == 3 && at[1] == -1 && at[2] == -3 && at[3] == -1
                        && at[4] == 2 && at[5] == -2 && at[6] == -2 && at[7] == -2;
        for (int y = 0; y < h; y++)
        {
            CheckData(mq);
            if (tpgdon)
            {
                ltp ^= mq.Decode(cx, TpgdContexts[template]) != 0;
                if (ltp)
                {
                    if (y > 0) Buffer.BlockCopy(b.P, (y - 1) * w, b.P, y * w, w);
                    continue;
                }
            }
            if (nominal0 && skip == null)
            {
                DecodeRowTemplate0(mq, cx, b, y);
                continue;
            }
            for (int x = 0; x < w; x++)
            {
                if (skip != null && skip.P[y * w + x] != 0) continue;
                int c = template switch
                {
                    0 => Context0(b, x, y, at),
                    1 => Context1(b, x, y, at),
                    2 => Context2(b, x, y, at),
                    _ => Context3(b, x, y, at),
                };
                if (mq.Decode(cx, c) != 0) b.P[y * w + x] = 1;
            }
        }
        return b;
    }

    private static void DecodeRowTemplate0(MqDecoder mq, byte[] cx, Bitmap b, int y)
    {
        int w = b.W;
        var p = b.P;
        int r1 = (y - 1) * w, r2 = (y - 2) * w, r0 = y * w;
        bool has1 = y >= 1, has2 = y >= 2;
        int G1(int x) => has1 && (uint)x < (uint)w ? p[r1 + x] : 0;
        int G2(int x) => has2 && (uint)x < (uint)w ? p[r2 + x] : 0;
        // Windows over rows y-2 (x-2..x+2), y-1 (x-3..x+3) and y (x-4..x-1), shifted one pixel per step.
        int line2 = G2(-2) << 4 | G2(-1) << 3 | G2(0) << 2 | G2(1) << 1 | G2(2);
        int line1 = G1(-3) << 6 | G1(-2) << 5 | G1(-1) << 4 | G1(0) << 3 | G1(1) << 2 | G1(2) << 1 | G1(3);
        int line0 = 0;
        for (int x = 0; x < w; x++)
        {
            // Spec bit order: y-2 row: A4(-2) b15, -1 b14, 0 b13, +1 b12, A3(+2) b11;
            // y-1 row: A2(-3) b10, -2 b9, -1 b8, 0 b7, +1 b6, +2 b5, A1(+3) b4; y row: -4 b3 .. -1 b0.
            int c = line2 << 11 | line1 << 4 | line0;
            int bit = mq.Decode(cx, c);
            if (bit != 0) p[r0 + x] = 1;
            line2 = (line2 << 1 & 0x1F) | G2(x + 3);
            line1 = (line1 << 1 & 0x7F) | G1(x + 4);
            line0 = (line0 << 1 & 0xF) | bit;
        }
    }

    private static int Context0(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
        | b.Get(x + at[0], y + at[1]) << 4
        | b.Get(x + 2, y - 1) << 5 | b.Get(x + 1, y - 1) << 6 | b.Get(x, y - 1) << 7 | b.Get(x - 1, y - 1) << 8 | b.Get(x - 2, y - 1) << 9
        | b.Get(x + at[2], y + at[3]) << 10 | b.Get(x + at[4], y + at[5]) << 11
        | b.Get(x + 1, y - 2) << 12 | b.Get(x, y - 2) << 13 | b.Get(x - 1, y - 2) << 14
        | b.Get(x + at[6], y + at[7]) << 15;

    private static int Context1(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2
        | b.Get(x + at[0], y + at[1]) << 3
        | b.Get(x + 2, y - 1) << 4 | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8
        | b.Get(x + 2, y - 2) << 9 | b.Get(x + 1, y - 2) << 10 | b.Get(x, y - 2) << 11 | b.Get(x - 1, y - 2) << 12;

    private static int Context2(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1
        | b.Get(x + at[0], y + at[1]) << 2
        | b.Get(x + 1, y - 1) << 3 | b.Get(x, y - 1) << 4 | b.Get(x - 1, y - 1) << 5 | b.Get(x - 2, y - 1) << 6
        | b.Get(x + 1, y - 2) << 7 | b.Get(x, y - 2) << 8 | b.Get(x - 1, y - 2) << 9;

    private static int Context3(Bitmap b, int x, int y, sbyte[] at) =>
        b.Get(x - 1, y) | b.Get(x - 2, y) << 1 | b.Get(x - 3, y) << 2 | b.Get(x - 4, y) << 3
        | b.Get(x + at[0], y + at[1]) << 4
        | b.Get(x + 1, y - 1) << 5 | b.Get(x, y - 1) << 6 | b.Get(x - 1, y - 1) << 7 | b.Get(x - 2, y - 1) << 8 | b.Get(x - 3, y - 1) << 9;

    private static int GenericContextSize(int template) => template == 0 ? 1 << 16 : template == 1 ? 1 << 13 : 1 << 10;

    private Bitmap DecodeGenericMmr(byte[] d, int start, int w, int h, out int end)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h);
        var packed = CcittFaxDecoder.DecodeMmr(d, start, w, h, out end);
        int rowBytes = (w + 7) / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.P[y * w + x] = (byte)(packed[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1);
        return b;
    }

    private Bitmap? DecodeGenericRegionSegment(Segment seg, out RegionInfo info)
    {
        var d = seg.Data;
        int p = seg.Start;
        info = ReadRegionInfo(d, p);
        p += 17;
        if (p >= seg.End) return null;
        int flags = d[p++];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool tpgdon = (flags & 8) != 0;
        var at = new sbyte[8];
        if (!mmr)
        {
            int n = template == 0 ? 8 : 2;
            for (int i = 0; i < n; i++) at[i] = p + i < seg.End ? (sbyte)d[p + i] : (sbyte)0;
            p += n;
        }
        int h = info.H;
        if (h == int.MaxValue)
        {
            // Unknown height: the row count follows the data.
            if (seg.End - 4 >= p) h = (int)Math.Min(ReadU32(d, seg.End - 4), int.MaxValue);
            info.H = h;
        }
        if (info.W <= 0 || h <= 0) return null;
        if (mmr) return DecodeGenericMmr(d[..seg.End], p, info.W, h, out _);
        var mq = new MqDecoder(d, p, seg.End);
        return DecodeGenericArith(mq, new byte[GenericContextSize(template)], info.W, h, template, tpgdon, at);
    }

    // ------------------------------------------------------------------ generic refinement (6.3)

    private Bitmap DecodeRefinement(MqDecoder mq, byte[] cx, int w, int h, int template, Bitmap reference, int dx, int dy,
        bool tpgron, sbyte[] at)
    {
        var b = NewBitmap(w, h);
        Work((long)w * h * 2);
        bool ltp = false;
        for (int y = 0; y < h; y++)
        {
            CheckData(mq);
            if (tpgron) ltp ^= mq.Decode(cx, template == 0 ? 0x0100 : 0x0080) != 0;
            for (int x = 0; x < w; x++)
            {
                int rx = x - dx, ry = y - dy;
                if (ltp)
                {
                    // Typical prediction: a uniform 3x3 reference neighborhood predicts the pixel.
                    int v = reference.Get(rx, ry);
                    bool uniform = true;
                    for (int j = -1; j <= 1 && uniform; j++)
                        for (int i = -1; i <= 1; i++)
                            if (reference.Get(rx + i, ry + j) != v) { uniform = false; break; }
                    if (uniform)
                    {
                        b.P[y * w + x] = (byte)v;
                        continue;
                    }
                }
                int c;
                if (template == 0)
                {
                    c = b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x + at[0], y + at[1]) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx - 1, ry + 1) << 6
                        | reference.Get(rx + 1, ry) << 7 | reference.Get(rx, ry) << 8 | reference.Get(rx - 1, ry) << 9
                        | reference.Get(rx + 1, ry - 1) << 10 | reference.Get(rx, ry - 1) << 11
                        | reference.Get(rx + at[2], ry + at[3]) << 12;
                }
                else
                {
                    c = b.Get(x - 1, y) | b.Get(x + 1, y - 1) << 1 | b.Get(x, y - 1) << 2 | b.Get(x - 1, y - 1) << 3
                        | reference.Get(rx + 1, ry + 1) << 4 | reference.Get(rx, ry + 1) << 5 | reference.Get(rx + 1, ry) << 6
                        | reference.Get(rx, ry) << 7 | reference.Get(rx - 1, ry) << 8 | reference.Get(rx, ry - 1) << 9;
                }
                if (mq.Decode(cx, c) != 0) b.P[y * w + x] = 1;
            }
        }
        return b;
    }

    private static int RefinementContextSize(int template) => template == 0 ? 1 << 13 : 1 << 10;

    private Bitmap? DecodeRefinementSegment(Segment seg, out RegionInfo info)
    {
        var d = seg.Data;
        int p = seg.Start;
        info = ReadRegionInfo(d, p);
        p += 17;
        if (p >= seg.End) return null;
        int flags = d[p++];
        int template = flags & 1;
        bool tpgron = (flags & 2) != 0;
        var at = new sbyte[4];
        if (template == 0)
        {
            for (int i = 0; i < 4; i++) at[i] = p + i < seg.End ? (sbyte)d[p + i] : (sbyte)0;
            p += 4;
        }
        Bitmap? reference = null;
        foreach (int r in seg.Refs)
            if (_regions.TryGetValue(r, out var reg)) { reference = reg.Bmp; _regions.Remove(r); break; }
        if (reference == null)
        {
            if (_page == null) return null;
            reference = Crop(_page, info.X, info.Y, info.W, info.H);
        }
        if (info.W <= 0 || info.H <= 0) return null;
        var mq = new MqDecoder(d, p, seg.End);
        return DecodeRefinement(mq, new byte[RefinementContextSize(template)], info.W, info.H, template, reference, 0, 0, tpgron, at);
    }

    // ------------------------------------------------------------------ Huffman tables (Annex B)

    private sealed class HuffLine
    {
        public int PrefLen, RangeLen, RangeLow, Code;
        public bool Lower, Upper, Oob;
    }

    private sealed class HuffTable
    {
        public readonly HuffLine[] Lines;
        private readonly Dictionary<long, HuffLine> _byCode = new();
        private readonly int _maxLen;

        public HuffTable(List<HuffLine> lines)
        {
            Lines = lines.ToArray();
            // Canonical prefix codes in table order (B.3).
            int maxLen = 0;
            foreach (var l in lines) maxLen = Math.Max(maxLen, l.PrefLen);
            var count = new int[maxLen + 2];
            foreach (var l in lines) if (l.PrefLen > 0) count[l.PrefLen]++;
            int code = 0;
            for (int len = 1; len <= maxLen; len++)
            {
                code = (code + count[len - 1]) << 1;
                if (len == 1) code = 0;
                int c = code;
                foreach (var l in lines)
                {
                    if (l.PrefLen != len) continue;
                    l.Code = c++;
                    _byCode[(long)len << 32 | (uint)l.Code] = l;
                }
            }
            _maxLen = maxLen;
        }

        /// <summary>Decodes a value; returns false for OOB.</summary>
        public bool Decode(BitReader br, out int value)
        {
            int code = 0;
            for (int len = 1; len <= _maxLen; len++)
            {
                code = code << 1 | br.Bit();
                if (!_byCode.TryGetValue((long)len << 32 | (uint)code, out var l)) continue;
                if (l.Oob) { value = 0; return false; }
                long v;
                if (l.Lower) v = l.RangeLow - (long)br.Bits(l.RangeLen);
                else v = l.RangeLow + (long)br.Bits(l.RangeLen);
                value = (int)Math.Clamp(v, int.MinValue, int.MaxValue);
                return true;
            }
            throw new InvalidDataException("Bad JBIG2 Huffman code.");
        }
    }

    private static HuffTable Table(params (int Low, int PrefLen, int RangeLen, int Kind)[] rows)
    {
        // Kind: 0 normal, 1 lower range, 2 upper range, 3 OOB.
        var lines = new List<HuffLine>();
        foreach (var (low, pref, range, kind) in rows)
            lines.Add(new HuffLine { RangeLow = low, PrefLen = pref, RangeLen = range, Lower = kind == 1, Upper = kind == 2, Oob = kind == 3 });
        return new HuffTable(lines);
    }

    private static readonly Lazy<HuffTable[]> Standard = new(() => new[]
    {
        null!,
        Table((0, 1, 4, 0), (16, 2, 8, 0), (272, 3, 16, 0), (65808, 3, 32, 2)),
        Table((0, 1, 0, 0), (1, 2, 0, 0), (2, 3, 0, 0), (3, 4, 3, 0), (11, 5, 6, 0), (75, 6, 32, 2), (0, 6, 0, 3)),
        Table((-256, 8, 8, 0), (0, 1, 0, 0), (1, 2, 0, 0), (2, 3, 0, 0), (3, 4, 3, 0), (11, 5, 6, 0), (-257, 8, 32, 1),
            (75, 7, 32, 2), (0, 6, 0, 3)),
        Table((1, 1, 0, 0), (2, 2, 0, 0), (3, 3, 0, 0), (4, 4, 3, 0), (12, 5, 6, 0), (76, 5, 32, 2)),
        Table((-255, 7, 8, 0), (1, 1, 0, 0), (2, 2, 0, 0), (3, 3, 0, 0), (4, 4, 3, 0), (12, 5, 6, 0), (-256, 7, 32, 1),
            (76, 6, 32, 2)),
        Table((-2048, 5, 10, 0), (-1024, 4, 9, 0), (-512, 4, 8, 0), (-256, 4, 7, 0), (-128, 5, 6, 0), (-64, 5, 5, 0),
            (-32, 4, 5, 0), (0, 2, 7, 0), (128, 3, 7, 0), (256, 3, 8, 0), (512, 4, 9, 0), (1024, 4, 10, 0), (-2049, 6, 32, 1),
            (2048, 6, 32, 2)),
        Table((-1024, 4, 9, 0), (-512, 3, 8, 0), (-256, 4, 7, 0), (-128, 5, 6, 0), (-64, 5, 5, 0), (-32, 4, 5, 0), (0, 4, 5, 0),
            (32, 5, 5, 0), (64, 5, 6, 0), (128, 4, 7, 0), (256, 3, 8, 0), (512, 3, 9, 0), (1024, 3, 10, 0), (-1025, 5, 32, 1),
            (2048, 5, 32, 2)),
        Table((-15, 8, 3, 0), (-7, 9, 1, 0), (-5, 8, 1, 0), (-3, 9, 0, 0), (-2, 7, 0, 0), (-1, 4, 0, 0), (0, 2, 1, 0), (2, 5, 0, 0),
            (3, 6, 0, 0), (4, 3, 4, 0), (20, 6, 1, 0), (22, 4, 4, 0), (38, 4, 5, 0), (70, 5, 6, 0), (134, 5, 7, 0), (262, 6, 7, 0),
            (390, 7, 8, 0), (646, 6, 10, 0), (-16, 9, 32, 1), (1670, 9, 32, 2), (0, 2, 0, 3)),
        Table((-31, 8, 4, 0), (-15, 9, 2, 0), (-11, 8, 2, 0), (-7, 9, 1, 0), (-5, 7, 1, 0), (-3, 4, 1, 0), (-1, 3, 1, 0), (1, 3, 1, 0),
            (3, 5, 1, 0), (5, 6, 1, 0), (7, 3, 5, 0), (39, 6, 2, 0), (43, 4, 5, 0), (75, 4, 6, 0), (139, 5, 7, 0), (267, 5, 8, 0),
            (523, 6, 8, 0), (779, 7, 9, 0), (1291, 6, 11, 0), (-32, 9, 32, 1), (3339, 9, 32, 2), (0, 2, 0, 3)),
        Table((-21, 7, 4, 0), (-5, 8, 0, 0), (-4, 7, 0, 0), (-3, 5, 0, 0), (-2, 2, 2, 0), (2, 5, 0, 0), (3, 6, 0, 0), (4, 7, 0, 0),
            (5, 8, 0, 0), (6, 2, 6, 0), (70, 5, 5, 0), (102, 6, 5, 0), (134, 6, 6, 0), (198, 6, 7, 0), (326, 6, 8, 0), (582, 6, 9, 0),
            (1094, 6, 10, 0), (2118, 7, 11, 0), (-22, 8, 32, 1), (4166, 8, 32, 2), (0, 2, 0, 3)),
        Table((1, 1, 0, 0), (2, 2, 1, 0), (4, 4, 0, 0), (5, 4, 1, 0), (7, 5, 1, 0), (9, 5, 2, 0), (13, 6, 2, 0), (17, 7, 2, 0),
            (21, 7, 3, 0), (29, 7, 4, 0), (45, 7, 5, 0), (77, 7, 6, 0), (141, 7, 32, 2)),
        Table((1, 1, 0, 0), (2, 2, 0, 0), (3, 3, 1, 0), (5, 5, 0, 0), (6, 5, 1, 0), (8, 6, 1, 0), (10, 7, 0, 0), (11, 7, 1, 0),
            (13, 7, 2, 0), (17, 7, 3, 0), (25, 7, 4, 0), (41, 8, 5, 0), (73, 8, 32, 2)),
        Table((1, 1, 0, 0), (2, 3, 0, 0), (3, 4, 0, 0), (4, 5, 0, 0), (5, 4, 1, 0), (7, 3, 3, 0), (15, 6, 1, 0), (17, 6, 2, 0),
            (21, 6, 3, 0), (29, 6, 4, 0), (45, 6, 5, 0), (77, 7, 6, 0), (141, 7, 32, 2)),
        Table((-2, 3, 0, 0), (-1, 3, 0, 0), (0, 1, 0, 0), (1, 3, 0, 0), (2, 3, 0, 0)),
        Table((-24, 7, 4, 0), (-8, 6, 2, 0), (-4, 5, 1, 0), (-2, 4, 0, 0), (-1, 3, 0, 0), (0, 1, 0, 0), (1, 3, 0, 0), (2, 4, 0, 0),
            (3, 5, 1, 0), (5, 6, 2, 0), (9, 7, 4, 0), (-25, 7, 32, 1), (25, 7, 32, 2)),
    });

    private static HuffTable Std(int n) => Standard.Value[n];

    private static HuffTable ReadTableSegment(Segment seg)
    {
        var d = seg.Data;
        int p = seg.Start;
        if (seg.End - p < 9) throw new InvalidDataException("Short JBIG2 table segment.");
        int flags = d[p];
        bool oob = (flags & 1) != 0;
        int htps = ((flags >> 1) & 7) + 1, htrs = ((flags >> 4) & 7) + 1;
        int low = ReadS32(d, p + 1), high = ReadS32(d, p + 5);
        var br = new BitReader(d, p + 9, seg.End);
        var lines = new List<HuffLine>();
        long cur = low;
        while (cur < high)
        {
            if (lines.Count > 1 << 16 || br.AtEnd) throw new InvalidDataException("Bad JBIG2 table segment.");
            int pref = (int)br.Bits(htps), range = (int)br.Bits(htrs);
            lines.Add(new HuffLine { PrefLen = pref, RangeLen = range, RangeLow = (int)cur });
            cur += 1L << range;
        }
        lines.Add(new HuffLine { PrefLen = (int)br.Bits(htps), RangeLen = 32, RangeLow = low - 1, Lower = true });
        lines.Add(new HuffLine { PrefLen = (int)br.Bits(htps), RangeLen = 32, RangeLow = high, Upper = true });
        if (oob) lines.Add(new HuffLine { PrefLen = (int)br.Bits(htps), Oob = true });
        return new HuffTable(lines);
    }

    /// <summary>MSB-first bit reader for Huffman-coded data.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _d;
        public int Pos;
        private readonly int _end;
        private int _bit;

        public BitReader(byte[] d, int pos, int end)
        {
            _d = d;
            Pos = pos;
            _end = Math.Min(end, d.Length);
        }

        public bool AtEnd => Pos >= _end;

        public int Bit()
        {
            int b = Pos < _end ? (_d[Pos] >> (7 - _bit)) & 1 : 0;
            if (++_bit == 8) { _bit = 0; Pos++; }
            return b;
        }

        public uint Bits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | (uint)Bit();
            return v;
        }

        public void Align()
        {
            if (_bit != 0) { _bit = 0; Pos++; }
        }

        public int End => _end;
    }

    private HuffTable UserTable(List<HuffTable> custom, ref int next)
    {
        if (next >= custom.Count) throw new InvalidDataException("Missing JBIG2 custom table.");
        return custom[next++];
    }

    // ------------------------------------------------------------------ symbol dictionaries (6.5)

    private sealed class ArithState
    {
        public readonly IntContext Iadh = new(), Iadw = new(), Iaex = new(), Iaai = new(), Iadt = new(), Iafs = new(),
            Iads = new(), Iait = new(), Iari = new(), Iardw = new(), Iardh = new(), Iardx = new(), Iardy = new();
        public byte[] Iaid = Array.Empty<byte>();
        public byte[] Gb = Array.Empty<byte>(), Gr = Array.Empty<byte>();
    }

    private Bitmap[] DecodeSymbolDictionary(Segment seg)
    {
        var d = seg.Data;
        int p = seg.Start;
        if (seg.End - p < 2) throw new InvalidDataException("Short JBIG2 symbol dictionary.");
        int flags = ReadU16(d, p);
        p += 2;
        bool huff = (flags & 1) != 0, refagg = (flags & 2) != 0;
        int selDh = (flags >> 2) & 3, selDw = (flags >> 4) & 3, selBm = (flags >> 6) & 1, selAgg = (flags >> 7) & 1;
        int template = (flags >> 10) & 3, rtemplate = (flags >> 12) & 1;
        var at = new sbyte[8];
        if (!huff)
        {
            int n = template == 0 ? 8 : 2;
            for (int i = 0; i < n; i++) at[i] = (sbyte)d[p + i];
            p += n;
        }
        var rat = new sbyte[4];
        if (refagg && rtemplate == 0)
        {
            for (int i = 0; i < 4; i++) rat[i] = (sbyte)d[p + i];
            p += 4;
        }
        uint numEx = ReadU32(d, p), numNew = ReadU32(d, p + 4);
        p += 8;
        if (numEx > MaxSymbols || numNew > MaxSymbols) throw new InvalidDataException("Too many JBIG2 symbols.");

        // Input symbols from referred dictionaries, and custom tables.
        var input = new List<Bitmap>();
        var custom = new List<HuffTable>();
        foreach (int r in seg.Refs)
        {
            if (_symbols.TryGetValue(r, out var s)) input.AddRange(s);
            else if (_tables.TryGetValue(r, out var t)) custom.Add(t);
        }
        int ci = 0;
        HuffTable? tDh = null, tDw = null, tBm = null, tAgg = null;
        if (huff)
        {
            tDh = selDh == 0 ? Std(4) : selDh == 1 ? Std(5) : UserTable(custom, ref ci);
            tDw = selDw == 0 ? Std(2) : selDw == 1 ? Std(3) : UserTable(custom, ref ci);
            tBm = selBm == 0 ? Std(1) : UserTable(custom, ref ci);
            tAgg = selAgg == 0 ? Std(1) : UserTable(custom, ref ci);
        }

        int total = input.Count + (int)numNew;
        var symbols = new List<Bitmap>(input);
        var mq = huff ? null : new MqDecoder(d, p, seg.End);
        var br = huff ? new BitReader(d, p, seg.End) : null;
        var st = new ArithState();
        int codeLen = CeilLog2(total);
        if (!huff)
        {
            st.Gb = new byte[GenericContextSize(template)];
            st.Gr = new byte[RefinementContextSize(rtemplate)];
            st.Iaid = new byte[1 << (codeLen + 1)];
        }
        else if (refagg) st.Gr = new byte[RefinementContextSize(rtemplate)];

        var newSyms = new List<Bitmap>();
        int hcHeight = 0;
        while (newSyms.Count < numNew)
        {
            int dh;
            if (huff ? !tDh!.Decode(br!, out dh) : !DecodeInt(mq!, st.Iadh, out dh)) throw new InvalidDataException("Bad JBIG2 height class.");
            hcHeight += dh;
            if (hcHeight < 0 || hcHeight > 1 << 16) throw new InvalidDataException("Bad JBIG2 symbol height.");
            int symWidth = 0, totWidth = 0, hcFirst = newSyms.Count;
            var widths = new List<int>();
            while (true)
            {
                int dw;
                if (huff ? !tDw!.Decode(br!, out dw) : !DecodeInt(mq!, st.Iadw, out dw)) break; // OOB ends the height class
                if (newSyms.Count >= numNew) throw new InvalidDataException("Too many JBIG2 symbols in a height class.");
                CheckData(mq, br);
                symWidth += dw;
                if (symWidth < 0 || symWidth > 1 << 16) throw new InvalidDataException("Bad JBIG2 symbol width.");
                totWidth += symWidth;
                if (!huff || refagg)
                {
                    Bitmap bmp;
                    if (!refagg)
                        bmp = DecodeGenericArith(mq!, st.Gb, symWidth, hcHeight, template, false, at);
                    else
                    {
                        int nInst;
                        if (huff ? !tAgg!.Decode(br!, out nInst) : !DecodeInt(mq!, st.Iaai, out nInst)) throw new InvalidDataException("Bad JBIG2 aggregate.");
                        var all = new List<Bitmap>(symbols);
                        all.AddRange(newSyms);
                        if (nInst > 1)
                        {
                            var tp = new TextParams
                            {
                                Huff = huff, Refine = true, W = symWidth, H = hcHeight, NumInstances = nInst, LogStrips = 0,
                                Syms = all, CodeLen = huff ? Math.Max(1, codeLen) : codeLen, DefPixel = 0, CombOp = 0,
                                Transposed = false, RefCorner = 1, DsOffset = 0, RTemplate = rtemplate, Rat = rat,
                                Mq = mq, Br = br, St = st,
                            };
                            if (huff)
                            {
                                tp.Fs = Std(6); tp.Ds = Std(8); tp.Dt = Std(11);
                                tp.Rdw = tp.Rdh = tp.Rdx = tp.Rdy = Std(15); tp.Rsize = Std(1);
                            }
                            bmp = DecodeText(tp);
                        }
                        else
                        {
                            int id, rdx, rdy;
                            if (huff)
                            {
                                id = (int)br!.Bits(Math.Max(1, codeLen));
                                Std(15).Decode(br, out rdx);
                                Std(15).Decode(br, out rdy);
                                Std(1).Decode(br, out int bmSize);
                                br.Align();
                                if ((uint)id >= (uint)all.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
                                var rmq = new MqDecoder(d, br.Pos, Math.Min(seg.End, br.Pos + Math.Max(0, bmSize)));
                                bmp = DecodeRefinement(rmq, st.Gr, symWidth, hcHeight, rtemplate, all[id], rdx, rdy, false, rat);
                                br.Pos += Math.Max(0, bmSize);
                            }
                            else
                            {
                                id = DecodeIaid(mq!, st.Iaid, codeLen);
                                DecodeInt(mq!, st.Iardx, out rdx);
                                DecodeInt(mq!, st.Iardy, out rdy);
                                if ((uint)id >= (uint)all.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
                                bmp = DecodeRefinement(mq!, st.Gr, symWidth, hcHeight, rtemplate, all[id], rdx, rdy, false, rat);
                            }
                        }
                    }
                    newSyms.Add(bmp);
                }
                else
                {
                    widths.Add(symWidth);
                    newSyms.Add(null!); // filled from the collective bitmap below
                }
            }
            if (huff && !refagg)
            {
                // The height class's symbols are stored side by side in one collective bitmap.
                tBm!.Decode(br!, out int bmSize);
                br!.Align();
                Bitmap collective;
                if (bmSize == 0)
                {
                    int rowBytes = (totWidth + 7) / 8;
                    if ((long)rowBytes * hcHeight > seg.End - br.Pos) throw new InvalidDataException("Short JBIG2 collective bitmap.");
                    collective = NewBitmap(totWidth, hcHeight);
                    for (int y = 0; y < hcHeight; y++)
                        for (int x = 0; x < totWidth; x++)
                        {
                            int bi = br.Pos + y * rowBytes + (x >> 3);
                            collective.P[y * totWidth + x] = (byte)(bi < seg.End ? d[bi] >> (7 - (x & 7)) & 1 : 0);
                        }
                    br.Pos += rowBytes * hcHeight;
                }
                else
                {
                    collective = DecodeGenericMmr(d[..Math.Min(seg.End, br.Pos + bmSize)], br.Pos, totWidth, hcHeight, out _);
                    br.Pos += bmSize;
                }
                int x0 = 0;
                for (int i = 0; i < widths.Count; i++)
                {
                    newSyms[hcFirst + i] = Crop(collective, x0, 0, widths[i], hcHeight);
                    x0 += widths[i];
                }
            }
        }

        // Exported symbols: alternating runs of not-exported and exported symbols.
        var exported = new List<Bitmap>();
        var allSyms = new List<Bitmap>(symbols);
        allSyms.AddRange(newSyms);
        int index = 0;
        bool exportFlag = false;
        for (int guard = 0; index < allSyms.Count && guard <= 2 * allSyms.Count + 2; guard++)
        {
            int run;
            if (huff ? !Std(1).Decode(br!, out run) : !DecodeInt(mq!, st.Iaex, out run)) break;
            if (run < 0 || run > allSyms.Count - index) throw new InvalidDataException("Bad JBIG2 export run.");
            if (exportFlag) for (int i = 0; i < run; i++) exported.Add(allSyms[index + i]);
            index += run;
            exportFlag = !exportFlag;
        }
        return exported.ToArray();
    }

    private static int CeilLog2(int v)
    {
        int n = 0;
        while ((1L << n) < v) n++;
        return n;
    }

    // ------------------------------------------------------------------ text regions (6.4)

    private sealed class TextParams
    {
        public bool Huff, Refine, Transposed;
        public int W, H, NumInstances, LogStrips, CodeLen, DefPixel, CombOp, RefCorner, DsOffset, RTemplate;
        public List<Bitmap> Syms = null!;
        public sbyte[] Rat = new sbyte[4];
        public MqDecoder? Mq;
        public BitReader? Br;
        public ArithState St = null!;
        public HuffTable? Fs, Ds, Dt, Rdw, Rdh, Rdx, Rdy, Rsize, SymCodes;
        public byte[]? Data;
        public int End;
    }

    private Bitmap DecodeText(TextParams t)
    {
        var region = NewBitmap(t.W, t.H);
        region.Fill(t.DefPixel);
        int strips = 1 << t.LogStrips;
        var mq = t.Mq;
        var br = t.Br;
        var st = t.St;

        int Int(IntContext ic, HuffTable? table, out bool oob)
        {
            int v;
            oob = t.Huff ? !table!.Decode(br!, out v) : !DecodeInt(mq!, ic, out v);
            return v;
        }

        int stript = -Int(st.Iadt, t.Dt, out _) * strips;
        int firsts = 0;
        int instances = 0;
        while (instances < t.NumInstances)
        {
            int dt = Int(st.Iadt, t.Dt, out bool dtOob);
            if (dtOob) throw new InvalidDataException("Bad JBIG2 strip.");
            stript += dt * strips;
            bool first = true;
            int curs = 0;
            while (true)
            {
                if (first)
                {
                    int dfs = Int(st.Iafs, t.Fs, out _);
                    firsts += dfs;
                    curs = firsts;
                    first = false;
                }
                else
                {
                    int ids = Int(st.Iads, t.Ds, out bool oob);
                    if (oob) break;
                    curs += ids + t.DsOffset;
                }
                if (instances >= t.NumInstances) break;
                CheckData(mq, br);
                int curt = strips == 1 ? 0 : t.Huff ? (int)br!.Bits(t.LogStrips) : Int(st.Iait, null, out _);
                int tt = stript + curt;
                int id;
                if (t.Huff)
                {
                    if (t.SymCodes != null) t.SymCodes.Decode(br!, out id);
                    else id = (int)br!.Bits(t.CodeLen);
                }
                else id = DecodeIaid(mq!, st.Iaid, t.CodeLen);
                if ((uint)id >= (uint)t.Syms.Count) throw new InvalidDataException("Bad JBIG2 symbol id.");
                int ri = 0;
                if (t.Refine) ri = t.Huff ? br!.Bit() : Int(st.Iari, null, out _);
                Bitmap ib;
                if (ri == 0) ib = t.Syms[id];
                else
                {
                    int rdw = Int(st.Iardw, t.Rdw, out _), rdh = Int(st.Iardh, t.Rdh, out _);
                    int rdx = Int(st.Iardx, t.Rdx, out _), rdy = Int(st.Iardy, t.Rdy, out _);
                    var ibo = t.Syms[id];
                    int w = ibo.W + rdw, h = ibo.H + rdh;
                    if (w < 0 || h < 0) throw new InvalidDataException("Bad JBIG2 refinement size.");
                    int dx = (rdw >> 1) + rdx, dy = (rdh >> 1) + rdy;
                    if (t.Huff)
                    {
                        t.Rsize!.Decode(br!, out int bmSize);
                        br!.Align();
                        var rmq = new MqDecoder(t.Data!, br.Pos, Math.Min(t.End, br.Pos + Math.Max(0, bmSize)));
                        ib = DecodeRefinement(rmq, st.Gr, w, h, t.RTemplate, ibo, dx, dy, false, t.Rat);
                        br.Pos += Math.Max(0, bmSize);
                    }
                    else ib = DecodeRefinement(mq!, st.Gr, w, h, t.RTemplate, ibo, dx, dy, false, t.Rat);
                }
                int wi = ib.W, hi = ib.H;
                // REFCORNER: 0 bottom-left, 1 top-left, 2 bottom-right, 3 top-right.
                bool right = t.RefCorner is 2 or 3, bottom = t.RefCorner is 0 or 2;
                if (!t.Transposed && right) curs += wi - 1;
                else if (t.Transposed && bottom) curs += hi - 1;
                int s = curs;
                int x, y;
                if (!t.Transposed)
                {
                    x = right ? s - wi + 1 : s;
                    y = bottom ? tt - hi + 1 : tt;
                }
                else
                {
                    x = right ? tt - wi + 1 : tt;
                    y = bottom ? s - hi + 1 : s;
                }
                Work((long)wi * hi + 16);
                region.Compose(ib, x, y, t.CombOp);
                if (!t.Transposed && !right) curs += wi - 1;
                else if (t.Transposed && !bottom) curs += hi - 1;
                instances++;
            }
        }
        return region;
    }

    private Bitmap? DecodeTextRegionSegment(Segment seg, out RegionInfo info)
    {
        var d = seg.Data;
        int p = seg.Start;
        info = ReadRegionInfo(d, p);
        p += 17;
        int flags = ReadU16(d, p);
        p += 2;
        var t = new TextParams
        {
            Huff = (flags & 1) != 0,
            Refine = (flags & 2) != 0,
            LogStrips = (flags >> 2) & 3,
            RefCorner = (flags >> 4) & 3,
            Transposed = (flags & 0x40) != 0,
            CombOp = (flags >> 7) & 3,
            DefPixel = (flags >> 9) & 1,
            DsOffset = (flags >> 10 & 0x1F) >= 16 ? (flags >> 10 & 0x1F) - 32 : flags >> 10 & 0x1F,
            RTemplate = (flags >> 15) & 1,
            W = info.W,
            H = info.H,
            Data = d,
            End = seg.End,
        };
        int hflags = 0;
        if (t.Huff)
        {
            hflags = ReadU16(d, p);
            p += 2;
        }
        if (t.Refine && t.RTemplate == 0)
        {
            for (int i = 0; i < 4; i++) t.Rat[i] = (sbyte)d[p + i];
            p += 4;
        }
        t.NumInstances = (int)Math.Min(ReadU32(d, p), int.MaxValue);
        p += 4;

        var custom = new List<HuffTable>();
        var syms = new List<Bitmap>();
        foreach (int r in seg.Refs)
        {
            if (_symbols.TryGetValue(r, out var s)) syms.AddRange(s);
            else if (_tables.TryGetValue(r, out var tb)) custom.Add(tb);
        }
        t.Syms = syms;
        if (info.W <= 0 || info.H <= 0) return null;
        var st = new ArithState();
        t.St = st;
        if (t.Huff)
        {
            int ci = 0;
            int fs = hflags & 3, ds = (hflags >> 2) & 3, dt = (hflags >> 4) & 3;
            int rdw = (hflags >> 6) & 3, rdh = (hflags >> 8) & 3, rdx = (hflags >> 10) & 3, rdy = (hflags >> 12) & 3, rsize = (hflags >> 14) & 1;
            t.Fs = fs == 0 ? Std(6) : fs == 1 ? Std(7) : UserTable(custom, ref ci);
            t.Ds = ds == 0 ? Std(8) : ds == 1 ? Std(9) : ds == 2 ? Std(10) : UserTable(custom, ref ci);
            t.Dt = dt == 0 ? Std(11) : dt == 1 ? Std(12) : dt == 2 ? Std(13) : UserTable(custom, ref ci);
            t.Rdw = rdw == 0 ? Std(14) : rdw == 1 ? Std(15) : UserTable(custom, ref ci);
            t.Rdh = rdh == 0 ? Std(14) : rdh == 1 ? Std(15) : UserTable(custom, ref ci);
            t.Rdx = rdx == 0 ? Std(14) : rdx == 1 ? Std(15) : UserTable(custom, ref ci);
            t.Rdy = rdy == 0 ? Std(14) : rdy == 1 ? Std(15) : UserTable(custom, ref ci);
            t.Rsize = rsize == 0 ? Std(1) : UserTable(custom, ref ci);
            var br = new BitReader(d, p, seg.End);
            t.Br = br;
            t.SymCodes = ReadSymbolCodes(br, syms.Count);
            br.Align();
            st.Gr = new byte[RefinementContextSize(t.RTemplate)];
        }
        else
        {
            t.CodeLen = CeilLog2(syms.Count);
            st.Iaid = new byte[1 << (t.CodeLen + 1)];
            st.Gr = new byte[RefinementContextSize(t.RTemplate)];
            t.Mq = new MqDecoder(d, p, seg.End);
        }
        return DecodeText(t);
    }

    private static HuffTable ReadSymbolCodes(BitReader br, int numSyms)
    {
        if (numSyms > MaxSymbols * 4) throw new InvalidDataException("Too many JBIG2 symbols.");
        var runLines = new List<HuffLine>();
        for (int i = 0; i < 35; i++) runLines.Add(new HuffLine { PrefLen = (int)br.Bits(4), RangeLow = i });
        var runTable = new HuffTable(runLines);
        var lens = new int[numSyms];
        int n = 0;
        while (n < numSyms)
        {
            if (!runTable.Decode(br, out int code)) throw new InvalidDataException("Bad JBIG2 symbol code lengths.");
            int repeat, value;
            if (code < 32) { lens[n++] = code; continue; }
            if (code == 32) { if (n == 0) throw new InvalidDataException("Bad JBIG2 run code."); value = lens[n - 1]; repeat = 3 + (int)br.Bits(2); }
            else if (code == 33) { value = 0; repeat = 3 + (int)br.Bits(3); }
            else { value = 0; repeat = 11 + (int)br.Bits(7); }
            for (int i = 0; i < repeat && n < numSyms; i++) lens[n++] = value;
        }
        var lines = new List<HuffLine>(numSyms);
        for (int i = 0; i < numSyms; i++) lines.Add(new HuffLine { PrefLen = lens[i], RangeLow = i });
        return new HuffTable(lines);
    }

    // ------------------------------------------------------------------ pattern dictionaries and halftones (6.6, 6.7)

    private Bitmap[] DecodePatternDictionary(Segment seg)
    {
        var d = seg.Data;
        int p = seg.Start;
        if (seg.End - p < 7) throw new InvalidDataException("Short JBIG2 pattern dictionary.");
        int flags = d[p];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        int pw = d[p + 1], ph = d[p + 2];
        uint grayMax = ReadU32(d, p + 3);
        p += 7;
        if (pw == 0 || ph == 0 || grayMax >= MaxSymbols) throw new InvalidDataException("Bad JBIG2 pattern dictionary.");
        int n = (int)grayMax + 1;
        long cw = (long)n * pw;
        if (cw > int.MaxValue) throw new InvalidDataException("Bad JBIG2 pattern dictionary.");
        Bitmap collective;
        if (mmr) collective = DecodeGenericMmr(d[..seg.End], p, (int)cw, ph, out _);
        else
        {
            var at = new sbyte[] { (sbyte)-pw, 0, -3, -1, 2, -2, -2, -2 };
            if (pw > 128) at[0] = -128; // the template position is a signed byte
            var mq = new MqDecoder(d, p, seg.End);
            collective = DecodeGenericArith(mq, new byte[GenericContextSize(template)], (int)cw, ph, template, false, at);
        }
        var pats = new Bitmap[n];
        for (int i = 0; i < n; i++) pats[i] = Crop(collective, i * pw, 0, pw, ph);
        return pats;
    }

    private Bitmap? DecodeHalftoneRegion(Segment seg, out RegionInfo info)
    {
        var d = seg.Data;
        int p = seg.Start;
        info = ReadRegionInfo(d, p);
        p += 17;
        if (seg.End - p < 21) return null;
        int flags = d[p];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool enableSkip = (flags & 8) != 0;
        int combOp = (flags >> 4) & 7;
        int defPixel = (flags >> 7) & 1;
        uint gw = ReadU32(d, p + 1), gh = ReadU32(d, p + 5);
        int gx = ReadS32(d, p + 9), gy = ReadS32(d, p + 13);
        int rx = ReadU16(d, p + 17), ry = ReadU16(d, p + 19);
        p += 21;
        Bitmap[]? pats = null;
        foreach (int r in seg.Refs) if (_patterns.TryGetValue(r, out pats)) break;
        if (pats == null || pats.Length == 0 || info.W <= 0 || info.H <= 0) return null;
        // The region is drawn on a page no larger than the image, and every grid cell covers at least a pixel of the
        // region in any real file. Both sizes come from the segment header, so without these bounds a few bytes could
        // ask for gigabytes: the region, the skip bitmap, the bit-planes and the gray-scale values all scale with them.
        // Each side is checked before the product, which two 32-bit sides could overflow even as a long.
        long regionPixels = (long)info.W * info.H;
        if (regionPixels > _imagePixels || gw == 0 || gh == 0 || gw > regionPixels || gh > regionPixels) return null;
        long cells = (long)gw * gh;
        if (cells > regionPixels) return null;
        int ngw = (int)gw, ngh = (int)gh;
        Work(cells * sizeof(int)); // the gray-scale values below
        var region = NewBitmap(info.W, info.H);
        region.Fill(defPixel);
        int hpw = pats[0].W, hph = pats[0].H;

        Bitmap? skip = null;
        if (enableSkip)
        {
            skip = NewBitmap(ngw, ngh);
            for (int mg = 0; mg < ngh; mg++)
                for (int ng = 0; ng < ngw; ng++)
                {
                    long x = (gx + (long)mg * ry + (long)ng * rx) >> 8, y = (gy + (long)mg * rx - (long)ng * ry) >> 8;
                    if (x + hpw <= 0 || x >= info.W || y + hph <= 0 || y >= info.H) skip.P[mg * ngw + ng] = 1;
                }
        }

        // Gray-scale image: Gray-coded bit-planes, most significant first (C.5).
        int bpp = CeilLog2(pats.Length);
        var gray = new int[ngw * ngh];
        if (bpp > 0)
        {
            Bitmap? prev = null;
            var at = template switch
            {
                0 => new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 },
                1 => new sbyte[] { 3, -1, 0, 0, 0, 0, 0, 0 },
                _ => new sbyte[] { 2, -1, 0, 0, 0, 0, 0, 0 },
            };
            var mq = mmr ? null : new MqDecoder(d, p, seg.End);
            var cx = new byte[GenericContextSize(template)];
            int pos = p;
            for (int j = bpp - 1; j >= 0; j--)
            {
                Bitmap plane;
                if (mmr)
                {
                    plane = DecodeGenericMmr(d[..seg.End], pos, ngw, ngh, out int end);
                    pos = end;
                }
                else plane = DecodeGenericArith(mq!, cx, ngw, ngh, template, false, at, skip);
                if (prev != null)
                    for (int i = 0; i < plane.P.Length; i++) plane.P[i] ^= prev.P[i];
                for (int i = 0; i < gray.Length; i++) gray[i] |= plane.P[i] << j;
                prev = plane;
            }
        }

        for (int mg = 0; mg < ngh; mg++)
            for (int ng = 0; ng < ngw; ng++)
            {
                if (skip != null && skip.P[mg * ngw + ng] != 0) continue;
                long x = (gx + (long)mg * ry + (long)ng * rx) >> 8, y = (gy + (long)mg * rx - (long)ng * ry) >> 8;
                int g = Math.Min(gray[mg * ngw + ng], pats.Length - 1);
                if (x <= -hpw || y <= -hph || x >= info.W || y >= info.H) continue;
                Work((long)hpw * hph + 4);
                region.Compose(pats[g], (int)x, (int)y, combOp);
            }
        return region;
    }
}

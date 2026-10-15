namespace PdfPecker.Rendering;

/// <summary>
/// Writes baseline JPEG (JFIF) files: YCbCr with 4:2:0 chroma subsampling below quality 90 and 4:4:4 from 90 up, the
/// IJG quantisation tables scaled by quality, and Huffman tables optimised for each image. Alpha is flattened onto a
/// matte colour because JPEG has no transparency.
/// </summary>
internal static class JpegEncoder
{
    /// <summary>The largest width or height a baseline JPEG can describe.</summary>
    public const int MaxDimension = 65535;

    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    // ITU-T T.81 Annex K tables (natural order), as scaled by the IJG library.
    private static readonly byte[] StdLuminance =
    {
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29,
        51, 87, 80, 62, 18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121,
        120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    };

    private static readonly byte[] StdChrominance =
    {
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    };

    private static readonly double[] AanScale = { 1.0, 1.387039845, 1.306562965, 1.175875602, 1.0, 0.785694958, 0.541196100, 0.275899379 };

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels, composited onto <paramref name="matte"/> (0xRRGGBB).</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, int quality, uint matte)
    {
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 1 and 100.");
        if (width > MaxDimension || height > MaxDimension)
            throw new NotSupportedException($"JPEG images are limited to {MaxDimension} pixels in each dimension; lower the Dpi or MaxDimension.");

        bool subsample = quality < 90;
        int mcu = subsample ? 16 : 8;
        int mcusX = (width + mcu - 1) / mcu, mcusY = (height + mcu - 1) / mcu;
        int lumaW = mcusX * mcu, lumaH = mcusY * mcu;
        int chromaW = subsample ? lumaW / 2 : lumaW, chromaH = subsample ? lumaH / 2 : lumaH;

        var qLuma = ScaleTable(StdLuminance, quality);
        var qChroma = ScaleTable(StdChrominance, quality);

        // Colour conversion into planes padded to whole MCUs by repeating the last row and column.
        var y = new byte[lumaW * lumaH];
        var cb = new byte[lumaW * lumaH];
        var cr = new byte[lumaW * lumaH];
        ToYCbCr(pixels, width, height, matte, y, cb, cr, lumaW, lumaH);
        if (subsample)
        {
            cb = Downsample(cb, lumaW, lumaH);
            cr = Downsample(cr, lumaW, lumaH);
        }

        // Transform and quantise every block once; the coefficients feed both the statistics and the output pass.
        var yCoef = Transform(y, lumaW, lumaH, qLuma);
        var cbCoef = Transform(cb, chromaW, chromaH, qChroma);
        var crCoef = Transform(cr, chromaW, chromaH, qChroma);
        int yBlocksX = lumaW / 8, cBlocksX = chromaW / 8;

        var scan = new Scan(yCoef, cbCoef, crCoef, yBlocksX, cBlocksX, mcusX, mcusY, subsample);
        var dcFreq = new long[2][] { new long[257], new long[257] };
        var acFreq = new long[2][] { new long[257], new long[257] };
        scan.Gather(dcFreq, acFreq);
        var dc = new[] { HuffmanTable.Optimal(dcFreq[0]), HuffmanTable.Optimal(dcFreq[1]) };
        var ac = new[] { HuffmanTable.Optimal(acFreq[0]), HuffmanTable.Optimal(acFreq[1]) };

        var output = new ByteWriter(width * height / 4 + 1024);
        output.Marker(0xD8); // SOI
        // APP0 JFIF 1.01, no density units, 1:1 aspect, no thumbnail.
        output.Marker(0xE0);
        output.Word(16);
        output.Bytes("JFIF\0"u8);
        output.Byte(1); output.Byte(1); output.Byte(0);
        output.Word(1); output.Word(1);
        output.Byte(0); output.Byte(0);
        // DQT: both tables, 8-bit, zigzag order.
        output.Marker(0xDB);
        output.Word(2 + 2 * 65);
        output.Byte(0);
        for (int k = 0; k < 64; k++) output.Byte(qLuma[ZigZag[k]]);
        output.Byte(1);
        for (int k = 0; k < 64; k++) output.Byte(qChroma[ZigZag[k]]);
        // SOF0 baseline.
        output.Marker(0xC0);
        output.Word(8 + 3 * 3);
        output.Byte(8);
        output.Word(height);
        output.Word(width);
        output.Byte(3);
        output.Byte(1); output.Byte((byte)(subsample ? 0x22 : 0x11)); output.Byte(0);
        output.Byte(2); output.Byte(0x11); output.Byte(1);
        output.Byte(3); output.Byte(0x11); output.Byte(1);
        // DHT.
        WriteTable(output, 0x00, dc[0]);
        WriteTable(output, 0x10, ac[0]);
        WriteTable(output, 0x01, dc[1]);
        WriteTable(output, 0x11, ac[1]);
        // SOS.
        output.Marker(0xDA);
        output.Word(6 + 2 * 3);
        output.Byte(3);
        output.Byte(1); output.Byte(0x00);
        output.Byte(2); output.Byte(0x11);
        output.Byte(3); output.Byte(0x11);
        output.Byte(0); output.Byte(63); output.Byte(0);
        scan.Write(output, dc, ac);
        output.Marker(0xD9); // EOI
        return output.ToArray();
    }

    private static byte[] ScaleTable(byte[] std, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var t = new byte[64];
        for (int i = 0; i < 64; i++) t[i] = (byte)Math.Clamp((std[i] * scale + 50) / 100, 1, 255);
        return t;
    }

    private static void ToYCbCr(uint[] px, int width, int height, uint matte, byte[] y, byte[] cb, byte[] cr, int stride, int paddedH)
    {
        int mr = (int)(matte >> 16) & 255, mg = (int)(matte >> 8) & 255, mb = (int)matte & 255;
        for (int row = 0; row < height; row++)
        {
            int src = row * width, dst = row * stride;
            uint last = 0;
            int ly = 0, lcb = 0, lcr = 0;
            for (int x = 0; x < width; x++)
            {
                uint p = px[src + x];
                if (x == 0 || p != last)
                {
                    int r = (int)(p & 255), g = (int)((p >> 8) & 255), b = (int)((p >> 16) & 255), a = (int)(p >> 24);
                    if (a != 255)
                    {
                        // Premultiplied colour over the matte: c + matte * (1 - alpha).
                        int inv = 255 - a;
                        r += (mr * inv + 127) / 255;
                        g += (mg * inv + 127) / 255;
                        b += (mb * inv + 127) / 255;
                        if (r > 255) r = 255;
                        if (g > 255) g = 255;
                        if (b > 255) b = 255;
                    }
                    ly = (19595 * r + 38470 * g + 7471 * b + 32768) >> 16;
                    lcb = (-11059 * r - 21709 * g + 32768 * b + (128 << 16) + 32767) >> 16;
                    lcr = (32768 * r - 27439 * g - 5329 * b + (128 << 16) + 32767) >> 16;
                    last = p;
                }
                y[dst + x] = (byte)ly;
                cb[dst + x] = (byte)lcb;
                cr[dst + x] = (byte)lcr;
            }
            for (int x = width; x < stride; x++)
            {
                y[dst + x] = (byte)ly;
                cb[dst + x] = (byte)lcb;
                cr[dst + x] = (byte)lcr;
            }
        }
        int lastRow = (height - 1) * stride;
        for (int row = height; row < paddedH; row++)
        {
            Array.Copy(y, lastRow, y, row * stride, stride);
            Array.Copy(cb, lastRow, cb, row * stride, stride);
            Array.Copy(cr, lastRow, cr, row * stride, stride);
        }
    }

    /// <summary>Halves a plane in both directions by averaging each 2x2 square.</summary>
    private static byte[] Downsample(byte[] plane, int w, int h)
    {
        int ow = w / 2, oh = h / 2;
        var o = new byte[ow * oh];
        for (int y = 0; y < oh; y++)
        {
            int a = 2 * y * w, b = a + w, d = y * ow;
            for (int x = 0; x < ow; x++, a += 2, b += 2)
                o[d + x] = (byte)((plane[a] + plane[a + 1] + plane[b] + plane[b + 1] + 1 + (x & 1)) >> 2);
        }
        return o;
    }

    /// <summary>Forward DCT (the AAN float algorithm) and quantisation of every 8x8 block, stored in zigzag order.</summary>
    private static short[] Transform(byte[] plane, int w, int h, byte[] quant)
    {
        var divisors = new float[64];
        for (int r = 0, i = 0; r < 8; r++)
            for (int c = 0; c < 8; c++, i++)
                divisors[i] = (float)(1.0 / (quant[i] * AanScale[r] * AanScale[c] * 8.0));
        int blocksX = w / 8, blocksY = h / 8;
        var coef = new short[blocksX * blocksY * 64];
        Parallel.For(0, blocksY, new ParallelOptions { MaxDegreeOfParallelism = blocksY > 32 ? Environment.ProcessorCount : 1 }, by =>
        {
            Span<float> d = stackalloc float[64];
            for (int bx = 0; bx < blocksX; bx++)
            {
                int origin = by * 8 * w + bx * 8;
                byte first = plane[origin];
                bool flat = true;
                for (int r = 0; r < 8; r++)
                {
                    int row = origin + r * w;
                    for (int c = 0; c < 8; c++)
                    {
                        byte v = plane[row + c];
                        if (v != first) flat = false;
                        d[r * 8 + c] = v - 128f;
                    }
                }
                var dst = coef.AsSpan((by * blocksX + bx) * 64, 64);
                if (flat)
                {
                    // A uniform block has only a DC term: 8 * (value - 128), scaled like the transform's output.
                    dst[0] = (short)MathF.Round((first - 128f) * 64f * divisors[0]);
                    continue;
                }
                Fdct(d);
                for (int k = 0; k < 64; k++)
                {
                    int n = ZigZag[k];
                    dst[k] = (short)MathF.Round(d[n] * divisors[n]);
                }
            }
        });
        return coef;
    }

    private static void Fdct(Span<float> d)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            int step = pass == 0 ? 1 : 8, next = pass == 0 ? 8 : 1;
            for (int line = 0; line < 8; line++)
            {
                int o = line * next;
                float d0 = d[o], d1 = d[o + step], d2 = d[o + 2 * step], d3 = d[o + 3 * step];
                float d4 = d[o + 4 * step], d5 = d[o + 5 * step], d6 = d[o + 6 * step], d7 = d[o + 7 * step];
                float tmp0 = d0 + d7, tmp7 = d0 - d7, tmp1 = d1 + d6, tmp6 = d1 - d6;
                float tmp2 = d2 + d5, tmp5 = d2 - d5, tmp3 = d3 + d4, tmp4 = d3 - d4;

                float tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
                d[o] = tmp10 + tmp11;
                d[o + 4 * step] = tmp10 - tmp11;
                float z1 = (tmp12 + tmp13) * 0.707106781f;
                d[o + 2 * step] = tmp13 + z1;
                d[o + 6 * step] = tmp13 - z1;

                tmp10 = tmp4 + tmp5;
                tmp11 = tmp5 + tmp6;
                tmp12 = tmp6 + tmp7;
                float z5 = (tmp10 - tmp12) * 0.382683433f;
                float z2 = 0.541196100f * tmp10 + z5;
                float z4 = 1.306562965f * tmp12 + z5;
                float z3 = tmp11 * 0.707106781f;
                float z11 = tmp7 + z3, z13 = tmp7 - z3;
                d[o + 5 * step] = z13 + z2;
                d[o + 3 * step] = z13 - z2;
                d[o + step] = z11 + z4;
                d[o + 7 * step] = z11 - z4;
            }
        }
    }

    private static void WriteTable(ByteWriter o, byte classAndId, HuffmanTable t)
    {
        o.Marker(0xC4);
        o.Word(2 + 1 + 16 + t.Values.Length);
        o.Byte(classAndId);
        for (int i = 1; i <= 16; i++) o.Byte(t.Bits[i]);
        foreach (var v in t.Values) o.Byte(v);
    }

    /// <summary>The blocks in scan order (MCU by MCU, Y then Cb then Cr), run through the entropy coder twice.</summary>
    private sealed class Scan
    {
        private readonly short[][] _coef;
        private readonly int[] _blocksX;
        private readonly int _mcusX, _mcusY;
        private readonly bool _subsample;

        public Scan(short[] y, short[] cb, short[] cr, int yBlocksX, int cBlocksX, int mcusX, int mcusY, bool subsample)
        {
            _coef = new[] { y, cb, cr };
            _blocksX = new[] { yBlocksX, cBlocksX, cBlocksX };
            _mcusX = mcusX;
            _mcusY = mcusY;
            _subsample = subsample;
        }

        private void ForEachBlock(Action<int, int> block)
        {
            int ySpan = _subsample ? 2 : 1;
            for (int my = 0; my < _mcusY; my++)
                for (int mx = 0; mx < _mcusX; mx++)
                {
                    for (int v = 0; v < ySpan; v++)
                        for (int h = 0; h < ySpan; h++)
                            block(0, ((my * ySpan + v) * _blocksX[0] + mx * ySpan + h) * 64);
                    int c = (my * _blocksX[1] + mx) * 64;
                    block(1, c);
                    block(2, c);
                }
        }

        public void Gather(long[][] dcFreq, long[][] acFreq)
        {
            var pred = new int[3];
            ForEachBlock((comp, offset) =>
            {
                var b = _coef[comp].AsSpan(offset, 64);
                int table = comp == 0 ? 0 : 1;
                int diff = b[0] - pred[comp];
                pred[comp] = b[0];
                dcFreq[table][BitLength(diff)]++;
                var ac = acFreq[table];
                int run = 0;
                for (int k = 1; k < 64; k++)
                {
                    int v = b[k];
                    if (v == 0) { run++; continue; }
                    while (run > 15) { ac[0xF0]++; run -= 16; }
                    ac[(run << 4) | BitLength(v)]++;
                    run = 0;
                }
                if (run > 0) ac[0]++;
            });
        }

        public void Write(ByteWriter output, HuffmanTable[] dc, HuffmanTable[] ac)
        {
            var bits = new BitWriter(output);
            var pred = new int[3];
            ForEachBlock((comp, offset) =>
            {
                var b = _coef[comp].AsSpan(offset, 64);
                int table = comp == 0 ? 0 : 1;
                int diff = b[0] - pred[comp];
                pred[comp] = b[0];
                int size = BitLength(diff);
                var dct = dc[table];
                bits.Write(dct.Code[size], dct.Size[size]);
                if (size > 0) bits.Write(Magnitude(diff, size), size);
                var act = ac[table];
                int run = 0;
                for (int k = 1; k < 64; k++)
                {
                    int v = b[k];
                    if (v == 0) { run++; continue; }
                    while (run > 15)
                    {
                        bits.Write(act.Code[0xF0], act.Size[0xF0]);
                        run -= 16;
                    }
                    int s = BitLength(v);
                    int sym = (run << 4) | s;
                    bits.Write(act.Code[sym], act.Size[sym]);
                    bits.Write(Magnitude(v, s), s);
                    run = 0;
                }
                if (run > 0) bits.Write(act.Code[0], act.Size[0]);
            });
            bits.Flush();
        }

        private static int BitLength(int v)
        {
            uint a = (uint)Math.Abs(v);
            return a == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(a);
        }

        private static uint Magnitude(int v, int size) => (uint)(v < 0 ? v - 1 : v) & ((1u << size) - 1);
    }

    /// <summary>A Huffman table built from symbol frequencies (ITU-T T.81 Annex K.2), limited to 16-bit codes.</summary>
    private sealed class HuffmanTable
    {
        public readonly byte[] Bits = new byte[17];
        public byte[] Values = Array.Empty<byte>();
        public readonly uint[] Code = new uint[256];
        public readonly byte[] Size = new byte[256];

        public static HuffmanTable Optimal(long[] frequencies)
        {
            var freq = (long[])frequencies.Clone();
            freq[256] = 1; // reserves the all-ones code point
            bool any = false;
            for (int i = 0; i < 256 && !any; i++) any = freq[i] != 0;
            if (!any) freq[0] = 1; // a table needs at least one symbol
            var codeSize = new int[257];
            var others = new int[257];
            Array.Fill(others, -1);
            while (true)
            {
                int c1 = -1, c2 = -1;
                long v = long.MaxValue, v2 = long.MaxValue;
                for (int i = 0; i <= 256; i++)
                    if (freq[i] != 0 && freq[i] <= v) { v = freq[i]; c1 = i; }
                for (int i = 0; i <= 256; i++)
                    if (freq[i] != 0 && freq[i] <= v2 && i != c1) { v2 = freq[i]; c2 = i; }
                if (c2 < 0) break;
                freq[c1] += freq[c2];
                freq[c2] = 0;
                codeSize[c1]++;
                while (others[c1] >= 0) { c1 = others[c1]; codeSize[c1]++; }
                others[c1] = c2;
                codeSize[c2]++;
                while (others[c2] >= 0) { c2 = others[c2]; codeSize[c2]++; }
            }
            var bits = new int[33];
            for (int i = 0; i <= 256; i++)
                if (codeSize[i] > 0) bits[codeSize[i]]++;
            // Limit code lengths to 16 bits (Annex K.3 figure K.3).
            for (int i = 32; i > 16; i--)
            {
                while (bits[i] > 0)
                {
                    int j = i - 2;
                    while (bits[j] == 0) j--;
                    bits[i] -= 2;
                    bits[i - 1]++;
                    bits[j + 1] += 2;
                    bits[j]--;
                }
            }
            int last = 16;
            while (bits[last] == 0) last--;
            bits[last]--; // drop the reserved code point

            var t = new HuffmanTable();
            for (int i = 1; i <= 16; i++) t.Bits[i] = (byte)bits[i];
            var values = new List<byte>();
            for (int len = 1; len <= 32; len++)
                for (int s = 0; s < 256; s++)
                    if (codeSize[s] == len) values.Add((byte)s);
            t.Values = values.ToArray();
            // Canonical codes.
            uint code = 0;
            int k = 0;
            for (int len = 1; len <= 16; len++)
            {
                for (int n = 0; n < t.Bits[len]; n++, k++)
                {
                    t.Code[t.Values[k]] = code++;
                    t.Size[t.Values[k]] = (byte)len;
                }
                code <<= 1;
            }
            return t;
        }
    }

    private sealed class ByteWriter
    {
        private byte[] _buf;
        private int _len;

        public ByteWriter(int capacity) => _buf = new byte[Math.Max(capacity, 4096)];

        public void Byte(byte b)
        {
            if (_len == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            _buf[_len++] = b;
        }

        public void Word(int v)
        {
            Byte((byte)(v >> 8));
            Byte((byte)v);
        }

        public void Marker(byte m)
        {
            Byte(0xFF);
            Byte(m);
        }

        public void Bytes(ReadOnlySpan<byte> b)
        {
            foreach (var x in b) Byte(x);
        }

        public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    }

    /// <summary>Most-significant-bit-first entropy-coded data, with a zero byte stuffed after every 0xFF.</summary>
    private sealed class BitWriter
    {
        private readonly ByteWriter _out;
        private ulong _acc;
        private int _count;

        public BitWriter(ByteWriter output) => _out = output;

        public void Write(uint code, int size)
        {
            _acc = (_acc << size) | code;
            _count += size;
            while (_count >= 8)
            {
                _count -= 8;
                byte b = (byte)(_acc >> _count);
                _out.Byte(b);
                if (b == 0xFF) _out.Byte(0);
            }
        }

        public void Flush()
        {
            // Pad the last byte with one bits.
            if (_count > 0) Write((1u << (8 - _count)) - 1, 8 - _count);
        }
    }
}

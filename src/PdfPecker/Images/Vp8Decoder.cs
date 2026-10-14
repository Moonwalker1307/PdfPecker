namespace PdfPecker.Images;

/// <summary>
/// Decoder for lossy WebP: one VP8 key frame (RFC 6386), converted from YUV 4:2:0 to RGB with the same fixed-point
/// arithmetic and chroma upsampling as libwebp, so the pixels match what browsers show. It follows libwebp's decoder
/// closely and is covered by libwebp's BSD license (see THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed class Vp8Decoder
{
    private const int Bps = 32; // stride of the work buffer
    private const int YOff = Bps + 8, UOff = YOff + Bps * 16 + Bps, VOff = UOff + 16;

    // Intra modes. The 16x16 and chroma modes share the numbers of their 4x4 counterparts.
    private const int BDcPred = 0, BTmPred = 1, BVePred = 2, BHePred = 3, BRdPred = 4, BVrPred = 5, BLdPred = 6, BVlPred = 7, BHdPred = 8, BHuPred = 9;
    private const int DcPred = BDcPred, VPred = BVePred, HPred = BHePred, TmPred = BTmPred;
    private const int DcPredNoTop = 4, DcPredNoLeft = 5, DcPredNoTopLeft = 6; // DC variants at the picture edges

    private static readonly byte[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };
    private static readonly byte[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    private static readonly byte[][] Cat3456 =
    {
        new byte[] { 173, 148, 140 },
        new byte[] { 176, 155, 140, 135 },
        new byte[] { 180, 157, 141, 134, 130 },
        new byte[] { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129 },
    };

    private struct Quant { public int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc; }
    private struct FilterInfo { public int Limit, ILevel, HevThresh; public bool Inner; }

    private int _mbW, _mbH;
    private readonly Quant[] _quant = new Quant[4];
    private readonly byte[] _proba = new byte[4 * 8 * 3 * 11];
    private readonly byte[] _segmentProba = { 255, 255, 255 };
    private bool _useSegments, _updateMap, _absoluteDelta = true;
    private readonly int[] _segQuant = new int[4], _segFilter = new int[4];
    private int _filterType, _filterLevel, _sharpness;
    private bool _useLfDelta;
    private readonly int[] _refLfDelta = new int[4], _modeLfDelta = new int[4];
    private bool _useSkipProba;
    private int _skipProba;
    private readonly FilterInfo[,] _filterStrengths = new FilterInfo[4, 2];

    // Per macroblock row state.
    private byte[] _intraTop = Array.Empty<byte>();   // 4 sub-block modes per macroblock column
    private readonly byte[] _intraLeft = new byte[4];
    private byte[] _nzTop = Array.Empty<byte>(), _nzDcTop = Array.Empty<byte>();
    private byte _nzLeft, _nzDcLeft;

    private readonly short[] _coeffs = new short[384];
    private readonly byte[] _ws = new byte[Bps * 17 + Bps * 9];
    private byte[] _y = Array.Empty<byte>(), _u = Array.Empty<byte>(), _v = Array.Empty<byte>();
    private int _yStride, _uvStride;
    private FilterInfo[] _mbFilter = Array.Empty<FilterInfo>();

    /// <summary>Decodes a VP8 chunk to RGB samples, or returns null when it is not a readable key frame.</summary>
    public static DecodedImage? Decode(byte[] data, int offset, int length, long maxPixels)
    {
        try
        {
            return new Vp8Decoder().Run(data, offset, length, maxPixels);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private DecodedImage? Run(byte[] d, int offset, int length, long maxPixels)
    {
        int end = Math.Min(d.Length, offset + length);
        if (end - offset < 10) return null;
        int bits = d[offset] | d[offset + 1] << 8 | d[offset + 2] << 16;
        bool keyFrame = (bits & 1) == 0;
        int profile = (bits >> 1) & 7;
        bool show = ((bits >> 4) & 1) == 1;
        int partitionLength = bits >> 5;
        if (!keyFrame || profile > 3 || !show) return null;
        int p = offset + 3;
        if (d[p] != 0x9D || d[p + 1] != 0x01 || d[p + 2] != 0x2A) return null;
        int width = (d[p + 3] | d[p + 4] << 8) & 0x3FFF, height = (d[p + 5] | d[p + 6] << 8) & 0x3FFF;
        if (width == 0 || height == 0 || (long)width * height > maxPixels) return null;
        p += 7;
        if (partitionLength > end - p) return null;
        _mbW = (width + 15) >> 4;
        _mbH = (height + 15) >> 4;
        // Every macroblock header takes more than three bits of the first partition: a few bytes cannot describe a
        // huge picture, so they are not decoded into one.
        if ((long)_mbW * _mbH > partitionLength * 32L) return null;

        var br = new BoolDecoder(d, p, partitionLength);
        int tokens = p + partitionLength;
        br.GetValue(1); // colour space
        br.GetValue(1); // clamping type
        ParseSegmentHeader(br);
        ParseFilterHeader(br);

        // Token partitions: their sizes (3 bytes each, all but the last) precede them.
        int partitions = 1 << br.GetValue(2);
        int sizesLength = 3 * (partitions - 1);
        if (end - tokens < sizesLength) return null;
        var parts = new BoolDecoder[partitions];
        int start = tokens + sizesLength;
        for (int i = 0; i < partitions - 1; i++)
        {
            int size = d[tokens + 3 * i] | d[tokens + 3 * i + 1] << 8 | d[tokens + 3 * i + 2] << 16;
            size = Math.Min(size, end - start);
            parts[i] = new BoolDecoder(d, start, size);
            start += size;
        }
        if (start >= end) return null;
        parts[partitions - 1] = new BoolDecoder(d, start, end - start);

        ParseQuant(br);
        br.GetValue(1); // refresh entropy probabilities: there is only one frame
        ParseProba(br);
        PrecomputeFilterStrengths();

        _yStride = _mbW * 16;
        _uvStride = _mbW * 8;
        _y = new byte[_yStride * _mbH * 16];
        _u = new byte[_uvStride * _mbH * 8];
        _v = new byte[_uvStride * _mbH * 8];
        _intraTop = new byte[_mbW * 4];
        _nzTop = new byte[_mbW];
        _nzDcTop = new byte[_mbW];
        _mbFilter = new FilterInfo[_mbW * _mbH];
        var modes = new byte[16];

        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            var tokenBr = parts[mbY & (partitions - 1)];
            Array.Clear(_intraLeft);
            _nzLeft = 0;
            _nzDcLeft = 0;
            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                // Macroblock header (first partition).
                int segment = 0;
                if (_updateMap)
                    segment = br.GetBit(_segmentProba[0]) == 0 ? br.GetBit(_segmentProba[1]) : br.GetBit(_segmentProba[2]) + 2;
                bool skip = _useSkipProba && br.GetBit(_skipProba) == 1;
                bool i4x4 = br.GetBit(145) == 0;
                int uvMode;
                if (!i4x4)
                {
                    int ymode = br.GetBit(156) == 1 ? (br.GetBit(128) == 1 ? TmPred : HPred) : (br.GetBit(163) == 1 ? VPred : DcPred);
                    modes[0] = (byte)ymode;
                    for (int k = 0; k < 4; k++) { _intraTop[mbX * 4 + k] = (byte)ymode; _intraLeft[k] = (byte)ymode; }
                }
                else
                {
                    for (int y = 0; y < 4; y++)
                    {
                        int ymode = _intraLeft[y];
                        for (int x = 0; x < 4; x++)
                        {
                            int prob = (_intraTop[mbX * 4 + x] * 10 + ymode) * 9;
                            ymode = ReadBMode(br, prob);
                            _intraTop[mbX * 4 + x] = (byte)ymode;
                            modes[y * 4 + x] = (byte)ymode;
                        }
                        _intraLeft[y] = (byte)ymode;
                    }
                }
                uvMode = br.GetBit(142) == 0 ? DcPred : br.GetBit(114) == 0 ? VPred : br.GetBit(183) == 1 ? TmPred : HPred;

                // Residuals (token partition).
                Array.Clear(_coeffs);
                uint nonZeroY = 0, nonZeroUv = 0;
                if (!skip)
                {
                    (nonZeroY, nonZeroUv) = ParseResiduals(tokenBr, mbX, i4x4, _quant[_useSegments ? segment : 0]);
                    skip = (nonZeroY | nonZeroUv) == 0;
                }
                else
                {
                    _nzTop[mbX] = 0;
                    _nzLeft = 0;
                    if (!i4x4) { _nzDcTop[mbX] = 0; _nzDcLeft = 0; }
                }
                if (_filterType > 0)
                {
                    var info = _filterStrengths[_useSegments ? segment : 0, i4x4 ? 1 : 0];
                    info.Inner |= !skip;
                    _mbFilter[mbY * _mbW + mbX] = info;
                }
                Reconstruct(mbX, mbY, i4x4, modes, uvMode);
            }
        }

        if (_filterType > 0)
            for (int mbY = 0; mbY < _mbH; mbY++)
                for (int mbX = 0; mbX < _mbW; mbX++) Filter(mbX, mbY, _mbFilter[mbY * _mbW + mbX]);

        return new DecodedImage(width, height, ToRgb(width, height), null);
    }

    // ------------------------------------------------------------------ headers

    private void ParseSegmentHeader(BoolDecoder br)
    {
        _useSegments = br.GetValue(1) == 1;
        if (!_useSegments) return;
        _updateMap = br.GetValue(1) == 1;
        if (br.GetValue(1) == 1)
        {
            _absoluteDelta = br.GetValue(1) == 1;
            for (int s = 0; s < 4; s++) _segQuant[s] = br.GetValue(1) == 1 ? br.GetSignedValue(7) : 0;
            for (int s = 0; s < 4; s++) _segFilter[s] = br.GetValue(1) == 1 ? br.GetSignedValue(6) : 0;
        }
        if (_updateMap)
            for (int s = 0; s < 3; s++) _segmentProba[s] = (byte)(br.GetValue(1) == 1 ? br.GetValue(8) : 255);
    }

    private void ParseFilterHeader(BoolDecoder br)
    {
        bool simple = br.GetValue(1) == 1;
        _filterLevel = br.GetValue(6);
        _sharpness = br.GetValue(3);
        _useLfDelta = br.GetValue(1) == 1;
        if (_useLfDelta && br.GetValue(1) == 1)
        {
            for (int i = 0; i < 4; i++) if (br.GetValue(1) == 1) _refLfDelta[i] = br.GetSignedValue(6);
            for (int i = 0; i < 4; i++) if (br.GetValue(1) == 1) _modeLfDelta[i] = br.GetSignedValue(6);
        }
        _filterType = _filterLevel == 0 ? 0 : simple ? 1 : 2;
    }

    private void ParseQuant(BoolDecoder br)
    {
        int baseQ = br.GetValue(7);
        int dqY1Dc = br.GetValue(1) == 1 ? br.GetSignedValue(4) : 0;
        int dqY2Dc = br.GetValue(1) == 1 ? br.GetSignedValue(4) : 0;
        int dqY2Ac = br.GetValue(1) == 1 ? br.GetSignedValue(4) : 0;
        int dqUvDc = br.GetValue(1) == 1 ? br.GetSignedValue(4) : 0;
        int dqUvAc = br.GetValue(1) == 1 ? br.GetSignedValue(4) : 0;
        static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;
        for (int i = 0; i < 4; i++)
        {
            int q;
            if (_useSegments)
            {
                q = _segQuant[i];
                if (!_absoluteDelta) q += baseQ;
            }
            else if (i > 0)
            {
                _quant[i] = _quant[0];
                continue;
            }
            else q = baseQ;
            ref var m = ref _quant[i];
            m.Y1Dc = Vp8Tables.DcTable[Clip(q + dqY1Dc, 127)];
            m.Y1Ac = Vp8Tables.AcTable[Clip(q, 127)];
            m.Y2Dc = Vp8Tables.DcTable[Clip(q + dqY2Dc, 127)] * 2;
            m.Y2Ac = Vp8Tables.AcTable[Clip(q + dqY2Ac, 127)] * 101581 >> 16; // x * 155 / 100
            if (m.Y2Ac < 8) m.Y2Ac = 8;
            m.UvDc = Vp8Tables.DcTable[Clip(q + dqUvDc, 117)];
            m.UvAc = Vp8Tables.AcTable[Clip(q + dqUvAc, 127)];
        }
    }

    private void ParseProba(BoolDecoder br)
    {
        for (int i = 0; i < _proba.Length; i++)
            _proba[i] = (byte)(br.GetBit(Vp8Tables.CoeffsUpdateProba[i]) == 1 ? br.GetValue(8) : Vp8Tables.CoeffsProba0[i]);
        _useSkipProba = br.GetValue(1) == 1;
        if (_useSkipProba) _skipProba = br.GetValue(8);
    }

    private void PrecomputeFilterStrengths()
    {
        if (_filterType == 0) return;
        for (int s = 0; s < 4; s++)
        {
            int baseLevel = _filterLevel;
            if (_useSegments)
            {
                baseLevel = _segFilter[s];
                if (!_absoluteDelta) baseLevel += _filterLevel;
            }
            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refLfDelta[0];
                    if (i4x4 == 1) level += _modeLfDelta[0];
                }
                level = level < 0 ? 0 : level > 63 ? 63 : level;
                var info = new FilterInfo { Inner = i4x4 == 1 };
                if (level > 0)
                {
                    int ilevel = level;
                    if (_sharpness > 0)
                    {
                        ilevel >>= _sharpness > 4 ? 2 : 1;
                        if (ilevel > 9 - _sharpness) ilevel = 9 - _sharpness;
                    }
                    if (ilevel < 1) ilevel = 1;
                    info.ILevel = ilevel;
                    info.Limit = 2 * level + ilevel;
                    info.HevThresh = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                }
                _filterStrengths[s, i4x4] = info;
            }
        }
    }

    private static int ReadBMode(BoolDecoder br, int p)
    {
        var t = Vp8Tables.BModesProba;
        if (br.GetBit(t[p]) == 0) return BDcPred;
        if (br.GetBit(t[p + 1]) == 0) return BTmPred;
        if (br.GetBit(t[p + 2]) == 0) return BVePred;
        if (br.GetBit(t[p + 3]) == 0)
            return br.GetBit(t[p + 4]) == 0 ? BHePred : br.GetBit(t[p + 5]) == 0 ? BRdPred : BVrPred;
        if (br.GetBit(t[p + 6]) == 0) return BLdPred;
        if (br.GetBit(t[p + 7]) == 0) return BVlPred;
        return br.GetBit(t[p + 8]) == 0 ? BHdPred : BHuPred;
    }

    // ------------------------------------------------------------------ residuals

    private static int BandBase(int type, int n) => (type * 8 + Bands[n]) * 33;

    private (uint Y, uint Uv) ParseResiduals(BoolDecoder br, int mbX, bool i4x4, in Quant q)
    {
        int first, acType;
        if (!i4x4)
        {
            // The DC of the 16 luma blocks, coded apart and spread by an inverse Walsh-Hadamard transform.
            Span<short> dc = stackalloc short[16];
            dc.Clear();
            int ctx = _nzDcTop[mbX] + _nzDcLeft;
            int nz = GetCoeffs(br, 1, ctx, q.Y2Dc, q.Y2Ac, 0, dc);
            byte flag = (byte)(nz > 0 ? 1 : 0);
            _nzDcTop[mbX] = flag;
            _nzDcLeft = flag;
            TransformWht(dc, _coeffs);
            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        uint nonZeroY = 0, nonZeroUv = 0;
        uint tnz = (uint)(_nzTop[mbX] & 0x0F), lnz = (uint)(_nzLeft & 0x0F);
        int dst = 0;
        for (int y = 0; y < 4; y++)
        {
            uint l = lnz & 1, nzCoeffs = 0;
            for (int x = 0; x < 4; x++)
            {
                int ctx = (int)(l + (tnz & 1));
                int nz = GetCoeffs(br, acType, ctx, q.Y1Dc, q.Y1Ac, first, _coeffs.AsSpan(dst, 16));
                l = nz > first ? 1u : 0u;
                tnz = (tnz >> 1) | (l << 7);
                nzCoeffs = NzCodeBits(nzCoeffs, nz, _coeffs[dst] != 0);
                dst += 16;
            }
            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
            nonZeroY = (nonZeroY << 8) | nzCoeffs;
        }
        uint outTnz = tnz, outLnz = lnz >> 4;

        for (int ch = 0; ch < 4; ch += 2)
        {
            uint nzCoeffs = 0;
            tnz = (uint)(_nzTop[mbX] >> (4 + ch));
            lnz = (uint)(_nzLeft >> (4 + ch));
            for (int y = 0; y < 2; y++)
            {
                uint l = lnz & 1;
                for (int x = 0; x < 2; x++)
                {
                    int ctx = (int)(l + (tnz & 1));
                    int nz = GetCoeffs(br, 2, ctx, q.UvDc, q.UvAc, 0, _coeffs.AsSpan(dst, 16));
                    l = nz > 0 ? 1u : 0u;
                    tnz = (tnz >> 1) | (l << 3);
                    nzCoeffs = NzCodeBits(nzCoeffs, nz, _coeffs[dst] != 0);
                    dst += 16;
                }
                tnz >>= 2;
                lnz = (lnz >> 1) | (l << 5);
            }
            nonZeroUv |= nzCoeffs << (4 * ch);
            outTnz |= (tnz << 4) << ch;
            outLnz |= (lnz & 0xF0) << ch;
        }
        _nzTop[mbX] = (byte)outTnz;
        _nzLeft = (byte)outLnz;
        return (nonZeroY, nonZeroUv);
    }

    private static uint NzCodeBits(uint nzCoeffs, int nz, bool dcNz)
    {
        nzCoeffs <<= 2;
        nzCoeffs |= nz > 3 ? 3u : nz > 1 ? 2u : dcNz ? 1u : 0u;
        return nzCoeffs;
    }

    /// <summary>Reads the tokens of one 4x4 block from position <paramref name="n"/>; returns the position after the last non-zero one.</summary>
    private int GetCoeffs(BoolDecoder br, int type, int ctx, int dq0, int dq1, int n, Span<short> output)
    {
        var proba = _proba;
        int p = BandBase(type, n) + ctx * 11;
        for (; n < 16; n++)
        {
            if (br.GetBit(proba[p]) == 0) return n; // end of block
            while (br.GetBit(proba[p + 1]) == 0)
            {
                if (++n == 16) return 16; // zeros up to the end
                p = BandBase(type, n);
            }
            int next = BandBase(type, n + 1);
            int v;
            if (br.GetBit(proba[p + 2]) == 0)
            {
                v = 1;
                p = next + 11;
            }
            else
            {
                v = GetLargeValue(br, p);
                p = next + 22;
            }
            int signed = br.GetBit(128) == 1 ? -v : v;
            output[Zigzag[n]] = (short)(signed * (n > 0 ? dq1 : dq0));
        }
        return 16;
    }

    private int GetLargeValue(BoolDecoder br, int p)
    {
        var proba = _proba;
        int v;
        if (br.GetBit(proba[p + 3]) == 0)
        {
            v = br.GetBit(proba[p + 4]) == 0 ? 2 : 3 + br.GetBit(proba[p + 5]);
        }
        else if (br.GetBit(proba[p + 6]) == 0)
        {
            if (br.GetBit(proba[p + 7]) == 0) v = 5 + br.GetBit(159);
            else
            {
                v = 7 + 2 * br.GetBit(165);
                v += br.GetBit(145);
            }
        }
        else
        {
            int bit1 = br.GetBit(proba[p + 8]);
            int bit0 = br.GetBit(proba[p + 9 + bit1]);
            int cat = 2 * bit1 + bit0;
            v = 0;
            foreach (var prob in Cat3456[cat]) v += v + br.GetBit(prob);
            v += 3 + (8 << cat);
        }
        return v;
    }

    // ------------------------------------------------------------------ transforms

    private static void TransformWht(ReadOnlySpan<short> input, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i], a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i], a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }
        int o = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i * 4] + 3;
            int a0 = dc + tmp[3 + i * 4], a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            int a2 = tmp[1 + i * 4] - tmp[2 + i * 4], a3 = dc - tmp[3 + i * 4];
            output[o] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }

    private static int Mul1(int a) => ((a * 20091) >> 16) + a;
    private static int Mul2(int a) => (a * 35468) >> 16;

    /// <summary>Adds the inverse DCT of a 4x4 block of coefficients to the prediction at <paramref name="dst"/>.</summary>
    private void TransformAdd(int coeffs, int dst)
    {
        var c = _coeffs;
        bool any = false;
        for (int i = 0; i < 16; i++) if (c[coeffs + i] != 0) { any = true; break; }
        if (!any) return;
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int inp = coeffs + i;
            int a = c[inp] + c[inp + 8], b = c[inp] - c[inp + 8];
            int cc = Mul2(c[inp + 4]) - Mul1(c[inp + 12]);
            int d = Mul1(c[inp + 4]) + Mul2(c[inp + 12]);
            tmp[i * 4] = a + d;
            tmp[i * 4 + 1] = b + cc;
            tmp[i * 4 + 2] = b - cc;
            tmp[i * 4 + 3] = a - d;
        }
        var ws = _ws;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i] + 4;
            int a = dc + tmp[8 + i], b = dc - tmp[8 + i];
            int cc = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            int row = dst + i * Bps;
            ws[row] = Clip8(ws[row] + ((a + d) >> 3));
            ws[row + 1] = Clip8(ws[row + 1] + ((b + cc) >> 3));
            ws[row + 2] = Clip8(ws[row + 2] + ((b - cc) >> 3));
            ws[row + 3] = Clip8(ws[row + 3] + ((a - d) >> 3));
        }
    }

    private static byte Clip8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    // ------------------------------------------------------------------ reconstruction

    private void Reconstruct(int mbX, int mbY, bool i4x4, byte[] modes, int uvMode)
    {
        var ws = _ws;
        int x0 = mbX * 16, y0 = mbY * 16, cx0 = mbX * 8, cy0 = mbY * 8;

        // Edges: the row above (127 on the first row), the column to the left (129 on the first column).
        for (int j = 0; j < 16; j++) ws[YOff + j * Bps - 1] = mbX > 0 ? _y[(y0 + j) * _yStride + x0 - 1] : (byte)129;
        for (int j = 0; j < 8; j++)
        {
            ws[UOff + j * Bps - 1] = mbX > 0 ? _u[(cy0 + j) * _uvStride + cx0 - 1] : (byte)129;
            ws[VOff + j * Bps - 1] = mbX > 0 ? _v[(cy0 + j) * _uvStride + cx0 - 1] : (byte)129;
        }
        if (mbY == 0)
        {
            ws.AsSpan(YOff - Bps - 1, 21).Fill(127);
            ws.AsSpan(UOff - Bps - 1, 9).Fill(127);
            ws.AsSpan(VOff - Bps - 1, 9).Fill(127);
        }
        else
        {
            ws[YOff - Bps - 1] = mbX > 0 ? _y[(y0 - 1) * _yStride + x0 - 1] : (byte)129;
            ws[UOff - Bps - 1] = mbX > 0 ? _u[(cy0 - 1) * _uvStride + cx0 - 1] : (byte)129;
            ws[VOff - Bps - 1] = mbX > 0 ? _v[(cy0 - 1) * _uvStride + cx0 - 1] : (byte)129;
            Array.Copy(_y, (y0 - 1) * _yStride + x0, ws, YOff - Bps, 16);
            Array.Copy(_u, (cy0 - 1) * _uvStride + cx0, ws, UOff - Bps, 8);
            Array.Copy(_v, (cy0 - 1) * _uvStride + cx0, ws, VOff - Bps, 8);
        }

        if (i4x4)
        {
            int topRight = YOff - Bps + 16;
            if (mbY > 0)
            {
                if (mbX >= _mbW - 1) ws.AsSpan(topRight, 4).Fill(_y[(y0 - 1) * _yStride + x0 + 15]);
                else Array.Copy(_y, (y0 - 1) * _yStride + x0 + 16, ws, topRight, 4);
            }
            // The blocks on the right edge use the macroblock's top-right samples for their own.
            for (int k = 1; k <= 3; k++) Array.Copy(ws, topRight, ws, topRight + 4 * k * Bps, 4);
            for (int n = 0; n < 16; n++)
            {
                int dst = YOff + (n >> 2) * 4 * Bps + (n & 3) * 4;
                PredictLuma4(modes[n], dst);
                TransformAdd(n * 16, dst);
            }
        }
        else
        {
            PredictLuma16(CheckMode(mbX, mbY, modes[0]), YOff);
            for (int n = 0; n < 16; n++) TransformAdd(n * 16, YOff + (n >> 2) * 4 * Bps + (n & 3) * 4);
        }
        int uvPred = CheckMode(mbX, mbY, uvMode);
        PredictChroma8(uvPred, UOff);
        PredictChroma8(uvPred, VOff);
        for (int n = 0; n < 4; n++)
        {
            int off = (n >> 1) * 4 * Bps + (n & 1) * 4;
            TransformAdd(256 + n * 16, UOff + off);
            TransformAdd(320 + n * 16, VOff + off);
        }

        for (int j = 0; j < 16; j++) Array.Copy(ws, YOff + j * Bps, _y, (y0 + j) * _yStride + x0, 16);
        for (int j = 0; j < 8; j++)
        {
            Array.Copy(ws, UOff + j * Bps, _u, (cy0 + j) * _uvStride + cx0, 8);
            Array.Copy(ws, VOff + j * Bps, _v, (cy0 + j) * _uvStride + cx0, 8);
        }
    }

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode != BDcPred) return mode;
        if (mbX == 0) return mbY == 0 ? DcPredNoTopLeft : DcPredNoLeft;
        return mbY == 0 ? DcPredNoTop : BDcPred;
    }

    private void TrueMotion(int dst, int size)
    {
        var ws = _ws;
        int topLeft = ws[dst - Bps - 1];
        for (int y = 0; y < size; y++)
        {
            int left = ws[dst + y * Bps - 1];
            for (int x = 0; x < size; x++) ws[dst + y * Bps + x] = Clip8(ws[dst - Bps + x] + left - topLeft);
        }
    }

    private void Fill(int dst, int size, int value)
    {
        for (int y = 0; y < size; y++) _ws.AsSpan(dst + y * Bps, size).Fill((byte)value);
    }

    private void PredictLuma16(int mode, int dst)
    {
        var ws = _ws;
        switch (mode)
        {
            case TmPred: TrueMotion(dst, 16); break;
            case VPred: for (int y = 0; y < 16; y++) Array.Copy(ws, dst - Bps, ws, dst + y * Bps, 16); break;
            case HPred: for (int y = 0; y < 16; y++) ws.AsSpan(dst + y * Bps, 16).Fill(ws[dst + y * Bps - 1]); break;
            case DcPred:
            {
                int dc = 16;
                for (int j = 0; j < 16; j++) dc += ws[dst - 1 + j * Bps] + ws[dst + j - Bps];
                Fill(dst, 16, dc >> 5);
                break;
            }
            case DcPredNoTop:
            {
                int dc = 8;
                for (int j = 0; j < 16; j++) dc += ws[dst - 1 + j * Bps];
                Fill(dst, 16, dc >> 4);
                break;
            }
            case DcPredNoLeft:
            {
                int dc = 8;
                for (int j = 0; j < 16; j++) dc += ws[dst + j - Bps];
                Fill(dst, 16, dc >> 4);
                break;
            }
            default: Fill(dst, 16, 0x80); break;
        }
    }

    private void PredictChroma8(int mode, int dst)
    {
        var ws = _ws;
        switch (mode)
        {
            case TmPred: TrueMotion(dst, 8); break;
            case VPred: for (int y = 0; y < 8; y++) Array.Copy(ws, dst - Bps, ws, dst + y * Bps, 8); break;
            case HPred: for (int y = 0; y < 8; y++) ws.AsSpan(dst + y * Bps, 8).Fill(ws[dst + y * Bps - 1]); break;
            case DcPred:
            {
                int dc = 8;
                for (int j = 0; j < 8; j++) dc += ws[dst + j - Bps] + ws[dst - 1 + j * Bps];
                Fill(dst, 8, dc >> 4);
                break;
            }
            case DcPredNoTop:
            {
                int dc = 4;
                for (int j = 0; j < 8; j++) dc += ws[dst - 1 + j * Bps];
                Fill(dst, 8, dc >> 3);
                break;
            }
            case DcPredNoLeft:
            {
                int dc = 4;
                for (int j = 0; j < 8; j++) dc += ws[dst + j - Bps];
                Fill(dst, 8, dc >> 3);
                break;
            }
            default: Fill(dst, 8, 0x80); break;
        }
    }

    private static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);
    private static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    private void PredictLuma4(int mode, int dst)
    {
        var w = _ws;
        int top = dst - Bps;
        void Set(int x, int y, byte v) => w[dst + x + y * Bps] = v;
        int I = w[dst - 1], J = w[dst - 1 + Bps], K = w[dst - 1 + 2 * Bps], L = w[dst - 1 + 3 * Bps], X = w[top - 1];
        int A = w[top], B = w[top + 1], C = w[top + 2], D = w[top + 3], E = w[top + 4], F = w[top + 5], G = w[top + 6], H = w[top + 7];
        switch (mode)
        {
            case BDcPred:
            {
                int dc = 4;
                for (int i = 0; i < 4; i++) dc += w[top + i] + w[dst - 1 + i * Bps];
                dc >>= 3;
                for (int y = 0; y < 4; y++) w.AsSpan(dst + y * Bps, 4).Fill((byte)dc);
                break;
            }
            case BTmPred: TrueMotion(dst, 4); break;
            case BVePred:
            {
                byte v0 = Avg3(X, A, B), v1 = Avg3(A, B, C), v2 = Avg3(B, C, D), v3 = Avg3(C, D, E);
                for (int y = 0; y < 4; y++) { Set(0, y, v0); Set(1, y, v1); Set(2, y, v2); Set(3, y, v3); }
                break;
            }
            case BHePred:
            {
                byte r0 = Avg3(X, I, J), r1 = Avg3(I, J, K), r2 = Avg3(J, K, L), r3 = Avg3(K, L, L);
                w.AsSpan(dst, 4).Fill(r0);
                w.AsSpan(dst + Bps, 4).Fill(r1);
                w.AsSpan(dst + 2 * Bps, 4).Fill(r2);
                w.AsSpan(dst + 3 * Bps, 4).Fill(r3);
                break;
            }
            case BRdPred:
                Set(0, 3, Avg3(J, K, L));
                Set(1, 3, Avg3(I, J, K)); Set(0, 2, Avg3(I, J, K));
                Set(2, 3, Avg3(X, I, J)); Set(1, 2, Avg3(X, I, J)); Set(0, 1, Avg3(X, I, J));
                Set(3, 3, Avg3(A, X, I)); Set(2, 2, Avg3(A, X, I)); Set(1, 1, Avg3(A, X, I)); Set(0, 0, Avg3(A, X, I));
                Set(3, 2, Avg3(B, A, X)); Set(2, 1, Avg3(B, A, X)); Set(1, 0, Avg3(B, A, X));
                Set(3, 1, Avg3(C, B, A)); Set(2, 0, Avg3(C, B, A));
                Set(3, 0, Avg3(D, C, B));
                break;
            case BLdPred:
                Set(0, 0, Avg3(A, B, C));
                Set(1, 0, Avg3(B, C, D)); Set(0, 1, Avg3(B, C, D));
                Set(2, 0, Avg3(C, D, E)); Set(1, 1, Avg3(C, D, E)); Set(0, 2, Avg3(C, D, E));
                Set(3, 0, Avg3(D, E, F)); Set(2, 1, Avg3(D, E, F)); Set(1, 2, Avg3(D, E, F)); Set(0, 3, Avg3(D, E, F));
                Set(3, 1, Avg3(E, F, G)); Set(2, 2, Avg3(E, F, G)); Set(1, 3, Avg3(E, F, G));
                Set(3, 2, Avg3(F, G, H)); Set(2, 3, Avg3(F, G, H));
                Set(3, 3, Avg3(G, H, H));
                break;
            case BVrPred:
                Set(0, 0, Avg2(X, A)); Set(1, 2, Avg2(X, A));
                Set(1, 0, Avg2(A, B)); Set(2, 2, Avg2(A, B));
                Set(2, 0, Avg2(B, C)); Set(3, 2, Avg2(B, C));
                Set(3, 0, Avg2(C, D));
                Set(0, 3, Avg3(K, J, I));
                Set(0, 2, Avg3(J, I, X));
                Set(0, 1, Avg3(I, X, A)); Set(1, 3, Avg3(I, X, A));
                Set(1, 1, Avg3(X, A, B)); Set(2, 3, Avg3(X, A, B));
                Set(2, 1, Avg3(A, B, C)); Set(3, 3, Avg3(A, B, C));
                Set(3, 1, Avg3(B, C, D));
                break;
            case BVlPred:
                Set(0, 0, Avg2(A, B));
                Set(1, 0, Avg2(B, C)); Set(0, 2, Avg2(B, C));
                Set(2, 0, Avg2(C, D)); Set(1, 2, Avg2(C, D));
                Set(3, 0, Avg2(D, E)); Set(2, 2, Avg2(D, E));
                Set(0, 1, Avg3(A, B, C));
                Set(1, 1, Avg3(B, C, D)); Set(0, 3, Avg3(B, C, D));
                Set(2, 1, Avg3(C, D, E)); Set(1, 3, Avg3(C, D, E));
                Set(3, 1, Avg3(D, E, F)); Set(2, 3, Avg3(D, E, F));
                Set(3, 2, Avg3(E, F, G));
                Set(3, 3, Avg3(F, G, H));
                break;
            case BHuPred:
                Set(0, 0, Avg2(I, J));
                Set(2, 0, Avg2(J, K)); Set(0, 1, Avg2(J, K));
                Set(2, 1, Avg2(K, L)); Set(0, 2, Avg2(K, L));
                Set(1, 0, Avg3(I, J, K));
                Set(3, 0, Avg3(J, K, L)); Set(1, 1, Avg3(J, K, L));
                Set(3, 1, Avg3(K, L, L)); Set(1, 2, Avg3(K, L, L));
                Set(3, 2, (byte)L); Set(2, 2, (byte)L); Set(0, 3, (byte)L); Set(1, 3, (byte)L); Set(2, 3, (byte)L); Set(3, 3, (byte)L);
                break;
            default: // BHdPred
                Set(0, 0, Avg2(I, X)); Set(2, 1, Avg2(I, X));
                Set(0, 1, Avg2(J, I)); Set(2, 2, Avg2(J, I));
                Set(0, 2, Avg2(K, J)); Set(2, 3, Avg2(K, J));
                Set(0, 3, Avg2(L, K));
                Set(3, 0, Avg3(A, B, C));
                Set(2, 0, Avg3(X, A, B));
                Set(1, 0, Avg3(I, X, A)); Set(3, 1, Avg3(I, X, A));
                Set(1, 1, Avg3(J, I, X)); Set(3, 2, Avg3(J, I, X));
                Set(1, 2, Avg3(K, J, I)); Set(3, 3, Avg3(K, J, I));
                Set(1, 3, Avg3(L, K, J));
                break;
        }
    }

    // ------------------------------------------------------------------ loop filter

    private void Filter(int mbX, int mbY, in FilterInfo f)
    {
        int limit = f.Limit;
        if (limit == 0) return;
        int y = mbY * 16 * _yStride + mbX * 16;
        if (_filterType == 1)
        {
            if (mbX > 0) SimpleFilter16(_y, y, 1, _yStride, limit + 4);
            if (f.Inner) for (int k = 4; k < 16; k += 4) SimpleFilter16(_y, y + k, 1, _yStride, limit);
            if (mbY > 0) SimpleFilter16(_y, y, _yStride, 1, limit + 4);
            if (f.Inner) for (int k = 4; k < 16; k += 4) SimpleFilter16(_y, y + k * _yStride, _yStride, 1, limit);
            return;
        }
        int uv = mbY * 8 * _uvStride + mbX * 8;
        int il = f.ILevel, hev = f.HevThresh;
        if (mbX > 0)
        {
            FilterLoop(_y, y, 1, _yStride, 16, limit + 4, il, hev, true);
            FilterLoop(_u, uv, 1, _uvStride, 8, limit + 4, il, hev, true);
            FilterLoop(_v, uv, 1, _uvStride, 8, limit + 4, il, hev, true);
        }
        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4) FilterLoop(_y, y + k, 1, _yStride, 16, limit, il, hev, false);
            FilterLoop(_u, uv + 4, 1, _uvStride, 8, limit, il, hev, false);
            FilterLoop(_v, uv + 4, 1, _uvStride, 8, limit, il, hev, false);
        }
        if (mbY > 0)
        {
            FilterLoop(_y, y, _yStride, 1, 16, limit + 4, il, hev, true);
            FilterLoop(_u, uv, _uvStride, 1, 8, limit + 4, il, hev, true);
            FilterLoop(_v, uv, _uvStride, 1, 8, limit + 4, il, hev, true);
        }
        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4) FilterLoop(_y, y + k * _yStride, _yStride, 1, 16, limit, il, hev, false);
            FilterLoop(_u, uv + 4 * _uvStride, _uvStride, 1, 8, limit, il, hev, false);
            FilterLoop(_v, uv + 4 * _uvStride, _uvStride, 1, 8, limit, il, hev, false);
        }
    }

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;
    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    /// <summary>The simple filter across an edge: <paramref name="step"/> crosses it, <paramref name="along"/> runs along its 16 pixels.</summary>
    private static void SimpleFilter16(byte[] b, int p, int step, int along, int thresh)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < 16; i++, p += along)
            if (NeedsFilter(b, p, step, thresh2)) DoFilter2(b, p, step);
    }

    private static bool NeedsFilter(byte[] b, int p, int step, int t)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        return 4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) <= t;
    }

    private static void FilterLoop(byte[] b, int p, int step, int along, int size, int thresh, int ithresh, int hevThresh, bool edge)
    {
        int thresh2 = 2 * thresh + 1;
        for (int i = 0; i < size; i++, p += along)
        {
            int p3 = b[p - 4 * step], p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step];
            int q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step], q3 = b[p + 3 * step];
            if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > thresh2) continue;
            if (Math.Abs(p3 - p2) > ithresh || Math.Abs(p2 - p1) > ithresh || Math.Abs(p1 - p0) > ithresh
                || Math.Abs(q3 - q2) > ithresh || Math.Abs(q2 - q1) > ithresh || Math.Abs(q1 - q0) > ithresh) continue;
            if (Math.Abs(p1 - p0) > hevThresh || Math.Abs(q1 - q0) > hevThresh) DoFilter2(b, p, step);
            else if (edge) DoFilter6(b, p, step);
            else DoFilter4(b, p, step);
        }
    }

    private static void DoFilter2(byte[] b, int p, int step)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        int a = 3 * (q0 - p0) + SClip1(p1 - q1);
        int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3);
        b[p - step] = Clip8(p0 + a2);
        b[p] = Clip8(q0 - a1);
    }

    private static void DoFilter4(byte[] b, int p, int step)
    {
        int p1 = b[p - 2 * step], p0 = b[p - step], q0 = b[p], q1 = b[p + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3), a3 = (a1 + 1) >> 1;
        b[p - 2 * step] = Clip8(p1 + a3);
        b[p - step] = Clip8(p0 + a2);
        b[p] = Clip8(q0 - a1);
        b[p + step] = Clip8(q1 - a3);
    }

    private static void DoFilter6(byte[] b, int p, int step)
    {
        int p2 = b[p - 3 * step], p1 = b[p - 2 * step], p0 = b[p - step];
        int q0 = b[p], q1 = b[p + step], q2 = b[p + 2 * step];
        int a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
        int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
        b[p - 3 * step] = Clip8(p2 + a3);
        b[p - 2 * step] = Clip8(p1 + a2);
        b[p - step] = Clip8(p0 + a1);
        b[p] = Clip8(q0 - a1);
        b[p + step] = Clip8(q1 - a2);
        b[p + 2 * step] = Clip8(q2 - a3);
    }

    // ------------------------------------------------------------------ colour conversion

    private static int MultHi(int v, int coeff) => (v * coeff) >> 8;
    private static int YuvClip(int v) => (v & ~16383) == 0 ? v >> 6 : v < 0 ? 0 : 255;

    private static void YuvToRgb(int y, int u, int v, byte[] rgb, int o)
    {
        rgb[o] = (byte)YuvClip(MultHi(y, 19077) + MultHi(v, 26149) - 14234);
        rgb[o + 1] = (byte)YuvClip(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        rgb[o + 2] = (byte)YuvClip(MultHi(y, 19077) + MultHi(u, 33050) - 17685);
    }

    /// <summary>Converts the picture to RGB, interpolating the half-resolution chroma as libwebp's "fancy" upsampler does.</summary>
    private byte[] ToRgb(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        int uvRows = (height + 1) / 2;
        UpsamplePair(0, -1, 0, 0, width, rgb);
        int y = 1;
        for (; y + 1 < height; y += 2) UpsamplePair(y, y + 1, (y - 1) / 2, (y + 1) / 2, width, rgb);
        if (y < height) UpsamplePair(y, -1, Math.Min(uvRows - 1, y / 2), Math.Min(uvRows - 1, y / 2), width, rgb);
        return rgb;
    }

    /// <summary>
    /// Rows <paramref name="topRow"/> and <paramref name="bottomRow"/> (or -1) of the picture, between chroma rows
    /// <paramref name="uvTop"/> and <paramref name="uvCur"/>: the top row is nearer the first, the bottom row the second.
    /// </summary>
    private void UpsamplePair(int topRow, int bottomRow, int uvTop, int uvCur, int len, byte[] rgb)
    {
        int ty = topRow * _yStride, by = bottomRow * _yStride, tu = uvTop * _uvStride, cu = uvCur * _uvStride;
        int to = topRow * len * 3, bo = bottomRow * len * 3;
        int lastPair = (len - 1) >> 1;
        int tlU = _u[tu], tlV = _v[tu], lU = _u[cu], lV = _v[cu];
        YuvToRgb(_y[ty], (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2, rgb, to);
        if (bottomRow >= 0) YuvToRgb(_y[by], (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2, rgb, bo);
        for (int x = 1; x <= lastPair; x++)
        {
            int tU = _u[tu + x], tV = _v[tu + x], cU = _u[cu + x], cV = _v[cu + x];
            int avgU = tlU + tU + lU + cU + 8, avgV = tlV + tV + lV + cV + 8;
            int d12U = (avgU + 2 * (tU + lU)) >> 3, d12V = (avgV + 2 * (tV + lV)) >> 3;
            int d03U = (avgU + 2 * (tlU + cU)) >> 3, d03V = (avgV + 2 * (tlV + cV)) >> 3;
            YuvToRgb(_y[ty + 2 * x - 1], (d12U + tlU) >> 1, (d12V + tlV) >> 1, rgb, to + (2 * x - 1) * 3);
            YuvToRgb(_y[ty + 2 * x], (d03U + tU) >> 1, (d03V + tV) >> 1, rgb, to + 2 * x * 3);
            if (bottomRow >= 0)
            {
                YuvToRgb(_y[by + 2 * x - 1], (d03U + lU) >> 1, (d03V + lV) >> 1, rgb, bo + (2 * x - 1) * 3);
                YuvToRgb(_y[by + 2 * x], (d12U + cU) >> 1, (d12V + cV) >> 1, rgb, bo + 2 * x * 3);
            }
            tlU = tU; tlV = tV; lU = cU; lV = cV;
        }
        if ((len & 1) == 0)
        {
            YuvToRgb(_y[ty + len - 1], (3 * tlU + lU + 2) >> 2, (3 * tlV + lV + 2) >> 2, rgb, to + (len - 1) * 3);
            if (bottomRow >= 0) YuvToRgb(_y[by + len - 1], (3 * lU + tlU + 2) >> 2, (3 * lV + tlV + 2) >> 2, rgb, bo + (len - 1) * 3);
        }
    }

    // ------------------------------------------------------------------ boolean decoder

    /// <summary>The boolean entropy decoder of RFC 6386 section 7. Reading past the end yields zeros and sets <see cref="Overrun"/>.</summary>
    private sealed class BoolDecoder
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _end;
        private uint _value;
        private uint _range = 255;
        private int _bitCount;
        public bool Overrun;

        public BoolDecoder(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = Math.Min(data.Length, offset + Math.Max(0, length));
            _value = (uint)(NextByte() << 8 | NextByte());
        }

        private int NextByte()
        {
            if (_pos < _end) return _data[_pos++];
            Overrun = true;
            return 0;
        }

        public int GetBit(int prob)
        {
            uint split = 1 + (((_range - 1) * (uint)prob) >> 8);
            uint bigSplit = split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = split;
            }
            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)NextByte();
                }
            }
            return bit;
        }

        public int GetValue(int bits)
        {
            int v = 0;
            while (bits-- > 0) v |= GetBit(0x80) << bits;
            return v;
        }

        public int GetSignedValue(int bits)
        {
            int value = GetValue(bits);
            return GetValue(1) == 1 ? -value : value;
        }
    }
}

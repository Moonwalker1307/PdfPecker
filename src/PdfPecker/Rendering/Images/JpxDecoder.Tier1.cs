namespace PdfPecker.Rendering.Images;

// Tier 1 (ISO/IEC 15444-1, annex D): the bit-plane coding of each code-block, with the MQ decoder or raw bits.
internal sealed partial class JpxDecoder
{
    private static void DecodeCodeBlocks(TileComponent tc)
    {
        int style = tc.Style.CodeBlockStyle;
        var jobs = new List<(CodeBlock Block, Band Band, int BitPlanes)>();
        foreach (var res in tc.Resolutions)
            foreach (var precinct in res.Precincts)
                foreach (var part in precinct)
                {
                    var band = res.Bands[part.Band];
                    foreach (var block in part.Blocks)
                    {
                        if (block.Passes == 0 || block.Segments.Count == 0) continue;
                        int bitPlanes = band.MaxBitPlanes + tc.RoiShift - block.ZeroBitPlanes;
                        if (bitPlanes <= 0 || bitPlanes > 31) continue;
                        jobs.Add((block, band, bitPlanes));
                    }
                }
        // Code-blocks are independent, so large images decode them on several cores.
        int stride = tc.X1 - tc.X0;
        ParallelRange(jobs.Count, 64, () => new Tier1(), (i, t1) =>
        {
            var (block, band, bitPlanes) = jobs[i];
            t1.Decode(block, band, bitPlanes, style, tc.RoiShift, tc.Style.Reversible, tc.Buffer, stride);
        });
    }

    /// <summary>Decodes code-blocks, one at a time, reusing its buffers.</summary>
    private sealed class Tier1
    {
        // Per-coefficient flags: own state, significance of the 8 neighbors, and signs of the 4 direct neighbors.
        private const int Sig = 1, Neg = 2, Visit = 4, Refined = 8;
        private const int NbW = 1 << 4, NbE = 1 << 5, NbN = 1 << 6, NbS = 1 << 7, NbNW = 1 << 8, NbNE = 1 << 9, NbSW = 1 << 10, NbSE = 1 << 11;
        private const int NegW = 1 << 12, NegE = 1 << 13, NegN = 1 << 14, NegS = 1 << 15;
        private const int NbAll = 0xFF << 4;
        private const int BelowMask = ~(NbS | NbSW | NbSE | NegS); // vertically causal mode ignores the next stripe

        // The 19 contexts: 0 to 8 zero coding, 9 to 13 sign coding, 14 to 16 magnitude refinement (first refinement
        // without and with significant neighbors, then later ones), 17 run length and 18 uniform. Contexts 0, 17 and 18
        // start in other MQ states than 0 (table D.7).
        private const int ContextCount = 19;
        private const int FirstRefinementContext = 14, NeighborRefinementContext = 15, LaterRefinementContext = 16;
        private const int RunLengthContext = 17, UniformContext = 18;

        // The three passes over each bit-plane; the first bit-plane has only a cleanup pass.
        private const int SignificancePropagation = 0, MagnitudeRefinement = 1, Cleanup = 2;

        private int[] _flags = Array.Empty<int>();
        private int[] _mag = Array.Empty<int>();
        private byte[] _lastBp = Array.Empty<byte>();
        private readonly byte[] _cx = new byte[ContextCount];
        private readonly MqDecoder _mq = new();
        private int _w, _h, _fw;
        private byte[] _zc = ZcLut[0];
        private bool _vsc;

        // Raw (bypass) bit reader.
        private byte[] _raw = Array.Empty<byte>();
        private int _rawPos, _rawEnd, _rawBits, _rawCur;

        private static readonly byte[][] ZcLut = BuildZc();
        private static readonly byte[] SignLut = BuildSign(); // index: neighbor bits >> 4 & 0xFFF compressed to 8 bits

        private static byte[][] BuildZc()
        {
            var t = new byte[4][];
            for (int o = 0; o < 4; o++)
            {
                t[o] = new byte[256];
                for (int nb = 0; nb < 256; nb++)
                {
                    int f = nb << 4;
                    int h = ((f & NbW) != 0 ? 1 : 0) + ((f & NbE) != 0 ? 1 : 0);
                    int v = ((f & NbN) != 0 ? 1 : 0) + ((f & NbS) != 0 ? 1 : 0);
                    int d = ((f & NbNW) != 0 ? 1 : 0) + ((f & NbNE) != 0 ? 1 : 0) + ((f & NbSW) != 0 ? 1 : 0) + ((f & NbSE) != 0 ? 1 : 0);
                    t[o][nb] = (byte)ZcLabel(o, h, v, d);
                }
            }
            return t;
        }

        private static int ZcLabel(int orient, int h, int v, int d)
        {
            if (orient == BandHH)
            {
                int hv = h + v;
                if (d >= 3) return 8;
                if (d == 2) return hv >= 1 ? 7 : 6;
                if (d == 1) return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                return hv >= 2 ? 2 : hv == 1 ? 1 : 0;
            }
            if (orient == BandHL) (h, v) = (v, h); // HL: vertical neighbors lead
            if (h == 2) return 8;
            if (h == 1) return v >= 1 ? 7 : d >= 1 ? 6 : 5;
            if (v == 2) return 4;
            if (v == 1) return 3;
            return d >= 2 ? 2 : d == 1 ? 1 : 0;
        }

        // Sign context and XOR bit (bit 7) from the significance and signs of the 4 direct neighbors.
        private static int SignIndex(int f) =>
            (f >> 4 & 0xF) | (f >> 8 & 0xF0); // W E N S significance, then W E N S negative

        private static byte[] BuildSign()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int Contribution(int sigBit, int negBit) => (i & sigBit) == 0 ? 0 : (i & negBit) != 0 ? -1 : 1;
                int hc = Math.Clamp(Contribution(1, 16) + Contribution(2, 32), -1, 1);
                int vc = Math.Clamp(Contribution(4, 64) + Contribution(8, 128), -1, 1);
                int ctx, xor;
                if (hc == 1) { ctx = vc == 1 ? 13 : vc == 0 ? 12 : 11; xor = 0; }
                else if (hc == 0) { ctx = vc == 0 ? 9 : 10; xor = vc == -1 ? 1 : 0; }
                else { ctx = vc == 1 ? 11 : vc == 0 ? 12 : 13; xor = 1; }
                t[i] = (byte)(ctx | xor << 7);
            }
            return t;
        }

        private void ResetContexts()
        {
            Array.Clear(_cx);
            _cx[0] = 4 << 1;
            _cx[RunLengthContext] = 3 << 1;
            _cx[UniformContext] = 46 << 1;
        }

        public void Decode(CodeBlock cb, Band band, int numbps, int style, int roiShift, bool reversible, float[] dst, int stride)
        {
            _w = cb.X1 - cb.X0;
            _h = cb.Y1 - cb.Y0;
            if (_w <= 0 || _h <= 0) return;
            _fw = _w + 2;
            int fsize = _fw * (_h + 2);
            if (_flags.Length < fsize) _flags = new int[fsize];
            else Array.Clear(_flags, 0, fsize);
            int n = _w * _h;
            if (_mag.Length < n) { _mag = new int[n]; _lastBp = new byte[n]; }
            else { Array.Clear(_mag, 0, n); Array.Clear(_lastBp, 0, n); }
            _zc = ZcLut[band.Orientation];
            _vsc = (style & CodeBlockCausal) != 0;
            bool bypass = (style & CodeBlockBypass) != 0, reset = (style & CodeBlockReset) != 0, segsym = (style & CodeBlockSegmentSymbols) != 0;
            ResetContexts();

            int bp = numbps - 1;
            int kind = Cleanup;
            int passIdx = 0;
            var data = cb.Data;
            int segStart = 0;
            foreach (var seg in cb.Segments)
            {
                int passes = seg.Passes, len = seg.Length;
                int start = segStart;
                segStart += len;
                bool raw = bypass && passIdx >= ArithmeticPassesBeforeBypass && kind != Cleanup;
                if (raw)
                {
                    _raw = data;
                    _rawPos = start;
                    _rawEnd = Math.Min(start + len, cb.DataLength);
                    _rawBits = 0;
                    _rawCur = 0;
                }
                else _mq.Init(data, start, Math.Min(start + len, cb.DataLength));
                for (int k = 0; k < passes && bp >= 0; k++)
                {
                    switch (kind)
                    {
                        case SignificancePropagation: SignificancePass(bp, raw); break;
                        case MagnitudeRefinement: RefinementPass(bp, raw); break;
                        default:
                            CleanupPass(bp);
                            if (segsym)
                                for (int s = 0; s < 4; s++) _mq.Decode(_cx, UniformContext);
                            break;
                    }
                    if (reset) ResetContexts();
                    if (kind == Cleanup) { kind = SignificancePropagation; bp--; }
                    else kind++;
                    passIdx++;
                }
                if (bp < 0) break;
            }

            // Write dequantized coefficients into the band.
            int ox = cb.X0 - band.X0 + band.OffsetX, oy = cb.Y0 - band.Y0 + band.OffsetY;
            float delta = band.StepSize;
            bool scale = !reversible || delta != 1f;
            int roiThresh = roiShift > 0 && roiShift < 31 ? 1 << roiShift : int.MaxValue;
            for (int y = 0; y < _h; y++)
            {
                int fRow = (y + 1) * _fw + 1;
                int dRow = (oy + y) * stride + ox;
                int mRow = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    int m = _mag[mRow + x];
                    if (m == 0) continue;
                    bool roi = m >= roiThresh;
                    int lb = _lastBp[mRow + x];
                    if (lb > 0) m += 1 << (lb - 1); // reconstruct at the middle of the undecoded range
                    if (roi) m >>= roiShift;
                    float v = scale ? m * delta : m;
                    dst[dRow + x] = (_flags[fRow + x] & Neg) != 0 ? -v : v;
                }
            }
        }

        private int RawBit()
        {
            if (_rawBits == 0)
            {
                if (_rawCur == 0xFF)
                {
                    int b = _rawPos < _rawEnd ? _raw[_rawPos] : 0xFF;
                    if (b > 0x8F) { _rawCur = 0xFF; _rawBits = 8; }
                    else { _rawCur = b; _rawPos++; _rawBits = 7; }
                }
                else
                {
                    _rawCur = _rawPos < _rawEnd ? _raw[_rawPos] : 0xFF;
                    _rawPos++;
                    _rawBits = 8;
                }
            }
            _rawBits--;
            return (_rawCur >> _rawBits) & 1;
        }

        /// <summary>Flags as seen from row <paramref name="y"/> (the next stripe is hidden in vertically causal mode).</summary>
        private int View(int f, int y) => _vsc && (y & 3) == 3 ? f & BelowMask : f;

        private int DecodeSign(int f, bool raw)
        {
            if (raw) return RawBit();
            int e = SignLut[SignIndex(f)];
            return _mq.Decode(_cx, e & 0x1F) ^ (e >> 7);
        }

        private void BecomeSignificant(int fi, int i, int y, int bp, bool raw)
        {
            int sign = DecodeSign(View(_flags[fi], y), raw);
            var fl = _flags;
            int fw = _fw;
            fl[fi] |= Sig | (sign != 0 ? Neg : 0);
            _mag[i] = 1 << bp;
            _lastBp[i] = (byte)bp;
            // Tell the neighbors.
            if (sign != 0)
            {
                fl[fi - 1] |= NbE | NegE;
                fl[fi + 1] |= NbW | NegW;
                fl[fi - fw] |= NbS | NegS;
                fl[fi + fw] |= NbN | NegN;
            }
            else
            {
                fl[fi - 1] |= NbE;
                fl[fi + 1] |= NbW;
                fl[fi - fw] |= NbS;
                fl[fi + fw] |= NbN;
            }
            fl[fi - fw - 1] |= NbSE;
            fl[fi - fw + 1] |= NbSW;
            fl[fi + fw - 1] |= NbNE;
            fl[fi + fw + 1] |= NbNW;
        }

        private void SignificancePass(int bp, bool raw)
        {
            var fl = _flags;
            var zc = _zc;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi = (y0 + 1) * _fw + x + 1;
                    for (int y = y0; y < y1; y++, fi += _fw)
                    {
                        int f = fl[fi];
                        if ((f & Sig) != 0) continue;
                        f = View(f, y);
                        if ((f & NbAll) == 0) continue;
                        int bit = raw ? RawBit() : _mq.Decode(_cx, zc[f >> 4 & 0xFF]);
                        if (bit != 0) BecomeSignificant(fi, y * _w + x, y, bp, raw);
                        fl[fi] |= Visit;
                    }
                }
            }
        }

        private void RefinementPass(int bp, bool raw)
        {
            var fl = _flags;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi = (y0 + 1) * _fw + x + 1;
                    for (int y = y0; y < y1; y++, fi += _fw)
                    {
                        int f = fl[fi];
                        if ((f & (Sig | Visit)) != Sig) continue;
                        int bit;
                        if (raw) bit = RawBit();
                        else
                        {
                            int ctx = (f & Refined) != 0 ? LaterRefinementContext : (View(f, y) & NbAll) != 0 ? NeighborRefinementContext : FirstRefinementContext;
                            bit = _mq.Decode(_cx, ctx);
                        }
                        int i = y * _w + x;
                        if (bit != 0) _mag[i] |= 1 << bp;
                        _lastBp[i] = (byte)bp;
                        fl[fi] = f | Refined;
                    }
                }
            }
        }

        private void CleanupPass(int bp)
        {
            var fl = _flags;
            var zc = _zc;
            int fw = _fw;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi0 = (y0 + 1) * fw + x + 1;
                    int yStart = y0;
                    if (y1 - y0 == 4
                        && ((fl[fi0] | fl[fi0 + fw] | fl[fi0 + 2 * fw] | View(fl[fi0 + 3 * fw], 3)) & (Sig | Visit | NbAll)) == 0)
                    {
                        // Run mode: one decision for four insignificant coefficients with insignificant neighbors.
                        if (_mq.Decode(_cx, RunLengthContext) == 0) continue;
                        int r = _mq.Decode(_cx, UniformContext) << 1;
                        r |= _mq.Decode(_cx, UniformContext);
                        int y = y0 + r;
                        BecomeSignificant(fi0 + r * fw, y * _w + x, y, bp, false);
                        yStart = y + 1;
                    }
                    int fi = fi0 + (yStart - y0) * fw;
                    for (int y = yStart; y < y1; y++, fi += fw)
                    {
                        int f = fl[fi];
                        if ((f & (Sig | Visit)) != 0) continue;
                        if (_mq.Decode(_cx, zc[View(f, y) >> 4 & 0xFF]) != 0) BecomeSignificant(fi, y * _w + x, y, bp, false);
                    }
                }
            }
            // Clear the visited marks for the next bit-plane.
            for (int y = 0; y < _h; y++)
            {
                int f = (y + 1) * fw + 1;
                for (int x = 0; x < _w; x++) fl[f + x] &= ~Visit;
            }
        }
    }
}

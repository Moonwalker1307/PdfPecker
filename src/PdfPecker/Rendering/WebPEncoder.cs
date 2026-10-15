using System.Numerics;

namespace PdfPecker.Rendering;

/// <summary>
/// Writes lossless WebP files (a RIFF container holding one VP8L bitstream). Images with up to 256 colours use the
/// colour-indexing transform (packing 2, 4 or 8 pixels into one when the palette is small); others use the
/// subtract-green and predictor transforms. The coded pixels go through LZ77 back-references, a colour cache and
/// length-limited canonical prefix codes. Alpha is kept exactly.
/// </summary>
internal static class WebPEncoder
{
    /// <summary>The largest width or height a VP8L bitstream can describe.</summary>
    public const int MaxDimension = 16384;

    private const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40, MaxCacheBits = 10;
    private const int MaxCopyLength = 4096, MaxCopyDistance = (1 << 20) - 120, MinCopyLength = 3;
    private static readonly int[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    // Distance codes 1..120 name nearby pixels (see the decoder); this is the inverse map, indexed by row * 16 + 8 - dx.
    private static readonly byte[] PlaneToCode = CreatePlaneToCode();

    private static byte[] CreatePlaneToCode()
    {
        byte[] codeToPlane =
        {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
            0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
            0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
            0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
            0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
        };
        var map = new byte[128];
        for (int i = 0; i < codeToPlane.Length; i++) map[codeToPlane[i]] = (byte)(i + 1);
        return map;
    }

    /// <summary>How hard the encoder searches, from the 1..100 effort.</summary>
    private sealed class Settings
    {
        public int ChainLength;
        public int NiceLength;
        public int[] Modes = Array.Empty<int>();
        public int[] CacheBits = Array.Empty<int>();
        public bool TryBoth;

        // Predictor modes in the order tried; the first ones win most tiles of rendered pages.
        private static readonly int[] FewModes = { 1, 2, 11, 12 };
        private static readonly int[] SomeModes = { 1, 2, 11, 12, 13, 7, 5, 10 };
        private static readonly int[] AllModes = { 1, 2, 11, 12, 13, 7, 5, 10, 6, 8, 9, 3, 4, 0 };

        public static Settings For(int effort) => new()
        {
            ChainLength = 1 + effort / 3,
            NiceLength = effort < 50 ? 64 : 256,
            Modes = effort < 30 ? FewModes : effort < 90 ? SomeModes : AllModes,
            CacheBits = effort < 30 ? new[] { 0, 10 } : effort < 90 ? new[] { 0, 4, 7, 10 } : new[] { 0, 2, 4, 6, 8, 10 },
            TryBoth = effort >= 90,
        };
    }

    /// <summary>Encodes premultiplied 0xAABBGGRR pixels; <paramref name="effort"/> (1..100) trades speed for size.</summary>
    public static byte[] Encode(uint[] pixels, int width, int height, int effort)
    {
        if (effort is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(effort), "Quality must be between 1 and 100.");
        if (width > MaxDimension || height > MaxDimension)
            throw new NotSupportedException($"WebP images are limited to {MaxDimension} pixels in each dimension; lower the Dpi or MaxDimension.");
        var argb = ToArgb(pixels, out bool hasAlpha);
        var settings = Settings.For(effort);

        var palette = FindPalette(argb);
        byte[] best = palette != null
            ? EncodeWithPalette(argb, width, height, hasAlpha, palette, settings)
            : EncodeWithPredictor(argb, width, height, hasAlpha, settings);
        if (palette != null && settings.TryBoth)
        {
            var other = EncodeWithPredictor(argb, width, height, hasAlpha, settings);
            if (other.Length < best.Length) best = other;
        }

        // RIFF container: "RIFF" size "WEBP" then the "VP8L" chunk, padded to an even length.
        int pad = best.Length & 1;
        var file = new byte[20 + best.Length + pad];
        "RIFF"u8.CopyTo(file);
        WriteLe32(file, 4, 12 + best.Length + pad);
        "WEBPVP8L"u8.CopyTo(file.AsSpan(8));
        WriteLe32(file, 16, best.Length);
        best.CopyTo(file, 20);
        return file;
    }

    private static void WriteLe32(byte[] b, int at, int v)
    {
        b[at] = (byte)v;
        b[at + 1] = (byte)(v >> 8);
        b[at + 2] = (byte)(v >> 16);
        b[at + 3] = (byte)(v >> 24);
    }

    /// <summary>Straight-alpha 0xAARRGGBB, rounding like the PNG encoder so both formats carry the same pixels.</summary>
    private static uint[] ToArgb(uint[] px, out bool hasAlpha)
    {
        var o = new uint[px.Length];
        hasAlpha = false;
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i], a = c >> 24;
            uint r = c & 255, g = (c >> 8) & 255, b = (c >> 16) & 255;
            if (a != 255)
            {
                hasAlpha = true;
                if (a == 0) { o[i] = 0; continue; }
                r = Math.Min(255, (r * 255 + a / 2) / a);
                g = Math.Min(255, (g * 255 + a / 2) / a);
                b = Math.Min(255, (b * 255 + a / 2) / a);
            }
            o[i] = a << 24 | r << 16 | g << 8 | b;
        }
        return o;
    }

    private static VP8LWriter Header(int width, int height, bool hasAlpha, int capacity)
    {
        var w = new VP8LWriter(capacity);
        w.Write(0x2F, 8);
        w.Write((uint)width - 1, 14);
        w.Write((uint)height - 1, 14);
        w.Write(hasAlpha ? 1u : 0u, 1);
        w.Write(0, 3); // version
        return w;
    }

    // ------------------------------------------------------------------ colour indexing

    /// <summary>The distinct colours, sorted, when there are at most 256 of them; otherwise null.</summary>
    private static uint[]? FindPalette(uint[] argb)
    {
        var keys = new uint[1024];
        var used = new bool[1024];
        int count = 0;
        uint last = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            if (i > 0 && p == last) continue;
            last = p;
            int h = (int)((p * 0x1E35A7BDu) >> 22);
            while (used[h] && keys[h] != p) h = (h + 1) & 1023;
            if (used[h]) continue;
            if (++count > 256) return null;
            used[h] = true;
            keys[h] = p;
        }
        var palette = new uint[count];
        for (int i = 0, n = 0; i < keys.Length; i++)
            if (used[i]) palette[n++] = keys[i];
        Array.Sort(palette);
        return palette;
    }

    private static byte[] EncodeWithPalette(uint[] argb, int width, int height, bool hasAlpha, uint[] palette, Settings s)
    {
        int colors = palette.Length;
        var w = Header(width, height, hasAlpha, argb.Length / 8 + 1024);
        w.Write(1, 1);
        w.Write(3, 2); // colour indexing
        w.Write((uint)colors - 1, 8);
        var delta = new uint[colors];
        delta[0] = palette[0];
        for (int i = 1; i < colors; i++) delta[i] = SubPixels(palette[i], palette[i - 1]);
        EncodeImage(w, delta, colors, 1, false, s);

        // Pixels as indexes in the green channel, 2, 4 or 8 to a pixel when the palette is small.
        int bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
        int bitsPerPixel = 8 >> bits, perPixel = 1 << bits;
        int packedWidth = (width + perPixel - 1) >> bits;
        var index = new Dictionary<uint, uint>(colors);
        for (int i = 0; i < colors; i++) index[palette[i]] = (uint)i;
        var packed = new uint[packedWidth * height];
        uint lastColor = argb[0], lastIndex = index[argb[0]];
        for (int y = 0; y < height; y++)
        {
            int row = y * width, prow = y * packedWidth;
            for (int x = 0; x < width; x++)
            {
                uint p = argb[row + x];
                if (p != lastColor)
                {
                    lastColor = p;
                    lastIndex = index[p];
                }
                packed[prow + (x >> bits)] |= lastIndex << (8 + (x & (perPixel - 1)) * bitsPerPixel);
            }
        }
        for (int i = 0; i < packed.Length; i++) packed[i] |= 0xFF000000;
        w.Write(0, 1); // no more transforms
        EncodeImage(w, packed, packedWidth, height, true, s);
        return w.ToArray();
    }

    // ------------------------------------------------------------------ subtract green and prediction

    private const int PredictorBits = 4;

    private static byte[] EncodeWithPredictor(uint[] argb, int width, int height, bool hasAlpha, Settings s)
    {
        var w = Header(width, height, hasAlpha, argb.Length + 1024);
        var px = (uint[])argb.Clone();
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i], green = (p >> 8) & 0xFF;
            uint redBlue = 0xFF00FF00 + (p & 0x00FF00FF) - (green << 16 | green); // the high bytes absorb borrows
            px[i] = (p & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
        w.Write(1, 1);
        w.Write(2, 2); // subtract green

        int tilesX = (width + (1 << PredictorBits) - 1) >> PredictorBits;
        int tilesY = (height + (1 << PredictorBits) - 1) >> PredictorBits;
        var modes = new uint[tilesX * tilesY];
        var residual = new uint[px.Length];
        Parallel.For(0, tilesY, new ParallelOptions { MaxDegreeOfParallelism = tilesY > 8 ? Environment.ProcessorCount : 1 }, ty =>
        {
            for (int tx = 0; tx < tilesX; tx++)
            {
                int mode = BestMode(px, width, height, tx, ty, s.Modes);
                modes[ty * tilesX + tx] = 0xFF000000 | (uint)mode << 8;
                int x0 = tx << PredictorBits, y0 = ty << PredictorBits;
                int x1 = Math.Min(width, x0 + (1 << PredictorBits)), y1 = Math.Min(height, y0 + (1 << PredictorBits));
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * width + x;
                        residual[i] = SubPixels(px[i], PredictAt(px, i, x, y, width, mode));
                    }
            }
        });
        w.Write(1, 1);
        w.Write(0, 2); // predictor
        w.Write(PredictorBits - 2, 3);
        EncodeImage(w, modes, tilesX, tilesY, false, s);
        w.Write(0, 1); // no more transforms
        EncodeImage(w, residual, width, height, true, s);
        return w.ToArray();
    }

    private static readonly int[] ResidualCost = CreateResidualCost();

    private static int[] CreateResidualCost()
    {
        var c = new int[256];
        for (int v = 1; v < 256; v++)
        {
            int m = v < 128 ? v : 256 - v;
            c[v] = (int)(16 * (1 + Math.Log2(m)) + 0.5);
        }
        return c;
    }

    private static int BestMode(uint[] px, int width, int height, int tx, int ty, int[] modes)
    {
        int x0 = tx << PredictorBits, y0 = ty << PredictorBits;
        int x1 = Math.Min(width, x0 + (1 << PredictorBits)), y1 = Math.Min(height, y0 + (1 << PredictorBits));
        // The first row and column use fixed predictors; score the rest of the tile.
        int sx = Math.Max(x0, 1), sy = Math.Max(y0, 1);
        if (sx >= x1 || sy >= y1) return 1;
        var cost = ResidualCost;
        int best = modes[0];
        long bestCost = long.MaxValue;
        foreach (int mode in modes)
        {
            long total = 0;
            for (int y = sy; y < y1 && total < bestCost; y++)
            {
                int row = y * width;
                for (int x = sx; x < x1; x++)
                {
                    int i = row + x;
                    uint r = SubPixels(px[i], Predict(mode, px[i - 1], px[i - width], px[i - width - 1], px[i - width + 1]));
                    if (r != 0) total += cost[r & 255] + cost[(r >> 8) & 255] + cost[(r >> 16) & 255] + cost[r >> 24];
                }
            }
            if (total < bestCost)
            {
                bestCost = total;
                best = mode;
                if (total == 0) break;
            }
        }
        return best;
    }

    private static uint PredictAt(uint[] px, int i, int x, int y, int width, int mode)
    {
        if (y == 0) return x == 0 ? 0xFF000000 : px[i - 1];
        if (x == 0) return px[i - width];
        // For the last pixel of a row, "top right" is the first pixel of the current row, as in the decoder.
        return Predict(mode, px[i - 1], px[i - width], px[i - width - 1], px[i - width + 1]);
    }

    private static uint Predict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average2(Average2(left, topRight), top),
        6 => Average2(left, topLeft),
        7 => Average2(left, top),
        8 => Average2(topLeft, top),
        9 => Average2(top, topRight),
        10 => Average2(Average2(left, topLeft), Average2(top, topRight)),
        11 => Select(top, left, topLeft),
        12 => ClampedAddSubtractFull(left, top, topLeft),
        13 => ClampedAddSubtractHalf(Average2(left, top), topLeft),
        _ => 0xFF000000,
    };

    private static uint SubPixels(uint a, uint b)
    {
        uint alphaGreen = 0x00FF00FF + (a & 0xFF00FF00) - (b & 0xFF00FF00);
        uint redBlue = 0xFF00FF00 + (a & 0x00FF00FF) - (b & 0x00FF00FF);
        return (alphaGreen & 0xFF00FF00) | (redBlue & 0x00FF00FF);
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb = Sub3(a, b, c, 24) + Sub3(a, b, c, 16) + Sub3(a, b, c, 8) + Sub3(a, b, c, 0);
        return paMinusPb <= 0 ? a : b;
    }

    private static int Sub3(uint a, uint b, uint c, int shift)
    {
        int av = (int)(a >> shift) & 0xFF, bv = (int)(b >> shift) & 0xFF, cv = (int)(c >> shift) & 0xFF;
        return Math.Abs(bv - cv) - Math.Abs(av - cv);
    }

    private static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static uint ClampedAddSubtractFull(uint a, uint b, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int v = (int)(a >> shift & 0xFF) + (int)(b >> shift & 0xFF) - (int)(c >> shift & 0xFF);
            result |= (uint)Clip255(v) << shift;
        }
        return result;
    }

    private static uint ClampedAddSubtractHalf(uint avg, uint c)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            int a = (int)(avg >> shift & 0xFF), b = (int)(c >> shift & 0xFF);
            result |= (uint)Clip255(a + (a - b) / 2) << shift;
        }
        return result;
    }

    // ------------------------------------------------------------------ entropy-coded image

    /// <summary>
    /// Writes one entropy-coded image: colour cache size, (for the main image) no meta prefix codes, five prefix codes
    /// and the coded pixels.
    /// </summary>
    private static void EncodeImage(VP8LWriter w, uint[] px, int xsize, int ysize, bool level0, Settings s)
    {
        var refs = BackwardReferences(px, xsize, s);
        int cacheBits = ChooseCacheBits(px, refs, s);
        if (cacheBits > 0)
        {
            w.Write(1, 1);
            w.Write((uint)cacheBits, 4);
        }
        else w.Write(0, 1);
        if (level0) w.Write(0, 1); // a single group of prefix codes

        int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        var green = new int[NumLiteralCodes + NumLengthCodes + cacheSize];
        var red = new int[256];
        var blue = new int[256];
        var alpha = new int[256];
        var dist = new int[NumDistanceCodes];
        var cacheHit = new int[refs.Count]; // cache key of a literal found in the cache, else -1
        var cache = cacheSize > 0 ? new uint[cacheSize] : null;
        var cacheValid = cacheSize > 0 ? new bool[cacheSize] : null;
        int shift = 32 - cacheBits;
        int pos = 0;
        for (int t = 0; t < refs.Count; t++)
        {
            int len = refs.Length[t];
            if (refs.Distance[t] == 0)
            {
                uint p = px[pos++];
                cacheHit[t] = -1;
                if (cache != null)
                {
                    int key = (int)((0x1E35A7BDu * p) >> shift);
                    if (cacheValid![key] && cache[key] == p)
                    {
                        cacheHit[t] = key;
                        green[NumLiteralCodes + NumLengthCodes + key]++;
                        continue;
                    }
                    cache[key] = p;
                    cacheValid[key] = true;
                }
                green[(p >> 8) & 255]++;
                red[(p >> 16) & 255]++;
                blue[p & 255]++;
                alpha[p >> 24]++;
            }
            else
            {
                green[NumLiteralCodes + Prefix(len, out _, out _)]++;
                dist[Prefix(refs.Distance[t], out _, out _)]++;
                if (cache != null)
                    for (int k = 0; k < len; k++)
                    {
                        uint p = px[pos + k];
                        int key = (int)((0x1E35A7BDu * p) >> shift);
                        cache[key] = p;
                        cacheValid![key] = true;
                    }
                pos += len;
            }
        }

        var gCode = WriteCode(w, green);
        var rCode = WriteCode(w, red);
        var bCode = WriteCode(w, blue);
        var aCode = WriteCode(w, alpha);
        var dCode = WriteCode(w, dist);

        pos = 0;
        for (int t = 0; t < refs.Count; t++)
        {
            int len = refs.Length[t];
            if (refs.Distance[t] == 0)
            {
                uint p = px[pos++];
                if (cacheHit[t] >= 0)
                {
                    gCode.Put(w, NumLiteralCodes + NumLengthCodes + cacheHit[t]);
                    continue;
                }
                gCode.Put(w, (int)(p >> 8) & 255);
                rCode.Put(w, (int)(p >> 16) & 255);
                bCode.Put(w, (int)p & 255);
                aCode.Put(w, (int)(p >> 24));
            }
            else
            {
                int code = Prefix(len, out int extraBits, out int extra);
                gCode.Put(w, NumLiteralCodes + code);
                w.Write((uint)extra, extraBits);
                code = Prefix(refs.Distance[t], out extraBits, out extra);
                dCode.Put(w, code);
                w.Write((uint)extra, extraBits);
                pos += len;
            }
        }
    }

    /// <summary>The prefix code of a length or distance value (1 or more) with its extra bits.</summary>
    private static int Prefix(int value, out int extraBits, out int extra)
    {
        int d = value - 1;
        if (d < 4)
        {
            extraBits = extra = 0;
            return d;
        }
        int high = 31 - BitOperations.LeadingZeroCount((uint)d);
        int second = (d >> (high - 1)) & 1;
        extraBits = high - 1;
        extra = d & ((1 << extraBits) - 1);
        return 2 * high + second;
    }

    /// <summary>LZ77 parse: literals (distance 0) and copies (length, distance as a plane code).</summary>
    private sealed class Refs
    {
        public int[] Length;
        public int[] Distance;
        public int Count;

        public Refs(int capacity)
        {
            Length = new int[Math.Max(capacity, 16)];
            Distance = new int[Math.Max(capacity, 16)];
        }

        public void Add(int length, int distance)
        {
            if (Count == Length.Length)
            {
                Array.Resize(ref Length, Count * 2);
                Array.Resize(ref Distance, Count * 2);
            }
            Length[Count] = length;
            Distance[Count++] = distance;
        }
    }

    private const int HashBits = 18;

    private static Refs BackwardReferences(uint[] px, int xsize, Settings s)
    {
        int n = px.Length;
        var refs = new Refs(n < 4096 ? n : n / 4);
        if (n < 2 * MinCopyLength)
        {
            for (int i = 0; i < n; i++) refs.Add(1, 0);
            return refs;
        }
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var chain = new int[n];
        int i0 = 0;
        while (i0 < n)
        {
            int bestLen = 0, bestDist = 0;
            int maxLen = Math.Min(MaxCopyLength, n - i0);
            if (maxLen >= MinCopyLength)
            {
                // Runs (the pixel to the left) and the row above first: they are the common long matches.
                if (i0 >= 1)
                {
                    int l = MatchLength(px, i0 - 1, i0, maxLen);
                    if (l > bestLen) { bestLen = l; bestDist = 1; }
                }
                if (i0 >= xsize && xsize > 1 && bestLen < maxLen)
                {
                    int l = MatchLength(px, i0 - xsize, i0, maxLen);
                    if (l > bestLen) { bestLen = l; bestDist = xsize; }
                }
                if (bestLen < s.NiceLength && bestLen < maxLen && i0 + 1 < n)
                {
                    int cand = head[Hash(px[i0], px[i0 + 1])];
                    for (int steps = s.ChainLength; cand >= 0 && steps > 0; steps--, cand = chain[cand])
                    {
                        int d = i0 - cand;
                        if (d > MaxCopyDistance) break;
                        if (bestLen > 0 && px[cand + bestLen] != px[i0 + bestLen]) continue; // cannot beat the best
                        int l = MatchLength(px, cand, i0, maxLen);
                        if (l > bestLen)
                        {
                            bestLen = l;
                            bestDist = d;
                            if (l >= s.NiceLength || l == maxLen) break;
                        }
                    }
                }
            }
            int advance;
            if (bestLen >= MinCopyLength)
            {
                refs.Add(bestLen, PlaneCode(xsize, bestDist));
                advance = bestLen;
            }
            else
            {
                refs.Add(1, 0);
                advance = 1;
            }
            for (int k = 0; k < advance; k++, i0++)
            {
                if (i0 + 1 >= n) continue;
                int h = Hash(px[i0], px[i0 + 1]);
                chain[i0] = head[h];
                head[h] = i0;
            }
        }
        return refs;
    }

    private static int Hash(uint a, uint b) => (int)(((a * 0x1E35A7BDu) ^ (b * 0x9E3779B1u)) >> (32 - HashBits));

    private static int MatchLength(uint[] px, int from, int to, int max)
    {
        return px.AsSpan(from, max).CommonPrefixLength(px.AsSpan(to, max));
    }

    /// <summary>The distance code for a copy: one of the 120 short codes for nearby pixels, else distance + 120.</summary>
    private static int PlaneCode(int xsize, int dist)
    {
        int yOffset = dist / xsize, xOffset = dist - yOffset * xsize;
        if (xOffset <= 8 && yOffset < 8) return PlaneToCode[yOffset * 16 + 8 - xOffset];
        if (xOffset > xsize - 8 && yOffset < 7) return PlaneToCode[(yOffset + 1) * 16 + 8 + (xsize - xOffset)];
        return dist + 120;
    }

    /// <summary>Picks the colour cache size with the smallest estimated literal cost.</summary>
    private static int ChooseCacheBits(uint[] px, Refs refs, Settings s)
    {
        int best = 0;
        double bestCost = double.MaxValue;
        var green = new int[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
        var red = new int[256];
        var blue = new int[256];
        var alpha = new int[256];
        foreach (int bits in s.CacheBits)
        {
            Array.Clear(green);
            Array.Clear(red);
            Array.Clear(blue);
            Array.Clear(alpha);
            int size = bits > 0 ? 1 << bits : 0;
            var cache = size > 0 ? new uint[size] : null;
            var valid = size > 0 ? new bool[size] : null;
            int shift = 32 - bits, pos = 0;
            for (int t = 0; t < refs.Count; t++)
            {
                int len = refs.Length[t];
                if (refs.Distance[t] == 0)
                {
                    uint p = px[pos++];
                    if (cache != null)
                    {
                        int key = (int)((0x1E35A7BDu * p) >> shift);
                        if (valid![key] && cache[key] == p)
                        {
                            green[NumLiteralCodes + NumLengthCodes + key]++;
                            continue;
                        }
                        cache[key] = p;
                        valid[key] = true;
                    }
                    green[(p >> 8) & 255]++;
                    red[(p >> 16) & 255]++;
                    blue[p & 255]++;
                    alpha[p >> 24]++;
                }
                else
                {
                    green[NumLiteralCodes + Prefix(len, out _, out _)]++;
                    if (cache != null)
                        for (int k = 0; k < len; k++)
                        {
                            uint p = px[pos + k];
                            int key = (int)((0x1E35A7BDu * p) >> shift);
                            cache[key] = p;
                            valid![key] = true;
                        }
                    pos += len;
                }
            }
            // Entropy plus a rough charge for describing each used symbol's code length.
            double cost = Entropy(green, NumLiteralCodes + NumLengthCodes + size) + Entropy(red, 256) + Entropy(blue, 256) + Entropy(alpha, 256);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = bits;
            }
        }
        return best;
    }

    private static double Entropy(int[] h, int n)
    {
        long total = 0;
        int used = 0;
        for (int i = 0; i < n; i++)
            if (h[i] > 0) { total += h[i]; used++; }
        if (total == 0) return 0;
        double bits = 0;
        for (int i = 0; i < n; i++)
            if (h[i] > 0) bits += h[i] * Math.Log2((double)total / h[i]);
        return bits + used * 4;
    }

    // ------------------------------------------------------------------ prefix codes

    /// <summary>The codes of one alphabet, ready to emit; a code of a single symbol takes no bits.</summary>
    private sealed class Code
    {
        public readonly uint[] Bits;
        public readonly byte[] Lengths;

        public Code(int alphabet)
        {
            Bits = new uint[alphabet];
            Lengths = new byte[alphabet];
        }

        public void Put(VP8LWriter w, int symbol) => w.Write(Bits[symbol], Lengths[symbol]);
    }

    /// <summary>Builds a code from a histogram, writes its description and returns it.</summary>
    private static Code WriteCode(VP8LWriter w, int[] histogram)
    {
        int alphabet = histogram.Length;
        var code = new Code(alphabet);
        int used = 0, s0 = -1, s1 = -1;
        for (int i = 0; i < alphabet; i++)
            if (histogram[i] > 0)
            {
                if (used == 0) s0 = i;
                else if (used == 1) s1 = i;
                used++;
            }
        if (used == 0) s0 = 0;
        if (used <= 2 && s0 < 256 && s1 < 256)
        {
            // Simple code: one or two 8-bit symbols.
            w.Write(1, 1);
            w.Write(used == 2 ? 1u : 0u, 1);
            if (s0 < 2)
            {
                w.Write(0, 1);
                w.Write((uint)s0, 1);
            }
            else
            {
                w.Write(1, 1);
                w.Write((uint)s0, 8);
            }
            if (used == 2)
            {
                w.Write((uint)s1, 8);
                code.Bits[s1] = 1; // the larger symbol takes code 1
                code.Lengths[s0] = code.Lengths[s1] = 1;
            }
            return code;
        }

        var lengths = new int[alphabet];
        BuildLengths(histogram, 15, lengths);
        WriteLengths(w, lengths);
        if (used > 1) Canonical(lengths, code);
        return code;
    }

    /// <summary>Writes code lengths with the run-length codes 16 (repeat previous), 17 and 18 (zeros).</summary>
    private static void WriteLengths(VP8LWriter w, int[] lengths)
    {
        int n = lengths.Length;
        var symbols = new List<(int Symbol, int Extra)>(n / 2 + 16);
        int previous = 8;
        for (int i = 0; i < n;)
        {
            int v = lengths[i], run = 1;
            while (i + run < n && lengths[i + run] == v) run++;
            i += run;
            if (v == 0)
            {
                while (run >= 3)
                {
                    if (run >= 11)
                    {
                        int r = Math.Min(run, 138);
                        symbols.Add((18, r - 11));
                        run -= r;
                    }
                    else
                    {
                        int r = Math.Min(run, 10);
                        symbols.Add((17, r - 3));
                        run -= r;
                    }
                }
                while (run-- > 0) symbols.Add((0, 0));
            }
            else
            {
                if (v != previous)
                {
                    symbols.Add((v, 0));
                    previous = v;
                    run--;
                }
                while (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    symbols.Add((16, r - 3));
                    run -= r;
                }
                while (run-- > 0) symbols.Add((v, 0));
            }
        }

        var histogram = new int[19];
        foreach (var (sym, _) in symbols) histogram[sym]++;
        var clLengths = new int[19];
        BuildLengths(histogram, 7, clLengths);
        var cl = new Code(19);
        int clUsed = 0;
        foreach (int c in histogram) if (c > 0) clUsed++;
        if (clUsed > 1) Canonical(clLengths, cl);

        int count = 19;
        while (count > 4 && clLengths[CodeLengthCodeOrder[count - 1]] == 0) count--;
        w.Write(0, 1); // normal code
        w.Write((uint)count - 4, 4);
        for (int i = 0; i < count; i++) w.Write((uint)clLengths[CodeLengthCodeOrder[i]], 3);
        w.Write(0, 1); // the lengths cover the whole alphabet
        foreach (var (sym, extra) in symbols)
        {
            cl.Put(w, sym);
            if (sym == 16) w.Write((uint)extra, 2);
            else if (sym == 17) w.Write((uint)extra, 3);
            else if (sym == 18) w.Write((uint)extra, 7);
        }
    }

    /// <summary>Canonical codes from lengths, bit-reversed for least-significant-first output.</summary>
    private static void Canonical(int[] lengths, Code code)
    {
        var count = new int[16];
        foreach (int l in lengths) count[l]++;
        count[0] = 0;
        var next = new int[16];
        int c = 0;
        for (int len = 1; len < 16; len++)
        {
            c = (c + count[len - 1]) << 1;
            next[len] = c;
        }
        for (int s = 0; s < lengths.Length; s++)
        {
            int len = lengths[s];
            if (len == 0) continue;
            int v = next[len]++, r = 0;
            for (int k = 0; k < len; k++) { r = r << 1 | (v & 1); v >>= 1; }
            code.Bits[s] = (uint)r;
            code.Lengths[s] = (byte)len;
        }
    }

    /// <summary>Huffman code lengths limited to <paramref name="maxLength"/>, flattening rare counts until they fit.</summary>
    private static void BuildLengths(int[] histogram, int maxLength, int[] lengths)
    {
        Array.Clear(lengths);
        int m = 0;
        foreach (int h in histogram) if (h > 0) m++;
        if (m == 0) { lengths[0] = 1; return; }
        if (m == 1)
        {
            lengths[Array.FindIndex(histogram, h => h > 0)] = 1;
            return;
        }
        var symbols = new int[m];
        for (int i = 0, k = 0; i < histogram.Length; i++) if (histogram[i] > 0) symbols[k++] = i;
        var weight = new long[2 * m - 1];
        var parent = new int[2 * m - 1];
        var depth = new int[2 * m - 1];
        var sortedSymbols = new int[m];
        var keys = new long[m];
        for (long floor = 1; ; floor *= 2)
        {
            for (int i = 0; i < m; i++)
            {
                keys[i] = Math.Max(histogram[symbols[i]], floor);
                sortedSymbols[i] = symbols[i];
            }
            Array.Sort(keys, sortedSymbols);
            for (int i = 0; i < m; i++) weight[i] = keys[i];
            // Two-queue Huffman: leaves in order, internal nodes are created in non-decreasing weight.
            int leaf = 0, inner = m, next = m;
            for (int k = 0; k < m - 1; k++)
            {
                int a = leaf < m && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                int b = leaf < m && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                weight[next] = weight[a] + weight[b];
                parent[a] = parent[b] = next;
                next++;
            }
            int root = 2 * m - 2, maxDepth = 0;
            depth[root] = 0;
            for (int node = root - 1; node >= 0; node--)
            {
                depth[node] = depth[parent[node]] + 1;
                if (node < m && depth[node] > maxDepth) maxDepth = depth[node];
            }
            if (maxDepth <= maxLength)
            {
                for (int i = 0; i < m; i++) lengths[sortedSymbols[i]] = depth[i];
                return;
            }
        }
    }

    /// <summary>Writes bits least significant first.</summary>
    private sealed class VP8LWriter
    {
        private byte[] _buf;
        private int _len;
        private ulong _acc;
        private int _count;

        public VP8LWriter(int capacity) => _buf = new byte[Math.Max(capacity, 256)];

        public void Write(uint value, int bits)
        {
            if (bits == 0) return;
            _acc |= (ulong)value << _count;
            _count += bits;
            if (_count >= 32)
            {
                if (_len + 4 > _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                _buf[_len] = (byte)_acc;
                _buf[_len + 1] = (byte)(_acc >> 8);
                _buf[_len + 2] = (byte)(_acc >> 16);
                _buf[_len + 3] = (byte)(_acc >> 24);
                _len += 4;
                _acc >>= 32;
                _count -= 32;
            }
        }

        public byte[] ToArray()
        {
            var o = new byte[_len + (_count + 7) / 8];
            Array.Copy(_buf, o, _len);
            for (int i = _len; i < o.Length; i++)
            {
                o[i] = (byte)_acc;
                _acc >>= 8;
            }
            return o;
        }
    }
}

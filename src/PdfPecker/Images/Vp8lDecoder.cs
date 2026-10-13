namespace PdfPecker.Images;

/// <summary>
/// Decoder for lossless WebP (the VP8L bitstream): prefix-coded ARGB pixels with LZ77 back-references and a colour
/// cache, undone through up to four image transforms. Also decodes the lossless alpha planes of lossy WebP images.
/// </summary>
internal sealed class Vp8lDecoder
{
    private const int PredictorTransform = 0, CrossColorTransform = 1, SubtractGreenTransform = 2, ColorIndexingTransform = 3;
    private const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40, MaxCacheBits = 11;
    private static readonly int[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    // Distance codes 1..120 map to nearby pixels: high nibble is the row above, low nibble is 8 minus the column offset.
    private static readonly byte[] CodeToPlane =
    {
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    };

    private readonly BitReader _br;
    private readonly long _maxPixels;
    private readonly List<Transform> _transforms = new();
    private int _seenTransforms;
    private long _work; // pixels decoded in all images of the stream, against the pixel budget
    private long _tableEntries; // prefix code lookup entries kept, against a memory budget
    private const long MaxTableEntries = 1 << 24;

    private Vp8lDecoder(byte[] data, int offset, int length, long maxPixels)
    {
        _br = new BitReader(data, offset, length);
        _maxPixels = maxPixels;
    }

    private sealed class Transform
    {
        public int Type, Bits, XSize;
        public uint[]? Data;
    }

    /// <summary>Decodes a VP8L chunk (with its signature and size header) to ARGB pixels, or returns null.</summary>
    public static uint[]? Decode(byte[] data, int offset, int length, long maxPixels, out int width, out int height, out bool hasAlpha)
    {
        width = height = 0;
        hasAlpha = false;
        if (length < 5 || data[offset] != 0x2F) return null;
        var dec = new Vp8lDecoder(data, offset + 1, length - 1, maxPixels);
        width = (int)dec._br.Read(14) + 1;
        height = (int)dec._br.Read(14) + 1;
        hasAlpha = dec._br.Read(1) == 1;
        if (dec._br.Read(3) != 0) return null; // version
        if ((long)width * height > maxPixels) return null;
        return dec.DecodeMain(width, height);
    }

    /// <summary>Decodes a headerless VP8L stream of a known size (the lossless alpha plane of a lossy image).</summary>
    public static uint[]? DecodeStream(byte[] data, int offset, int length, int width, int height, long maxPixels)
    {
        if ((long)width * height > maxPixels) return null;
        return new Vp8lDecoder(data, offset, length, maxPixels).DecodeMain(width, height);
    }

    private uint[]? DecodeMain(int width, int height)
    {
        try
        {
            int xsize = width;
            while (_br.Read(1) == 1)
                if (!ReadTransform(ref xsize, height)) return null;
            var pixels = DecodeImageStream(xsize, height, true);
            if (pixels == null) return null;
            for (int i = _transforms.Count - 1; i >= 0; i--) pixels = Inverse(_transforms[i], pixels, height);
            return pixels;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private bool ReadTransform(ref int xsize, int ysize)
    {
        int type = (int)_br.Read(2);
        if ((_seenTransforms & (1 << type)) != 0) return false; // each transform at most once
        _seenTransforms |= 1 << type;
        var t = new Transform { Type = type, XSize = xsize };
        switch (type)
        {
            case PredictorTransform:
            case CrossColorTransform:
                t.Bits = (int)_br.Read(3) + 2;
                t.Data = DecodeImageStream(SubSampleSize(xsize, t.Bits), SubSampleSize(ysize, t.Bits), false);
                if (t.Data == null) return false;
                break;
            case ColorIndexingTransform:
            {
                int colors = (int)_br.Read(8) + 1;
                t.Bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                var table = DecodeImageStream(colors, 1, false);
                if (table == null) return false;
                // The palette is delta coded; entries past its end are transparent black.
                var map = new uint[1 << (8 >> t.Bits)];
                map[0] = table[0];
                for (int i = 1; i < colors && i < map.Length; i++) map[i] = AddPixels(table[i], map[i - 1]);
                t.Data = map;
                xsize = SubSampleSize(xsize, t.Bits);
                break;
            }
        }
        _transforms.Add(t);
        return true;
    }

    // ------------------------------------------------------------------ entropy-coded image

    private uint[]? DecodeImageStream(int xsize, int ysize, bool level0)
    {
        long pixels = (long)xsize * ysize;
        _work += pixels;
        if (pixels > _maxPixels || _work > _maxPixels * 2) return null;

        int cacheBits = 0;
        if (_br.Read(1) == 1)
        {
            cacheBits = (int)_br.Read(4);
            if (cacheBits is < 1 or > MaxCacheBits) return null;
        }

        // Meta prefix codes: an entropy image choosing the code group of each block of pixels.
        int huffBits = 0, huffXSize = 0;
        uint[]? huffImage = null;
        int groupCount = 1;
        if (level0 && _br.Read(1) == 1)
        {
            huffBits = (int)_br.Read(3) + 2;
            huffXSize = SubSampleSize(xsize, huffBits);
            huffImage = DecodeImageStream(huffXSize, SubSampleSize(ysize, huffBits), false);
            if (huffImage == null) return null;
            int max = 0;
            for (int i = 0; i < huffImage.Length; i++)
            {
                int g = (int)(huffImage[i] >> 8) & 0xFFFF;
                huffImage[i] = (uint)g;
                if (g > max) max = g;
            }
            groupCount = max + 1;
        }

        int cacheSize = cacheBits > 0 ? 1 << cacheBits : 0;
        var groups = new Huffman[groupCount][];
        var used = new bool[groupCount];
        if (huffImage != null) foreach (var g in huffImage) used[g] = true;
        else used[0] = true;
        var lengths = new int[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
        for (int g = 0; g < groupCount; g++)
        {
            // Every group is in the stream, but only the ones the entropy image refers to are kept.
            var codes = new Huffman[5];
            for (int j = 0; j < 5; j++)
            {
                int alphabet = j == 0 ? NumLiteralCodes + NumLengthCodes + cacheSize : j == 4 ? NumDistanceCodes : 256;
                codes[j] = ReadHuffmanCode(alphabet, lengths) ?? throw new InvalidDataException("Bad prefix code.");
                if (used[g] && (_tableEntries += codes[j].TableSize) > MaxTableEntries) return null;
            }
            if (used[g]) groups[g] = codes;
        }

        var data = new uint[pixels];
        var cache = cacheSize > 0 ? new uint[cacheSize] : null;
        int cacheShift = 32 - cacheBits;
        int pos = 0, x = 0, y = 0, total = (int)pixels;
        var group = groups[0];
        while (pos < total)
        {
            if (huffImage != null) group = groups[huffImage[(y >> huffBits) * huffXSize + (x >> huffBits)]];
            int code = group[0].Read(_br);
            if (code < NumLiteralCodes)
            {
                int red = group[1].Read(_br), blue = group[2].Read(_br), alpha = group[3].Read(_br);
                uint argb = (uint)(alpha << 24 | red << 16 | code << 8 | blue);
                data[pos++] = argb;
                if (cache != null) cache[(0x1e35a7bd * argb) >> cacheShift] = argb;
                if (++x == xsize) { x = 0; y++; }
            }
            else if (code < NumLiteralCodes + NumLengthCodes)
            {
                int length = CopyDistance(code - NumLiteralCodes);
                int distSymbol = group[4].Read(_br);
                int dist = PlaneCodeToDistance(xsize, CopyDistance(distSymbol));
                if (dist > pos || length > total - pos) throw new InvalidDataException("Bad back-reference.");
                for (int k = 0; k < length; k++, pos++)
                {
                    uint argb = data[pos - dist];
                    data[pos] = argb;
                    if (cache != null) cache[(0x1e35a7bd * argb) >> cacheShift] = argb;
                }
                x += length;
                while (x >= xsize) { x -= xsize; y++; }
            }
            else
            {
                int key = code - NumLiteralCodes - NumLengthCodes;
                if (cache == null || key >= cache.Length) throw new InvalidDataException("Bad colour cache index.");
                data[pos++] = cache[key];
                if (++x == xsize) { x = 0; y++; }
            }
            if (_br.Overrun) throw new InvalidDataException("Truncated lossless WebP data.");
        }
        return data;
    }

    private int CopyDistance(int symbol)
    {
        if (symbol < 4) return symbol + 1;
        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)_br.Read(extraBits) + 1;
    }

    private static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > 120) return planeCode - 120;
        int distCode = CodeToPlane[planeCode - 1];
        int yOffset = distCode >> 4, xOffset = 8 - (distCode & 0xF);
        int dist = yOffset * xsize + xOffset;
        return dist >= 1 ? dist : 1;
    }

    private Huffman? ReadHuffmanCode(int alphabet, int[] lengths)
    {
        Array.Clear(lengths, 0, Math.Max(alphabet, 256));
        if (_br.Read(1) == 1)
        {
            // Simple code: one or two symbols.
            int count = (int)_br.Read(1) + 1;
            int first = (int)_br.Read(_br.Read(1) == 0 ? 1 : 8);
            lengths[first] = 1;
            if (count == 2) lengths[_br.Read(8)] = 1;
        }
        else
        {
            var codeLengthLengths = new int[CodeLengthCodeOrder.Length];
            int n = (int)_br.Read(4) + 4;
            for (int i = 0; i < n; i++) codeLengthLengths[CodeLengthCodeOrder[i]] = (int)_br.Read(3);
            var lengthCode = Huffman.Build(codeLengthLengths, codeLengthLengths.Length);
            if (lengthCode == null) return null;

            int maxSymbol = alphabet;
            if (_br.Read(1) == 1)
            {
                int bits = 2 + 2 * (int)_br.Read(3);
                maxSymbol = 2 + (int)_br.Read(bits);
                if (maxSymbol > alphabet) return null;
            }
            int symbol = 0, previous = 8;
            while (symbol < alphabet)
            {
                if (maxSymbol-- == 0) break;
                int len = lengthCode.Read(_br);
                if (len < 16)
                {
                    lengths[symbol++] = len;
                    if (len != 0) previous = len;
                }
                else
                {
                    int slot = len - 16;
                    int repeat = (int)_br.Read(slot == 0 ? 2 : slot == 1 ? 3 : 7) + (slot == 2 ? 11 : 3);
                    if (symbol + repeat > alphabet) return null;
                    int value = slot == 0 ? previous : 0;
                    while (repeat-- > 0) lengths[symbol++] = value;
                }
            }
        }
        if (_br.Overrun) return null;
        return Huffman.Build(lengths, alphabet);
    }

    // ------------------------------------------------------------------ inverse transforms

    private static uint[] Inverse(Transform t, uint[] src, int height)
    {
        int width = t.XSize;
        switch (t.Type)
        {
            case SubtractGreenTransform:
                for (int i = 0; i < src.Length; i++)
                {
                    uint argb = src[i], green = (argb >> 8) & 0xFF;
                    uint redBlue = (argb & 0x00FF00FF) + (green << 16 | green);
                    src[i] = (argb & 0xFF00FF00) | (redBlue & 0x00FF00FF);
                }
                return src;
            case CrossColorTransform:
            {
                int tiles = SubSampleSize(width, t.Bits);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width, tileRow = (y >> t.Bits) * tiles;
                    for (int x = 0; x < width; x++)
                    {
                        uint m = t.Data![tileRow + (x >> t.Bits)];
                        sbyte greenToRed = (sbyte)m, greenToBlue = (sbyte)(m >> 8), redToBlue = (sbyte)(m >> 16);
                        uint argb = src[row + x];
                        sbyte green = (sbyte)(argb >> 8);
                        int red = (int)(argb >> 16) & 0xFF, blue = (int)argb & 0xFF;
                        red = (red + (greenToRed * green >> 5)) & 0xFF;
                        blue += greenToBlue * green >> 5;
                        blue += redToBlue * (sbyte)red >> 5;
                        src[row + x] = (argb & 0xFF00FF00) | (uint)red << 16 | (uint)(blue & 0xFF);
                    }
                }
                return src;
            }
            case PredictorTransform:
            {
                int tiles = SubSampleSize(width, t.Bits);
                // The first row predicts from the left (its first pixel from opaque black), the first column from above.
                src[0] = AddPixels(src[0], 0xFF000000);
                for (int x = 1; x < width; x++) src[x] = AddPixels(src[x], src[x - 1]);
                for (int y = 1; y < height; y++)
                {
                    int row = y * width, tileRow = (y >> t.Bits) * tiles;
                    src[row] = AddPixels(src[row], src[row - width]);
                    for (int x = 1; x < width; x++)
                    {
                        int mode = (int)(t.Data![tileRow + (x >> t.Bits)] >> 8) & 0xF;
                        int p = row + x;
                        // The top-right of the last pixel in a row is the first pixel of the current row.
                        src[p] = AddPixels(src[p], Predict(mode, src[p - 1], src[p - width], src[p - width - 1], src[p - width + 1]));
                    }
                }
                return src;
            }
            default: // colour indexing
            {
                var map = t.Data!;
                if (t.Bits == 0)
                {
                    for (int i = 0; i < src.Length; i++)
                    {
                        int index = (int)(src[i] >> 8) & 0xFF;
                        src[i] = index < map.Length ? map[index] : 0;
                    }
                    return src;
                }
                int packedWidth = SubSampleSize(width, t.Bits);
                int bitsPerPixel = 8 >> t.Bits, perByte = 1 << t.Bits;
                uint mask = (1u << bitsPerPixel) - 1;
                var dst = new uint[(long)width * height];
                for (int y = 0; y < height; y++)
                {
                    uint packed = 0;
                    for (int x = 0; x < width; x++)
                    {
                        if ((x & (perByte - 1)) == 0) packed = (src[y * packedWidth + (x >> t.Bits)] >> 8) & 0xFF;
                        dst[y * width + x] = map[packed & mask];
                        packed >>= bitsPerPixel;
                    }
                }
                return dst;
            }
        }
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

    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xFF00FF00) + (b & 0xFF00FF00);
        uint redBlue = (a & 0x00FF00FF) + (b & 0x00FF00FF);
        return (alphaGreen & 0xFF00FF00) | (redBlue & 0x00FF00FF);
    }

    private static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    private static uint Select(uint a, uint b, uint c)
    {
        int Sub3(int shift)
        {
            int av = (int)(a >> shift) & 0xFF, bv = (int)(b >> shift) & 0xFF, cv = (int)(c >> shift) & 0xFF;
            return Math.Abs(bv - cv) - Math.Abs(av - cv);
        }
        int paMinusPb = Sub3(24) + Sub3(16) + Sub3(8) + Sub3(0);
        return paMinusPb <= 0 ? a : b;
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

    // ------------------------------------------------------------------ bits and prefix codes

    /// <summary>Reads bits least significant first. Reading past the end yields zeros and sets <see cref="Overrun"/>.</summary>
    internal sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos;
        private readonly int _end;
        private ulong _bits;
        private int _count;
        public bool Overrun;

        public BitReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = Math.Min(data.Length, offset + Math.Max(0, length));
        }

        private void Fill()
        {
            while (_count <= 56 && _pos < _end)
            {
                _bits |= (ulong)_data[_pos++] << _count;
                _count += 8;
            }
        }

        public uint Peek(int n)
        {
            if (_count < n) Fill();
            return (uint)(_bits & ((1UL << n) - 1));
        }

        public void Skip(int n)
        {
            if (n > _count)
            {
                Overrun = true;
                _bits = 0;
                _count = 0;
                return;
            }
            _bits >>= n;
            _count -= n;
        }

        public uint Read(int n)
        {
            if (n == 0) return 0;
            uint v = Peek(n);
            Skip(n);
            return v;
        }
    }

    /// <summary>A canonical prefix code, read through a lookup table on its first bits.</summary>
    internal sealed class Huffman
    {
        private const int TableBits = 9;
        private int _single = -1;
        private int _tableBits;
        private int[] _table = Array.Empty<int>(); // symbol << 4 | length, or -1 for longer codes
        private int[] _counts = Array.Empty<int>();
        private int[] _symbols = Array.Empty<int>();

        public int TableSize => _table.Length;

        public static Huffman? Build(int[] lengths, int alphabet)
        {
            var h = new Huffman();
            var counts = new int[16];
            int used = 0, last = 0, maxLen = 0;
            for (int s = 0; s < alphabet; s++)
            {
                int len = lengths[s];
                if (len == 0) continue;
                if (len > 15) return null;
                counts[len]++;
                used++;
                last = s;
                if (len > maxLen) maxLen = len;
            }
            if (used == 0) return null;
            if (used == 1)
            {
                h._single = last; // a code of one symbol takes no bits
                return h;
            }
            // The code must be complete: neither over-subscribed nor with unused codes.
            int left = 1;
            for (int len = 1; len < 16; len++)
            {
                left = (left << 1) - counts[len];
                if (left < 0) return null;
            }
            if (left != 0) return null;

            var offsets = new int[16];
            for (int len = 1; len < 15; len++) offsets[len + 1] = offsets[len] + counts[len];
            var symbols = new int[used];
            for (int s = 0; s < alphabet; s++)
                if (lengths[s] != 0) symbols[offsets[lengths[s]]++] = s;
            h._counts = counts;
            h._symbols = symbols;

            // Lookup table for codes up to TableBits long, indexed by the next bits of the stream (least significant first).
            h._tableBits = Math.Min(maxLen, TableBits);
            var table = new int[1 << h._tableBits];
            Array.Fill(table, -1);
            int code = 0, index = 0;
            for (int len = 1; len <= maxLen; len++)
            {
                for (int k = 0; k < counts[len]; k++, index++, code++)
                {
                    if (len > h._tableBits) continue;
                    int reversed = Reverse(code, len);
                    for (int r = reversed; r < table.Length; r += 1 << len) table[r] = symbols[index] << 4 | len;
                }
                code <<= 1;
            }
            h._table = table;
            return h;
        }

        private static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++) { r = r << 1 | (code & 1); code >>= 1; }
            return r;
        }

        public int Read(BitReader br)
        {
            if (_single >= 0) return _single;
            int entry = _table[br.Peek(_tableBits)];
            if (entry >= 0)
            {
                br.Skip(entry & 0xF);
                return entry >> 4;
            }
            // A long code: walk it one bit at a time.
            int code = 0, first = 0, index = 0;
            for (int len = 1; len < 16; len++)
            {
                code |= (int)br.Read(1);
                int count = _counts[len];
                if (code - first < count) return _symbols[index + code - first];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException("Bad prefix code.");
        }
    }
}

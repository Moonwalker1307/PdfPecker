using System.Buffers.Binary;

namespace PdfPecker.Images;

/// <summary>
/// Decoder for lossy WebP: one VP8 key frame (RFC 6386), converted from YUV 4:2:0 to RGB with the same fixed-point
/// arithmetic and chroma upsampling as libwebp, so the pixels match what browsers show. It follows libwebp's decoder
/// closely and is covered by libwebp's BSD license (see THIRD-PARTY-NOTICES.md).
/// </summary>
internal sealed partial class Vp8Decoder
{
    // The frame tag: bit 0 is clear for a key frame, bits 1-3 are the profile, bit 4 asks for the frame to be shown,
    // and bits 5-23 are the size of the first partition. A key frame goes on with a start code and the width and
    // height in 14 bits each; their top two bits ask for upscaling, which is left to whoever shows the picture.
    private const int KeyFrameHeaderLength = 10;
    private const int StartCodeField = 3;
    private const int WidthField = 6;
    private const int HeightField = 8;
    private const int MaxProfile = 3;
    private const int SizeMask = 0x3FFF;
    private const int FirstPartitionSizeShift = 5;
    private static ReadOnlySpan<byte> StartCode => [0x9D, 0x01, 0x2A];

    // Every macroblock header takes more than three bits of the first partition, so a few bytes cannot describe a
    // huge picture and are not decoded into one.
    private const long MaxMacroblocksPerPartitionByte = 32;

    // The tokens are in 1, 2, 4 or 8 partitions, each holding every so many macroblock rows; the sizes of all but
    // the last come first, in 3 bytes each.
    private const int PartitionCountBits = 2;
    private const int PartitionSizeLength = 3;

    // Segments: up to four groups of macroblocks with a quantizer and a filter strength of their own.
    private const int NumSegments = 4;
    private const int SegmentQuantizerBits = 7;
    private const int SegmentFilterBits = 6;
    private const int SegmentProbaBits = 8;

    private const int FilterLevelBits = 6;
    private const int SharpnessBits = 3;
    private const int FilterDeltaBits = 6;
    private const int NumFilterDeltas = 4;
    private const int MaxFilterLevel = 63;

    private const int QuantizerBits = 7;
    private const int QuantizerDeltaBits = 4;
    private const int MaxQuantizer = 127;
    private const int MaxChromaDcQuantizer = 117;

    // The key frame macroblock modes are coded with fixed probabilities (RFC 6386, 11.2). An entry of a tree is the
    // index of the next pair of entries, or minus the mode at a leaf.
    private const int PerSubblockModes = 4; // the luma leaf for a macroblock with a mode per 4x4 block
    private static ReadOnlySpan<sbyte> LumaModeTree => [-PerSubblockModes, 2, 4, 6, -DcMode, -VerticalMode, -HorizontalMode, -TrueMotionMode];
    private static ReadOnlySpan<byte> LumaModeProbas => [145, 156, 163, 128];
    private static ReadOnlySpan<sbyte> ChromaModeTree => [-DcMode, 2, -VerticalMode, 4, -HorizontalMode, -TrueMotionMode];
    private static ReadOnlySpan<byte> ChromaModeProbas => [142, 114, 183];

    private static ReadOnlySpan<sbyte> SubblockModeTree =>
    [
        -DcMode, 2,
        -TrueMotionMode, 4,
        -VerticalMode, 6,
        8, 12,
        -HorizontalMode, 10,
        -DownRightMode, -VerticalRightMode,
        -DownLeftMode, 14,
        -VerticalLeftMode, 16,
        -HorizontalDownMode, -HorizontalUpMode,
    ];
    private const int SubblockModeProbas = NumSubblockModes - 1;

    /// <summary>The dequantization factors of a segment, for the DC and the other coefficients of each kind of block.</summary>
    private struct Quantizer
    {
        public int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc;
    }

    /// <summary>How strongly the loop filter smooths the edges of one macroblock, and whether it filters its inner edges.</summary>
    private struct FilterStrength
    {
        public int Limit, InnerLimit, HighEdgeVarianceThreshold;
        public bool FilterInnerEdges;
    }

    /// <summary>What the first partition says about a macroblock, besides the luma modes it writes into <see cref="_modes"/>.</summary>
    private readonly record struct MacroblockHeader(int Segment, bool Skip, bool Is4x4, int ChromaMode);

    // Frame header.
    private int _mbWidth, _mbHeight;
    private readonly Quantizer[] _quantizers = new Quantizer[NumSegments];
    private readonly byte[] _coeffProbas = new byte[NumBlockTypes * NumBands * NumContexts * NumProbas];
    private readonly byte[] _segmentProbas = { 255, 255, 255 };
    private bool _useSegments, _updateSegmentMap, _absoluteSegmentValues = true;
    private readonly int[] _segmentQuantizers = new int[NumSegments], _segmentFilterLevels = new int[NumSegments];
    private LoopFilter _filter;
    private int _filterLevel, _sharpness;
    private bool _useFilterDeltas;
    private readonly int[] _referenceFilterDeltas = new int[NumFilterDeltas], _modeFilterDeltas = new int[NumFilterDeltas];
    private bool _useSkipProba;
    private int _skipProba;
    private readonly FilterStrength[,] _filterStrengths = new FilterStrength[NumSegments, 2];

    // The modes of the macroblock being decoded, and those along its top and left edges, the contexts of the next ones.
    private readonly byte[] _modes = new byte[16];
    private byte[] _topModes = Array.Empty<byte>(); // 4 per macroblock column
    private readonly byte[] _leftModes = new byte[4];

    private Plane _y, _u, _v;
    private FilterStrength[] _macroblockFilters = Array.Empty<FilterStrength>();

    /// <summary>Decodes a VP8 chunk to RGB samples, or returns null when it is not a readable key frame.</summary>
    public static DecodedImage? Decode(byte[] data, int offset, int length, long maxPixels)
    {
        try
        {
            return new Vp8Decoder().DecodeFrame(data, offset, length, maxPixels);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private DecodedImage? DecodeFrame(byte[] data, int offset, int length, long maxPixels)
    {
        int end = Math.Min(data.Length, offset + length);
        if (end - offset < KeyFrameHeaderLength) return null;
        var header = data.AsSpan(offset, KeyFrameHeaderLength);
        int tag = ReadUInt24(header);
        bool keyFrame = (tag & 1) == 0, show = (tag & 0x10) != 0;
        int profile = (tag >> 1) & 7, firstPartitionSize = tag >> FirstPartitionSizeShift;
        if (!keyFrame || profile > MaxProfile || !show) return null;
        if (!header.Slice(StartCodeField, StartCode.Length).SequenceEqual(StartCode)) return null;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(header[WidthField..]) & SizeMask;
        int height = BinaryPrimitives.ReadUInt16LittleEndian(header[HeightField..]) & SizeMask;
        if (width == 0 || height == 0 || (long)width * height > maxPixels) return null;
        int firstPartition = offset + KeyFrameHeaderLength;
        if (firstPartitionSize > end - firstPartition) return null;
        _mbWidth = (width + 15) >> 4;
        _mbHeight = (height + 15) >> 4;
        if ((long)_mbWidth * _mbHeight > firstPartitionSize * MaxMacroblocksPerPartitionByte) return null;

        var br = new BoolDecoder(data, firstPartition, firstPartitionSize);
        br.GetValue(1); // colour space
        br.GetValue(1); // clamping type
        ParseSegmentHeader(br);
        ParseFilterHeader(br);
        var partitions = CreateTokenPartitions(data, firstPartition + firstPartitionSize, end, 1 << br.GetValue(PartitionCountBits));
        if (partitions == null) return null;
        ParseQuantizers(br);
        br.GetValue(1); // refresh entropy probabilities: there is only one frame
        ParseProbas(br);
        PrecomputeFilterStrengths();

        _y = new Plane(_mbWidth * 16, _mbHeight * 16);
        _u = new Plane(_mbWidth * 8, _mbHeight * 8);
        _v = new Plane(_mbWidth * 8, _mbHeight * 8);
        _topModes = new byte[_mbWidth * 4];
        _topNonZero = new ushort[_mbWidth];
        if (_filter != LoopFilter.None) _macroblockFilters = new FilterStrength[_mbWidth * _mbHeight];

        for (int mbY = 0; mbY < _mbHeight; mbY++)
        {
            var tokens = partitions[mbY & (partitions.Length - 1)];
            Array.Clear(_leftModes);
            _leftNonZero = 0;
            for (int mbX = 0; mbX < _mbWidth; mbX++) DecodeMacroblock(br, tokens, mbX, mbY);
        }
        if (_filter != LoopFilter.None)
            for (int mbY = 0; mbY < _mbHeight; mbY++)
                for (int mbX = 0; mbX < _mbWidth; mbX++) FilterMacroblock(mbX, mbY, _macroblockFilters[mbY * _mbWidth + mbX]);

        return new DecodedImage(width, height, ToRgb(width, height), null);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;

    /// <summary>
    /// The token partitions after the first one; a partition size that reaches past the chunk is cut at its end, but
    /// the last partition needs at least one byte.
    /// </summary>
    private static BoolDecoder[]? CreateTokenPartitions(byte[] data, int start, int end, int count)
    {
        int sizesLength = PartitionSizeLength * (count - 1);
        if (end - start < sizesLength) return null;
        var sizes = data.AsSpan(start, sizesLength);
        var partitions = new BoolDecoder[count];
        int position = start + sizesLength;
        for (int i = 0; i < count - 1; i++)
        {
            int size = Math.Min(ReadUInt24(sizes[(PartitionSizeLength * i)..]), end - position);
            partitions[i] = new BoolDecoder(data, position, size);
            position += size;
        }
        if (position >= end) return null;
        partitions[count - 1] = new BoolDecoder(data, position, end - position);
        return partitions;
    }

    private void DecodeMacroblock(BoolDecoder br, BoolDecoder tokens, int mbX, int mbY)
    {
        var header = ParseMacroblockHeader(br, mbX);
        Array.Clear(_coeffs);
        bool skip = header.Skip;
        if (!skip) skip = !ParseResiduals(tokens, mbX, header.Is4x4, _quantizers[header.Segment]);
        else ClearNonZeroContexts(mbX, header.Is4x4);
        if (_filter != LoopFilter.None)
        {
            var strength = _filterStrengths[header.Segment, header.Is4x4 ? 1 : 0];
            // A macroblock with coefficients has its inner edges filtered too.
            strength.FilterInnerEdges |= !skip;
            _macroblockFilters[mbY * _mbWidth + mbX] = strength;
        }
        Reconstruct(mbX, mbY, header.Is4x4, header.ChromaMode);
    }

    /// <summary>Reads the segment, the skip flag and the prediction modes of a macroblock from the first partition.</summary>
    private MacroblockHeader ParseMacroblockHeader(BoolDecoder br, int mbX)
    {
        int segment = 0;
        if (_updateSegmentMap)
            segment = br.GetBit(_segmentProbas[0]) == 0 ? br.GetBit(_segmentProbas[1]) : br.GetBit(_segmentProbas[2]) + 2;
        bool skip = _useSkipProba && br.GetBit(_skipProba) == 1;
        var topModes = _topModes.AsSpan(mbX * 4, 4);
        int lumaMode = br.ReadTree(LumaModeTree, LumaModeProbas);
        bool is4x4 = lumaMode == PerSubblockModes;
        if (!is4x4)
        {
            // The 4x4 blocks next to a 16x16 macroblock take its mode as their context.
            _modes[0] = (byte)lumaMode;
            topModes.Fill((byte)lumaMode);
            _leftModes.AsSpan().Fill((byte)lumaMode);
        }
        else
        {
            for (int y = 0; y < 4; y++)
            {
                int mode = _leftModes[y];
                for (int x = 0; x < 4; x++)
                {
                    var probas = Vp8Tables.BModesProba.AsSpan((topModes[x] * NumSubblockModes + mode) * SubblockModeProbas, SubblockModeProbas);
                    mode = br.ReadTree(SubblockModeTree, probas);
                    topModes[x] = (byte)mode;
                    _modes[y * 4 + x] = (byte)mode;
                }
                _leftModes[y] = (byte)mode;
            }
        }
        int chromaMode = br.ReadTree(ChromaModeTree, ChromaModeProbas);
        return new MacroblockHeader(segment, skip, is4x4, chromaMode);
    }

    // ------------------------------------------------------------------ frame header

    private void ParseSegmentHeader(BoolDecoder br)
    {
        _useSegments = br.ReadFlag();
        if (!_useSegments) return;
        _updateSegmentMap = br.ReadFlag();
        if (br.ReadFlag())
        {
            // The values are absolute, or deltas from the frame's quantizer and filter level.
            _absoluteSegmentValues = br.ReadFlag();
            for (int s = 0; s < NumSegments; s++) _segmentQuantizers[s] = br.ReadFlag() ? br.GetSignedValue(SegmentQuantizerBits) : 0;
            for (int s = 0; s < NumSegments; s++) _segmentFilterLevels[s] = br.ReadFlag() ? br.GetSignedValue(SegmentFilterBits) : 0;
        }
        if (_updateSegmentMap)
            for (int i = 0; i < _segmentProbas.Length; i++) _segmentProbas[i] = (byte)(br.ReadFlag() ? br.GetValue(SegmentProbaBits) : 255);
    }

    private void ParseFilterHeader(BoolDecoder br)
    {
        bool simple = br.ReadFlag();
        _filterLevel = br.GetValue(FilterLevelBits);
        _sharpness = br.GetValue(SharpnessBits);
        _useFilterDeltas = br.ReadFlag();
        if (_useFilterDeltas && br.ReadFlag())
        {
            for (int i = 0; i < NumFilterDeltas; i++) if (br.ReadFlag()) _referenceFilterDeltas[i] = br.GetSignedValue(FilterDeltaBits);
            for (int i = 0; i < NumFilterDeltas; i++) if (br.ReadFlag()) _modeFilterDeltas[i] = br.GetSignedValue(FilterDeltaBits);
        }
        _filter = _filterLevel == 0 ? LoopFilter.None : simple ? LoopFilter.Simple : LoopFilter.Normal;
    }

    private void ParseQuantizers(BoolDecoder br)
    {
        int baseQ = br.GetValue(QuantizerBits);
        int y1Dc = ReadQuantizerDelta(br), y2Dc = ReadQuantizerDelta(br), y2Ac = ReadQuantizerDelta(br);
        int uvDc = ReadQuantizerDelta(br), uvAc = ReadQuantizerDelta(br);
        for (int s = 0; s < NumSegments; s++)
        {
            if (!_useSegments && s > 0)
            {
                _quantizers[s] = _quantizers[0];
                continue;
            }
            int q = _useSegments ? _segmentQuantizers[s] + (_absoluteSegmentValues ? 0 : baseQ) : baseQ;
            ref var quantizer = ref _quantizers[s];
            quantizer.Y1Dc = Vp8Tables.DcTable[Math.Clamp(q + y1Dc, 0, MaxQuantizer)];
            quantizer.Y1Ac = Vp8Tables.AcTable[Math.Clamp(q, 0, MaxQuantizer)];
            quantizer.Y2Dc = Vp8Tables.DcTable[Math.Clamp(q + y2Dc, 0, MaxQuantizer)] * 2;
            // The Y2 AC factor is 155/100 of the table's, at least 8.
            quantizer.Y2Ac = Math.Max(8, Vp8Tables.AcTable[Math.Clamp(q + y2Ac, 0, MaxQuantizer)] * 101581 >> 16);
            quantizer.UvDc = Vp8Tables.DcTable[Math.Clamp(q + uvDc, 0, MaxChromaDcQuantizer)];
            quantizer.UvAc = Vp8Tables.AcTable[Math.Clamp(q + uvAc, 0, MaxQuantizer)];
        }
    }

    private static int ReadQuantizerDelta(BoolDecoder br) => br.ReadFlag() ? br.GetSignedValue(QuantizerDeltaBits) : 0;

    private void ParseProbas(BoolDecoder br)
    {
        for (int i = 0; i < _coeffProbas.Length; i++)
            _coeffProbas[i] = (byte)(br.GetBit(Vp8Tables.CoeffsUpdateProba[i]) == 1 ? br.GetValue(8) : Vp8Tables.CoeffsProba0[i]);
        _useSkipProba = br.ReadFlag();
        if (_useSkipProba) _skipProba = br.GetValue(8);
    }

    /// <summary>
    /// The loop filter strength of each segment, for 16x16 and for 4x4 macroblocks: the level adjusted by the
    /// segment and the deltas, the interior limit lowered by the sharpness, and the high edge variance threshold.
    /// </summary>
    private void PrecomputeFilterStrengths()
    {
        if (_filter == LoopFilter.None) return;
        for (int s = 0; s < NumSegments; s++)
        {
            int segmentLevel = _useSegments ? _segmentFilterLevels[s] + (_absoluteSegmentValues ? 0 : _filterLevel) : _filterLevel;
            for (int is4x4 = 0; is4x4 <= 1; is4x4++)
            {
                int level = segmentLevel;
                if (_useFilterDeltas)
                {
                    // Key frames use the delta of the intra reference frame, and the mode delta of 4x4 prediction.
                    level += _referenceFilterDeltas[0];
                    if (is4x4 == 1) level += _modeFilterDeltas[0];
                }
                level = Math.Clamp(level, 0, MaxFilterLevel);
                var strength = new FilterStrength { FilterInnerEdges = is4x4 == 1 };
                if (level > 0)
                {
                    int innerLimit = level;
                    if (_sharpness > 0)
                    {
                        innerLimit >>= _sharpness > 4 ? 2 : 1;
                        innerLimit = Math.Min(innerLimit, 9 - _sharpness);
                    }
                    innerLimit = Math.Max(innerLimit, 1);
                    strength.InnerLimit = innerLimit;
                    strength.Limit = 2 * level + innerLimit;
                    strength.HighEdgeVarianceThreshold = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                }
                _filterStrengths[s, is4x4] = strength;
            }
        }
    }

    /// <summary>One plane of the picture, in whole macroblocks.</summary>
    private readonly struct Plane(int stride, int rows)
    {
        public readonly byte[] Samples = new byte[stride * rows];
        public readonly int Stride = stride;

        public Span<byte> Row(int y) => Samples.AsSpan(y * Stride, Stride);
    }
}

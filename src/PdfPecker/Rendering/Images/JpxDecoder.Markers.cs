using System.Buffers.Binary;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

// The codestream (ISO/IEC 15444-1, annex A): the main header, then tile-parts, each with its own header and packet data.
internal sealed partial class JpxDecoder
{
    // Markers. Those from 0xFF30 to 0xFF3F have no segment; every other one is followed by a segment length.
    private const int StartOfCodestream = 0xFF4F;      // SOC
    private const int ImageAndTileSize = 0xFF51;       // SIZ
    private const int CodingStyleDefault = 0xFF52;     // COD
    private const int CodingStyleComponent = 0xFF53;   // COC
    private const int QuantizationDefault = 0xFF5C;    // QCD
    private const int QuantizationComponent = 0xFF5D;  // QCC
    private const int RegionOfInterest = 0xFF5E;       // RGN
    private const int ProgressionOrderChange = 0xFF5F; // POC
    private const int PackedHeadersMain = 0xFF60;      // PPM
    private const int PackedHeadersTile = 0xFF61;      // PPT
    private const int StartOfTile = 0xFF90;            // SOT
    private const int StartOfPacket = 0xFF91;          // SOP
    private const int EndOfPacketHeader = 0xFF92;      // EPH
    private const int StartOfData = 0xFF93;            // SOD
    private const int EndOfCodestream = 0xFFD9;        // EOC
    private const int FirstMarkerWithoutSegment = 0xFF30, LastMarkerWithoutSegment = 0xFF3F;

    private const int MarkerPrefix = 0xFF00, MarkerLength = 2, SegmentLengthLength = 2;

    // Segment sizes, without the marker: SIZ has 36 bytes and 3 per component, SOT is 10 bytes, a COD is at least 10
    // (its own 5 bytes and 5 of code-block parameters) and a POC entry has 5 bytes and two component indexes.
    private const int SizFixedLength = 36, SizComponentLength = 3, StartOfTileLength = 10;
    private const int CodMinLength = 10, ComponentStyleMinLength = 5;
    private const int ProgressionChangeFixedLength = 5;

    // Ssiz, and the column depths of a JP2 palette: the bit depth - 1, and a flag for signed values.
    private const int DepthMask = 0x7F, SignedDepth = 0x80, MaxPrecision = 30;

    // Scod and Scoc flags.
    private const int CustomPrecincts = 1, UsesStartOfPacket = 2, UsesEndOfPacketHeader = 4;

    // Code-block sizes are stored as log2 - 2 and may be at most 2^10, and 2^12 samples together. Without custom
    // precinct sizes precincts are 2^15 samples wide, which in effect means one per resolution.
    private const int CodeBlockSizeOffset = 2, MaxCodeBlockSizeLog2 = 10, MaxCodeBlockAreaLog2 = 12, DefaultPrecinctLog2 = 15;
    private const int LargestPrecincts = 0xFF; // 2^15 by 2^15, for resolutions whose precinct size is missing
    private const int ReversibleTransform = 1; // the 5/3 wavelet
    private const int FirstThreeComponents = 1; // the multiple component transform applies to components 0 to 2

    // Sqcd and Sqcc: guard bits in the top 3 bits, the quantization style in the low 5. Without quantization each band
    // has an 8-bit exponent field (the exponent in its top 5 bits); otherwise a 16-bit one with an 11-bit mantissa.
    private const int GuardBitsShift = 5, QuantizationStyleMask = 0x1F, UnquantizedExponentShift = 3, ExponentShift = 11, MantissaMask = 0x7FF;
    private const int NoQuantization = 0, ScalarDerived = 1;

    // Component indexes in COC, QCC, RGN and POC take one byte, or two in an image of more than 256 components.
    private const int OneByteComponentIndexLimit = 256;

    /// <summary>The COD parameters that apply to all components of a tile.</summary>
    private sealed record GeneralCodingStyle(bool StartOfPacketMarkers, bool EndOfPacketHeaderMarkers, int Progression, int Layers, int ComponentTransform);

    /// <summary>The COD parameters that a COC segment can set per component.</summary>
    private sealed record ComponentCodingStyle(
        int Levels, int CodeBlockWidthLog2, int CodeBlockHeightLog2, int CodeBlockStyle, bool Reversible, int[] PrecinctWidthLog2, int[] PrecinctHeightLog2);

    /// <summary>A QCD or QCC segment: guard bits, the quantization style and each sub-band's exponent and mantissa.</summary>
    private sealed record Quantization(int GuardBits, int Style, int[] Exponents, int[] Mantissas);

    /// <summary>A POC entry: packets from the first resolution and component up to the end ones, in an order.</summary>
    private readonly record struct ProgressionChange(int FirstResolution, int FirstComponent, int EndLayer, int EndResolution, int EndComponent, int Order);

    /// <summary>The coding parameters of the main header, or of a tile's own header, which override the main header's.</summary>
    private sealed class CodingParameters
    {
        public GeneralCodingStyle? General;
        public ComponentCodingStyle? Style;
        public readonly Dictionary<int, ComponentCodingStyle> ComponentStyles = new();
        public Quantization? Quantization;
        public readonly Dictionary<int, Quantization> ComponentQuantizations = new();
        public readonly Dictionary<int, int> RoiShifts = new();
        public readonly List<ProgressionChange> ProgressionChanges = new();
    }

    /// <summary>A tile as its tile-parts describe it.</summary>
    private sealed class TileData(int index)
    {
        public int Index { get; } = index;

        /// <summary>The packet data of each tile-part, as positions in the data.</summary>
        public List<(int Start, int End)> Parts { get; } = new();

        /// <summary>The parameters of the tile's first tile-part header; later tile-parts can only add progression changes.</summary>
        public CodingParameters Parameters { get; } = new();

        /// <summary>The packed packet headers of the PPT segments, in order.</summary>
        public List<byte[]> PackedHeaders { get; } = new();

        /// <summary>The main header's packed packet headers for each of the tile's tile-parts.</summary>
        public List<byte[]> MainPackedHeaders { get; } = new();

        public bool FirstPartRead { get; set; }
    }

    /// <summary>Where the body of a marker segment lies in the data.</summary>
    private readonly record struct Segment(int Start, int End)
    {
        public int Length => End - Start;
    }

    private readonly CodingParameters _main = new();
    private bool _sizRead;
    private readonly Dictionary<int, TileData> _tiles = new();
    private readonly List<TileData> _tileOrder = new(); // in the order of their first tile-part
    private TileData? _tile; // the tile of the latest tile-part, or null while in the main header
    private readonly SortedDictionary<int, byte[]> _mainPackedHeaders = new(); // PPM segments by their index
    private Queue<byte[]>? _mainPackedHeaderParts;

    private int TilesAcross => (int)Math.Max(1, ((long)_imageX1 - _tileX0 + _tileWidth - 1) / _tileWidth);
    private int TilesDown => (int)Math.Max(1, ((long)_imageY1 - _tileY0 + _tileHeight - 1) / _tileHeight);

    private int U16At(int at) => _data[at] << 8 | _data[at + 1];

    private static bool IsMarker(int value) => (value & MarkerPrefix) == MarkerPrefix;

    /// <summary>
    /// Reads the main header and the tile-parts, then decodes the tiles. Returns false when the codestream cannot be
    /// decoded at all.
    /// </summary>
    private bool DecodeCodestream(int start, int end)
    {
        int pos = start + MarkerLength; // past SOC, which Decode found or the JP2 codestream box implies
        while (pos + MarkerLength <= end)
        {
            int marker = U16At(pos);
            if (marker == EndOfCodestream) break;
            // Data that is not a marker ends the codestream; the tiles read so far still decode.
            if (!IsMarker(marker)) return _sizRead && _tileOrder.Count > 0 && DecodeTiles();
            pos += MarkerLength;
            if (marker is >= FirstMarkerWithoutSegment and <= LastMarkerWithoutSegment) continue;
            if (pos + SegmentLengthLength > end) break;
            int length = U16At(pos);
            if (length < SegmentLengthLength || pos + length > end) break;
            var segment = new Segment(pos + SegmentLengthLength, pos + length);
            if (marker == StartOfTile)
            {
                if (!_sizRead || length < StartOfTileLength) return false;
                var reader = new JpxReader(_data, segment.Start);
                int index = reader.U16();
                long partLength = reader.U32();
                // A tile index past the last tile ends the codestream.
                if (index >= TilesAcross * TilesDown) return _tileOrder.Count > 0 && DecodeTiles();
                pos = ReadTilePart(index, pos - MarkerLength, partLength, segment.End, end);
                continue;
            }
            if (!ReadHeaderSegment(marker, segment)) return false;
            pos = segment.End;
        }
        return _sizRead && _main.Style != null && _main.Quantization != null && DecodeTiles();
    }

    /// <summary>
    /// Reads a marker segment of the main header, or one between tile-parts. Returns false when the codestream cannot
    /// be decoded.
    /// </summary>
    private bool ReadHeaderSegment(int marker, Segment segment)
    {
        switch (marker)
        {
            case ImageAndTileSize:
                if (!ReadSiz(segment)) return false;
                _sizRead = true;
                return true;
            case CodingStyleDefault or CodingStyleComponent or QuantizationDefault or QuantizationComponent or RegionOfInterest:
                // Only QCD may come before SIZ. Between tile-parts these segments set nothing, but they are still read,
                // and a bad COD there fails the codestream too.
                if (!_sizRead && marker != QuantizationDefault) return false;
                return ReadCodingSegment(marker, segment, _tile == null ? _main : null);
            case ProgressionOrderChange:
                if (!_sizRead) return false;
                ReadProgressionChanges(segment, (_tile?.Parameters ?? _main).ProgressionChanges);
                return true;
            case PackedHeadersMain:
                if (_tile == null && segment.Length > 0)
                {
                    int index = _data[segment.Start];
                    _mainPackedHeaders[index] = PackedHeaderBytes(segment);
                }
                return true;
            case PackedHeadersTile:
                if (_tile != null && segment.Length > 0) _tile.PackedHeaders.Add(PackedHeaderBytes(segment));
                return true;
            default:
                // TLM, PLM, PLT, CRG, COM and unknown marker segments carry nothing the decoder needs.
                return true;
        }
    }

    /// <summary>The packed packet headers of a PPM or PPT segment, after its index byte.</summary>
    private byte[] PackedHeaderBytes(Segment segment) => _data.AsSpan(segment.Start + 1, segment.Length - 1).ToArray();

    /// <summary>
    /// Reads a tile-part from its SOT segment: its header, and where its packet data lies. Returns the position after
    /// the tile-part.
    /// </summary>
    private int ReadTilePart(int index, int partStart, long partLength, int headerStart, int end)
    {
        // The main header ends at the first tile-part. Its packed headers are dealt out to the tile-parts in order.
        if (_tile == null && _mainPackedHeaders.Count > 0) _mainPackedHeaderParts = SplitMainPackedHeaders();
        if (!_tiles.TryGetValue(index, out var tile))
        {
            tile = new TileData(index);
            _tiles[index] = tile;
            _tileOrder.Add(tile);
        }
        _tile = tile;
        // A tile-part runs from its SOT marker for its length, or to the end of the codestream when the length is 0.
        int partEnd = partLength == 0 ? end : (int)Math.Min(end, partStart + partLength);
        if (_mainPackedHeaderParts is { Count: > 0 }) tile.MainPackedHeaders.Add(_mainPackedHeaderParts.Dequeue());
        ReadTilePartHeader(tile, headerStart, partEnd);
        tile.FirstPartRead = true;
        return partEnd;
    }

    /// <summary>Reads the marker segments of a tile-part header up to SOD, after which the tile-part's packet data runs to its end.</summary>
    private void ReadTilePartHeader(TileData tile, int pos, int partEnd)
    {
        while (pos + MarkerLength + SegmentLengthLength <= partEnd)
        {
            int marker = U16At(pos);
            if (marker == StartOfData)
            {
                tile.Parts.Add((pos + MarkerLength, partEnd));
                return;
            }
            int length = U16At(pos + MarkerLength);
            if (!IsMarker(marker) || length < SegmentLengthLength || pos + MarkerLength + length > partEnd) return;
            var segment = new Segment(pos + MarkerLength + SegmentLengthLength, pos + MarkerLength + length);
            switch (marker)
            {
                case CodingStyleDefault or CodingStyleComponent or QuantizationDefault or QuantizationComponent or RegionOfInterest:
                    // Only the first tile-part of a tile sets its coding parameters; a bad COD there is ignored.
                    if (!tile.FirstPartRead) ReadCodingSegment(marker, segment, tile.Parameters);
                    break;
                case ProgressionOrderChange:
                    ReadProgressionChanges(segment, tile.Parameters.ProgressionChanges);
                    break;
                case PackedHeadersTile:
                    if (segment.Length > 0) tile.PackedHeaders.Add(PackedHeaderBytes(segment));
                    break;
            }
            pos = segment.End;
        }
    }

    /// <summary>
    /// Splits the main header's packed packet headers into those of each tile-part, in codestream order. They are a run
    /// of (length, headers) pairs, which the PPM segments may split anywhere.
    /// </summary>
    private Queue<byte[]> SplitMainPackedHeaders()
    {
        var all = Concat(_mainPackedHeaders.Values);
        var parts = new Queue<byte[]>();
        int pos = 0;
        while (pos + 4 <= all.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(all.AsSpan(pos));
            pos += 4;
            if (length < 0 || pos + length > all.Length) length = all.Length - pos; // the last part takes what is left
            parts.Enqueue(new ArraySegment<byte>(all, pos, length).ToArray());
            pos += length;
        }
        return parts;
    }

    private static byte[] Concat(ICollection<byte[]> parts)
    {
        int total = 0;
        foreach (var part in parts) total += part.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, pos);
            pos += part.Length;
        }
        return result;
    }

    private bool ReadSiz(Segment segment)
    {
        if (segment.Length < SizFixedLength) return false;
        var reader = new JpxReader(_data, segment.Start);
        reader.Skip(2); // Rsiz, the profile, which does not change how the codestream decodes
        _imageX1 = reader.I32();
        _imageY1 = reader.I32();
        _imageX0 = reader.I32();
        _imageY0 = reader.I32();
        _tileWidth = reader.I32();
        _tileHeight = reader.I32();
        _tileX0 = reader.I32();
        _tileY0 = reader.I32();
        int count = reader.U16();
        if (_imageX1 <= 0 || _imageY1 <= 0 || _imageX0 < 0 || _imageY0 < 0 || _imageX0 >= _imageX1 || _imageY0 >= _imageY1) return false;
        // The first tile must start at or before the image and reach into it.
        if (_tileWidth <= 0 || _tileHeight <= 0 || _tileX0 < 0 || _tileY0 < 0 || _tileX0 > _imageX0 || _tileY0 > _imageY0
            || (long)_tileX0 + _tileWidth <= _imageX0 || (long)_tileY0 + _tileHeight <= _imageY0)
            return false;
        if (count < 1 || count > MaxComponents || segment.Start + SizFixedLength + SizComponentLength * count > segment.End) return false;
        if ((long)(_imageX1 - _imageX0) * (_imageY1 - _imageY0) > PdfImageDecoder.MaxPixels) return false;
        if ((long)TilesAcross * TilesDown > MaxTiles) return false;
        _components = new ImageComponent[count];
        for (int i = 0; i < count; i++)
        {
            int depth = reader.U8();
            var component = new ImageComponent(Precision: (depth & DepthMask) + 1, Signed: (depth & SignedDepth) != 0, SubsamplingX: reader.U8(), SubsamplingY: reader.U8());
            if (component.SubsamplingX < 1 || component.SubsamplingY < 1 || component.Precision > MaxPrecision) return false;
            _components[i] = component;
        }
        _planes = new ComponentPlane[count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var c = _components[i];
            int x0 = CeilDiv(_imageX0, c.SubsamplingX), y0 = CeilDiv(_imageY0, c.SubsamplingY);
            var plane = _planes[i] = new ComponentPlane(x0, y0, CeilDiv(_imageX1, c.SubsamplingX) - x0, CeilDiv(_imageY1, c.SubsamplingY) - y0);
            total += (long)plane.Width * plane.Height;
        }
        if (total > int.MaxValue / 2) return false;
        PdfFile.ChargeDecoded(total);
        foreach (var plane in _planes) plane.Samples = new byte[plane.Width * plane.Height];
        return true;
    }

    /// <summary>
    /// Reads a COD, COC, QCD, QCC or RGN segment into <paramref name="target"/>, or only reads it when the target is null.
    /// Returns false for a COD segment that cannot be decoded.
    /// </summary>
    private bool ReadCodingSegment(int marker, Segment segment, CodingParameters? target)
    {
        switch (marker)
        {
            case CodingStyleDefault:
            {
                if (ReadCod(segment) is not { } cod) return false;
                if (target != null) (target.General, target.Style) = cod;
                break;
            }
            case CodingStyleComponent:
            {
                var (component, style) = ReadCoc(segment);
                if (style != null && component < _components.Length && target != null) target.ComponentStyles[component] = style;
                break;
            }
            case QuantizationDefault:
            {
                var quantization = ReadQuantization(segment.Start, segment.End);
                if (target != null) target.Quantization = quantization;
                break;
            }
            case QuantizationComponent:
            {
                // An empty segment takes its component index from the byte after it, which is usually the next
                // marker's 0xFF and so names no component.
                var reader = new JpxReader(_data, segment.Start);
                int component = ReadComponentIndex(ref reader);
                if (component >= _components.Length) break;
                var quantization = ReadQuantization(reader.Position, segment.End);
                if (target != null) target.ComponentQuantizations[component] = quantization;
                break;
            }
            case RegionOfInterest:
            {
                // The component, the ROI style (always the max shift method) and the shift.
                if (segment.Start + ComponentIndexLength + 2 > segment.End) break;
                var reader = new JpxReader(_data, segment.Start);
                int component = ReadComponentIndex(ref reader);
                reader.Skip(1);
                int shift = reader.U8();
                if (target != null) target.RoiShifts[component] = shift;
                break;
            }
        }
        return true;
    }

    private int ComponentIndexLength => _components.Length <= OneByteComponentIndexLimit ? 1 : 2;

    private int ReadComponentIndex(ref JpxReader reader) => ComponentIndexLength == 1 ? reader.U8() : reader.U16();

    /// <summary>Reads a COD segment, or returns null when it has no layers or bad code-block parameters.</summary>
    private (GeneralCodingStyle General, ComponentCodingStyle Style)? ReadCod(Segment segment)
    {
        if (segment.Length < CodMinLength) return null;
        var reader = new JpxReader(_data, segment.Start);
        int flags = reader.U8();
        var general = new GeneralCodingStyle(
            StartOfPacketMarkers: (flags & UsesStartOfPacket) != 0, EndOfPacketHeaderMarkers: (flags & UsesEndOfPacketHeader) != 0,
            Progression: reader.U8(), Layers: reader.U16(), ComponentTransform: reader.U8());
        if (general.Layers < 1) return null;
        var style = ReadComponentCodingStyle(reader.Position, segment.End, (flags & CustomPrecincts) != 0);
        return style == null ? null : (general, style);
    }

    private (int Component, ComponentCodingStyle? Style) ReadCoc(Segment segment)
    {
        if (segment.Length < ComponentIndexLength + 1) return (0, null);
        var reader = new JpxReader(_data, segment.Start);
        int component = ReadComponentIndex(ref reader);
        int flags = reader.U8();
        return (component, ReadComponentCodingStyle(reader.Position, segment.End, (flags & CustomPrecincts) != 0));
    }

    /// <summary>Reads the SPcod or SPcoc fields, or returns null when the levels or code-block size are out of range.</summary>
    private ComponentCodingStyle? ReadComponentCodingStyle(int start, int end, bool customPrecincts)
    {
        if (end - start < ComponentStyleMinLength) return null;
        var reader = new JpxReader(_data, start);
        int levels = reader.U8();
        int blockWidth = (reader.U8() & 0xF) + CodeBlockSizeOffset, blockHeight = (reader.U8() & 0xF) + CodeBlockSizeOffset;
        int blockStyle = reader.U8();
        bool reversible = reader.U8() == ReversibleTransform;
        if (levels > MaxLevels || blockWidth > MaxCodeBlockSizeLog2 || blockHeight > MaxCodeBlockSizeLog2 || blockWidth + blockHeight > MaxCodeBlockAreaLog2)
            return null;
        // One precinct size per resolution, the width in the low 4 bits; missing ones are the largest.
        var precinctWidth = new int[levels + 1];
        var precinctHeight = new int[levels + 1];
        for (int r = 0; r <= levels; r++)
        {
            if (customPrecincts)
            {
                int size = reader.Position < end ? reader.U8() : LargestPrecincts;
                precinctWidth[r] = size & 0xF;
                precinctHeight[r] = size >> 4;
            }
            else
            {
                precinctWidth[r] = DefaultPrecinctLog2;
                precinctHeight[r] = DefaultPrecinctLog2;
            }
        }
        return new ComponentCodingStyle(levels, blockWidth, blockHeight, blockStyle, reversible, precinctWidth, precinctHeight);
    }

    private Quantization ReadQuantization(int start, int end)
    {
        if (start >= end) return new Quantization(0, 0, Array.Empty<int>(), Array.Empty<int>());
        var reader = new JpxReader(_data, start);
        int flags = reader.U8();
        int style = flags & QuantizationStyleMask;
        int[] exponents, mantissas;
        if (style == NoQuantization)
        {
            exponents = new int[end - reader.Position];
            mantissas = new int[exponents.Length];
            for (int i = 0; i < exponents.Length; i++) exponents[i] = reader.U8() >> UnquantizedExponentShift;
        }
        else
        {
            exponents = new int[(end - reader.Position) / 2];
            mantissas = new int[exponents.Length];
            for (int i = 0; i < exponents.Length; i++)
            {
                int value = reader.U16();
                exponents[i] = value >> ExponentShift;
                mantissas[i] = value & MantissaMask;
            }
        }
        return new Quantization(flags >> GuardBitsShift, style, exponents, mantissas);
    }

    private void ReadProgressionChanges(Segment segment, List<ProgressionChange> changes)
    {
        int entryLength = ProgressionChangeFixedLength + 2 * ComponentIndexLength;
        var reader = new JpxReader(_data, segment.Start);
        while (reader.Position + entryLength <= segment.End)
        {
            int firstResolution = reader.U8(), firstComponent = ReadComponentIndex(ref reader);
            int endLayer = reader.U16(), endResolution = reader.U8(), endComponent = ReadComponentIndex(ref reader);
            int order = reader.U8();
            // An end component of 0 stands for 256.
            changes.Add(new ProgressionChange(firstResolution, firstComponent, endLayer, endResolution, endComponent == 0 ? 256 : endComponent, order));
        }
    }
}

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace PdfPecker.Rendering.Images;

// Segment headers (7.2), region segment information (7.4.1) and page information (7.4.8).
internal sealed partial class Jbig2Decoder
{
    /// <summary>The segment types of 7.3 that the decoder knows; any other value is skipped.</summary>
    private enum SegmentType
    {
        SymbolDictionary = 0,
        IntermediateTextRegion = 4,
        ImmediateTextRegion = 6,
        ImmediateLosslessTextRegion = 7,
        PatternDictionary = 16,
        IntermediateHalftoneRegion = 20,
        ImmediateHalftoneRegion = 22,
        ImmediateLosslessHalftoneRegion = 23,
        IntermediateGenericRegion = 36,
        ImmediateGenericRegion = 38,
        ImmediateLosslessGenericRegion = 39,
        IntermediateRefinementRegion = 40,
        ImmediateRefinementRegion = 42,
        ImmediateLosslessRefinementRegion = 43,
        PageInformation = 48,
        EndOfPage = 49,
        EndOfStripe = 50,
        EndOfFile = 51,
        Profiles = 52,
        Tables = 53,
        Extension = 62,
    }

    // The shortest header: segment number, flags, one byte of referred-segment count, a one-byte page association and
    // the data length.
    private const int MinSegmentHeaderLength = 11;

    // A count of 7 in the first three bits says the count is a 29-bit number in the long form instead (7.2.4).
    private const int LongFormReferredCount = 7;
    private const uint LongFormCountMask = 0x1FFFFFFF;
    private const int MaxReferredSegments = 1 << 16;

    // A data length of all ones means unknown, which only a generic region may use (7.2.7).
    private const uint UnknownDataLength = 0xFFFFFFFF;

    private const int RegionInfoLength = 17;
    private const int PageInfoLength = 19;
    private const uint UnknownPageHeight = 0xFFFFFFFF;

    /// <summary>The flags byte of a segment header (7.2.3).</summary>
    private readonly record struct SegmentHeaderFlags(int Bits)
    {
        public SegmentType Type => (SegmentType)(Bits & 0x3F);

        /// <summary>Whether the page association is four bytes rather than one.</summary>
        public bool LargePageAssociation => (Bits & 0x40) != 0;

        public int PageAssociationSize => LargePageAssociation ? 4 : 1;
    }

    /// <summary>A segment: its header fields and where its data lies in the stream.</summary>
    private sealed record Segment(int Number, SegmentType Type, int Page, int[] Refs, byte[] Data, int Start, int End)
    {
        public int Length => End - Start;

        /// <summary>An intermediate region is kept for a later refinement rather than drawn on the page.</summary>
        public bool IsIntermediateRegion => Type is SegmentType.IntermediateTextRegion or SegmentType.IntermediateHalftoneRegion
            or SegmentType.IntermediateGenericRegion or SegmentType.IntermediateRefinementRegion;

        public SegmentReader Reader() => new(Data, Start);
    }

    /// <summary>
    /// Reads the segment header at <paramref name="position"/> and moves past its data. Returns false at the end of the
    /// stream or at a header that cannot be read, which ends the stream.
    /// </summary>
    private static bool TryReadSegment(byte[] data, ref int position, [NotNullWhen(true)] out Segment? segment)
    {
        segment = null;
        if (position + MinSegmentHeaderLength > data.Length) return false;
        var reader = new SegmentReader(data, position);
        int number = (int)reader.U32();
        var flags = new SegmentHeaderFlags(reader.U8());
        if (!TryReadReferredSegments(ref reader, number, out var refs)) return false;
        if (reader.Remaining < flags.PageAssociationSize + 4) return false;
        int page = flags.LargePageAssociation ? (int)reader.U32() : reader.U8();
        uint length = reader.U32();
        int start = reader.Position, end;
        if (length == UnknownDataLength)
        {
            if (flags.Type is not (SegmentType.IntermediateGenericRegion or SegmentType.ImmediateGenericRegion
                or SegmentType.ImmediateLosslessGenericRegion)) return false;
            end = FindGenericEnd(data, start);
        }
        else end = (int)Math.Min(data.Length, start + (long)length);
        position = end;
        segment = new Segment(number, flags.Type, page, refs, data, start, end);
        return true;
    }

    /// <summary>Reads the referred-to segment count, the retention flags and the referred segment numbers (7.2.4, 7.2.5).</summary>
    private static bool TryReadReferredSegments(ref SegmentReader reader, int number, [NotNullWhen(true)] out int[]? refs)
    {
        refs = null;
        int count = reader.PeekU8() >> 5;
        if (count == LongFormReferredCount)
        {
            if (reader.Remaining < 4) return false;
            count = (int)(reader.U32() & LongFormCountMask);
            if (count > MaxReferredSegments) return false;
            reader.Skip((count + 8) / 8); // a retention bit for this segment and each referred one
        }
        else reader.Skip(1); // the count shares its byte with the retention bits
        // Referred segments come before this one, so their numbers take no more bytes than this one needs.
        int refSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        var numbers = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (reader.Remaining < refSize) return false;
            numbers[i] = refSize switch
            {
                1 => reader.U8(),
                2 => reader.U16(),
                _ => (int)reader.U32(),
            };
        }
        refs = numbers;
        return true;
    }

    private static int FindGenericEnd(byte[] d, int start)
    {
        // An immediate generic region of unknown length ends with 0xFF 0xAC (arithmetic) or 0x00 0x00 (MMR),
        // followed by a 4-byte row count.
        int p = start + RegionInfoLength + 1;
        if (p >= d.Length) return d.Length;
        bool mmr = new GenericRegionFlags(d[p - 1]).Mmr;
        for (int i = p; i + 1 < d.Length; i++)
        {
            if (mmr ? d[i] == 0 && d[i + 1] == 0 : d[i] == 0xFF && d[i + 1] == 0xAC)
                return Math.Min(d.Length, i + 6);
        }
        return d.Length;
    }

    /// <summary>
    /// A big-endian read position in a JBIG2 stream, for the fixed fields of a segment. The fields are bounded by the
    /// stream rather than the segment, as the decoder has always read them: a multi-byte field past the end of the stream
    /// reads as zero, and a single byte there throws <see cref="IndexOutOfRangeException"/>, which <see cref="Decode"/>
    /// takes as damaged data. Callers that must stay inside the segment check its length first.
    /// </summary>
    private ref struct SegmentReader(ReadOnlySpan<byte> data, int position)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; } = position;

        /// <summary>The bytes left in the stream, negative once a skip has run past its end.</summary>
        public readonly int Remaining => _data.Length - Position;

        public void Skip(int count) => Position += count;

        public readonly int PeekU8() => _data[Position];

        public byte U8() => _data[Position++];

        /// <summary>A byte, or zero past the end of the stream.</summary>
        public byte U8OrZero()
        {
            byte value = Position < _data.Length ? _data[Position] : (byte)0;
            Position++;
            return value;
        }

        public sbyte S8() => (sbyte)U8();

        /// <summary>A signed byte, or zero at or past <paramref name="end"/>.</summary>
        public sbyte S8Before(int end)
        {
            sbyte value = Position < end ? (sbyte)_data[Position] : (sbyte)0;
            Position++;
            return value;
        }

        public int U16()
        {
            int value = Position + 1 < _data.Length ? BinaryPrimitives.ReadUInt16BigEndian(_data.Slice(Position)) : 0;
            Position += 2;
            return value;
        }

        public uint U32()
        {
            uint value = U32At(Position);
            Position += 4;
            return value;
        }

        public int S32() => (int)U32();

        public readonly uint U32At(int at) => at + 3 < _data.Length ? BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(at)) : 0;
    }

    /// <summary>The region segment information field (7.4.1): the region's size, place and combination operator.</summary>
    private readonly record struct RegionInfo(int W, int H, int X, int Y, CombinationOperator Op)
    {
        public static RegionInfo Read(ref SegmentReader reader) => new(
            W: (int)Math.Min(reader.U32(), int.MaxValue),
            H: (int)Math.Min(reader.U32(), int.MaxValue),
            X: reader.S32(),
            Y: reader.S32(),
            Op: (CombinationOperator)(reader.U8OrZero() & 7));
    }

    /// <summary>The flags byte of a page information segment (7.4.8.5).</summary>
    private readonly record struct PageInfoFlags(int Bits)
    {
        public int DefaultPixel => (Bits >> 2) & 1;
    }

    private void PageInfo(Segment seg, int pdfHeight)
    {
        if (seg.Length < PageInfoLength) throw new InvalidDataException("Short JBIG2 page information.");
        var reader = seg.Reader();
        uint w = reader.U32(), h = reader.U32();
        reader.Skip(8); // the resolution
        _pageDefault = new PageInfoFlags(reader.U8()).DefaultPixel;
        if (h == UnknownPageHeight) h = (uint)Math.Max(1, pdfHeight);
        if (w == 0 || w > int.MaxValue || h > int.MaxValue) throw new InvalidDataException("Bad JBIG2 page size.");
        _page = NewBitmap((int)w, (int)h);
        _page.Fill(_pageDefault);
    }
}

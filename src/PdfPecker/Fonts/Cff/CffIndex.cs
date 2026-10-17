using System.Buffers.Binary;

namespace PdfPecker.Fonts.Cff;

/// <summary>
/// An INDEX of a CFF or CFF2 table: a count, then the offsets of its items, then their data. The offsets are kept as
/// absolute positions in the table, so an item is a plain range of it.
/// </summary>
internal readonly struct CffIndex
{
    // CFF (version 1) INDEXes count their items in 16 bits, CFF2 ones in 32.
    private const int CountSize = 2, Cff2CountSize = 4;
    private const int MinOffsetSize = 1, MaxOffsetSize = 4;
    private const int OffsetSizeFieldLength = 1;

    private readonly int[]? _offsets; // Count + 1 absolute positions

    private CffIndex(int[] offsets, int start, int end, int dataStart)
    {
        _offsets = offsets;
        Start = start;
        End = end;
        DataStart = dataStart;
    }

    /// <summary>The first byte of the INDEX: its count.</summary>
    public int Start { get; }

    /// <summary>The first byte after the INDEX.</summary>
    public int End { get; }

    /// <summary>Where the data of the first item would start; items that start before it are damaged.</summary>
    public int DataStart { get; }

    public int Count => _offsets == null ? 0 : _offsets.Length - 1;

    /// <summary>
    /// The item's start and end, unchecked: callers that read it byte by byte treat an end before the start as an
    /// empty item, as the renderer always has.
    /// </summary>
    public (int Start, int End) Range(int i) => (_offsets![i], _offsets[i + 1]);

    /// <summary>The item's start and end; an item outside the INDEX's data is a damaged font.</summary>
    public (int Start, int End) CheckedRange(int i)
    {
        int start = _offsets![i], end = _offsets[i + 1];
        if (start < DataStart || end < start || end > End) throw new FormatException("Damaged CFF INDEX.");
        return (start, end);
    }

    /// <summary>The item's bytes, checked as by <see cref="CheckedRange"/>.</summary>
    public ReadOnlySpan<byte> Item(byte[] data, int i)
    {
        var (start, end) = CheckedRange(i);
        return data.AsSpan(start, end - start);
    }

    /// <summary>The whole INDEX as it is encoded, for copying it unchanged into another font.</summary>
    public byte[] Raw(byte[] data) => data.AsSpan(Start, End - Start).ToArray();

    /// <summary>Reads a CFF (version 1) INDEX, which has a 16-bit count, and moves past it.</summary>
    public static CffIndex Read(byte[] data, ref int pos)
    {
        int start = pos;
        int count = data[pos] << 8 | data[pos + 1];
        pos += CountSize;
        if (count == 0) return Empty(start, pos);
        int offsetSize = data[pos++];
        if (offsetSize < MinOffsetSize || offsetSize > MaxOffsetSize) throw new FormatException("Damaged CFF INDEX.");
        // Offsets count from the byte before the data, so offset 1 is the first data byte.
        int offsetsStart = pos;
        int dataStart = offsetsStart + (count + 1) * offsetSize;
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
            offsets[i] = dataStart - 1 + (int)ReadOffset(data, offsetsStart + i * offsetSize, offsetSize);
        // Only the last offset is checked here; an item is checked when it is read.
        pos = offsets[count];
        if (pos > data.Length || pos < dataStart) throw new FormatException("Damaged CFF INDEX.");
        return new CffIndex(offsets, start, pos, dataStart);
    }

    /// <summary>Reads a CFF2 INDEX, which has a 32-bit count and whose offsets are all checked up front.</summary>
    public static CffIndex ReadCff2(byte[] data, ref int pos)
    {
        int start = pos;
        if (pos < 0 || pos + Cff2CountSize > data.Length) throw new FormatException("Bad CFF2 INDEX.");
        long count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        pos += Cff2CountSize;
        if (count == 0) return Empty(start, pos);
        int offsetSize = data[pos++];
        if (offsetSize is < MinOffsetSize or > MaxOffsetSize || pos + (count + 1) * offsetSize > data.Length)
            throw new FormatException("Bad CFF2 INDEX.");
        int offsetsStart = pos;
        int dataStart = offsetsStart + (int)(count + 1) * offsetSize;
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            long absolute = dataStart - 1 + (long)ReadOffset(data, offsetsStart + i * offsetSize, offsetSize);
            if (absolute > data.Length || i > 0 && absolute < offsets[i - 1]) throw new FormatException("Bad CFF2 INDEX offset.");
            offsets[i] = (int)absolute;
        }
        pos = offsets[count];
        return new CffIndex(offsets, start, pos, dataStart);
    }

    /// <summary>Encodes a CFF (version 1) INDEX with the smallest offset size that fits.</summary>
    public static byte[] Write(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0) return new byte[CountSize];
        // Offsets are 1-based, so the last one is the data's length plus one.
        long lastOffset = 1;
        foreach (var item in items) lastOffset += item.Length;
        int offsetSize = lastOffset <= 0xFF ? 1 : lastOffset <= 0xFFFF ? 2 : lastOffset <= 0xFFFFFF ? 3 : 4;
        int offsetsStart = CountSize + OffsetSizeFieldLength;
        int dataStart = offsetsStart + (items.Count + 1) * offsetSize;
        var encoded = new byte[dataStart + lastOffset - 1];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)items.Count);
        encoded[CountSize] = (byte)offsetSize;
        int offset = 1, position = offsetsStart, dataPosition = dataStart;
        for (int i = 0; i <= items.Count; i++)
        {
            WriteOffset(encoded, position, offsetSize, offset);
            position += offsetSize;
            if (i == items.Count) break;
            items[i].CopyTo(encoded, dataPosition);
            dataPosition += items[i].Length;
            offset += items[i].Length;
        }
        return encoded;
    }

    private static CffIndex Empty(int start, int end) => new([end], start, end, end);

    // The offsets are big-endian numbers of one to four bytes; a large one can set the sign bit of the result.
    private static uint ReadOffset(byte[] data, int at, int size)
    {
        uint value = 0;
        for (int i = 0; i < size; i++) value = value << 8 | data[at + i];
        return value;
    }

    private static void WriteOffset(byte[] data, int at, int size, int value)
    {
        for (int i = 0; i < size; i++) data[at + i] = (byte)(value >> 8 * (size - 1 - i));
    }
}

using System.Buffers.Binary;

namespace PdfPecker.Fonts.Cff;

/// <summary>
/// An INDEX of a CFF or CFF2 table: a count, then the offsets of its items, then their data. The offsets are kept as
/// absolute positions in the table, so an item is a plain range of it.
/// </summary>
internal readonly struct CffIndex
{
    private readonly int[]? _offsets; // Count + 1 absolute positions

    /// <summary>The first byte of the INDEX (its count) and the first byte after it.</summary>
    public readonly int Start, End;

    /// <summary>Where the data of the first item would start; items that start before it are damaged.</summary>
    public readonly int DataStart;

    private CffIndex(int[] offsets, int start, int end, int dataStart)
    {
        _offsets = offsets;
        Start = start;
        End = end;
        DataStart = dataStart;
    }

    public int Count => _offsets == null ? 0 : _offsets.Length - 1;

    /// <summary>
    /// The item's start and end, unchecked: callers that read it byte by byte treat an end before the start as an
    /// empty item, as the renderer always has.
    /// </summary>
    public (int Start, int End) Range(int i) => (_offsets![i], _offsets[i + 1]);

    /// <summary>The item's start and end; an item outside the INDEX's data is a damaged font.</summary>
    public (int Start, int End) CheckedRange(int i)
    {
        int a = _offsets![i], b = _offsets[i + 1];
        if (a < DataStart || b < a || b > End) throw new FormatException("Damaged CFF INDEX.");
        return (a, b);
    }

    /// <summary>The item's bytes, checked as by <see cref="CheckedRange"/>.</summary>
    public ReadOnlySpan<byte> Item(byte[] d, int i)
    {
        var (a, b) = CheckedRange(i);
        return d.AsSpan(a, b - a);
    }

    /// <summary>The whole INDEX as it is encoded, for copying it unchanged into another font.</summary>
    public byte[] Raw(byte[] d) => d.AsSpan(Start, End - Start).ToArray();

    /// <summary>Reads a CFF (version 1) INDEX, which has a 16-bit count, and moves past it.</summary>
    public static CffIndex Read(byte[] d, ref int pos)
    {
        int start = pos;
        int count = d[pos] << 8 | d[pos + 1];
        pos += 2;
        if (count == 0) return new CffIndex(new[] { pos }, start, pos, pos);
        int offSize = d[pos++];
        if (offSize < 1 || offSize > 4) throw new FormatException("Damaged CFF INDEX.");
        // Offsets count from the byte before the data, so offset 1 is the first data byte.
        int dataStart = pos + (count + 1) * offSize;
        var offsets = new int[count + 1];
        for (int i = count; i >= 0; i--)
        {
            int v = 0;
            for (int k = 0; k < offSize; k++) v = v << 8 | d[pos + i * offSize + k];
            offsets[i] = dataStart - 1 + v;
        }
        // Only the last offset is checked here; an item is checked when it is read.
        pos = offsets[count];
        if (pos > d.Length || pos < dataStart) throw new FormatException("Damaged CFF INDEX.");
        return new CffIndex(offsets, start, pos, dataStart);
    }

    /// <summary>Reads a CFF2 INDEX, which has a 32-bit count and whose offsets are all checked up front.</summary>
    public static CffIndex ReadCff2(byte[] d, ref int pos)
    {
        int start = pos;
        if (pos < 0 || pos + 4 > d.Length) throw new FormatException("Bad CFF2 INDEX.");
        long count = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
        pos += 4;
        if (count == 0) return new CffIndex(new[] { pos }, start, pos, pos);
        int offSize = d[pos++];
        if (offSize is < 1 or > 4 || pos + (count + 1) * offSize > d.Length) throw new FormatException("Bad CFF2 INDEX.");
        int dataStart = pos + (int)(count + 1) * offSize;
        var offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            uint v = 0;
            for (int b = 0; b < offSize; b++) v = v << 8 | d[pos + i * offSize + b];
            long abs = dataStart - 1 + (long)v;
            if (abs > d.Length || i > 0 && abs < offsets[i - 1]) throw new FormatException("Bad CFF2 INDEX offset.");
            offsets[i] = (int)abs;
        }
        pos = offsets[count];
        return new CffIndex(offsets, start, pos, dataStart);
    }

    /// <summary>Encodes a CFF (version 1) INDEX with the smallest offset size that fits.</summary>
    public static byte[] Write(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0) return new byte[2];
        long total = 1;
        foreach (var it in items) total += it.Length;
        int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        var o = new byte[3 + (items.Count + 1) * offSize + total - 1];
        BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)items.Count);
        o[2] = (byte)offSize;
        int off = 1, p = 3, data = 3 + (items.Count + 1) * offSize;
        for (int i = 0; i <= items.Count; i++)
        {
            for (int k = offSize - 1; k >= 0; k--) o[p++] = (byte)(off >> (8 * k));
            if (i < items.Count)
            {
                items[i].CopyTo(o, data);
                data += items[i].Length;
                off += items[i].Length;
            }
        }
        return o;
    }
}

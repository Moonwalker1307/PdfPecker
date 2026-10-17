using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>Assembles sfnt font files from tables, with a sorted table directory and correct checksums.</summary>
internal static class SfntBuilder
{
    // The offset table: the sfnt version, the table count and the binary search fields of the directory.
    private const int OffsetTableLength = 12;
    private const int NumTablesField = 4, SearchRangeField = 6, EntrySelectorField = 8, RangeShiftField = 10;

    // Table records: tag, checksum, offset and length, each 32 bits.
    private const int TableRecordLength = 16;
    private const int RecordChecksumField = 4, RecordOffsetField = 8, RecordLengthField = 12;

    private const uint HeadTag = 0x68656164; // 'head'
    private const int CheckSumAdjustmentField = 8;
    // The whole file sums to this magic value once checkSumAdjustment is set.
    private const uint CheckSumMagic = 0xB1B0AFBA;

    /// <summary>
    /// Builds the file. Tables are sorted by tag and padded to four bytes. A head table's checkSumAdjustment is
    /// zeroed in place (it is computed over the file with the field zero) and then filled in.
    /// </summary>
    public static byte[] Build(uint version, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        int count = tables.Count;
        int size = OffsetTableLength + TableRecordLength * count;
        foreach (var table in tables) size += Padded(table.Data.Length);
        var file = new byte[size];
        var span = file.AsSpan();
        WriteOffsetTable(span, version, count);

        int offset = OffsetTableLength + TableRecordLength * count, headOffset = -1;
        for (int i = 0; i < count; i++)
        {
            var (tag, data) = tables[i];
            if (tag == HeadTag && data.Length >= CheckSumAdjustmentField + 4)
            {
                Array.Clear(data, CheckSumAdjustmentField, 4);
                headOffset = offset;
            }
            data.CopyTo(file, offset);
            int record = OffsetTableLength + i * TableRecordLength;
            BinaryPrimitives.WriteUInt32BigEndian(span[record..], tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + RecordChecksumField)..], Checksum(file.AsSpan(offset, Padded(data.Length))));
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + RecordOffsetField)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(record + RecordLengthField)..], (uint)data.Length);
            offset += Padded(data.Length);
        }
        if (headOffset >= 0) BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + CheckSumAdjustmentField)..], CheckSumMagic - Checksum(file));
        return file;
    }

    private static int Padded(int length) => (length + 3) & ~3;

    // searchRange, entrySelector and rangeShift describe a binary search over the directory: the largest power of two
    // of records that fits, its logarithm, and the records beyond it, all in bytes.
    private static void WriteOffsetTable(Span<byte> span, uint version, int count)
    {
        BinaryPrimitives.WriteUInt32BigEndian(span, version);
        BinaryPrimitives.WriteUInt16BigEndian(span[NumTablesField..], (ushort)count);
        int powerOfTwo = 1, log = 0;
        while (powerOfTwo * 2 <= count)
        {
            powerOfTwo *= 2;
            log++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(span[SearchRangeField..], (ushort)(powerOfTwo * TableRecordLength));
        BinaryPrimitives.WriteUInt16BigEndian(span[EntrySelectorField..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(span[RangeShiftField..], (ushort)(count * TableRecordLength - powerOfTwo * TableRecordLength));
    }

    /// <summary>The sfnt checksum: the sum of the data as big-endian 32-bit words, the last one padded with zeros.</summary>
    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (i < data.Length)
        {
            uint last = 0;
            for (int k = 0; k < 4; k++) last = last << 8 | (i + k < data.Length ? data[i + k] : 0u);
            sum += last;
        }
        return sum;
    }
}

using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfPecker.Fonts;

/// <summary>Unpacking of WOFF 1.0 files: each table is stored as is or zlib-compressed, and the sfnt is rebuilt from them.</summary>
internal sealed partial class FontProgram
{
    // WOFF header: the wrapped sfnt's version and the table count, then one directory entry per table.
    private const int WoffFlavorField = 4, WoffNumTablesField = 12, WoffHeaderLength = 44;
    private const int WoffEntryLength = 20, WoffEntryOffsetField = 4, WoffEntryCompressedLengthField = 8, WoffEntryOriginalLengthField = 12;

    /// <summary>The most a WOFF file may unpack to.</summary>
    private const long MaxWoffSize = 64L * 1024 * 1024;
    // zlib cannot expand data by more than about a thousandfold, so a larger claim is a damaged size.
    private const long MaxZlibExpansion = 1100, ZlibExpansionSlack = 1024;

    /// <summary>Unpacks a WOFF 1.0 file into the sfnt it wraps.</summary>
    private static byte[] DecodeWoff(byte[] woff)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(WoffNumTablesField));
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(WoffFlavorField));
        var tables = new List<(uint Tag, byte[] Data)>(numTables);
        long total = 0;
        for (int i = 0; i < numTables; i++)
        {
            var entry = woff.AsSpan(WoffHeaderLength + i * WoffEntryLength);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[WoffEntryOffsetField..]);
            int compressedLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[WoffEntryCompressedLengthField..]);
            int originalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[WoffEntryOriginalLengthField..]);
            if (offset < 0 || compressedLength < 0 || originalLength < 0 || (long)offset + compressedLength > woff.Length)
                throw new FormatException("Damaged WOFF table.");
            // The declared sizes are allocated up front: keep them to what zlib can produce from the data and to a sane
            // font size, so a small file cannot claim gigabytes.
            total += originalLength;
            if (total > MaxWoffSize || originalLength > (long)compressedLength * MaxZlibExpansion + ZlibExpansionSlack)
                throw new FormatException("Damaged WOFF table.");
            // A table whose compressed length equals its original length is stored uncompressed.
            byte[] data = compressedLength < originalLength
                ? Inflate(woff, offset, compressedLength, originalLength)
                : woff.AsSpan(offset, originalLength).ToArray();
            tables.Add((tag, data));
        }
        return SfntBuilder.Build(flavor, tables);
    }

    private static byte[] Inflate(byte[] woff, int offset, int compressedLength, int originalLength)
    {
        var data = new byte[originalLength];
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(woff, offset, compressedLength), CompressionMode.Decompress);
            zlib.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The stream reads from memory, so any I/O error is corrupt zlib data, which newer runtimes report
            // as ZLibException rather than InvalidDataException; to the caller it is a damaged font file.
            throw new FormatException("Damaged WOFF table.", ex);
        }
        return data;
    }
}

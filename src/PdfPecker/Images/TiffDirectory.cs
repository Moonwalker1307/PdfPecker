using System.Buffers.Binary;

namespace PdfPecker.Images;

/// <summary>The numbers of the TIFF tags the decoder reads.</summary>
internal static class TiffTag
{
    public const int ImageWidth = 256;
    public const int ImageLength = 257;
    public const int BitsPerSample = 258;
    public const int Compression = 259;
    public const int PhotometricInterpretation = 262;
    public const int FillOrder = 266;
    public const int StripOffsets = 273;
    public const int SamplesPerPixel = 277;
    public const int RowsPerStrip = 278;
    public const int StripByteCounts = 279;
    public const int PlanarConfiguration = 284;
    public const int T4Options = 292;
    public const int Predictor = 317;
    public const int ColorMap = 320;
    public const int TileWidth = 322;
    public const int TileLength = 323;
    public const int TileOffsets = 324;
    public const int TileByteCounts = 325;
    public const int ExtraSamples = 338;
    public const int SampleFormat = 339;
    public const int JpegTables = 347;
}

/// <summary>
/// One image file directory (IFD) of a TIFF file: its tags, read in the file's byte order. Offsets and counts that point
/// outside the file read as zero or as no values rather than failing, so a damaged directory looks like missing tags
/// and the decoder can treat it as any other incomplete image.
/// </summary>
internal sealed class TiffDirectory
{
    private const int FirstDirectoryOffset = 4;
    private const int HeaderLength = 8;
    private const int EntryLength = 12;
    private const int MaxValues = 1 << 20;

    // Each directory ends with the offset of the next one; this bound stops a directory chain that loops.
    private const int MaxDirectorySkips = 1 << 16;

    private readonly byte[] data;
    private readonly Dictionary<int, Entry> entries;

    /// <summary>A tag's field type, its number of values, and where its value (or the offset of its values) is stored.</summary>
    private readonly record struct Entry(int Type, long Count, long ValueOffset);

    private TiffDirectory(byte[] data, Dictionary<int, Entry> entries)
    {
        this.data = data;
        this.entries = entries;
    }

    /// <summary>True for an "II" (Intel) file, whose numbers are stored least significant byte first.</summary>
    public bool LittleEndian => IsLittleEndian(data);

    /// <summary>
    /// Reads directory number <paramref name="page"/>, counted from 0, of data that starts with a TIFF header. Returns
    /// null when the chain of directories ends or leaves the file before it.
    /// </summary>
    public static TiffDirectory? Read(byte[] data, int page)
    {
        long offset = ReadUInt32(data, FirstDirectoryOffset);
        for (int skipped = 0; skipped < page && skipped < MaxDirectorySkips; skipped++)
        {
            if (!StartsDirectory(data, offset)) return null;
            offset = ReadUInt32(data, offset + 2 + ReadUInt16(data, offset) * (long)EntryLength);
        }
        if (!StartsDirectory(data, offset)) return null;

        int count = ReadUInt16(data, offset);
        var entries = new Dictionary<int, Entry>();
        for (int i = 0; i < count; i++)
        {
            long at = offset + 2 + i * (long)EntryLength;
            if (at + EntryLength > data.Length) break; // a directory cut off by the end of the file keeps its whole entries
            entries[ReadUInt16(data, at)] = new Entry(ReadUInt16(data, at + 2), ReadUInt32(data, at + 4), at + 8);
        }
        return new TiffDirectory(data, entries);
    }

    public bool Contains(int tag) => entries.ContainsKey(tag);

    /// <summary>The tag's value when it holds exactly one number, otherwise <paramref name="fallback"/>.</summary>
    public long Single(int tag, long fallback)
        => entries.TryGetValue(tag, out var entry) && ReadValues(entry, 1) is { Length: 1 } values ? values[0] : fallback;

    /// <summary>The tag's numbers, or none when the tag is missing, not numeric, too long or outside the file.</summary>
    public long[] Values(int tag)
        => entries.TryGetValue(tag, out var entry) ? ReadValues(entry, MaxValues) : Array.Empty<long>();

    /// <summary>
    /// The tag's raw bytes whatever its field type, none when they lie outside the file, or null when the tag is
    /// missing.
    /// </summary>
    public byte[]? Bytes(int tag)
    {
        if (!entries.TryGetValue(tag, out var entry)) return null;
        if (entry.Count <= 0 || entry.Count > data.Length) return Array.Empty<byte>();
        long at = entry.Count <= 4 ? entry.ValueOffset : ReadUInt32(data, entry.ValueOffset);
        return at < 0 || at + entry.Count > data.Length ? Array.Empty<byte>() : data.AsSpan((int)at, (int)entry.Count).ToArray();
    }

    /// <summary>Reads a 16-bit number at <paramref name="offset"/>, or 0 when it is past the end.</summary>
    public static int ReadUInt16(byte[] data, long offset)
    {
        if (offset + 2 > data.Length) return 0;
        var bytes = data.AsSpan((int)offset, 2);
        return IsLittleEndian(data) ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    /// <summary>Reads a 32-bit number at <paramref name="offset"/>, or 0 when it is past the end.</summary>
    public static long ReadUInt32(byte[] data, long offset)
    {
        if (offset + 4 > data.Length) return 0;
        var bytes = data.AsSpan((int)offset, 4);
        return IsLittleEndian(data) ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static bool IsLittleEndian(byte[] data) => data[0] == 'I';

    private static bool StartsDirectory(byte[] data, long offset) => offset >= HeaderLength && offset + 2 <= data.Length;

    /// <summary>The values of a BYTE, SHORT or LONG field (or their signed and undefined variants, read unsigned).</summary>
    private long[] ReadValues(Entry entry, int maxCount)
    {
        int size = entry.Type switch
        {
            1 or 2 or 6 or 7 => 1, // BYTE, ASCII, SBYTE, UNDEFINED
            3 or 8 => 2, // SHORT, SSHORT
            4 or 9 => 4, // LONG, SLONG
            _ => 0,
        };
        if (size == 0 || entry.Count <= 0 || entry.Count > maxCount) return Array.Empty<long>();
        // Values that fit in the entry's four value bytes are stored there; longer ones are stored at the offset it holds.
        long at = size * entry.Count <= 4 ? entry.ValueOffset : ReadUInt32(data, entry.ValueOffset);
        if (at < 0 || at + size * entry.Count > data.Length) return Array.Empty<long>();
        var values = new long[entry.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = size switch
            {
                1 => data[at + i],
                2 => ReadUInt16(data, at + i * 2L),
                _ => ReadUInt32(data, at + i * 4L),
            };
        }
        return values;
    }
}

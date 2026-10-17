namespace PdfPecker.Fonts;

/// <summary>
/// Big-endian reads from the bytes of a font. A read past either end throws <see cref="IndexOutOfRangeException"/>,
/// which the table parsers that tolerate damaged fonts catch, so a truncated table is as harmless as a bad offset.
/// </summary>
internal readonly struct TableReader(byte[] data)
{
    /// <summary>The bytes the offsets of this reader address.</summary>
    public byte[] Data => data;

    public int U16(int offset) => data[offset] << 8 | data[offset + 1];

    public short S16(int offset) => (short)(data[offset] << 8 | data[offset + 1]);

    public uint U32(int offset) => (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    public sbyte S8(int offset) => (sbyte)data[offset];
}

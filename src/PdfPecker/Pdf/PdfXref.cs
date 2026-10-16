namespace PdfPecker.Pdf;

/// <summary>
/// The objects of a newly written file, numbered from 1 with generation 0, and the cross-reference table and trailer
/// that close it. Every writer that makes a whole file records its objects here.
/// </summary>
internal sealed class PdfXref
{
    private readonly List<long> _offsets = new() { 0 }; // by object id; 0 = not written

    /// <summary>One more than the highest object id begun so far.</summary>
    public int Count => _offsets.Count;

    /// <summary>Starts object <paramref name="id"/> at the end of <paramref name="b"/>.</summary>
    public ByteBuffer Begin(ByteBuffer b, int id) => Begin(b, id, b.Length);

    /// <summary>Starts object <paramref name="id"/>, which begins at <paramref name="position"/> in the file.</summary>
    public ByteBuffer Begin(ByteBuffer b, int id, long position)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = position;
        return b.Int(id).Ascii(" 0 obj\n");
    }

    /// <summary>The first id from 1 to <paramref name="count"/> - 1 that was never begun, or -1 when every id was written.</summary>
    public int FirstMissing(int count)
    {
        for (int i = 1; i < count; i++)
            if (i >= _offsets.Count || _offsets[i] <= 0) return i;
        return -1;
    }

    public static void End(ByteBuffer b) => b.Ascii("\nendobj\n");

    /// <summary>
    /// Writes the cross-reference table for objects 0 to <paramref name="count"/> - 1. An id that was never written is
    /// marked free: no object can start at offset 0, where the file header is, so listing it as in use would point a reader
    /// at the header.
    /// </summary>
    public void WriteTable(ByteBuffer b, int count)
    {
        b.Ascii("xref\n0 ").Int(count).Ascii("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            long offset = i < _offsets.Count ? _offsets[i] : 0;
            if (offset > 0) Padded(b, offset, 10).Ascii(" 00000 n \n");
            else b.Ascii("0000000000 00000 f \n");
        }
    }

    /// <summary>
    /// Writes the trailer and the end of the file. <paramref name="infoId"/> and <paramref name="encryptId"/> are 0 when
    /// there is no such object, and the file identifier is left out when <paramref name="id0"/> is empty.
    /// </summary>
    public static void WriteTrailer(ByteBuffer b, int size, int rootId, int infoId, int encryptId, ReadOnlySpan<byte> id0, ReadOnlySpan<byte> id1,
        bool upperHexId, long startxref)
    {
        b.Ascii("trailer\n<< /Size ").Int(size).Ascii(" /Root ").Int(rootId).Ascii(" 0 R");
        if (infoId != 0) b.Ascii(" /Info ").Int(infoId).Ascii(" 0 R");
        if (encryptId != 0) b.Ascii(" /Encrypt ").Int(encryptId).Ascii(" 0 R");
        if (!id0.IsEmpty)
        {
            // The identifier is never encrypted.
            b.Ascii(" /ID [");
            PdfSyntax.Hex(b, id0, upperHexId);
            PdfSyntax.Hex(b, id1, upperHexId);
            b.Ascii("]");
        }
        b.Ascii(" >>\nstartxref\n").Int(startxref).Ascii("\n%%EOF\n");
    }

    /// <summary>Writes a number with leading zeros to at least <paramref name="width"/> digits, as <c>ToString("D10")</c> would.</summary>
    public static ByteBuffer Padded(ByteBuffer b, long value, int width)
    {
        if (value < 0) b.Byte((byte)'-');
        ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
        Span<byte> digits = stackalloc byte[20];
        int n = 0;
        do { digits[n++] = (byte)('0' + magnitude % 10); magnitude /= 10; } while (magnitude > 0);
        for (int i = n; i < width; i++) b.Byte((byte)'0');
        while (n > 0) b.Byte(digits[--n]);
        return b;
    }
}

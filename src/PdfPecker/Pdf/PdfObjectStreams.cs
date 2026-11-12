using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Objects collected for compressed object streams (PDF 1.5), and the cross-reference stream that a file with object
/// streams needs instead of a table. The compactor packs every object that is not a stream; the document writer packs
/// the structure tree of a tagged PDF, which is most of a long tagged file.
/// </summary>
internal sealed class PdfObjectStreams
{
    /// <summary>How many objects go into one object stream; more compress better, and every index still fits a byte.</summary>
    public const int ObjectsPerStream = 200;

    private readonly ByteBuffer _bodies = new();
    private readonly List<(int Id, int Start, int Length)> _objects = new();
    private int _current = -1, _start;

    /// <summary>The highest object number collected, or 0.</summary>
    public int MaxId { get; private set; }

    /// <summary>How many objects were collected.</summary>
    public int Count => _objects.Count;

    /// <summary>How many object streams <see cref="Write"/> writes.</summary>
    public int StreamCount => (_objects.Count + ObjectsPerStream - 1) / ObjectsPerStream;

    /// <summary>Starts object <paramref name="id"/>: its body (without "obj" and "endobj") goes into the buffer returned.</summary>
    public ByteBuffer Begin(int id)
    {
        _current = id;
        _start = _bodies.Length;
        return _bodies;
    }

    /// <summary>Ends the object begun last.</summary>
    public void End()
    {
        _objects.Add((_current, _start, _bodies.Length - _start));
        MaxId = Math.Max(MaxId, _current);
        _current = -1;
    }

    /// <summary>
    /// Writes the object streams, numbered from <paramref name="firstStreamId"/>, in the order the objects were begun.
    /// <paramref name="packed"/> learns the stream and index of each object, <paramref name="written"/> the offset of
    /// each stream.
    /// </summary>
    public void Write(ByteBuffer b, int firstStreamId, Func<byte[], byte[]> deflate, Action<int, int, int> packed, Action<int, long> written)
    {
        var body = new ByteBuffer();
        var header = new ByteBuffer();
        var all = _bodies.Span;
        for (int start = 0, streamId = firstStreamId; start < _objects.Count; start += ObjectsPerStream, streamId++)
        {
            PdfFile.CheckCancel();
            int n = Math.Min(ObjectsPerStream, _objects.Count - start);
            body.Clear();
            header.Clear();
            for (int i = 0; i < n; i++)
            {
                var (id, from, length) = _objects[start + i];
                packed(id, streamId, i);
                if (i > 0) header.Sp();
                header.Int(id).Sp().Int(body.Length);
                if (i > 0) body.Nl();
                body.Bytes(all.Slice(from, length));
            }
            header.Nl();
            var content = new byte[header.Length + body.Length];
            header.Span.CopyTo(content);
            body.Span.CopyTo(content.AsSpan(header.Length));
            var data = deflate(content);
            written(streamId, b.Length);
            b.Int(streamId).Ascii(" 0 obj\n<</Type/ObjStm/N ").Int(n).Ascii("/First ").Int(header.Length)
                .Ascii("/Filter/FlateDecode/Length ").Int(data.Length).Ascii(">>stream\n").Bytes(data).Ascii("\nendstream");
            PdfXref.End(b);
        }
    }

    /// <summary>
    /// Writes the cross-reference stream (object <paramref name="xrefId"/>, the last of the file) and the end of the
    /// file. <paramref name="entry"/> gives each other object's row: in use at an offset (type 1), in an object stream at
    /// an index (type 2), or free (type 0). Object 0 is always the head of the free list.
    /// </summary>
    public static void WriteXrefStream(ByteBuffer b, int xrefId, Func<int, (int Type, long Field, int Index)> entry, int rootId, int infoId,
        int encryptId, ReadOnlySpan<byte> id0, ReadOnlySpan<byte> id1, bool upperHexId, Func<byte[], byte[]> deflate)
    {
        int size = xrefId + 1;
        long xrefOffset = b.Length;
        int offsetBytes = BytesFor(Math.Max(xrefOffset, size));
        const int FieldBytes = 2;
        int rowLength = 1 + offsetBytes + FieldBytes;
        var rows = new byte[size * rowLength];
        for (int id = 0; id < size; id++)
        {
            var row = rows.AsSpan(id * rowLength, rowLength);
            var (type, field, index) = id == 0 ? (0, 0L, 0xFFFF) : id == xrefId ? (1, xrefOffset, 0) : entry(id);
            row[0] = (byte)type;
            Put(row.Slice(1, offsetBytes), field);
            Put(row.Slice(1 + offsetBytes), index);
        }
        var xrefData = deflate(PngUp(rows, rowLength));
        b.Int(xrefId).Ascii(" 0 obj\n<</Type/XRef/Size ").Int(size).Ascii("/W[1 ").Int(offsetBytes).Ascii(" ").Int(FieldBytes)
            .Ascii("]/Root ").Int(rootId).Ascii(" 0 R");
        if (infoId != 0) b.Ascii("/Info ").Int(infoId).Ascii(" 0 R");
        if (encryptId != 0) b.Ascii("/Encrypt ").Int(encryptId).Ascii(" 0 R");
        if (!id0.IsEmpty)
        {
            b.Ascii("/ID[");
            PdfSyntax.Hex(b, id0, upperHexId);
            PdfSyntax.Hex(b, id1, upperHexId);
            b.Ascii("]");
        }
        b.Ascii("/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns ").Int(rowLength).Ascii(">>/Length ").Int(xrefData.Length)
            .Ascii(">>stream\n").Bytes(xrefData).Ascii("\nendstream");
        PdfXref.End(b);
        b.Ascii("startxref\n").Int(xrefOffset).Ascii("\n%%EOF\n");
    }

    /// <summary>The bytes a big-endian field needs to hold <paramref name="value"/>.</summary>
    private static int BytesFor(long value)
    {
        int n = 1;
        while (n < 8 && value >> (8 * n) != 0) n++;
        return n;
    }

    /// <summary>Writes <paramref name="value"/> big-endian across <paramref name="field"/>.</summary>
    private static void Put(Span<byte> field, long value)
    {
        for (int i = field.Length - 1; i >= 0; i--, value >>= 8) field[i] = (byte)value;
    }

    /// <summary>The rows with the PNG Up predictor: a filter byte, then each byte minus the one above it.</summary>
    private static byte[] PngUp(byte[] rows, int rowLength)
    {
        int count = rows.Length / rowLength;
        var result = new byte[count * (rowLength + 1)];
        for (int r = 0; r < count; r++)
        {
            int to = r * (rowLength + 1), from = r * rowLength;
            result[to] = 2;
            for (int i = 0; i < rowLength; i++) result[to + 1 + i] = (byte)(rows[from + i] - (r > 0 ? rows[from - rowLength + i] : 0));
        }
        return result;
    }
}

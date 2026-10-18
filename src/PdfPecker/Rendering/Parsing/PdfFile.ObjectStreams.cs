namespace PdfPecker.Rendering.Parsing;

/// <summary>Object streams: compressed objects stored one after another behind a table of "number offset" pairs.</summary>
internal sealed partial class PdfFile
{
    /// <summary>The most objects one object stream is read to hold (/N), against a bogus count allocating its tables.</summary>
    private const int MaxObjectsPerStream = 1_000_000;

    /// <summary>The decoded data of an object stream and the table that says where each object starts in it.</summary>
    private sealed class ObjectStream
    {
        private readonly int[] numbers;
        private readonly int[] offsets;
        private readonly int first;

        public ObjectStream(int[] numbers, int[] offsets, byte[] data, int first)
        {
            this.numbers = numbers;
            this.offsets = offsets;
            Data = data;
            this.first = first;
        }

        public byte[] Data { get; }

        /// <summary>The object numbers in the stream's own order.</summary>
        public ReadOnlySpan<int> Numbers => numbers;

        /// <summary>
        /// Finds where object <paramref name="num"/> starts in <see cref="Data"/>. The cross-reference entry says at which
        /// index it should be; when that index names another object, the table is searched instead.
        /// </summary>
        public bool TryFindObject(int num, int expectedIndex, out int start)
        {
            int index = expectedIndex;
            if (index < 0 || index >= numbers.Length || numbers[index] != num)
            {
                index = Array.IndexOf(numbers, num);
                if (index < 0)
                {
                    start = 0;
                    return false;
                }
            }
            start = first + offsets[index];
            return start >= 0 && start < Data.Length;
        }
    }

    private ObjectStream GetObjectStream(int streamNum)
    {
        if (_objStreams.TryGetValue(streamNum, out var cached)) return cached;
        if (GetObject(streamNum) is not PdfStream stream) throw new PdfFormatException("Object stream " + streamNum + " is missing.");
        int count = Math.Clamp(GetInt(stream, "N", 0), 0, MaxObjectsPerStream);
        int first = GetInt(stream, "First", 0);
        var data = DecodeStream(stream);
        var objectStream = new ObjectStream(ReadObjectTable(data, first, count, out var offsets), offsets, data, first);
        _objStreams[streamNum] = objectStream;
        return objectStream;
    }

    /// <summary>Reads up to <paramref name="count"/> "number offset" pairs from the header before <paramref name="first"/>; a damaged pair ends the table.</summary>
    private static int[] ReadObjectTable(byte[] data, int first, int count, out int[] offsets)
    {
        var lexer = new PdfLexer(data, 0, Math.Clamp(first, 0, data.Length));
        var numbers = new int[count];
        offsets = new int[count];
        int read = 0;
        for (; read < count; read++)
        {
            if (lexer.Next() != TokenKind.Integer) break;
            numbers[read] = (int)lexer.Integer;
            if (lexer.Next() != TokenKind.Integer) break;
            offsets[read] = (int)lexer.Integer;
        }
        if (read < count)
        {
            Array.Resize(ref numbers, read);
            Array.Resize(ref offsets, read);
        }
        return numbers;
    }
}

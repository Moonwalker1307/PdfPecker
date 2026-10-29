namespace PdfPecker.Rendering.Parsing;

/// <summary>Object streams: compressed objects stored one after another behind a table of "number offset" pairs.</summary>
internal sealed partial class PdfFile
{
    /// <summary>The most objects one object stream is read to hold (/N), against a bogus count allocating its tables.</summary>
    private const int MaxObjectsPerStream = 1_000_000;

    /// <summary>
    /// The most bytes the object streams of one file may decode to in all, whatever its size. Decoded object streams stay
    /// cached for the life of the file, and each one may decode to a quarter of a gigabyte, so without a total a few
    /// megabytes of compressed streams would hold gigabytes of memory.
    /// </summary>
    private const long MinObjectStreamBudget = 256L * 1024 * 1024;

    /// <summary>
    /// How much a file's object streams may decode to per byte of the file, for files so large that the fixed budget is
    /// too small. Object streams of real files are text that compresses by well under this factor.
    /// </summary>
    private const int ObjectStreamBudgetPerFileByte = 16;

    private long _objectStreamBytes;

    /// <summary>The bytes the object streams read so far decoded to, counted once per decode.</summary>
    internal long ObjectStreamBytes => _objectStreamBytes;

    private long ObjectStreamBudget => Math.Max(MinObjectStreamBudget, (long)Data.Length * ObjectStreamBudgetPerFileByte);

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
        // The total only grows: a stream decoded again after the cache is cleared counts again, which bounds the work too.
        if (_objectStreamBytes >= ObjectStreamBudget) throw ObjectStreamBudgetExceeded();
        var data = DecodeStream(stream);
        _objectStreamBytes += data.Length;
        if (_objectStreamBytes > ObjectStreamBudget) throw ObjectStreamBudgetExceeded();
        var objectStream = new ObjectStream(ReadObjectTable(data, first, count, out var offsets), offsets, data, first);
        _objStreams[streamNum] = objectStream;
        return objectStream;
    }

    private static PdfFormatException ObjectStreamBudgetExceeded() => new("The object streams decode to more data than allowed.");

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

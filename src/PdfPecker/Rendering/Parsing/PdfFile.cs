namespace PdfPecker.Rendering.Parsing;

/// <summary>
/// Random access to the objects of a PDF file: cross-reference tables and streams (including incremental updates and
/// hybrid files), object streams, decryption, and reconstruction of a damaged cross-reference table.
/// </summary>
/// <remarks>
/// Thread-safe: object loading is serialized, decoded objects are immutable once returned. The class is split by
/// concern: this file holds the object table and object loading, <c>PdfFile.Xref.cs</c> reads cross-reference tables
/// and streams, <c>PdfFile.ObjectStreams.cs</c> object streams, <c>PdfFile.Recovery.cs</c> rebuilds the table of a
/// damaged file, <c>PdfFile.Streams.cs</c> parses and decodes streams and <c>PdfFile.Accessors.cs</c> has the typed
/// dictionary accessors.
/// </remarks>
internal sealed partial class PdfFile
{
    /// <summary>How many references may point at references before <see cref="Resolve"/> gives up on a cycle.</summary>
    private const int MaxReferenceChain = 32;

    private enum XrefEntryKind : byte
    {
        Free,
        InFile,
        InObjectStream,
    }

    /// <summary>Where an object is: at a byte offset of the file, or at an index of an object stream.</summary>
    private readonly struct XrefEntry
    {
        // Entries are created through the factories, so that the two meanings of each field are not mixed up.
        private readonly long location;
        private readonly int ordinal;

        private XrefEntry(XrefEntryKind kind, long location, int ordinal)
        {
            Kind = kind;
            this.location = location;
            this.ordinal = ordinal;
        }

        public XrefEntryKind Kind { get; }

        /// <summary>The byte offset of an object in the file.</summary>
        public long Offset => location;

        public int Generation => ordinal;

        /// <summary>The number of the object stream that holds the object.</summary>
        public int StreamNumber => (int)location;

        public int IndexInStream => ordinal;

        public static XrefEntry Free => default;

        public static XrefEntry AtOffset(long offset, int generation) => new(XrefEntryKind.InFile, offset, generation);

        public static XrefEntry InStream(long streamNumber, int index) => new(XrefEntryKind.InObjectStream, streamNumber, index);
    }

    public readonly byte[] Data;
    private readonly int _headerOffset;
    private Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<int, ObjectStream> _objStreams = new();
    private readonly HashSet<int> _loading = new();
    private readonly object _lock = new();
    private Decryptor? _decryptor;
    private int _encryptObjNum = -1;
    private bool _reconstructed;

    public PdfDict Trailer { get; private set; } = new();

    /// <summary>The security handler of an encrypted file, or null.</summary>
    public Decryptor? Decryptor => _decryptor;

    /// <summary>The offset of the newest cross-reference section, or -1.</summary>
    public long LastXrefOffset => FindStartXref();

    /// <summary>The highest object number in the cross-reference table.</summary>
    public int MaxObjectNumber => _xref.Count == 0 ? 0 : _xref.Keys.Max();

    /// <summary>
    /// The numbers of the objects that the incremental updates after byte <paramref name="revisionEnd"/> wrote or freed:
    /// those stored at or after it, in an object stream stored at or after it, or free. An earlier revision is a prefix of
    /// the file, so every other object reads as it did in that revision. Objects free in both are included; they compare
    /// equal.
    /// </summary>
    public List<int> ObjectsChangedAfter(long revisionEnd)
    {
        var changed = new List<int>();
        lock (_lock)
        {
            foreach (var (num, entry) in _xref)
            {
                bool after = entry.Kind switch
                {
                    // Offsets may be relative to a header after junk, so the comparison errs towards "changed".
                    XrefEntryKind.InFile => entry.Offset + _headerOffset >= revisionEnd,
                    XrefEntryKind.InObjectStream => !_xref.TryGetValue(entry.StreamNumber, out var stream)
                        || stream.Kind != XrefEntryKind.InFile || stream.Offset + _headerOffset >= revisionEnd,
                    _ => true,
                };
                if (after) changed.Add(num);
            }
        }
        return changed;
    }

    private PdfFile(byte[] data)
    {
        Data = data;
        _headerOffset = FindHeader(data);
    }

    public static PdfFile Open(byte[] data, string? password)
    {
        var file = new PdfFile(data);
        file.Load(password);
        return file;
    }

    private void Load(string? password)
    {
        bool ok = false;
        try
        {
            long startxref = FindStartXref();
            if (startxref >= 0) ok = ReadXrefChain(startxref);
        }
        catch (Exception ex) when (IsDamagedObjectError(ex))
        {
            ok = false;
        }
        if (!ok || Trailer["Root"] == null || Resolve(Trailer["Root"]) is not PdfDict) Reconstruct();

        var encryptRef = Trailer["Encrypt"];
        if (encryptRef == null || encryptRef.IsNull) return;
        if (encryptRef is PdfRef reference) _encryptObjNum = reference.Num;
        if (Resolve(encryptRef) is not PdfDict encrypt) return;

        byte[] firstId = Array.Empty<byte>();
        if (Resolve(Trailer["ID"]) is PdfArray ids && ids.Count > 0 && Resolve(ids[0]) is PdfString id) firstId = id.Bytes;
        var decryptor = Decryptor.Create(this, encrypt, firstId, password);
        lock (_lock)
        {
            // Objects parsed so far (the trailer chain, the encryption dictionary) stay as read; drop the rest.
            var keep = _encryptObjNum >= 0 && _cache.TryGetValue(_encryptObjNum, out var encryptObject) ? encryptObject : null;
            _cache.Clear();
            _objStreams.Clear();
            if (keep != null) _cache[_encryptObjNum] = keep;
            _decryptor = decryptor;
        }
    }

    /// <summary>
    /// The exceptions that parsing a damaged object raises, which make the object unreadable rather than the file: the
    /// parser's own, the index and argument errors of reading past damaged offsets and lengths, and the limits of the
    /// stream decoders.
    /// </summary>
    private static bool IsDamagedObjectError(Exception ex)
        => ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or InvalidDataException;

    // ------------------------------------------------------------------ objects

    public PdfObject? Resolve(PdfObject? o)
    {
        int guard = 0;
        while (o is PdfRef r && guard++ < MaxReferenceChain) o = GetObject(r.Num);
        return o is PdfRef ? null : o;
    }

    public PdfObject? GetObject(int num)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(num, out var cached)) return cached;
            if (!_loading.Add(num)) return null; // reference cycle (e.g. a /Length pointing at itself)
            try
            {
                var o = TryLoadObject(num);
                if (o == null && !_reconstructed && _xref.ContainsKey(num))
                {
                    // The table points somewhere wrong: rebuild it once and retry.
                    _loading.Remove(num);
                    var trailer = Trailer;
                    Reconstruct(opened: true);
                    if (trailer["Root"] != null && Trailer != trailer && Resolve(trailer["Root"]) is PdfDict) Trailer = Merge(Trailer, trailer);
                    if (_cache.TryGetValue(num, out cached)) return cached;
                    _loading.Add(num);
                    o = TryLoadObject(num);
                }
                o ??= PdfNull.Instance;
                _cache[num] = o;
                return o;
            }
            finally
            {
                _loading.Remove(num);
            }
        }
    }

    /// <summary>Loads an object, or returns null when it is missing or too damaged to parse.</summary>
    private PdfObject? TryLoadObject(int num)
    {
        try
        {
            return LoadObject(num);
        }
        catch (Exception ex) when (IsDamagedObjectError(ex))
        {
            return null;
        }
    }

    private PdfObject? LoadObject(int num)
    {
        if (!_xref.TryGetValue(num, out var entry)) return null;
        switch (entry.Kind)
        {
            case XrefEntryKind.InFile:
                // Offsets of a file with junk before the header may be relative to the header.
                return TryParseAt(entry.Offset, num) ?? (_headerOffset > 0 ? TryParseAt(entry.Offset + _headerOffset, num) : null);
            case XrefEntryKind.InObjectStream:
                var stream = GetObjectStream(entry.StreamNumber);
                if (!stream.TryFindObject(num, entry.IndexInStream, out int start)) return null;
                return new PdfLexer(stream.Data, start).ReadObject() ?? PdfNull.Instance;
            default:
                return null;
        }
    }

    /// <summary>Parses the indirect object at <paramref name="offset"/>, or returns null when another object is there.</summary>
    private PdfObject? TryParseAt(long offset, int expectedNum)
    {
        if (offset <= 0 && expectedNum != 0 || offset >= Data.Length) return null;
        var lexer = new PdfLexer(Data, (int)offset);
        var o = ParseIndirectAt(lexer, out int num, out _, decrypt: true);
        return num == expectedNum ? o : null;
    }

    /// <summary>Parses "num gen obj value [stream ... endstream]" at the lexer position.</summary>
    private PdfObject? ParseIndirectAt(PdfLexer lexer, out int num, out int gen, bool decrypt)
    {
        num = -1;
        gen = 0;
        if (lexer.Next() != TokenKind.Integer) return null;
        int number = (int)lexer.Integer;
        if (lexer.Next() != TokenKind.Integer) return null;
        gen = (int)lexer.Integer;
        if (lexer.Next() != TokenKind.Keyword || !lexer.KeywordIs("obj")) return null;
        num = number;

        var obj = lexer.ReadObject() ?? PdfNull.Instance;
        if (obj is PdfDict dict)
        {
            int afterDict = lexer.Pos;
            if (lexer.Next() == TokenKind.Keyword && lexer.KeywordIs("stream")) return ReadStreamBody(lexer.Pos, dict, num, gen, decrypt);
            lexer.Pos = afterDict;
        }
        if (decrypt && _decryptor != null && num != _encryptObjNum) obj = DecryptStrings(obj, num, gen);
        return obj;
    }
}

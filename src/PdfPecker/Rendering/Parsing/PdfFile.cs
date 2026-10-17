namespace PdfPecker.Rendering.Parsing;

/// <summary>
/// Random access to the objects of a PDF file: cross-reference tables and streams (including incremental updates and
/// hybrid files), object streams, decryption, and reconstruction of a damaged cross-reference table.
/// </summary>
/// <remarks>Thread-safe: object loading is serialized, decoded objects are immutable once returned.</remarks>
internal sealed class PdfFile
{
    private struct XrefEntry
    {
        public byte Type; // 1 = at offset, 2 = in object stream
        public long Offset; // type 1: byte offset; type 2: object stream number
        public int Index; // type 2: index within the object stream; type 1: generation
    }

    public readonly byte[] Data;
    private readonly int _headerOffset;
    private Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject> _cache = new();
    private readonly Dictionary<int, (int[] Nums, int[] Offsets, byte[] Data, int First)> _objStreams = new();
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

    private PdfFile(byte[] data)
    {
        Data = data;
        _headerOffset = FindHeader(data);
    }

    public static PdfFile Open(byte[] data, string? password)
    {
        var f = new PdfFile(data);
        f.Load(password);
        return f;
    }

    private static int FindHeader(byte[] d)
    {
        int lim = Math.Min(d.Length - 5, 1024);
        for (int i = 0; i < lim; i++)
            if (d[i] == '%' && d[i + 1] == 'P' && d[i + 2] == 'D' && d[i + 3] == 'F' && d[i + 4] == '-') return i;
        return 0;
    }

    private void Load(string? password)
    {
        bool ok = false;
        try
        {
            long startxref = FindStartXref();
            if (startxref >= 0) ok = ReadXrefChain(startxref);
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or InvalidDataException)
        {
            ok = false;
        }
        if (!ok || Trailer["Root"] == null || !(Resolve(Trailer["Root"]) is PdfDict)) Reconstruct();

        var encRef = Trailer["Encrypt"];
        if (encRef != null && !encRef.IsNull)
        {
            if (encRef is PdfRef er) _encryptObjNum = er.Num;
            if (Resolve(encRef) is PdfDict enc)
            {
                byte[] id0 = Array.Empty<byte>();
                if (Resolve(Trailer["ID"]) is PdfArray ids && ids.Count > 0 && Resolve(ids[0]) is PdfString s) id0 = s.Bytes;
                var dec = Decryptor.Create(this, enc, id0, password);
                lock (_lock)
                {
                    // Objects parsed so far (the trailer chain, the encryption dictionary) stay as read; drop the rest.
                    var keep = _encryptObjNum >= 0 && _cache.TryGetValue(_encryptObjNum, out var e) ? e : null;
                    _cache.Clear();
                    _objStreams.Clear();
                    if (keep != null) _cache[_encryptObjNum] = keep;
                    _decryptor = dec;
                }
            }
        }
    }

    private long FindStartXref()
    {
        var d = Data;
        int from = Math.Max(0, d.Length - 2048);
        for (int i = d.Length - 9; i >= from; i--)
        {
            if (d[i] == 's' && d[i + 1] == 't' && d[i + 2] == 'a' && d[i + 3] == 'r' && d[i + 4] == 't' && d[i + 5] == 'x' && d[i + 6] == 'r' && d[i + 7] == 'e' && d[i + 8] == 'f')
            {
                var lx = new PdfLexer(d, i + 9);
                if (lx.Next() == TokenKind.Integer) return lx.Integer;
                return -1;
            }
        }
        return -1;
    }

    private bool ReadXrefChain(long offset)
    {
        var visited = new HashSet<long>();
        PdfDict? first = null;
        while (offset >= 0 && visited.Add(offset))
        {
            var trailer = ReadXrefSection(offset) ?? ReadXrefSection(offset + _headerOffset);
            if (trailer == null) return false;
            first ??= trailer;
            // Hybrid file: the classic table's trailer points at an xref stream with the compressed objects.
            if (trailer["XRefStm"] is PdfNumber xs && visited.Add((long)xs.Value)) ReadXrefSection((long)xs.Value);
            if (trailer["Prev"] is PdfNumber prev) offset = (long)prev.Value;
            else break;
        }
        if (first == null) return false;
        Trailer = first;
        return true;
    }

    /// <summary>Reads one xref table or stream at <paramref name="offset"/>, adding entries not already known. Returns its trailer.</summary>
    private PdfDict? ReadXrefSection(long offset)
    {
        if (offset < 0 || offset >= Data.Length) return null;
        var lx = new PdfLexer(Data, (int)offset);
        var k = lx.Next();
        if (k == TokenKind.Keyword && lx.KeywordIs("xref"))
        {
            while (true)
            {
                int save = lx.Pos;
                k = lx.Next();
                if (k == TokenKind.Keyword && lx.KeywordIs("trailer")) break;
                if (k != TokenKind.Integer) { lx.Pos = save; break; }
                long start = lx.Integer;
                if (lx.Next() != TokenKind.Integer) return null;
                long count = lx.Integer;
                for (long i = 0; i < count; i++)
                {
                    if (lx.Next() != TokenKind.Integer) return null;
                    long off = lx.Integer;
                    if (lx.Next() != TokenKind.Integer) return null;
                    long gen = lx.Integer;
                    if (lx.Next() != TokenKind.Keyword) return null;
                    bool inUse = lx.TokLen == 1 && lx.Data[lx.TokStart] == 'n';
                    int num = (int)(start + i);
                    if (num == 0 && start == 1 && i == 0 && off == 0 && !inUse) { } // some writers start at 1 with the free entry
                    if (!_xref.ContainsKey(num))
                        _xref[num] = inUse && off > 0 ? new XrefEntry { Type = 1, Offset = off, Index = (int)gen } : new XrefEntry { Type = 0 };
                }
            }
            var trailer = lx.ReadObject() as PdfDict;
            return trailer;
        }
        if (k == TokenKind.Integer)
        {
            // Cross-reference stream: "n g obj << ... >> stream".
            lx.Pos = (int)offset;
            var obj = ParseIndirectAt(lx, out int num, out _, decrypt: false);
            if (obj is not PdfStream xs || GetName(xs, "Type") != "XRef") return null;
            if (!_cache.ContainsKey(num)) _cache[num] = xs;
            ReadXrefStream(xs);
            return xs;
        }
        return null;
    }

    private void ReadXrefStream(PdfStream xs)
    {
        if (Resolve(xs["W"]) is not PdfArray wArr || wArr.Count < 3) return;
        int w0 = GetIntValue(wArr[0]), w1 = GetIntValue(wArr[1]), w2 = GetIntValue(wArr[2]);
        if (w0 < 0 || w1 < 0 || w2 < 0 || w0 > 8 || w1 > 8 || w2 > 8) return;
        int size = GetInt(xs, "Size", 0);
        var index = new List<(int Start, int Count)>();
        if (Resolve(xs["Index"]) is PdfArray ia)
        {
            for (int i = 0; i + 1 < ia.Count; i += 2) index.Add((GetIntValue(ia[i]), GetIntValue(ia[i + 1])));
        }
        else index.Add((0, size));
        var data = DecodeStream(xs);
        int rowLen = w0 + w1 + w2;
        if (rowLen == 0) return;
        int pos = 0;
        foreach (var (start, count) in index)
        {
            for (int i = 0; i < count && pos + rowLen <= data.Length; i++, pos += rowLen)
            {
                long type = w0 == 0 ? 1 : ReadBE(data, pos, w0);
                long f2 = ReadBE(data, pos + w0, w1);
                long f3 = ReadBE(data, pos + w0 + w1, w2);
                int num = start + i;
                if (_xref.ContainsKey(num)) continue;
                _xref[num] = type switch
                {
                    1 => new XrefEntry { Type = 1, Offset = f2, Index = (int)f3 },
                    2 => new XrefEntry { Type = 2, Offset = f2, Index = (int)f3 },
                    _ => new XrefEntry { Type = 0 },
                };
            }
        }
    }

    private static long ReadBE(byte[] d, int pos, int len)
    {
        long v = 0;
        for (int i = 0; i < len; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    // ------------------------------------------------------------------ reconstruction

    /// <summary>Rebuilds the object table by scanning the file for "n g obj" headers.</summary>
    /// <param name="opened">
    /// True when the file already opened through its own cross-reference table and only one entry was found wrong
    /// later. A scan that then finds no catalog keeps that table and trailer instead of failing: the file was readable
    /// a moment ago, and this runs deep inside a caller that does not expect the reader's exception.
    /// </param>
    private void Reconstruct(bool opened = false)
    {
        lock (_lock)
        {
            if (_reconstructed) return;
            _reconstructed = true;
            var previous = _xref;
            var table = new Dictionary<int, XrefEntry>();
            var d = Data;
            PdfDict? trailer = null;
            for (int i = 0; i + 3 < d.Length; i++)
            {
                byte b = d[i];
                if (b == 'o' && d[i + 1] == 'b' && d[i + 2] == 'j' && (i + 3 >= d.Length || !PdfLexer.IsRegular(d[i + 3])))
                {
                    // Walk back over "num gen ".
                    int p = i - 1;
                    while (p >= 0 && PdfLexer.IsWhite(d[p])) p--;
                    int genEnd = p;
                    while (p >= 0 && d[p] >= '0' && d[p] <= '9') p--;
                    if (p == genEnd) continue;
                    int genStart = p + 1;
                    while (p >= 0 && PdfLexer.IsWhite(d[p])) p--;
                    int numEnd = p;
                    while (p >= 0 && d[p] >= '0' && d[p] <= '9') p--;
                    if (p == numEnd) continue;
                    int numStart = p + 1;
                    if (p >= 0 && PdfLexer.IsRegular(d[p]) && d[p] != '%') continue;
                    if (numEnd - numStart > 9 || genEnd - genStart > 5) continue;
                    // Ten digits can still be more than an int holds; such a header is damage, not an object.
                    if (!int.TryParse(d.AsSpan(numStart, numEnd - numStart + 1), System.Globalization.NumberStyles.None, null, out int num)) continue;
                    int gen = int.Parse(System.Text.Encoding.ASCII.GetString(d, genStart, genEnd - genStart + 1));
                    table[num] = new XrefEntry { Type = 1, Offset = numStart, Index = gen };
                }
                else if (b == 't' && i + 7 < d.Length && d[i + 1] == 'r' && d[i + 2] == 'a' && d[i + 3] == 'i' && d[i + 4] == 'l' && d[i + 5] == 'e' && d[i + 6] == 'r')
                {
                    try
                    {
                        var lx = new PdfLexer(d, i + 7);
                        if (lx.ReadObject() is PdfDict t && t["Root"] != null) trailer = Merge(trailer, t);
                    }
                    catch (PdfFormatException) { }
                }
            }
            _xref = table;
            _cache.Clear();
            _objStreams.Clear();

            // Objects inside object streams, and trailer information from xref streams.
            foreach (var (num, _) in table.ToArray())
            {
                PdfObject? o;
                try { o = LoadObject(num); }
                catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException) { continue; }
                if (o is PdfStream s)
                {
                    var type = GetName(s, "Type");
                    if (type == "XRef" && s["Root"] != null) trailer ??= s;
                    else if (type == "ObjStm")
                    {
                        try
                        {
                            var os = GetObjStream(num);
                            for (int k = 0; k < os.Nums.Length; k++)
                                if (!_xref.ContainsKey(os.Nums[k])) _xref[os.Nums[k]] = new XrefEntry { Type = 2, Offset = num, Index = k };
                        }
                        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or NotSupportedException or InvalidDataException) { }
                    }
                }
            }
            if (trailer == null || !(Resolve(trailer["Root"]) is PdfDict))
            {
                // Find the catalog by type.
                foreach (var num in _xref.Keys.ToArray())
                {
                    if (GetObject(num) is PdfDict dd && !(dd is PdfStream) && GetName(dd, "Type") == "Catalog")
                    {
                        var t = new PdfDict();
                        if (trailer != null) foreach (var kv in trailer.Entries) t.Entries[kv.Key] = kv.Value;
                        t.Entries["Root"] = new PdfRef(num, 0);
                        trailer = t;
                        break;
                    }
                }
            }
            if (trailer == null && opened)
            {
                _xref = previous;
                _cache.Clear();
                _objStreams.Clear();
                return;
            }
            if (trailer == null) throw new PdfFormatException("No document catalog found; the file is not a readable PDF.");
            Trailer = trailer;
        }
    }

    private static PdfDict Merge(PdfDict? older, PdfDict newer)
    {
        if (older == null) return newer;
        var d = new PdfDict();
        foreach (var kv in older.Entries) d.Entries[kv.Key] = kv.Value;
        foreach (var kv in newer.Entries) d.Entries[kv.Key] = kv.Value;
        return d;
    }

    // ------------------------------------------------------------------ objects

    public PdfObject? Resolve(PdfObject? o)
    {
        int guard = 0;
        while (o is PdfRef r && guard++ < 32) o = GetObject(r.Num);
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
                PdfObject? o;
                try { o = LoadObject(num); }
                catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or InvalidDataException)
                {
                    o = null;
                }
                if (o == null && !_reconstructed && _xref.ContainsKey(num))
                {
                    // The table points somewhere wrong: rebuild it once and retry.
                    _loading.Remove(num);
                    var trailer = Trailer;
                    Reconstruct(opened: true);
                    if (trailer["Root"] != null && Trailer != trailer && Resolve(trailer["Root"]) is PdfDict) Trailer = Merge(Trailer, trailer);
                    if (_cache.TryGetValue(num, out cached)) return cached;
                    _loading.Add(num);
                    try { o = LoadObject(num); }
                    catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException or InvalidDataException) { o = null; }
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

    private PdfObject? LoadObject(int num)
    {
        if (!_xref.TryGetValue(num, out var e)) return null;
        if (e.Type == 1)
        {
            long off = e.Offset;
            var o = TryParseAt(off, num) ?? (_headerOffset > 0 ? TryParseAt(off + _headerOffset, num) : null);
            return o;
        }
        if (e.Type == 2)
        {
            var os = GetObjStream((int)e.Offset);
            int idx = e.Index;
            if (idx < 0 || idx >= os.Nums.Length || os.Nums[idx] != num)
            {
                idx = Array.IndexOf(os.Nums, num);
                if (idx < 0) return null;
            }
            int start = os.First + os.Offsets[idx];
            if (start < 0 || start >= os.Data.Length) return null;
            var lx = new PdfLexer(os.Data, start);
            return lx.ReadObject() ?? PdfNull.Instance;
        }
        return null;
    }

    private PdfObject? TryParseAt(long offset, int expectedNum)
    {
        if (offset <= 0 && expectedNum != 0 || offset >= Data.Length) return null;
        var lx = new PdfLexer(Data, (int)offset);
        var o = ParseIndirectAt(lx, out int num, out _, decrypt: true);
        if (num != expectedNum) return null;
        return o;
    }

    private (int[] Nums, int[] Offsets, byte[] Data, int First) GetObjStream(int streamNum)
    {
        if (_objStreams.TryGetValue(streamNum, out var os)) return os;
        if (GetObject(streamNum) is not PdfStream s) throw new PdfFormatException("Object stream " + streamNum + " is missing.");
        int n = Math.Clamp(GetInt(s, "N", 0), 0, 1_000_000);
        int first = GetInt(s, "First", 0);
        var data = DecodeStream(s);
        var lx = new PdfLexer(data, 0, Math.Clamp(first, 0, data.Length));
        var nums = new int[n];
        var offs = new int[n];
        int count = 0;
        for (; count < n; count++)
        {
            if (lx.Next() != TokenKind.Integer) break;
            nums[count] = (int)lx.Integer;
            if (lx.Next() != TokenKind.Integer) break;
            offs[count] = (int)lx.Integer;
        }
        if (count < n) { Array.Resize(ref nums, count); Array.Resize(ref offs, count); }
        os = (nums, offs, data, first);
        _objStreams[streamNum] = os;
        return os;
    }

    /// <summary>Parses "num gen obj value [stream ... endstream]" at the lexer position.</summary>
    private PdfObject? ParseIndirectAt(PdfLexer lx, out int num, out int gen, bool decrypt)
    {
        num = -1;
        gen = 0;
        if (lx.Next() != TokenKind.Integer) return null;
        num = (int)lx.Integer;
        if (lx.Next() != TokenKind.Integer) { num = -1; return null; }
        gen = (int)lx.Integer;
        if (lx.Next() != TokenKind.Keyword || !lx.KeywordIs("obj")) { num = -1; return null; }
        var obj = lx.ReadObject() ?? PdfNull.Instance;
        if (obj is PdfDict dict)
        {
            int save = lx.Pos;
            if (lx.Next() == TokenKind.Keyword && lx.KeywordIs("stream"))
            {
                int p = lx.Pos;
                if (p < Data.Length && Data[p] == '\r') p++;
                if (p < Data.Length && Data[p] == '\n') p++;
                int length = GetIntValue(dict["Length"] is PdfRef lr ? (lr.Num == num ? null : GetObject(lr.Num)) : dict["Length"]);
                int end = p + length;
                bool valid = length >= 0 && end <= Data.Length && EndstreamFollows(end);
                if (!valid)
                {
                    end = FindEndstream(p);
                    // Trim the end-of-line marker before "endstream".
                    if (end > p && Data[end - 1] == '\n') end--;
                    if (end > p && Data[end - 1] == '\r') end--;
                }
                var raw = Data.AsSpan(p, Math.Max(0, end - p)).ToArray();
                if (decrypt && _decryptor != null && num != _encryptObjNum && StreamIsEncrypted(dict))
                    raw = _decryptor.DecryptStream(raw, num, gen);
                var s = new PdfStream(dict.Entries, raw) { ObjectNumber = num };
                if (decrypt && _decryptor != null && num != _encryptObjNum) DecryptStrings(s, num, gen);
                return s;
            }
            lx.Pos = save;
        }
        if (decrypt && _decryptor != null && num != _encryptObjNum) obj = DecryptStrings(obj, num, gen);
        return obj;
    }

    /// <summary>
    /// Whether a stream's data is encrypted: not a cross-reference stream, not the XMP metadata of a file encrypted with
    /// /EncryptMetadata false, and not a stream whose own /Crypt filter is the Identity filter (the default when the
    /// filter names none).
    /// </summary>
    private bool StreamIsEncrypted(PdfDict dict)
    {
        var type = GetName(dict, "Type");
        if (type == "XRef" || type == "Metadata" && !_decryptor!.EncryptMetadata) return false;
        var filter = dict["Filter"];
        PdfObject? parms = dict["DecodeParms"];
        if (filter is PdfArray fa && fa.Count > 0)
        {
            filter = fa[0];
            parms = parms is PdfArray pa && pa.Count > 0 ? pa[0] : null;
        }
        if (filter is not PdfName { Value: "Crypt" }) return true;
        return parms is PdfDict pd && pd["Name"] is PdfName { Value: var name } && name != "Identity";
    }

    private PdfObject DecryptStrings(PdfObject o, int num, int gen)
    {
        switch (o)
        {
            case PdfString s:
                s.Bytes = _decryptor!.DecryptString(s.Bytes, num, gen);
                return s;
            case PdfArray a:
                foreach (var item in a.Items) DecryptStrings(item, num, gen);
                return a;
            case PdfDict d:
                foreach (var item in d.Entries.Values) DecryptStrings(item, num, gen);
                return d;
            default:
                return o;
        }
    }

    private bool EndstreamFollows(int pos)
    {
        var d = Data;
        while (pos < d.Length && PdfLexer.IsWhite(d[pos])) pos++;
        return pos + 9 <= d.Length && d[pos] == 'e' && d[pos + 1] == 'n' && d[pos + 2] == 'd' && d[pos + 3] == 's' && d[pos + 4] == 't';
    }

    private int[]? _endstreams, _endobjs, _headers;

    private int FindEndstream(int from)
    {
        // Positions of the keywords are found in one pass and binary searched: scanning ahead from every stream with a
        // wrong /Length would be quadratic on a file made of such streams.
        var d = Data;
        lock (_lock)
        {
            if (_endstreams == null)
            {
                _endstreams = AllPositions(d, "endstream"u8);
                _endobjs = AllPositions(d, "endobj"u8);
                _headers = ObjectHeaders(d);
            }
        }
        // The stream ends at "endstream"; when that is missing, at "endobj" or at the next object, never further: a
        // file of unterminated streams must not give each one a copy of the rest of the file.
        int found = Next(_endstreams, from);
        int endobj = Next(_endobjs!, from), header = Next(_headers!, from);
        if (found < 0 || endobj >= 0 && endobj < found) found = endobj;
        if (found < 0 || header >= 0 && header < found) found = header;
        return found < 0 ? d.Length : found;

        static int[] AllPositions(byte[] data, ReadOnlySpan<byte> keyword)
        {
            var list = new List<int>();
            for (int i = 0; ;)
            {
                int k = data.AsSpan(i).IndexOf(keyword);
                if (k < 0) break;
                list.Add(i + k);
                i += k + keyword.Length;
            }
            return list.ToArray();
        }

        // Starts of "num gen obj" headers.
        static int[] ObjectHeaders(byte[] data)
        {
            var list = new List<int>();
            for (int i = 0; ;)
            {
                int k = data.AsSpan(i).IndexOf("obj"u8);
                if (k < 0) break;
                int o = i + k;
                i = o + 3;
                int p = o - 1;
                if (p < 0 || !PdfLexer.IsWhite(data[p])) continue;
                while (p >= 0 && PdfLexer.IsWhite(data[p])) p--;
                int digits = p;
                while (p >= 0 && data[p] >= '0' && data[p] <= '9') p--;
                if (p == digits || p < 0 || !PdfLexer.IsWhite(data[p])) continue;
                while (p >= 0 && PdfLexer.IsWhite(data[p])) p--;
                digits = p;
                while (p >= 0 && data[p] >= '0' && data[p] <= '9') p--;
                if (p == digits) continue;
                list.Add(p + 1);
            }
            return list.ToArray();
        }

        static int Next(int[] sorted, int from)
        {
            int idx = Array.BinarySearch(sorted, from);
            if (idx < 0) idx = ~idx;
            return idx < sorted.Length ? sorted[idx] : -1;
        }
    }

    // ------------------------------------------------------------------ streams

    // Work limits for the page being rendered on this thread (see BeginRender): content streams, forms, fonts and images
    // drawn many times or decompressing to huge sizes add up, and one page must not decode without bound.
    [ThreadStatic] private static long t_decodeBudget;
    [ThreadStatic] private static bool t_budgeted;
    [ThreadStatic] private static CancellationToken t_cancel;

    /// <summary>The most decoded stream bytes one page render may produce, counting repeated decodes.</summary>
    public const long MaxDecodedPerPage = 512L * 1024 * 1024;

    /// <summary>What the page being rendered on this thread may still decode.</summary>
    internal static long DecodeLimit => t_budgeted ? Math.Max(0, t_decodeBudget) : long.MaxValue;

    /// <summary>
    /// Applies a decode budget (by default the per-page one) and <paramref name="cancel"/> to decoding on this thread
    /// until disposed.
    /// </summary>
    public static RenderScope BeginRender(CancellationToken cancel, long budget = MaxDecodedPerPage)
    {
        t_budgeted = true;
        t_decodeBudget = budget;
        t_cancel = cancel;
        return default;
    }

    public readonly struct RenderScope : IDisposable
    {
        public void Dispose()
        {
            t_budgeted = false;
            t_cancel = default;
        }
    }

    private static void Charge(int decodedBytes)
    {
        t_cancel.ThrowIfCancellationRequested();
        if (t_budgeted && (t_decodeBudget -= decodedBytes) < 0)
            throw new InvalidDataException("The page decodes to more data than allowed.");
    }

    /// <summary>Charges memory an image codec allocates against the page's decode budget, and checks for cancellation.</summary>
    internal static void ChargeDecoded(long bytes)
    {
        t_cancel.ThrowIfCancellationRequested();
        if (t_budgeted && (t_decodeBudget -= bytes) < 0)
            throw new InvalidDataException("The page decodes to more data than allowed.");
    }

    /// <summary>The cancellation token of the render running on this thread.</summary>
    internal static CancellationToken CurrentCancellation => t_cancel;

    /// <summary>Throws if the render running on this thread was cancelled.</summary>
    internal static void CheckCancel() => t_cancel.ThrowIfCancellationRequested();

    /// <summary>Decodes a stream through all its filters.</summary>
    public byte[] DecodeStream(PdfStream s) => DecodeStream(s, stopAtImageFilter: false, out _, out _);

    /// <summary>
    /// Decodes a stream. With <paramref name="stopAtImageFilter"/>, decoding stops before an image codec (DCT, JPX,
    /// CCITT, JBIG2), which is returned in <paramref name="imageFilter"/> with its parameters.
    /// </summary>
    public byte[] DecodeStream(PdfStream s, bool stopAtImageFilter, out string? imageFilter, out PdfDict? imageParms)
    {
        imageFilter = null;
        imageParms = null;
        var filterObj = Resolve(s["Filter"] ?? s["F"]);
        var parmsObj = Resolve(s["DecodeParms"] ?? s["DP"]);
        var data = s.RawData;
        if (filterObj == null || filterObj.IsNull) return data;
        var filters = new List<string>();
        var parms = new List<PdfDict?>();
        if (filterObj is PdfName n)
        {
            filters.Add(Filters.Canonical(n.Value));
            parms.Add(parmsObj as PdfDict ?? (parmsObj is PdfArray pa0 && pa0.Count > 0 ? Resolve(pa0[0]) as PdfDict : null));
        }
        else if (filterObj is PdfArray fa)
        {
            for (int i = 0; i < fa.Count; i++)
            {
                if (Resolve(fa[i]) is not PdfName fn) continue;
                filters.Add(Filters.Canonical(fn.Value));
                parms.Add(parmsObj is PdfArray pa ? (i < pa.Count ? Resolve(pa[i]) as PdfDict : null) : i == 0 ? parmsObj as PdfDict : null);
            }
        }
        for (int i = 0; i < filters.Count; i++)
        {
            if (Filters.IsImageFilter(filters[i]))
            {
                if (stopAtImageFilter)
                {
                    imageFilter = filters[i];
                    imageParms = parms[i];
                    return data;
                }
                throw new NotSupportedException("Image filter " + filters[i] + " in a non-image stream.");
            }
            t_cancel.ThrowIfCancellationRequested();
            if (t_budgeted && t_decodeBudget <= 0) throw new InvalidDataException("The page decodes to more data than allowed.");
            data = Filters.Decode(filters[i], data, parms[i], this);
            Charge(data.Length);
        }
        return data;
    }

    // ------------------------------------------------------------------ typed accessors

    public PdfDict? Catalog => Resolve(Trailer["Root"]) as PdfDict;

    public PdfDict? GetDict(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfDict;
    public PdfArray? GetArray(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfArray;
    public PdfStream? GetStream(PdfDict? d, string key) => d == null ? null : Resolve(d[key]) as PdfStream;

    public string? GetName(PdfDict? d, string key) => d == null ? null : (Resolve(d[key]) as PdfName)?.Value;

    public int GetInt(PdfDict? d, string key, int fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfNumber n ? n.IntValue : fallback;
    }

    public double GetNumber(PdfDict? d, string key, double fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfNumber n ? n.Value : fallback;
    }

    public bool GetBool(PdfDict? d, string key, bool fallback)
    {
        if (d == null) return fallback;
        return Resolve(d[key]) is PdfBool b ? b.Value : fallback;
    }

    public byte[]? GetBytes(PdfDict? d, string key) => d == null ? null : (Resolve(d[key]) as PdfString)?.Bytes;

    public int GetIntValue(PdfObject? o) => Resolve(o) is PdfNumber n ? n.IntValue : 0;

    public double Num(PdfObject? o, double fallback = 0) => Resolve(o) is PdfNumber n ? n.Value : fallback;

    /// <summary>Reads a numeric array, e.g. a matrix or rectangle.</summary>
    public double[]? GetNumbers(PdfDict? d, string key) => ToNumbers(d == null ? null : Resolve(d[key]) as PdfArray);

    public double[]? ToNumbers(PdfArray? a)
    {
        if (a == null) return null;
        var r = new double[a.Count];
        for (int i = 0; i < r.Length; i++) r[i] = Num(a[i]);
        return r;
    }
}

using System.Security.Cryptography;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a parsed PDF out again as a new file: every object reachable from the catalog and the document information is
/// copied, renumbered, with its strings and streams decrypted (the parser has already done that) and, when an
/// <see cref="PdfEncryptor"/> is given, encrypted again with AES-256. Earlier revisions (incremental updates), unused
/// objects and object streams are not kept; the result has one classic cross-reference table.
/// </summary>
internal sealed class PdfRewriter : PdfObjectWriter
{
    private readonly PdfFile _file;
    private readonly PdfEncryptor? _crypt;
    private readonly ByteBuffer _out;
    private readonly Dictionary<int, int> _map = new(); // source object number -> new id; 0 = write null
    private readonly Queue<(int Num, int Id)> _queue = new();
    private readonly PdfXref _xref = new();
    private int _nextId = 1;
    private int _rootNum = -1;

    private readonly bool _reproducible;

    private PdfRewriter(PdfFile file, PdfEncryptor? crypt, bool reproducible) : base(PdfObjectSyntax.Rewrite)
    {
        _file = file;
        _crypt = crypt;
        _reproducible = reproducible;
        Encryption = crypt;
        _out = new ByteBuffer(Math.Max(4096, file.Data.Length + file.Data.Length / 8));
    }

    /// <summary>
    /// Rewrites <paramref name="file"/>, encrypted with <paramref name="crypt"/> or unencrypted when it is null. With a
    /// <paramref name="creationDate"/> or <c>SOURCE_DATE_EPOCH</c> and no encryption, the new identifier is a hash of the
    /// result, so the same input gives the same file; an encrypted result has random keys, so it stays random.
    /// </summary>
    public static ByteBuffer Write(PdfFile file, PdfEncryptor? crypt, DateTimeOffset? creationDate = null)
    {
        var w = new PdfRewriter(file, crypt, crypt == null && ReproducibleDate.Resolve(creationDate) != null);
        w.Run();
        return w._out;
    }

    private void Run()
    {
        var trailer = _file.Trailer;
        if (trailer["Encrypt"] is PdfRef enc) _map[enc.Num] = 0;
        if (trailer["Root"] is PdfRef root) _rootNum = root.Num;

        // AES-256 belongs to PDF 2.0, or to PDF 1.7 with Adobe extension level 8 (which the catalog then declares).
        var (major, minor) = HeaderVersion(_file.Data);
        if (_crypt != null && (major, minor).CompareTo((1, 7)) < 0) (major, minor) = (1, 7);
        _out.Ascii("%PDF-").Int(major).Byte((byte)'.').Int(minor).Ascii("\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        // The catalog and information dictionary come first: direct ones in the trailer are written as indirect objects.
        int rootId = TopLevel(trailer["Root"], isRoot: true);
        int infoId = _file.Resolve(trailer["Info"]) is PdfDict ? TopLevel(trailer["Info"], isRoot: false) : 0;
        while (_queue.Count > 0)
        {
            var (num, id) = _queue.Dequeue();
            WriteIndirect(id, _file.GetObject(num) ?? PdfNull.Instance, num == _rootNum);
        }

        int encryptId = 0;
        if (_crypt != null)
        {
            encryptId = _nextId++;
            Begin(encryptId);
            _crypt.WriteDictionary(_out);
            End();
        }

        long xref = _out.Length;
        int count = _nextId;
        _xref.WriteTable(_out, count);
        // The first identifier stays the document's permanent one; the second marks this new version, hashed from the
        // file when the result is to be reproducible.
        var id1 = _reproducible ? ReproducibleDate.FileId(_out.Span) : null;
        var id0 = _file.Resolve(trailer["ID"]) is PdfArray ids && ids.Count > 0 && _file.Resolve(ids[0]) is PdfString { Bytes.Length: > 0 } s
            ? s.Bytes
            : id1 ?? RandomNumberGenerator.GetBytes(16);
        PdfXref.WriteTrailer(_out, count, rootId, infoId, encryptId, id0, id1 ?? RandomNumberGenerator.GetBytes(16), upperHexId: false, xref);
    }

    private int TopLevel(PdfObject? o, bool isRoot)
    {
        if (o is PdfRef r)
        {
            if (_map.TryGetValue(r.Num, out int existing)) return existing;
            int id = _nextId++;
            _map[r.Num] = id;
            WriteIndirect(id, _file.GetObject(r.Num) ?? PdfNull.Instance, isRoot);
            return id;
        }
        int direct = _nextId++;
        WriteIndirect(direct, o ?? PdfNull.Instance, isRoot);
        return direct;
    }

    internal static (int Major, int Minor) HeaderVersion(byte[] d)
    {
        int lim = Math.Min(d.Length - 8, 1024);
        for (int i = 0; i < lim; i++)
            if (d[i] == '%' && d[i + 1] == 'P' && d[i + 2] == 'D' && d[i + 3] == 'F' && d[i + 4] == '-'
                && d[i + 5] is >= (byte)'1' and <= (byte)'2' && d[i + 6] == '.' && d[i + 7] is >= (byte)'0' and <= (byte)'9')
                return (d[i + 5] - '0', d[i + 7] - '0');
        return (1, 4);
    }

    private ByteBuffer Begin(int id) => _xref.Begin(_out, id);

    private void End() => PdfXref.End(_out);

    private void WriteIndirect(int id, PdfObject obj, bool isRoot)
    {
        PdfFile.CheckCancel();
        Begin(id);
        if (obj is PdfStream s) WriteStream(_out, _file, s);
        else if (isRoot && _crypt != null && obj is PdfDict catalog) WriteDict(_out, WithExtensionLevel(_file, catalog), 0);
        else WriteObject(_out, obj);
        End();
    }

    /// <summary>The catalog with /Extensions declaring Adobe extension level 8, under which PDF 1.7 readers expect AES-256.</summary>
    internal static PdfDict WithExtensionLevel(PdfFile file, PdfDict catalog)
    {
        var entries = new Dictionary<string, PdfObject>(catalog.Entries, StringComparer.Ordinal);
        var extensions = file.Resolve(catalog["Extensions"]) is PdfDict ext
            ? new Dictionary<string, PdfObject>(ext.Entries, StringComparer.Ordinal)
            : new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (file.Resolve(extensions.GetValueOrDefault("ADBE")) is not PdfDict adbe || file.GetInt(adbe, "ExtensionLevel", 0) < 8)
            extensions["ADBE"] = new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["BaseVersion"] = PdfName.Get("1.7"),
                ["ExtensionLevel"] = PdfNumber.Int(8),
            });
        entries["Extensions"] = new PdfDict(extensions);
        return new PdfDict(entries);
    }

    /// <summary>A reference is renumbered, and its object queued to be copied when it is met first.</summary>
    protected override void WriteRef(ByteBuffer b, PdfRef r)
    {
        if (!_map.TryGetValue(r.Num, out int id))
        {
            var target = _file.GetObject(r.Num);
            if (target == null || target.IsNull)
            {
                id = 0;
            }
            else
            {
                id = _nextId++;
                _queue.Enqueue((r.Num, id));
            }
            _map[r.Num] = id;
        }
        if (id == 0) b.Ascii("null");
        else b.Int(id).Ascii(" 0 R");
    }
}

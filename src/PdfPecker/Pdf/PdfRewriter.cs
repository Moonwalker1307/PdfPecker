using System.Globalization;
using System.Security.Cryptography;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a parsed PDF out again as a new file: every object reachable from the catalog and the document information is
/// copied, renumbered, with its strings and streams decrypted (the parser has already done that) and, when an
/// <see cref="PdfEncryptor"/> is given, encrypted again with AES-256. Earlier revisions (incremental updates), unused
/// objects and object streams are not kept; the result has one classic cross-reference table.
/// </summary>
internal sealed class PdfRewriter
{
    private readonly PdfFile _file;
    private readonly PdfEncryptor? _crypt;
    private readonly ByteBuffer _out;
    private readonly Dictionary<int, int> _map = new(); // source object number -> new id; 0 = write null
    private readonly Queue<(int Num, int Id)> _queue = new();
    private readonly List<long> _offsets = new() { 0 };
    private int _nextId = 1;
    private int _rootNum = -1;

    private PdfRewriter(PdfFile file, PdfEncryptor? crypt)
    {
        _file = file;
        _crypt = crypt;
        _out = new ByteBuffer(Math.Max(4096, file.Data.Length + file.Data.Length / 8));
    }

    /// <summary>Rewrites <paramref name="file"/>, encrypted with <paramref name="crypt"/> or unencrypted when it is null.</summary>
    public static ByteBuffer Write(PdfFile file, PdfEncryptor? crypt)
    {
        var w = new PdfRewriter(file, crypt);
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
        _out.Ascii("xref\n0 ").Int(count).Ascii("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            if (i < _offsets.Count && _offsets[i] > 0)
                _out.Ascii(_offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Ascii(" 00000 n \n");
            else
                _out.Ascii("0000000000 00000 f \n");
        }
        // The first identifier stays the document's permanent one; the second marks this new version.
        var id0 = _file.Resolve(trailer["ID"]) is PdfArray ids && ids.Count > 0 && _file.Resolve(ids[0]) is PdfString { Bytes.Length: > 0 } s
            ? s.Bytes
            : RandomNumberGenerator.GetBytes(16);
        _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(rootId).Ascii(" 0 R");
        if (infoId != 0) _out.Ascii(" /Info ").Int(infoId).Ascii(" 0 R");
        if (encryptId != 0) _out.Ascii(" /Encrypt ").Int(encryptId).Ascii(" 0 R");
        _out.Ascii(" /ID [");
        PdfSyntax.HexString(_out, id0); // never encrypted
        PdfSyntax.HexString(_out, RandomNumberGenerator.GetBytes(16));
        _out.Ascii("] >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");
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

    private static (int Major, int Minor) HeaderVersion(byte[] d)
    {
        int lim = Math.Min(d.Length - 8, 1024);
        for (int i = 0; i < lim; i++)
            if (d[i] == '%' && d[i + 1] == 'P' && d[i + 2] == 'D' && d[i + 3] == 'F' && d[i + 4] == '-'
                && d[i + 5] is >= (byte)'1' and <= (byte)'2' && d[i + 6] == '.' && d[i + 7] is >= (byte)'0' and <= (byte)'9')
                return (d[i + 5] - '0', d[i + 7] - '0');
        return (1, 4);
    }

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

    private void WriteIndirect(int id, PdfObject obj, bool isRoot)
    {
        PdfFile.CheckCancel();
        Begin(id);
        if (obj is PdfStream s) WriteStream(s);
        else if (isRoot && _crypt != null && obj is PdfDict catalog) WriteDict(WithExtensionLevel(catalog), 0);
        else WriteObject(obj, 0);
        End();
    }

    /// <summary>The catalog with /Extensions declaring Adobe extension level 8, under which PDF 1.7 readers expect AES-256.</summary>
    private PdfDict WithExtensionLevel(PdfDict catalog)
    {
        var entries = new Dictionary<string, PdfObject>(catalog.Entries, StringComparer.Ordinal);
        var extensions = _file.Resolve(catalog["Extensions"]) is PdfDict ext
            ? new Dictionary<string, PdfObject>(ext.Entries, StringComparer.Ordinal)
            : new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (_file.Resolve(extensions.GetValueOrDefault("ADBE")) is not PdfDict adbe || _file.GetInt(adbe, "ExtensionLevel", 0) < 8)
            extensions["ADBE"] = new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["BaseVersion"] = PdfName.Get("1.7"),
                ["ExtensionLevel"] = PdfNumber.Int(8),
            });
        entries["Extensions"] = new PdfDict(extensions);
        return new PdfDict(entries);
    }

    private void WriteStream(PdfStream s)
    {
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("Length");
        PdfMergeWriter.RemoveCryptFilter(_file, entries);
        var data = _crypt != null ? _crypt.Encrypt(s.RawData) : s.RawData;
        _out.Ascii("<<");
        foreach (var (key, value) in entries)
        {
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteObject(value, 1);
        }
        _out.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
    }

    private void WriteDict(PdfDict d, int depth)
    {
        _out.Ascii("<<");
        foreach (var (key, value) in d.Entries)
        {
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteObject(value, depth + 1);
        }
        _out.Ascii(" >>");
    }

    private void WriteObject(PdfObject? o, int depth)
    {
        if (depth > 256) { _out.Ascii("null"); return; }
        switch (o)
        {
            case PdfBool b:
                _out.Ascii(b.Value ? "true" : "false");
                break;
            case PdfNumber n:
                if (n.IsInteger) _out.Int((long)n.Value);
                else _out.Ascii(double.IsFinite(n.Value) ? n.Value.ToString("0.##########", CultureInfo.InvariantCulture) : "0");
                break;
            case PdfString s:
                if (_crypt != null) PdfSyntax.HexString(_out, _crypt.Encrypt(s.Bytes));
                else PdfSyntax.HexString(_out, s.Bytes);
                break;
            case PdfName nm:
                PdfSyntax.RawName(_out, nm.Value);
                break;
            case PdfRef r:
                WriteRef(r);
                break;
            case PdfArray a:
                _out.Byte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) _out.Sp();
                    WriteObject(a[i], depth + 1);
                }
                _out.Byte((byte)']');
                break;
            case PdfStream:
                // A stream can only be an indirect object; a direct one cannot occur in parsed data.
                _out.Ascii("null");
                break;
            case PdfDict d:
                WriteDict(d, depth);
                break;
            default:
                _out.Ascii("null");
                break;
        }
    }

    private void WriteRef(PdfRef r)
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
        if (id == 0) _out.Ascii("null");
        else _out.Int(id).Ascii(" 0 R");
    }
}

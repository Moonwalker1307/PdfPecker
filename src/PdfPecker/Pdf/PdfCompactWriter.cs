using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a parsed PDF out again in as few bytes as it can (see <see cref="PdfCompactor"/>): only the objects reachable
/// from the catalog and the document information, identical objects once, streams recompressed with the strongest
/// Flate level (and PNG predictors for images) when that is smaller, objects in minimal syntax packed into compressed
/// object streams, and a compressed cross-reference stream. The image settings of <see cref="PdfCompactOptions"/> add
/// downsampling and JPEG recompression.
/// </summary>
/// <remarks>
/// This file collects the objects, merges identical ones and writes the file; <c>PdfCompactWriter.Streams.cs</c>
/// recompresses streams and <c>PdfCompactWriter.Images.cs</c> downsamples and recompresses images.
/// </remarks>
internal sealed partial class PdfCompactWriter : PdfObjectWriter
{
    /// <summary>How many objects go into one object stream; more compress better, and every index still fits a byte.</summary>
    private const int ObjectsPerStream = 200;

    /// <summary>
    /// How many rounds of merging run. Each round can merge objects whose only difference was a reference to two objects
    /// merged in the round before (a font whose font files were identical), so a few rounds reach every level.
    /// </summary>
    private const int MaxMergeRounds = 16;

    // The source keys of a catalog or information dictionary written directly in the trailer, which get an object of
    // their own in the result.
    private const int DirectRootKey = -1;
    private const int DirectInfoKey = -2;

    private static readonly Regex PdfAPart = new(@"pdfaid:part\s*(?:>\s*|=\s*[""'])(\d)", RegexOptions.CultureInvariant);

    private readonly PdfFile _file;
    private readonly PdfCompactOptions _options;
    private readonly ByteBuffer _out;

    // The objects to write by source object number (or one of the direct keys), with the entries the options drop
    // removed, in the order they were reached from the catalog.
    private readonly Dictionary<int, PdfObject> _objects = new();
    private readonly List<int> _order = new();

    // Each object's representative among the identical objects, and the representatives' numbers in the result.
    private Dictionary<int, int> _canonical = new();
    private readonly Dictionary<int, int> _ids = new();

    // Images that other images use as a soft mask or stencil mask, which keep their exact samples.
    private readonly HashSet<int> _maskImages = new();

    private readonly bool _objectStreams;
    private readonly bool _removeMetadata;
    private bool _keyMode;

    private PdfCompactWriter(PdfFile file, PdfCompactOptions options) : base(PdfObjectSyntax.Compact)
    {
        _file = file;
        _options = options;
        _out = new ByteBuffer(Math.Max(4096, file.Data.Length / 2));
        var xmp = Xmp(file);
        int? pdfAPart = xmp != null && PdfAPart.Match(xmp) is { Success: true } m ? m.Groups[1].Value[0] - '0' : null;
        bool pdfUA = xmp != null && xmp.Contains("http://www.aiim.org/pdfua/ns/id/", StringComparison.Ordinal);
        // PDF/A-1 is based on PDF 1.4, which has neither object streams nor cross-reference streams.
        _objectStreams = options.UseObjectStreams && pdfAPart != 1;
        // PDF/A and PDF/UA need the XMP metadata that declares them.
        _removeMetadata = options.RemoveMetadata && pdfAPart == null && !pdfUA;
    }

    /// <summary>Compacts <paramref name="file"/>, which is neither encrypted nor signed.</summary>
    public static ByteBuffer Write(PdfFile file, PdfCompactOptions options)
    {
        var w = new PdfCompactWriter(file, options);
        w.Run();
        return w._out;
    }

    private void Run()
    {
        var trailer = _file.Trailer;
        int rootKey = trailer["Root"] is PdfRef root ? root.Num : DirectRootKey;
        int infoKey = 0;
        if (!_removeMetadata && _file.Resolve(trailer["Info"]) is PdfDict) infoKey = trailer["Info"] is PdfRef info ? info.Num : DirectInfoKey;
        Collect(rootKey, infoKey);
        if (!_objects.ContainsKey(rootKey)) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
        // An information dictionary reached only through a reference to a reference is not collected, and is left out.
        if (!_objects.ContainsKey(infoKey)) infoKey = 0;

        var placements = _options.MaxImageDpi != null ? ScanImagePlacements() : null;
        // A stream repeated with the same dictionary (the same font in every merged document) is recompressed once.
        var done = new Dictionary<string, PdfStream>(StringComparer.Ordinal);
        var plain = new PdfObjectWriter(PdfObjectSyntax.Compact);
        var buffer = new ByteBuffer();
        foreach (int key in _order)
        {
            PdfFile.CheckCancel();
            if (_objects[key] is not PdfStream s) continue;
            buffer.Clear();
            plain.WriteDict(buffer, s, 0);
            double dpi = placements != null && placements.TryGetValue(key, out double d) ? d : 0;
            buffer.Ascii(_maskImages.Contains(key) ? "mask" : "").Ascii(dpi.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            var memo = Convert.ToBase64String(SHA256.HashData(buffer.Span)) + Convert.ToBase64String(SHA256.HashData(s.RawData));
            if (!done.TryGetValue(memo, out var result)) done[memo] = result = Recompress(key, s, placements);
            _objects[key] = result;
        }

        MergeIdentical();
        foreach (int key in _order)
            if (_canonical[key] == key) _ids[key] = _ids.Count + 1;

        var (major, minor) = PdfRewriter.HeaderVersion(_file.Data);
        if (_objectStreams && (major, minor).CompareTo((1, 5)) < 0) (major, minor) = (1, 5);
        _out.Ascii("%PDF-").Int(major).Byte((byte)'.').Int(minor).Ascii("\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        int rootId = _ids[rootKey];
        int infoId = infoKey != 0 ? _ids[_canonical[infoKey]] : 0;
        if (_objectStreams) WriteWithObjectStreams(rootId, infoId);
        else WriteClassic(rootId, infoId);
    }

    // ------------------------------------------------------------------ collecting

    /// <summary>
    /// Finds every object reachable from the catalog and the information dictionary, breadth first, dropping the
    /// entries the options remove before following their references.
    /// </summary>
    private void Collect(int rootKey, int infoKey)
    {
        var queue = new Queue<int>();
        var seen = new HashSet<int> { rootKey };
        queue.Enqueue(rootKey);
        if (infoKey != 0 && seen.Add(infoKey)) queue.Enqueue(infoKey);
        var refs = new List<int>();
        while (queue.Count > 0)
        {
            PdfFile.CheckCancel();
            int key = queue.Dequeue();
            var o = key switch
            {
                DirectRootKey => _file.Trailer["Root"],
                DirectInfoKey => _file.Trailer["Info"],
                _ => _file.GetObject(key),
            };
            if (o == null || o.IsNull || o is PdfRef) continue; // a missing object: references to it are written as null
            o = Prune(o);
            _objects[key] = o;
            _order.Add(key);
            refs.Clear();
            References(o, refs, 0);
            foreach (int num in refs)
                if (seen.Add(num)) queue.Enqueue(num);
        }
    }

    /// <summary>
    /// A dictionary or stream without the entries the options remove, or the object itself when none apply. A stream also
    /// loses /Length, which is written from its data, so an indirect length is not kept as an object of its own.
    /// </summary>
    private PdfObject Prune(PdfObject o)
    {
        if (o is not PdfDict d) return o;
        if (d is PdfStream { } stream && stream.ContainsKey("Length"))
        {
            var withoutLength = new Dictionary<string, PdfObject>(stream.Entries, StringComparer.Ordinal);
            withoutLength.Remove("Length");
            d = new PdfStream(withoutLength, stream.RawData);
            o = d;
        }
        bool removeThumb = _options.RemoveApplicationData && _file.GetName(d, "Type") == "Page" && d.ContainsKey("Thumb");
        bool removePieceInfo = _options.RemoveApplicationData && d.ContainsKey("PieceInfo");
        bool removeMetadata = _removeMetadata && d.ContainsKey("Metadata");
        if (!removeThumb && !removePieceInfo && !removeMetadata) return o;
        var entries = new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal);
        if (removeThumb) entries.Remove("Thumb");
        if (removePieceInfo) entries.Remove("PieceInfo");
        if (removeMetadata) entries.Remove("Metadata");
        return d is PdfStream s ? new PdfStream(entries, s.RawData) : new PdfDict(entries);
    }

    /// <summary>
    /// Adds the object numbers <paramref name="o"/> refers to, and notes the images used as masks. Nesting deeper than the
    /// writer goes is not followed, as it is written as null.
    /// </summary>
    private void References(PdfObject o, List<int> refs, int depth)
    {
        if (depth > Syntax.MaxDepth) return;
        switch (o)
        {
            case PdfRef r:
                refs.Add(r.Num);
                break;
            case PdfArray a:
                foreach (var item in a.Items) References(item, refs, depth + 1);
                break;
            case PdfDict d:
                foreach (var (key, value) in d.Entries)
                {
                    if (key is "SMask" or "Mask" && value is PdfRef mask && d is PdfStream) _maskImages.Add(mask.Num);
                    References(value, refs, depth + 1);
                }
                break;
        }
    }

    // ------------------------------------------------------------------ merging identical objects

    /// <summary>
    /// Merges identical objects: in each round, two objects that may be shared and are written the same, with every
    /// reference replaced by its target's representative, become one. Objects whose identity matters (pages, fields,
    /// annotations, structure elements, outline items, optional content) are never merged.
    /// </summary>
    private void MergeIdentical()
    {
        foreach (int key in _order) _canonical[key] = key;
        var mergeable = _order.Where(k => IsMergeable(_objects[k])).ToList();
        if (mergeable.Count < 2) return;

        var dataHashes = new Dictionary<int, string>();
        foreach (int key in mergeable)
            if (_objects[key] is PdfStream s) dataHashes[key] = Convert.ToBase64String(SHA256.HashData(s.RawData));

        var buffer = new ByteBuffer();
        for (int round = 0; round < MaxMergeRounds; round++)
        {
            var next = new Dictionary<int, int>(_canonical);
            var firstWithKey = new Dictionary<string, int>(StringComparer.Ordinal);
            bool changed = false;
            _keyMode = true;
            try
            {
                foreach (int key in mergeable)
                {
                    PdfFile.CheckCancel();
                    buffer.Clear();
                    var o = _objects[key];
                    if (o is PdfStream s)
                    {
                        WriteDict(buffer, s, 0);
                        buffer.Ascii("stream").Ascii(dataHashes[key]);
                    }
                    else
                    {
                        // The kind of object comes first, so that an array and a dictionary can never collide.
                        buffer.Ascii(o.GetType().Name);
                        WriteObject(buffer, o);
                    }
                    var hash = Convert.ToBase64String(SHA256.HashData(buffer.Span));
                    if (firstWithKey.TryGetValue(hash, out int first))
                    {
                        if (_canonical[key] != first) changed = true;
                        next[key] = first;
                    }
                    else
                    {
                        firstWithKey[hash] = key;
                        if (_canonical[key] != key) changed = true;
                        next[key] = key;
                    }
                }
            }
            finally
            {
                _keyMode = false;
            }
            _canonical = next;
            if (!changed) break;
        }
    }

    /// <summary>
    /// Whether an object may be shared by everything that refers to it: streams, arrays, plain values, and dictionaries of
    /// the kinds documents share anyway (fonts, graphics states, resources, functions, shadings).
    /// </summary>
    private bool IsMergeable(PdfObject o)
    {
        if (o is PdfStream) return true;
        if (o is not PdfDict d) return true;
        var type = _file.GetName(d, "Type");
        if (type != null)
            return type is "Font" or "FontDescriptor" or "ExtGState" or "Encoding" or "Pattern" or "Group" or "Mask" or "Halftone";
        if (d.ContainsKey("FunctionType") || d.ContainsKey("ShadingType")) return true;
        foreach (var key in d.Entries.Keys)
            if (key is "Parent" or "P" or "Kids" or "K" or "FT" or "T" or "S" or "Subtype" or "Pg" or "Annots" or "Contents" or "Next"
                or "Prev" or "First" or "Last" or "Dest" or "A" or "OC")
                return false;
        return true;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>A reference becomes its representative's new number, or null when its target does not exist.</summary>
    protected override void WriteRef(ByteBuffer b, PdfRef r)
    {
        if (!_canonical.TryGetValue(r.Num, out int canonical)) b.Ascii("null");
        else if (_keyMode) b.Int(canonical).Ascii(" R");
        else b.Int(_ids[canonical]).Ascii(" 0 R");
    }

    /// <summary>Every object as a top-level object, with a classic cross-reference table, as PDF/A-1 requires.</summary>
    private void WriteClassic(int rootId, int infoId)
    {
        var xref = new PdfXref();
        foreach (int key in _order)
        {
            if (_canonical[key] != key) continue;
            PdfFile.CheckCancel();
            xref.Begin(_out, _ids[key]);
            WriteBody(_out, _objects[key]);
            PdfXref.End(_out);
        }
        long start = _out.Length;
        int count = _ids.Count + 1;
        xref.WriteTable(_out, count);
        var (id0, id1) = FileIds();
        PdfXref.WriteTrailer(_out, count, rootId, infoId, 0, id0, id1, upperHexId: false, start);
    }

    /// <summary>
    /// Streams as top-level objects, everything else in compressed object streams, and a compressed cross-reference
    /// stream in place of the table and trailer.
    /// </summary>
    private void WriteWithObjectStreams(int rootId, int infoId)
    {
        int count = _ids.Count + 1;
        var offsets = new long[count];
        var inStream = new (int Stream, int Index)[count];
        var packed = new List<int>();
        foreach (int key in _order)
        {
            if (_canonical[key] != key) continue;
            PdfFile.CheckCancel();
            int id = _ids[key];
            if (_objects[key] is PdfStream s)
            {
                offsets[id] = _out.Length;
                _out.Int(id).Ascii(" 0 obj\n");
                WriteBody(_out, s);
                PdfXref.End(_out);
            }
            else
            {
                packed.Add(key);
            }
        }

        var streamOffsets = new List<long>();
        var body = new ByteBuffer();
        var header = new ByteBuffer();
        for (int start = 0; start < packed.Count; start += ObjectsPerStream)
        {
            PdfFile.CheckCancel();
            int streamId = count + streamOffsets.Count;
            int n = Math.Min(ObjectsPerStream, packed.Count - start);
            body.Clear();
            header.Clear();
            for (int i = 0; i < n; i++)
            {
                int key = packed[start + i];
                int id = _ids[key];
                inStream[id] = (streamId, i);
                if (i > 0) header.Sp();
                header.Int(id).Sp().Int(body.Length);
                if (i > 0) body.Nl();
                WriteObject(body, _objects[key]);
            }
            header.Nl();
            var data = Deflate(Concat(header.Span, body.Span));
            streamOffsets.Add(_out.Length);
            _out.Int(streamId).Ascii(" 0 obj\n<</Type/ObjStm/N ").Int(n).Ascii("/First ").Int(header.Length)
                .Ascii("/Filter/FlateDecode/Length ").Int(data.Length).Ascii(">>stream\n").Bytes(data).Ascii("\nendstream");
            PdfXref.End(_out);
        }

        // The cross-reference stream lists itself too. Its rows are type, offset or object stream, and generation or index.
        int xrefId = count + streamOffsets.Count;
        int size = xrefId + 1;
        long xrefOffset = _out.Length;
        int offsetBytes = BytesFor(Math.Max(xrefOffset, size));
        const int FieldBytes = 2;
        int rowLength = 1 + offsetBytes + FieldBytes;
        var rows = new byte[size * rowLength];
        for (int id = 0; id < size; id++)
        {
            var row = rows.AsSpan(id * rowLength, rowLength);
            if (id == 0)
            {
                Put(row.Slice(1 + offsetBytes), 0xFFFF); // the head of the free list, generation 65535
            }
            else if (id < count && inStream[id].Stream != 0)
            {
                row[0] = 2;
                Put(row.Slice(1, offsetBytes), inStream[id].Stream);
                Put(row.Slice(1 + offsetBytes), inStream[id].Index);
            }
            else
            {
                long offset = id < count ? offsets[id] : id < xrefId ? streamOffsets[id - count] : xrefOffset;
                row[0] = 1;
                Put(row.Slice(1, offsetBytes), offset);
            }
        }
        var xrefData = Deflate(PngUp(rows, rowLength));
        var (id0, id1) = FileIds();
        _out.Int(xrefId).Ascii(" 0 obj\n<</Type/XRef/Size ").Int(size).Ascii("/W[1 ").Int(offsetBytes).Ascii(" ").Int(FieldBytes)
            .Ascii("]/Root ").Int(rootId).Ascii(" 0 R");
        if (infoId != 0) _out.Ascii("/Info ").Int(infoId).Ascii(" 0 R");
        _out.Ascii("/ID[");
        PdfSyntax.Hex(_out, id0, upper: false);
        PdfSyntax.Hex(_out, id1, upper: false);
        _out.Ascii("]/Filter/FlateDecode/DecodeParms<</Predictor 12/Columns ").Int(rowLength).Ascii(">>/Length ").Int(xrefData.Length)
            .Ascii(">>stream\n").Bytes(xrefData).Ascii("\nendstream");
        PdfXref.End(_out);
        _out.Ascii("startxref\n").Int(xrefOffset).Ascii("\n%%EOF\n");
    }

    /// <summary>
    /// The file identifier: the input's permanent first identifier (or a hash when it has none), and a second one hashed
    /// from the result, so the same input always gives the same file.
    /// </summary>
    private (byte[] Id0, byte[] Id1) FileIds()
    {
        var id1 = ReproducibleDate.FileId(_out.Span);
        var id0 = _file.Resolve(_file.Trailer["ID"]) is PdfArray ids && ids.Count > 0 && _file.Resolve(ids[0]) is PdfString { Bytes.Length: > 0 } s
            ? s.Bytes
            : id1;
        return (id0, id1);
    }

    /// <summary>An object's body: a stream with its dictionary, /Length last, or any other object.</summary>
    private void WriteBody(ByteBuffer b, PdfObject o)
    {
        if (o is not PdfStream s)
        {
            WriteObject(b, o);
            return;
        }
        b.Ascii("<<");
        foreach (var (key, value) in s.Entries)
            if (key != "Length") WriteEntry(b, null, key, value, 0);
        b.Ascii("/Length ").Int(s.RawData.Length).Ascii(">>stream\n").Bytes(s.RawData).Ascii("\nendstream");
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
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

    /// <summary>The XMP metadata of the catalog, which says whether the file claims PDF/A or PDF/UA.</summary>
    private static string? Xmp(PdfFile file)
    {
        if (file.Resolve(file.Catalog?["Metadata"]) is not PdfStream meta) return null;
        try
        {
            return Encoding.UTF8.GetString(file.DecodeStream(meta));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}

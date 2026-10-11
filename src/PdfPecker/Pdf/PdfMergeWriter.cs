using System.Security.Cryptography;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a new PDF holding the pages of several parsed PDFs. Each input's objects reachable from its pages (and its
/// bookmarks) are copied once, renumbered; references to the page tree and catalog are dropped, named destinations are
/// resolved to explicit ones, and signature fields are left out.
/// </summary>
internal sealed class PdfMergeWriter
{
    private sealed class Input
    {
        public PdfFile File = null!;
        public readonly List<(PdfDict Page, int SourceNum, int Id, PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate)> Pages = new();
        public readonly Dictionary<int, int> Map = new(); // source object number -> new id; 0 = write null
        public readonly List<int> TopOutline = new(); // source numbers of the top-level bookmarks
        public Dictionary<string, PdfObject>? NamedDests;
    }

    private readonly PdfMergeOptions _options;
    private readonly CancellationToken _cancel;
    private ByteBuffer _out = new(1 << 16);
    private readonly List<long> _offsets = new() { 0 }; // by object id; 0 = not written
    private int _nextId;

    private const int CatalogId = 1, PagesId = 2, InfoId = 3;

    public PdfMergeWriter(PdfMergeOptions options, CancellationToken cancel)
    {
        _options = options;
        _cancel = cancel;
        _nextId = InfoId + 1;
    }

    private int NewId() => _nextId++;

    public byte[] Merge(List<byte[]> data)
    {
        // 1. Open every input and number its pages, so links between inputs' bookmarks can be chained up front.
        var inputs = new List<Input>(data.Count);
        int pageCount = 0, maxPages = Math.Max(1, _options.MaxPages);
        for (int i = 0; i < data.Count; i++)
        {
            _cancel.ThrowIfCancellationRequested();
            var input = new Input();
            string? password = _options.Passwords is { } pw && i < pw.Count ? pw[i] : null;
            using (PdfFile.BeginRender(_cancel))
            {
                try
                {
                    input.File = PdfFile.Open(data[i], password);
                }
                catch (PdfFormatException ex)
                {
                    throw new InvalidDataException($"PDF {i + 1} is not readable: {ex.Message}", ex);
                }
                catch (PdfPasswordException ex)
                {
                    throw new PdfPasswordException($"PDF {i + 1}: {ex.Message}");
                }
                var catalog = input.File.Catalog;
                if (input.File.Trailer["Root"] is PdfRef root) input.Map[root.Num] = 0;
                if (input.File.Trailer["Encrypt"] is PdfRef enc) input.Map[enc.Num] = 0;
                if (catalog?["Pages"] is { } pagesRef)
                    CollectPages(input, pagesRef, null, null, null, null, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
                if (_options.Bookmarks) CollectOutline(input, catalog);
            }
            pageCount += input.Pages.Count;
            if (pageCount > maxPages)
                throw new PdfLimitException($"The merged document has more than {maxPages} pages (PdfMergeOptions.MaxPages).");
            inputs.Add(input);
        }

        int outlinesId = inputs.Any(x => x.TopOutline.Count > 0) ? NewId() : 0;
        int firstOutline = 0, lastOutline = 0, outlineCount = 0;
        // Top-level bookmarks are chained across inputs: (new id) -> (previous, next).
        var chain = new Dictionary<int, (int Prev, int Next)>();
        foreach (var input in inputs)
        {
            foreach (var num in input.TopOutline)
            {
                int id = input.Map[num];
                if (firstOutline == 0) firstOutline = id;
                if (lastOutline != 0)
                {
                    chain[lastOutline] = (chain.TryGetValue(lastOutline, out var p) ? p.Prev : 0, id);
                    chain[id] = (lastOutline, 0);
                }
                else chain[id] = (0, 0);
                lastOutline = id;
                outlineCount++;
            }
        }

        // 2. Write.
        _out.Ascii("%PDF-1.7\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();
        var props = Properties(inputs[0].File);
        Begin(PagesId).Ascii("<< /Type /Pages /Count ").Int(pageCount).Ascii(" /Kids [");
        foreach (var input in inputs)
            foreach (var p in input.Pages) _out.Int(p.Id).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();
        var now = DateTime.UtcNow;
        Begin(InfoId);
        props.WriteInfo(_out, now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)), modDate: true, PdfSyntax.TextString);
        End();
        if (outlinesId != 0)
        {
            Begin(outlinesId).Ascii("<< /Type /Outlines /First ").Int(firstOutline).Ascii(" 0 R /Last ").Int(lastOutline)
                .Ascii(" 0 R /Count ").Int(outlineCount).Ascii(" >>");
            End();
        }

        // Optional content (layers): every input's groups, and the ones hidden by default.
        var ocgs = new ByteBuffer(64);
        var ocgsOff = new ByteBuffer(64);
        foreach (var input in inputs)
        {
            using (PdfFile.BeginRender(_cancel))
            {
                var queue = new Queue<(int Num, int Id)>();
                foreach (var p in input.Pages) WritePage(input, p, queue);
                if (input.File.GetDict(input.File.Catalog, "OCProperties") is { } oc)
                {
                    WriteItemsTo(ocgs, input, input.File.GetArray(oc, "OCGs"), queue);
                    WriteItemsTo(ocgsOff, input, input.File.GetArray(input.File.GetDict(oc, "D"), "OFF"), queue);
                }
                foreach (var num in input.TopOutline)
                {
                    int id = input.Map[num];
                    var (prev, next) = chain[id];
                    WriteOutlineItem(input, num, id, prev, next, outlinesId, queue);
                }
                while (queue.Count > 0)
                {
                    _cancel.ThrowIfCancellationRequested();
                    var (num, id) = queue.Dequeue();
                    var obj = input.File.GetObject(num) ?? PdfNull.Instance;
                    Begin(id);
                    if (obj is PdfStream s) WriteStream(input, s, queue);
                    else WriteObject(input, obj, queue, 0);
                    End();
                }
            }
            input.File = null!; // let the parsed objects go
        }

        Begin(CatalogId).Ascii("<< /Type /Catalog /Pages ").Int(PagesId).Ascii(" 0 R");
        if (outlinesId != 0) _out.Ascii(" /Outlines ").Int(outlinesId).Ascii(" 0 R");
        if (ocgs.Length > 0)
        {
            _out.Ascii(" /OCProperties << /OCGs [").Bytes(ocgs.Span).Ascii("] /D << /Order [").Bytes(ocgs.Span).Ascii("]");
            if (ocgsOff.Length > 0) _out.Ascii(" /OFF [").Bytes(ocgsOff.Span).Ascii("]");
            _out.Ascii(" >> >>");
        }
        _out.Ascii(" >>");
        End();

        // Cross-reference table and trailer.
        long xref = _out.Length;
        int count = _nextId;
        _out.Ascii("xref\n0 ").Int(count).Ascii("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            if (i < _offsets.Count && _offsets[i] > 0)
                _out.Ascii(_offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Ascii(" 00000 n \n");
            else
                _out.Ascii("0000000000 00000 f \n");
        }
        var id0 = RandomNumberGenerator.GetBytes(16);
        _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(CatalogId).Ascii(" 0 R /Info ").Int(InfoId).Ascii(" 0 R /ID [");
        PdfSyntax.HexString(_out, id0);
        PdfSyntax.HexString(_out, id0);
        _out.Ascii("] >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");
        return _out.Span.ToArray();
    }

    // ------------------------------------------------------------------ reading

    private void CollectPages(Input input, PdfObject node, PdfObject? res, PdfObject? media, PdfObject? crop, PdfObject? rotate,
        HashSet<PdfDict> seen, int depth)
    {
        var file = input.File;
        if (depth > 64 || file.Resolve(node) is not PdfDict dict || !seen.Add(dict)) return;
        res = dict["Resources"] ?? res;
        media = dict["MediaBox"] ?? media;
        crop = dict["CropBox"] ?? crop;
        rotate = dict["Rotate"] ?? rotate;
        var kids = file.GetArray(dict, "Kids");
        if (kids != null && file.GetName(dict, "Type") != "Page")
        {
            // An intermediate node: references to it (a page's /Parent) are dropped.
            if (node is PdfRef r) input.Map[r.Num] = 0;
            foreach (var k in kids.Items) CollectPages(input, k, res, media, crop, rotate, seen, depth + 1);
            return;
        }
        if (input.Pages.Count + 1 > Math.Max(1, _options.MaxPages))
            throw new PdfLimitException($"The merged document has more than {_options.MaxPages} pages (PdfMergeOptions.MaxPages).");
        int id = NewId();
        int num = node is PdfRef pr ? pr.Num : 0;
        if (num != 0) input.Map[num] = id;
        input.Pages.Add((dict, num, id, res, media, crop, rotate));
    }

    private void CollectOutline(Input input, PdfDict? catalog)
    {
        var file = input.File;
        var rootRef = catalog?["Outlines"];
        if (file.Resolve(rootRef) is not PdfDict root) return;
        var seen = new HashSet<int>();
        var item = root["First"];
        while (item is PdfRef r && seen.Add(r.Num) && seen.Count <= 100_000)
        {
            if (file.GetObject(r.Num) is not PdfDict d) break;
            if (!input.Map.ContainsKey(r.Num)) input.Map[r.Num] = NewId();
            input.TopOutline.Add(r.Num);
            item = d["Next"];
        }
        // The old root is replaced by the merged one: its children's /Parent is rewritten when they are written.
        if (rootRef is PdfRef rr && !input.Map.ContainsKey(rr.Num)) input.Map[rr.Num] = 0;
    }

    private DocumentProperties Properties(PdfFile first)
    {
        string? title = null, author = null, subject = null, keywords = null, creator = null;
        var custom = new List<KeyValuePair<string, string>>();
        if (first.Resolve(first.Trailer["Info"]) is PdfDict info)
        {
            foreach (var (rawKey, value) in info.Entries)
            {
                if (first.Resolve(value) is not PdfString s) continue;
                var text = s.Text;
                switch (rawKey)
                {
                    case "Title": title = text; break;
                    case "Author": author = text; break;
                    case "Subject": subject = text; break;
                    case "Keywords": keywords = text; break;
                    case "Creator": creator = text; break;
                    default:
                        if (DocumentProperties.StandardKeys.Contains(rawKey) || rawKey.Length == 0) break;
                        custom.Add(new(Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(rawKey)), text));
                        break;
                }
            }
        }
        foreach (var kv in DocumentProperties.Validate(_options.CustomProperties))
        {
            custom.RemoveAll(c => c.Key == kv.Key);
            custom.Add(kv);
        }
        return new DocumentProperties
        {
            Title = _options.Title ?? title,
            Author = _options.Author ?? author,
            Subject = _options.Subject ?? subject,
            Keywords = _options.Keywords ?? keywords,
            Creator = _options.Creator ?? creator,
            Producer = _options.Producer,
            Custom = custom,
        };
    }

    // ------------------------------------------------------------------ writing

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

    private void WritePage(Input input, (PdfDict Page, int SourceNum, int Id, PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate) p,
        Queue<(int, int)> queue)
    {
        _cancel.ThrowIfCancellationRequested();
        var file = input.File;
        Begin(p.Id).Ascii("<< /Type /Page /Parent ").Int(PagesId).Ascii(" 0 R");
        foreach (var (key, value) in p.Page.Entries)
        {
            // Inherited attributes are written below; /B (article beads) leads to the whole article thread.
            if (key is "Type" or "Parent" or "B" or "Annots" or "Resources" or "MediaBox" or "CropBox" or "Rotate") continue;
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteObject(input, value, queue, 0, key);
        }
        _out.Ascii(" /Resources ");
        WriteObject(input, p.Resources ?? new PdfDict(), queue, 0);
        _out.Ascii(" /MediaBox ");
        if (p.MediaBox != null) WriteObject(input, p.MediaBox, queue, 0);
        else _out.Ascii("[0 0 612 792]");
        if (p.CropBox != null) { _out.Ascii(" /CropBox "); WriteObject(input, p.CropBox, queue, 0); }
        if (p.Rotate != null) { _out.Ascii(" /Rotate "); WriteObject(input, p.Rotate, queue, 0); }
        if (file.GetArray(p.Page, "Annots") is { } annots)
        {
            _out.Ascii(" /Annots [");
            foreach (var a in annots.Items)
            {
                if (file.Resolve(a) is not PdfDict ad || IsSignatureField(file, ad)) continue;
                WriteObject(input, a, queue, 1);
                _out.Sp();
            }
            _out.Ascii("]");
        }
        _out.Ascii(" >>");
        End();
    }

    /// <summary>Writes the (referenced) items of an array to another buffer, separated by spaces.</summary>
    private void WriteItemsTo(ByteBuffer target, Input input, PdfArray? items, Queue<(int, int)> queue)
    {
        if (items == null) return;
        var saved = _out;
        _out = target;
        try
        {
            foreach (var item in items.Items)
            {
                if (item is not PdfRef) continue; // optional content groups are always indirect
                WriteObject(input, item, queue, 1);
                _out.Sp();
            }
        }
        finally
        {
            _out = saved;
        }
    }

    private static bool IsSignatureField(PdfFile file, PdfDict annot)
    {
        if (file.GetName(annot, "Subtype") != "Widget") return false;
        PdfDict? field = annot;
        for (int i = 0; field != null && i < 32; i++)
        {
            if (file.GetName(field, "FT") is { } ft) return ft == "Sig";
            field = file.GetDict(field, "Parent");
        }
        return false;
    }

    private void WriteOutlineItem(Input input, int num, int id, int prev, int next, int parent, Queue<(int, int)> queue)
    {
        var d = input.File.GetObject(num) as PdfDict ?? new PdfDict();
        Begin(id).Ascii("<< /Parent ").Int(parent).Ascii(" 0 R");
        if (prev != 0) _out.Ascii(" /Prev ").Int(prev).Ascii(" 0 R");
        if (next != 0) _out.Ascii(" /Next ").Int(next).Ascii(" 0 R");
        foreach (var (key, value) in d.Entries)
        {
            if (key is "Parent" or "Prev" or "Next") continue;
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteObject(input, value, queue, 1, key);
        }
        _out.Ascii(" >>");
        End();
    }

    private void WriteStream(Input input, PdfStream s, Queue<(int, int)> queue)
    {
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("Length");
        RemoveCryptFilter(input.File, entries);
        _out.Ascii("<<");
        foreach (var (key, value) in entries)
        {
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteObject(input, value, queue, 1, key);
        }
        _out.Ascii(" /Length ").Int(s.RawData.Length).Ascii(" >>\nstream\n").Bytes(s.RawData).Ascii("\nendstream");
    }

    /// <summary>The input's decryption is already undone, so a /Crypt filter in the chain must go too.</summary>
    private static void RemoveCryptFilter(PdfFile file, Dictionary<string, PdfObject> entries)
    {
        var filter = file.Resolve(entries.GetValueOrDefault("Filter"));
        if (filter is PdfName { Value: "Crypt" })
        {
            entries.Remove("Filter");
            entries.Remove("DecodeParms");
        }
        else if (filter is PdfArray fa && fa.Items.Any(f => file.Resolve(f) is PdfName { Value: "Crypt" }))
        {
            var parms = file.Resolve(entries.GetValueOrDefault("DecodeParms")) as PdfArray;
            var nf = new List<PdfObject>();
            var np = new List<PdfObject>();
            for (int i = 0; i < fa.Count; i++)
            {
                if (file.Resolve(fa[i]) is PdfName { Value: "Crypt" }) continue;
                nf.Add(fa[i]);
                np.Add(parms != null && i < parms.Count ? parms[i] : PdfNull.Instance);
            }
            entries["Filter"] = new PdfArray(nf);
            if (parms != null) entries["DecodeParms"] = new PdfArray(np);
        }
    }

    private void WriteObject(Input input, PdfObject? o, Queue<(int, int)> queue, int depth, string? key = null)
    {
        if (depth > 256) { _out.Ascii("null"); return; }
        switch (o)
        {
            case null:
            case PdfNull:
                _out.Ascii("null");
                break;
            case PdfBool b:
                _out.Ascii(b.Value ? "true" : "false");
                break;
            case PdfNumber n:
                if (n.IsInteger) _out.Int((long)n.Value);
                else _out.Ascii(double.IsFinite(n.Value) ? n.Value.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) : "0");
                break;
            case PdfString s:
                if (key == "Dest") { WriteObject(input, NamedDest(input, s) ?? PdfNull.Instance, queue, depth + 1); break; }
                PdfSyntax.HexString(_out, s.Bytes);
                break;
            case PdfName nm:
                if (key == "Dest") { WriteObject(input, NamedDest(input, nm) ?? PdfNull.Instance, queue, depth + 1); break; }
                PdfSyntax.RawName(_out, nm.Value);
                break;
            case PdfRef r:
                WriteRef(input, r, queue, key);
                break;
            case PdfArray a:
                _out.Byte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) _out.Sp();
                    WriteObject(input, a[i], queue, depth + 1);
                }
                _out.Byte((byte)']');
                break;
            case PdfStream:
                // A stream can only be an indirect object; a direct one cannot occur in parsed data.
                _out.Ascii("null");
                break;
            case PdfDict d:
                WriteDict(input, d, queue, depth);
                break;
            default:
                _out.Ascii("null");
                break;
        }
    }

    private void WriteDict(Input input, PdfDict d, Queue<(int, int)> queue, int depth)
    {
        var file = input.File;
        // A go-to action naming its destination: point it at the page instead, since the name tree is not copied.
        bool goTo = file.GetName(d, "S") == "GoTo";
        _out.Ascii("<<");
        foreach (var (key, value) in d.Entries)
        {
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            if (goTo && key == "D" && file.Resolve(value) is PdfString or PdfName)
                WriteObject(input, NamedDest(input, file.Resolve(value)!) ?? PdfNull.Instance, queue, depth + 1);
            else
                WriteObject(input, value, queue, depth + 1, key);
        }
        _out.Ascii(" >>");
    }

    private void WriteRef(Input input, PdfRef r, Queue<(int, int)> queue, string? key)
    {
        if (!input.Map.TryGetValue(r.Num, out int id))
        {
            var target = input.File.GetObject(r.Num);
            if (target == null || target.IsNull) { input.Map[r.Num] = 0; _out.Ascii("null"); return; }
            // A named destination behind a reference.
            if (key == "Dest" && target is PdfString or PdfName)
            {
                WriteObject(input, NamedDest(input, target) ?? PdfNull.Instance, queue, 1);
                return;
            }
            id = NewId();
            input.Map[r.Num] = id;
            queue.Enqueue((r.Num, id));
        }
        if (id == 0) _out.Ascii("null");
        else _out.Int(id).Ascii(" 0 R");
    }

    // ------------------------------------------------------------------ named destinations

    /// <summary>The explicit destination array a destination name stands for, or null.</summary>
    private PdfObject? NamedDest(Input input, PdfObject name)
    {
        var file = input.File;
        if (input.NamedDests == null)
        {
            input.NamedDests = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            var catalog = file.Catalog;
            // PDF 1.1: a dictionary of names.
            if (file.GetDict(catalog, "Dests") is { } dests)
                foreach (var (k, v) in dests.Entries) input.NamedDests.TryAdd("/" + k, v);
            // PDF 1.2 and later: a name tree of strings.
            if (file.GetDict(file.GetDict(catalog, "Names"), "Dests") is { } tree)
                WalkNameTree(file, tree, input.NamedDests, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
        }
        string key = name switch
        {
            PdfName n => "/" + n.Value,
            PdfString s => Encoding.Latin1.GetString(s.Bytes),
            _ => "",
        };
        if (!input.NamedDests.TryGetValue(key, out var dest) && name is PdfName nn)
            input.NamedDests.TryGetValue(nn.Value, out dest); // some writers use names for name-tree keys
        dest = file.Resolve(dest);
        if (dest is PdfDict dd) dest = file.Resolve(dd["D"]);
        return dest as PdfArray;
    }

    private static void WalkNameTree(PdfFile file, PdfDict node, Dictionary<string, PdfObject> into, HashSet<PdfDict> seen, int depth)
    {
        if (depth > 32 || !seen.Add(node) || into.Count > 100_000) return;
        if (file.GetArray(node, "Names") is { } names)
        {
            for (int i = 0; i + 1 < names.Count; i += 2)
                if (file.Resolve(names[i]) is PdfString k) into.TryAdd(Encoding.Latin1.GetString(k.Bytes), names[i + 1]);
        }
        if (file.GetArray(node, "Kids") is { } kids)
            foreach (var kid in kids.Items)
                if (file.Resolve(kid) is PdfDict kd) WalkNameTree(file, kd, into, seen, depth + 1);
    }
}

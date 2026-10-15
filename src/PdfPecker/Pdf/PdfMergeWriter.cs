using System.Security.Cryptography;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a new PDF holding the pages of several parsed PDFs. Each input's objects reachable from its pages (and its
/// bookmarks and form fields) are copied once, renumbered; references to the page tree and catalog are dropped, named
/// destinations are resolved to explicit ones, and signature fields are left out. Tagged inputs' structure trees are
/// joined under one root, with their parent-tree keys shifted so they stay apart. Identical streams without references
/// (images, embedded fonts, ICC profiles), which a run of documents from one template repeats, are written once.
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
        public readonly List<int> Fields = new(); // source numbers of the top-level form fields kept
        public readonly Dictionary<int, Dictionary<string, PdfObject>> Overrides = new(); // entries to replace when copying an object
        public bool NeedAppearances;
        public int StructOffset = -1; // added to the input's parent-tree keys; -1 = untagged, its keys are dropped
        public SortedDictionary<int, PdfObject>? ParentNums;
    }

    private readonly PdfMergeOptions _options;
    private CancellationToken _cancel;
    private readonly Stream? _sink; // written to as the buffer fills, when merging to a stream synchronously
    private readonly ByteBuffer _main = new(1 << 16);
    private ByteBuffer _out;
    private long _flushed; // bytes already handed out of _main
    private readonly List<long> _offsets = new() { 0 }; // by object id; 0 = not written
    private int _nextId;
    private readonly Dictionary<string, int> _streams = new(StringComparer.Ordinal); // content hash -> id of a written leaf stream

    private const int CatalogId = 1, PagesId = 2, InfoId = 3;
    private const int FlushSize = 1 << 20;

    private int _inputs, _pageCount;
    private readonly List<int> _pageIds = new();
    private DocumentProperties? _props;
    // Bookmarks: the merged root, the top-level items so far, and the last one, held back until the next is known.
    private int _outlinesId, _firstOutline, _lastOutline, _outlineCount;
    private (int Id, int Prev, ByteBuffer Body)? _pendingOutline;
    // Optional content (layers): every input's groups, and the ones hidden by default.
    private readonly ByteBuffer _ocgs = new(64), _ocgsOff = new(64);
    // Interactive form: every input's fields, and the fonts their default appearances name (the first input's win).
    private readonly ByteBuffer _fields = new(64), _calcOrder = new(64), _formFonts = new(64);
    private readonly HashSet<string> _formFontNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fieldNames = new(StringComparer.Ordinal);
    private PdfString? _formDA;
    private bool _needAppearances;
    // Logical structure: the merged root, its kids, the merged parent tree and role map, and the document language.
    private int _structRootId, _parentTreeNext;
    private readonly ByteBuffer _structKids = new(64), _parentNums = new(64), _roleMap = new(64), _classMap = new(64);
    private readonly HashSet<string> _roleNames = new(StringComparer.Ordinal), _classNames = new(StringComparer.Ordinal);
    private PdfString? _lang;
    private bool _allPdfUa = true; // every input so far claims PDF/UA-1, so the result can too
    // Attachments: every input's embedded files, by name-tree key (a name used before gets " (2)", " (3)", ...).
    private readonly List<(byte[] Key, ByteBuffer Value)> _attachments = new();
    private readonly HashSet<string> _attachmentNames = new(StringComparer.Ordinal);

    /// <summary>A writer; <paramref name="sink"/> is where the result goes as it is made, or null to keep it in memory (see <see cref="ToArray"/>).</summary>
    public PdfMergeWriter(PdfMergeOptions options, CancellationToken cancel, Stream? sink = null)
    {
        _options = options;
        _cancel = cancel;
        _sink = sink;
        _out = _main;
        _nextId = InfoId + 1;
    }

    private int NewId() => _nextId++;

    /// <summary>The number of inputs added so far.</summary>
    public int InputCount => _inputs;

    /// <summary>The number of pages added so far.</summary>
    public int PageCount => _pageCount;

    /// <summary>Stops the next <see cref="Add"/> or <see cref="Finish"/>, for callers that pass a token per call.</summary>
    public CancellationToken Cancellation { set => _cancel = value; }

    /// <summary>Merges PDFs held in memory and returns the result.</summary>
    public byte[] Merge(List<byte[]> data)
    {
        foreach (var d in data) Add(d);
        Finish();
        return ToArray();
    }

    /// <summary>
    /// Copies one input's pages, and everything they use, to the output. The input is parsed, written and let go before
    /// the next one is read, so only one input is held in memory at a time.
    /// </summary>
    public void Add(byte[] data)
    {
        _cancel.ThrowIfCancellationRequested();
        int index = _inputs++;
        if (index == 0) _out.Ascii("%PDF-1.7\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();
        var input = new Input();
        string? password = _options.Passwords is { } pw && index < pw.Count ? pw[index] : null;
        int maxPages = Math.Max(1, _options.MaxPages);
        using (PdfFile.BeginRender(_cancel))
        {
            try
            {
                input.File = PdfFile.Open(data, password);
            }
            catch (PdfFormatException ex)
            {
                throw new InvalidDataException($"PDF {index + 1} is not readable: {ex.Message}", ex);
            }
            catch (PdfPasswordException ex)
            {
                throw new PdfPasswordException($"PDF {index + 1}: {ex.Message}");
            }
            var catalog = input.File.Catalog;
            if (input.File.Trailer["Root"] is PdfRef root) input.Map[root.Num] = 0;
            if (input.File.Trailer["Encrypt"] is PdfRef enc) input.Map[enc.Num] = 0;
            if (catalog?["Pages"] is { } pagesRef)
                CollectPages(input, pagesRef, null, null, null, null, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
            _pageCount += input.Pages.Count;
            if (_pageCount > maxPages)
                throw new PdfLimitException($"The merged document has more than {maxPages} pages (PdfMergeOptions.MaxPages).");
            if (_options.Bookmarks) CollectOutline(input, catalog);
            CollectFields(input, catalog, _fieldNames, ref _formDA);
            CollectStructure(input, catalog);
            if (index == 0) _props = Properties(input.File);
            _needAppearances |= input.NeedAppearances;

            var queue = new Queue<(int Num, int Id)>();
            foreach (var p in input.Pages)
            {
                WritePage(input, p, queue);
                _pageIds.Add(p.Id);
            }
            if (input.File.GetDict(input.File.Catalog, "OCProperties") is { } oc)
            {
                WriteItemsTo(_ocgs, input, input.File.GetArray(oc, "OCGs"), queue);
                WriteItemsTo(_ocgsOff, input, input.File.GetArray(input.File.GetDict(oc, "D"), "OFF"), queue);
            }
            if (input.Fields.Count > 0)
            {
                var form = input.File.GetDict(input.File.Catalog, "AcroForm")!;
                WriteItemsTo(_fields, input, new PdfArray(input.Fields.Select(n => (PdfObject)new PdfRef(n, 0)).ToList()), queue);
                WriteItemsTo(_calcOrder, input, input.File.GetArray(form, "CO"), queue);
                if (input.File.GetDict(input.File.GetDict(form, "DR"), "Font") is { } drFonts)
                {
                    _out = _formFonts;
                    try
                    {
                        foreach (var (key, value) in drFonts.Entries)
                        {
                            if (!_formFontNames.Add(key)) continue;
                            PdfSyntax.RawName(_out, key);
                            _out.Sp();
                            WriteObject(input, value, queue, 1);
                            _out.Sp();
                        }
                    }
                    finally
                    {
                        _out = _main;
                    }
                }
            }
            if (input.StructOffset >= 0) WriteStructure(input, queue);
            CopyAttachments(input, catalog, queue);
            // Top-level bookmarks are chained across inputs; the last one waits for its /Next, which a later input may give.
            if (input.TopOutline.Count > 0 && _outlinesId == 0) _outlinesId = NewId();
            for (int k = 0; k < input.TopOutline.Count; k++)
            {
                int num = input.TopOutline[k], id = input.Map[num];
                if (_pendingOutline is { } pending)
                {
                    WriteOutlineItem(pending.Id, pending.Prev, id, pending.Body);
                    _pendingOutline = null;
                }
                if (_firstOutline == 0) _firstOutline = id;
                var body = OutlineItemBody(input, num, queue);
                if (k + 1 < input.TopOutline.Count) WriteOutlineItem(id, _lastOutline, input.Map[input.TopOutline[k + 1]], body);
                else _pendingOutline = (id, _lastOutline, body);
                _lastOutline = id;
                _outlineCount++;
            }
            while (queue.Count > 0)
            {
                _cancel.ThrowIfCancellationRequested();
                var (num, id) = queue.Dequeue();
                var obj = input.File.GetObject(num) ?? PdfNull.Instance;
                Begin(id);
                if (obj is PdfStream s) WriteStream(input, s, queue);
                else if (obj is PdfDict od && input.Overrides.TryGetValue(num, out var changes))
                {
                    var entries = new Dictionary<string, PdfObject>(od.Entries, StringComparer.Ordinal);
                    foreach (var (key, value) in changes) entries[key] = value;
                    WriteDict(input, new PdfDict(entries), queue, 0);
                }
                else WriteObject(input, obj, queue, 0);
                End();
            }
        }
    }

    /// <summary>Writes the page tree, catalog, cross-reference table and trailer that close the document.</summary>
    public void Finish()
    {
        if (_pageCount == 0) throw new InvalidDataException("None of the PDFs has any pages.");
        if (_pendingOutline is { } pending)
        {
            WriteOutlineItem(pending.Id, pending.Prev, 0, pending.Body);
            _pendingOutline = null;
        }
        Begin(PagesId).Ascii("<< /Type /Pages /Count ").Int(_pageCount).Ascii(" /Kids [");
        foreach (var id in _pageIds) _out.Int(id).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();
        var now = DateTime.UtcNow;
        Begin(InfoId);
        _props!.WriteInfo(_out, now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)), modDate: true, PdfSyntax.TextString);
        End();
        if (_outlinesId != 0)
        {
            Begin(_outlinesId).Ascii("<< /Type /Outlines /First ").Int(_firstOutline).Ascii(" 0 R /Last ").Int(_lastOutline)
                .Ascii(" 0 R /Count ").Int(_outlineCount).Ascii(" >>");
            End();
        }

        if (_structRootId != 0)
        {
            int parentTreeId = NewId();
            Begin(_structRootId).Ascii("<< /Type /StructTreeRoot /K [").Bytes(_structKids.Span).Ascii("] /ParentTree ").Int(parentTreeId)
                .Ascii(" 0 R /ParentTreeNextKey ").Int(_parentTreeNext);
            if (_roleMap.Length > 0) _out.Ascii(" /RoleMap << ").Bytes(_roleMap.Span).Ascii(">>");
            if (_classMap.Length > 0) _out.Ascii(" /ClassMap << ").Bytes(_classMap.Span).Ascii(">>");
            _out.Ascii(" >>");
            End();
            Begin(parentTreeId).Ascii("<< /Nums [").Bytes(_parentNums.Span).Ascii("] >>");
            End();
        }

        // When every input is PDF/UA-1, so is the result: its metadata says so, with the merged title.
        int metadataId = 0;
        if (_allPdfUa && _structRootId != 0)
        {
            var xmp = PdfA.Xmp(0, "", _props!, now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)), pdfua: true);
            metadataId = NewId();
            Begin(metadataId).Ascii("<< /Type /Metadata /Subtype /XML /Length ").Int(xmp.Length).Ascii(" >>\nstream\n").Bytes(xmp).Ascii("\nendstream");
            End();
        }

        Begin(CatalogId).Ascii("<< /Type /Catalog /Pages ").Int(PagesId).Ascii(" 0 R");
        if (metadataId != 0) _out.Ascii(" /Metadata ").Int(metadataId).Ascii(" 0 R");
        if (_outlinesId != 0) _out.Ascii(" /Outlines ").Int(_outlinesId).Ascii(" 0 R");
        if (_structRootId != 0)
        {
            _out.Ascii(" /MarkInfo << /Marked true >> /StructTreeRoot ").Int(_structRootId).Ascii(" 0 R");
            if (!string.IsNullOrEmpty(_props!.Title)) _out.Ascii(" /ViewerPreferences << /DisplayDocTitle true >>");
        }
        if (_lang != null) { _out.Ascii(" /Lang "); PdfSyntax.HexString(_out, _lang.Bytes); }
        if (_attachments.Count > 0)
        {
            _attachments.Sort((x, y) => x.Key.AsSpan().SequenceCompareTo(y.Key));
            _out.Ascii(" /Names << /EmbeddedFiles << /Names [");
            foreach (var (key, value) in _attachments)
            {
                PdfSyntax.HexString(_out, key);
                _out.Sp().Bytes(value.Span).Sp();
            }
            _out.Ascii("] >> >>");
        }
        if (_ocgs.Length > 0)
        {
            _out.Ascii(" /OCProperties << /OCGs [").Bytes(_ocgs.Span).Ascii("] /D << /Order [").Bytes(_ocgs.Span).Ascii("]");
            if (_ocgsOff.Length > 0) _out.Ascii(" /OFF [").Bytes(_ocgsOff.Span).Ascii("]");
            _out.Ascii(" >> >>");
        }
        if (_fields.Length > 0)
        {
            _out.Ascii(" /AcroForm << /Fields [").Bytes(_fields.Span).Ascii("]");
            if (_calcOrder.Length > 0) _out.Ascii(" /CO [").Bytes(_calcOrder.Span).Ascii("]");
            if (_formFonts.Length > 0) _out.Ascii(" /DR << /Font << ").Bytes(_formFonts.Span).Ascii(">> >>");
            if (_formDA != null) { _out.Ascii(" /DA "); PdfSyntax.HexString(_out, _formDA.Bytes); }
            if (_needAppearances) _out.Ascii(" /NeedAppearances true");
            _out.Ascii(" >>");
        }
        _out.Ascii(" >>");
        End();

        // Cross-reference table and trailer.
        long xref = Position;
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
    }

    /// <summary>The whole result, when nothing has been flushed.</summary>
    public byte[] ToArray() => _main.Span.ToArray();

    /// <summary>Hands what has been written so far to <paramref name="output"/>.</summary>
    public void Flush(Stream output)
    {
        output.Write(_main.Span);
        _flushed += _main.Length;
        _main.Clear();
    }

    /// <inheritdoc cref="Flush"/>
    public async Task FlushAsync(Stream output, CancellationToken cancellationToken)
    {
        await output.WriteAsync(_main.RawBuffer.AsMemory(0, _main.Length), cancellationToken).ConfigureAwait(false);
        _flushed += _main.Length;
        _main.Clear();
    }

    private long Position => _flushed + _main.Length;

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

    /// <summary>
    /// Picks the input's top-level form fields to keep (all but signatures). A field named like one from an earlier input
    /// is renamed "name_2", "name_3", ..., since fields with the same name share one value; one that relied on the
    /// input's default appearance gets it written in, when the merged form's default differs.
    /// </summary>
    private static void CollectFields(Input input, PdfDict? catalog, HashSet<string> usedNames, ref PdfString? formDA)
    {
        var file = input.File;
        if (file.GetDict(catalog, "AcroForm") is not { } form || file.GetArray(form, "Fields") is not { } items) return;
        input.NeedAppearances = file.Resolve(form["NeedAppearances"]) is PdfBool { Value: true };
        var da = file.Resolve(form["DA"]) as PdfString;
        formDA ??= da;
        var own = new List<(int Num, PdfDict Field, string? Name)>();
        var seen = new HashSet<int>();
        foreach (var item in items.Items)
        {
            if (own.Count >= 100_000) break;
            if (item is not PdfRef r || !seen.Add(r.Num) || file.Resolve(item) is not PdfDict field) continue;
            if (file.GetName(field, "FT") == "Sig") continue;
            own.Add((r.Num, field, (file.Resolve(field["T"]) as PdfString)?.Text));
        }
        var ownNames = own.Where(f => f.Name != null).Select(f => f.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var (num, field, name) in own)
        {
            input.Fields.Add(num);
            Dictionary<string, PdfObject>? changes = null;
            if (name != null && usedNames.Contains(name))
            {
                string renamed;
                int k = 2;
                do renamed = name + "_" + k++; while (usedNames.Contains(renamed) || ownNames.Contains(renamed));
                ownNames.Add(renamed);
                (changes ??= new())["T"] = new PdfString(TextStringBytes(renamed));
            }
            if (da != null && formDA != null && !da.Bytes.AsSpan().SequenceEqual(formDA.Bytes) && field["DA"] == null)
                (changes ??= new())["DA"] = da;
            if (changes != null) input.Overrides[num] = changes;
        }
        usedNames.UnionWith(ownNames);
    }

    /// <summary>
    /// Takes in a tagged input's structure tree: its root becomes the merged root, and its parent-tree keys are moved past
    /// the earlier inputs'. Sets the document language from the first input that has one; a later input in another
    /// language gets it on its top-level structure elements.
    /// </summary>
    private void CollectStructure(Input input, PdfDict? catalog)
    {
        var file = input.File;
        _allPdfUa &= ClaimsPdfUa(file, catalog);
        var lang = file.Resolve(catalog?["Lang"]) as PdfString;
        bool otherLang = lang != null && _lang != null && !lang.Bytes.AsSpan().SequenceEqual(_lang.Bytes);
        _lang ??= lang;
        if (file.GetDict(catalog, "StructTreeRoot") is not { } root) return;
        if (_structRootId == 0) _structRootId = NewId();
        if (catalog!["StructTreeRoot"] is PdfRef rootRef) input.Map[rootRef.Num] = _structRootId;
        var nums = new SortedDictionary<int, PdfObject>();
        CollectNums(file, file.GetDict(root, "ParentTree"), nums, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
        int nextKey = Math.Max(file.GetInt(root, "ParentTreeNextKey", 0), nums.Count > 0 ? nums.Keys.Last() + 1 : 0);
        if ((long)_parentTreeNext + nextKey > int.MaxValue / 2) return; // keys out of any sane range: leave this input untagged
        input.StructOffset = _parentTreeNext;
        input.ParentNums = nums;
        _parentTreeNext += nextKey;
        if (otherLang)
            foreach (var k in KidsOf(file, root["K"]))
                if (k is PdfRef kr && file.Resolve(kr) is PdfDict kd && kd["Lang"] == null && !input.Overrides.ContainsKey(kr.Num))
                    input.Overrides[kr.Num] = new Dictionary<string, PdfObject> { ["Lang"] = lang! };
    }

    /// <summary>Whether the input's XMP metadata identifies it as PDF/UA-1.</summary>
    private static bool ClaimsPdfUa(PdfFile file, PdfDict? catalog)
    {
        if (file.GetDict(catalog, "StructTreeRoot") == null || file.Resolve(catalog!["Metadata"]) is not PdfStream meta) return false;
        try
        {
            var xmp = Encoding.UTF8.GetString(file.DecodeStream(meta));
            return xmp.Contains("http://www.aiim.org/pdfua/ns/id/", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(xmp, @"pdfuaid:part\s*(>\s*|=\s*[""'])1\b");
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    private static IEnumerable<PdfObject> KidsOf(PdfFile file, PdfObject? k)
        => file.Resolve(k) switch
        {
            PdfArray a => a.Items,
            PdfDict => new[] { k! },
            _ => Array.Empty<PdfObject>(),
        };

    private static void CollectNums(PdfFile file, PdfDict? node, SortedDictionary<int, PdfObject> into, HashSet<PdfDict> seen, int depth)
    {
        if (node == null || depth > 32 || !seen.Add(node) || into.Count > 1_000_000) return;
        if (file.GetArray(node, "Nums") is { } nums)
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (file.Resolve(nums[i]) is PdfNumber { IsInteger: true } n && n.Value >= 0 && n.Value < int.MaxValue / 2)
                    into.TryAdd((int)n.Value, nums[i + 1]);
        if (file.GetArray(node, "Kids") is { } kids)
            foreach (var kid in kids.Items)
                CollectNums(file, file.Resolve(kid) as PdfDict, into, seen, depth + 1);
    }

    private static byte[] TextStringBytes(string s)
    {
        foreach (var c in s)
            if (c > 0x7E || c < 0x20)
                return new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(s)).ToArray();
        return Encoding.ASCII.GetBytes(s);
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
        _offsets[id] = Position;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End()
    {
        _out.Ascii("\nendobj\n");
        if (_sink != null && _main.Length >= FlushSize) Flush(_sink);
    }

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
            if (WriteStructKey(input, key, value)) continue;
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

    /// <summary>Adds a tagged input's top-level structure elements, parent-tree entries and role and class maps to the merged ones.</summary>
    private void WriteStructure(Input input, Queue<(int, int)> queue)
    {
        var file = input.File;
        var root = file.GetDict(file.Catalog, "StructTreeRoot")!;
        var saved = _out;
        try
        {
            _out = _structKids;
            foreach (var k in KidsOf(file, root["K"]))
            {
                WriteObject(input, k, queue, 1);
                _out.Sp();
            }
            _out = _parentNums;
            foreach (var (key, value) in input.ParentNums!)
            {
                _out.Int(key + input.StructOffset).Sp();
                WriteObject(input, value, queue, 1);
                _out.Sp();
            }
            WriteMapTo(_roleMap, _roleNames, file.GetDict(root, "RoleMap"));
            WriteMapTo(_classMap, _classNames, file.GetDict(root, "ClassMap"));
        }
        finally
        {
            _out = saved;
        }

        // The first input's entry wins for a name two inputs map differently.
        void WriteMapTo(ByteBuffer target, HashSet<string> names, PdfDict? map)
        {
            if (map == null) return;
            _out = target;
            foreach (var (key, value) in map.Entries)
            {
                if (!names.Add(key)) continue;
                PdfSyntax.RawName(_out, key);
                _out.Sp();
                WriteObject(input, value, queue, 1);
                _out.Sp();
            }
        }
    }

    /// <summary>
    /// Writes a /StructParent or /StructParents entry with the input's key offset, or leaves it out for an untagged input;
    /// false for any other key.
    /// </summary>
    private bool WriteStructKey(Input input, string key, PdfObject value)
    {
        if (key is not ("StructParent" or "StructParents")) return false;
        if (input.StructOffset >= 0 && input.File.Resolve(value) is PdfNumber { IsInteger: true } n && n.Value >= 0 && n.Value < int.MaxValue / 2)
        {
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp().Int((long)n.Value + input.StructOffset);
        }
        return true;
    }

    /// <summary>Copies the input's embedded files (its attachments), renaming a name an earlier input used.</summary>
    private void CopyAttachments(Input input, PdfDict? catalog, Queue<(int, int)> queue)
    {
        foreach (var (key, value) in EmbeddedFiles.NameTree(input.File, catalog))
        {
            string name = key.Text;
            if (!_attachmentNames.Add(name))
            {
                string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
                int k = 2;
                while (!_attachmentNames.Add(name = $"{stem} ({k}){ext}")) k++;
            }
            var buffer = new ByteBuffer(32);
            var saved = _out;
            _out = buffer;
            try
            {
                WriteObject(input, value, queue, 1);
            }
            finally
            {
                _out = saved;
            }
            _attachments.Add((EmbeddedFiles.KeyBytes(name), buffer));
        }
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

    /// <summary>A bookmark's entries other than its links to its neighbours, which are written with <see cref="WriteOutlineItem"/>.</summary>
    private ByteBuffer OutlineItemBody(Input input, int num, Queue<(int, int)> queue)
    {
        var d = input.File.GetObject(num) as PdfDict ?? new PdfDict();
        var body = new ByteBuffer(256);
        _out = body;
        try
        {
            foreach (var (key, value) in d.Entries)
            {
                if (key is "Parent" or "Prev" or "Next") continue;
                _out.Sp();
                PdfSyntax.RawName(_out, key);
                _out.Sp();
                WriteObject(input, value, queue, 1, key);
            }
        }
        finally
        {
            _out = _main;
        }
        return body;
    }

    private void WriteOutlineItem(int id, int prev, int next, ByteBuffer body)
    {
        Begin(id).Ascii("<< /Parent ").Int(_outlinesId).Ascii(" 0 R");
        if (prev != 0) _out.Ascii(" /Prev ").Int(prev).Ascii(" 0 R");
        if (next != 0) _out.Ascii(" /Next ").Int(next).Ascii(" 0 R");
        _out.Bytes(body.Span).Ascii(" >>");
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
            if (WriteStructKey(input, key, value)) continue;
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
            if (WriteStructKey(input, key, value)) continue;
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
            string? hash = target is PdfStream ts ? LeafStreamHash(input.File, ts) : null;
            if (hash != null && _streams.TryGetValue(hash, out id))
            {
                input.Map[r.Num] = id;
            }
            else
            {
                id = NewId();
                input.Map[r.Num] = id;
                queue.Enqueue((r.Num, id));
                if (hash != null) _streams[hash] = id;
            }
        }
        if (id == 0) _out.Ascii("null");
        else _out.Int(id).Ascii(" 0 R");
    }

    /// <summary>
    /// A hash of a stream that refers to no other object, as it will be written, so identical copies are written once;
    /// null for a stream with references or too small to be worth it.
    /// </summary>
    private string? LeafStreamHash(PdfFile file, PdfStream s)
    {
        // A key into the structure tree is shifted per input, so such a stream is never shared.
        if (s.RawData.Length < 256 || s.Entries.Values.Any(HasRef) || s.Entries.ContainsKey("StructParents")) return null;
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("Length");
        RemoveCryptFilter(file, entries);
        var dict = new ByteBuffer(256);
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            PdfSyntax.RawName(dict, key);
            dict.Sp();
            WriteLeaf(dict, value);
            dict.Sp();
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(dict.Span);
        sha.AppendData(s.RawData);
        return Convert.ToBase64String(sha.GetHashAndReset());

        static bool HasRef(PdfObject o) => o switch
        {
            PdfRef => true,
            PdfArray a => a.Items.Any(HasRef),
            PdfDict d => d.Entries.Values.Any(HasRef),
            _ => false,
        };
    }

    /// <summary>Writes an object without references (see <see cref="LeafStreamHash"/>).</summary>
    private static void WriteLeaf(ByteBuffer b, PdfObject o)
    {
        switch (o)
        {
            case PdfBool x: b.Ascii(x.Value ? "true" : "false"); break;
            case PdfNumber n: b.Ascii(n.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
            case PdfString x: PdfSyntax.HexString(b, x.Bytes); break;
            case PdfName x: PdfSyntax.RawName(b, x.Value); break;
            case PdfArray a:
                b.Byte((byte)'[');
                foreach (var i in a.Items) { WriteLeaf(b, i); b.Sp(); }
                b.Byte((byte)']');
                break;
            case PdfDict d:
                b.Ascii("<<");
                foreach (var (k, v) in d.Entries.OrderBy(e => e.Key, StringComparer.Ordinal)) { PdfSyntax.RawName(b, k); b.Sp(); WriteLeaf(b, v); b.Sp(); }
                b.Ascii(">>");
                break;
            default: b.Ascii("null"); break;
        }
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

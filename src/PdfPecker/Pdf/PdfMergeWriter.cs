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
/// <remarks>
/// An input can be given a <see cref="PageSpec"/>: which pages to take, in which order, and which to turn. Pages left out
/// map to null, and what only they use is not copied: their annotations, links to them, bookmarks to them (unless an
/// included bookmark is below one, which then stays without a destination), form fields with widgets only on them, and
/// structure elements with content only on them, along with their parent-tree entries. A page taken again is written as
/// a copy without structure keys, form widgets or popups; its other annotations are copied too.
/// </remarks>
internal sealed class PdfMergeWriter : PdfObjectWriter
{
    /// <summary>Which pages of an input to take, in order, and which of them to turn.</summary>
    internal sealed class PageSpec
    {
        public PdfPageRange? Pages; // null = every page
        public bool Invert; // Pages are the ones to leave out
        public int Rotate; // degrees clockwise, a multiple of 90, added to the pages' rotation
        public PdfPageRange? RotatePages; // the pages Rotate applies to; null = all
    }

    private readonly record struct SourcePage(PdfDict Page, int Num, PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);

    private readonly record struct OutPage(int Src, int Id, bool Repeat, int Turn);

    private sealed class Input
    {
        public PdfFile File = null!;
        public readonly List<SourcePage> Source = new(); // every page of the input
        public readonly List<OutPage> Out = new(); // the pages written, in order
        public readonly HashSet<int> PageNums = new(); // object numbers of the input's pages
        public bool Partial, Repeats; // some pages are left out; some are taken more than once
        public HashSet<int>? KeptAnnots; // with Partial: annotations on included pages that are kept
        public readonly HashSet<int> DroppedKeys = new(); // with Partial: parent-tree keys of content left out
        public List<PdfObject>? StructKids; // with Partial: the structure root's kids that are kept
        public readonly Dictionary<int, Dictionary<string, PdfObject>> IdOverrides = new(); // by new id, for copies of annotations
        public readonly Dictionary<int, int> Map = new(); // source object number -> new id; 0 = write null
        public readonly List<int> TopOutline = new(); // source numbers of the top-level bookmarks
        public Dictionary<string, PdfObject>? NamedDests;
        public readonly List<int> Fields = new(); // source numbers of the top-level form fields kept
        public readonly Dictionary<int, Dictionary<string, PdfObject>> Overrides = new(); // entries to replace when copying an object; null removes
        public bool NeedAppearances;
        public int StructOffset = -1; // added to the input's parent-tree keys; -1 = untagged, its keys are dropped
        public SortedDictionary<int, PdfObject>? ParentNums;
    }

    private readonly PdfMergeOptions _options;
    private readonly DateTime? _fixedDate; // the date of a reproducible result, or null for the current time
    // With a fixed date, a running hash of every byte written, flushed or not, from which the file identifier is made
    // without holding a streamed result in memory.
    private IncrementalHash? _hash;
    private CancellationToken _cancel;
    private readonly Stream? _sink; // written to as the buffer fills, when merging to a stream synchronously
    private readonly ByteBuffer _main = new(1 << 16);
    private ByteBuffer _out;
    private long _flushed; // bytes already handed out of _main
    private readonly PdfXref _xref = new();
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
    // Page labels of every page written; written when any input has them.
    private readonly List<(string? Style, PdfString? Prefix, int Value)> _labels = new();
    private bool _anyLabels;
    private readonly bool _single; // one input: messages say "the PDF" rather than "PDF 1"

    /// <summary>The most pages an input given a <see cref="PageSpec"/> may have.</summary>
    private const int MaxSourcePages = 1_000_000;
    // Attachments: every input's embedded files, by name-tree key (a name used before gets " (2)", " (3)", ...).
    private readonly List<(byte[] Key, ByteBuffer Value)> _attachments = new();
    private readonly HashSet<string> _attachmentNames = new(StringComparer.Ordinal);
    // The input being copied, and its objects still to copy, for the hooks of the shared object writer.
    private Input _input = null!;
    private Queue<(int Num, int Id)> _queue = null!;

    /// <summary>A writer; <paramref name="sink"/> is where the result goes as it is made, or null to keep it in memory (see <see cref="ToArray"/>).</summary>
    public PdfMergeWriter(PdfMergeOptions options, CancellationToken cancel, Stream? sink = null, bool single = false)
        : base(PdfObjectSyntax.Rewrite)
    {
        _options = options;
        _cancel = cancel;
        _sink = sink;
        _single = single;
        _out = _main;
        _nextId = InfoId + 1;
        _fixedDate = ReproducibleDate.Resolve(options.CreationDate);
        if (_fixedDate != null) _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    private int NewId() => _nextId++;

    /// <summary>The number of inputs added so far.</summary>
    public int InputCount => _inputs;

    /// <summary>The number of pages added so far.</summary>
    public int PageCount => _pageCount;

    /// <summary>Stops the next <see cref="Add(byte[], PageSpec)"/> or <see cref="Finish"/>, for callers that pass a token per call.</summary>
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
    public void Add(byte[] data, PageSpec? spec = null)
    {
        _cancel.ThrowIfCancellationRequested();
        int index = _inputs++;
        string? password = _options.Passwords is { } pw && index < pw.Count ? pw[index] : null;
        using (PdfFile.BeginRender(_cancel))
            AddFile(Open(data, password, _single ? null : $"PDF {index + 1}"), index, spec);
    }

    /// <summary>Copies pages of a PDF that is already open (to split one PDF into several without parsing it again).</summary>
    public void Add(PdfFile file, PageSpec? spec)
    {
        _cancel.ThrowIfCancellationRequested();
        int index = _inputs++;
        using (PdfFile.BeginRender(_cancel))
            AddFile(file, index, spec);
    }

    /// <summary>Parses a PDF, with messages naming it as <paramref name="label"/> (null: "The PDF").</summary>
    public static PdfFile Open(byte[] data, string? password, string? label)
    {
        try
        {
            return PdfFile.Open(data, password);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException($"{label ?? "The PDF"} is not readable: {ex.Message}", ex);
        }
        catch (PdfPasswordException ex) when (label != null)
        {
            throw new PdfPasswordException($"{label}: {ex.Message}");
        }
    }

    private void AddFile(PdfFile file, int index, PageSpec? spec)
    {
        if (index == 0) _out.Ascii("%PDF-1.7\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();
        var input = new Input { File = file };
        int maxPages = Math.Max(1, _options.MaxPages);
        {
            var catalog = input.File.Catalog;
            if (input.File.Trailer["Root"] is PdfRef root) input.Map[root.Num] = 0;
            if (input.File.Trailer["Encrypt"] is PdfRef enc) input.Map[enc.Num] = 0;
            bool selecting = spec?.Pages != null;
            if (catalog?["Pages"] is { } pagesRef)
                CollectPages(input, pagesRef, null, null, null, null, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0,
                    selecting ? Math.Max(maxPages, MaxSourcePages) : maxPages, selecting ? $"{Label(index)} has" : "The merged document has");
            SelectPages(input, spec, index, maxPages);
            _pageCount += input.Out.Count;
            if (_pageCount > maxPages)
                throw new PdfLimitException($"The merged document has more than {maxPages} pages (PdfMergeOptions.MaxPages).");
            if (input.Partial) FilterAnnotations(input);
            if (_options.Bookmarks) CollectOutline(input, catalog);
            CollectFields(input, catalog, _fieldNames, ref _formDA);
            CollectStructure(input, catalog);
            CollectLabels(input, catalog);
            if (index == 0) _props = Properties(input.File);
            _needAppearances |= input.NeedAppearances;

            var queue = new Queue<(int Num, int Id)>();
            _input = input;
            _queue = queue;
            foreach (var p in input.Out)
            {
                WritePage(input, p, queue);
                _pageIds.Add(p.Id);
            }
            if (input.File.GetDict(input.File.Catalog, "OCProperties") is { } oc)
            {
                WriteItemsTo(_ocgs, input, input.File.GetArray(oc, "OCGs"));
                WriteItemsTo(_ocgsOff, input, input.File.GetArray(input.File.GetDict(oc, "D"), "OFF"));
            }
            if (input.Fields.Count > 0)
            {
                var form = input.File.GetDict(input.File.Catalog, "AcroForm")!;
                WriteItemsTo(_fields, input, new PdfArray(input.Fields.Select(n => (PdfObject)new PdfRef(n, 0)).ToList()));
                WriteItemsTo(_calcOrder, input, input.File.GetArray(form, "CO"));
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
                            WriteValue(_out, value, 1);
                            _out.Sp();
                        }
                    }
                    finally
                    {
                        _out = _main;
                    }
                }
            }
            if (input.StructOffset >= 0) WriteStructure(input);
            CopyAttachments(input, catalog);
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
                var body = OutlineItemBody(input, num);
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
                if (obj is PdfStream s) WriteStream(_out, input.File, s);
                else if (obj is PdfDict od && (input.Overrides.ContainsKey(num) || input.IdOverrides.ContainsKey(id)))
                    WriteDict(_out, Overridden(input, od, num, id), 0);
                else WriteObject(_out, obj);
                End();
            }
            _input = null!;
            _queue = null!;
        }
    }

    private string Label(int index) => _single ? "The PDF" : $"PDF {index + 1}";

    /// <summary>A dictionary with the entries <see cref="Input.Overrides"/> (by source number) and <see cref="Input.IdOverrides"/> (by new id) replace or remove.</summary>
    private static PdfDict Overridden(Input input, PdfDict d, int num, int id)
    {
        var entries = new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal);
        if (num != 0 && input.Overrides.TryGetValue(num, out var changes)) Apply(changes);
        if (id != 0 && input.IdOverrides.TryGetValue(id, out var copyChanges)) Apply(copyChanges);
        return new PdfDict(entries);

        void Apply(Dictionary<string, PdfObject> c)
        {
            foreach (var (key, value) in c)
            {
                if (value.IsNull) entries.Remove(key);
                else entries[key] = value;
            }
        }
    }

    /// <summary>Adds an entry to replace (or, with null, remove) when the input's object <paramref name="num"/> is copied.</summary>
    private static void SetOverride(Input input, int num, string key, PdfObject value)
    {
        if (!input.Overrides.TryGetValue(num, out var changes)) input.Overrides[num] = changes = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        changes[key] = value;
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
        var now = _fixedDate ?? ReproducibleDate.TruncateToSeconds(DateTime.UtcNow);
        Begin(InfoId);
        _props!.WriteInfo(_out, now, modDate: true, PdfSyntax.TextString);
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
            var xmp = PdfA.Xmp(0, null, _props!, now, uaPart: 1);
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
        if (_anyLabels) WritePageLabels();
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
        _xref.WriteTable(_out, count);
        byte[] id0;
        if (_hash != null)
        {
            // The same identifier ReproducibleDate.FileId gives for the whole file before its trailer.
            _hash.AppendData(_main.Span);
            id0 = _hash.GetHashAndReset()[..16];
            _hash.Dispose();
            _hash = null;
        }
        else id0 = RandomNumberGenerator.GetBytes(16);
        PdfXref.WriteTrailer(_out, count, CatalogId, InfoId, 0, id0, id0, upperHexId: false, xref);
    }

    /// <summary>The whole result, when nothing has been flushed.</summary>
    public byte[] ToArray() => _main.Span.ToArray();

    /// <summary>Hands what has been written so far to <paramref name="output"/>.</summary>
    public void Flush(Stream output)
    {
        _hash?.AppendData(_main.Span);
        output.Write(_main.Span);
        _flushed += _main.Length;
        _main.Clear();
    }

    /// <inheritdoc cref="Flush"/>
    public async Task FlushAsync(Stream output, CancellationToken cancellationToken)
    {
        _hash?.AppendData(_main.Span);
        await output.WriteAsync(_main.RawBuffer.AsMemory(0, _main.Length), cancellationToken).ConfigureAwait(false);
        _flushed += _main.Length;
        _main.Clear();
    }

    private long Position => _flushed + _main.Length;

    // ------------------------------------------------------------------ reading

    private void CollectPages(Input input, PdfObject node, PdfObject? res, PdfObject? media, PdfObject? crop, PdfObject? rotate,
        HashSet<PdfDict> seen, int depth, int limit, string tooMany)
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
            foreach (var k in kids.Items) CollectPages(input, k, res, media, crop, rotate, seen, depth + 1, limit, tooMany);
            return;
        }
        if (input.Source.Count + 1 > limit)
            throw new PdfLimitException(limit == Math.Max(1, _options.MaxPages)
                ? $"{tooMany} more than {limit} pages (PdfMergeOptions.MaxPages)."
                : $"{tooMany} more than {limit} pages.");
        int num = node is PdfRef pr ? pr.Num : 0;
        if (num != 0) input.PageNums.Add(num);
        input.Source.Add(new SourcePage(dict, num, res, media, crop, rotate));
    }

    /// <summary>The number of pages of a parsed PDF, found as <see cref="CollectPages"/> finds them.</summary>
    public static int CountPages(PdfFile file)
    {
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        int count = 0;
        Walk(file.Catalog?["Pages"], 0);
        return count;

        void Walk(PdfObject? node, int depth)
        {
            if (depth > 64 || file.Resolve(node) is not PdfDict dict || !seen.Add(dict) || count > MaxSourcePages) return;
            if (file.GetArray(dict, "Kids") is { } kids && file.GetName(dict, "Type") != "Page")
                foreach (var k in kids.Items) Walk(k, depth + 1);
            else count++;
        }
    }

    /// <summary>
    /// Decides which pages are written, in which order, and gives each its new id. A page's references lead to its first
    /// copy; those of a page left out are written as null.
    /// </summary>
    private void SelectPages(Input input, PageSpec? spec, int index, int maxPages)
    {
        int count = input.Source.Count;
        string what = _single ? "the PDF" : $"PDF {index + 1}";
        List<int> order;
        if (spec?.Pages == null) order = Enumerable.Range(0, count).ToList();
        else if (spec.Invert)
        {
            var drop = spec.Pages.Mark(count, what);
            order = Enumerable.Range(0, count).Where(i => !drop[i]).ToList();
            if (order.Count == 0) throw new ArgumentException($"Removing these pages would leave none: {what} has {count} page{(count == 1 ? "" : "s")}.", "pages");
        }
        else order = spec.Pages.Resolve(count, what, maxPages - _pageCount);
        if (_pageCount + order.Count > maxPages)
            throw new PdfLimitException($"The merged document has more than {maxPages} pages (PdfMergeOptions.MaxPages).");
        bool[]? turned = spec is { Rotate: not 0 } ? spec.RotatePages?.Mark(count, what) ?? Enumerable.Repeat(true, count).ToArray() : null;
        var used = new bool[count];
        foreach (int src in order)
        {
            bool repeat = used[src];
            used[src] = true;
            input.Repeats |= repeat;
            int id = NewId();
            var page = input.Source[src];
            if (!repeat && page.Num != 0) input.Map[page.Num] = id;
            input.Out.Add(new OutPage(src, id, repeat, turned != null && turned[src] ? spec!.Rotate : 0));
        }
        for (int i = 0; i < count; i++)
        {
            if (used[i]) continue;
            input.Partial = true;
            if (input.Source[i].Num != 0) input.Map[input.Source[i].Num] = 0;
        }
    }

    /// <summary>Whether <paramref name="o"/> is (a reference to) a page of the input that is left out.</summary>
    private static bool IsLeftOut(Input input, PdfObject? o)
        => o is PdfRef r && input.PageNums.Contains(r.Num) && IsDropped(input, r.Num);

    /// <summary>Whether the input's object <paramref name="num"/> is left out of the result.</summary>
    private static bool IsDropped(Input input, int num) => input.Map.TryGetValue(num, out int id) && id == 0;

    /// <summary>The page a bookmark or link goes to (the first item of its destination), or null.</summary>
    private PdfObject? DestPage(Input input, PdfDict d)
    {
        var file = input.File;
        var dest = d["Dest"];
        if (dest == null && file.GetDict(d, "A") is { } a && file.GetName(a, "S") == "GoTo") dest = a["D"];
        dest = file.Resolve(dest);
        if (dest is PdfString or PdfName) dest = NamedDest(input, dest);
        if (dest is PdfDict dd) dest = file.Resolve(dd["D"]);
        return dest is PdfArray { Count: > 0 } arr ? arr[0] : null;
    }

    /// <summary>
    /// With pages left out: drops their annotations, and links on the pages kept that go to them, and notes the
    /// structure keys of what is dropped so their parent-tree entries go too.
    /// </summary>
    private void FilterAnnotations(Input input)
    {
        var file = input.File;
        var kept = input.KeptAnnots = new HashSet<int>();
        var dropped = new HashSet<int>();
        var included = new bool[input.Source.Count];
        foreach (var o in input.Out) included[o.Src] = true;
        for (int i = 0; i < input.Source.Count; i++)
        {
            var page = input.Source[i].Page;
            if (!included[i] && file.Resolve(page["StructParents"]) is PdfNumber { IsInteger: true } pk) input.DroppedKeys.Add(pk.IntValue);
            if (file.GetArray(page, "Annots") is not { } annots) continue;
            foreach (var a in annots.Items)
            {
                if (a is not PdfRef r || file.Resolve(r) is not PdfDict ad) continue;
                bool drop = !included[i] || (file.GetName(ad, "Subtype") == "Link" && IsLeftOut(input, DestPage(input, ad)));
                if (!drop) { kept.Add(r.Num); continue; }
                dropped.Add(r.Num);
                if (file.Resolve(ad["StructParent"]) is PdfNumber { IsInteger: true } k) input.DroppedKeys.Add(k.IntValue);
            }
        }
        foreach (var num in dropped)
            if (!kept.Contains(num)) input.Map[num] = 0;
    }

    private void CollectOutline(Input input, PdfDict? catalog)
    {
        var file = input.File;
        var rootRef = catalog?["Outlines"];
        if (file.Resolve(rootRef) is not PdfDict root) return;
        var seen = new HashSet<int>();
        if (input.Partial)
        {
            input.TopOutline.AddRange(FilterOutlineLevel(input, root["First"], seen, 0, out _));
            foreach (var num in input.TopOutline)
                if (!input.Map.ContainsKey(num)) input.Map[num] = NewId();
        }
        else
        {
            var item = root["First"];
            while (item is PdfRef r && seen.Add(r.Num) && seen.Count <= 100_000)
            {
                if (file.GetObject(r.Num) is not PdfDict d) break;
                if (!input.Map.ContainsKey(r.Num)) input.Map[r.Num] = NewId();
                input.TopOutline.Add(r.Num);
                item = d["Next"];
            }
        }
        // The old root is replaced by the merged one: its children's /Parent is rewritten when they are written.
        if (rootRef is PdfRef rr && !input.Map.ContainsKey(rr.Num)) input.Map[rr.Num] = 0;
    }

    /// <summary>
    /// The bookmarks of one level that are kept when pages are left out: those that go to a page kept or nowhere, and those
    /// with a kept bookmark below them (which lose their destination when it is a page left out). The kept ones are
    /// linked to each other, and <paramref name="visible"/> is how many lines they show, counting open children.
    /// </summary>
    private List<int> FilterOutlineLevel(Input input, PdfObject? first, HashSet<int> seen, int depth, out int visible)
    {
        var file = input.File;
        var kept = new List<int>();
        visible = 0;
        var item = first;
        while (item is PdfRef r && seen.Add(r.Num) && seen.Count <= 100_000)
        {
            if (file.GetObject(r.Num) is not PdfDict d) break;
            item = d["Next"];
            int below = 0;
            var kids = depth < 64 ? FilterOutlineLevel(input, d["First"], seen, depth + 1, out below) : new List<int>();
            bool leftOut = IsLeftOut(input, DestPage(input, d));
            if (leftOut && kids.Count == 0)
            {
                input.Map[r.Num] = 0;
                continue;
            }
            kept.Add(r.Num);
            if (leftOut)
            {
                SetOverride(input, r.Num, "Dest", PdfNull.Instance);
                SetOverride(input, r.Num, "A", PdfNull.Instance);
            }
            bool open = file.GetInt(d, "Count", 0) > 0;
            if (kids.Count == 0)
            {
                foreach (var key in new[] { "First", "Last", "Count" }) SetOverride(input, r.Num, key, PdfNull.Instance);
            }
            else
            {
                SetOverride(input, r.Num, "First", new PdfRef(kids[0], 0));
                SetOverride(input, r.Num, "Last", new PdfRef(kids[^1], 0));
                SetOverride(input, r.Num, "Count", PdfNumber.Int(open ? below : -below));
            }
            visible += 1 + (open ? below : 0);
        }
        for (int i = 0; i < kept.Count; i++)
        {
            SetOverride(input, kept[i], "Prev", i > 0 ? new PdfRef(kept[i - 1], 0) : PdfNull.Instance);
            SetOverride(input, kept[i], "Next", i + 1 < kept.Count ? new PdfRef(kept[i + 1], 0) : PdfNull.Instance);
        }
        return kept;
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
            if (input.Partial && FieldStatus(input, r.Num, field, 0, seen) < 0)
            {
                input.Map[r.Num] = 0;
                continue;
            }
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
            if (changes != null)
                foreach (var (key, value) in changes) SetOverride(input, num, key, value);
        }
        usedNames.UnionWith(ownNames);
    }

    /// <summary>
    /// With pages left out: 1 when a form field (or widget) has a widget on a page kept, -1 when all its widgets are on
    /// pages left out, 0 when it has none. Kids that are left out are dropped from the field's /Kids.
    /// </summary>
    private static int FieldStatus(Input input, int num, PdfDict field, int depth, HashSet<int> seen)
    {
        var file = input.File;
        bool anyKept = false, anyDropped = false;
        if (file.GetName(field, "Subtype") == "Widget")
        {
            if (input.KeptAnnots!.Contains(num)) anyKept = true;
            else if (IsDropped(input, num) || IsLeftOut(input, field["P"])) anyDropped = true;
            else if (field["P"] is PdfRef p && input.PageNums.Contains(p.Num)) anyKept = true;
        }
        if (depth < 32 && file.GetArray(field, "Kids") is { } kids)
        {
            var keptKids = new List<PdfObject>();
            bool changed = false;
            foreach (var k in kids.Items)
            {
                if (k is PdfRef kr && seen.Add(kr.Num) && file.Resolve(kr) is PdfDict kd)
                {
                    int s = FieldStatus(input, kr.Num, kd, depth + 1, seen);
                    if (s < 0)
                    {
                        input.Map[kr.Num] = 0;
                        anyDropped = changed = true;
                        continue;
                    }
                    anyKept |= s > 0;
                }
                keptKids.Add(k);
            }
            if (changed && anyKept) SetOverride(input, num, "Kids", new PdfArray(keptKids));
        }
        return anyKept ? 1 : anyDropped ? -1 : 0;
    }

    /// <summary>
    /// Takes in a tagged input's structure tree: its root becomes the merged root, and its parent-tree keys are moved past
    /// the earlier inputs'. Sets the document language from the first input that has one; a later input in another
    /// language gets it on its top-level structure elements.
    /// </summary>
    private void CollectStructure(Input input, PdfDict? catalog)
    {
        var file = input.File;
        // A page taken twice is copied without its tags, so the copy is no longer accessible.
        _allPdfUa &= ClaimsPdfUa(file, catalog) && !input.Repeats;
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
                    SetOverride(input, kr.Num, "Lang", lang!);
        if (input.Partial) FilterStructure(input, root);
    }

    /// <summary>
    /// With pages left out: keeps the structure elements with content on a page kept, or with no content at all, and
    /// drops the rest; kept elements lose their kids on pages left out, and their /Pg when it is one.
    /// </summary>
    private static void FilterStructure(Input input, PdfDict root)
    {
        var kids = new List<PdfObject>();
        var seen = new HashSet<int>();
        int budget = 1_000_000;
        foreach (var k in KidsOf(input.File, root["K"]))
        {
            var (status, kid) = FilterStructKid(input, k, 0, seen, 0, ref budget);
            if (status >= 0) kids.Add(kid);
        }
        input.StructKids = kids;
    }

    /// <summary>
    /// 1 when a structure element's kid has content on a page kept, -1 when only on pages left out, 0 when it has none; and
    /// the kid to write (a direct element changed in place is a new dictionary).
    /// </summary>
    private static (int Status, PdfObject Kid) FilterStructKid(Input input, PdfObject kid, int page, HashSet<int> seen, int depth, ref int budget)
    {
        var file = input.File;
        switch (file.Resolve(kid))
        {
            case PdfNumber:
                return (OnPage(page), kid);
            case PdfDict d when file.GetName(d, "Type") == "MCR":
                return (OnPage(d["Pg"] is PdfRef mp ? mp.Num : page), kid);
            case PdfDict d when file.GetName(d, "Type") == "OBJR":
                if (d["Obj"] is PdfRef obj && IsDropped(input, obj.Num)) return (-1, kid);
                return (OnPage(d["Pg"] is PdfRef op ? op.Num : page), kid);
            case PdfDict d:
                if (kid is PdfRef r && !seen.Add(r.Num)) return (1, kid); // shared or a cycle: left as it is
                if (depth > 256 || --budget < 0) return (1, kid);
                int own = d["Pg"] is PdfRef pg ? pg.Num : page;
                bool anyKept = false, anyDropped = false, changed = false;
                var kept = new List<PdfObject>();
                var k = d["K"];
                var items = file.Resolve(k) is PdfArray ka ? ka.Items : k != null ? new List<PdfObject> { k } : new List<PdfObject>();
                foreach (var item in items)
                {
                    var (s, written) = FilterStructKid(input, item, own, seen, depth + 1, ref budget);
                    if (s < 0)
                    {
                        anyDropped = changed = true;
                        continue;
                    }
                    anyKept |= s > 0;
                    changed |= !ReferenceEquals(written, item);
                    kept.Add(written);
                }
                int status = anyKept ? 1 : anyDropped ? -1 : 0;
                if (status < 0)
                {
                    if (kid is PdfRef dr) input.Map[dr.Num] = 0;
                    return (status, kid);
                }
                var changes = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                if (changed) changes["K"] = new PdfArray(kept);
                if (IsLeftOut(input, d["Pg"])) changes["Pg"] = PdfNull.Instance;
                if (changes.Count == 0) return (status, kid);
                if (kid is PdfRef er)
                {
                    foreach (var (key, value) in changes) SetOverride(input, er.Num, key, value);
                    return (status, kid);
                }
                var entries = new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal);
                foreach (var (key, value) in changes)
                {
                    if (value.IsNull) entries.Remove(key);
                    else entries[key] = value;
                }
                return (status, new PdfDict(entries));
            default:
                return (0, kid);
        }

        int OnPage(int num) => num != 0 && input.PageNums.Contains(num) && IsDropped(input, num) ? -1 : 1;
    }

    /// <summary>
    /// Notes the label of every page written (such as "iii" or "A-2") from the input's /PageLabels; a page of an input
    /// without them is labelled with its number in that input.
    /// </summary>
    private void CollectLabels(Input input, PdfDict? catalog)
    {
        var file = input.File;
        SortedDictionary<int, PdfObject>? ranges = null;
        if (file.GetDict(catalog, "PageLabels") is { } tree)
        {
            ranges = new SortedDictionary<int, PdfObject>();
            CollectNums(file, tree, ranges, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
            if (ranges.Count == 0) ranges = null;
        }
        _anyLabels |= ranges != null;
        var starts = ranges?.Keys.ToArray();
        foreach (var o in input.Out)
        {
            int at = starts == null ? -1 : Array.BinarySearch(starts, o.Src);
            if (at < 0 && starts != null) at = ~at - 1;
            if (at < 0)
            {
                _labels.Add(("D", null, o.Src + 1));
                continue;
            }
            var range = file.Resolve(ranges![starts![at]]) as PdfDict;
            string? style = file.GetName(range, "S") is "D" or "R" or "r" or "A" or "a" ? file.GetName(range, "S") : null;
            long first = Math.Max(1, file.GetInt(range, "St", 1));
            _labels.Add((style, file.Resolve(range?["P"]) as PdfString, (int)Math.Min(int.MaxValue, first + o.Src - starts[at])));
        }
    }

    /// <summary>Writes the page labels as ranges: a new one starts where a page's label does not follow on from the one before.</summary>
    private void WritePageLabels()
    {
        _out.Ascii(" /PageLabels << /Nums [");
        for (int i = 0; i < _labels.Count; i++)
        {
            var (style, prefix, value) = _labels[i];
            if (i > 0)
            {
                var (s0, p0, v0) = _labels[i - 1];
                bool samePrefix = prefix == null ? p0 == null : p0 != null && prefix.Bytes.AsSpan().SequenceEqual(p0.Bytes);
                if (s0 == style && samePrefix && (style == null || value == v0 + 1)) continue;
            }
            _out.Int(i).Ascii(" <<");
            if (style != null) { _out.Ascii(" /S "); PdfSyntax.RawName(_out, style); }
            if (prefix != null) { _out.Ascii(" /P "); PdfSyntax.HexString(_out, prefix.Bytes); }
            if (style != null && value != 1) _out.Ascii(" /St ").Int(value);
            _out.Ascii(" >> ");
        }
        _out.Ascii("] >>");
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

    private ByteBuffer Begin(int id) => _xref.Begin(_out, id, Position);

    private void End()
    {
        PdfXref.End(_out);
        if (_sink != null && _main.Length >= FlushSize) Flush(_sink);
    }

    private void WritePage(Input input, OutPage o, Queue<(int, int)> queue)
    {
        _cancel.ThrowIfCancellationRequested();
        var file = input.File;
        var p = input.Source[o.Src];
        Begin(o.Id).Ascii("<< /Type /Page /Parent ").Int(PagesId).Ascii(" 0 R");
        foreach (var (key, value) in p.Page.Entries)
        {
            // Inherited attributes are written below; /B (article beads) leads to the whole article thread.
            if (key is "Type" or "Parent" or "B" or "Annots" or "Resources" or "MediaBox" or "CropBox" or "Rotate") continue;
            // A second copy of a page is not in the structure tree.
            if (o.Repeat && key is "StructParents" or "StructParent") continue;
            if (WriteStructKey(_out, input, key, value)) continue;
            _out.Sp();
            PdfSyntax.RawName(_out, key);
            _out.Sp();
            WriteValue(_out, value, 0, key);
        }
        _out.Ascii(" /Resources ");
        WriteValue(_out, p.Resources ?? new PdfDict(), 0);
        _out.Ascii(" /MediaBox ");
        if (p.MediaBox != null) WriteValue(_out, p.MediaBox, 0);
        else _out.Ascii("[0 0 612 792]");
        if (p.CropBox != null) { _out.Ascii(" /CropBox "); WriteValue(_out, p.CropBox, 0); }
        if (o.Turn != 0)
        {
            int turned = ((file.GetIntValue(p.Rotate) / 90 * 90 + o.Turn) % 360 + 360) % 360;
            _out.Ascii(" /Rotate ").Int(turned);
        }
        else if (p.Rotate != null) { _out.Ascii(" /Rotate "); WriteValue(_out, p.Rotate, 0); }
        if (file.GetArray(p.Page, "Annots") is { } annots)
        {
            _out.Ascii(" /Annots [");
            foreach (var a in annots.Items)
            {
                if (file.Resolve(a) is not PdfDict ad || IsSignatureField(file, ad)) continue;
                if (a is PdfRef dropped && IsDropped(input, dropped.Num)) continue;
                if (input.Partial && a is not PdfRef && file.GetName(ad, "Subtype") == "Link" && IsLeftOut(input, DestPage(input, ad))) continue;
                if (o.Repeat)
                {
                    // A copy has annotations of its own; a form field's widget and a popup belong to one page.
                    if (file.GetName(ad, "Subtype") is "Widget" or "Popup") continue;
                    var copy = new Dictionary<string, PdfObject>(StringComparer.Ordinal)
                    {
                        ["P"] = PdfNull.Instance, ["StructParent"] = PdfNull.Instance, ["Popup"] = PdfNull.Instance,
                    };
                    if (a is PdfRef ar)
                    {
                        int id = NewId();
                        input.IdOverrides[id] = copy;
                        queue.Enqueue((ar.Num, id));
                        _out.Int(id).Ascii(" 0 R ");
                    }
                    else
                    {
                        WriteDict(_out, Without(ad, copy.Keys), 1);
                        _out.Sp();
                    }
                    continue;
                }
                WriteValue(_out, a, 1);
                _out.Sp();
            }
            _out.Ascii("]");
        }
        _out.Ascii(" >>");
        End();

        static PdfDict Without(PdfDict d, IEnumerable<string> keys)
        {
            var entries = new Dictionary<string, PdfObject>(d.Entries, StringComparer.Ordinal);
            foreach (var key in keys) entries.Remove(key);
            return new PdfDict(entries);
        }
    }

    /// <summary>Adds a tagged input's top-level structure elements, parent-tree entries and role and class maps to the merged ones.</summary>
    private void WriteStructure(Input input)
    {
        var file = input.File;
        var root = file.GetDict(file.Catalog, "StructTreeRoot")!;
        var saved = _out;
        try
        {
            _out = _structKids;
            foreach (var k in input.StructKids ?? KidsOf(file, root["K"]))
            {
                WriteValue(_out, k, 1);
                _out.Sp();
            }
            _out = _parentNums;
            foreach (var (key, value) in input.ParentNums!)
            {
                // With pages left out, entries for what is dropped go too.
                if (input.Partial && (input.DroppedKeys.Contains(key) || !LeadsToKeptElement(value))) continue;
                _out.Int(key + input.StructOffset).Sp();
                WriteValue(_out, value, 1);
                _out.Sp();
            }
            WriteMapTo(_roleMap, _roleNames, file.GetDict(root, "RoleMap"));
            WriteMapTo(_classMap, _classNames, file.GetDict(root, "ClassMap"));
        }
        finally
        {
            _out = saved;
        }

        bool LeadsToKeptElement(PdfObject value) => file.Resolve(value) switch
        {
            PdfArray a => a.Items.Any(i => i is PdfRef r && !IsDropped(input, r.Num)),
            PdfDict => value is not PdfRef r || !IsDropped(input, r.Num),
            _ => false,
        };

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
                WriteValue(_out, value, 1);
                _out.Sp();
            }
        }
    }

    /// <summary>
    /// Writes a /StructParent or /StructParents entry with the input's key offset, or leaves it out for an untagged input;
    /// false for any other key.
    /// </summary>
    private bool WriteStructKey(ByteBuffer b, Input input, string key, PdfObject value)
    {
        if (key is not ("StructParent" or "StructParents")) return false;
        if (input.StructOffset >= 0 && input.File.Resolve(value) is PdfNumber { IsInteger: true } n && n.Value >= 0 && n.Value < int.MaxValue / 2)
        {
            b.Sp();
            PdfSyntax.RawName(b, key);
            b.Sp().Int((long)n.Value + input.StructOffset);
        }
        return true;
    }

    /// <summary>Copies the input's embedded files (its attachments), renaming a name an earlier input used.</summary>
    private void CopyAttachments(Input input, PdfDict? catalog)
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
                WriteValue(_out, value, 1);
            }
            finally
            {
                _out = saved;
            }
            _attachments.Add((EmbeddedFiles.KeyBytes(name), buffer));
        }
    }

    /// <summary>Writes the (referenced) items of an array to another buffer, separated by spaces.</summary>
    private void WriteItemsTo(ByteBuffer target, Input input, PdfArray? items)
    {
        if (items == null) return;
        var saved = _out;
        _out = target;
        try
        {
            foreach (var item in items.Items)
            {
                if (item is not PdfRef r) continue; // optional content groups are always indirect
                if (IsDropped(input, r.Num)) continue; // a form field left out with its pages
                WriteValue(_out, item, 1);
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
    private ByteBuffer OutlineItemBody(Input input, int num)
    {
        var d = input.File.GetObject(num) as PdfDict ?? new PdfDict();
        if (input.Overrides.ContainsKey(num)) d = Overridden(input, d, num, 0);
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
                WriteValue(_out, value, 1, key);
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

    /// <summary>
    /// Writes an entry's value; a destination given by name is written as the explicit one it names, as the name tree is
    /// not copied.
    /// </summary>
    private void WriteValue(ByteBuffer b, PdfObject? o, int depth, string? key = null)
    {
        var input = _input;
        if (key == "Dest")
        {
            if (o is PdfString or PdfName) { WriteObject(b, NamedDest(input, o) ?? PdfNull.Instance, depth + 1); return; }
            // A named destination behind a reference.
            if (o is PdfRef r && !input.Map.ContainsKey(r.Num) && input.File.GetObject(r.Num) is PdfString or PdfName)
            {
                WriteObject(b, NamedDest(input, input.File.GetObject(r.Num)!) ?? PdfNull.Instance, 1);
                return;
            }
        }
        WriteObject(b, o, depth);
    }

    /// <summary>
    /// Shifts structure keys, and points a go-to action naming its destination at the page instead, since the name tree
    /// is not copied.
    /// </summary>
    protected override void WriteEntry(ByteBuffer b, PdfDict? owner, string key, PdfObject value, int depth)
    {
        var input = _input;
        if (WriteStructKey(b, input, key, value)) return;
        b.Sp();
        PdfSyntax.RawName(b, key);
        b.Sp();
        var file = input.File;
        if (owner != null && key == "D" && file.Resolve(value) is PdfString or PdfName && file.GetName(owner, "S") == "GoTo")
            WriteObject(b, NamedDest(input, file.Resolve(value)!) ?? PdfNull.Instance, depth + 1);
        else
            WriteValue(b, value, depth + 1, key);
    }

    /// <summary>A reference is renumbered, its object queued to be copied, or a stream identical to one written shares its copy.</summary>
    protected override void WriteRef(ByteBuffer b, PdfRef r)
    {
        var input = _input;
        if (!input.Map.TryGetValue(r.Num, out int id))
        {
            var target = input.File.GetObject(r.Num);
            if (target == null || target.IsNull) { input.Map[r.Num] = 0; b.Ascii("null"); return; }
            string? hash = target is PdfStream ts ? LeafStreamHash(input.File, ts) : null;
            if (hash != null && _streams.TryGetValue(hash, out id))
            {
                input.Map[r.Num] = id;
            }
            else
            {
                id = NewId();
                input.Map[r.Num] = id;
                _queue.Enqueue((r.Num, id));
                if (hash != null) _streams[hash] = id;
            }
        }
        if (id == 0) b.Ascii("null");
        else b.Int(id).Ascii(" 0 R");
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

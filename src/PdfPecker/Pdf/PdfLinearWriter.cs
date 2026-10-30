using System.IO.Compression;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Writes a parsed PDF out again as a linearized file (PDF 1.7 annex F, "Fast Web View"): the first page and the objects
/// it needs come first, behind a cross-reference table of their own and a hint stream that tells a viewer where every
/// page's objects lie, so that the first page shows before the rest of the file has arrived. Only the objects reachable
/// from the catalog and the document information are kept, the page tree becomes one node with inherited attributes
/// pushed down to the pages, and the result has classic cross-reference tables.
/// </summary>
/// <remarks>
/// The objects are put into the parts of annex F the way qpdf puts them, because qpdf's <c>--check-linearization</c> is
/// the strictest reader of hint tables there is and works its expectations out the same way: an object reached from the
/// first page goes with it, an object reached from exactly one other page goes with that page, an object reached from
/// several later pages is shared, and everything else (the page tree, the structure tree, the outline unless it opens
/// with the document) comes last. Offsets in the hint tables leave out the hint stream itself, as the specification
/// asks.
/// </remarks>
internal sealed class PdfLinearWriter : PdfObjectWriter
{
    // Objects the result has that are not indirect objects of the source: the new page tree node, a catalog or
    // information dictionary written directly in the trailer, the encryption dictionary, and pages written directly in
    // their parents' /Kids, which get keys from FirstDirectPageKey downwards.
    private const int PagesKey = -1, DirectRootKey = -2, DirectInfoKey = -3, EncryptKey = -4, FirstDirectPageKey = -5;

    /// <summary>The catalog entries whose objects a viewer needs to open the document (annex F, part 4).</summary>
    private static readonly HashSet<string> OpenDocumentKeys = new(StringComparer.Ordinal) { "ViewerPreferences", "PageMode", "Threads", "OpenAction", "AcroForm" };

    private readonly PdfFile _file;
    private readonly PdfEncryptor? _crypt;

    // The objects to write by key: source object numbers, or the synthetic keys above for the objects made here. Pages
    // and the catalog are copies with their entries changed; every other object is the parsed one.
    private readonly Dictionary<int, PdfObject> _objects = new();
    // Source objects written as another key (the old page tree nodes as the new node) or as null (0: pages that are not
    // in the page tree, and the source's encryption dictionary).
    private readonly Dictionary<int, int> _redirect = new();
    private readonly List<int> _pages = new();
    private readonly HashSet<int> _pageSet = new();
    private int _rootKey, _infoKey;

    // Who uses each object, as qpdf records it: the pages that reach it and the catalog and trailer entries that do.
    private readonly Dictionary<int, Use> _uses = new();
    private readonly List<int> _discovered = new(); // every key reached, in the order first reached
    private List<int>[] _pageObjects = Array.Empty<List<int>>(); // by page, the keys its traversal reached

    // The new object number of each key.
    private readonly Dictionary<int, int> _ids = new();

    private sealed class Use
    {
        public bool Root, OpenDocument, Outlines, FirstPage;
        public int OtherPages, Thumbs, Others;
        public int Users => (Root ? 1 : 0) + (OpenDocument ? 1 : 0) + (Outlines ? 1 : 0) + (FirstPage ? 1 : 0) + OtherPages + Thumbs + Others;
    }

    private PdfLinearWriter(PdfFile file, PdfEncryptor? crypt) : base(PdfObjectSyntax.Linearize)
    {
        _file = file;
        _crypt = crypt;
        Encryption = crypt;
    }

    /// <summary>
    /// Linearizes <paramref name="file"/>, encrypted with <paramref name="crypt"/> or unencrypted when it is null. The file
    /// identifier is kept, or made from <paramref name="fallbackId"/> when the source has none.
    /// </summary>
    public static ByteBuffer Write(PdfFile file, PdfEncryptor? crypt, byte[] fallbackId)
    {
        var w = new PdfLinearWriter(file, crypt);
        w.Collect();
        w.Classify(out var parts);
        return w.Emit(parts, fallbackId);
    }

    // ------------------------------------------------------------------ collecting

    private void Collect()
    {
        var trailer = _file.Trailer;
        if (trailer["Encrypt"] is PdfRef enc) _redirect[enc.Num] = 0;
        var catalog = _file.Catalog ?? throw new InvalidDataException("The PDF is not readable: it has no catalog.");
        _rootKey = trailer["Root"] is PdfRef root ? root.Num : DirectRootKey;

        CollectPages(catalog["Pages"]);
        if (_pages.Count == 0) throw new InvalidDataException("The PDF is not readable: it has no pages.");

        var kids = new List<PdfObject>(_pages.Count);
        foreach (int key in _pages) kids.Add(new PdfRef(key, 0));
        _objects[PagesKey] = Dict(("Type", PdfName.Get("Pages")), ("Kids", new PdfArray(kids)), ("Count", PdfNumber.Int(_pages.Count)));

        var catalogEntries = new Dictionary<string, PdfObject>(catalog.Entries, StringComparer.Ordinal) { ["Pages"] = new PdfRef(PagesKey, 0) };
        PdfDict newCatalog = new(catalogEntries);
        if (_crypt != null && PdfRewriter.HeaderVersion(_file.Data).Major < 2) newCatalog = PdfRewriter.WithExtensionLevel(_file, newCatalog);
        _objects[_rootKey] = newCatalog;

        // An information dictionary that is also the catalog, a page or a page tree node (only a damaged file has one) is
        // left out.
        if (_file.Resolve(trailer["Info"]) is PdfDict info
            && !(trailer["Info"] is PdfRef ir && (ir.Num == _rootKey || _pageSet.Contains(ir.Num) || _redirect.ContainsKey(ir.Num))))
        {
            _infoKey = trailer["Info"] is PdfRef r ? r.Num : DirectInfoKey;
            _objects[_infoKey] = info;
        }
        if (_crypt != null) _objects[EncryptKey] = new PdfDict();
    }

    /// <summary>
    /// Walks the page tree, recording each page as a copy with the attributes it inherits, a /Type and its new parent;
    /// intermediate nodes are written as the one new node.
    /// </summary>
    private void CollectPages(PdfObject? root)
    {
        int nextDirect = FirstDirectPageKey;
        PageTree.Walk(_file, root, (node, dict, inherited) =>
        {
            int key = node is PdfRef pr ? pr.Num : nextDirect--;
            if (dict is PdfStream || key == _rootKey || _pageSet.Contains(key)) return true;
            var entries = new Dictionary<string, PdfObject>(dict.Entries, StringComparer.Ordinal)
            {
                ["Type"] = PdfName.Get("Page"),
                ["Parent"] = new PdfRef(PagesKey, 0),
            };
            // A linearized file's pages carry what they inherit themselves.
            Carry("Resources", inherited.Resources);
            Carry("MediaBox", inherited.MediaBox);
            Carry("CropBox", inherited.CropBox);
            Carry("Rotate", inherited.Rotate);
            if (entries.TryGetValue("Resources", out var resources)) entries["Resources"] = OwnResources(resources, Contents(entries.GetValueOrDefault("Contents")));
            _objects[key] = new PdfDict(entries);
            _pages.Add(key);
            _pageSet.Add(key);
            return true;

            void Carry(string name, PdfObject? value)
            {
                if (value is { IsNull: false }) entries[name] = value;
            }
        },
        (node, _) =>
        {
            if (node is PdfRef r && r.Num != _rootKey) _redirect[r.Num] = PagesKey;
        });
    }

    private static PdfDict Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) d[k] = v;
        return new PdfDict(d);
    }

    /// <summary>The key a reference is written as, or 0 when it is written as null.</summary>
    private int KeyOf(PdfRef r) => _redirect.TryGetValue(r.Num, out int k) ? k : r.Num;

    /// <summary>The object with <paramref name="key"/>, or null when there is none and references to it are written as null.</summary>
    private PdfObject? Get(int key)
    {
        if (key == 0) return null;
        if (_objects.TryGetValue(key, out var o)) return o;
        if (key < 0) return null;
        o = _file.GetObject(key);
        if (o == null || o.IsNull || IsPage(o))
        {
            // A page that is not in the page tree (an old copy an outline still points at) is no page of the document.
            _redirect[key] = 0;
            return null;
        }
        // A form or tiling pattern that shares a resource dictionary gets its own, as pages do.
        if (o is PdfStream st && st["Resources"] is PdfRef shared)
        {
            var own = OwnResources(shared, new[] { st });
            if (own != shared) o = new PdfStream(new Dictionary<string, PdfObject>(st.Entries, StringComparer.Ordinal) { ["Resources"] = own }, st.RawData);
        }
        _objects[key] = o;
        return o;
    }

    /// <summary>The content streams of a page's /Contents: one stream or an array of them.</summary>
    private List<PdfStream> Contents(PdfObject? contents)
    {
        var list = new List<PdfStream>();
        if (_file.Resolve(contents) is PdfStream one) list.Add(one);
        else if (_file.Resolve(contents) is PdfArray parts)
            foreach (var part in parts.Items)
                if (_file.Resolve(part) is PdfStream s) list.Add(s);
        return list;
    }

    /// <summary>
    /// The resources a page, form or tiling pattern needs when its resource dictionary is an object others share too:
    /// a copy with only the fonts, images and other named resources its content names. A writer that keeps one resource
    /// dictionary for the whole document (as this library's does) would otherwise make every page need every image, and
    /// the first page section would hold nearly the whole file. Names are gathered from the content's bytes without
    /// parsing it, so a name in a string or an inline image keeps an unused resource but no used one is ever dropped;
    /// content that cannot be decoded keeps the whole dictionary.
    /// </summary>
    private PdfObject OwnResources(PdfObject resources, IReadOnlyList<PdfStream> contents)
    {
        if (resources is not PdfRef || _file.Resolve(resources) is not PdfDict all || all is PdfStream) return resources;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in contents)
        {
            try
            {
                ContentNames(_file.DecodeStream(content), names);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                return resources;
            }
        }
        var entries = new Dictionary<string, PdfObject>(all.Entries.Count, StringComparer.Ordinal);
        foreach (var (category, value) in all.Entries)
        {
            if (!NamedResources.Contains(category) || _file.Resolve(value) is not PdfDict byName || byName is PdfStream)
            {
                entries[category] = value;
                continue;
            }
            var kept = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            foreach (var (name, resource) in byName.Entries)
                if (names.Contains(name)) kept[name] = resource;
            if (kept.Count > 0) entries[category] = new PdfDict(kept);
        }
        return new PdfDict(entries);
    }

    /// <summary>The resource categories whose entries content streams name.</summary>
    private static readonly HashSet<string> NamedResources = new(StringComparer.Ordinal) { "Font", "XObject", "ExtGState", "Pattern", "Shading", "ColorSpace", "Properties" };

    /// <summary>Adds every name token in <paramref name="data"/> to <paramref name="names"/>, with #xx escapes decoded.</summary>
    internal static void ContentNames(ReadOnlySpan<byte> data, HashSet<string> names)
    {
        var name = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != '/') continue;
            name.Clear();
            int j = i + 1;
            while (j < data.Length && IsRegular(data[j]))
            {
                if (data[j] == '#' && j + 2 < data.Length && Uri.IsHexDigit((char)data[j + 1]) && Uri.IsHexDigit((char)data[j + 2]))
                {
                    name.Append((char)Convert.ToByte(System.Text.Encoding.ASCII.GetString(data.Slice(j + 1, 2)), 16));
                    j += 3;
                }
                else name.Append((char)data[j++]);
            }
            if (name.Length > 0) names.Add(name.ToString());
            i = j - 1;
        }
    }

    /// <summary>Whether a byte continues a name: neither whitespace nor a delimiter.</summary>
    private static bool IsRegular(byte c)
        => c is not ((byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\f' or 0 or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    private bool IsPage(PdfObject o) => o is PdfDict d and not PdfStream && _file.GetName(d, "Type") == "Page";

    // ------------------------------------------------------------------ classifying

    /// <summary>The objects of each part of the file, in the order they are written.</summary>
    private sealed class Parts
    {
        public readonly List<int> Part4 = new(), Part6 = new(), Part7 = new(), Part8 = new(), Part9 = new();
        public int[] PageObjectCounts = Array.Empty<int>();
        public int[] PageStarts = Array.Empty<int>(); // by page after the first, the index of its page object in Part7
        public List<int>[] SharedOfPage = Array.Empty<List<int>>(); // by page, indexes into the shared object table
        public int OutlineCount; // the outline's objects, starting with the outline dictionary; 0 without one
        public int OutlineKey;
    }

    private void Classify(out Parts parts)
    {
        // Who reaches what: each page, each catalog entry and the trailer's /Info, walked as qpdf walks them.
        _pageObjects = new List<int>[_pages.Count];
        for (int i = 0; i < _pages.Count; i++)
        {
            _pageObjects[i] = new List<int>();
            Walk(new PdfRef(_pages[i], 0), page: i, rootKey: null, _pageObjects[i]);
        }
        if (_infoKey != 0) Walk(new PdfRef(_infoKey, 0), page: -1, rootKey: null, null);
        if (_crypt != null) Mark(EncryptKey).OpenDocument = true;
        var catalog = (PdfDict)_objects[_rootKey];
        foreach (var (key, value) in catalog.Entries) Walk(value, page: -1, rootKey: key, null);
        Mark(_rootKey).Root = true;

        var open = new List<int>();
        var firstPrivate = new List<int>();
        var firstShared = new List<int>();
        var otherPrivate = new HashSet<int>();
        var otherShared = new List<int>();
        var thumbPrivate = new List<int>();
        var thumbShared = new List<int>();
        var outlines = new List<int>();
        var other = new List<int>();
        foreach (int key in _discovered)
        {
            var u = _uses[key];
            if (u.Root) continue;
            if (u.Outlines) outlines.Add(key);
            else if (u.OpenDocument) open.Add(key);
            else if (u.FirstPage && u.Others == 0 && u.OtherPages == 0 && u.Thumbs == 0) firstPrivate.Add(key);
            else if (u.FirstPage) firstShared.Add(key);
            else if (u.OtherPages == 1 && u.Others == 0 && u.Thumbs == 0) otherPrivate.Add(key);
            else if (u.OtherPages > 1) otherShared.Add(key);
            else if (u.Thumbs == 1 && u.Others == 0) thumbPrivate.Add(key);
            else if (u.Thumbs > 1) thumbShared.Add(key);
            else other.Add(key);
        }

        parts = new Parts();
        parts.Part4.Add(_rootKey);
        parts.Part4.AddRange(open);

        // The first page section: the page itself, then everything it reaches, and the outline when the document opens
        // with it showing.
        int firstPage = _pages[0];
        firstPrivate.Remove(firstPage);
        parts.Part6.Add(firstPage);
        parts.Part6.AddRange(firstPrivate);
        parts.Part6.AddRange(firstShared);
        int outlineKey = catalog["Outlines"] is PdfRef or && KeyOf(or) is var ok && ok != 0 && outlines.Contains(ok) ? ok : 0;
        bool outlinesFirst = outlineKey != 0 && _file.GetName(catalog, "PageMode") == "UseOutlines";
        if (outlineKey != 0)
        {
            outlines.Remove(outlineKey);
            outlines.Insert(0, outlineKey);
            parts.OutlineKey = outlineKey;
            parts.OutlineCount = outlines.Count;
        }
        if (outlinesFirst) parts.Part6.AddRange(outlines);

        // Each later page with the objects only it uses.
        parts.PageObjectCounts = new int[_pages.Count];
        parts.PageObjectCounts[0] = parts.Part6.Count;
        parts.PageStarts = new int[_pages.Count];
        for (int i = 1; i < _pages.Count; i++)
        {
            otherPrivate.Remove(_pages[i]);
            parts.PageStarts[i] = parts.Part7.Count;
            parts.Part7.Add(_pages[i]);
            int count = 1;
            foreach (int key in _pageObjects[i])
                if (otherPrivate.Remove(key))
                {
                    parts.Part7.Add(key);
                    count++;
                }
            parts.PageObjectCounts[i] = count;
        }
        parts.Part8.AddRange(otherShared);

        parts.Part9.Add(PagesKey);
        other.Remove(PagesKey);
        parts.Part9.AddRange(thumbPrivate);
        parts.Part9.AddRange(thumbShared);
        if (!outlinesFirst && outlineKey != 0) parts.Part9.AddRange(outlines);
        else if (outlineKey == 0) other.AddRange(outlines);
        parts.Part9.AddRange(other);

        // The shared object table lists every object of the first page section, then the shared objects of later pages;
        // a later page refers to the entries of the objects it reaches that something else reaches too.
        var sharedIndex = new Dictionary<int, int>();
        foreach (int key in parts.Part6) sharedIndex[key] = sharedIndex.Count;
        foreach (int key in parts.Part8) sharedIndex[key] = sharedIndex.Count;
        parts.SharedOfPage = new List<int>[_pages.Count];
        parts.SharedOfPage[0] = new List<int>();
        for (int i = 1; i < _pages.Count; i++)
        {
            var list = new List<int>();
            foreach (int key in _pageObjects[i])
                if (_uses[key].Users > 1 && sharedIndex.TryGetValue(key, out int index)) list.Add(index);
            list.Sort();
            parts.SharedOfPage[i] = list;
        }
    }

    private Use Mark(int key)
    {
        if (!_uses.TryGetValue(key, out var u))
        {
            _uses[key] = u = new Use();
            _discovered.Add(key);
        }
        return u;
    }

    /// <summary>
    /// Records <paramref name="page"/> (or the catalog entry <paramref name="rootKey"/>, or the trailer's /Info when both
    /// are absent) as a user of every object reached from <paramref name="start"/>. As in qpdf, a walk never enters a page
    /// other than the one it starts from, never climbs a page's /Parent, and counts what a page's /Thumb reaches as the
    /// thumbnail's.
    /// </summary>
    private void Walk(PdfObject start, int page, string? rootKey, List<int>? reached)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Obj, bool Top, bool Thumb)>();
        stack.Push((start, true, false));
        long work = 0;
        while (stack.Count > 0)
        {
            var (o, top, thumb) = stack.Pop();
            PdfObject? target = o;
            if (o is PdfRef r)
            {
                int key = KeyOf(r);
                target = Get(key);
                if (target == null) continue;
                // Only a page's own walk enters it: a catalog entry or the document information naming a page directly
                // (which qpdf would let through) must not claim it.
                if (_pageSet.Contains(key) && !(top && page >= 0)) continue;
                if (!visited.Add(key)) continue;
                var u = Mark(key);
                if (thumb) u.Thumbs++;
                else if (page == 0) u.FirstPage = true;
                else if (page > 0) u.OtherPages++;
                else if (rootKey == null) u.Others++;
                else if (OpenDocumentKeys.Contains(rootKey)) u.OpenDocument = true;
                else if (rootKey == "Outlines") u.Outlines = true;
                else u.Others++;
                reached?.Add(key);
            }
            if (++work % 4096 == 0) PdfFile.CheckCancel();
            switch (target)
            {
                case PdfArray a:
                    for (int i = a.Count - 1; i >= 0; i--) stack.Push((a[i], false, thumb));
                    break;
                case PdfDict d:
                    bool pageNode = top && page >= 0 && o is PdfRef;
                    foreach (var (key, value) in d.Entries)
                    {
                        if (value.IsNull || (pageNode && key == "Parent")) continue;
                        if (d is PdfStream && key == "Length") continue; // written directly
                        stack.Push((value, false, thumb || (pageNode && key == "Thumb")));
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ writing

    /// <summary>A reference is written with the new number of the object it leads to, or as null when it leads nowhere.</summary>
    protected override void WriteRef(ByteBuffer b, PdfRef r)
    {
        int key = KeyOf(r);
        if (key != 0 && _ids.TryGetValue(key, out int id)) b.Int(id).Ascii(" 0 R");
        else b.Ascii("null");
    }

    /// <summary>One object's place in a body buffer: its start and its length up to the next object.</summary>
    private readonly record struct Placed(int Key, int Id, int Start, int Length);

    private ByteBuffer Emit(Parts parts, byte[] fallbackId)
    {
        // Numbers: the later pages, their shared objects and the rest from 1; then the linearization dictionary, the
        // document-level objects, the hint stream and the first page section, which the first table lists.
        int next = 1;
        Number(parts.Part7, ref next);
        Number(parts.Part8, ref next);
        Number(parts.Part9, ref next);
        int firstHalf = next;
        int linId = next++;
        Number(parts.Part4, ref next);
        int hintId = next++;
        Number(parts.Part6, ref next);
        int size = next;

        var (major, minor) = PdfRewriter.HeaderVersion(_file.Data);
        if (_crypt != null && (major, minor).CompareTo((1, 7)) < 0) (major, minor) = (1, 7);
        var header = new ByteBuffer(32);
        header.Ascii("%PDF-").Int(major).Byte((byte)'.').Int(minor).Ascii("\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        var docLevel = new ByteBuffer(4096);
        var placedA = WriteBody(docLevel, parts.Part4);
        var body = new ByteBuffer(Math.Max(4096, _file.Data.Length + _file.Data.Length / 8));
        var placedB = new List<Placed>();
        placedB.AddRange(WriteBody(body, parts.Part6));
        int part6End = body.Length;
        placedB.AddRange(WriteBody(body, parts.Part7));
        int part8Start = body.Length;
        placedB.AddRange(WriteBody(body, parts.Part8));
        placedB.AddRange(WriteBody(body, parts.Part9));
        var byKey = new Dictionary<int, Placed>(placedB.Count);
        foreach (var p in placedB) byKey[p.Key] = p;

        var ids = _file.Resolve(_file.Trailer["ID"]) as PdfArray;
        byte[] id0 = ids is { Count: > 0 } && _file.Resolve(ids[0]) is PdfString { Bytes.Length: > 0 } s0 ? s0.Bytes : fallbackId;
        byte[] id1 = ids is { Count: > 1 } && _file.Resolve(ids[1]) is PdfString { Bytes.Length: > 0 } s1 ? s1.Bytes : id0;

        // The sizes of the linearization dictionary and the first trailer depend on the offsets they name, which depend on
        // those sizes: guess, measure and repeat until nothing moves. A size only ever grows, padded with spaces when its
        // numbers get shorter again, so this ends, after a few rounds at most.
        int linLength = 0, trailerLength = 0;
        ByteBuffer lin = null!, firstTrailer = null!, hint = null!;
        long mainXref = 0, fileLength = 0, hintOffset = 0;
        int firstEntries = size - firstHalf;
        string firstXrefHead = "xref\n" + firstHalf.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "
                               + firstEntries.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        int firstTableLength = firstXrefHead.Length + 20 * firstEntries;
        string mainXrefHead = "xref\n0 " + firstHalf.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
        while (true)
        {
            hintOffset = header.Length + linLength + firstTableLength + trailerLength + docLevel.Length;
            // The hint tables give offsets as if the hint stream were not there, so the body would start where it does.
            hint = HintStream(hintId, parts, byKey, hintOffset, part8Start);
            long bodyStart = hintOffset + hint.Length;
            mainXref = bodyStart + body.Length;
            long mainZero = mainXref + mainXrefHead.Length - 1; // the newline before the entry for object 0
            fileLength = mainXref + mainXrefHead.Length + 20L * firstHalf + MainTrailer(firstHalf, id0, id1, header.Length + linLength).Length;
            int firstPageId = _ids[_pages[0]];
            lin = new ByteBuffer(256);
            lin.Int(linId).Ascii(" 0 obj\n<< /Linearized 1 /L ").Int(fileLength).Ascii(" /H [").Int(hintOffset).Sp().Int(hint.Length)
                .Ascii("] /O ").Int(firstPageId).Ascii(" /E ").Int(bodyStart + part6End).Ascii(" /N ").Int(_pages.Count)
                .Ascii(" /T ").Int(mainZero);
            Pad(lin, linLength - " >>\nendobj\n".Length);
            lin.Ascii(" >>\nendobj\n");
            firstTrailer = FirstTrailer(size, id0, id1, mainXref, trailerLength);
            if (lin.Length == linLength && firstTrailer.Length == trailerLength) break;
            linLength = Math.Max(linLength, lin.Length);
            trailerLength = Math.Max(trailerLength, firstTrailer.Length);
        }

        long linStart = header.Length;
        long docStart = linStart + lin.Length + firstTableLength + firstTrailer.Length;
        long bStart = hintOffset + hint.Length;
        var output = new ByteBuffer((int)Math.Min(int.MaxValue, fileLength));
        output.Bytes(header.Span).Bytes(lin.Span).Ascii(firstXrefHead);
        // The first table: the linearization dictionary, the document-level objects, the hint stream, the first page.
        PdfXref.Padded(output, linStart, 10).Ascii(" 00000 n \n");
        foreach (var p in placedA) PdfXref.Padded(output, docStart + p.Start, 10).Ascii(" 00000 n \n");
        PdfXref.Padded(output, hintOffset, 10).Ascii(" 00000 n \n");
        int part6Count = parts.Part6.Count;
        for (int i = 0; i < part6Count; i++) PdfXref.Padded(output, bStart + placedB[i].Start, 10).Ascii(" 00000 n \n");
        output.Bytes(firstTrailer.Span).Bytes(docLevel.Span).Bytes(hint.Span).Bytes(body.Span);
        // The main table: object 0 and the later pages, shared objects and the rest, numbered 1 up in file order.
        output.Ascii(mainXrefHead).Ascii("0000000000 65535 f \n");
        for (int i = part6Count; i < placedB.Count; i++) PdfXref.Padded(output, bStart + placedB[i].Start, 10).Ascii(" 00000 n \n");
        output.Bytes(MainTrailer(firstHalf, id0, id1, linStart + lin.Length).Span);
        System.Diagnostics.Debug.Assert(output.Length == fileLength, "The linearized file is not the length its dictionary says.");
        return output;
    }

    /// <summary>Numbers <paramref name="keys"/> in order from <paramref name="next"/>.</summary>
    private void Number(List<int> keys, ref int next)
    {
        foreach (int key in keys)
            // Every object belongs to one part; one in two means a page that is also something else.
            if (!_ids.TryAdd(key, next++)) throw new InvalidDataException("The PDF is not readable: its page tree is damaged.");
    }

    /// <summary>Writes <paramref name="keys"/> one after another and records where each lies.</summary>
    private List<Placed> WriteBody(ByteBuffer b, List<int> keys)
    {
        var placed = new List<Placed>(keys.Count);
        foreach (int key in keys)
        {
            PdfFile.CheckCancel();
            int id = _ids[key];
            int start = b.Length;
            b.Int(id).Ascii(" 0 obj\n");
            ObjectNumber = id;
            if (key == EncryptKey) _crypt!.WriteDictionary(b);
            else if (_objects[key] is PdfStream s) WriteStream(b, _file, s);
            else WriteObject(b, _objects[key]);
            PdfXref.End(b);
            placed.Add(new Placed(key, id, start, b.Length - start));
        }
        return placed;
    }

    /// <summary>Writes spaces until <paramref name="b"/> is <paramref name="length"/> bytes long.</summary>
    private static void Pad(ByteBuffer b, int length)
    {
        while (b.Length < length) b.Sp();
    }

    /// <summary>The first page's trailer, padded with spaces to at least <paramref name="padTo"/> bytes.</summary>
    private ByteBuffer FirstTrailer(int size, byte[] id0, byte[] id1, long mainXref, int padTo)
    {
        var t = new ByteBuffer(256);
        t.Ascii("trailer\n<< /Size ").Int(size).Ascii(" /Root ").Int(_ids[_rootKey]).Ascii(" 0 R");
        if (_infoKey != 0 && _ids.TryGetValue(_infoKey, out int infoId)) t.Ascii(" /Info ").Int(infoId).Ascii(" 0 R");
        if (_crypt != null) t.Ascii(" /Encrypt ").Int(_ids[EncryptKey]).Ascii(" 0 R");
        WriteId(t, id0, id1);
        t.Ascii(" /Prev ").Int(mainXref);
        const string End = " >>\nstartxref\n0\n%%EOF\n";
        Pad(t, padTo - End.Length);
        t.Ascii(End);
        return t;
    }

    /// <summary>The main trailer, whose startxref leads a reader that does not know linearization to the first table.</summary>
    private static ByteBuffer MainTrailer(int size, byte[] id0, byte[] id1, long firstXref)
    {
        var t = new ByteBuffer(160);
        t.Ascii("trailer\n<< /Size ").Int(size);
        WriteId(t, id0, id1);
        t.Ascii(" >>\nstartxref\n").Int(firstXref).Ascii("\n%%EOF\n");
        return t;
    }

    private static void WriteId(ByteBuffer t, byte[] id0, byte[] id1)
    {
        // The identifier is never encrypted.
        t.Ascii(" /ID [");
        PdfSyntax.Hex(t, id0, upper: true);
        PdfSyntax.Hex(t, id1, upper: true);
        t.Ascii("]");
    }

    // ------------------------------------------------------------------ hint stream

    /// <summary>
    /// The primary hint stream object: the page offset hint table, the shared object hint table and, when there is an
    /// outline, its generic hint table (annex F.4). <paramref name="bodyStart"/> is where the first page section would
    /// start without the hint stream.
    /// </summary>
    private ByteBuffer HintStream(int hintId, Parts parts, Dictionary<int, Placed> placed, long bodyStart, int part8Start)
    {
        var bits = new BitWriter();
        int pageCount = _pages.Count;

        // Page offset hint table. A page's objects are numbered consecutively from its page object, so they lie one after
        // another and their lengths add up to the page's.
        var pageLengths = new long[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            var group = i == 0 ? parts.Part6 : parts.Part7;
            int start = i == 0 ? 0 : parts.PageStarts[i];
            for (int n = 0; n < parts.PageObjectCounts[i]; n++) pageLengths[i] += placed[group[start + n]].Length;
        }
        int minObjects = parts.PageObjectCounts.Min(), maxObjects = parts.PageObjectCounts.Max();
        long minLength = pageLengths.Min(), maxLength = pageLengths.Max();
        int maxShared = parts.SharedOfPage.Max(l => l.Count);
        int sharedTotal = parts.Part6.Count + parts.Part8.Count;
        int nbObjects = NBits(maxObjects - minObjects), nbLength = NBits(maxLength - minLength);
        int nbShared = NBits(maxShared), nbIdentifier = NBits(sharedTotal);
        bits.Write(minObjects, 32);
        bits.Write(bodyStart + placed[_pages[0]].Start, 32);
        bits.Write(nbObjects, 16);
        bits.Write(minLength, 32);
        bits.Write(nbLength, 16);
        bits.Write(0, 32); // least content stream offset: 0, as Acrobat writes it
        bits.Write(0, 16);
        bits.Write(minLength, 32); // content stream lengths: the page lengths, as qpdf and pdlin write them
        bits.Write(nbLength, 16);
        bits.Write(nbShared, 16);
        bits.Write(nbIdentifier, 16);
        bits.Write(0, 16); // numerators of the fractional position: none
        bits.Write(4, 16);
        for (int i = 0; i < pageCount; i++) bits.Write(parts.PageObjectCounts[i] - minObjects, nbObjects);
        bits.Flush();
        for (int i = 0; i < pageCount; i++) bits.Write(pageLengths[i] - minLength, nbLength);
        bits.Flush();
        for (int i = 0; i < pageCount; i++) bits.Write(parts.SharedOfPage[i].Count, nbShared);
        bits.Flush();
        for (int i = 0; i < pageCount; i++)
            foreach (int index in parts.SharedOfPage[i]) bits.Write(index, nbIdentifier);
        bits.Flush();
        // The numerators and the content stream offsets take 0 bits each; the content stream lengths repeat the page lengths.
        for (int i = 0; i < pageCount; i++) bits.Write(pageLengths[i] - minLength, nbLength);
        bits.Flush();

        // Shared object hint table: one object per group.
        int sharedOffset = bits.Length;
        var groupLengths = new long[sharedTotal];
        for (int i = 0; i < sharedTotal; i++)
            groupLengths[i] = placed[i < parts.Part6.Count ? parts.Part6[i] : parts.Part8[i - parts.Part6.Count]].Length;
        long minGroup = sharedTotal > 0 ? groupLengths.Min() : 0, maxGroup = sharedTotal > 0 ? groupLengths.Max() : 0;
        int nbGroup = NBits(maxGroup - minGroup);
        bits.Write(parts.Part8.Count > 0 ? _ids[parts.Part8[0]] : 0, 32);
        bits.Write(parts.Part8.Count > 0 ? bodyStart + part8Start : 0, 32);
        bits.Write(parts.Part6.Count, 32);
        bits.Write(sharedTotal, 32);
        bits.Write(0, 16); // objects per group, less one: always 0 bits
        bits.Write(minGroup, 32);
        bits.Write(nbGroup, 16);
        for (int i = 0; i < sharedTotal; i++) bits.Write(groupLengths[i] - minGroup, nbGroup);
        bits.Flush();
        for (int i = 0; i < sharedTotal; i++) bits.Write(0, 1); // no MD5 signatures
        bits.Flush();

        // Outline hint table.
        int outlineOffset = 0;
        if (parts.OutlineCount > 0)
        {
            outlineOffset = bits.Length;
            var first = placed[parts.OutlineKey];
            long length = 0;
            int index = parts.Part6.IndexOf(parts.OutlineKey);
            var list = index >= 0 ? parts.Part6 : parts.Part9;
            if (index < 0) index = list.IndexOf(parts.OutlineKey);
            for (int n = 0; n < parts.OutlineCount; n++) length += placed[list[index + n]].Length;
            bits.Write(first.Id, 32);
            bits.Write(bodyStart + first.Start, 32);
            bits.Write(parts.OutlineCount, 32);
            bits.Write(length, 32);
            bits.Flush();
        }

        byte[] data;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(bits.Span);
            data = ms.ToArray();
        }
        if (Encryption != null) data = Encryption.EncryptStream(data, hintId, 0);
        var b = new ByteBuffer(data.Length + 128);
        b.Int(hintId).Ascii(" 0 obj\n<< /Filter /FlateDecode /S ").Int(sharedOffset);
        if (outlineOffset > 0) b.Ascii(" /O ").Int(outlineOffset);
        b.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
        PdfXref.End(b);
        return b;
    }

    /// <summary>The number of bits that hold values from 0 to <paramref name="value"/>.</summary>
    private static int NBits(long value)
    {
        int n = 0;
        while (value > 0) { n++; value >>= 1; }
        return n;
    }

    /// <summary>Packs numbers into bits, most significant first, as the hint tables store them.</summary>
    private sealed class BitWriter
    {
        private readonly ByteBuffer _bytes = new(256);
        private int _current, _used;

        public int Length => _bytes.Length;

        public ReadOnlySpan<byte> Span => _bytes.Span;

        public void Write(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_used == 8)
                {
                    _bytes.Byte((byte)_current);
                    _current = _used = 0;
                }
            }
        }

        /// <summary>Pads the last byte with zeros: each item of a hint table starts on a byte boundary.</summary>
        public void Flush()
        {
            if (_used > 0) Write(0, 8 - _used);
        }
    }
}

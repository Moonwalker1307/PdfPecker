using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Rewrites a parsed PDF with a page of another PDF drawn over or under its pages. Every object reachable from the
/// trailer is copied once, renumbered; the chosen pages get the stamp page as a form XObject in their resources and a
/// content stream before or after theirs that draws it. The stamp's objects reachable from its page's resources are
/// copied the same way, without their links into the stamp document's page tree and structure tree.
/// </summary>
internal sealed class PdfStampWriter
{
    private sealed class Source
    {
        public readonly PdfFile File;
        public readonly bool IsStamp;
        public readonly Dictionary<int, int> Map = new(); // source object number -> new id; 0 = write null
        public readonly Queue<(int Num, int Id)> Queue = new();
        public readonly List<PageInfo> Pages = new();
        public Source(PdfFile file, bool isStamp) { File = file; IsStamp = isStamp; }
    }

    private sealed class PageInfo
    {
        public PdfDict Dict = null!;
        public int Num; // 0 for a direct page object, which is left as it is
        public PdfObject? Resources;
        public double[] Box = null!; // the visible area: crop box within media box, normalized
        public int Rotate; // 0, 90, 180 or 270
        public (Source Source, int Page)? Stamp;
    }

    private readonly PdfStampOptions _options;
    private readonly CancellationToken _cancel;
    private readonly Source _target;
    private readonly bool _tagged;
    private readonly ByteBuffer _out = new(1 << 16);
    private readonly List<long> _offsets = new() { 0 };
    private int _nextId = 1;
    private PdfEncryptor? _crypt;
    private readonly Dictionary<PdfFile, Source> _stamps = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Source, int), int> _forms = new();
    private readonly Queue<(int Id, Source Source, int Page)> _pendingForms = new();
    private readonly Dictionary<string, int> _contentStreams = new(StringComparer.Ordinal);
    private readonly List<(int Id, byte[] Data)> _pendingStreams = new();
    private readonly Dictionary<int, PageInfo> _stamped = new(); // by object number
    private int _gsId;

    /// <summary>The most decoded content a stamp page may have: it is copied into the output whole.</summary>
    private const int MaxStampContent = 64 << 20;

    public PdfStampWriter(byte[] pdf, PdfStampOptions options, CancellationToken cancel)
    {
        _options = options;
        _cancel = cancel;
        if (options.StampPage < 0) throw new ArgumentOutOfRangeException(nameof(options), "PdfStampOptions.StampPage cannot be negative.");
        if (!(options.Opacity >= 0 && options.Opacity <= 1)) throw new ArgumentOutOfRangeException(nameof(options), "PdfStampOptions.Opacity must be between 0 and 1.");
        if (!(options.Scale >= 0 && options.Scale <= 1000)) throw new ArgumentOutOfRangeException(nameof(options), "PdfStampOptions.Scale must be between 0 and 1000.");
        if (!float.IsFinite(options.OffsetX) || !float.IsFinite(options.OffsetY) || Math.Abs(options.OffsetX) > 1e6 || Math.Abs(options.OffsetY) > 1e6)
            throw new ArgumentOutOfRangeException(nameof(options), "PdfStampOptions.OffsetX and OffsetY must be finite and within a million points.");

        using (PdfFile.BeginRender(cancel))
        {
            PdfFile file;
            try
            {
                file = PdfFile.Open(pdf, options.Password);
            }
            catch (PdfFormatException ex)
            {
                throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
            }
            _target = new Source(file, false);
            var catalog = file.Catalog ?? throw new InvalidDataException("The PDF has no document catalog.");
            if (!options.InvalidateSignatures && IsSigned(file, catalog))
                throw new InvalidOperationException("The PDF is digitally signed, and stamping it would invalidate the signatures. "
                    + "Stamp it before signing, or set PdfStampOptions.InvalidateSignatures to stamp it anyway.");
            if (file.Trailer["Encrypt"] is PdfRef enc) _target.Map[enc.Num] = 0;
            CollectPages(_target, catalog["Pages"]);
            if (_target.Pages.Count == 0) throw new InvalidDataException("The PDF has no pages.");
            _tagged = file.GetDict(catalog, "StructTreeRoot") != null;
        }
    }

    /// <summary>Opens a stamp PDF and reads its page tree, so a stamp shared by many pages is parsed once.</summary>
    public static PdfFile OpenStamp(byte[] pdf, string? password, CancellationToken cancel)
    {
        using (PdfFile.BeginRender(cancel))
        {
            try
            {
                return PdfFile.Open(pdf, password);
            }
            catch (PdfFormatException ex)
            {
                throw new InvalidDataException("The stamp PDF is not readable: " + ex.Message, ex);
            }
            catch (PdfPasswordException ex)
            {
                throw new PdfPasswordException("The stamp PDF: " + ex.Message);
            }
        }
    }

    /// <summary>The size of a page as it is displayed (its visible area, turned by its rotation), in points.</summary>
    public (double Width, double Height) DisplaySize(int pageIndex)
    {
        var p = _target.Pages[pageIndex];
        double w = p.Box[2] - p.Box[0], h = p.Box[3] - p.Box[1];
        return p.Rotate is 90 or 270 ? (h, w) : (w, h);
    }

    /// <summary>Writes the stamped document; <paramref name="stampFor"/> gives the stamp PDF for a chosen page.</summary>
    public byte[] Write(Func<int, PdfFile> stampFor)
    {
        // Choose the stamp page of each page first, so a bad option fails before anything is written.
        for (int i = 0; i < _target.Pages.Count; i++)
        {
            _cancel.ThrowIfCancellationRequested();
            var page = _target.Pages[i];
            if (page.Num == 0 || (_options.Pages != null && !_options.Pages(i))) continue;
            var source = StampSource(stampFor(i));
            int count = source.Pages.Count;
            int index = _options.MatchPages ? Math.Min(i, count - 1) : _options.StampPage;
            if (index >= count)
                throw new ArgumentOutOfRangeException("options", $"PdfStampOptions.StampPage is {index}, but the stamp PDF has {count} page(s).");
            page.Stamp = (source, index);
            _stamped[page.Num] = page;
        }

        if (_options.Security != null) _crypt = new PdfEncryptor(_options.Security);
        try
        {
            var file = _target.File;
            bool v2 = file.Data.Length >= 8 && Encoding.ASCII.GetString(file.Data, 0, 8) == "%PDF-2.0";
            _out.Ascii(v2 ? "%PDF-2.0\n%" : "%PDF-1.7\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();
            {
                int rootId = IdFor(_target, file.Trailer["Root"]);
                int infoId = file.Trailer["Info"] is { } info && file.Resolve(info) is PdfDict ? IdFor(_target, info) : 0;
                if (_options.Opacity < 1 && _target.Pages.Any(p => p.Stamp != null))
                {
                    _gsId = NewId();
                    Begin(_gsId).Ascii("<< /Type /ExtGState /CA ").Ascii(Num(_options.Opacity)).Ascii(" /ca ").Ascii(Num(_options.Opacity)).Ascii(" >>");
                    End();
                }
                Drain();

                int encryptId = 0;
                if (_crypt != null)
                {
                    encryptId = NewId();
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
                _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(rootId).Ascii(" 0 R");
                if (infoId != 0) _out.Ascii(" /Info ").Int(infoId).Ascii(" 0 R");
                if (encryptId != 0) _out.Ascii(" /Encrypt ").Int(encryptId).Ascii(" 0 R");
                // The document keeps its permanent identifier; the second part marks this version.
                var first = file.Resolve(file.Trailer["ID"]) is PdfArray ids && ids.Count > 0 && file.Resolve(ids[0]) is PdfString s && s.Bytes.Length is > 0 and <= 64
                    ? s.Bytes : RandomNumberGenerator.GetBytes(16);
                _out.Ascii(" /ID [");
                PdfEncryptor.Hex(_out, first);
                PdfEncryptor.Hex(_out, RandomNumberGenerator.GetBytes(16));
                _out.Ascii("] >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");
            }
            return _out.Span.ToArray();
        }
        finally
        {
            _crypt?.Dispose();
        }
    }

    private Source StampSource(PdfFile file)
    {
        if (_stamps.TryGetValue(file, out var source)) return source;
        source = new Source(file, true);
        using (PdfFile.BeginRender(_cancel))
        {
            var catalog = file.Catalog;
            // Links back into the stamp document (its catalog, page tree, pages, structure tree) are not followed.
            if (file.Trailer["Root"] is PdfRef root) source.Map[root.Num] = 0;
            if (file.Trailer["Encrypt"] is PdfRef enc) source.Map[enc.Num] = 0;
            if (catalog?["StructTreeRoot"] is PdfRef st) source.Map[st.Num] = 0;
            CollectPages(source, catalog?["Pages"]);
        }
        if (source.Pages.Count == 0) throw new InvalidDataException("The stamp PDF has no pages.");
        foreach (var p in source.Pages)
            if (p.Num != 0) source.Map[p.Num] = 0;
        _stamps[file] = source;
        return source;
    }

    // ------------------------------------------------------------------ reading

    private static bool IsSigned(PdfFile file, PdfDict catalog)
    {
        // Certification and usage-rights signatures.
        if (file.GetDict(catalog, "Perms") is { } perms && perms.Entries.Count > 0) return true;
        var fields = file.GetArray(file.GetDict(catalog, "AcroForm"), "Fields");
        if (fields == null) return false;
        var stack = new Stack<(PdfObject Field, int Depth)>(fields.Items.Select(f => (f, 0)));
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0 && seen.Count < 100_000)
        {
            var (item, depth) = stack.Pop();
            if (depth > 32 || file.Resolve(item) is not PdfDict field || !seen.Add(field)) continue;
            if (file.GetDict(field, "V") is { } v && v["ByteRange"] != null) return true;
            if (file.GetArray(field, "Kids") is { } kids)
                foreach (var k in kids.Items) stack.Push((k, depth + 1));
        }
        return false;
    }

    private void CollectPages(Source source, PdfObject? root)
    {
        var file = source.File;
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Node, PdfObject? Res, PdfObject? Media, PdfObject? Crop, PdfObject? Rotate, int Depth)>();
        if (root != null) stack.Push((root, null, null, null, null, 0));
        while (stack.Count > 0)
        {
            var (node, res, media, crop, rotate, depth) = stack.Pop();
            if (depth > 64 || file.Resolve(node) is not PdfDict dict || !seen.Add(dict)) continue;
            res = dict["Resources"] ?? res;
            media = dict["MediaBox"] ?? media;
            crop = dict["CropBox"] ?? crop;
            rotate = dict["Rotate"] ?? rotate;
            var kids = file.GetArray(dict, "Kids");
            if (kids != null && file.GetName(dict, "Type") != "Page")
            {
                if (source.IsStamp && node is PdfRef r) source.Map[r.Num] = 0;
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], res, media, crop, rotate, depth + 1));
                continue;
            }
            if (source.Pages.Count >= 1_000_000) throw new PdfLimitException("The PDF has more than 1,000,000 pages.");
            source.Pages.Add(new PageInfo
            {
                Dict = dict,
                Num = node is PdfRef pr ? pr.Num : 0,
                Resources = res,
                Box = VisibleBox(file, media, crop),
                Rotate = ((int)Math.Round(file.Num(rotate) / 90) % 4 + 4) % 4 * 90,
            });
        }
        // A page listed twice in the tree is stamped once.
        var nums = new HashSet<int>();
        foreach (var p in source.Pages)
            if (p.Num != 0 && !nums.Add(p.Num)) p.Num = 0;
    }

    private static double[] VisibleBox(PdfFile file, PdfObject? media, PdfObject? crop)
    {
        var m = Rect(file, media) ?? new double[] { 0, 0, 612, 792 };
        if (Rect(file, crop) is { } c)
        {
            var x = new[] { Math.Max(m[0], c[0]), Math.Max(m[1], c[1]), Math.Min(m[2], c[2]), Math.Min(m[3], c[3]) };
            if (x[2] - x[0] > 0.01 && x[3] - x[1] > 0.01) return x;
        }
        return m;
    }

    private static double[]? Rect(PdfFile file, PdfObject? o)
    {
        if (file.ToNumbers(file.Resolve(o) as PdfArray) is not { Length: >= 4 } a || a.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e7)) return null;
        var r = new[] { Math.Min(a[0], a[2]), Math.Min(a[1], a[3]), Math.Max(a[0], a[2]), Math.Max(a[1], a[3]) };
        return r[2] - r[0] > 0.01 && r[3] - r[1] > 0.01 ? r : null;
    }

    // ------------------------------------------------------------------ writing

    private int NewId() => _nextId++;

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

    /// <summary>The id an object gets in the output: a reference is copied when the queue reaches it, a direct object now.</summary>
    private int IdFor(Source source, PdfObject? o)
    {
        if (o is PdfRef r)
        {
            if (!source.Map.TryGetValue(r.Num, out int id))
            {
                id = NewId();
                source.Map[r.Num] = id;
                source.Queue.Enqueue((r.Num, id));
            }
            return id;
        }
        int direct = NewId();
        Begin(direct);
        WriteObject(source, source.File.Resolve(o) ?? new PdfDict(), 0);
        End();
        return direct;
    }

    /// <summary>Copies queued objects of every source, and the stamp forms the pages use, until nothing is left.</summary>
    private void Drain()
    {
        while (true)
        {
            _cancel.ThrowIfCancellationRequested();
            Source? source = _target.Queue.Count > 0 ? _target : _stamps.Values.FirstOrDefault(s => s.Queue.Count > 0);
            if (source != null)
            {
                var (num, id) = source.Queue.Dequeue();
                // Each object, with the page content read to stamp a page, gets its own decoding budget.
                using (PdfFile.BeginRender(_cancel))
                {
                    var obj = source.File.GetObject(num) ?? PdfNull.Instance;
                    Begin(id);
                    if (source == _target && _stamped.TryGetValue(num, out var page)) WriteStampedPage(page);
                    else if (obj is PdfStream s) WriteStream(source, s);
                    else WriteObject(source, obj, 0);
                    End();
                }
                foreach (var (sid, data) in _pendingStreams) WriteContentStream(sid, data, compressed: false);
                _pendingStreams.Clear();
            }
            else if (_pendingForms.Count > 0)
            {
                var (id, stamp, index) = _pendingForms.Dequeue();
                WriteForm(id, stamp, stamp.Pages[index]);
            }
            else return;
        }
    }

    private void WriteStampedPage(PageInfo page)
    {
        var file = _target.File;
        var (stamp, stampIndex) = page.Stamp!.Value;
        if (!_forms.TryGetValue((stamp, stampIndex), out int formId))
        {
            formId = NewId();
            _forms[(stamp, stampIndex)] = formId;
            _pendingForms.Enqueue((formId, stamp, stampIndex));
        }
        var res = file.Resolve(page.Resources) as PdfDict ?? new PdfDict();
        var xobjects = file.GetDict(res, "XObject") ?? new PdfDict();
        var states = file.GetDict(res, "ExtGState") ?? new PdfDict();
        string formName = FreeName(xobjects, "Stamp"), gsName = FreeName(states, "StampGS");

        _out.Ascii("<<");
        foreach (var (key, value) in page.Dict.Entries)
        {
            if (key is "Contents" or "Resources") continue;
            WriteEntry(_target, key, value, 1);
        }
        _out.Ascii(" /Resources <<");
        foreach (var (key, value) in res.Entries)
            if (key is not ("XObject" or "ExtGState")) WriteEntry(_target, key, value, 2);
        _out.Ascii(" /XObject <<");
        foreach (var (key, value) in xobjects.Entries) WriteEntry(_target, key, value, 3);
        _out.Sp();
        PdfSyntax.RawName(_out, formName);
        _out.Sp().Int(formId).Ascii(" 0 R >>");
        if (_gsId != 0 || states.Entries.Count > 0)
        {
            _out.Ascii(" /ExtGState <<");
            foreach (var (key, value) in states.Entries) WriteEntry(_target, key, value, 3);
            if (_gsId != 0)
            {
                _out.Sp();
                PdfSyntax.RawName(_out, gsName);
                _out.Sp().Int(_gsId).Ascii(" 0 R");
            }
            _out.Ascii(" >>");
        }
        _out.Ascii(" >>");

        // The drawing of the stamp, placed as the page is displayed, and kept out of the structure tree.
        var draw = new StringBuilder();
        if (_tagged) draw.Append("/Artifact BMC\n");
        draw.Append("q\n").Append(MatrixOps(Placement(page, stamp.Pages[stampIndex]))).Append(" cm\n");
        if (_gsId != 0) draw.Append('/').Append(NameText(gsName)).Append(" gs\n");
        draw.Append('/').Append(NameText(formName)).Append(" Do\nQ\n");
        if (_tagged) draw.Append("EMC\n");

        var contents = ContentItems(file, page.Dict["Contents"]);
        _out.Ascii(" /Contents [");
        if (_options.Underlay)
        {
            _out.Int(ContentStream(draw.ToString())).Ascii(" 0 R");
            foreach (var c in contents) { _out.Sp(); WriteObject(_target, c, 2); }
        }
        else
        {
            // The page content is wrapped in q ... Q, with extra levels for a page that restores more than it saves or
            // leaves states saved, so nothing it sets carries over to the stamp.
            var (under, open) = SaveDepth(file, contents);
            _out.Int(ContentStream(string.Concat(Enumerable.Repeat("q\n", 1 + under)))).Ascii(" 0 R");
            foreach (var c in contents) { _out.Sp(); WriteObject(_target, c, 2); }
            _out.Sp().Int(ContentStream("\n" + string.Concat(Enumerable.Repeat("Q\n", 1 + open)) + draw)).Ascii(" 0 R");
        }
        _out.Ascii("] >>");
    }

    /// <summary>The page's content streams, as references (or the stream objects themselves, when given directly).</summary>
    private static List<PdfObject> ContentItems(PdfFile file, PdfObject? contents)
    {
        var resolved = file.Resolve(contents);
        if (resolved is PdfStream) return new List<PdfObject> { contents! };
        if (resolved is PdfArray a) return a.Items.Where(i => file.Resolve(i) is PdfStream).Take(100_000).ToList();
        return new List<PdfObject>();
    }

    /// <summary>
    /// How far the page content's q/Q nesting goes below its start (more Q than q), and how many states it leaves saved
    /// at its end above that lowest level: wrapped in 1 + Under q and 1 + Open Q, it is balanced. Content that cannot be
    /// read counts as balanced.
    /// </summary>
    private (int Under, int Open) SaveDepth(PdfFile file, List<PdfObject> contents)
    {
        int depth = 0, low = 0;
        try
        {
            foreach (var c in contents)
            {
                if (file.Resolve(c) is not PdfStream s) continue;
                var parser = new ContentParser(file.DecodeStream(s));
                while (parser.Next())
                {
                    if (parser.Op == "q") depth++;
                    else if (parser.Op == "Q" && --depth < low) low = depth;
                }
            }
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return (0, 0);
        }
        int under = Math.Min(-low, 10_000);
        return (under, Math.Min(depth - low, 10_000));
    }

    /// <summary>Maps the stamp's display space (see <see cref="WriteForm"/>) onto the page's user space.</summary>
    private Matrix Placement(PageInfo page, PageInfo stamp)
    {
        var (w, h) = Display(page);
        var (sw, sh) = Display(stamp);
        double s = _options.Scale > 0 ? _options.Scale : Math.Min(w / sw, h / sh);
        double pw = sw * s, ph = sh * s;
        var pos = _options.Position;
        double x = pos is PdfStampPosition.Left or PdfStampPosition.TopLeft or PdfStampPosition.BottomLeft ? 0
            : pos is PdfStampPosition.Right or PdfStampPosition.TopRight or PdfStampPosition.BottomRight ? w - pw
            : (w - pw) / 2;
        double y = pos is PdfStampPosition.Bottom or PdfStampPosition.BottomLeft or PdfStampPosition.BottomRight ? 0
            : pos is PdfStampPosition.Top or PdfStampPosition.TopLeft or PdfStampPosition.TopRight ? h - ph
            : (h - ph) / 2;
        x += _options.OffsetX;
        y -= _options.OffsetY;
        return new Matrix(s, 0, 0, s, x, y).Multiply(DisplayToUser(page));
    }

    private static (double W, double H) Display(PageInfo p)
    {
        double w = p.Box[2] - p.Box[0], h = p.Box[3] - p.Box[1];
        return p.Rotate is 90 or 270 ? (h, w) : (w, h);
    }

    /// <summary>
    /// Maps a page as displayed (origin at its bottom left corner as seen, after rotation) to its user space: the
    /// rotation turns the page clockwise.
    /// </summary>
    private static Matrix DisplayToUser(PageInfo p)
    {
        double x0 = p.Box[0], y0 = p.Box[1], w = p.Box[2] - x0, h = p.Box[3] - y0;
        return p.Rotate switch
        {
            90 => new Matrix(0, 1, -1, 0, x0 + w, y0),
            180 => new Matrix(-1, 0, 0, -1, x0 + w, y0 + h),
            270 => new Matrix(0, -1, 1, 0, x0, y0 + h),
            _ => new Matrix(1, 0, 0, 1, x0, y0),
        };
    }

    /// <summary>
    /// Writes a stamp page as a form XObject whose matrix maps it to its display space, so it can be placed upright
    /// whatever its own rotation; it is clipped to the stamp page's visible area.
    /// </summary>
    private void WriteForm(int id, Source stamp, PageInfo page)
    {
        var file = stamp.File;
        byte[] content;
        using (PdfFile.BeginRender(_cancel))
        {
            var data = new MemoryStream();
            try
            {
                foreach (var c in ContentItems(file, page.Dict["Contents"]))
                {
                    data.Write(file.DecodeStream((PdfStream)file.Resolve(c)!));
                    data.WriteByte((byte)'\n');
                    if (data.Length > MaxStampContent) throw new InvalidDataException("The stamp page's content is larger than 64 MB.");
                }
            }
            catch (Exception ex) when (ex is PdfFormatException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                throw new InvalidDataException("The stamp page's content cannot be read: " + ex.Message, ex);
            }
            content = WithoutMarkedContentIds(data.ToArray());
        }
        DisplayToUser(page).TryInvert(out var matrix);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
        var raw = packed.ToArray();
        if (_crypt != null) raw = _crypt.Encrypt(raw);

        Begin(id).Ascii("<< /Type /XObject /Subtype /Form /BBox [").Ascii(string.Join(" ", page.Box.Select(Num))).Ascii("] /Matrix [")
            .Ascii(MatrixOps(matrix)).Ascii("] /Resources ");
        WriteObject(stamp, file.Resolve(page.Resources) as PdfDict ?? new PdfDict(), 1);
        if (page.Dict["Group"] is { } group) { _out.Ascii(" /Group "); WriteObject(stamp, group, 1); }
        _out.Ascii(" /Filter /FlateDecode /Length ").Int(raw.Length).Ascii(" >>\nstream\n").Bytes(raw).Ascii("\nendstream");
        End();
    }

    /// <summary>
    /// The stamp's marked content with its MCIDs removed: they point into the stamp document's structure tree, which is
    /// not copied, and the stamp is an artifact in a tagged page.
    /// </summary>
    private static byte[] WithoutMarkedContentIds(byte[] content)
    {
        var parser = new ContentParser(content);
        var result = new MemoryStream();
        int copied = 0;
        while (parser.Next())
        {
            if (parser.Op != "BDC" || parser.Operands.Count != 2 || parser.Operands[0].Name is not { } tag
                || parser.Operands[1].Dict is not { } props || !props.ContainsKey("MCID")) continue;
            result.Write(content, copied, parser.Start - copied);
            var b = new ByteBuffer(64);
            b.Nl();
            PdfSyntax.RawName(b, tag);
            b.Ascii(" BMC");
            result.Write(b.Span);
            copied = parser.End;
        }
        if (copied == 0) return content;
        result.Write(content, copied, content.Length - copied);
        return result.ToArray();
    }

    /// <summary>The id of a small content stream, shared by every page that draws the same.</summary>
    private int ContentStream(string text)
    {
        if (_contentStreams.TryGetValue(text, out int id)) return id;
        id = NewId();
        _contentStreams[text] = id;
        _pendingStreams.Add((id, Encoding.Latin1.GetBytes(text)));
        return id;
    }

    private void WriteContentStream(int id, byte[] data, bool compressed)
    {
        if (_crypt != null) data = _crypt.Encrypt(data);
        Begin(id).Ascii("<< /Length ").Int(data.Length).Ascii(compressed ? " /Filter /FlateDecode" : "").Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
        End();
    }

    private static string FreeName(PdfDict names, string stem)
    {
        if (!names.ContainsKey(stem)) return stem;
        for (int i = 1; ; i++)
            if (!names.ContainsKey(stem + i)) return stem + i;
    }

    private static string NameText(string name)
    {
        var b = new ByteBuffer(32);
        PdfSyntax.RawName(b, name);
        return Encoding.Latin1.GetString(b.Span)[1..];
    }

    private static string MatrixOps(Matrix m) => string.Join(" ", new[] { m.A, m.B, m.C, m.D, m.E, m.F }.Select(Num));

    private static string Num(double v)
    {
        if (!double.IsFinite(v)) return "0";
        var s = Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string Num(float v) => Num((double)v);

    // ------------------------------------------------------------------ copying objects

    private void WriteEntry(Source source, string key, PdfObject value, int depth)
    {
        // Keys into the stamp document's structure tree have no meaning here.
        if (source.IsStamp && key is "StructParent" or "StructParents") return;
        _out.Sp();
        PdfSyntax.RawName(_out, key);
        _out.Sp();
        WriteObject(source, value, depth + 1);
    }

    private void WriteStream(Source source, PdfStream s)
    {
        var entries = new Dictionary<string, PdfObject>(s.Entries, StringComparer.Ordinal);
        entries.Remove("Length");
        PdfMergeWriter.RemoveCryptFilter(source.File, entries);
        var data = _crypt != null ? _crypt.Encrypt(s.RawData) : s.RawData;
        _out.Ascii("<<");
        foreach (var (key, value) in entries) WriteEntry(source, key, value, 1);
        _out.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
    }

    private void WriteObject(Source source, PdfObject? o, int depth)
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
                if (_crypt != null) PdfEncryptor.Hex(_out, _crypt.Encrypt(s.Bytes));
                else PdfSyntax.HexString(_out, s.Bytes);
                break;
            case PdfName nm:
                PdfSyntax.RawName(_out, nm.Value);
                break;
            case PdfRef r:
                if (!source.Map.TryGetValue(r.Num, out int id))
                {
                    var target = source.File.GetObject(r.Num);
                    if (target == null || target.IsNull) id = 0;
                    else
                    {
                        id = NewId();
                        source.Queue.Enqueue((r.Num, id));
                    }
                    source.Map[r.Num] = id;
                }
                if (id == 0) _out.Ascii("null");
                else _out.Int(id).Ascii(" 0 R");
                break;
            case PdfArray a:
                _out.Byte((byte)'[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) _out.Sp();
                    WriteObject(source, a[i], depth + 1);
                }
                _out.Byte((byte)']');
                break;
            case PdfStream:
                _out.Ascii("null"); // a stream can only be an indirect object
                break;
            case PdfDict d:
                _out.Ascii("<<");
                foreach (var (key, value) in d.Entries) WriteEntry(source, key, value, depth);
                _out.Ascii(" >>");
                break;
            default:
                _out.Ascii("null");
                break;
        }
    }
}

using System.Globalization;
using PdfPecker.Images;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Removes what pages draw in given areas from the parsed file, for <see cref="PdfRedactor"/>: page content is written
/// again without the glyphs, shapes and image pixels in the areas, forms are redacted in copies, and annotations that touch
/// an area go. The caller writes the file out with <see cref="PdfRewriter"/>.
/// </summary>
internal sealed partial class Redaction
{
    /// <summary>An area in points on the displayed page, origin top left, y downward.</summary>
    internal readonly record struct Area(double X0, double Y0, double X1, double Y1);

    /// <summary>A bounding box in the same space as <see cref="Area"/>.</summary>
    private readonly record struct Box(double X0, double Y0, double X1, double Y1);

    /// <summary>How deep forms that draw forms are followed; a form deeper than this that touches an area is removed.</summary>
    private const int MaxDepth = 24;

    /// <summary>The share of a glyph's width and height an area must cover for the glyph to go.</summary>
    private const double GlyphCover = 0.2;

    /// <summary>How deep the structure tree is climbed from an element that held removed text.</summary>
    private const int MaxStructureDepth = 64;

    /// <summary>
    /// How many forms one page may walk. A form that draws itself twice doubles the work at each level, so past this many
    /// the forms left that touch an area are removed instead of walked.
    /// </summary>
    private const int MaxFormWalks = 4096;

    private readonly PdfFile _file;
    private readonly uint? _fill;
    private readonly Dictionary<PdfDict, PdfFont?> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(int StructParents, int Mcid)> _structHits = new();
    private readonly HashSet<PdfDict> _replaced = new(ReferenceEqualityComparer.Instance);
    private SortedDictionary<int, PdfObject>? _parentTree;
    private List<Area> _areas = new();
    private AreaGrid? _grid;
    private Matrix _display;
    private int _names;
    private int _formWalks;
    private int _hiddenWalks;

    public Redaction(PdfFile file, uint? fill)
    {
        _file = file;
        _fill = fill;
    }

    /// <summary>Redacts <paramref name="areas"/> (in points on the displayed page) of page <paramref name="index"/>.</summary>
    public void RedactPage(RenderDocument doc, int index, List<Area> areas)
    {
        var info = doc.Pages[index];
        var page = info.Dict;
        _areas = areas.Where(a => a.X1 > a.X0 && a.Y1 > a.Y0).ToList();
        if (_areas.Count == 0) return;
        _grid = AreaGrid.Build(_areas);
        _display = doc.DisplayMatrix(index);
        _structHits.Clear();
        _formWalks = 0;
        _hiddenWalks = 0;

        var context = new Context(PdfDict.CopyOf(info.Resources), _file.GetInt(page, "StructParents", -1));
        var data = PageContent(page);
        var body = data != null ? Walk(data, context, Matrix.Identity, 0) : null;
        if (body != null || _fill != null)
        {
            var drawn = body ?? data ?? Array.Empty<byte>();
            var content = new ByteBuffer(drawn.Length + 256);
            // The boxes are drawn in the page's own space: content that restores more states than it saves, or leaves
            // some saved with a changed transform, gets the q and Q it lacks.
            var (under, open) = PdfStampWriter.SaveDepth(drawn);
            for (int i = 0; i <= under; i++) content.Ascii("q\n");
            content.Bytes(drawn).Ascii("\n");
            for (int i = 0; i <= open; i++) content.Ascii("Q\n");
            if (_fill is uint fill && _display.TryInvert(out var toUser))
            {
                content.Ascii("q ").Ascii(F(((fill >> 16) & 255) / 255.0)).Sp().Ascii(F(((fill >> 8) & 255) / 255.0)).Sp()
                    .Ascii(F((fill & 255) / 255.0)).Ascii(" rg\n");
                foreach (var a in _areas)
                {
                    var (x0, y0) = toUser.Apply(a.X0, a.Y0);
                    var (x1, y1) = toUser.Apply(a.X1, a.Y1);
                    content.Ascii(F(Math.Min(x0, x1))).Sp().Ascii(F(Math.Min(y0, y1))).Sp().Ascii(F(Math.Abs(x1 - x0))).Sp()
                        .Ascii(F(Math.Abs(y1 - y0))).Ascii(" re\n");
                }
                content.Ascii("f\nQ\n");
            }
            page.Entries["Contents"] = _file.AddObject(new PdfStream(
                new Dictionary<string, PdfObject>(StringComparer.Ordinal) { ["Filter"] = PdfName.Get("FlateDecode") },
                PdfImage.Deflate(content.Span.ToArray(), System.IO.Compression.CompressionLevel.Optimal)));
        }
        if (context.Changed) page.Entries["Resources"] = context.Resources;
        page.Entries.Remove("Thumb");
        RemoveAnnotations(page);
        StripStructureTexts();
    }

    /// <summary>The page's content streams, decoded and joined; null when it has none. A stream that cannot be decoded draws nothing, so it is left out.</summary>
    private byte[]? PageContent(PdfDict page)
    {
        var streams = _file.Resolve(page["Contents"]) switch
        {
            PdfStream s => new List<PdfStream> { s },
            PdfArray a => a.Items.Select(i => _file.Resolve(i) as PdfStream).Where(s => s != null).Select(s => s!).ToList(),
            _ => new List<PdfStream>(),
        };
        if (streams.Count == 0) return null;
        var all = new MemoryStream();
        foreach (var s in streams)
        {
            if (Decode(s) is not { } part) continue;
            all.Write(part);
            all.WriteByte((byte)'\n');
        }
        return all.ToArray();
    }

    private byte[]? Decode(PdfStream s)
    {
        try
        {
            return _file.DecodeStream(s);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>The bounding box on the displayed page of a rectangle in the space <paramref name="m"/> maps to user space.</summary>
    private Box BoxOf(double x0, double y0, double x1, double y1, in Matrix m)
    {
        var full = m.Multiply(_display);
        var (ax, ay) = full.Apply(x0, y0);
        var (bx, by) = full.Apply(x1, y0);
        var (cx, cy) = full.Apply(x1, y1);
        var (dx, dy) = full.Apply(x0, y1);
        return new Box(Min(ax, bx, cx, dx), Min(ay, by, cy, dy), Max(ax, bx, cx, dx), Max(ay, by, cy, dy));
    }

    private static double Min(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private static double Max(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    /// <summary>
    /// Whether an area covers at least <paramref name="share"/> of the box's width and of its height; a box of no width
    /// or height counts as covered along that side when its edge lies in the area. A share of 0 asks whether they overlap.
    /// </summary>
    private bool Covered(Box b, double share)
    {
        if (!double.IsFinite(b.X0) || !double.IsFinite(b.Y0) || !double.IsFinite(b.X1) || !double.IsFinite(b.Y1)) return false;
        if (_grid is { } grid && grid.Candidates(b.X0, b.Y0, b.X1, b.Y1, out var candidates))
        {
            foreach (int i in candidates)
            {
                var a = _areas[i];
                if (Side(b.X0, b.X1, a.X0, a.X1, share) && Side(b.Y0, b.Y1, a.Y0, a.Y1, share)) return true;
            }
            return false;
        }
        foreach (var a in _areas)
        {
            if (Side(b.X0, b.X1, a.X0, a.X1, share) && Side(b.Y0, b.Y1, a.Y0, a.Y1, share)) return true;
        }
        return false;
    }

    /// <summary>
    /// The areas of a page by the cells of a grid they reach, so that a box is compared only with the areas near it.
    /// Redacting every match of a word that a page repeats 200,000 times compared each glyph with every area, which
    /// took minutes.
    /// </summary>
    private sealed class AreaGrid
    {
        private const int MinAreas = 64, MaxSide = 256, MaxEntries = 8_000_000;

        // A box reaching more cells than this is compared with every area: it is rare, and walking the cells of a
        // large box would cost more than the areas.
        private const int MaxBoxCells = 256;

        private readonly double _x0, _y0, _x1, _y1, _cellWidth, _cellHeight;
        private readonly int _side;
        private readonly int[] _starts, _entries;

        private AreaGrid(double x0, double y0, double x1, double y1, int side, int[] starts, int[] entries)
        {
            (_x0, _y0, _x1, _y1, _side, _starts, _entries) = (x0, y0, x1, y1, side, starts, entries);
            _cellWidth = Math.Max((x1 - x0) / side, 1e-9);
            _cellHeight = Math.Max((y1 - y0) / side, 1e-9);
        }

        /// <summary>The grid over the areas, or null when there are few of them or they would fill it too much.</summary>
        public static AreaGrid? Build(List<Area> areas)
        {
            if (areas.Count < MinAreas) return null;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var a in areas)
            {
                if (!double.IsFinite(a.X0) || !double.IsFinite(a.Y0) || !double.IsFinite(a.X1) || !double.IsFinite(a.Y1)) return null;
                x0 = Math.Min(x0, a.X0);
                y0 = Math.Min(y0, a.Y0);
                x1 = Math.Max(x1, a.X1);
                y1 = Math.Max(y1, a.Y1);
            }
            int side = Math.Clamp((int)Math.Ceiling(Math.Sqrt(areas.Count)), 1, MaxSide);
            var grid = new AreaGrid(x0, y0, x1, y1, side, new int[side * side + 1], Array.Empty<int>());
            long total = 0;
            foreach (var a in areas)
            {
                var (cx0, cy0, cx1, cy1) = grid.Cells(a.X0, a.Y0, a.X1, a.Y1);
                total += (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
                if (total > MaxEntries) return null;
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++) grid._starts[cy * side + cx + 1]++;
            }
            for (int c = 1; c < grid._starts.Length; c++) grid._starts[c] += grid._starts[c - 1];
            var entries = new int[total];
            var next = grid._starts.AsSpan(0, side * side).ToArray();
            for (int i = 0; i < areas.Count; i++)
            {
                var (cx0, cy0, cx1, cy1) = grid.Cells(areas[i].X0, areas[i].Y0, areas[i].X1, areas[i].Y1);
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++) entries[next[cy * side + cx]++] = i;
            }
            return new AreaGrid(x0, y0, x1, y1, side, grid._starts, entries);
        }

        /// <summary>
        /// The indexes of the areas that may touch the box, an area once for each cell they share; false when the box is
        /// too large and every area has to be looked at.
        /// </summary>
        public bool Candidates(double x0, double y0, double x1, double y1, out IEnumerable<int> candidates)
        {
            candidates = Array.Empty<int>();
            // An area touches only boxes that reach its own extent, and all of them lie inside the grid's.
            if (x1 < _x0 || x0 > _x1 || y1 < _y0 || y0 > _y1) return true;
            var (cx0, cy0, cx1, cy1) = Cells(x0, y0, x1, y1);
            if ((long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1) > MaxBoxCells) return false;
            candidates = Enumerate(cx0, cy0, cx1, cy1);
            return true;
        }

        private IEnumerable<int> Enumerate(int cx0, int cy0, int cx1, int cy1)
        {
            for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int k = _starts[cy * _side + cx], end = _starts[cy * _side + cx + 1]; k < end; k++) yield return _entries[k];
        }

        private (int X0, int Y0, int X1, int Y1) Cells(double x0, double y0, double x1, double y1)
            => (Cell(x0, _x0, _cellWidth), Cell(y0, _y0, _cellHeight), Cell(x1, _x0, _cellWidth), Cell(y1, _y0, _cellHeight));

        // The cell a coordinate falls in, clamped to the grid; computed in double first, since a cast of a value out of
        // range differs between runtimes.
        private int Cell(double v, double origin, double size) => (int)Math.Clamp(Math.Floor((v - origin) / size), 0, _side - 1);
    }

    private static bool Side(double b0, double b1, double a0, double a1, double share)
    {
        double size = b1 - b0;
        if (size < 1e-9) return b0 >= a0 && b0 <= a1;
        double overlap = Math.Min(b1, a1) - Math.Max(b0, a0);
        return share <= 0 ? overlap > 0 : overlap >= share * size - 1e-9;
    }

    private bool Overlaps(Box b) => Covered(b, 0);

    private bool Inside(Box b) => Covered(b, 1);

    // ------------------------------------------------------------------ annotations and forms

    /// <summary>Removes the annotations that touch an area, with their pop-ups, and the form fields of removed widgets.</summary>
    private void RemoveAnnotations(PdfDict page)
    {
        if (_file.GetArray(page, "Annots") is not { } annots) return;
        var removed = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        foreach (var item in annots.Items)
        {
            if (_file.Resolve(item) is not PdfDict annot || _file.GetNumbers(annot, "Rect") is not { Length: >= 4 } r) continue;
            if (!Overlaps(BoxOf(r[0], r[1], r[2], r[3], Matrix.Identity))) continue;
            removed.Add(annot);
            if (_file.GetDict(annot, "Popup") is { } popup) removed.Add(popup);
        }
        if (removed.Count == 0) return;
        // A pop-up shows its parent's text, so it goes with its parent wherever it sits.
        foreach (var item in annots.Items)
            if (_file.Resolve(item) is PdfDict annot && _file.GetDict(annot, "Parent") is { } parent && removed.Contains(parent)
                && _file.GetName(annot, "Subtype") == "Popup")
                removed.Add(annot);

        page.Entries["Annots"] = new PdfArray(annots.Items.Where(i => _file.Resolve(i) is not PdfDict d || !removed.Contains(d)).ToList());
        foreach (var annot in removed)
        {
            if (_file.GetName(annot, "Subtype") == "Widget") RemoveField(annot);
            // The dictionary may still be reachable, from the structure tree or another annotation, so it is emptied as well.
            annot.Entries.Clear();
        }
    }

    /// <summary>Takes a widget out of the form's field tree, and with it each field left without widgets.</summary>
    private void RemoveField(PdfDict widget)
    {
        var acroForm = _file.GetDict(_file.Catalog, "AcroForm");
        if (acroForm == null) return;
        // An XFA form keeps the field data again, out of reach of the widgets.
        acroForm.Entries.Remove("XFA");
        PdfDict node = widget;
        for (int depth = 0; depth < MaxStructureDepth; depth++)
        {
            var parent = _file.GetDict(node, "Parent");
            var kids = parent != null ? _file.GetArray(parent, "Kids") : _file.GetArray(acroForm, "Fields");
            if (kids == null) return;
            kids.Items.RemoveAll(k => ReferenceEquals(_file.Resolve(k), node));
            if (!ReferenceEquals(node, widget)) node.Entries.Clear();
            if (parent == null || kids.Count > 0) return;
            node = parent;
        }
    }

    /// <summary>
    /// Structure elements whose marked content lost text lose their replacement and alternative texts, which would
    /// otherwise still say what was there; so do the elements around them.
    /// </summary>
    private void StripStructureTexts()
    {
        if (_structHits.Count == 0) return;
        if (_parentTree == null)
        {
            _parentTree = new SortedDictionary<int, PdfObject>();
            var root = _file.GetDict(_file.Catalog, "StructTreeRoot");
            CollectNums(_file.GetDict(root, "ParentTree"), _parentTree, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
        }
        foreach (var (structParents, mcid) in _structHits)
        {
            if (!_parentTree.TryGetValue(structParents, out var entry)) continue;
            var element = _file.Resolve(entry) switch
            {
                PdfArray a when mcid >= 0 && mcid < a.Count => _file.Resolve(a[mcid]) as PdfDict,
                PdfDict d => d,
                _ => null,
            };
            for (int depth = 0; element != null && depth < MaxStructureDepth && _file.GetName(element, "Type") != "StructTreeRoot"; depth++)
            {
                element.Entries.Remove("ActualText");
                element.Entries.Remove("Alt");
                element.Entries.Remove("E");
                element = _file.GetDict(element, "P");
            }
        }
    }

    private void CollectNums(PdfDict? node, SortedDictionary<int, PdfObject> into, HashSet<PdfDict> seen, int depth)
    {
        if (node == null || depth > 32 || !seen.Add(node) || into.Count > 1_000_000) return;
        if (_file.GetArray(node, "Nums") is { } nums)
            for (int i = 0; i + 1 < nums.Count; i += 2)
                if (_file.Resolve(nums[i]) is PdfNumber { IsInteger: true } n && n.Value >= 0 && n.Value < int.MaxValue / 2)
                    into.TryAdd((int)n.Value, nums[i + 1]);
        if (_file.GetArray(node, "Kids") is { } kids)
            foreach (var kid in kids.Items)
                CollectNums(_file.Resolve(kid) as PdfDict, into, seen, depth + 1);
    }

    // ------------------------------------------------------------------ writing

    /// <summary>A number as content streams spell it.</summary>
    private static string F(double v)
    {
        if (!double.IsFinite(v)) return "0";
        var s = Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static void WriteName(ByteBuffer b, string name)
    {
        b.Byte((byte)'/');
        foreach (char c in name)
        {
            byte x = (byte)c;
            if (x is < 0x21 or > 0x7E or (byte)'#' or (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
                or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
                b.Byte((byte)'#').Ascii(x.ToString("X2", CultureInfo.InvariantCulture));
            else b.Byte(x);
        }
    }

    private static void WriteHex(ByteBuffer b, ReadOnlySpan<byte> bytes)
    {
        b.Byte((byte)'<');
        foreach (byte x in bytes) b.Ascii(x.ToString("X2", CultureInfo.InvariantCulture));
        b.Byte((byte)'>');
    }

    /// <summary>A direct object as content streams spell it, for marked content properties.</summary>
    private static void WriteValue(ByteBuffer b, PdfObject? o, int depth)
    {
        switch (depth > 16 ? null : o)
        {
            case PdfName n: WriteName(b, n.Value); break;
            case PdfNumber n: b.Ascii(n.IsInteger ? ((long)n.Value).ToString(CultureInfo.InvariantCulture) : F(n.Value)); break;
            case PdfString s: WriteHex(b, s.Bytes); break;
            case PdfBool v: b.Ascii(v.Value ? "true" : "false"); break;
            case PdfArray a:
                b.Byte((byte)'[');
                foreach (var item in a.Items)
                {
                    WriteValue(b, item, depth + 1);
                    b.Sp();
                }
                b.Byte((byte)']');
                break;
            case PdfDict d:
                b.Ascii("<<");
                foreach (var (key, value) in d.Entries)
                {
                    WriteName(b, key);
                    b.Sp();
                    WriteValue(b, value, depth + 1);
                    b.Sp();
                }
                b.Ascii(">>");
                break;
            default: b.Ascii("null"); break;
        }
    }

    /// <summary>The resources a stream is walked with: a copy that names the redacted forms and images it adds.</summary>
    private sealed class Context
    {
        public Context(PdfDict resources, int structParents)
        {
            Resources = resources;
            StructParents = structParents;
        }

        public readonly PdfDict Resources;
        public readonly int StructParents;
        public bool Changed;
        private PdfDict? _xObjects;

        private PdfDict? _properties;

        public string AddXObject(Redaction owner, PdfRef xObject) => Add(owner, "XObject", ref _xObjects, xObject);

        public string AddProperties(Redaction owner, PdfDict properties) => Add(owner, "Properties", ref _properties, properties);

        private string Add(Redaction owner, string category, ref PdfDict? names, PdfObject value)
        {
            if (names == null)
            {
                names = PdfDict.CopyOf(owner._file.GetDict(Resources, category));
                Resources.Entries[category] = names;
            }
            string name;
            do name = "Rd" + (++owner._names).ToString(CultureInfo.InvariantCulture);
            while (names.ContainsKey(name));
            names.Entries[name] = value;
            Changed = true;
            return name;
        }
    }
}

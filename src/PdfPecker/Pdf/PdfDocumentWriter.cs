using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

internal sealed class PageSetup
{
    public float Width, Height;
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public uint Background; // @page background colour, painted over the whole sheet
    public float BandTop, BandBottom; // space for running headers and footers at the top and bottom of the content area
    /// <summary>
    /// Size on paper of one layout point (PdfOptions.Scale). The other fields are in layout points, so the page is laid
    /// out at its paper size divided by the scale and drawn scaled.
    /// </summary>
    public float Scale = 1;
    public float ContentWidth => Width - MarginLeft - MarginRight;
    public float ContentHeight => Height - MarginTop - MarginBottom;
    /// <summary>Height available to the flowing content on each page.</summary>
    public float FlowHeight => ContentHeight - BandTop - BandBottom;
}

/// <summary>
/// A run of pages with one page setup: the content of a named page group (<c>@page wide { size: landscape }</c>), or
/// the whole document. Its draw ops, anchors and fixed boxes use y from the top of its own flow.
/// </summary>
internal sealed class PageSection
{
    public PageSetup Page = null!;
    private PageGrid? _grid;
    /// <summary>The setups and heights of the section's pages; every page has <see cref="Page"/> unless set.</summary>
    public PageGrid Grid { get => _grid ??= new PageGrid(Page); set => _grid = value; }
    public List<DrawOp> Ops = null!;
    public float ContentHeight;
    public IReadOnlyDictionary<string, float> Anchors = new Dictionary<string, float>();
    public List<DrawOp>? FixedOps;
    /// <summary>The footnote areas of the section's pages, in flow coordinates at the bottom of their pages.</summary>
    public List<DrawOp>? FootnoteOps;
    /// <summary>
    /// The height a page's footnote area (by its index within the section) takes from the bottom of the page's flow:
    /// the flow's boxes are cut off there, as at the end of a page.
    /// </summary>
    public Func<int, float>? FootnoteReserve;
    /// <summary>The canvas background of the section's page with the given index within the section.</summary>
    public Func<int, List<DrawOp>>? Canvas;
    /// <summary>The number of pages the content needs, from its height: at least one.</summary>
    public static int PagesFor(float contentHeight, float flowHeight)
        => Math.Max(1, (int)Math.Floor((Math.Max(0, contentHeight - 0.5f) + 0.01) / flowHeight) + 1);
}

/// <summary>
/// Serializes laid-out draw operations into a PDF 1.4 file (PDF 1.7 for PDF/A-2 and PDF/A-3, tagging and AES-256
/// encryption; PDF 2.0 for PDF/A-4 and PDF/UA-2).
/// </summary>
internal sealed partial class PdfDocumentWriter
{
    private PageSetup _page; // the setup of the page being written
    private readonly bool _compress;
    private readonly CompressionLevel _level;
    private readonly ByteBuffer _out = new(64 * 1024);
    private readonly PdfXref _xref = new();

    // Content-stream graphics state while writing one page.
    private uint _fill;
    private bool _inText;
    private TextFont? _textFont;
    private float _textSize, _tw, _tc;
    private int _tr;           // text rendering mode: 0 fill, 2 fill and stroke (synthetic bold), 3 invisible
    // Writing only the text of content drawn as pixels, invisibly, so that it can still be selected, searched and tagged.
    private bool _invisibleText;
    private uint _stroke;
    private float _lineWidth;
    private int _fillAlpha = 255, _strokeAlpha = 255; // the constant alphas (0-255) in effect; -1 when unknown
    private bool _anyAlpha;                           // an alpha other than 1 was set: alphas after Q are unknown
    private int _resourcesId;                         // the shared resources object, for pattern cells that draw SVG images
    private bool _noAlpha;                            // writing a stream without the shared ExtGState resources
    private readonly SortedSet<int> _fillAlphas = new(), _strokeAlphas = new(), _groupAlphas = new();
    private readonly List<byte[]> _forms = new(); // transparency groups of translucent layers, written as /Fx1, /Fx2, ...
    private readonly List<bool> _formIsolated = new(); // by form: an isolated group (blended or isolation: isolate)
    private readonly SortedSet<(int Alpha, string Blend)> _blendStates = new(); // /g<alpha><Blend>: group alpha and blend mode
    private readonly SortedSet<string> _layerBlends = new(StringComparer.Ordinal); // /b<Blend>: a background layer's blend mode
    private readonly List<(byte[] Content, string Dict, bool Alpha)> _masks = new(); // soft masks (gradient alphas, SVG masks), set by /s1, /s2, ...
    private readonly List<PdfImage> _rasters = new(); // SVG and HTML filter results and SVG image elements, written as /Xr1, /Xr2, ...
    private readonly Dictionary<PdfImage, int> _rasterNumbers = new(); // the /Xr number of each image an SVG embeds
    private readonly List<FontRun> _runs = new();
    private readonly Dictionary<FontProgram, GlyphSubset> _subsets = new();
    private readonly List<GlyphSubset> _subsetList = new();
    private float _yOrigin; // distance from the page top to flow y = 0 on the page being written
    private readonly ByteBuffer _patterns = new(256); // "/P1 << ... >>" entries for the shared resources
    private readonly List<Gradient> _conics = new();  // conic gradients, whose functions are stream objects
    private int _patternCount;
    private readonly List<(int Name, byte[] Content, string Dict)> _tilings = new(); // tiling patterns, written as /T1, /T2, ...
    private List<PdfImage> _imageList = new(); // the images written, by image number - 1

    /// <summary>Stamp on every page.</summary>
    public PdfWatermark? Watermark { get; init; }

    /// <summary>The document is right to left: its pages progress from right to left (/ViewerPreferences /Direction).</summary>
    public bool RightToLeft { get; init; }

    /// <summary>Encrypt the file with these passwords.</summary>
    public PdfSecurity? Security { get; init; }

    /// <summary>Write a PDF/A file.</summary>
    public PdfConformance Conformance { get; init; }

    /// <summary>A Factur-X / ZUGFeRD invoice, for the XMP metadata. Its XML is one of the <see cref="Attachments"/>.</summary>
    public PdfEInvoice? EInvoice { get; init; }

    /// <summary>Files to embed, sorted by name and with unique names (see <see cref="EmbeddedFiles.Prepare"/>).</summary>
    public IReadOnlyList<PdfAttachment> Attachments { get; init; } = Array.Empty<PdfAttachment>();

    /// <summary>Font for the watermark text. Default: Helvetica (bold when the watermark asks for it).</summary>
    public TextFont? WatermarkFont { get; init; }

    /// <summary>The watermark image when it is an SVG file, parsed with the document's fonts.</summary>
    public SvgImage? WatermarkSvg { get; init; }

    /// <summary>Font for <c>HeaderText</c> and <c>FooterText</c>. Default: Helvetica.</summary>
    public TextFont? MarginFont { get; init; }

    /// <summary>Draw text in the standard fonts with the bundled, embedded Liberation fonts (always on for PDF/A).</summary>
    public bool EmbedStandardFonts { get; init; }

    /// <summary>The most pages the document may have.</summary>
    public int MaxPages { get; init; } = int.MaxValue;

    /// <summary>Stops writing when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>Keywords, Creator, Producer and custom entries for the document information dictionary (and the XMP for PDF/A).</summary>
    public DocumentProperties Properties { get; init; } = new();

    /// <summary>Write a tagged PDF with this logical structure. Default: null, untagged.</summary>
    public StructureTree? Structure { get; init; }

    /// <summary>Claim PDF/UA-1 (1) or PDF/UA-2 (2) conformance in the metadata; 0 claims neither. Needs <see cref="Structure"/>.</summary>
    public int PdfUAPart { get; init; }

    private bool PdfUA => PdfUAPart > 0;

    /// <summary>PDF/A-4 and PDF/UA-2 are based on PDF 2.0: the file is written as PDF 2.0.</summary>
    private bool Pdf20 => PdfA.IsPdf20(Conformance) || PdfUAPart >= 2;

    /// <summary>The document's natural language, such as "en-US", for a tagged PDF.</summary>
    public string? Language { get; init; }

    /// <summary>Called once with each character no font can draw; see <see cref="PdfOptions.MissingCharacter"/>.</summary>
    public Action<string>? MissingCharacter { get; init; }

    private HashSet<int>? _missingReported;

    private bool EmbedStandard => EmbedStandardFonts || Conformance != PdfConformance.None || PdfUA;

    /// <summary>PDF/A and PDF/UA forbid showing the .notdef glyph.</summary>
    private bool NoNotdef => Conformance != PdfConformance.None || PdfUA;

    // Tagged PDF: the stream being written, the open marked-content sequence (a ContentSlot or Artifact) and whether
    // everything is an artifact for now (running headers and footers, fixed boxes, the page canvas).
    private McStream? _mc;
    private object? _mcOpen;
    private bool _forceArtifact;
    private static readonly object Artifact = new();
    private readonly List<McStream?> _formStreams = new(); // by form, the marked content of tagged transparency groups

    /// <summary>Starts the marked-content sequence for content of <paramref name="slot"/> (an artifact when null), unless it is open.</summary>
    private void Mark(ByteBuffer b, ContentSlot? slot)
    {
        if (_mc == null) return;
        object want = _forceArtifact || slot == null ? Artifact : slot;
        if (ReferenceEquals(_mcOpen, want)) return;
        EndMarked(b);
        if (want is ContentSlot s)
        {
            int mcid = _mc.Elements.Count;
            _mc.Elements.Add(s.Parent);
            s.Refs.Add((_mc, mcid));
            b.Ascii("/").Ascii(s.Parent.Type).Ascii(" <</MCID ").Int(mcid).Ascii(">> BDC\n");
        }
        else b.Ascii("/Artifact BMC\n");
        _mcOpen = want;
    }

    /// <summary>Closes the open marked-content sequence, and the text object inside it.</summary>
    private void EndMarked(ByteBuffer b)
    {
        if (_mcOpen == null) return;
        EndText(b);
        b.Ascii("EMC\n");
        _mcOpen = null;
    }

    /// <summary>The font that draws a run: standard fonts become their bundled replacements when they must be embedded.</summary>
    private TextFont Leaf(TextFont font)
        => font is StandardFont sf && EmbedStandard && BundledFonts.Get(sf) is { } bundled ? bundled : font;

    private PdfEncryptor? _crypt;

    public PdfDocumentWriter(PageSetup page, bool compress, CompressionLevel level)
    {
        _page = page;
        _compress = compress;
        _level = level;
    }

    private readonly struct Placed
    {
        public readonly DrawOp Op;
        public readonly float Y0, Y1; // clipped vertical extent for split rectangles and lines (flow coordinates)
        public Placed(DrawOp op, float y0, float y1) { Op = op; Y0 = y0; Y1 = y1; }
    }

    /// <summary>The part of a transformed or translucent layer that falls on one page, drawn with the layer's transform and opacity.</summary>
    private sealed class LayerBlock : DrawOp
    {
        public readonly LayerOp Layer;
        public readonly List<Placed> Items;
        public LayerBlock(LayerOp layer, List<Placed> items) { Layer = layer; Items = items; }
        public override void Offset(float dx, float dy) => throw new NotSupportedException();
    }

    /// <summary>The part of a clipped box's content that falls on one page, drawn inside the clip.</summary>
    private sealed class ClipBlock : DrawOp
    {
        public readonly ClipOp Clip;
        public readonly List<Placed> Items;
        public ClipBlock(ClipOp clip, List<Placed> items) { Clip = clip; Items = items; }
        public override void Offset(float dx, float dy) => throw new NotSupportedException();
    }

    /// <summary>A form field on one page: its widget's appearance, or, where it cannot be a widget, a drawing of it.</summary>
    private sealed class FieldBlock : DrawOp
    {
        public readonly FieldOp Field;
        public readonly List<Placed> Items;
        public FieldBlock(FieldOp field, List<Placed> items) { Field = field; Items = items; }
        public override void Offset(float dx, float dy) => throw new NotSupportedException();
    }

    /// <summary>Flattens an op for a list that is drawn as it is, without page splitting: layers, clips and fields become blocks.</summary>
    private static void Flatten(DrawOp op, List<Placed> list)
    {
        switch (op)
        {
            case LayerOp { NeedsBlock: true } layer:
            {
                var items = new List<Placed>();
                foreach (var o in layer.Ops) Flatten(o, items);
                list.Add(new Placed(new LayerBlock(layer, items), 0, 0));
                break;
            }
            case ClipOp clip:
            {
                var items = new List<Placed>();
                foreach (var o in clip.Ops) Flatten(o, items);
                list.Add(new Placed(new ClipBlock(clip, items), 0, 0));
                break;
            }
            case FieldOp field:
            {
                var items = new List<Placed>();
                foreach (var o in field.Ops) Flatten(o, items);
                list.Add(new Placed(new FieldBlock(field, items), field.Y, field.Y + field.H));
                break;
            }
            case GroupOp g: foreach (var o in g.Ops) Flatten(o, list); break;
            case RectOp r: if (r.W > 0 && r.H > 0) list.Add(new Placed(r, r.Y, r.Y + r.H)); break;
            case BorderRectOp br: list.Add(new Placed(br, br.Y, br.Y + br.H)); break;
            case RoundRectOp rr: list.Add(new Placed(rr, rr.Y, rr.Y + rr.H)); break;
            case BackgroundOp bg:
            {
                var (_, y0, _, y1) = bg.Painted();
                list.Add(new Placed(bg, y0, y1));
                break;
            }
            case LineOp l: list.Add(new Placed(l, l.Y1, l.Y2)); break;
            case LinkOp lk: list.Add(new Placed(lk, lk.Y, lk.Y + lk.H)); break;
            default: list.Add(new Placed(op, 0, 0)); break;
        }
    }

    /// <summary>
    /// The form fields of a page's flowing content: at the top level, or inside a clip or a layer that is not transformed.
    /// The others are drawn, not made into widgets.
    /// </summary>
    private static void CollectFields(List<Placed> items, List<FieldBlock> fields)
    {
        foreach (var p in items)
            switch (p.Op)
            {
                case FieldBlock fb: fields.Add(fb); break;
                case LayerBlock { Layer.Transform: null } block: CollectFields(block.Items, fields); break;
                case ClipBlock clip: CollectFields(clip.Items, fields); break;
            }
    }

    /// <summary>
    /// The links inside a translucent layer that is not transformed, or inside a clip: they stay where they are on the page.
    /// </summary>
    private static void AddUntransformedLinks(Placed placed, List<(Placed Link, float Origin, float PageTop)> links, float origin, float pageTop)
    {
        var items = placed.Op switch
        {
            LayerBlock { Layer.Transform: null } block => block.Items,
            ClipBlock clip => clip.Items,
            _ => null,
        };
        if (items == null) return;
        foreach (var p in items)
        {
            if (p.Op is LinkOp) links.Add((p, origin, pageTop));
            else AddUntransformedLinks(p, links, origin, pageTop);
        }
    }

    /// <summary>The ops to draw, looking inside transformed and translucent layers.</summary>
    private static void ForEachLeaf(List<Placed> list, Action<Placed> visit)
    {
        foreach (var p in list)
        {
            if (p.Op is LayerBlock tb) ForEachLeaf(tb.Items, visit);
            else if (p.Op is ClipBlock cb) ForEachLeaf(cb.Items, visit);
            else if (p.Op is FieldBlock fb) ForEachLeaf(fb.Items, visit);
            else visit(p);
        }
    }

    public byte[] Write(List<DrawOp> ops, float contentHeight, IReadOnlyDictionary<string, float> anchors, uint pageBackground,
        string? title, string? author, string? subject, string? footerTemplate, string? headerTemplate,
        Func<int, int, List<DrawOp>>? pageBands = null, IReadOnlyList<OutlineHeading>? outline = null, List<DrawOp>? fixedOps = null,
        Func<int, List<DrawOp>>? pageCanvas = null)
        => Write(new[] { new PageSection { Page = _page, Ops = ops, ContentHeight = contentHeight, Anchors = anchors, FixedOps = fixedOps, Canvas = pageCanvas } },
            pageBackground, title, author, subject, footerTemplate, headerTemplate, pageBands, outline);

    /// <summary>
    /// Writes the sections' pages in order. <paramref name="pageBands"/> gets the page's index in the whole document;
    /// the outline's keys are looked up in the sections' anchors.
    /// </summary>
    public byte[] Write(IReadOnlyList<PageSection> sections, uint pageBackground,
        string? title, string? author, string? subject, string? footerTemplate, string? headerTemplate,
        Func<int, int, List<DrawOp>>? pageBands = null, IReadOnlyList<OutlineHeading>? outline = null)
    {
        var firstSetup = _page = sections[0].Grid.SetupOf(0);
        var grid = sections[0].Grid;
        int pageOffset = 0; // pages of the sections before the one being distributed
        int maxPages = Math.Max(1, MaxPages);
        int PageOf(float y)
        {
            // Compare as doubles: content far down the flow (a huge height or margin) must hit the page limit, not
            // overflow the page number.
            double p = grid.PageOf(y + 0.01);
            if (p + pageOffset >= maxPages || double.IsNaN(p)) throw TooManyPages(maxPages);
            return Math.Max(0, (int)p);
        }

        // 1. Distribute ops over pages.
        var pages = new List<List<Placed>>();
        Func<int, float>? footnoteCut = null; // the space footnote areas take at the bottom of the pages being distributed
        List<Placed> PageList(int p)
        {
            // Sized like the page before: pages of a document tend to hold about as many ops each.
            while (pages!.Count <= p) pages.Add(new List<Placed>(pages.Count > 0 ? Math.Max(16, pages[^1].Count) : 16));
            return pages[p];
        }
        // The list of a page: the document's own, or, for a transformed layer, the layer's part on that page.
        List<Placed> Target(SortedDictionary<int, List<Placed>>? into, int p)
        {
            if (into == null) return PageList(pageOffset + p);
            if (!into.TryGetValue(p, out var list)) into[p] = list = new List<Placed>();
            return list;
        }
        void AddSplit(DrawOp op, float top, float bottom, SortedDictionary<int, List<Placed>>? into)
        {
            if (bottom < top) (top, bottom) = (bottom, top);
            int p0 = PageOf(top), p1 = Math.Max(p0, PageOf(bottom - 0.5f));
            for (int p = p0; p <= p1; p++)
            {
                float y0 = Math.Max(top, grid.Start(p)), y1 = Math.Min(bottom, grid.Start(p + 1) - (footnoteCut?.Invoke(p) ?? 0));
                if (y1 > y0 || top == bottom) Target(into, p).Add(new Placed(op, y0, y1));
            }
        }
        void Distribute(DrawOp op, SortedDictionary<int, List<Placed>>? into)
        {
            switch (op)
            {
                case LayerOp { NeedsBlock: true } layer:
                {
                    // Each page gets the part of the layer that falls on it, drawn with the whole layer's transform.
                    var parts = new SortedDictionary<int, List<Placed>>();
                    foreach (var o in layer.Ops) Distribute(o, parts);
                    foreach (var (p, items) in parts) Target(into, p).Add(new Placed(new LayerBlock(layer, items), 0, 0));
                    break;
                }
                case ClipOp clip:
                {
                    // Content hidden beyond the clip's last page must not add pages of its own.
                    var parts = new SortedDictionary<int, List<Placed>>();
                    foreach (var o in clip.Ops) Distribute(o, parts);
                    bool clipsY = clip.H < ClipOp.Far;
                    int first = clipsY ? PageOf(clip.Y) : 0, last = clipsY ? Math.Max(first, PageOf(clip.Y + clip.H - 0.5f)) : int.MaxValue;
                    foreach (var (p, items) in parts)
                        if (p >= first && p <= last) Target(into, p).Add(new Placed(new ClipBlock(clip, items), 0, 0));
                    break;
                }
                case FieldOp field:
                {
                    // A field stays whole, on the page where it starts.
                    var items = new List<Placed>();
                    foreach (var o in field.Ops) Flatten(o, items);
                    Target(into, PageOf(field.Y)).Add(new Placed(new FieldBlock(field, items), field.Y, field.Y + field.H));
                    break;
                }
                case GroupOp g:
                    foreach (var o in g.Ops) Distribute(o, into);
                    break;
                case RectOp r:
                    if (r.W > 0 && r.H > 0) AddSplit(r, r.Y, r.Y + r.H, into);
                    break;
                case RoundRectOp rr:
                    if (rr.W > 0 && rr.H > 0) AddSplit(rr, rr.Y, rr.Y + rr.H, into); // only the outer shape is painted
                    break;
                case BorderRectOp br:
                    if (PageOf(br.Y) == PageOf(br.Y + br.H - 0.5f)) Target(into, PageOf(br.Y)).Add(new Placed(br, br.Y, br.Y + br.H));
                    else foreach (var side in br.Sides()) Distribute(side, into); // a box split by a page break is open at the break
                    break;
                case BackgroundOp bg:
                {
                    var (x0, y0, x1, y1) = bg.Painted();
                    if (x1 > x0 && y1 > y0) AddSplit(bg, y0, y1, into);
                    break;
                }
                case LineOp l:
                    if (l.X1 == l.X2) AddSplit(l, l.Y1, l.Y2, into);
                    else Target(into, PageOf(l.Y1)).Add(new Placed(l, l.Y1, l.Y2));
                    break;
                case TextOp t:
                    Target(into, PageOf(t.LineTop)).Add(new Placed(t, 0, 0));
                    break;
                case ImageOp im:
                    Target(into, PageOf(im.Y)).Add(new Placed(im, 0, 0));
                    break;
                case SvgOp sv:
                    Target(into, PageOf(sv.Y)).Add(new Placed(sv, 0, 0));
                    break;
                case LinkOp lk:
                    if (lk.W > 0 && lk.H > 0) AddSplit(lk, lk.Y, lk.Y + lk.H, into); // a link box split by a page break is clickable on both pages
                    break;
                case CircleOp c:
                    Target(into, PageOf(c.Cy - c.R)).Add(new Placed(c, 0, 0));
                    break;
            }
        }
        // Each section's pages follow the last section's; the per-page arrays say which section a page is in and
        // where in that section's flow it starts. Anchors go into one space: a section's flow y plus the flow length
        // of the sections before it.
        var pageSection = new List<int>();
        var sectionFirst = new int[sections.Count];
        var sectionBase = new float[sections.Count + 1];
        for (int si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            grid = section.Grid;
            pageOffset = sectionFirst[si] = pageSection.Count;
            footnoteCut = section.FootnoteReserve;
            foreach (var op in section.Ops) Distribute(op, null);
            footnoteCut = null;
            if (section.FootnoteOps != null) foreach (var op in section.FootnoteOps) Distribute(op, null);
            int sectionPages = Math.Max(pages.Count - pageOffset, Math.Max(1, PageOf(Math.Max(0, section.ContentHeight - 0.5f)) + 1));
            while (pages.Count < pageOffset + sectionPages) pages.Add(new List<Placed>());
            for (int k = 0; k < sectionPages; k++) pageSection.Add(si);
            sectionBase[si + 1] = sectionBase[si] + grid.Start(sectionPages);
        }
        int pageCount = pages.Count;
        _page = firstSetup;
        _sections = sections;
        _pageSection = pageSection.ToArray();
        _sectionFirst = sectionFirst;
        _sectionBase = sectionBase;
        IReadOnlyDictionary<string, float> anchors = sections[0].Anchors;
        if (sections.Count > 1)
        {
            var merged = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int si = 0; si < sections.Count; si++)
                foreach (var (key, y) in sections[si].Anchors) merged.TryAdd(key, sectionBase[si] + y);
            anchors = merged;
        }

        // Running headers and footers, laid out per page now that the page count is known. Their ops use y from the
        // top of the content area.
        // The canvas background of each page goes first, also in y from the top of the content area.
        var bands = new List<Placed>[pageCount];
        var canvas = new List<Placed>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            var list = bands[p] = new List<Placed>();
            canvas[p] = new List<Placed>();
            var section = sections[_pageSection[p]];
            var fixedOps = section.FixedOps;
            if (pageBands == null && fixedOps == null && section.Canvas == null) continue;
            if (section.Canvas != null) foreach (var op in section.Canvas(p - _sectionFirst[_pageSection[p]])) Flatten(op, canvas[p]);
            if (pageBands != null) foreach (var op in pageBands(p, pageCount)) Flatten(op, list);
            // position: fixed boxes repeat on every page, as in Chrome's print output.
            if (fixedOps != null) foreach (var op in fixedOps) Flatten(op, list);
        }

        // 2. Collect resources.
        var usedFonts = new bool[StandardFont.All.Length];
        var images = new List<PdfImage>();
        var imageIds = new Dictionary<PdfImage, int>();
        void CollectResources(Placed p)
        {
            if (p.Op is BackgroundOp { Gradient: { Conic: true, FunctionId: 0 } conic })
            {
                conic.FunctionId = -1; // numbered with the other objects below
                _conics.Add(conic);
            }
            if (p.Op is TextOp t) CollectFonts(t, usedFonts);
            else if (p.Op is SvgOp sv) CollectSvgFonts(sv.Image, usedFonts);
            else if (p.Op is BackgroundOp { Svg: { } bgSvg }) CollectSvgFonts(bgSvg, usedFonts);
            else if (p.Op is ImageOp im && !imageIds.ContainsKey(im.Image))
            {
                imageIds[im.Image] = images.Count + 1;
                images.Add(im.Image);
            }
            else if (p.Op is BackgroundOp { Image: { } bgImage } && !imageIds.ContainsKey(bgImage))
            {
                imageIds[bgImage] = images.Count + 1;
                images.Add(bgImage);
            }
        }
        Action<Placed> collectResources = CollectResources;
        foreach (var page in pages) ForEachLeaf(page, collectResources);
        foreach (var page in bands) ForEachLeaf(page, collectResources);
        foreach (var page in canvas) ForEachLeaf(page, collectResources);
        bool hasMargins = footerTemplate != null || headerTemplate != null;
        var marginFont = MarginFont ?? StandardFont.Get(FontFamily.Helvetica, false, false);
        if (hasMargins)
        {
            // Page numbers are only known per page: collect every digit.
            foreach (var template in new[] { headerTemplate, footerTemplate })
                if (template != null) CollectFonts(new TextOp(0, 0, 0, marginFont, MarginTextSize, 0, template + "0123456789", 0, 0), usedFonts);
        }

        var wm = Watermark;
        (List<TextOp> Lines, float Cos, float Sin)? wmText = null;
        PdfImage? wmImage = null;
        SvgImage? wmSvg = null;
        if (wm != null)
        {
            if (wm.Image != null && (wmImage = PdfImage.FromBytes(wm.Image)) == null)
            {
                wmSvg = WatermarkSvg ?? throw new ArgumentException("The watermark image is not a PNG, JPEG, GIF, WebP, BMP, TIFF or SVG image this library can read.");
                CollectSvgFonts(wmSvg, usedFonts);
            }
            if (wmImage != null)
            {
                if (!imageIds.ContainsKey(wmImage))
                {
                    imageIds[wmImage] = images.Count + 1;
                    images.Add(wmImage);
                }
            }
            if (!string.IsNullOrEmpty(wm.Text))
            {
                wmText = LayoutWatermarkText(wm, WatermarkFont ?? StandardFont.Get(FontFamily.Helvetica, wm.Bold, false));
                foreach (var line in wmText.Value.Lines) CollectFonts(line, usedFonts);
            }
            if (wmText == null && wmImage == null && wmSvg == null) wm = null;
        }

        bool pdfa = Conformance != PdfConformance.None;
        if (pdfa)
        {
            // An sRGB output intent does not cover DeviceCMYK: convert CMYK images.
            for (int i = 0; i < images.Count; i++)
            {
                var rgb = images[i].ToRgb();
                if (rgb == null || ReferenceEquals(rgb, images[i])) continue;
                foreach (var key in imageIds.Where(kv => ReferenceEquals(kv.Key, images[i])).Select(kv => kv.Key).ToList())
                    imageIds[rgb] = imageIds[key];
                images[i] = rgb;
            }
        }
        if (Security != null) _crypt = new PdfEncryptor(Security);
        _imageList = images;

        // 3. Object numbers.
        int next = 1;
        // PDF 2.0 deprecates the document information dictionary (PDF/A-4 allows only its ModDate): the XMP metadata
        // carries the properties instead.
        int catalogId = next++, pagesId = next++, resourcesId = next++, infoId = Pdf20 ? 0 : next++;
        _resourcesId = resourcesId;
        var fontIds = new int[usedFonts.Length];
        for (int i = 0; i < usedFonts.Length; i++) if (usedFonts[i]) fontIds[i] = next++;
        int firstEmbeddedId = next;
        next += _subsetList.Count * 5; // Type0 font, CIDFont, descriptor, font file, ToUnicode CMap
        foreach (var img in images)
        {
            img.ObjectNumber = next++;
            if (img.SMask != null) img.SMask.ObjectNumber = next++;
        }
        var pageIds = new int[pageCount];
        var contentIds = new int[pageCount];
        for (int i = 0; i < pageCount; i++) { pageIds[i] = next++; contentIds[i] = next++; }
        _pageIds = pageIds;
        var outlineItems = BuildOutline(outline, anchors);
        int outlinesId = 0;
        if (outlineItems.Count > 0)
        {
            outlinesId = next++;
            foreach (var item in outlineItems) item.Id = next++;
        }
        int watermarkId = wm != null ? next++ : 0;
        int watermarkSvgId = wmSvg != null ? next++ : 0;
        int metadataId = 0, outputIntentId = 0, iccId = 0, encryptId = 0;
        if (pdfa) { metadataId = next++; outputIntentId = next++; iccId = next++; }
        var fileSpecIds = new int[Attachments.Count];
        for (int i = 0; i < fileSpecIds.Length; i++) { fileSpecIds[i] = next; next += 2; } // file specification, embedded file
        if (_crypt != null) encryptId = next++;
        bool tagged = Structure != null;
        int structRootId = tagged ? next++ : 0;
        if (PdfUA && metadataId == 0) metadataId = next++;
        var pageFields = AllocateFields(pages, pageCount, ref next);
        foreach (var conic in _conics)
        {
            conic.FunctionId = next++;
            if (conic.Alphas != null) conic.AlphaFunctionId = next++;
        }

        // 4. Header.
        _out.Ascii(Pdf20 ? "%PDF-2.0\n%" : pdfa || _crypt != null || tagged ? "%PDF-1.7\n%" : "%PDF-1.4\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        // Catalog, pages tree.
        Begin(catalogId).Ascii("<< /Type /Catalog /Pages ").Int(pagesId).Ascii(" 0 R");
        if (outlinesId != 0) _out.Ascii(" /Outlines ").Int(outlinesId).Ascii(" 0 R");
        if (metadataId != 0) _out.Ascii(" /Metadata ").Int(metadataId).Ascii(" 0 R");
        if (pdfa) _out.Ascii(" /OutputIntents [").Int(outputIntentId).Ascii(" 0 R]");
        if (tagged)
        {
            _out.Ascii(" /MarkInfo << /Marked true >> /StructTreeRoot ").Int(structRootId).Ascii(" 0 R");
            if (!string.IsNullOrEmpty(Language)) { _out.Ascii(" /Lang "); WriteTextString(_out, Language!); }
        }
        // PDF/UA: viewers show the title, not the file name. A right-to-left document's pages progress from right to
        // left, which a viewer showing two pages side by side needs to know to put them in the right order.
        bool displayTitle = tagged && !string.IsNullOrEmpty(title);
        if (displayTitle || RightToLeft)
            _out.Ascii(" /ViewerPreferences <<").Ascii(displayTitle ? " /DisplayDocTitle true" : "").Ascii(RightToLeft ? " /Direction /R2L" : "").Ascii(" >>");
        if (fileSpecIds.Length > 0)
        {
            // Attachments are listed in the name tree viewers show and are associated files (/AF), as PDF/A-3 needs.
            _out.Ascii(" /Names << /EmbeddedFiles << /Names [");
            for (int i = 0; i < fileSpecIds.Length; i++)
            {
                if (i > 0) _out.Sp();
                WriteTextString(_out, Attachments[i].FileName);
                _out.Sp().Int(fileSpecIds[i]).Ascii(" 0 R");
            }
            _out.Ascii("] >> >> /AF [");
            for (int i = 0; i < fileSpecIds.Length; i++) _out.Ascii(i > 0 ? " " : "").Int(fileSpecIds[i]).Ascii(" 0 R");
            _out.Ascii("]");
        }
        WriteAcroForm();
        // AES-256 (revision 6) is the Adobe extension level 8 to PDF 1.7, standardised in PDF 2.0.
        if (_crypt != null && !Pdf20) _out.Ascii(" /Extensions << /ADBE << /BaseVersion /1.7 /ExtensionLevel 8 >> >>");
        _out.Ascii(" >>");
        End();
        Begin(pagesId).Ascii("<< /Type /Pages /Count ").Int(pageCount).Ascii(" /Kids [");
        for (int i = 0; i < pageCount; i++) _out.Int(pageIds[i]).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();

        // Info.
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var props = Properties.WithStandard(title, author, subject);
        if (metadataId != 0) props = props.Cleaned(); // Info and XMP must agree, and XML cannot carry control characters.
        if (infoId != 0)
        {
            Begin(infoId);
            props.WriteInfo(_out, now, modDate: metadataId != 0, WriteTextString);
            End();
        }

        if (metadataId != 0)
        {
            var (part, level, rev) = PdfA.Identification(Conformance);
            var xmp = PdfA.Xmp(part, level, props, now, EInvoice, PdfUAPart, rev);
            WriteStream(metadataId, xmp, compress: false, "/Type /Metadata /Subtype /XML");
        }
        if (pdfa)
        {
            Begin(outputIntentId).Ascii("<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier ");
            WriteTextString(_out, PdfA.OutputCondition);
            _out.Ascii(" /Info ");
            WriteTextString(_out, PdfA.OutputCondition);
            _out.Ascii(" /DestOutputProfile ").Int(iccId).Ascii(" 0 R >>");
            End();
            WriteStream(iccId, PdfA.SrgbProfile, _compress, "/N 3");
        }
        for (int i = 0; i < fileSpecIds.Length; i++) WriteAttachment(Attachments[i], fileSpecIds[i], now);

        // Fonts.
        for (int i = 0; i < usedFonts.Length; i++)
        {
            if (!usedFonts[i]) continue;
            Begin(fontIds[i]).Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /").Ascii(StandardFont.All[i].BaseFont)
                .Ascii(" /Encoding /WinAnsiEncoding >>");
            End();
        }
        for (int i = 0; i < _subsetList.Count; i++) WriteEmbeddedFont(_subsetList[i], firstEmbeddedId + i * 5);

        // Images.
        foreach (var img in images)
        {
            WriteImage(img);
            if (img.SMask != null) WriteImage(img.SMask);
        }

        if (wm != null)
        {
            var fontDict = new ByteBuffer(128);
            for (int i = 0; i < usedFonts.Length; i++)
                if (usedFonts[i]) fontDict.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
            for (int i = 0; i < _subsetList.Count; i++) fontDict.Ascii(" /E").Int(i + 1).Sp().Int(firstEmbeddedId + i * 5).Ascii(" 0 R");
            WriteWatermark(watermarkId, wm, wmText, fontDict, wmImage, wmSvg, watermarkSvgId);
        }

        // Pages, contents, annotations. A scaled page is written at the size Chrome gives the paper (see PaperSize).
        var content = new ByteBuffer(32 * 1024);
        var annotations = new List<(int Id, Placed Link, float Origin, float PageTop, int Page)>();
        var pageStreams = new McStream[pageCount];
        int parentKey = pageCount; // keys 0 to pageCount - 1 of the parent tree are the pages'
        var annotKeys = new Dictionary<int, int>();
        for (int p = 0; p < pageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            content.Clear();
            ResetState();
            float paperW = SetPage(p, out float paperH), paperDy = _paperDy, pageTop = PageTop(p);
            if (tagged) { _mc = pageStreams[p] = new McStream(p); _mcOpen = null; }
            if (_page.Scale != 1)
            {
                // Num keeps 3 decimals, too few for a scale such as 0.99916. The content keeps to the top of the page.
                string k = _page.Scale.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                content.Ascii("q ").Ascii(k).Ascii(" 0 0 ").Ascii(k).Ascii(" 0 ").Num(paperDy).Ascii(" cm\n");
            }
            // A named page's own @page background, or the document's.
            uint background = _sections.Count > 1 || _sections[0].Grid.Varies ? _page.Background : pageBackground;
            if (CssColor.IsVisible(background))
            {
                Mark(content, null);
                SetFill(content, background);
                content.Ascii("0 0 ").Num(_page.Width).Sp().Num(_page.Height).Ascii(" re f\n");
            }
            _yOrigin = _page.MarginTop;
            _forceArtifact = true;
            foreach (var placed in canvas[p]) WriteOp(content, placed, 0, imageIds);
            _forceArtifact = false;
            if (wm is { Behind: true }) { Mark(content, null); content.Ascii(WatermarkDo(firstSetup)); }
            var pageLinks = new List<(Placed Link, float Origin, float PageTop)>();
            _yOrigin = _page.MarginTop + _page.BandTop;
            (_flowTop, _flowBottom) = (pageTop, pageTop + _page.FlowHeight);
            foreach (var placed in pages[p])
            {
                if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, pageTop)); continue; }
                AddUntransformedLinks(placed, pageLinks, _yOrigin, pageTop);
                WriteOp(content, placed, pageTop, imageIds);
            }
            _flowTop = _flowBottom = float.NaN;
            _yOrigin = _page.MarginTop;
            _forceArtifact = true;
            foreach (var placed in bands[p])
            {
                if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, 0)); continue; }
                AddUntransformedLinks(placed, pageLinks, _yOrigin, 0);
                WriteOp(content, placed, 0, imageIds);
            }
            _forceArtifact = false;
            if (hasMargins)
            {
                Mark(content, null);
                EndText(content);
                if (headerTemplate != null) WriteMarginText(content, marginFont, headerTemplate, p + 1, pageCount, top: true);
                if (footerTemplate != null) WriteMarginText(content, marginFont, footerTemplate, p + 1, pageCount, top: false);
            }
            EndText(content);
            if (wm is { Behind: false }) { Mark(content, null); content.Ascii(WatermarkDo(firstSetup)); }
            EndMarked(content);
            if (_page.Scale != 1) content.Ascii("Q\n");

            var annotIds = new List<int>();
            foreach (var (lk, origin, linkTop) in pageLinks)
            {
                int id = next++;
                annotIds.Add(id);
                annotations.Add((id, lk, origin, linkTop, p));
                if (tagged)
                {
                    // A link annotation belongs to its Link element; one outside the structure (in a running header,
                    // say) gets a Link element of its own at the end.
                    var linkOp = (LinkOp)lk.Op;
                    var link = linkOp.Link ?? new StructElem("Link", Structure!.Root) { Text = linkOp.Target };
                    link.Kids.Add(new AnnotRef(id, p));
                    _mc!.Annotations.Add((id, link));
                    annotKeys[id] = parentKey++;
                }
            }
            foreach (var field in pageFields[p])
            {
                int id = _fieldIds[field];
                annotIds.Add(id);
                if (tagged)
                {
                    // A widget belongs to its Form element; one outside the structure gets a Form element of its own.
                    var f = field.Field;
                    var form = f.Form ?? new StructElem("Form", Structure!.Root) { Alt = f.Field.Tooltip ?? f.Field.Name };
                    form.Kids.Add(new AnnotRef(id, p));
                    _mc!.Annotations.Add((id, form));
                    annotKeys[id] = parentKey++;
                }
            }

            Begin(pageIds[p]).Ascii("<< /Type /Page /Parent ").Int(pagesId).Ascii(" 0 R /MediaBox [0 0 ")
                .Num(paperW).Sp().Num(paperH).Ascii("] /Resources ").Int(resourcesId).Ascii(" 0 R /Contents ")
                .Int(contentIds[p]).Ascii(" 0 R");
            if (annotIds.Count > 0)
            {
                _out.Ascii(" /Annots [");
                foreach (var id in annotIds) _out.Int(id).Ascii(" 0 R ");
                _out.Ascii("]");
            }
            if (tagged)
            {
                if (_mc!.Elements.Count > 0) _out.Ascii(" /StructParents ").Int(p);
                _out.Ascii(" /Tabs /S"); // PDF/UA: annotations in structure order
            }
            _out.Ascii(" >>");
            End();

            WriteStream(contentIds[p], content.Span, _compress, extraDict: null);
        }

        // The structure elements get their object numbers now: PDF 2.0 links and bookmarks refer to them.
        var structElements = tagged ? NumberStructure(ref next) : null;

        foreach (var (id, placed, origin, linkPageTop, linkPage) in annotations)
        {
            SetPage(linkPage, out _);
            float paperDy = _paperDy;
            var link = (LinkOp)placed.Op;
            float x1 = _page.MarginLeft + link.X, x2 = x1 + link.W;
            float yTop = _page.Height - origin - (placed.Y0 - linkPageTop);
            float yBottom = yTop - (placed.Y1 - placed.Y0);
            float k = _page.Scale;
            x1 *= k; x2 *= k; yTop = yTop * k + paperDy; yBottom = yBottom * k + paperDy;
            Begin(id).Ascii("<< /Type /Annot /Subtype /Link /Rect [").Num(x1).Sp().Num(yBottom).Sp().Num(x2).Sp().Num(yTop)
                .Ascii("] /F 4 /Border [0 0 0] ");
            if (annotKeys.TryGetValue(id, out int key))
            {
                // PDF/UA: a link's description, for assistive technology.
                var text = (link.Link?.Text is { Length: > 0 } t ? t : null) ?? link.Target;
                _out.Ascii("/StructParent ").Int(key).Ascii(" /Contents ");
                WriteTextString(_out, text);
                _out.Sp();
            }
            if (link.Target.StartsWith("#", StringComparison.Ordinal))
            {
                string anchor = Uri.UnescapeDataString(link.Target.Substring(1));
                if (!anchors.TryGetValue(anchor, out float ay) && anchors.TryGetValue(link.Target.Substring(1), out ay))
                    anchor = link.Target.Substring(1);
                if (anchors.ContainsKey(anchor)) WriteGoTo(ay, Structure?.TargetOf(anchor));
            }
            else
            {
                _out.Ascii("/A << /S /URI /URI ");
                WriteAsciiString(_out, link.Target);
                _out.Ascii(" >>");
            }
            _out.Ascii(" >>");
            End();
        }

        WriteFields(pageFields, pageIds, imageIds, annotKeys, ref next);
        SetPage(0, out _);

        // Bookmarks.
        if (outlinesId != 0)
        {
            var top = outlineItems.Where(o => o.Parent == null).ToList();
            Begin(outlinesId).Ascii("<< /Type /Outlines /First ").Int(top[0].Id).Ascii(" 0 R /Last ").Int(top[^1].Id)
                .Ascii(" 0 R /Count ").Int(outlineItems.Count).Ascii(" >>");
            End();
            foreach (var item in outlineItems)
            {
                Begin(item.Id).Ascii("<< /Title ");
                WriteTextString(_out, item.Title);
                _out.Ascii(" /Parent ").Int(item.Parent?.Id ?? outlinesId).Ascii(" 0 R");
                if (item.Prev != null) _out.Ascii(" /Prev ").Int(item.Prev.Id).Ascii(" 0 R");
                if (item.Next != null) _out.Ascii(" /Next ").Int(item.Next.Id).Ascii(" 0 R");
                if (item.Children.Count > 0)
                    _out.Ascii(" /First ").Int(item.Children[0].Id).Ascii(" 0 R /Last ").Int(item.Children[^1].Id)
                        .Ascii(" 0 R /Count ").Int(item.Descendants);
                _out.Sp();
                WriteGoTo(item.Y, item.Heading is { } h ? Structure?.ElementOf(h) : null);
                _out.Ascii(" >>");
                End();
            }
        }

        if (_crypt != null)
        {
            Begin(encryptId);
            _crypt.WriteDictionary(_out);
            End();
        }

        foreach (var conic in _conics)
        {
            WriteStream(conic.FunctionId, conic.ConicFunction(gray: false), _compress, Gradient.ConicFunctionDict(gray: false));
            if (conic.AlphaFunctionId != 0) WriteStream(conic.AlphaFunctionId, conic.ConicFunction(gray: true), _compress, Gradient.ConicFunctionDict(gray: true));
        }

        // Tiling patterns of repeated backgrounds, collected while writing the pages.
        foreach (var (n, tileContent, tileDict) in _tilings)
        {
            int id = next++;
            WriteStream(id, tileContent, _compress, tileDict);
            _patterns.Ascii(" /T").Int(n).Sp().Int(id).Ascii(" 0 R");
        }

        // Transparency groups of translucent layers. They share the page resources.
        var formIds = new int[_forms.Count];
        for (int i = 0; i < _forms.Count; i++)
        {
            formIds[i] = next++;
            string structParents = "";
            if (_formStreams.Count > i && _formStreams[i] is { Elements.Count: > 0 } fs)
            {
                fs.Key = parentKey++;
                fs.FormId = formIds[i];
                structParents = " /StructParents " + fs.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            WriteStream(formIds[i], _forms[i], _compress, "/Type /XObject /Subtype /Form /BBox [-32767 -32767 32767 32767] "
                + (_formIsolated[i] ? "/Group << /S /Transparency /I true >> /Resources " : "/Group << /S /Transparency >> /Resources ")
                + resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R"
                + structParents);
        }
        if (tagged)
        {
            _mc = null;
            next = WriteStructure(next, structElements!, structRootId, parentKey, pageIds, pageStreams, annotKeys);
        }

        var maskIds = new int[_masks.Count];
        for (int i = 0; i < _masks.Count; i++)
        {
            maskIds[i] = next++;
            WriteStream(maskIds[i], _masks[i].Content, _compress, _masks[i].Dict);
        }

        // Images of SVG filter results.
        foreach (var raster in _rasters)
        {
            raster.ObjectNumber = next++;
            if (raster.SMask != null) raster.SMask.ObjectNumber = next++;
            WriteImage(raster);
            if (raster.SMask != null) WriteImage(raster.SMask);
        }

        // Shared resources, written last: shading patterns are collected while writing the pages.
        Begin(resourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        for (int i = 0; i < usedFonts.Length; i++)
            if (usedFonts[i]) _out.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
        for (int i = 0; i < _subsetList.Count; i++) _out.Ascii(" /E").Int(i + 1).Sp().Int(firstEmbeddedId + i * 5).Ascii(" 0 R");
        _out.Ascii(" >>");
        if (images.Count > 0 || wm != null || _forms.Count > 0 || _rasters.Count > 0)
        {
            _out.Ascii(" /XObject <<");
            for (int i = 0; i < images.Count; i++) _out.Ascii(" /Im").Int(i + 1).Sp().Int(images[i].ObjectNumber).Ascii(" 0 R");
            if (wm != null) _out.Ascii(" /Wm ").Int(watermarkId).Ascii(" 0 R");
            for (int i = 0; i < _forms.Count; i++) _out.Ascii(" /Fx").Int(i + 1).Sp().Int(formIds[i]).Ascii(" 0 R");
            for (int i = 0; i < _rasters.Count; i++) _out.Ascii(" /Xr").Int(i + 1).Sp().Int(_rasters[i].ObjectNumber).Ascii(" 0 R");
            _out.Ascii(" >>");
        }
        if (_fillAlphas.Count + _strokeAlphas.Count + _groupAlphas.Count + _blendStates.Count + _layerBlends.Count + _masks.Count > 0)
        {
            _out.Ascii(" /ExtGState <<");
            foreach (int a in _fillAlphas) _out.Ascii(" /a").Int(a).Ascii(" << /ca ").Num(a / 255f).Ascii(" >>");
            foreach (int a in _strokeAlphas) _out.Ascii(" /A").Int(a).Ascii(" << /CA ").Num(a / 255f).Ascii(" >>");
            foreach (int a in _groupAlphas) _out.Ascii(" /g").Int(a).Ascii(" << /ca ").Num(a / 255f).Ascii(" /CA ").Num(a / 255f).Ascii(" >>");
            foreach (var blend in _layerBlends) _out.Ascii(" /b").Ascii(blend).Ascii(" << /BM /").Ascii(blend).Ascii(" >>");
            foreach (var (a, blend) in _blendStates)
                _out.Ascii(" /g").Int(a).Ascii(blend).Ascii(" << /ca ").Num(a / 255f).Ascii(" /CA ").Num(a / 255f).Ascii(" /BM /").Ascii(blend).Ascii(" >>");
            for (int i = 0; i < _masks.Count; i++)
                _out.Ascii(" /s").Int(i + 1).Ascii(_masks[i].Alpha ? " << /SMask << /Type /Mask /S /Alpha /G " : " << /SMask << /Type /Mask /S /Luminosity /G ")
                    .Int(maskIds[i]).Ascii(" 0 R >> >>");
            _out.Ascii(" >>");
        }
        if (_patterns.Length > 0) _out.Ascii(" /Pattern <<").Bytes(_patterns.Span).Ascii(" >>");
        _out.Ascii(" >>");
        End();

        // Cross-reference table and trailer.
        long xref = _out.Length;
        int count = _xref.Count;
        _xref.WriteTable(_out, count, allInUse: true);
        var fileId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        PdfXref.WriteTrailer(_out, count, catalogId, infoId, encryptId, fileId, fileId, upperHexId: true, xref);

        _crypt?.Dispose();
        return _out.Span.ToArray();
    }

    // ------------------------------------------------------------------ objects

    /// <summary>
    /// Writes an attachment's file specification (with the /Desc PDF/UA asks for and PDF/A-3's /AFRelationship) at
    /// <paramref name="specId"/> and its embedded file stream at the next number.
    /// </summary>
    private void WriteAttachment(PdfAttachment a, int specId, DateTime now)
    {
        int fileId = specId + 1;
        Begin(specId).Ascii("<< /Type /Filespec /F ");
        WriteTextString(_out, a.FileName);
        _out.Ascii(" /UF ");
        WriteTextString(_out, a.FileName);
        _out.Ascii(" /Desc ");
        WriteTextString(_out, string.IsNullOrEmpty(a.Description) ? a.FileName : a.Description);
        _out.Ascii(" /AFRelationship /").Ascii(a.Relationship.ToString())
            .Ascii(" /EF << /F ").Int(fileId).Ascii(" 0 R /UF ").Int(fileId).Ascii(" 0 R >> >>");
        End();
        var dict = new ByteBuffer(160);
        dict.Ascii("/Type /EmbeddedFile /Subtype ");
        PdfSyntax.Name(dict, a.MimeTypeOrGuess);
        dict.Ascii(" /Params << /Size ").Int(a.Data.Length);
        if (a.CreationDate is { } created)
        {
            dict.Ascii(" /CreationDate ");
            WriteTextString(dict, EmbeddedFiles.PdfDate(created));
        }
        dict.Ascii(" /ModDate ");
        WriteTextString(dict, a.ModDate is { } mod ? EmbeddedFiles.PdfDate(mod) : EmbeddedFiles.PdfDate(now));
        if (!a.OmitChecksum)
        {
            dict.Ascii(" /CheckSum ");
            var md5 = System.Security.Cryptography.MD5.HashData(a.Data);
            PdfSyntax.Hex(dict, _crypt != null ? _crypt.Encrypt(md5) : md5, upper: true);
        }
        dict.Ascii(" >>");
        WriteStream(fileId, a.Data, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    private ByteBuffer Begin(int id) => _xref.Begin(_out, id);

    private void End() => PdfXref.End(_out);

    private int[] _pageIds = Array.Empty<int>();

    /// <summary>
    /// Writes the destination of a link or bookmark to flow position <paramref name="y"/> (a <c>/Dest</c> entry). A
    /// PDF 2.0 tagged file goes there with a GoTo action that also names the structure element (<paramref name="target"/>,
    /// or the nearest ancestor that was written), a structure destination as PDF/UA-2 requires.
    /// </summary>
    private void WriteGoTo(float y, StructElem? target)
    {
        while (target is { Id: 0 }) target = target.Parent;
        if (!Pdf20 || target == null)
        {
            _out.Ascii("/Dest ");
            WriteDest(y);
            return;
        }
        _out.Ascii("/A << /S /GoTo /D ");
        WriteDest(y);
        _out.Ascii(" /SD ");
        WriteDest(y, target.Id);
        _out.Ascii(" >>");
    }

    /// <summary>
    /// Writes an explicit destination for flow position <paramref name="y"/>: its page, scrolled to that height; or, with
    /// <paramref name="structId"/>, the structure destination of that element, scrolled the same.
    /// </summary>
    private void WriteDest(float y, int structId = 0)
    {
        // The section the position is in, then the page within the section.
        int si = 0;
        while (si + 1 < _sections.Count && y >= _sectionBase[si + 1] - 0.01f) si++;
        y -= _sectionBase[si];
        var grid = _sections[si].Grid;
        int last = si + 1 < _sections.Count ? _sectionFirst[si + 1] - 1 : _pageIds.Length - 1;
        int p = Math.Clamp(_sectionFirst[si] + grid.PageOf(y + 0.01f), _sectionFirst[si], last);
        var page = grid.SetupOf(p - _sectionFirst[si]);
        float py = page.Height - page.MarginTop - page.BandTop - (y - grid.Start(p - _sectionFirst[si]));
        float paperDy = PaperDy(page);
        _out.Ascii("[").Int(structId != 0 ? structId : _pageIds[p]).Ascii(" 0 R /XYZ 0 ").Num(Math.Min(py, page.Height) * page.Scale + paperDy).Ascii(" null]");
    }

    private IReadOnlyList<PageSection> _sections = Array.Empty<PageSection>();

    /// <summary>While writing: the section a page (0-based, in the whole document) belongs to, and its index in the section.</summary>
    internal (int Section, int Local) SectionOfPage(int page) => (_pageSection[page], page - _sectionFirst[_pageSection[page]]);

    /// <summary>While writing: the page (0-based, in the whole document) of a y in a section's flow, and the y on that page.</summary>
    internal (int Page, float Y) PageAt(int section, float y)
    {
        var grid = _sections[section].Grid;
        int local = Math.Max(0, (int)grid.PageOf(y + 0.01));
        return (_sectionFirst[section] + local, y - grid.Start(local));
    }
    private int[] _pageSection = Array.Empty<int>(), _sectionFirst = Array.Empty<int>();
    private float[] _sectionBase = Array.Empty<float>();

    /// <summary>Makes page <paramref name="p"/>'s setup current and returns its paper width (and height); sets <see cref="_paperDy"/>.</summary>
    private float SetPage(int p, out float paperH)
    {
        _page = _sections[_pageSection[p]].Grid.SetupOf(p - _sectionFirst[_pageSection[p]]);
        float paperW = _page.Width * _page.Scale;
        paperH = _page.Height * _page.Scale;
        if (_page.Scale != 1) { paperW = PaperSize(paperW); paperH = PaperSize(paperH); }
        _paperDy = paperH - _page.Height * _page.Scale;
        return paperW;
    }

    private float PaperDy(PageSetup page)
        => page.Scale == 1 ? 0 : PaperSize(page.Height * page.Scale) - page.Height * page.Scale;

    /// <summary>Where page <paramref name="p"/> starts in its section's flow.</summary>
    private float PageTop(int p)
    {
        int si = _pageSection[p];
        return _sections[si].Grid.Start(p - _sectionFirst[si]);
    }

    /// <summary>Draws the watermark, made for the first page's size, centred on the current page.</summary>
    private string WatermarkDo(PageSetup first)
    {
        float dx = (_page.Width - first.Width) / 2, dy = (_page.Height - first.Height) / 2;
        if (Math.Abs(dx) < 0.001f && Math.Abs(dy) < 0.001f) return "q /Wm Do Q\n";
        var b = new ByteBuffer(48);
        b.Ascii("q 1 0 0 1 ").Num(dx).Sp().Num(dy).Ascii(" cm /Wm Do Q\n");
        return System.Text.Encoding.ASCII.GetString(b.Span);
    }

    private float _paperDy; // how far a scaled page's content moves up to stay at the top of the paper

    /// <summary>
    /// A paper dimension as Chrome prints it: rounded to whole points, then to Chrome's 1/300 inch device units, so A4
    /// is 594.96 x 841.92 pt. IronPDF, which uses Chrome, writes pages this size; pdfium fits each page to whole pixels,
    /// so the same size makes it snap thin lines the same way.
    /// </summary>
    internal static float PaperSize(float pt)
        => (float)(Math.Round(Math.Round(pt, MidpointRounding.AwayFromZero) * 300 / 72, MidpointRounding.AwayFromZero) * 0.24);
}

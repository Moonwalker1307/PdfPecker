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
    private readonly List<long> _offsets = new() { 0 };

    // Content-stream graphics state while writing one page.
    private uint _fill;
    private bool _inText;
    private TextFont? _textFont;
    private float _textSize, _tw, _tc;
    private int _tr;           // text rendering mode: 0 fill, 2 fill and stroke (synthetic bold)
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
                float y0 = Math.Max(top, grid.Start(p)), y1 = Math.Min(bottom, grid.Start(p + 1));
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
            foreach (var op in section.Ops) Distribute(op, null);
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
        int count = _offsets.Count;
        _out.Ascii("xref\n0 ").Int(count).Ascii("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
        {
            string off = _offsets[i].ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
            _out.Ascii(off).Ascii(" 00000 n \n");
        }
        _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(catalogId).Ascii(" 0 R");
        if (infoId != 0) _out.Ascii(" /Info ").Int(infoId).Ascii(" 0 R");
        if (encryptId != 0) _out.Ascii(" /Encrypt ").Int(encryptId).Ascii(" 0 R");
        var fileId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        _out.Ascii(" /ID [");
        PdfEncryptor.Hex(_out, fileId);
        PdfEncryptor.Hex(_out, fileId);
        _out.Ascii("] >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");

        _crypt?.Dispose();
        return _out.Span.ToArray();
    }

    // ------------------------------------------------------------------ structure

    /// <summary>
    /// Writes the structure tree of a tagged PDF: its root, the elements, and the parent tree that maps each page's
    /// marked content and each link annotation back to its element. Returns the next free object number.
    /// </summary>
    private int WriteStructure(int next, List<StructElem> elements, int rootId, int nextKey, int[] pageIds, McStream[] pageStreams,
        Dictionary<int, int> annotKeys)
    {
        var tree = Structure!;
        int parentTreeId = next++;

        // PDF 2.0 files put every element in the PDF 2.0 standard structure namespace; the PDF 1.7 types that PDF 2.0
        // dropped stay in the PDF 1.7 namespace, role mapped to their nearest PDF 2.0 type.
        int ns2Id = 0, ns17Id = 0;
        if (Pdf20)
        {
            ns2Id = next++;
            if (elements.Any(e => Pdf17Only(e.Type) != null)) ns17Id = next++;
        }

        Begin(rootId).Ascii("<< /Type /StructTreeRoot /K ").Int(tree.Root.Id).Ascii(" 0 R /ParentTree ").Int(parentTreeId)
            .Ascii(" 0 R /ParentTreeNextKey ").Int(nextKey);
        if (ns2Id != 0)
        {
            _out.Ascii(" /Namespaces [").Int(ns2Id).Ascii(" 0 R");
            if (ns17Id != 0) _out.Ascii(" ").Int(ns17Id).Ascii(" 0 R");
            _out.Ascii("]");
        }
        _out.Ascii(" >>");
        End();
        if (ns2Id != 0)
        {
            // Through WriteTextString, so an encrypted file encrypts the namespace names like every other string.
            Begin(ns2Id).Ascii("<< /Type /Namespace /NS ");
            WriteTextString(_out, "http://iso.org/pdf2/ssn");
            _out.Ascii(" >>");
            End();
        }
        if (ns17Id != 0)
        {
            Begin(ns17Id).Ascii("<< /Type /Namespace /NS ");
            WriteTextString(_out, "http://iso.org/pdf/ssn");
            _out.Ascii(" /RoleMapNS <<");
            foreach (var type in elements.Select(e => e.Type).Where(t => Pdf17Only(t) != null).Distinct())
                _out.Ascii(" /").Ascii(type).Ascii(" [/").Ascii(Pdf17Only(type)!).Sp().Int(ns2Id).Ascii(" 0 R]");
            _out.Ascii(" >> >>");
            End();
        }

        // The parent tree: a number tree with one flat /Nums array, in key order.
        var entries = new List<(int Key, McStream? Stream, StructElem? Link)>();
        foreach (var page in pageStreams)
        {
            if (page.Elements.Count > 0) entries.Add((page.Page, page, null));
            foreach (var (id, link) in page.Annotations) entries.Add((annotKeys[id], null, link));
        }
        foreach (var form in _formStreams)
            if (form is { Key: >= 0 }) entries.Add((form.Key, form, null));
        entries.Sort((x, y) => x.Key.CompareTo(y.Key));
        Begin(parentTreeId).Ascii("<< /Nums [");
        foreach (var (key, stream, link) in entries)
        {
            _out.Ascii("\n").Int(key).Sp();
            if (link != null) { _out.Int(link.Id).Ascii(" 0 R"); continue; }
            _out.Ascii("[");
            foreach (var e in stream!.Elements) _out.Int(e.Id).Ascii(" 0 R ");
            _out.Ascii("]");
        }
        _out.Ascii("] >>");
        End();

        foreach (var e in elements)
        {
            Begin(e.Id).Ascii("<< /Type /StructElem /S /").Ascii(e.Type).Ascii(" /P ").Int(e.Parent?.Id ?? rootId).Ascii(" 0 R");
            if (ns2Id != 0) _out.Ascii(" /NS ").Int(Pdf17Only(e.Type) != null ? ns17Id : ns2Id).Ascii(" 0 R");
            if (e.Lang != null) { _out.Ascii(" /Lang "); WriteTextString(_out, e.Lang); }
            if (e.Alt != null) { _out.Ascii(" /Alt "); WriteTextString(_out, e.Alt); }
            if (e.Scope != null || e.RowSpan > 1 || e.ColSpan > 1)
            {
                _out.Ascii(" /A << /O /Table");
                if (e.Scope != null) _out.Ascii(" /Scope /").Ascii(e.Scope);
                if (e.RowSpan > 1) _out.Ascii(" /RowSpan ").Int(e.RowSpan);
                if (e.ColSpan > 1) _out.Ascii(" /ColSpan ").Int(e.ColSpan);
                _out.Ascii(" >>");
            }
            if (e.ListNumbering != null) _out.Ascii(" /A << /O /List /ListNumbering /").Ascii(e.ListNumbering).Ascii(" >>");
            _out.Ascii(" /K [");
            foreach (var k in e.Kids)
            {
                switch (k)
                {
                    case StructElem c:
                        _out.Int(c.Id).Ascii(" 0 R ");
                        break;
                    case ContentSlot slot:
                        foreach (var (stream, mcid) in slot.Refs)
                        {
                            _out.Ascii("<< /Type /MCR /Pg ").Int(pageIds[stream.Page]).Ascii(" 0 R /MCID ").Int(mcid);
                            if (stream.FormId != 0) _out.Ascii(" /Stm ").Int(stream.FormId).Ascii(" 0 R");
                            _out.Ascii(" >> ");
                        }
                        break;
                    case AnnotRef annot:
                        _out.Ascii("<< /Type /OBJR /Pg ").Int(pageIds[annot.Page]).Ascii(" 0 R /Obj ").Int(annot.Id).Ascii(" 0 R >> ");
                        break;
                }
            }
            _out.Ascii("] >>");
            End();
        }
        return next;
    }

    /// <summary>Leaves out the structure elements without content and numbers the others, in tree order.</summary>
    private List<StructElem> NumberStructure(ref int next)
    {
        Structure!.Prune();
        var elements = new List<StructElem>();
        int id = next;
        void Number(StructElem e)
        {
            e.Id = id++;
            elements.Add(e);
            foreach (var k in e.Kids)
                if (k is StructElem c) Number(c);
        }
        Number(Structure.Root);
        next = id;
        return elements;
    }

    /// <summary>For a PDF 1.7 structure type that PDF 2.0 dropped, the PDF 2.0 type it is role mapped to; else null.</summary>
    private static string? Pdf17Only(string type) => type switch
    {
        "BlockQuote" => "Div",
        "Quote" or "Code" => "Span",
        "Art" => "Sect",
        _ => null,
    };

    // ------------------------------------------------------------------ outline

    private sealed class OutlineItem
    {
        public string Title = "";
        public int Level, Id, Descendants;
        public float Y;
        public Html.Element? Heading;
        public OutlineItem? Parent, Prev, Next;
        public readonly List<OutlineItem> Children = new();
    }

    /// <summary>Nests the headings by level: a heading becomes a child of the nearest earlier heading of a higher level.</summary>
    private static PdfLimitException TooManyPages(int max)
        => new($"The document would have more than {max:N0} pages (PdfOptions.MaxPages).");

    private static List<OutlineItem> BuildOutline(IReadOnlyList<OutlineHeading>? headings, IReadOnlyDictionary<string, float> anchors)
    {
        var all = new List<OutlineItem>();
        if (headings == null) return all;
        var stack = new List<OutlineItem>();
        OutlineItem? lastTop = null;
        foreach (var h in headings)
        {
            if (!anchors.TryGetValue(h.Key, out float y)) continue;
            var item = new OutlineItem { Title = h.Title, Level = h.Level, Y = y, Heading = h.Element };
            while (stack.Count > 0 && stack[^1].Level >= h.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0)
            {
                var parent = stack[^1];
                item.Parent = parent;
                if (parent.Children.Count > 0) { item.Prev = parent.Children[^1]; item.Prev.Next = item; }
                parent.Children.Add(item);
                for (var p = parent; p != null; p = p.Parent) p.Descendants++;
            }
            else
            {
                if (lastTop != null) { item.Prev = lastTop; lastTop.Next = item; }
                lastTop = item;
            }
            stack.Add(item);
            all.Add(item);
        }
        return all;
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
            PdfEncryptor.Hex(dict, _crypt != null ? _crypt.Encrypt(md5) : md5);
        }
        dict.Ascii(" >>");
        WriteStream(fileId, a.Data, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

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

    // Reused for every compressed stream, so compressing a page does not allocate a new buffer each time.
    private MemoryStream? _deflated;

    private void WriteStream(int id, ReadOnlySpan<byte> data, bool compress, string? extraDict)
    {
        ReadOnlySpan<byte> body = data;
        bool filtered = false;
        if (compress && data.Length > 64)
        {
            var ms = _deflated ??= new MemoryStream(Math.Max(4096, data.Length / 3 + 64));
            ms.SetLength(0);
            using (var z = new ZLibStream(ms, _level, leaveOpen: true)) z.Write(data);
            body = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            filtered = true;
        }
        byte[]? encrypted = _crypt?.Encrypt(body);
        if (encrypted != null) body = encrypted;
        Begin(id).Ascii("<< /Length ").Int(body.Length);
        if (filtered) _out.Ascii(" /Filter /FlateDecode");
        if (extraDict != null) _out.Sp().Ascii(extraDict);
        _out.Ascii(" >>\nstream\n");
        _out.Bytes(body);
        _out.Ascii("\nendstream");
        End();
    }

    private void WriteImage(PdfImage img)
    {
        string colorSpace = img.ColorSpace;
        // An indexed colour space carries its palette as a hex string, which must be encrypted like any other string.
        if (_crypt != null && colorSpace.IndexOf('<') is int lt and >= 0)
        {
            int gt = colorSpace.IndexOf('>', lt);
            var palette = System.Convert.FromHexString(colorSpace.AsSpan(lt + 1, gt - lt - 1));
            var sb = new ByteBuffer(64 + palette.Length * 2);
            sb.Ascii(img.ColorSpace.Substring(0, lt));
            PdfEncryptor.Hex(sb, _crypt.Encrypt(palette));
            sb.Ascii(img.ColorSpace.Substring(gt + 1));
            colorSpace = System.Text.Encoding.ASCII.GetString(sb.Span);
        }
        var data = _crypt != null ? _crypt.Encrypt(img.Data) : img.Data;
        Begin(img.ObjectNumber).Ascii("<< /Type /XObject /Subtype /Image /Width ").Int(img.Width).Ascii(" /Height ").Int(img.Height)
            .Ascii(" /ColorSpace ").Ascii(colorSpace).Ascii(" /BitsPerComponent ").Int(img.BitsPerComponent)
            .Ascii(" /Filter ").Ascii(img.Filter);
        if (img.DecodeParms != null) _out.Ascii(" /DecodeParms ").Ascii(img.DecodeParms);
        if (img.Decode != null) _out.Ascii(" /Decode ").Ascii(img.Decode);
        if (img.SMask != null) _out.Ascii(" /SMask ").Int(img.SMask.ObjectNumber).Ascii(" 0 R");
        _out.Ascii(" /Length ").Int(data.Length).Ascii(" >>\nstream\n").Bytes(data).Ascii("\nendstream");
        End();
    }

    /// <summary>
    /// The watermark text as one TextOp per line, centred on the origin of a rotated coordinate system whose
    /// cosine and sine are returned; the form XObject moves that origin to the page centre.
    /// </summary>
    private (List<TextOp> Lines, float Cos, float Sin) LayoutWatermarkText(PdfWatermark wm, TextFont font)
    {
        float W = _page.Width, H = _page.Height;
        var lines = wm.Text!.Replace("\r\n", "\n").Split('\n');
        double a = (wm.Angle ?? Math.Atan2(H, W) * 180 / Math.PI) * Math.PI / 180;
        float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
        const float lead = 1.15f, cap = 0.72f;
        float ls = float.IsFinite(wm.LetterSpacing) ? Math.Clamp(wm.LetterSpacing, -100, 100) : 0;
        if (ls != 0) font = font.WithoutLigatures(); // as browsers set letter-spaced text
        int maxUnits = 1, maxChars = 0;
        foreach (var line in lines)
        {
            int units = font.MeasureUnits(line, 0, line.Length);
            if (units >= maxUnits) { maxUnits = units; maxChars = line.Length; }
        }
        float size = wm.FontSize;
        if (size <= 0)
        {
            // As large as fits in 80% of the page with the text block rotated; letter spacing does not scale.
            float tw = maxUnits / 1000f, th = lead * (lines.Length - 1) + cap, lsw = ls * maxChars;
            float ac = Math.Abs(c), asn = Math.Abs(sn);
            size = Math.Min((0.8f * W - lsw * ac) / (tw * ac + th * asn), (0.8f * H - lsw * asn) / (tw * asn + th * ac));
            size = Math.Clamp(size, 4, 400);
        }
        uint color = CssColor.TryParse(wm.Color ?? "", CssColor.Black, out var parsed) ? parsed : 0xFF808080;
        float blockH = size * (lead * (lines.Length - 1) + cap);
        var ops = new List<TextOp>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            // Centred on the letters: the spacing after the last one is left out.
            float xl = -(font.Measure(lines[i], size) + ls * Math.Max(0, lines[i].Length - 1)) / 2;
            float yl = blockH / 2 - cap * size - i * lead * size;
            ops.Add(new TextOp(xl, yl, 0, font, size, color, lines[i], 0, ls));
        }
        return (ops, c, sn);
    }

    /// <summary>The watermark as a form XObject covering the page, drawn by every page's content stream.</summary>
    private void WriteWatermark(int id, PdfWatermark wm, (List<TextOp> Lines, float Cos, float Sin)? text, ByteBuffer fontDict, PdfImage? image,
        SvgImage? svg, int svgId)
    {
        float W = _page.Width, H = _page.Height, cx = W / 2, cy = H / 2;
        float opacity = Math.Clamp(wm.Opacity, 0, 1);
        var b = new ByteBuffer(512);
        ResetState();
        _noAlpha = true; // the watermark's own resources have no alpha states: its opacity applies to all of it
        b.Ascii("q /Gw gs\n");
        if (image != null)
        {
            float iw = wm.ImageWidth > 0 ? wm.ImageWidth : W / 2;
            float ih = iw * image.Height / Math.Max(1, image.Width);
            double a = (wm.Angle ?? 0) * Math.PI / 180;
            float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            b.Num(iw * c).Sp().Num(iw * sn).Sp().Num(-ih * sn).Sp().Num(ih * c).Sp()
                .Num(cx - iw * c / 2 + ih * sn / 2).Sp().Num(cy - iw * sn / 2 - ih * c / 2).Ascii(" cm /Iw Do\n");
            b.Ascii("Q q /Gw gs\n");
        }
        else if (svg != null)
        {
            // The drawing is a transparency group of its own with the shared resources: its own translucent parts keep
            // their alpha, and the watermark's opacity applies to the group as a whole.
            float iw = wm.ImageWidth > 0 ? wm.ImageWidth : W / 2;
            float ih = iw * SvgAspect(svg);
            double a = (wm.Angle ?? 0) * Math.PI / 180;
            float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            b.Num(c).Sp().Num(sn).Sp().Num(-sn).Sp().Num(c).Sp()
                .Num(cx - iw * c / 2 + ih * sn / 2).Sp().Num(cy - iw * sn / 2 - ih * c / 2).Ascii(" cm /Iw Do\n");
            b.Ascii("Q q /Gw gs\n");
            var drawing = new ByteBuffer(1024);
            var saved = SaveState();
            _noAlpha = false;
            (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
            _anyAlpha = true;
            WriteSvg(drawing, svg, 0, ih, iw, ih);
            RestoreState(saved);
            _noAlpha = true;
            WriteStream(svgId, drawing.Span, _compress, "/Type /XObject /Subtype /Form /BBox [0 0 " + Fmt(iw) + " " + Fmt(ih)
                + "] /Group << /S /Transparency >> /Resources " + _resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R");
        }
        if (text is { } t)
        {
            b.Num(t.Cos).Sp().Num(t.Sin).Sp().Num(-t.Sin).Sp().Num(t.Cos).Sp().Num(cx).Sp().Num(cy).Ascii(" cm\nBT\n");
            _fill = 0; // force the colour to be written
            foreach (var line in t.Lines)
            {
                SetFill(b, line.Color);
                if (_tc != line.CharSpacing) { _tc = line.CharSpacing; b.Num(line.CharSpacing).Ascii(" Tc\n"); }
                WriteTextLine(b, line, line.X, line.Baseline);
            }
            b.Ascii("ET\n");
        }
        b.Ascii("Q\n");
        _noAlpha = false;

        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [0 0 ").Num(W).Sp().Num(H).Ascii("] /Resources << /ExtGState << /Gw << /Type /ExtGState /ca ")
            .Num(opacity).Ascii(" /CA ").Num(opacity).Ascii(" >> >>");
        if (text != null) dict.Ascii(" /Font <<").Bytes(fontDict.Span).Ascii(" >>");
        if (image != null) dict.Ascii(" /XObject << /Iw ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        else if (svg != null) dict.Ascii(" /XObject << /Iw ").Int(svgId).Ascii(" 0 R >>");
        dict.Ascii(" >>");
        WriteStream(id, b.Span, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    /// <summary>Height over width of an SVG drawing: from its width and height, else its viewBox, else 300 × 150.</summary>
    private static float SvgAspect(SvgImage svg)
    {
        if (svg.Width is > 0 and var w && svg.Height is > 0 and var h) return h / w;
        if (svg.HasViewBox) return svg.ViewBoxHeight / svg.ViewBoxWidth;
        return 0.5f;
    }

    private static string Fmt(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ content streams

    /// <summary>After Q: the colours and text state set inside are gone, so the next op writes its own.</summary>
    private void InvalidateState()
    {
        _fill = 1;
        _stroke = 1;
        _textFont = null;
        _textSize = -1;
        _tw = _tc = float.NaN;
        _tr = -1;
        _lineWidth = -1;
        if (_anyAlpha) _fillAlpha = _strokeAlpha = -1;
    }

    private void ResetState()
    {
        _fill = CssColor.Black;
        _inText = false;
        _textFont = null;
        _textSize = 0;
        _tw = 0;
        _tc = 0;
        _tr = 0;
        _stroke = CssColor.Black;
        _lineWidth = 1;
        _fillAlpha = _strokeAlpha = 255;
    }

    private float PX(float x) => _page.MarginLeft + x;

    private static float SnapPx(float pt) => MathF.Round(pt / 0.75f, MidpointRounding.AwayFromZero) * 0.75f;
    private float PY(float y, float pageTop) => _page.Height - _yOrigin - (y - pageTop);

    private void SetFill(ByteBuffer b, uint color)
    {
        SetFillAlpha(b, color);
        if (color == _fill) return;
        _fill = color;
        WriteColor(b, color);
        b.Ascii(" rg\n");
    }

    private void SetStroke(ByteBuffer b, uint color)
    {
        SetStrokeAlpha(b, color);
        if (color == _stroke) return;
        _stroke = color;
        WriteColor(b, color);
        b.Ascii(" RG\n");
    }

    /// <summary>Sets the fill alpha to the alpha of <paramref name="color"/> (0xAARRGGBB) through an ExtGState.</summary>
    private void SetFillAlpha(ByteBuffer b, uint color)
    {
        int a = (int)(color >> 24);
        if (a == _fillAlpha || _noAlpha) return;
        _fillAlpha = a;
        _anyAlpha = true;
        _fillAlphas.Add(a);
        b.Ascii("/a").Int(a).Ascii(" gs\n");
    }

    private void SetStrokeAlpha(ByteBuffer b, uint color)
    {
        int a = (int)(color >> 24);
        if (a == _strokeAlpha || _noAlpha) return;
        _strokeAlpha = a;
        _anyAlpha = true;
        _strokeAlphas.Add(a);
        b.Ascii("/A").Int(a).Ascii(" gs\n");
    }

    /// <summary>The tracked graphics state, saved at q and put back at the matching Q.</summary>
    private (uint Fill, uint Stroke, float LineWidth, int FillAlpha, int StrokeAlpha) SaveState()
        => (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha);

    private void RestoreState((uint Fill, uint Stroke, float LineWidth, int FillAlpha, int StrokeAlpha) s)
        => (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = s;

    private static void WriteColor(ByteBuffer b, uint color)
    {
        b.Num(((color >> 16) & 0xFF) / 255f).Sp().Num(((color >> 8) & 0xFF) / 255f).Sp().Num((color & 0xFF) / 255f);
    }

    private void EndText(ByteBuffer b)
    {
        if (!_inText) return;
        b.Ascii("ET\n");
        _inText = false;
    }

    private void WriteOp(ByteBuffer b, in Placed placed, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        if (_mc != null)
        {
            // Marked content nests inside the q/Q of clips and layers, which open their own sequences.
            if (placed.Op is LayerBlock or ClipBlock) EndMarked(b);
            else Mark(b, placed.Op.Tag);
        }
        switch (placed.Op)
        {
            case TextOp t:
            {
                if (!_inText)
                {
                    b.Ascii("BT\n");
                    _inText = true;
                    _textFont = null;
                }
                SetFill(b, t.Color);
                if (_tw != t.WordSpacing) { _tw = t.WordSpacing; b.Num(t.WordSpacing).Ascii(" Tw\n"); }
                if (_tc != t.CharSpacing) { _tc = t.CharSpacing; b.Num(t.CharSpacing).Ascii(" Tc\n"); }
                // Chrome puts baselines on whole CSS pixels (horizontal positions stay fractional).
                float y = PY(pageTop + SnapPx(t.Baseline - pageTop), pageTop);
                WriteTextLine(b, t, PX(t.X), y);
                break;
            }
            case RectOp r:
            {
                EndText(b);
                SetFill(b, r.Color);
                // Chrome paints boxes on whole CSS pixels: each edge rounds to the nearest pixel of the page.
                // Text decorations only snap vertically.
                float x0 = r.IsDecoration ? r.X : SnapPx(r.X), x1 = r.IsDecoration ? r.X + r.W : SnapPx(r.X + r.W);
                float top = pageTop + SnapPx(placed.Y0 - pageTop), bottom = pageTop + SnapPx(placed.Y1 - pageTop);
                if (x1 <= x0) { x0 = r.X; x1 = r.X + r.W; }
                if (bottom <= top) { top = placed.Y0; bottom = placed.Y1; }
                b.Num(PX(x0)).Sp().Num(PY(bottom, pageTop)).Sp().Num(x1 - x0).Sp().Num(bottom - top).Ascii(" re f\n");
                break;
            }
            case BorderRectOp br:
            {
                EndText(b);
                // Snapped like the edges of a filled box, then stroked along the middle of the border.
                float x0 = SnapPx(br.X), x1 = SnapPx(br.X + br.W);
                float top = pageTop + SnapPx(placed.Y0 - pageTop), bottom = pageTop + SnapPx(placed.Y1 - pageTop);
                float lw = br.Width, half = lw / 2;
                if (x1 - x0 <= lw || bottom - top <= lw)
                {
                    SetFill(b, br.Color);
                    b.Num(PX(x0)).Sp().Num(PY(bottom, pageTop)).Sp().Num(x1 - x0).Sp().Num(bottom - top).Ascii(" re f\n");
                    break;
                }
                SetStroke(b, br.Color);
                if (_lineWidth != lw) { _lineWidth = lw; b.Num(lw).Ascii(" w\n"); }
                b.Num(PX(x0 + half)).Sp().Num(PY(bottom - half, pageTop)).Sp().Num(x1 - x0 - lw).Sp().Num(bottom - top - lw).Ascii(" re S\n");
                break;
            }
            case BackgroundOp bg:
                EndText(b);
                if (bg.BlendMode is { } mode)
                {
                    // background-blend-mode: the layer blends with the layers and colour already painted in its group.
                    var saved = SaveState();
                    _layerBlends.Add(mode);
                    b.Ascii("q /b").Ascii(mode).Ascii(" gs\n");
                    WriteBackground(b, bg, placed, pageTop, imageIds);
                    b.Ascii("Q\n");
                    RestoreState(saved);
                }
                else WriteBackground(b, bg, placed, pageTop, imageIds);
                break;
            case RoundRectOp rr:
                EndText(b);
                WriteRoundRect(b, rr, placed, pageTop);
                break;
            case LayerBlock tb:
            {
                EndText(b);
                b.Ascii("q ");
                if (tb.Layer.Transform is { } m)
                {
                    // The layer's y-down transform about its origin, mapped into PDF space (y up).
                    float px = PX(tb.Layer.OriginX), py = PY(tb.Layer.OriginY, pageTop);
                    float e = px - m.A * px + m.C * py + m.E, f = py + m.B * px - m.D * py - m.F;
                    b.Num6(m.A).Sp().Num6(-m.B).Sp().Num6(-m.C).Sp().Num6(m.D).Sp().Num(e).Sp().Num(f).Ascii(" cm\n");
                }
                if (tb.Layer.ClipVerbs is { } clipVerbs)
                {
                    // clip-path: the outline in the layer's own space, so that a transform turns it with the content.
                    if (_mc != null) b.Ascii("/Artifact BMC ");
                    WriteFlowPath(b, clipVerbs, tb.Layer.ClipPoints!, pageTop);
                    b.Ascii(tb.Layer.ClipEvenOdd ? "W* n\n" : "W n\n");
                    if (_mc != null) b.Ascii("EMC\n");
                }
                if (tb.Layer.NeedsGroup)
                {
                    // A translucent or blended layer is a transparency group, painted as a whole at its opacity and with
                    // its blend mode, so that its own overlapping parts do not show through each other.
                    var group = new ByteBuffer(4096);
                    _fill = _stroke = 1;
                    _lineWidth = -1;
                    _textFont = null;
                    _textSize = -1;
                    _tw = _tc = float.NaN;
                    _tr = -1;
                    _fillAlpha = _strokeAlpha = -1; // a group starts at alpha 1, but not every viewer resets it
                    _anyAlpha = true;
                    // A transparency group is a stream of its own, with its own marked-content numbers.
                    var pageMc = _mc;
                    if (pageMc != null) _mc = new McStream(pageMc.Page);
                    WriteLayerContent(group, tb, pageTop, imageIds);
                    EndText(group);
                    EndMarked(group);
                    _forms.Add(group.Span.ToArray());
                    _formIsolated.Add(tb.Layer.BlendMode != null || tb.Layer.Isolated);
                    _formStreams.Add(_mc);
                    _mc = pageMc;
                    int alpha = (int)MathF.Round(Math.Clamp(tb.Layer.Opacity, 0, 1) * 255);
                    if (tb.Layer.BlendMode is { } blend)
                    {
                        _blendStates.Add((alpha, blend));
                        b.Ascii("/g").Int(alpha).Ascii(blend);
                    }
                    else
                    {
                        _groupAlphas.Add(alpha);
                        b.Ascii("/g").Int(alpha);
                    }
                    b.Ascii(" gs /Fx").Int(_forms.Count).Ascii(" Do\n");
                }
                else
                {
                    WriteLayerContent(b, tb, pageTop, imageIds);
                    EndText(b);
                    EndMarked(b);
                }
                b.Ascii("Q\n");
                InvalidateState();
                break;
            }
            case FieldBlock fb:
                // A widget is written on its own; a field that cannot be one is drawn, checked or not.
                if (_fieldIds.ContainsKey(fb) || fb.Field.Kind is ControlKind.CheckBox or ControlKind.Radio && !fb.Field.Checked) break;
                foreach (var item in fb.Items)
                    if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                break;
            case ClipBlock cb:
            {
                EndText(b);
                var c = cb.Clip;
                // Edges on whole CSS pixels, like the box's background and borders.
                float x0 = c.X, x1 = c.X + c.W, y0 = c.Y, y1 = c.Y + c.H;
                if (c.W < ClipOp.Far) { x0 = SnapPx(x0); x1 = SnapPx(x1); }
                if (c.H < ClipOp.Far) { y0 = SnapPx(y0); y1 = SnapPx(y1); }
                b.Ascii("q ");
                if (_mc != null) b.Ascii("/Artifact BMC ");
                if (c.Radii is { } r && !r.IsZero) RoundRectPath(b, x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0), r, pageTop);
                else b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(Math.Max(0, x1 - x0)).Sp().Num(Math.Max(0, y1 - y0)).Ascii(" re ");
                b.Ascii("W n\n");
                if (_mc != null) b.Ascii("EMC\n");
                foreach (var item in cb.Items)
                    if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                EndText(b);
                EndMarked(b);
                b.Ascii("Q\n");
                InvalidateState();
                break;
            }
            case LineOp l:
            {
                EndText(b);
                var saved = SaveState();
                b.Ascii("q ");
                SetStroke(b, l.Color);
                b.Num(l.Width).Ascii(" w ");
                if (l.Solid) b.Ascii("1 J ");
                else if (l.Dotted) b.Ascii("1 J [0 ").Num(l.Width * 2).Ascii("] 0 d ");
                else b.Ascii("[").Num(l.Width * 3).Sp().Num(l.Width * 3).Ascii("] 0 d ");
                float y1 = l.X1 == l.X2 ? placed.Y0 : l.Y1, y2 = l.X1 == l.X2 ? placed.Y1 : l.Y2;
                b.Num(PX(l.X1)).Sp().Num(PY(y1, pageTop)).Ascii(" m ").Num(PX(l.X2)).Sp().Num(PY(y2, pageTop)).Ascii(" l S Q\n");
                RestoreState(saved);
                break;
            }
            case ImageOp im:
            {
                EndText(b);
                SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
                b.Ascii("q ");
                if (im.Radii is { } ir) { RoundRectPath(b, im.X, im.Y, im.W, im.H, ir, pageTop); b.Ascii("W n "); }
                b.Num(im.W).Ascii(" 0 0 ").Num(im.H).Sp().Num(PX(im.X)).Sp().Num(PY(im.Y + im.H, pageTop))
                    .Ascii(" cm /Im").Int(imageIds[im.Image]).Ascii(" Do Q\n");
                break;
            }
            case SvgOp sv:
                EndText(b);
                WriteSvg(b, sv.Image, PX(sv.X), PY(sv.Y, pageTop), sv.W, sv.H);
                break;
            case CircleOp c:
            {
                EndText(b);
                float cx = PX(c.Cx), cy = PY(c.Cy, pageTop), r = c.R, k = r * 0.5523f;
                var saved = SaveState();
                if (c.Filled) SetFill(b, c.Color);
                else
                {
                    b.Ascii("q ");
                    SetStroke(b, c.Color);
                    b.Ascii("0.75 w "); // 1px, as Chrome strokes a circle marker
                }
                b.Num(cx + r).Sp().Num(cy).Ascii(" m ");
                b.Num(cx + r).Sp().Num(cy + k).Sp().Num(cx + k).Sp().Num(cy + r).Sp().Num(cx).Sp().Num(cy + r).Ascii(" c ");
                b.Num(cx - k).Sp().Num(cy + r).Sp().Num(cx - r).Sp().Num(cy + k).Sp().Num(cx - r).Sp().Num(cy).Ascii(" c ");
                b.Num(cx - r).Sp().Num(cy - k).Sp().Num(cx - k).Sp().Num(cy - r).Sp().Num(cx).Sp().Num(cy - r).Ascii(" c ");
                b.Num(cx + k).Sp().Num(cy - r).Sp().Num(cx + r).Sp().Num(cy - k).Sp().Num(cx + r).Sp().Num(cy).Ascii(" c ");
                b.Ascii(c.Filled ? "f\n" : "S Q\n");
                if (!c.Filled) RestoreState(saved);
                break;
            }
        }
    }

    /// <summary>Writes a path given in flow coordinates (SvgShape verbs) in the page's PDF space.</summary>
    private void WriteFlowPath(ByteBuffer b, byte[] verbs, float[] pts, float pageTop)
    {
        int k = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case SvgShape.MoveTo: b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Ascii(" m "); k += 2; break;
                case SvgShape.LineTo: b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Ascii(" l "); k += 2; break;
                case SvgShape.CurveTo:
                    b.Num(PX(pts[k])).Sp().Num(PY(pts[k + 1], pageTop)).Sp().Num(PX(pts[k + 2])).Sp().Num(PY(pts[k + 3], pageTop)).Sp()
                        .Num(PX(pts[k + 4])).Sp().Num(PY(pts[k + 5], pageTop)).Ascii(" c ");
                    k += 6;
                    break;
                default: b.Ascii("h "); break;
            }
        }
        // An empty outline clips everything away.
        if (verbs.Length == 0) b.Ascii("0 0 m h ");
    }

    /// <summary>The content of a layer: its ops, or for a filter that runs on pixels, the filtered image under or in place of them.</summary>
    private void WriteLayerContent(ByteBuffer b, LayerBlock tb, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var layer = tb.Layer;
        if (layer.Raster != null)
        {
            EndText(b);
            if (_mc != null) { EndMarked(b); b.Ascii("/Artifact BMC\n"); }
            WriteLayerRaster(b, layer, pageTop);
            if (_mc != null) b.Ascii("EMC\n");
            if (layer.RasterReplaces) return;
        }
        foreach (var item in tb.Items)
            if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
    }

    /// <summary>
    /// Draws an SVG drawing in the box whose top-left corner is (<paramref name="left"/>, <paramref name="top"/>) in PDF
    /// space: clipped to the box, with the viewBox mapped by preserveAspectRatio.
    /// </summary>
    private void WriteSvg(ByteBuffer b, SvgImage img, float left, float top, float w, float h)
    {
        if (img.Shapes.Count == 0 || w <= 0 || h <= 0) return;
        float sx, sy, tx = 0, ty = 0, vx = 0, vy = 0;
        if (img.HasViewBox)
        {
            vx = img.ViewBoxX;
            vy = img.ViewBoxY;
            sx = w / img.ViewBoxWidth;
            sy = h / img.ViewBoxHeight;
            if (img.Align != "none")
            {
                float scale = img.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
                sx = sy = scale;
                float extraX = w - img.ViewBoxWidth * scale, extraY = h - img.ViewBoxHeight * scale;
                var a = img.Align;
                tx = a.StartsWith("xmid", StringComparison.Ordinal) ? extraX / 2 : a.StartsWith("xmax", StringComparison.Ordinal) ? extraX : 0;
                ty = a.EndsWith("ymid", StringComparison.Ordinal) ? extraY / 2 : a.EndsWith("ymax", StringComparison.Ordinal) ? extraY : 0;
            }
        }
        else sx = sy = 0.75f; // user units are CSS pixels

        var saved = SaveState();
        b.Ascii("q ").Num(left).Sp().Num(top - h).Sp().Num(w).Sp().Num(h).Ascii(" re W n\n");
        // User space (y down) to PDF space (y up).
        var toPage = new Matrix(sx, 0, 0, -sy, left + tx - vx * sx, top - ty + vy * sy);
        b.Num(sx).Ascii(" 0 0 ").Num(-sy).Sp().Num(toPage.E).Sp().Num(toPage.F).Ascii(" cm\n");
        WriteSvgShapes(b, img.Shapes, toPage);
        b.Ascii("Q\n");
        RestoreState(saved);
    }

    /// <summary>
    /// Writes SVG shapes in the drawing's user space (already set up by a cm). <paramref name="toPage"/> maps that space
    /// to the default space of the stream, where shading and tiling patterns are placed.
    /// </summary>
    private void WriteSvgShapes(ByteBuffer b, List<SvgShape> shapes, Matrix toPage)
    {
        var states = new Stack<(uint, uint, float, int, int)>();
        foreach (var shape in shapes)
        {
            switch (shape.Kind)
            {
                case SvgShape.KindClip:
                    states.Push(SaveState());
                    b.Ascii("q\n");
                    WritePath(b, shape);
                    b.Ascii(shape.EvenOdd ? "W* n\n" : "W n\n");
                    continue;
                case SvgShape.KindMask:
                {
                    // The soft mask's coordinates are those in effect when it is set: the stream's default space, where
                    // its patterns are placed too. Then back to user space for the masked shapes.
                    int mask = AddSvgMask(shape, toPage);
                    states.Push(SaveState());
                    b.Ascii("q ");
                    if (toPage.TryInvert(out var back)) WriteMatrix(b, back).Ascii(" cm ");
                    b.Ascii("/s").Int(mask).Ascii(" gs ");
                    WriteMatrix(b, toPage).Ascii(" cm\n");
                    continue;
                }
                case SvgShape.KindEndClip:
                case SvgShape.KindEndMask:
                    b.Ascii("Q\n");
                    if (states.Count > 0) RestoreState(states.Pop());
                    continue;
                case SvgShape.KindFilter:
                    WriteSvgFilter(b, shape, toPage);
                    continue;
                case SvgShape.KindImage:
                    WriteSvgImage(b, shape, toPage);
                    continue;
                case SvgShape.KindText:
                    WriteSvgPaint(b, shape, toPage);
                    WriteSvgText(b, shape, shape.HasFill ? (shape.HasStroke ? 2 : 0) : 1);
                    continue;
            }
            WritePath(b, shape);
            WriteSvgPaint(b, shape, toPage);
            if (shape.HasFill && shape.HasStroke) b.Ascii(shape.EvenOdd ? "B*\n" : "B\n");
            else if (shape.HasFill) b.Ascii(shape.EvenOdd ? "f*\n" : "f\n");
            else b.Ascii("S\n");
        }
        // Unbalanced groups (none are made, but a cut-off drawing must not leave the stream's state saved).
        while (states.Count > 0)
        {
            b.Ascii("Q\n");
            RestoreState(states.Pop());
        }
    }

    /// <summary>Writes an SVG text run with the text rendering mode given (3 draws nothing but keeps the text selectable).</summary>
    private void WriteSvgText(ByteBuffer b, SvgShape shape, int mode)
    {
        var m = shape.TextMatrix;
        var svgFont = Leaf(shape.Font!);
        if (svgFont is EmbeddedFont svgEmbedded) b.Ascii("BT /E").Int(_subsetList.IndexOf(_subsets[svgEmbedded.Program]) + 1);
        else b.Ascii("BT /F").Int(((StandardFont)shape.Font!).Index + 1);
        b.Sp().Num(shape.FontSize).Ascii(" Tf ").Int(mode).Ascii(" Tr 0 Tw ")
            .Num(shape.CharSpacing).Ascii(" Tc ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
            .Ascii(" Tm ");
        if (svgFont is EmbeddedFont svgFontEmbedded) WriteGlyphHex(b, svgFontEmbedded, _subsets[svgFontEmbedded.Program], shape.Text!, shape.FontSize, shape.Rtl, shape.CharSpacing);
        else WriteStandardText(b, shape.Font!, shape.Rtl ? Text.Bidi.VisualText(shape.Text!) : shape.Text!);
        b.Ascii(" ET\n");
    }

    /// <summary>The text runs drawn by filtered content, written as invisible text over the filter's image.</summary>
    private void WriteInvisibleSvgText(ByteBuffer b, List<SvgShape> shapes, int depth)
    {
        if (depth > 16) return;
        foreach (var shape in shapes)
        {
            if (shape.Kind == SvgShape.KindText) WriteSvgText(b, shape, 3);
            else if (shape.Kind == SvgShape.KindFilter && shape.Children != null) WriteInvisibleSvgText(b, shape.Children, depth + 1);
        }
    }

    private static ByteBuffer WriteMatrix(ByteBuffer b, Matrix m)
        => b.Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F);

    /// <summary>
    /// Adds the soft mask of an SVG mask: a transparency group drawing the mask content inside the mask region, in the
    /// stream's default space. Returns the number of the graphics state that sets it.
    /// </summary>
    private int AddSvgMask(SvgShape mask, Matrix toPage)
    {
        var content = new ByteBuffer(256);
        var saved = SaveState();
        // The group starts from the graphics state of the masked content: nothing about its colours is known.
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
        _anyAlpha = true;
        WriteMatrix(content, toPage).Ascii(" cm\n");
        WritePath(content, mask);
        content.Ascii("W n\n");
        WriteSvgShapes(content, mask.Children!, toPage);
        RestoreState(saved);
        string dict = "/Type /XObject /Subtype /Form /BBox [-32767 -32767 32767 32767] /Group << /S /Transparency /CS /DeviceRGB >> /Resources "
            + _resourcesId.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R";
        _masks.Add((content.Span.ToArray(), dict, mask.MaskAlpha));
        return _masks.Count;
    }

    /// <summary>Adds a tiling pattern repeating an SVG pattern's tile and returns its number.</summary>
    private int AddSvgTiling(SvgPattern pattern, Matrix toPage)
    {
        var cell = new ByteBuffer(256);
        var saved = SaveState();
        // A pattern cell starts from the default graphics state and uses the shared resources.
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (CssColor.Black, CssColor.Black, 1, 255, 255);
        WriteSvgShapes(cell, pattern.Shapes, Matrix.Identity);
        RestoreState(saved);
        var m = pattern.ToRoot.Then(toPage);
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(pattern.Width).Sp().Num(pattern.Height)
            .Ascii("] /XStep ").Num(pattern.Width).Ascii(" /YStep ").Num(pattern.Height).Ascii(" /Matrix [");
        WriteMatrix(dict, m).Ascii("] /Resources ").Int(_resourcesId).Ascii(" 0 R");
        int n = _tilings.Count + 1;
        _tilings.Add((n, cell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span)));
        return n;
    }

    /// <summary>
    /// Draws an SVG image element: a raster image as an image XObject (one per image, however often it is drawn), or an
    /// embedded SVG drawing's shapes in their own space. A translucent drawing is a transparency group, so that its
    /// overlapping parts do not show through each other.
    /// </summary>
    private void WriteSvgImage(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        if (shape.Image is { } source)
        {
            if (!_rasterNumbers.TryGetValue(source, out int n))
            {
                // A copy: the same image may also be drawn by an img element, numbered with the page images.
                var image = Conformance != PdfConformance.None ? source.ToRgb() : source;
                n = image == null ? 0 : _rasters.Count + 1;
                if (image != null) _rasters.Add(image.Copy());
                _rasterNumbers[source] = n;
            }
            if (n == 0) return;
            SetFillAlpha(b, (uint)shape.ImageAlpha << 24); // an image is painted with the fill alpha
            b.Ascii("q ");
            WriteMatrix(b, shape.ImageMatrix).Ascii(" cm /Xr").Int(n).Ascii(" Do Q\n");
            return;
        }
        if (shape.Children is not { Count: > 0 } children) return;
        var inner = shape.ImageMatrix.Then(toPage);
        var saved = SaveState();
        if (shape.ImageAlpha == 255)
        {
            b.Ascii("q ");
            WriteMatrix(b, shape.ImageMatrix).Ascii(" cm\n");
            WriteSvgShapes(b, children, inner);
            b.Ascii("Q\n");
            RestoreState(saved);
            return;
        }
        var group = new ByteBuffer(1024);
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1); // a group starts from unknown colours
        _anyAlpha = true;
        WriteMatrix(group, shape.ImageMatrix).Ascii(" cm\n");
        WriteSvgShapes(group, children, inner);
        RestoreState(saved);
        _forms.Add(group.Span.ToArray());
        _formIsolated.Add(false);
        _formStreams.Add(null);
        _groupAlphas.Add(shape.ImageAlpha);
        b.Ascii("q /g").Int(shape.ImageAlpha).Ascii(" gs /Fx").Int(_forms.Count).Ascii(" Do Q\n");
    }

    /// <summary>The resolution of SVG filter results: pixels per point (2 is 144 dpi), and the most pixels one may have.</summary>
    private const float FilterPixelsPerPoint = 2;
    private const long MaxFilterPixels = 4_000_000;

    /// <summary>
    /// Draws filtered SVG content: the content is rendered to pixels over the filter region, the filter runs on them,
    /// and the result is drawn as an image with an alpha mask.
    /// </summary>
    private void WriteSvgFilter(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        var f = shape.Filter!;
        if (!(f.W > 0 && f.H > 0)) return;
        // Pixels per root unit, from the size of a root unit in the stream's default space.
        float kx = MathF.Sqrt(toPage.A * toPage.A + toPage.B * toPage.B) * FilterPixelsPerPoint;
        float ky = MathF.Sqrt(toPage.C * toPage.C + toPage.D * toPage.D) * FilterPixelsPerPoint;
        double pw = Math.Ceiling(f.W * kx), ph = Math.Ceiling(f.H * ky);
        if (!(pw >= 1 && ph >= 1)) return;
        if (pw * ph > MaxFilterPixels)
        {
            double k = Math.Sqrt(MaxFilterPixels / (pw * ph));
            pw = Math.Max(1, Math.Floor(pw * k));
            ph = Math.Max(1, Math.Floor(ph * k));
        }
        int w = (int)pw, h = (int)ph;
        var source = RasterizeSvg(shape.Children!, f.X, f.Y, f.W, f.H, w, h);
        if (source == null) return;
        var image = FilterImage(f.Apply(source, w, h, w / f.W, h / f.H, shapes => RasterizeSvg(shapes, f.X, f.Y, f.W, f.H, w, h)), w, h);
        if (image == null) return;
        _rasters.Add(image);
        SetFillAlpha(b, CssColor.Black); // an image is painted with the fill alpha
        // The image's unit square onto the region, its first row at the top (user space has y down).
        b.Ascii("q ").Num(f.W).Ascii(" 0 0 ").Num(-f.H).Sp().Num(f.X).Sp().Num(f.Y + f.H).Ascii(" cm /Xr").Int(_rasters.Count).Ascii(" Do Q\n");
        // The image hides the text in it from search and selection: the same text again, invisible, brings it back.
        WriteInvisibleSvgText(b, shape.Children!, 0);
    }

    /// <summary>A filter's result (premultiplied sRGB floats) as an RGB image with an alpha mask; null when it is all transparent.</summary>
    private static PdfImage? FilterImage(float[] result, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool any = false;
        for (int i = 0, o = 0; i < w * h; i++, o += 3)
        {
            float a = Math.Clamp(result[i * 4 + 3], 0, 1);
            if (a <= 0) continue;
            any = true;
            alpha[i] = (byte)MathF.Round(a * 255);
            rgb[o] = (byte)MathF.Round(Math.Clamp(result[i * 4] / a, 0, 1) * 255);
            rgb[o + 1] = (byte)MathF.Round(Math.Clamp(result[i * 4 + 1] / a, 0, 1) * 255);
            rgb[o + 2] = (byte)MathF.Round(Math.Clamp(result[i * 4 + 2] / a, 0, 1) * 255);
        }
        if (!any) return null;
        return new PdfImage
        {
            Width = w, Height = h, Data = PdfImage.Deflate(rgb),
            SMask = new PdfImage { Width = w, Height = h, ColorSpace = "/DeviceGray", Data = PdfImage.Deflate(alpha) },
        };
    }

    /// <summary>
    /// Renders SVG shapes over the root-space rectangle (x, y, w, h) into a pixels × pixels image, through a one-page
    /// PDF of their own. Returns premultiplied sRGB floats (RGBA), or null when the shapes cannot be rendered.
    /// </summary>
    private float[]? RasterizeSvg(List<SvgShape> shapes, float x, float y, float w, float h, int pixelsW, int pixelsH)
    {
        var drawing = new SvgImage { HasViewBox = true, ViewBoxX = x, ViewBoxY = y, ViewBoxWidth = w, ViewBoxHeight = h, Align = "none" };
        drawing.Shapes.AddRange(shapes);
        var page = new PageSetup { Width = pixelsW, Height = pixelsH };
        byte[] pdf;
        try
        {
            var writer = new PdfDocumentWriter(page, compress: false, CompressionLevel.NoCompression) { Cancellation = Cancellation };
            pdf = writer.Write(new List<DrawOp> { new SvgOp(0, 0, pixelsW, pixelsH, drawing) }, pixelsH,
                new Dictionary<string, float>(), 0, null, null, null, null, null);
        }
        catch (PdfLimitException) { return null; }
        return RenderPremultiplied(pdf, pixelsW, pixelsH);
    }

    /// <summary>The first page of a PDF rendered at pixelsW × pixelsH on transparency, as premultiplied sRGB floats (RGBA).</summary>
    private float[]? RenderPremultiplied(byte[] pdf, int pixelsW, int pixelsH)
    {
        var rendered = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Width = pixelsW, Height = pixelsH, Transparent = true }, Cancellation);
        if (rendered.Width != pixelsW || rendered.Height != pixelsH) return null;
        var px = rendered.Premultiplied;
        var result = new float[px.Length * 4];
        for (int i = 0; i < px.Length; i++)
        {
            uint p = px[i];
            if (p == 0) continue;
            result[i * 4] = (p & 255) / 255f;
            result[i * 4 + 1] = ((p >> 8) & 255) / 255f;
            result[i * 4 + 2] = ((p >> 16) & 255) / 255f;
            result[i * 4 + 3] = (p >> 24) / 255f;
        }
        return result;
    }

    private static void WritePath(ByteBuffer b, SvgShape shape)
    {
        var pts = shape.Points;
        int k = 0;
        foreach (var verb in shape.Verbs)
        {
            switch (verb)
            {
                case SvgShape.MoveTo: b.Num(pts[k]).Sp().Num(pts[k + 1]).Ascii(" m\n"); k += 2; break;
                case SvgShape.LineTo: b.Num(pts[k]).Sp().Num(pts[k + 1]).Ascii(" l\n"); k += 2; break;
                case SvgShape.CurveTo:
                    b.Num(pts[k]).Sp().Num(pts[k + 1]).Sp().Num(pts[k + 2]).Sp().Num(pts[k + 3]).Sp().Num(pts[k + 4]).Sp().Num(pts[k + 5]).Ascii(" c\n");
                    k += 6;
                    break;
                default: b.Ascii("h\n"); break;
            }
        }
    }

    /// <summary>Sets the fill and stroke of an SVG shape: colours, or shading patterns for gradients.</summary>
    private void WriteSvgPaint(ByteBuffer b, SvgShape shape, Matrix toPage)
    {
        if (shape.FillGradient is { } fg)
        {
            SetFillAlpha(b, CssColor.Black); // the gradient's stops carry the opacity
            b.Ascii("/Pattern cs /P").Int(AddPattern(fg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" scn\n");
            _fill = 1; // not a colour: the next colour must be written
        }
        else if (shape.FillPattern is { } fp)
        {
            int tile = AddSvgTiling(fp, toPage);
            SetFillAlpha(b, (uint)fp.Alpha << 24);
            b.Ascii("/Pattern cs /T").Int(tile).Ascii(" scn\n");
            _fill = 1;
        }
        else if (shape.Fill is { } fill) SetFill(b, fill);
        if (!shape.HasStroke) return;
        if (shape.StrokeGradient is { } sg)
        {
            SetStrokeAlpha(b, CssColor.Black);
            b.Ascii("/Pattern CS /P").Int(AddPattern(sg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" SCN ");
            _stroke = 1;
        }
        else if (shape.StrokePattern is { } spat)
        {
            int tile = AddSvgTiling(spat, toPage);
            SetStrokeAlpha(b, (uint)spat.Alpha << 24);
            b.Ascii("/Pattern CS /T").Int(tile).Ascii(" SCN ");
            _stroke = 1;
        }
        else SetStroke(b, shape.Stroke!.Value);
        _lineWidth = -1;
        b.Num(shape.StrokeWidth).Ascii(" w ").Int((int)shape.LineCap).Ascii(" J ").Int((int)shape.LineJoin).Ascii(" j ")
            .Num(shape.MiterLimit).Ascii(" M\n");
    }

    /// <summary>Appends the outline of a rounded rectangle given in flow coordinates, as a closed path.</summary>
    private void RoundRectPath(ByteBuffer b, float x, float y, float w, float h, in CornerRadii r, float pageTop)
    {
        const float k = 0.5523f; // a quarter ellipse as one cubic Bézier
        float r0 = x + w, b0 = y + h;
        void Line(float px, float py) => b.Num(PX(px)).Sp().Num(PY(py, pageTop)).Ascii(" l ");
        // A corner from (ax, ay) to (bx, by) around the box corner (cx, cy).
        void Corner(float ax, float ay, float cx, float cy, float bx, float by)
        {
            if (ax == bx && ay == by) return;
            b.Num(PX(ax + k * (cx - ax))).Sp().Num(PY(ay + k * (cy - ay), pageTop)).Sp()
                .Num(PX(bx + k * (cx - bx))).Sp().Num(PY(by + k * (cy - by), pageTop)).Sp()
                .Num(PX(bx)).Sp().Num(PY(by, pageTop)).Ascii(" c ");
        }
        b.Num(PX(x + r.TLx)).Sp().Num(PY(y, pageTop)).Ascii(" m ");
        Line(r0 - r.TRx, y);
        Corner(r0 - r.TRx, y, r0, y, r0, y + r.TRy);
        Line(r0, b0 - r.BRy);
        Corner(r0, b0 - r.BRy, r0, b0, r0 - r.BRx, b0);
        Line(x + r.BLx, b0);
        Corner(x + r.BLx, b0, x, b0, x, b0 - r.BLy);
        Line(x, y + r.TLy);
        Corner(x, y + r.TLy, x, y, x + r.TLx, y);
        b.Ascii("h\n");
    }

    /// <summary>Draws a rounded rectangle, ring or shadow, limited to the part of it on this page.</summary>
    private void WriteRoundRect(ByteBuffer b, RoundRectOp rr, in Placed placed, float pageTop)
    {
        // Edges on whole CSS pixels, like the edges of square boxes.
        float x0 = SnapPx(rr.X), x1 = SnapPx(rr.X + rr.W), y0 = SnapPx(rr.Y), y1 = SnapPx(rr.Y + rr.H);
        if (x1 <= x0) { x0 = rr.X; x1 = rr.X + rr.W; }
        if (y1 <= y0) { y0 = rr.Y; y1 = rr.Y + rr.H; }
        var saved = SaveState();
        b.Ascii("q ");
        if (placed.Y0 > rr.Y + 0.01f || placed.Y1 < rr.Y + rr.H - 0.01f)
        {
            // Split by a page break: only this page's slice.
            b.Num(PX(x0 - 1)).Sp().Num(PY(placed.Y1, pageTop)).Sp().Num(x1 - x0 + 2).Sp().Num(placed.Y1 - placed.Y0).Ascii(" re W n\n");
        }
        if (rr.Clip is { Length: >= 6 } poly)
        {
            b.Num(PX(poly[0])).Sp().Num(PY(poly[1], pageTop)).Ascii(" m ");
            for (int i = 2; i < poly.Length; i += 2) b.Num(PX(poly[i])).Sp().Num(PY(poly[i + 1], pageTop)).Ascii(" l ");
            b.Ascii("h W n\n");
        }
        if (rr.HasHole)
        {
            // Everything around the outer shape but the hole: the outer shape is then painted without it.
            float hx0 = SnapPx(rr.HoleX), hx1 = SnapPx(rr.HoleX + rr.HoleW), hy0 = SnapPx(rr.HoleY), hy1 = SnapPx(rr.HoleY + rr.HoleH);
            if (hx1 <= hx0) { hx0 = rr.HoleX; hx1 = rr.HoleX + rr.HoleW; }
            if (hy1 <= hy0) { hy0 = rr.HoleY; hy1 = rr.HoleY + rr.HoleH; }
            float ax = Math.Min(x0, hx0) - 1, ay = Math.Min(y0, hy0) - 1, bx = Math.Max(x1, hx1) + 1, by = Math.Max(y1, hy1) + 1;
            b.Num(PX(ax)).Sp().Num(PY(by, pageTop)).Sp().Num(bx - ax).Sp().Num(by - ay).Ascii(" re\n");
            if (hx1 > hx0 && hy1 > hy0) RoundRectPath(b, hx0, hy0, hx1 - hx0, hy1 - hy0, rr.HoleR, pageTop);
            b.Ascii("W* n\n");
        }
        if (rr.StrokeWidth > 0)
        {
            // Stroked along the middle of the border.
            float half = rr.StrokeWidth / 2;
            SetStroke(b, rr.Color);
            b.Num(rr.StrokeWidth).Ascii(" w ");
            if (rr.Style == BorderStyle.Dotted) b.Ascii("1 J [0 ").Num(rr.StrokeWidth * 2).Ascii("] 0 d\n");
            else if (rr.Style == BorderStyle.Dashed) b.Ascii("[").Num(rr.StrokeWidth * 3).Sp().Num(rr.StrokeWidth * 3).Ascii("] 0 d\n");
            RoundRectPath(b, x0 + half, y0 + half, x1 - x0 - rr.StrokeWidth, y1 - y0 - rr.StrokeWidth,
                rr.R.Inset(half, half, half, half) ?? default, pageTop);
            b.Ascii("S Q\n");
        }
        else
        {
            SetFill(b, rr.Color);
            RoundRectPath(b, x0, y0, x1 - x0, y1 - y0, rr.R, pageTop);
            b.Ascii("f Q\n");
        }
        RestoreState(saved);
    }

    /// <summary>Draws one background layer: a single clipped tile, or a tiling pattern filling the painted area.</summary>
    private void WriteBackground(ByteBuffer b, BackgroundOp bg, in Placed placed, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var (x0, y0, x1, y1) = bg.Painted();
        y0 = Math.Max(y0, placed.Y0);
        y1 = Math.Min(y1, placed.Y1);
        if (!(x1 > x0 && y1 > y0)) return;
        SetFillAlpha(b, CssColor.Black); // images and patterns are painted with the fill alpha
        if (bg.ClipRadii is { } cr)
        {
            // Rounded corners: clip to the rounded painting area, then paint as usual.
            b.Ascii("q ");
            RoundRectPath(b, SnapPx(bg.ClipX), SnapPx(bg.ClipY), SnapPx(bg.ClipX + bg.ClipW) - SnapPx(bg.ClipX),
                SnapPx(bg.ClipY + bg.ClipH) - SnapPx(bg.ClipY), cr, pageTop);
            b.Ascii("W n\n");
            bg.ClipRadii = null;
            try { WriteBackground(b, bg, placed, pageTop, imageIds); }
            finally { bg.ClipRadii = cr; }
            b.Ascii("Q\n");
            return;
        }
        float tx = PX(bg.TileX), tw = bg.TileW, th = bg.TileH;
        // The image inside the tile, relative to the tile's top-left corner: all of it unless the tile shows a slice.
        var (ix, iy, iw, ih) = bg.Inner ?? (0, 0, tw, th);
        const float eps = 0.01f;
        bool single = (!bg.RepeatX || bg.TileX <= x0 + eps && bg.TileX + tw >= x1 - eps)
                      && (!bg.RepeatY || bg.TileY <= y0 + eps && bg.TileY + th >= y1 - eps);
        if (single && bg.Gradient is { } g)
        {
            // Gradient space is the tile; the pattern maps it to the page.
            var toPage = new Matrix(1, 0, 0, -1, tx, PY(bg.TileY, pageTop));
            float gx0 = x0 - bg.TileX, gy0 = y0 - bg.TileY, gx1 = x1 - bg.TileX, gy1 = y1 - bg.TileY;
            b.Ascii("q ");
            var channel = GradientChannel.Flattened;
            if (g.Alphas != null)
            {
                // Translucent stops: the unblended colours, through a soft mask holding the alphas.
                channel = GradientChannel.Straight;
                b.Ascii("/s").Int(AddSoftMask(g, toPage, gx0, gy0, gx1, gy1, PX(x0), PY(y1, pageTop), x1 - x0, y1 - y0)).Ascii(" gs ");
            }
            int name = AddPattern(g, toPage, gx0, gy0, gx1, gy1, channel);
            b.Ascii("/Pattern cs /P").Int(name).Ascii(" scn ");
            b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
            return;
        }
        if (bg.Svg is { } svg)
        {
            if (single)
            {
                b.Ascii("q ").Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re W n\n");
                WriteSlicedSvg(b, svg, tx + ix, PY(bg.TileY + iy, pageTop), iw, ih, bg.Inner != null);
                b.Ascii("Q\n");
                return;
            }
            // Repeated: a tiling pattern whose cell draws the SVG image once. The cell starts from the default
            // graphics state and uses the shared resources (fonts, alpha states, gradients).
            var svgCell = new ByteBuffer(256);
            var saved = SaveState();
            (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (CssColor.Black, CssColor.Black, 1, 255, 255);
            WriteSlicedSvg(svgCell, svg, ix, th - iy, iw, ih, bg.Inner != null);
            RestoreState(saved);
            var svgDict = new ByteBuffer(256);
            svgDict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(tw).Sp().Num(th)
                .Ascii("] /XStep ").Num(Math.Max(bg.StepX, tw)).Ascii(" /YStep ").Num(Math.Max(bg.StepY, th))
                .Ascii(" /Matrix [1 0 0 1 ").Num(tx).Sp().Num(PY(bg.TileY + th, pageTop)).Ascii("] /Resources ").Int(_resourcesId).Ascii(" 0 R");
            int svgTile = _tilings.Count + 1;
            _tilings.Add((svgTile, svgCell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(svgDict.Span)));
            b.Ascii("q /Pattern cs /T").Int(svgTile).Ascii(" scn ");
            b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
            return;
        }
        if (single)
        {
            b.Ascii("q ").Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re W n ")
                .Num(iw).Ascii(" 0 0 ").Num(ih).Sp().Num(tx + ix).Sp().Num(PY(bg.TileY + iy + ih, pageTop))
                .Ascii(" cm /Im").Int(imageIds[bg.Image!]).Ascii(" Do Q\n");
            return;
        }

        // A tiling pattern whose cell is the tile, its origin at the tile's bottom-left corner.
        var cell = new ByteBuffer(64);
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 ").Num(tw).Sp().Num(th)
            .Ascii("] /XStep ").Num(Math.Max(bg.StepX, tw)).Ascii(" /YStep ").Num(Math.Max(bg.StepY, th))
            .Ascii(" /Matrix [1 0 0 1 ").Num(tx).Sp().Num(PY(bg.TileY + th, pageTop)).Ascii("] /Resources << ");
        if (bg.Gradient is { } tg)
        {
            var m = tg.Transform;
            cell.Ascii("1 0 0 -1 0 ").Num(th).Ascii(" cm ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
                .Ascii(" cm /Sh1 sh\n");
            dict.Ascii("/Shading << /Sh1 ");
            tg.WriteShading(dict, 0, 0, tw, th);
            dict.Ascii(" >>");
        }
        else
        {
            // The written image: PDF/A may have replaced a CMYK image with an RGB copy.
            var image = _imageList[imageIds[bg.Image!] - 1];
            cell.Num(iw).Ascii(" 0 0 ").Num(ih).Sp().Num(ix).Sp().Num(th - iy - ih).Ascii(" cm /Im1 Do\n");
            dict.Ascii("/XObject << /Im1 ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        }
        dict.Ascii(" >>");
        int n = _tilings.Count + 1;
        _tilings.Add((n, cell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span)));
        b.Ascii("q /Pattern cs /T").Int(n).Ascii(" scn ");
        b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
    }

    /// <summary>
    /// Draws an SVG image into (left, top, w, h). An SVG file without a viewBox keeps its scale whatever box it is drawn
    /// into, but the slices of a border image (<paramref name="sliced"/>) scale the image as a picture, so it is drawn at
    /// its own size and the whole drawing scaled to the box.
    /// </summary>
    private void WriteSlicedSvg(ByteBuffer b, SvgImage svg, float left, float top, float w, float h, bool sliced)
    {
        if (sliced && !svg.HasViewBox && svg.Width is { } nw && svg.Height is { } nh && nw > 0 && nh > 0 && (w != nw || h != nh))
        {
            b.Ascii("q ").Num6(w / nw).Ascii(" 0 0 ").Num6(h / nh).Sp().Num(left).Sp().Num(top).Ascii(" cm\n");
            WriteSvg(b, svg, 0, 0, nw, nh);
            b.Ascii("Q\n");
            InvalidateState();
            return;
        }
        WriteSvg(b, svg, left, top, w, h);
    }

    /// <summary>Adds a shading pattern to the shared resources and returns its number.</summary>
    private int AddPattern(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened)
    {
        int n = ++_patternCount;
        _patterns.Ascii(" /P").Int(n).Sp();
        g.WritePattern(_patterns, toPage, minX, minY, maxX, maxY, channel);
        return n;
    }

    /// <summary>
    /// Adds a luminosity soft mask holding a gradient's alphas over the rectangle (x, y, w, h) of user space, and returns
    /// the number of the graphics state that sets it.
    /// </summary>
    private int AddSoftMask(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY, float x, float y, float w, float h)
    {
        var content = new ByteBuffer(64);
        content.Ascii("/Pattern cs /M scn ").Num(x).Sp().Num(y).Sp().Num(w).Sp().Num(h).Ascii(" re f\n");
        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [").Num(x).Sp().Num(y).Sp().Num(x + w).Sp().Num(y + h)
            .Ascii("] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Pattern << /M ");
        g.WritePattern(dict, toPage, minX, minY, maxX, maxY, GradientChannel.Alpha);
        dict.Ascii(" >> >>");
        _masks.Add((content.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span), false));
        return _masks.Count;
    }

    // ------------------------------------------------------------------ text

    private void CollectSvgFonts(SvgImage svg, bool[] usedFonts) => CollectSvgFonts(svg.Shapes, usedFonts, 0);

    /// <summary>The fonts of SVG text, also inside masks, pattern tiles, embedded drawings and filtered content.</summary>
    private void CollectSvgFonts(List<SvgShape> shapes, bool[] usedFonts, int depth)
    {
        if (depth > 16) return;
        foreach (var shape in shapes)
        {
            if (shape.Kind is SvgShape.KindMask or SvgShape.KindImage or SvgShape.KindFilter && shape.Children != null) CollectSvgFonts(shape.Children, usedFonts, depth + 1);
            if (shape.FillPattern != null) CollectSvgFonts(shape.FillPattern.Shapes, usedFonts, depth + 1);
            if (shape.StrokePattern != null) CollectSvgFonts(shape.StrokePattern.Shapes, usedFonts, depth + 1);
            if (shape.Font == null || shape.Kind != SvgShape.KindText) continue;
            if (Leaf(shape.Font) is EmbeddedFont ef) AddGlyphs(ef, shape.Text!, 0, shape.Text!.Length, shape.Rtl);
            else usedFonts[((StandardFont)shape.Font).Index] = true;
            CheckCharacters(shape.Font, shape.Text!, 0, shape.Text!.Length);
        }
    }

    private void CollectFonts(TextOp t, bool[] usedFonts)
    {
        if (t.Font is StandardFont sf && !EmbedStandard)
        {
            usedFonts[sf.Index] = true;
            CheckCharacters(sf, t.Text, 0, t.Text.Length);
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        foreach (var run in _runs)
        {
            if (Leaf(run.Font) is EmbeddedFont ef) AddGlyphs(ef, t.Text, run.Start, run.Length, t.Rtl);
            else usedFonts[((StandardFont)run.Font).Index] = true;
            CheckCharacters(run.Font, t.Text, run.Start, run.Length);
        }
    }

    /// <summary>Reports the characters of a run that its font has no glyph for, when <see cref="MissingCharacter"/> is set.</summary>
    private void CheckCharacters(TextFont font, string text, int start, int length)
    {
        if (MissingCharacter == null) return;
        var embedded = Leaf(font) as EmbeddedFont;
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            if (Invisible(cp)) continue;
            bool has = embedded != null
                ? embedded.Program.GlyphId(cp) != 0
                : cp == '?' || (cp < 0x10000 && WinAnsi.Encode((char)cp) != (byte)'?');
            if (has || !(_missingReported ??= new()).Add(cp)) continue;
            MissingCharacter(char.ConvertFromUtf32(char.IsSurrogate((char)cp) && cp < 0x10000 ? 0xFFFD : cp));
        }

        // Spaces, controls and format characters (soft hyphen, zero-width space and joiners, variation selectors) draw nothing.
        static bool Invisible(int cp)
            => cp <= 0x20 || (cp >= 0x7F && cp <= 0xA0) || cp == 0xAD || (cp >= 0x2000 && cp <= 0x200F) || (cp >= 0x2028 && cp <= 0x202F)
               || (cp >= 0x205F && cp <= 0x206F) || cp == 0x3000 || cp == 0xFEFF || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF);
    }

    /// <summary>
    /// Shows a text op starting at (x, y), one run per concrete font. Right-to-left text is drawn from its last run to
    /// its first.
    /// </summary>
    private void WriteTextLine(ByteBuffer b, TextOp t, float x, float y)
    {
        if (t.Font is StandardFont sf && !EmbedStandard)
        {
            WriteStandardRun(b, sf, t, 0, t.Text.Length, x, y);
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        for (int r = 0; r < _runs.Count; r++)
        {
            var run = _runs[t.Rtl ? _runs.Count - 1 - r : r];
            if (Leaf(run.Font) is EmbeddedFont ef) WriteEmbeddedRun(b, ef, t, run.Start, run.Length, x, y);
            else WriteStandardRun(b, (StandardFont)run.Font, t, run.Start, run.Length, x, y);
            x += RunAdvance(run.Font, t, run.Start, run.Length);
        }
    }

    private readonly List<ShapedGlyph> _glyphs = new();

    private void AddGlyphs(EmbeddedFont font, string text, int start, int length, bool rtl = false)
    {
        var subset = Subset(font.Program);
        font.Shape(text, start, length, _glyphs, rtl);
        foreach (var g in _glyphs) subset.Cid(g, text);
    }

    /// <summary>
    /// Shows text as hex strings of CIDs: with TJ when the font kerns, so kerned pairs can move glyphs, or when complex
    /// shaping positioned the glyphs. Right-to-left text is shown last glyph first.
    /// </summary>
    private void WriteGlyphHex(ByteBuffer b, EmbeddedFont font, GlyphSubset subset, string text, float size, bool rtl, float charSpacing = 0)
    {
        bool positioned = font.Shape(text, 0, text.Length, _glyphs, rtl);
        if (rtl) _glyphs.Reverse();
        // PDF/A and PDF/UA forbid showing .notdef: characters the font lacks become blank space of the same width.
        bool skipNotdef = NoNotdef && size > 0;
        float notdefAdjust = skipNotdef ? -(font.Program.Advance1000(0) + charSpacing * 1000f / size) : 0;
        if (positioned && size > 0)
        {
            WritePositionedGlyphs(b, font, subset, text, size, 0, skipNotdef, notdefAdjust, 0);
            return;
        }
        bool array = font.HasKerning || skipNotdef;
        int prev = -1;
        b.Ascii(array ? "[<" : "<");
        foreach (var g in _glyphs)
        {
            if (font.HasKerning && prev >= 0 && (rtl ? font.Program.Kern1000(g.Gid, prev) : font.Program.Kern1000(prev, g.Gid)) is int k && k != 0)
                b.Ascii(">").Int(-k).Ascii("<");
            prev = g.Gid;
            int cid = subset.Cid(g, text);
            if (cid == 0 && skipNotdef) b.Ascii(">").Num(notdefAdjust).Ascii("<");
            else Hex4(b, cid);
        }
        b.Ascii(array ? ">] TJ" : "> Tj");
    }

    private GlyphSubset Subset(FontProgram program)
    {
        if (!_subsets.TryGetValue(program, out var s))
        {
            _subsets[program] = s = new GlyphSubset(program);
            _subsetList.Add(s);
        }
        return s;
    }

    private void SetFont(ByteBuffer b, TextFont font, string resource, int index, float size)
    {
        if (ReferenceEquals(_textFont, font) && _textSize == size) return;
        _textFont = font;
        _textSize = size;
        b.Ascii(resource).Int(index).Sp().Num(size).Ascii(" Tf\n");
    }

    private void SetRenderMode(ByteBuffer b, int mode)
    {
        if (_tr == mode) return;
        _tr = mode;
        b.Int(mode).Ascii(" Tr\n");
    }

    private void WriteStandardRun(ByteBuffer b, StandardFont font, TextOp t, int start, int length, float x, float y)
    {
        // Font objects are keyed by the leaf font in SetFont, so an embedded font of the same size is switched back.
        SetFont(b, font, "/F", font.Index + 1, t.Size);
        SetRenderMode(b, 0);
        b.Ascii("1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");
        string text = start == 0 && length == t.Text.Length ? t.Text : t.Text.Substring(start, length);
        WriteStandardText(b, font, t.Rtl ? Text.Bidi.VisualText(text) : text);
        b.Ascii("\n");
    }

    /// <summary>Shows text in a standard font: one string with Tj, or with TJ when kerned pairs move glyphs.</summary>
    private static void WriteStandardText(ByteBuffer b, TextFont font, string text)
    {
        int first = -1;
        if (font.HasKerning)
            for (int i = 1; i < text.Length && first < 0; i++)
                if (font.KernUnits(text[i - 1], text[i]) != 0) first = i;
        if (first < 0)
        {
            WriteTextBytes(b, text);
            b.Ascii(" Tj");
            return;
        }
        b.Byte((byte)'[');
        int runStart = 0;
        for (int i = first; i < text.Length; i++)
        {
            int k = font.KernUnits(text[i - 1], text[i]);
            if (k == 0) continue;
            WriteTextBytes(b, text.Substring(runStart, i - runStart));
            b.Int(-k);
            runStart = i;
        }
        WriteTextBytes(b, text.Substring(runStart));
        b.Ascii("] TJ");
    }

    private void WriteEmbeddedRun(ByteBuffer b, EmbeddedFont font, TextOp t, int start, int length, float x, float y)
    {
        var subset = _subsets[font.Program];
        SetFont(b, font, "/E", _subsetList.IndexOf(subset) + 1, t.Size);
        if (font.SyntheticBold)
        {
            // Stroke the outlines in the fill colour, as browsers widen glyphs for a missing bold face.
            float k = Math.Clamp((t.Size - 9f) / 27f, 0f, 1f);
            float width = t.Size * (1f / 24f + (1f / 32f - 1f / 24f) * k);
            SetRenderMode(b, 2);
            SetStroke(b, t.Color);
            if (_lineWidth != width) { _lineWidth = width; b.Num(width).Ascii(" w\n"); }
        }
        else SetRenderMode(b, 0);
        b.Ascii(font.SyntheticItalic ? "1 0 0.2 1 " : "1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");

        // Word spacing (Tw) only applies to the single-byte code 32, so justified text moves spaces explicitly.
        // PDF/A forbids showing .notdef: characters no font has become blank space of the same width.
        string text = t.Text;
        bool skipNotdef = NoNotdef && t.Size > 0;
        // Kerning moves the glyph after a kerned pair, as the layout measured it.
        bool kern = font.HasKerning && t.Size > 0;
        bool adjust = (t.WordSpacing != 0 || skipNotdef || kern) && t.Size > 0;
        float spaceAdjust = adjust ? -t.WordSpacing * 1000f / t.Size : 0;
        float notdefAdjust = skipNotdef ? -(font.Program.Advance1000(0) + t.CharSpacing * 1000f / t.Size) : 0;
        bool positioned = font.Shape(text, start, length, _glyphs, t.Rtl);
        if (t.Rtl) _glyphs.Reverse();
        if (positioned && t.Size > 0)
        {
            WritePositionedGlyphs(b, font, subset, t.Text, t.Size, t.WordSpacing, skipNotdef, notdefAdjust, spaceAdjust);
            return;
        }
        b.Ascii(adjust ? "[<" : "<");
        int prev = -1;
        for (int k = 0; k < _glyphs.Count; k++)
        {
            var g = _glyphs[k];
            // Right to left the glyphs are drawn last first, and a kerned pair is the drawn one's logical successor.
            if (kern && prev >= 0 && (t.Rtl ? font.Program.Kern1000(g.Gid, prev) : font.Program.Kern1000(prev, g.Gid)) is int kv && kv != 0)
                b.Ascii(">").Int(-kv).Ascii("<");
            prev = g.Gid;
            int cid = subset.Cid(g, text);
            if (cid == 0 && skipNotdef) b.Ascii(">").Num(notdefAdjust).Ascii("<");
            else Hex4(b, cid);
            if (adjust && text[g.Start] == ' ' && t.WordSpacing != 0 && k + 1 < _glyphs.Count) b.Ascii(">").Num(spaceAdjust).Ascii("<");
        }
        b.Ascii(adjust ? ">] TJ\n" : "> Tj\n");
    }

    /// <summary>
    /// Shows glyphs that complex shaping positioned, in drawing order: TJ moves each glyph by its offset and corrects
    /// its advance to the shaped one, and a vertical offset (a mark above or below its base) raises it with Ts.
    /// </summary>
    private void WritePositionedGlyphs(ByteBuffer b, EmbeddedFont font, GlyphSubset subset, string text, float size, float wordSpacing,
        bool skipNotdef, float notdefAdjust, float spaceAdjust)
    {
        b.Ascii("[<");
        for (int k = 0; k < _glyphs.Count; k++)
        {
            var g = _glyphs[k];
            int cid = subset.Cid(g, text);
            float width = font.Program.Advance1000(g.Gid);
            if (cid == 0 && skipNotdef)
            {
                b.Ascii(">").Num(notdefAdjust - g.Advance + width).Ascii("<");
                continue;
            }
            if (g.Dy != 0)
            {
                // The raised glyph is shown on its own, between two TJ arrays.
                b.Ascii(">] TJ ").Num(g.Dy * size / 1000f).Ascii(" Ts [<");
            }
            if (g.Dx != 0) b.Ascii(">").Num(-g.Dx).Ascii("<");
            Hex4(b, cid);
            float after = g.Dx + width - g.Advance;
            if (wordSpacing != 0 && g.Length > 0 && text[g.Start] == ' ' && k + 1 < _glyphs.Count) after += spaceAdjust;
            if (after != 0) b.Ascii(">").Num(after).Ascii("<");
            if (g.Dy != 0) b.Ascii(">] TJ 0 Ts [<");
        }
        b.Ascii(">] TJ\n");
    }

    /// <summary>Horizontal advance of a run, including letter and word spacing.</summary>
    private static float RunAdvance(TextFont font, TextOp t, int start, int length)
    {
        float w = font.MeasureUnits(t.Text, start, length) * t.Size / 1000f;
        string text = t.Text;
        for (int i = start, end = start + length; i < end; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) i++;
            w += t.CharSpacing;
            if (text[i] == ' ') w += t.WordSpacing;
        }
        return w;
    }

    private static readonly byte[] HexDigits = "0123456789ABCDEF"u8.ToArray();

    private static void Hex4(ByteBuffer b, int v)
        => b.Byte(HexDigits[(v >> 12) & 15]).Byte(HexDigits[(v >> 8) & 15]).Byte(HexDigits[(v >> 4) & 15]).Byte(HexDigits[v & 15]);

    /// <summary>Writes the five objects of an embedded font: Type0 font, CIDFont, descriptor, font file and ToUnicode CMap.</summary>
    private void WriteEmbeddedFont(GlyphSubset subset, int id)
    {
        var font = subset.Program;
        // Subset fonts are marked with a tag in their name.
        string baseFont = subset.Tag() + "+" + font.PostScriptName;
        var used = subset.Used().ToList();

        Begin(id).Ascii("<< /Type /Font /Subtype /Type0 /BaseFont /").Ascii(baseFont).Ascii(" /Encoding /Identity-H /DescendantFonts [")
            .Int(id + 1).Ascii(" 0 R] /ToUnicode ").Int(id + 4).Ascii(" 0 R >>");
        End();

        Begin(id + 1).Ascii("<< /Type /Font /Subtype /").Ascii(font.IsCff ? "CIDFontType0" : "CIDFontType2").Ascii(" /BaseFont /").Ascii(baseFont)
            .Ascii(" /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor ").Int(id + 2)
            .Ascii(" 0 R /DW 1000 /W [");
        for (int i = 0; i < used.Count;)
        {
            int j = i;
            while (j + 1 < used.Count && used[j + 1].Cid == used[j].Cid + 1) j++;
            _out.Int(used[i].Cid).Ascii(" [");
            for (int k = i; k <= j; k++) _out.Int(font.Advance1000(used[k].Gid)).Sp();
            _out.Ascii("] ");
            i = j + 1;
        }
        _out.Ascii("]");
        if (!font.IsCff) _out.Ascii(" /CIDToGIDMap /Identity");
        _out.Ascii(" >>");
        End();

        float scale = 1000f / font.UnitsPerEm;
        int flags = 4 | (font.FixedPitch ? 1 : 0) | (font.IsItalic || font.ItalicAngle != 0 ? 64 : 0);
        int stemV = 50 + (int)(font.WeightClass / 65f * (font.WeightClass / 65f));
        Begin(id + 2).Ascii("<< /Type /FontDescriptor /FontName /").Ascii(baseFont).Ascii(" /Flags ").Int(flags)
            .Ascii(" /FontBBox [").Int((int)(font.XMin * scale)).Sp().Int((int)(font.YMin * scale)).Sp()
            .Int((int)Math.Ceiling(font.XMax * scale)).Sp().Int((int)Math.Ceiling(font.YMax * scale))
            .Ascii("] /ItalicAngle ").Num(font.ItalicAngle).Ascii(" /Ascent ").Int((int)Math.Round(font.Ascender * scale))
            .Ascii(" /Descent ").Int((int)Math.Round(-Math.Abs(font.Descender) * scale)).Ascii(" /CapHeight ").Int((int)Math.Round(font.CapHeight * scale))
            .Ascii(" /StemV ").Int(stemV).Ascii(font.IsCff ? " /FontFile3 " : " /FontFile2 ").Int(id + 3).Ascii(" 0 R >>");
        End();

        var file = FontSubsetter.Build(subset);
        WriteStream(id + 3, file, compress: true, extraDict: font.IsCff ? "/Subtype /OpenType" : "/Length1 " + file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var cmap = new ByteBuffer(256 + used.Count * 16);
        cmap.Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Ascii("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var mapped = used.Where(u => u.CodePoint > 0 || u.Text != null).ToList();
        for (int i = 0; i < mapped.Count; i += 100)
        {
            int n = Math.Min(100, mapped.Count - i);
            cmap.Int(n).Ascii(" beginbfchar\n");
            for (int k = i; k < i + n; k++)
            {
                cmap.Byte((byte)'<');
                Hex4(cmap, mapped[k].Cid);
                cmap.Ascii("> <");
                int cp = mapped[k].CodePoint;
                if (mapped[k].Text is { } chars)
                    foreach (char c in chars) Hex4(cmap, c);
                else if (cp >= 0x10000)
                {
                    cp -= 0x10000;
                    Hex4(cmap, 0xD800 + (cp >> 10));
                    Hex4(cmap, 0xDC00 + (cp & 0x3FF));
                }
                else Hex4(cmap, cp);
                cmap.Ascii(">\n");
            }
            cmap.Ascii("endbfchar\n");
        }
        cmap.Ascii("endcmap\nCMapName currentdict /CMapResource defineresource pop\nend\nend\n");
        WriteStream(id + 4, cmap.Span, _compress, extraDict: null);
    }

    private const float MarginTextSize = 9f;

    private void WriteMarginText(ByteBuffer b, TextFont font, string template, int page, int pages, bool top)
    {
        string text = template.Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture))
                              .Replace("{pages}", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        const float size = MarginTextSize;
        float w = font.Measure(text, size);
        float x = (_page.Width - w) / 2;
        float marginH = top ? _page.MarginTop : _page.MarginBottom;
        float y = top ? _page.Height - marginH / 2 - size / 3 : marginH / 2 - size / 3;
        b.Ascii("BT\n");
        if (_tw != 0) { _tw = 0; b.Ascii("0 Tw\n"); }
        if (_tc != 0) { _tc = 0; b.Ascii("0 Tc\n"); }
        SetFill(b, 0xFF666666);
        WriteTextLine(b, new TextOp(0, 0, 0, font, size, 0xFF666666, text, 0, 0), x, y);
        b.Ascii("ET\n");
    }

    private static void WriteTextBytes(ByteBuffer b, string text)
    {
        b.Byte((byte)'(');
        foreach (char ch in text)
        {
            byte c = WinAnsi.Encode(ch);
            if (c == (byte)'(' || c == (byte)')' || c == (byte)'\\') b.Byte((byte)'\\');
            b.Byte(c);
        }
        b.Byte((byte)')');
    }

    /// <summary>Writes a string object: literal, or encrypted and hex-encoded when the file is encrypted.</summary>
    private void WriteAsciiString(ByteBuffer b, string s) => WriteStringBytes(b, AsciiStringBytes(s));

    /// <summary>An ASCII string for a URI: other characters are percent-encoded as the UTF-8 bytes of their code point.</summary>
    private static byte[] AsciiStringBytes(string s)
    {
        var raw = new ByteBuffer(s.Length + 8);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in s.EnumerateRunes())
        {
            if (rune.Value > 126 || rune.Value < 32)
            {
                int n = rune.EncodeToUtf8(utf8);
                for (int k = 0; k < n; k++) raw.Byte((byte)'%').Ascii(utf8[k].ToString("X2"));
                continue;
            }
            raw.Byte((byte)rune.Value);
        }
        return raw.Span.ToArray();
    }

    private void WriteStringBytes(ByteBuffer b, byte[] raw)
    {
        if (_crypt != null)
        {
            PdfEncryptor.Hex(b, _crypt.Encrypt(raw));
            return;
        }
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            PdfEncryptor.Hex(b, raw);
            return;
        }
        b.Byte((byte)'(');
        foreach (var c in raw)
        {
            // Line ends are written as escapes: a reader would turn a bare CR or CR LF in a literal string into LF.
            switch (c)
            {
                case (byte)'\n': b.Byte((byte)'\\').Byte((byte)'n'); continue;
                case (byte)'\r': b.Byte((byte)'\\').Byte((byte)'r'); continue;
                case (byte)'\t': b.Byte((byte)'\\').Byte((byte)'t'); continue;
                case (byte)'\b': b.Byte((byte)'\\').Byte((byte)'b'); continue;
                case (byte)'\f': b.Byte((byte)'\\').Byte((byte)'f'); continue;
            }
            if (c == (byte)'(' || c == (byte)')' || c == (byte)'\\') b.Byte((byte)'\\');
            b.Byte(c);
        }
        b.Byte((byte)')');
    }

    /// <summary>Writes a PDF text string: PDFDocEncoding-compatible ASCII when possible, UTF-16BE otherwise.</summary>
    private void WriteTextString(ByteBuffer b, string s)
    {
        bool ascii = true;
        foreach (char ch in s) if (ch > 126) { ascii = false; break; }
        if (ascii)
        {
            // Text, not a URI: control characters such as a tab or a line break are kept, not percent-encoded.
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            WriteStringBytes(b, bytes);
            return;
        }
        var raw = new byte[2 + s.Length * 2];
        raw[0] = 0xFE;
        raw[1] = 0xFF;
        for (int i = 0; i < s.Length; i++)
        {
            raw[2 + i * 2] = (byte)(s[i] >> 8);
            raw[3 + i * 2] = (byte)s[i];
        }
        WriteStringBytes(b, raw);
    }
}

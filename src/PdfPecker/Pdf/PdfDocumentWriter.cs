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
    /// <summary>
    /// Whether the page is written at the paper size Chrome gives a scaled page (see <see cref="PdfDocumentWriter.PaperSize"/>): set for
    /// PdfOptions.Scale, but not for a page only shrunk to fit its content, whose paper keeps the size asked for.
    /// </summary>
    public bool ChromePaper;
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
    private readonly ByteBuffer _out = ByteBuffer.Rent(64 * 1024);
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

    /// <summary>
    /// The fixed date for the document's dates, which also makes the file identifier a hash of the content; null to use
    /// the current time and a random identifier.
    /// </summary>
    public DateTime? FixedDate { get; init; }

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
        public readonly bool Ink;     // only ink overflow, such as a box shadow, which never keeps a page of its own
        public Placed(DrawOp op, float y0, float y1, bool ink = false) { Op = op; Y0 = y0; Y1 = y1; Ink = ink; }

        /// <summary>The same placement, marked as ink overflow.</summary>
        public Placed AsInk() => new(Op, Y0, Y1, ink: true);

        /// <summary>True when the items hold something and all of it is ink overflow.</summary>
        public static bool AllInk(List<Placed> items)
        {
            foreach (var item in items) if (!item.Ink) return false;
            return items.Count > 0;
        }
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
            if (p.Op is LayerBlock tb)
            {
                ForEachLeaf(tb.Items, visit);
                if (tb.Layer.Mask is { } mask)
                    foreach (var layer in mask)
                        foreach (var op in layer.Ops) visit(new Placed(op, 0, 0));
            }
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
        var doc = new WriteState(sections, pageBackground, title, author, subject, footerTemplate, headerTemplate, outline);
        _page = doc.FirstSetup;
        DistributeOverPages(doc);
        LayOutPageBands(doc, pageBands);

        CollectResources(doc);
        PrepareWatermark(doc);
        if (IsPdfA) ConvertCmykImages(doc);
        if (Security != null) _crypt = new PdfEncryptor(Security);
        _imageList = doc.Images;

        // Every object gets its number before the first is written, except those that only writing the pages
        // reveals (annotations, patterns, transparency groups, masks), which are numbered after them.
        NumberObjects(doc);

        // The objects are written in the order of the file, which with their numbers makes its bytes: keep it.
        WriteHeader();
        WriteCatalog(doc);
        WritePageTree(doc);
        doc.Now = FixedDate ?? ReproducibleDate.TruncateToSeconds(DateTime.UtcNow);
        WriteInfoAndMetadata(doc);
        if (IsPdfA) WriteOutputIntent(doc);
        for (int i = 0; i < doc.FileSpecIds.Length; i++) WriteAttachment(Attachments[i], doc.FileSpecIds[i], doc.Now);
        WriteFonts(doc);
        WriteImages(doc);
        if (doc.Watermark != null) WriteWatermark(doc);

        WritePages(doc);
        // The structure elements get their object numbers now: PDF 2.0 links and bookmarks refer to them.
        if (Tagged) doc.StructElements = NumberStructure(ref doc.Next);
        WriteLinkAnnotations(doc);
        WriteFields(doc.PageFields, doc.PageIds, doc.ImageIds, doc.AnnotKeys, ref doc.Next);
        SetPage(0, out _);
        if (doc.OutlinesId != 0) WriteOutline(doc);
        if (_crypt != null) WriteEncryption(doc);

        WriteConicFunctions();
        WriteTilingPatterns(doc);
        WriteTransparencyGroups(doc);
        if (Tagged)
        {
            _mc = null;
            doc.Next = WriteStructure(doc.Next, doc.StructElements!, doc.StructRootId, doc.ParentKey, doc.PageIds, doc.PageStreams, doc.AnnotKeys);
        }
        WriteSoftMasks(doc);
        WriteFilterImages(doc);
        WriteSharedResources(doc);
        WriteTrailer(doc);

        _crypt?.Dispose();
        var pdf = _out.Span.ToArray();
        _out.Return();
        return pdf;
    }

    /// <summary>A PDF/A file of any part and level.</summary>
    private bool IsPdfA => Conformance != PdfConformance.None;

    /// <summary>A tagged PDF, with a logical structure.</summary>
    private bool Tagged => Structure != null;

    /// <summary>
    /// What the steps of writing one document share: its input, the ops on each page, the resources they use and the
    /// object numbers handed out.
    /// </summary>
    private sealed class WriteState
    {
        public WriteState(IReadOnlyList<PageSection> sections, uint pageBackground, string? title, string? author, string? subject,
            string? footerTemplate, string? headerTemplate, IReadOnlyList<OutlineHeading>? outline)
        {
            Sections = sections;
            FirstSetup = sections[0].Grid.SetupOf(0);
            PageBackground = pageBackground;
            Title = title;
            Author = author;
            Subject = subject;
            FooterTemplate = footerTemplate;
            HeaderTemplate = headerTemplate;
            Outline = outline;
        }

        public readonly IReadOnlyList<PageSection> Sections;
        /// <summary>The setup of the document's first page, which the watermark is made for.</summary>
        public readonly PageSetup FirstSetup;
        public readonly uint PageBackground;
        public readonly string? Title, Author, Subject, FooterTemplate, HeaderTemplate;
        public readonly IReadOnlyList<OutlineHeading>? Outline;
        public bool HasMargins => FooterTemplate != null || HeaderTemplate != null;

        // The ops of each page: its flowing content, its running headers, footers and fixed boxes, and its canvas.
        public List<List<Placed>> Pages = null!;
        public List<Placed>[] Bands = null!, Canvas = null!;
        public int PageCount;
        /// <summary>The anchors of all sections, at their y in the document's one flow (see <see cref="_sectionBase"/>).</summary>
        public IReadOnlyDictionary<string, float> Anchors = null!;

        // The resources the pages use: standard fonts by StandardFont.All index, and images by /Im number - 1.
        public readonly bool[] UsedFonts = new bool[StandardFont.All.Length];
        public readonly List<PdfImage> Images = new();
        public readonly Dictionary<PdfImage, int> ImageIds = new();
        public TextFont MarginFont = null!;

        // The watermark, null when it has nothing to draw, as laid-out text lines, a raster image or an SVG drawing.
        public PdfWatermark? Watermark;
        public (List<TextOp> Lines, float Cos, float Sin)? WatermarkText;
        public PdfImage? WatermarkImage;
        public SvgImage? WatermarkSvg;

        // Object numbers: the next free one, then those handed out (0 for an object the file does not have).
        public int Next = 1;
        public int CatalogId, PagesId, ResourcesId, InfoId, FirstEmbeddedId, OutlinesId, WatermarkId, WatermarkSvgId;
        public int MetadataId, OutputIntentId, IccId, EncryptId, StructRootId;
        public int[] FontIds = null!, PageIds = null!, ContentIds = null!, FileSpecIds = null!, FormIds = null!, MaskIds = null!;
        public List<OutlineItem> OutlineItems = null!;
        public List<FieldBlock>[] PageFields = null!;

        // What writing the pages leaves for the objects after them: the link annotations, each page's marked content,
        // the next free key of the parent tree and the key of each annotation in it.
        public readonly List<(int Id, Placed Link, float Origin, float PageTop, int Page)> Annotations = new();
        public McStream[] PageStreams = null!;
        public int ParentKey;
        public readonly Dictionary<int, int> AnnotKeys = new();
        public List<StructElem>? StructElements;

        /// <summary>The time of writing, to the second, for the info dictionary, the XMP metadata and attachments.</summary>
        public DateTime Now;
    }

    /// <summary>
    /// Hands out the object numbers known before the pages are written, in the order the file has always used: the
    /// numbers are part of the output, so the order must not change.
    /// </summary>
    private void NumberObjects(WriteState doc)
    {
        // PDF 2.0 deprecates the document information dictionary (PDF/A-4 allows only its ModDate): the XMP metadata
        // carries the properties instead.
        doc.CatalogId = doc.Next++;
        doc.PagesId = doc.Next++;
        doc.ResourcesId = doc.Next++;
        doc.InfoId = Pdf20 ? 0 : doc.Next++;
        _resourcesId = doc.ResourcesId;
        var usedFonts = doc.UsedFonts;
        doc.FontIds = new int[usedFonts.Length];
        for (int i = 0; i < usedFonts.Length; i++) if (usedFonts[i]) doc.FontIds[i] = doc.Next++;
        doc.FirstEmbeddedId = doc.Next;
        doc.Next += _subsetList.Count * 5; // Type0 font, CIDFont, descriptor, font file, ToUnicode CMap
        foreach (var img in doc.Images)
        {
            img.ObjectNumber = doc.Next++;
            if (img.SMask != null) img.SMask.ObjectNumber = doc.Next++;
        }
        doc.PageIds = new int[doc.PageCount];
        doc.ContentIds = new int[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++) { doc.PageIds[i] = doc.Next++; doc.ContentIds[i] = doc.Next++; }
        _pageIds = doc.PageIds;
        doc.OutlineItems = BuildOutline(doc.Outline, doc.Anchors);
        if (doc.OutlineItems.Count > 0)
        {
            doc.OutlinesId = doc.Next++;
            foreach (var item in doc.OutlineItems) item.Id = doc.Next++;
        }
        if (doc.Watermark != null) doc.WatermarkId = doc.Next++;
        if (doc.WatermarkSvg != null) doc.WatermarkSvgId = doc.Next++;
        if (IsPdfA) { doc.MetadataId = doc.Next++; doc.OutputIntentId = doc.Next++; doc.IccId = doc.Next++; }
        doc.FileSpecIds = new int[Attachments.Count];
        for (int i = 0; i < doc.FileSpecIds.Length; i++) { doc.FileSpecIds[i] = doc.Next; doc.Next += 2; } // file specification, embedded file
        if (_crypt != null) doc.EncryptId = doc.Next++;
        if (Tagged) doc.StructRootId = doc.Next++;
        if (PdfUA && doc.MetadataId == 0) doc.MetadataId = doc.Next++;
        doc.PageFields = AllocateFields(doc.Pages, doc.PageCount, ref doc.Next);
        foreach (var conic in _conics)
        {
            conic.FunctionId = doc.Next++;
            if (conic.Alphas != null) conic.AlphaFunctionId = doc.Next++;
        }
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
        if (_page.ChromePaper) { paperW = PaperSize(paperW); paperH = PaperSize(paperH); }
        _paperDy = paperH - _page.Height * _page.Scale;
        return paperW;
    }

    private static float PaperDy(PageSetup page)
        => !page.ChromePaper ? 0 : PaperSize(page.Height * page.Scale) - page.Height * page.Scale;

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

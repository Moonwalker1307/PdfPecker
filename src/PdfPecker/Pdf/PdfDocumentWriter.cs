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

/// <summary>Serializes laid-out draw operations into a PDF 1.4 file (PDF 1.7 for PDF/A and AES-256 encryption).</summary>
internal sealed class PdfDocumentWriter
{
    private readonly PageSetup _page;
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
    private readonly List<FontRun> _runs = new();
    private readonly Dictionary<FontProgram, GlyphSubset> _subsets = new();
    private readonly List<GlyphSubset> _subsetList = new();
    private float _yOrigin; // distance from the page top to flow y = 0 on the page being written
    private readonly ByteBuffer _patterns = new(256); // "/P1 << ... >>" entries for the shared resources
    private int _patternCount;
    private readonly List<(int Name, byte[] Content, string Dict)> _tilings = new(); // tiling patterns, written as /T1, /T2, ...
    private List<PdfImage> _imageList = new(); // the images written, by image number - 1

    /// <summary>Stamp on every page.</summary>
    public PdfWatermark? Watermark { get; init; }

    /// <summary>Encrypt the file with these passwords.</summary>
    public PdfSecurity? Security { get; init; }

    /// <summary>Write a PDF/A file.</summary>
    public PdfConformance Conformance { get; init; }

    /// <summary>Font for the watermark text. Default: Helvetica (bold when the watermark asks for it).</summary>
    public TextFont? WatermarkFont { get; init; }

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

    private bool EmbedStandard => EmbedStandardFonts || Conformance != PdfConformance.None;

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

    /// <summary>The part of a transformed layer that falls on one page, drawn with the layer's transform.</summary>
    private sealed class TransformBlock : DrawOp
    {
        public readonly LayerOp Layer;
        public readonly List<Placed> Items;
        public TransformBlock(LayerOp layer, List<Placed> items) { Layer = layer; Items = items; }
        public override void Offset(float dx, float dy) => throw new NotSupportedException();
    }

    /// <summary>The ops to draw, looking inside transformed layers.</summary>
    private static IEnumerable<Placed> Leaves(List<Placed> list)
    {
        foreach (var p in list)
        {
            if (p.Op is TransformBlock tb) { foreach (var inner in Leaves(tb.Items)) yield return inner; }
            else yield return p;
        }
    }

    public byte[] Write(List<DrawOp> ops, float contentHeight, IReadOnlyDictionary<string, float> anchors, uint pageBackground,
        string? title, string? author, string? subject, string? footerTemplate, string? headerTemplate,
        Func<int, int, List<DrawOp>>? pageBands = null, IReadOnlyList<OutlineHeading>? outline = null, List<DrawOp>? fixedOps = null)
    {
        float H = _page.FlowHeight;
        int maxPages = Math.Max(1, MaxPages);
        int PageOf(float y)
        {
            // Compare as doubles: content far down the flow (a huge height or margin) must hit the page limit, not
            // overflow the page number.
            double p = Math.Floor((y + 0.01) / H);
            if (p >= maxPages || double.IsNaN(p)) throw TooManyPages(maxPages);
            return Math.Max(0, (int)p);
        }

        // 1. Distribute ops over pages.
        var pages = new List<List<Placed>>();
        List<Placed> PageList(int p)
        {
            while (pages!.Count <= p) pages.Add(new List<Placed>());
            return pages[p];
        }
        // The list of a page: the document's own, or, for a transformed layer, the layer's part on that page.
        List<Placed> Target(SortedDictionary<int, List<Placed>>? into, int p)
        {
            if (into == null) return PageList(p);
            if (!into.TryGetValue(p, out var list)) into[p] = list = new List<Placed>();
            return list;
        }
        void AddSplit(DrawOp op, float top, float bottom, SortedDictionary<int, List<Placed>>? into)
        {
            if (bottom < top) (top, bottom) = (bottom, top);
            int p0 = PageOf(top), p1 = Math.Max(p0, PageOf(bottom - 0.5f));
            for (int p = p0; p <= p1; p++)
            {
                float y0 = Math.Max(top, p * H), y1 = Math.Min(bottom, (p + 1) * H);
                if (y1 > y0 || top == bottom) Target(into, p).Add(new Placed(op, y0, y1));
            }
        }
        void Distribute(DrawOp op, SortedDictionary<int, List<Placed>>? into)
        {
            switch (op)
            {
                case LayerOp { Transform: not null } layer:
                {
                    // Each page gets the part of the layer that falls on it, drawn with the whole layer's transform.
                    var parts = new SortedDictionary<int, List<Placed>>();
                    foreach (var o in layer.Ops) Distribute(o, parts);
                    foreach (var (p, items) in parts) Target(into, p).Add(new Placed(new TransformBlock(layer, items), 0, 0));
                    break;
                }
                case GroupOp g:
                    foreach (var o in g.Ops) Distribute(o, into);
                    break;
                case RectOp r:
                    if (r.W > 0 && r.H > 0) AddSplit(r, r.Y, r.Y + r.H, into);
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
        foreach (var op in ops) Distribute(op, null);
        int pageCount = Math.Max(pages.Count, Math.Max(1, PageOf(Math.Max(0, contentHeight - 0.5f)) + 1));
        while (pages.Count < pageCount) pages.Add(new List<Placed>());

        // Running headers and footers, laid out per page now that the page count is known. Their ops use y from the
        // top of the content area.
        var bands = new List<Placed>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            var list = bands[p] = new List<Placed>();
            if (pageBands == null && fixedOps == null) continue;
            void Flatten(DrawOp op, List<Placed> list)
            {
                switch (op)
                {
                    case LayerOp { Transform: not null } layer:
                    {
                        var items = new List<Placed>();
                        foreach (var o in layer.Ops) Flatten(o, items);
                        list.Add(new Placed(new TransformBlock(layer, items), 0, 0));
                        break;
                    }
                    case GroupOp g: foreach (var o in g.Ops) Flatten(o, list); break;
                    case RectOp r: if (r.W > 0 && r.H > 0) list.Add(new Placed(r, r.Y, r.Y + r.H)); break;
                    case BorderRectOp br: list.Add(new Placed(br, br.Y, br.Y + br.H)); break;
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
            if (pageBands != null) foreach (var op in pageBands(p, pageCount)) Flatten(op, list);
            // position: fixed boxes repeat on every page, as in Chrome's print output.
            if (fixedOps != null) foreach (var op in fixedOps) Flatten(op, list);
        }

        // 2. Collect resources.
        var usedFonts = new bool[StandardFont.All.Length];
        var images = new List<PdfImage>();
        var imageIds = new Dictionary<PdfImage, int>();
        foreach (var page in pages.Concat(bands))
            foreach (var p in Leaves(page))
            {
                if (p.Op is TextOp t) CollectFonts(t, usedFonts);
                else if (p.Op is SvgOp sv)
                {
                    foreach (var shape in sv.Image.Shapes)
                    {
                        if (shape.Font == null) continue;
                        if (Leaf(shape.Font) is EmbeddedFont ef) AddGlyphs(Subset(ef.Program), shape.Text!, 0, shape.Text!.Length);
                        else usedFonts[shape.Font.Index] = true;
                    }
                }
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
        if (wm != null)
        {
            if (wm.Image != null)
            {
                wmImage = PdfImage.FromBytes(wm.Image) ?? throw new ArgumentException("The watermark image is not a PNG or JPEG image this library can read.");
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
            if (wmText == null && wmImage == null) wm = null;
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
        int catalogId = next++, pagesId = next++, resourcesId = next++, infoId = next++;
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
        int metadataId = 0, outputIntentId = 0, iccId = 0, encryptId = 0;
        if (pdfa) { metadataId = next++; outputIntentId = next++; iccId = next++; }
        if (_crypt != null) encryptId = next++;

        // 4. Header.
        _out.Ascii(pdfa || _crypt != null ? "%PDF-1.7\n%" : "%PDF-1.4\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        // Catalog, pages tree.
        Begin(catalogId).Ascii("<< /Type /Catalog /Pages ").Int(pagesId).Ascii(" 0 R");
        if (outlinesId != 0) _out.Ascii(" /Outlines ").Int(outlinesId).Ascii(" 0 R");
        if (pdfa) _out.Ascii(" /Metadata ").Int(metadataId).Ascii(" 0 R /OutputIntents [").Int(outputIntentId).Ascii(" 0 R]");
        // AES-256 (revision 6) is the Adobe extension level 8 to PDF 1.7, standardised in PDF 2.0.
        if (_crypt != null) _out.Ascii(" /Extensions << /ADBE << /BaseVersion /1.7 /ExtensionLevel 8 >> >>");
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
        if (pdfa) props = props.Cleaned(); // Info and XMP must agree, and XML cannot carry control characters.
        Begin(infoId);
        props.WriteInfo(_out, now, modDate: pdfa, WriteTextString);
        End();

        if (pdfa)
        {
            int part = Conformance == PdfConformance.PdfA3b ? 3 : 2;
            var xmp = PdfA.Xmp(part, "B", props, now);
            WriteStream(metadataId, xmp, compress: false, "/Type /Metadata /Subtype /XML");
            Begin(outputIntentId).Ascii("<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier ");
            WriteTextString(_out, PdfA.OutputCondition);
            _out.Ascii(" /Info ");
            WriteTextString(_out, PdfA.OutputCondition);
            _out.Ascii(" /DestOutputProfile ").Int(iccId).Ascii(" 0 R >>");
            End();
            WriteStream(iccId, PdfA.SrgbProfile, _compress, "/N 3");
        }

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
            WriteWatermark(watermarkId, wm, wmText, fontDict, wmImage);
        }

        // Pages, contents, annotations. A scaled page is written at the size Chrome gives the paper (see PaperSize).
        float paperW = _page.Width * _page.Scale, paperH = _page.Height * _page.Scale;
        if (_page.Scale != 1) { paperW = PaperSize(paperW); paperH = PaperSize(paperH); }
        float paperDy = _paperDy = paperH - _page.Height * _page.Scale;
        var content = new ByteBuffer(32 * 1024);
        var annotations = new List<(int Id, Placed Link, float Origin, float PageTop)>();
        for (int p = 0; p < pageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            content.Clear();
            ResetState();
            if (_page.Scale != 1)
            {
                // Num keeps 3 decimals, too few for a scale such as 0.99916. The content keeps to the top of the page.
                string k = _page.Scale.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                content.Ascii("q ").Ascii(k).Ascii(" 0 0 ").Ascii(k).Ascii(" 0 ").Num(paperDy).Ascii(" cm\n");
            }
            if (CssColor.IsVisible(pageBackground))
            {
                SetFill(content, pageBackground);
                content.Ascii("0 0 ").Num(_page.Width).Sp().Num(_page.Height).Ascii(" re f\n");
            }
            if (wm is { Behind: true }) content.Ascii("q /Wm Do Q\n");
            var pageLinks = new List<(Placed Link, float Origin, float PageTop)>();
            _yOrigin = _page.MarginTop + _page.BandTop;
            foreach (var placed in pages[p])
            {
                if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, p * H)); continue; }
                WriteOp(content, placed, p * H, imageIds);
            }
            _yOrigin = _page.MarginTop;
            foreach (var placed in bands[p])
            {
                if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, 0)); continue; }
                WriteOp(content, placed, 0, imageIds);
            }
            if (hasMargins)
            {
                EndText(content);
                if (headerTemplate != null) WriteMarginText(content, marginFont, headerTemplate, p + 1, pageCount, top: true);
                if (footerTemplate != null) WriteMarginText(content, marginFont, footerTemplate, p + 1, pageCount, top: false);
            }
            EndText(content);
            if (wm is { Behind: false }) content.Ascii("q /Wm Do Q\n");
            if (_page.Scale != 1) content.Ascii("Q\n");

            var annotIds = new List<int>();
            foreach (var (lk, origin, pageTop) in pageLinks)
            {
                int id = next++;
                annotIds.Add(id);
                annotations.Add((id, lk, origin, pageTop));
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
            _out.Ascii(" >>");
            End();

            WriteStream(contentIds[p], content.Span, _compress, extraDict: null);
        }

        foreach (var (id, placed, origin, linkPageTop) in annotations)
        {
            var link = (LinkOp)placed.Op;
            float x1 = _page.MarginLeft + link.X, x2 = x1 + link.W;
            float yTop = _page.Height - origin - (placed.Y0 - linkPageTop);
            float yBottom = yTop - (placed.Y1 - placed.Y0);
            float k = _page.Scale;
            x1 *= k; x2 *= k; yTop = yTop * k + paperDy; yBottom = yBottom * k + paperDy;
            Begin(id).Ascii("<< /Type /Annot /Subtype /Link /Rect [").Num(x1).Sp().Num(yBottom).Sp().Num(x2).Sp().Num(yTop)
                .Ascii("] /F 4 /Border [0 0 0] ");
            if (link.Target.StartsWith("#", StringComparison.Ordinal))
            {
                if (anchors.TryGetValue(Uri.UnescapeDataString(link.Target.Substring(1)), out float ay)
                    || anchors.TryGetValue(link.Target.Substring(1), out ay))
                {
                    _out.Ascii("/Dest ");
                    WriteDest(ay);
                }
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
                _out.Ascii(" /Dest ");
                WriteDest(item.Y);
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

        // Tiling patterns of repeated backgrounds, collected while writing the pages.
        foreach (var (n, tileContent, tileDict) in _tilings)
        {
            int id = next++;
            WriteStream(id, tileContent, _compress, tileDict);
            _patterns.Ascii(" /T").Int(n).Sp().Int(id).Ascii(" 0 R");
        }

        // Shared resources, written last: shading patterns are collected while writing the pages.
        Begin(resourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        for (int i = 0; i < usedFonts.Length; i++)
            if (usedFonts[i]) _out.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
        for (int i = 0; i < _subsetList.Count; i++) _out.Ascii(" /E").Int(i + 1).Sp().Int(firstEmbeddedId + i * 5).Ascii(" 0 R");
        _out.Ascii(" >>");
        if (images.Count > 0 || wm != null)
        {
            _out.Ascii(" /XObject <<");
            for (int i = 0; i < images.Count; i++) _out.Ascii(" /Im").Int(i + 1).Sp().Int(images[i].ObjectNumber).Ascii(" 0 R");
            if (wm != null) _out.Ascii(" /Wm ").Int(watermarkId).Ascii(" 0 R");
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
        _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(catalogId).Ascii(" 0 R /Info ").Int(infoId).Ascii(" 0 R");
        if (encryptId != 0) _out.Ascii(" /Encrypt ").Int(encryptId).Ascii(" 0 R");
        var fileId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        _out.Ascii(" /ID [");
        PdfEncryptor.Hex(_out, fileId);
        PdfEncryptor.Hex(_out, fileId);
        _out.Ascii("] >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");

        _crypt?.Dispose();
        return _out.Span.ToArray();
    }

    // ------------------------------------------------------------------ outline

    private sealed class OutlineItem
    {
        public string Title = "";
        public int Level, Id, Descendants;
        public float Y;
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
            var item = new OutlineItem { Title = h.Title, Level = h.Level, Y = y };
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

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

    private int[] _pageIds = Array.Empty<int>();

    /// <summary>Writes an explicit destination for flow position <paramref name="y"/>: its page, scrolled to that height.</summary>
    private void WriteDest(float y)
    {
        float h = _page.FlowHeight;
        int p = Math.Clamp((int)Math.Floor((y + 0.01f) / h), 0, _pageIds.Length - 1);
        float py = _page.Height - _page.MarginTop - _page.BandTop - (y - p * h);
        _out.Ascii("[").Int(_pageIds[p]).Ascii(" 0 R /XYZ 0 ").Num(Math.Min(py, _page.Height) * _page.Scale + _paperDy).Ascii(" null]");
    }

    private float _paperDy; // how far a scaled page's content moves up to stay at the top of the paper

    /// <summary>
    /// A paper dimension as Chrome prints it: rounded to whole points, then to Chrome's 1/300 inch device units, so A4
    /// is 594.96 x 841.92 pt. IronPDF, which uses Chrome, writes pages this size; pdfium fits each page to whole pixels,
    /// so the same size makes it snap thin lines the same way.
    /// </summary>
    internal static float PaperSize(float pt)
        => (float)(Math.Round(Math.Round(pt, MidpointRounding.AwayFromZero) * 300 / 72, MidpointRounding.AwayFromZero) * 0.24);

    private void WriteStream(int id, ReadOnlySpan<byte> data, bool compress, string? extraDict)
    {
        byte[]? compressed = null;
        if (compress && data.Length > 64)
        {
            using var ms = new MemoryStream(data.Length / 3 + 64);
            using (var z = new ZLibStream(ms, _level, leaveOpen: true)) z.Write(data);
            compressed = ms.ToArray();
        }
        bool filtered = compressed != null;
        if (_crypt != null) compressed = _crypt.Encrypt(compressed ?? data);
        Begin(id).Ascii("<< /Length ").Int(compressed?.Length ?? data.Length);
        if (filtered) _out.Ascii(" /Filter /FlateDecode");
        if (extraDict != null) _out.Sp().Ascii(extraDict);
        _out.Ascii(" >>\nstream\n");
        if (compressed != null) _out.Bytes(compressed);
        else _out.Bytes(data);
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
    private void WriteWatermark(int id, PdfWatermark wm, (List<TextOp> Lines, float Cos, float Sin)? text, ByteBuffer fontDict, PdfImage? image)
    {
        float W = _page.Width, H = _page.Height, cx = W / 2, cy = H / 2;
        float opacity = Math.Clamp(wm.Opacity, 0, 1);
        var b = new ByteBuffer(512);
        ResetState();
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

        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [0 0 ").Num(W).Sp().Num(H).Ascii("] /Resources << /ExtGState << /Gw << /Type /ExtGState /ca ")
            .Num(opacity).Ascii(" /CA ").Num(opacity).Ascii(" >> >>");
        if (text != null) dict.Ascii(" /Font <<").Bytes(fontDict.Span).Ascii(" >>");
        if (image != null) dict.Ascii(" /XObject << /Iw ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        dict.Ascii(" >>");
        WriteStream(id, b.Span, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

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
    }

    private float PX(float x) => _page.MarginLeft + x;

    private static float SnapPx(float pt) => MathF.Round(pt / 0.75f, MidpointRounding.AwayFromZero) * 0.75f;
    private float PY(float y, float pageTop) => _page.Height - _yOrigin - (y - pageTop);

    private void SetFill(ByteBuffer b, uint color)
    {
        if (color == _fill) return;
        _fill = color;
        WriteColor(b, color);
        b.Ascii(" rg\n");
    }

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
                if (_stroke != br.Color) { _stroke = br.Color; WriteColor(b, br.Color); b.Ascii(" RG\n"); }
                if (_lineWidth != lw) { _lineWidth = lw; b.Num(lw).Ascii(" w\n"); }
                b.Num(PX(x0 + half)).Sp().Num(PY(bottom - half, pageTop)).Sp().Num(x1 - x0 - lw).Sp().Num(bottom - top - lw).Ascii(" re S\n");
                break;
            }
            case BackgroundOp bg:
                EndText(b);
                WriteBackground(b, bg, placed, pageTop, imageIds);
                break;
            case TransformBlock tb:
            {
                EndText(b);
                // The layer's y-down transform about its origin, mapped into PDF space (y up).
                var m = tb.Layer.Transform!.Value;
                float px = PX(tb.Layer.OriginX), py = PY(tb.Layer.OriginY, pageTop);
                float e = px - m.A * px + m.C * py + m.E, f = py + m.B * px - m.D * py - m.F;
                b.Ascii("q ").Num6(m.A).Sp().Num6(-m.B).Sp().Num6(-m.C).Sp().Num6(m.D).Sp().Num(e).Sp().Num(f).Ascii(" cm\n");
                foreach (var item in tb.Items)
                    if (item.Op is not LinkOp) WriteOp(b, item, pageTop, imageIds);
                EndText(b);
                b.Ascii("Q\n");
                InvalidateState();
                break;
            }
            case LineOp l:
            {
                EndText(b);
                b.Ascii("q ");
                WriteColor(b, l.Color);
                b.Ascii(" RG ").Num(l.Width).Ascii(" w ");
                if (l.Dotted) b.Ascii("1 J [0 ").Num(l.Width * 2).Ascii("] 0 d ");
                else b.Ascii("[").Num(l.Width * 3).Sp().Num(l.Width * 3).Ascii("] 0 d ");
                float y1 = l.X1 == l.X2 ? placed.Y0 : l.Y1, y2 = l.X1 == l.X2 ? placed.Y1 : l.Y2;
                b.Num(PX(l.X1)).Sp().Num(PY(y1, pageTop)).Ascii(" m ").Num(PX(l.X2)).Sp().Num(PY(y2, pageTop)).Ascii(" l S Q\n");
                break;
            }
            case ImageOp im:
            {
                EndText(b);
                b.Ascii("q ").Num(im.W).Ascii(" 0 0 ").Num(im.H).Sp().Num(PX(im.X)).Sp().Num(PY(im.Y + im.H, pageTop))
                    .Ascii(" cm /Im").Int(imageIds[im.Image]).Ascii(" Do Q\n");
                break;
            }
            case SvgOp sv:
                EndText(b);
                WriteSvg(b, sv, pageTop);
                break;
            case CircleOp c:
            {
                EndText(b);
                float cx = PX(c.Cx), cy = PY(c.Cy, pageTop), r = c.R, k = r * 0.5523f;
                if (c.Filled) SetFill(b, c.Color);
                else
                {
                    b.Ascii("q ");
                    WriteColor(b, c.Color);
                    b.Ascii(" RG 0.75 w "); // 1px, as Chrome strokes a circle marker
                }
                b.Num(cx + r).Sp().Num(cy).Ascii(" m ");
                b.Num(cx + r).Sp().Num(cy + k).Sp().Num(cx + k).Sp().Num(cy + r).Sp().Num(cx).Sp().Num(cy + r).Ascii(" c ");
                b.Num(cx - k).Sp().Num(cy + r).Sp().Num(cx - r).Sp().Num(cy + k).Sp().Num(cx - r).Sp().Num(cy).Ascii(" c ");
                b.Num(cx - r).Sp().Num(cy - k).Sp().Num(cx - k).Sp().Num(cy - r).Sp().Num(cx).Sp().Num(cy - r).Ascii(" c ");
                b.Num(cx + k).Sp().Num(cy - r).Sp().Num(cx + r).Sp().Num(cy - k).Sp().Num(cx + r).Sp().Num(cy).Ascii(" c ");
                b.Ascii(c.Filled ? "f\n" : "S Q\n");
                break;
            }
        }
    }

    /// <summary>Draws an SVG drawing: clipped to its box, with the viewBox mapped by preserveAspectRatio.</summary>
    private void WriteSvg(ByteBuffer b, SvgOp op, float pageTop)
    {
        var img = op.Image;
        if (img.Shapes.Count == 0 || op.W <= 0 || op.H <= 0) return;
        float sx, sy, tx = 0, ty = 0, vx = 0, vy = 0;
        if (img.HasViewBox)
        {
            vx = img.ViewBoxX;
            vy = img.ViewBoxY;
            sx = op.W / img.ViewBoxWidth;
            sy = op.H / img.ViewBoxHeight;
            if (img.Align != "none")
            {
                float scale = img.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
                sx = sy = scale;
                float extraX = op.W - img.ViewBoxWidth * scale, extraY = op.H - img.ViewBoxHeight * scale;
                var a = img.Align;
                tx = a.StartsWith("xmid", StringComparison.Ordinal) ? extraX / 2 : a.StartsWith("xmax", StringComparison.Ordinal) ? extraX : 0;
                ty = a.EndsWith("ymid", StringComparison.Ordinal) ? extraY / 2 : a.EndsWith("ymax", StringComparison.Ordinal) ? extraY : 0;
            }
        }
        else sx = sy = 0.75f; // user units are CSS pixels

        float left = PX(op.X), top = PY(op.Y, pageTop);
        b.Ascii("q ").Num(left).Sp().Num(top - op.H).Sp().Num(op.W).Sp().Num(op.H).Ascii(" re W n\n");
        // User space (y down) to PDF space (y up).
        var toPage = new Matrix(sx, 0, 0, -sy, left + tx - vx * sx, top - ty + vy * sy);
        b.Num(sx).Ascii(" 0 0 ").Num(-sy).Sp().Num(toPage.E).Sp().Num(toPage.F).Ascii(" cm\n");
        foreach (var shape in img.Shapes)
        {
            switch (shape.Kind)
            {
                case SvgShape.KindClip:
                    b.Ascii("q\n");
                    WritePath(b, shape);
                    b.Ascii(shape.EvenOdd ? "W* n\n" : "W n\n");
                    continue;
                case SvgShape.KindEndClip:
                    b.Ascii("Q\n");
                    continue;
                case SvgShape.KindText:
                    WriteSvgPaint(b, shape, toPage);
                    var m = shape.TextMatrix;
                    int mode = shape.HasFill ? (shape.HasStroke ? 2 : 0) : 1;
                    var svgFont = Leaf(shape.Font!);
                    if (svgFont is EmbeddedFont svgEmbedded) b.Ascii("BT /E").Int(_subsetList.IndexOf(_subsets[svgEmbedded.Program]) + 1);
                    else b.Ascii("BT /F").Int(shape.Font!.Index + 1);
                    b.Sp().Num(shape.FontSize).Ascii(" Tf ").Int(mode).Ascii(" Tr 0 Tw ")
                        .Num(shape.CharSpacing).Ascii(" Tc ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
                        .Ascii(" Tm ");
                    if (svgFont is EmbeddedFont svgFontEmbedded) WriteGlyphHex(b, svgFontEmbedded, _subsets[svgFontEmbedded.Program], shape.Text!);
                    else WriteStandardText(b, shape.Font!, shape.Text!);
                    b.Ascii(" ET\n");
                    continue;
            }
            WritePath(b, shape);
            WriteSvgPaint(b, shape, toPage);
            if (shape.HasFill && shape.HasStroke) b.Ascii(shape.EvenOdd ? "B*\n" : "B\n");
            else if (shape.HasFill) b.Ascii(shape.EvenOdd ? "f*\n" : "f\n");
            else b.Ascii("S\n");
        }
        b.Ascii("Q\n");
        _fill = 1; // the colour set inside q/Q is gone: force the next fill colour to be written
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
            b.Ascii("/Pattern cs /P").Int(AddPattern(fg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" scn\n");
        else if (shape.Fill is { } fill)
        {
            WriteColor(b, fill);
            b.Ascii(" rg\n");
        }
        if (!shape.HasStroke) return;
        if (shape.StrokeGradient is { } sg)
            b.Ascii("/Pattern CS /P").Int(AddPattern(sg, toPage, shape.MinX, shape.MinY, shape.MaxX, shape.MaxY)).Ascii(" SCN ");
        else
        {
            WriteColor(b, shape.Stroke!.Value);
            b.Ascii(" RG ");
        }
        b.Num(shape.StrokeWidth).Ascii(" w ").Int((int)shape.LineCap).Ascii(" J ").Int((int)shape.LineJoin).Ascii(" j ")
            .Num(shape.MiterLimit).Ascii(" M\n");
    }

    /// <summary>Draws one background layer: a single clipped tile, or a tiling pattern filling the painted area.</summary>
    private void WriteBackground(ByteBuffer b, BackgroundOp bg, in Placed placed, float pageTop, Dictionary<PdfImage, int> imageIds)
    {
        var (x0, y0, x1, y1) = bg.Painted();
        y0 = Math.Max(y0, placed.Y0);
        y1 = Math.Min(y1, placed.Y1);
        if (!(x1 > x0 && y1 > y0)) return;
        float tx = PX(bg.TileX), tw = bg.TileW, th = bg.TileH;
        const float eps = 0.01f;
        bool single = (!bg.RepeatX || bg.TileX <= x0 + eps && bg.TileX + tw >= x1 - eps)
                      && (!bg.RepeatY || bg.TileY <= y0 + eps && bg.TileY + th >= y1 - eps);
        if (single && bg.Gradient is { } g)
        {
            // Gradient space is the tile; the pattern maps it to the page.
            var toPage = new Matrix(1, 0, 0, -1, tx, PY(bg.TileY, pageTop));
            int name = AddPattern(g, toPage, x0 - bg.TileX, y0 - bg.TileY, x1 - bg.TileX, y1 - bg.TileY);
            b.Ascii("q /Pattern cs /P").Int(name).Ascii(" scn ");
            b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
            return;
        }
        if (single)
        {
            b.Ascii("q ").Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re W n ")
                .Num(tw).Ascii(" 0 0 ").Num(th).Sp().Num(tx).Sp().Num(PY(bg.TileY + th, pageTop))
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
            cell.Num(tw).Ascii(" 0 0 ").Num(th).Ascii(" 0 0 cm /Im1 Do\n");
            dict.Ascii("/XObject << /Im1 ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        }
        dict.Ascii(" >>");
        int n = _tilings.Count + 1;
        _tilings.Add((n, cell.Span.ToArray(), System.Text.Encoding.ASCII.GetString(dict.Span)));
        b.Ascii("q /Pattern cs /T").Int(n).Ascii(" scn ");
        b.Num(PX(x0)).Sp().Num(PY(y1, pageTop)).Sp().Num(x1 - x0).Sp().Num(y1 - y0).Ascii(" re f Q\n");
    }

    /// <summary>Adds a shading pattern to the shared resources and returns its number.</summary>
    private int AddPattern(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY)
    {
        int n = ++_patternCount;
        _patterns.Ascii(" /P").Int(n).Sp();
        g.WritePattern(_patterns, toPage, minX, minY, maxX, maxY);
        return n;
    }

    // ------------------------------------------------------------------ text

    private void CollectFonts(TextOp t, bool[] usedFonts)
    {
        if (t.Font is StandardFont sf && !EmbedStandard)
        {
            usedFonts[sf.Index] = true;
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        foreach (var run in _runs)
        {
            if (Leaf(run.Font) is EmbeddedFont ef) AddGlyphs(Subset(ef.Program), t.Text, run.Start, run.Length);
            else usedFonts[((StandardFont)run.Font).Index] = true;
        }
    }

    /// <summary>Shows a text op starting at (x, y), one run per concrete font.</summary>
    private void WriteTextLine(ByteBuffer b, TextOp t, float x, float y)
    {
        if (t.Font is StandardFont sf && !EmbedStandard)
        {
            WriteStandardRun(b, sf, t, 0, t.Text.Length, x, y);
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        foreach (var run in _runs)
        {
            if (Leaf(run.Font) is EmbeddedFont ef) WriteEmbeddedRun(b, ef, t, run.Start, run.Length, x, y);
            else WriteStandardRun(b, (StandardFont)run.Font, t, run.Start, run.Length, x, y);
            x += RunAdvance(run.Font, t, run.Start, run.Length);
        }
    }

    private static void AddGlyphs(GlyphSubset subset, string text, int start, int length)
    {
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            subset.Cid(cp);
        }
    }

    /// <summary>Shows text as hex strings of CIDs: with TJ when the font kerns, so kerned pairs can move glyphs.</summary>
    private static void WriteGlyphHex(ByteBuffer b, EmbeddedFont font, GlyphSubset subset, string text)
    {
        bool kern = font.HasKerning;
        int prev = -1;
        b.Ascii(kern ? "[<" : "<");
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            if (kern && prev >= 0 && font.KernUnits(prev, cp) is int k && k != 0) b.Ascii(">").Int(-k).Ascii("<");
            prev = cp;
            Hex4(b, subset.Cid(cp));
        }
        b.Ascii(kern ? ">] TJ" : "> Tj");
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
        WriteStandardText(b, font, start == 0 && length == t.Text.Length ? t.Text : t.Text.Substring(start, length));
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
            if (_stroke != t.Color) { _stroke = t.Color; WriteColor(b, t.Color); b.Ascii(" RG\n"); }
            if (_lineWidth != width) { _lineWidth = width; b.Num(width).Ascii(" w\n"); }
        }
        else SetRenderMode(b, 0);
        b.Ascii(font.SyntheticItalic ? "1 0 0.2 1 " : "1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");

        // Word spacing (Tw) only applies to the single-byte code 32, so justified text moves spaces explicitly.
        // PDF/A forbids showing .notdef: characters no font has become blank space of the same width.
        string text = t.Text;
        bool skipNotdef = Conformance != PdfConformance.None && t.Size > 0;
        // Kerning moves the glyph after a kerned pair, as the layout measured it.
        bool kern = font.HasKerning && t.Size > 0;
        bool adjust = (t.WordSpacing != 0 || skipNotdef || kern) && t.Size > 0;
        float spaceAdjust = adjust ? -t.WordSpacing * 1000f / t.Size : 0;
        float notdefAdjust = skipNotdef ? -(font.Program.Advance1000(0) + t.CharSpacing * 1000f / t.Size) : 0;
        b.Ascii(adjust ? "[<" : "<");
        int prev = -1;
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            if (kern && prev >= 0 && font.KernUnits(prev, cp) is int k && k != 0) b.Ascii(">").Int(-k).Ascii("<");
            prev = cp;
            int cid = subset.Cid(cp);
            if (cid == 0 && skipNotdef) b.Ascii(">").Num(notdefAdjust).Ascii("<");
            else Hex4(b, cid);
            if (adjust && cp == ' ' && t.WordSpacing != 0 && i + 1 < end) b.Ascii(">").Num(spaceAdjust).Ascii("<");
        }
        b.Ascii(adjust ? ">] TJ\n" : "> Tj\n");
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
        var mapped = used.Where(u => u.CodePoint > 0).ToList();
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
                if (cp >= 0x10000)
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

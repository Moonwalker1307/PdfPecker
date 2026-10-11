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
    private StandardFont? _textFont;
    private float _textSize, _tw, _tc;
    private float _yOrigin; // distance from the page top to flow y = 0 on the page being written
    private readonly ByteBuffer _patterns = new(256); // "/P1 << ... >>" entries for the shared resources
    private int _patternCount;

    /// <summary>Stamp on every page.</summary>
    public PdfWatermark? Watermark { get; init; }

    /// <summary>Encrypt the file with these passwords.</summary>
    public PdfSecurity? Security { get; init; }

    /// <summary>Write a PDF/A file.</summary>
    public PdfConformance Conformance { get; init; }

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

    public byte[] Write(List<DrawOp> ops, float contentHeight, IReadOnlyDictionary<string, float> anchors, uint pageBackground,
        string? title, string? author, string? subject, string? footerTemplate, string? headerTemplate,
        Func<int, int, List<DrawOp>>? pageBands = null, IReadOnlyList<OutlineHeading>? outline = null)
    {
        float H = _page.FlowHeight;
        int PageOf(float y) => Math.Max(0, (int)Math.Floor((y + 0.01f) / H));

        // 1. Distribute ops over pages.
        var pages = new List<List<Placed>>();
        List<Placed> PageList(int p)
        {
            while (pages!.Count <= p) pages.Add(new List<Placed>());
            return pages[p];
        }
        void AddSplit(DrawOp op, float top, float bottom)
        {
            if (bottom < top) (top, bottom) = (bottom, top);
            int p0 = PageOf(top), p1 = Math.Max(p0, PageOf(bottom - 0.5f));
            for (int p = p0; p <= p1; p++)
            {
                float y0 = Math.Max(top, p * H), y1 = Math.Min(bottom, (p + 1) * H);
                if (y1 > y0 || top == bottom) PageList(p).Add(new Placed(op, y0, y1));
            }
        }
        void Distribute(DrawOp op)
        {
            switch (op)
            {
                case GroupOp g:
                    foreach (var o in g.Ops) Distribute(o);
                    break;
                case RectOp r:
                    if (r.W > 0 && r.H > 0) AddSplit(r, r.Y, r.Y + r.H);
                    break;
                case GradientOp gr:
                    if (gr.W > 0 && gr.H > 0) AddSplit(gr, gr.Y, gr.Y + gr.H);
                    break;
                case LineOp l:
                    if (l.X1 == l.X2) AddSplit(l, l.Y1, l.Y2);
                    else PageList(PageOf(l.Y1)).Add(new Placed(l, l.Y1, l.Y2));
                    break;
                case TextOp t:
                    PageList(PageOf(t.LineTop)).Add(new Placed(t, 0, 0));
                    break;
                case ImageOp im:
                    PageList(PageOf(im.Y)).Add(new Placed(im, 0, 0));
                    break;
                case SvgOp sv:
                    PageList(PageOf(sv.Y)).Add(new Placed(sv, 0, 0));
                    break;
                case LinkOp lk:
                    if (lk.W > 0 && lk.H > 0) AddSplit(lk, lk.Y, lk.Y + lk.H); // a link box split by a page break is clickable on both pages
                    break;
                case CircleOp c:
                    PageList(PageOf(c.Cy - c.R)).Add(new Placed(c, 0, 0));
                    break;
            }
        }
        foreach (var op in ops) Distribute(op);
        int pageCount = Math.Max(pages.Count, Math.Max(1, PageOf(Math.Max(0, contentHeight - 0.5f)) + 1));
        while (pages.Count < pageCount) pages.Add(new List<Placed>());

        // Running headers and footers, laid out per page now that the page count is known. Their ops use y from the
        // top of the content area.
        var bands = new List<Placed>[pageCount];
        for (int p = 0; p < pageCount; p++)
        {
            var list = bands[p] = new List<Placed>();
            if (pageBands == null) continue;
            void Flatten(DrawOp op)
            {
                switch (op)
                {
                    case GroupOp g: foreach (var o in g.Ops) Flatten(o); break;
                    case RectOp r: if (r.W > 0 && r.H > 0) list.Add(new Placed(r, r.Y, r.Y + r.H)); break;
                    case GradientOp gr: if (gr.W > 0 && gr.H > 0) list.Add(new Placed(gr, gr.Y, gr.Y + gr.H)); break;
                    case LineOp l: list.Add(new Placed(l, l.Y1, l.Y2)); break;
                    case LinkOp lk: list.Add(new Placed(lk, lk.Y, lk.Y + lk.H)); break;
                    default: list.Add(new Placed(op, 0, 0)); break;
                }
            }
            foreach (var op in pageBands(p, pageCount)) Flatten(op);
        }

        // 2. Collect resources.
        var usedFonts = new bool[StandardFont.All.Length];
        var images = new List<PdfImage>();
        var imageIds = new Dictionary<PdfImage, int>();
        foreach (var page in pages.Concat(bands))
            foreach (var p in page)
            {
                if (p.Op is TextOp t) usedFonts[t.Font.Index] = true;
                else if (p.Op is SvgOp sv)
                {
                    foreach (var shape in sv.Image.Shapes)
                        if (shape.Font != null) usedFonts[shape.Font.Index] = true;
                }
                else if (p.Op is ImageOp im && !imageIds.ContainsKey(im.Image))
                {
                    imageIds[im.Image] = images.Count + 1;
                    images.Add(im.Image);
                }
            }
        bool hasMargins = footerTemplate != null || headerTemplate != null;
        var marginFont = StandardFont.Get(FontFamily.Helvetica, false, false);
        if (hasMargins) usedFonts[marginFont.Index] = true;

        var wm = Watermark;
        StandardFont? wmFont = null;
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
                wmFont = StandardFont.Get(FontFamily.Helvetica, wm.Bold, false);
                usedFonts[wmFont.Index] = true;
            }
            if (wmFont == null && wmImage == null) wm = null;
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

        // 3. Object numbers.
        int next = 1;
        int catalogId = next++, pagesId = next++, resourcesId = next++, infoId = next++;
        var fontIds = new int[usedFonts.Length];
        for (int i = 0; i < usedFonts.Length; i++) if (usedFonts[i]) fontIds[i] = next++;
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
        if (pdfa)
        {
            // Info and XMP must agree, and XML cannot carry control characters.
            title = Clean(title);
            author = Clean(author);
            subject = Clean(subject);
        }
        string date = "D:" + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        Begin(infoId).Ascii("<< /Producer ");
        WriteTextString(_out, "PdfPecker");
        if (!string.IsNullOrEmpty(title)) { _out.Ascii(" /Title "); WriteTextString(_out, title!); }
        if (!string.IsNullOrEmpty(author)) { _out.Ascii(" /Author "); WriteTextString(_out, author!); }
        if (!string.IsNullOrEmpty(subject)) { _out.Ascii(" /Subject "); WriteTextString(_out, subject!); }
        _out.Ascii(" /CreationDate ");
        WriteTextString(_out, date);
        if (pdfa)
        {
            _out.Ascii(" /ModDate ");
            WriteTextString(_out, date);
        }
        _out.Ascii(" >>");
        End();

        if (pdfa)
        {
            int part = Conformance == PdfConformance.PdfA3b ? 3 : 2;
            var xmp = PdfA.Xmp(part, "B", title, author, subject, "PdfPecker", now);
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

        // Images.
        foreach (var img in images)
        {
            WriteImage(img);
            if (img.SMask != null) WriteImage(img.SMask);
        }

        if (wm != null) WriteWatermark(watermarkId, wm, wmFont, wmFont != null ? fontIds[wmFont.Index] : 0, wmImage);

        // Pages, contents, annotations.
        var content = new ByteBuffer(32 * 1024);
        var annotations = new List<(int Id, Placed Link, float Origin, float PageTop)>();
        for (int p = 0; p < pageCount; p++)
        {
            content.Clear();
            ResetState();
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

            var annotIds = new List<int>();
            foreach (var (lk, origin, pageTop) in pageLinks)
            {
                int id = next++;
                annotIds.Add(id);
                annotations.Add((id, lk, origin, pageTop));
            }

            Begin(pageIds[p]).Ascii("<< /Type /Page /Parent ").Int(pagesId).Ascii(" 0 R /MediaBox [0 0 ")
                .Num(_page.Width).Sp().Num(_page.Height).Ascii("] /Resources ").Int(resourcesId).Ascii(" 0 R /Contents ")
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

        // Shared resources, written last: shading patterns are collected while writing the pages.
        Begin(resourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        for (int i = 0; i < usedFonts.Length; i++)
            if (usedFonts[i]) _out.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
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
        _out.Ascii("[").Int(_pageIds[p]).Ascii(" 0 R /XYZ 0 ").Num(Math.Min(py, _page.Height)).Ascii(" null]");
    }

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

    /// <summary>The watermark as a form XObject covering the page, drawn by every page's content stream.</summary>
    private void WriteWatermark(int id, PdfWatermark wm, StandardFont? font, int fontId, PdfImage? image)
    {
        float W = _page.Width, H = _page.Height, cx = W / 2, cy = H / 2;
        float opacity = Math.Clamp(wm.Opacity, 0, 1);
        var b = new ByteBuffer(512);
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
        if (font != null)
        {
            var lines = wm.Text!.Replace("\r\n", "\n").Split('\n');
            double a = (wm.Angle ?? Math.Atan2(H, W) * 180 / Math.PI) * Math.PI / 180;
            float c = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            const float lead = 1.15f, cap = 0.72f;
            int maxUnits = 1;
            foreach (var line in lines) maxUnits = Math.Max(maxUnits, font.MeasureUnits(line, 0, line.Length));
            float size = wm.FontSize;
            if (size <= 0)
            {
                // As large as fits in 80% of the page with the text block rotated.
                float tw = maxUnits / 1000f, th = lead * (lines.Length - 1) + cap;
                float ac = Math.Abs(c), asn = Math.Abs(sn);
                size = Math.Min(0.8f * W / (tw * ac + th * asn), 0.8f * H / (tw * asn + th * ac));
                size = Math.Clamp(size, 4, 400);
            }
            uint color = CssColor.TryParse(wm.Color ?? "", CssColor.Black, out var parsed) ? parsed : 0xFF808080;
            WriteColor(b, color);
            b.Ascii(" rg BT /Fw ").Num(size).Ascii(" Tf\n");
            float blockH = size * (lead * (lines.Length - 1) + cap);
            for (int i = 0; i < lines.Length; i++)
            {
                float xl = -font.Measure(lines[i], size) / 2;
                float yl = blockH / 2 - cap * size - i * lead * size;
                b.Num(c).Sp().Num(sn).Sp().Num(-sn).Sp().Num(c).Sp().Num(cx + xl * c - yl * sn).Sp().Num(cy + xl * sn + yl * c).Ascii(" Tm ");
                WriteTextBytes(b, lines[i]);
                b.Ascii(" Tj\n");
            }
            b.Ascii("ET\n");
        }
        b.Ascii("Q\n");

        var dict = new ByteBuffer(256);
        dict.Ascii("/Type /XObject /Subtype /Form /BBox [0 0 ").Num(W).Sp().Num(H).Ascii("] /Resources << /ExtGState << /Gw << /Type /ExtGState /ca ")
            .Num(opacity).Ascii(" /CA ").Num(opacity).Ascii(" >> >>");
        if (font != null) dict.Ascii(" /Font << /Fw ").Int(fontId).Ascii(" 0 R >>");
        if (image != null) dict.Ascii(" /XObject << /Iw ").Int(image.ObjectNumber).Ascii(" 0 R >>");
        dict.Ascii(" >>");
        WriteStream(id, b.Span, _compress, System.Text.Encoding.ASCII.GetString(dict.Span));
    }

    private static string? Clean(string? s)
    {
        if (s == null) return null;
        foreach (char ch in s)
            if (ch < 0x20 && ch is not ('\t' or '\n' or '\r'))
                return new string(s.Where(x => x >= 0x20 || x is '\t' or '\n' or '\r').ToArray());
        return s;
    }

    // ------------------------------------------------------------------ content streams

    private void ResetState()
    {
        _fill = CssColor.Black;
        _inText = false;
        _textFont = null;
        _textSize = 0;
        _tw = 0;
        _tc = 0;
    }

    private float PX(float x) => _page.MarginLeft + x;
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
                if (!ReferenceEquals(_textFont, t.Font) || _textSize != t.Size)
                {
                    _textFont = t.Font;
                    _textSize = t.Size;
                    b.Ascii("/F").Int(t.Font.Index + 1).Sp().Num(t.Size).Ascii(" Tf\n");
                }
                if (_tw != t.WordSpacing) { _tw = t.WordSpacing; b.Num(t.WordSpacing).Ascii(" Tw\n"); }
                if (_tc != t.CharSpacing) { _tc = t.CharSpacing; b.Num(t.CharSpacing).Ascii(" Tc\n"); }
                b.Ascii("1 0 0 1 ").Num(PX(t.X)).Sp().Num(PY(t.Baseline, pageTop)).Ascii(" Tm ");
                WriteTextBytes(b, t.Text);
                b.Ascii(" Tj\n");
                break;
            }
            case RectOp r:
            {
                EndText(b);
                SetFill(b, r.Color);
                float top = placed.Y0, bottom = placed.Y1;
                b.Num(PX(r.X)).Sp().Num(PY(bottom, pageTop)).Sp().Num(r.W).Sp().Num(bottom - top).Ascii(" re f\n");
                break;
            }
            case GradientOp gr:
            {
                EndText(b);
                float top = placed.Y0, bottom = placed.Y1;
                // Gradient space is the op's box; the pattern maps it to the page.
                var toPage = new Matrix(1, 0, 0, -1, PX(gr.X), PY(gr.Y, pageTop));
                var name = AddPattern(gr.Gradient, toPage, 0, 0, gr.W, gr.H);
                b.Ascii("q /Pattern cs /P").Int(name).Ascii(" scn ").Num(PX(gr.X)).Sp().Num(PY(bottom, pageTop)).Sp().Num(gr.W).Sp()
                    .Num(bottom - top).Ascii(" re f Q\n");
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
                    b.Ascii(" RG ").Num(Math.Max(0.5f, r * 0.3f)).Ascii(" w ");
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
                    b.Ascii("BT /F").Int(shape.Font!.Index + 1).Sp().Num(shape.FontSize).Ascii(" Tf ").Int(mode).Ascii(" Tr 0 Tw ")
                        .Num(shape.CharSpacing).Ascii(" Tc ").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
                        .Ascii(" Tm ");
                    WriteTextBytes(b, shape.Text!);
                    b.Ascii(" Tj ET\n");
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

    /// <summary>Adds a shading pattern to the shared resources and returns its number.</summary>
    private int AddPattern(Gradient g, Matrix toPage, float minX, float minY, float maxX, float maxY)
    {
        int n = ++_patternCount;
        _patterns.Ascii(" /P").Int(n).Sp();
        g.WritePattern(_patterns, toPage, minX, minY, maxX, maxY);
        return n;
    }

    private void WriteMarginText(ByteBuffer b, StandardFont font, string template, int page, int pages, bool top)
    {
        string text = template.Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture))
                              .Replace("{pages}", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        const float size = 9f;
        float w = font.Measure(text, size);
        float x = (_page.Width - w) / 2;
        float marginH = top ? _page.MarginTop : _page.MarginBottom;
        float y = top ? _page.Height - marginH / 2 - size / 3 : marginH / 2 - size / 3;
        b.Ascii("BT 0.4 0.4 0.4 rg /F").Int(font.Index + 1).Sp().Num(size).Ascii(" Tf 0 Tw 0 Tc 1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");
        WriteTextBytes(b, text);
        b.Ascii(" Tj ET\n");
        _fill = 0xFF666666;
        _tw = 0;
        _tc = 0;
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

    /// <summary>ASCII with other characters percent-encoded as UTF-8 (the form URIs need).</summary>
    private static byte[] AsciiStringBytes(string s)
    {
        var raw = new ByteBuffer(s.Length + 8);
        foreach (char ch in s)
        {
            if (ch > 126 || ch < 32)
            {
                foreach (var u in System.Text.Encoding.UTF8.GetBytes(ch.ToString()))
                    raw.Byte((byte)'%').Ascii(u.ToString("X2"));
                continue;
            }
            raw.Byte((byte)ch);
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
            WriteAsciiString(b, s);
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

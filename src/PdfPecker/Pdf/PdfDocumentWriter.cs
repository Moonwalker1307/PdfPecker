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

/// <summary>Serializes laid-out draw operations into a PDF 1.4 file.</summary>
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
        Func<int, int, List<DrawOp>>? pageBands = null)
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
                    PageList(PageOf(lk.Y)).Add(new Placed(lk, 0, 0));
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

        // 4. Header.
        _out.Ascii("%PDF-1.4\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        // Catalog, pages tree.
        Begin(catalogId).Ascii("<< /Type /Catalog /Pages ").Int(pagesId).Ascii(" 0 R >>");
        End();
        Begin(pagesId).Ascii("<< /Type /Pages /Count ").Int(pageCount).Ascii(" /Kids [");
        for (int i = 0; i < pageCount; i++) _out.Int(pageIds[i]).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();

        // Info.
        Begin(infoId).Ascii("<< /Producer ");
        WriteTextString(_out, "PdfPecker");
        if (!string.IsNullOrEmpty(title)) { _out.Ascii(" /Title "); WriteTextString(_out, title!); }
        if (!string.IsNullOrEmpty(author)) { _out.Ascii(" /Author "); WriteTextString(_out, author!); }
        if (!string.IsNullOrEmpty(subject)) { _out.Ascii(" /Subject "); WriteTextString(_out, subject!); }
        _out.Ascii(" /CreationDate (D:").Ascii(DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)).Ascii("Z) >>");
        End();

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

        // Pages, contents, annotations.
        var content = new ByteBuffer(32 * 1024);
        var annotations = new List<(int Id, LinkOp Link, float Origin, float PageTop)>();
        for (int p = 0; p < pageCount; p++)
        {
            content.Clear();
            ResetState();
            if (CssColor.IsVisible(pageBackground))
            {
                SetFill(content, pageBackground);
                content.Ascii("0 0 ").Num(_page.Width).Sp().Num(_page.Height).Ascii(" re f\n");
            }
            var pageLinks = new List<(LinkOp Link, float Origin, float PageTop)>();
            _yOrigin = _page.MarginTop + _page.BandTop;
            foreach (var placed in pages[p])
            {
                if (placed.Op is LinkOp lk) { pageLinks.Add((lk, _yOrigin, p * H)); continue; }
                WriteOp(content, placed, p * H, imageIds);
            }
            _yOrigin = _page.MarginTop;
            foreach (var placed in bands[p])
            {
                if (placed.Op is LinkOp lk) { pageLinks.Add((lk, _yOrigin, 0)); continue; }
                WriteOp(content, placed, 0, imageIds);
            }
            if (hasMargins)
            {
                EndText(content);
                if (headerTemplate != null) WriteMarginText(content, marginFont, headerTemplate, p + 1, pageCount, top: true);
                if (footerTemplate != null) WriteMarginText(content, marginFont, footerTemplate, p + 1, pageCount, top: false);
            }
            EndText(content);

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

        foreach (var (id, link, origin, linkPageTop) in annotations)
        {
            float x1 = _page.MarginLeft + link.X, x2 = x1 + link.W;
            float yTop = _page.Height - origin - (link.Y - linkPageTop);
            float yBottom = yTop - link.H;
            Begin(id).Ascii("<< /Type /Annot /Subtype /Link /Rect [").Num(x1).Sp().Num(yBottom).Sp().Num(x2).Sp().Num(yTop)
                .Ascii("] /Border [0 0 0] ");
            if (link.Target.StartsWith("#", StringComparison.Ordinal))
            {
                if (anchors.TryGetValue(link.Target.Substring(1), out float ay))
                {
                    int ap = Math.Min(PageOf(ay), pageCount - 1);
                    float py = _page.Height - _page.MarginTop - _page.BandTop - (ay - ap * H);
                    _out.Ascii("/Dest [").Int(pageIds[ap]).Ascii(" 0 R /XYZ 0 ").Num(py).Ascii(" null]");
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

        // Shared resources, written last: shading patterns are collected while writing the pages.
        Begin(resourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        for (int i = 0; i < usedFonts.Length; i++)
            if (usedFonts[i]) _out.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
        _out.Ascii(" >>");
        if (images.Count > 0)
        {
            _out.Ascii(" /XObject <<");
            for (int i = 0; i < images.Count; i++) _out.Ascii(" /Im").Int(i + 1).Sp().Int(images[i].ObjectNumber).Ascii(" 0 R");
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
        _out.Ascii("trailer\n<< /Size ").Int(count).Ascii(" /Root ").Int(catalogId).Ascii(" 0 R /Info ").Int(infoId)
            .Ascii(" 0 R >>\nstartxref\n").Int(xref).Ascii("\n%%EOF\n");

        return _out.Span.ToArray();
    }

    // ------------------------------------------------------------------ objects

    private ByteBuffer Begin(int id)
    {
        while (_offsets.Count <= id) _offsets.Add(0);
        _offsets[id] = _out.Length;
        return _out.Int(id).Ascii(" 0 obj\n");
    }

    private void End() => _out.Ascii("\nendobj\n");

    private void WriteStream(int id, ReadOnlySpan<byte> data, bool compress, string? extraDict)
    {
        byte[]? compressed = null;
        if (compress && data.Length > 64)
        {
            using var ms = new MemoryStream(data.Length / 3 + 64);
            using (var z = new ZLibStream(ms, _level, leaveOpen: true)) z.Write(data);
            compressed = ms.ToArray();
        }
        Begin(id).Ascii("<< /Length ").Int(compressed?.Length ?? data.Length);
        if (compressed != null) _out.Ascii(" /Filter /FlateDecode");
        if (extraDict != null) _out.Sp().Ascii(extraDict);
        _out.Ascii(" >>\nstream\n");
        if (compressed != null) _out.Bytes(compressed);
        else _out.Bytes(data);
        _out.Ascii("\nendstream");
        End();
    }

    private void WriteImage(PdfImage img)
    {
        Begin(img.ObjectNumber).Ascii("<< /Type /XObject /Subtype /Image /Width ").Int(img.Width).Ascii(" /Height ").Int(img.Height)
            .Ascii(" /ColorSpace ").Ascii(img.ColorSpace).Ascii(" /BitsPerComponent ").Int(img.BitsPerComponent)
            .Ascii(" /Filter ").Ascii(img.Filter);
        if (img.DecodeParms != null) _out.Ascii(" /DecodeParms ").Ascii(img.DecodeParms);
        if (img.Decode != null) _out.Ascii(" /Decode ").Ascii(img.Decode);
        if (img.SMask != null) _out.Ascii(" /SMask ").Int(img.SMask.ObjectNumber).Ascii(" 0 R");
        _out.Ascii(" /Length ").Int(img.Data.Length).Ascii(" >>\nstream\n").Bytes(img.Data).Ascii("\nendstream");
        End();
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

    private static void WriteAsciiString(ByteBuffer b, string s)
    {
        b.Byte((byte)'(');
        foreach (char ch in s)
        {
            if (ch > 126 || ch < 32)
            {
                foreach (var u in System.Text.Encoding.UTF8.GetBytes(ch.ToString()))
                    b.Byte((byte)'%').Ascii(u.ToString("X2"));
                continue;
            }
            if (ch == '(' || ch == ')' || ch == '\\') b.Byte((byte)'\\');
            b.Byte((byte)ch);
        }
        b.Byte((byte)')');
    }

    /// <summary>Writes a PDF text string: PDFDocEncoding-compatible ASCII when possible, UTF-16BE otherwise.</summary>
    private static void WriteTextString(ByteBuffer b, string s)
    {
        bool ascii = true;
        foreach (char ch in s) if (ch > 126) { ascii = false; break; }
        if (ascii)
        {
            WriteAsciiString(b, s);
            return;
        }
        b.Ascii("<FEFF");
        foreach (char ch in s) b.Ascii(((int)ch).ToString("X4"));
        b.Byte((byte)'>');
    }
}

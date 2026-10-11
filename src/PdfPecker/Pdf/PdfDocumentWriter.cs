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
    private TextFont? _textFont;
    private float _textSize, _tw, _tc;
    private int _tr;           // text rendering mode: 0 fill, 2 fill and stroke (synthetic bold)
    private uint _stroke;
    private float _lineWidth;
    private readonly List<FontRun> _runs = new();
    private readonly Dictionary<FontProgram, GlyphSubset> _subsets = new();
    private readonly List<GlyphSubset> _subsetList = new();
    private float _yOrigin; // distance from the page top to flow y = 0 on the page being written

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
                if (p.Op is TextOp t) CollectFonts(t, usedFonts);
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

        // 4. Header.
        _out.Ascii("%PDF-1.4\n%").Bytes(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).Nl();

        // Catalog, pages tree.
        Begin(catalogId).Ascii("<< /Type /Catalog /Pages ").Int(pagesId).Ascii(" 0 R >>");
        End();
        Begin(pagesId).Ascii("<< /Type /Pages /Count ").Int(pageCount).Ascii(" /Kids [");
        for (int i = 0; i < pageCount; i++) _out.Int(pageIds[i]).Ascii(" 0 R ");
        _out.Ascii("] >>");
        End();

        // Shared resources.
        Begin(resourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        for (int i = 0; i < usedFonts.Length; i++)
            if (usedFonts[i]) _out.Ascii(" /F").Int(i + 1).Sp().Int(fontIds[i]).Ascii(" 0 R");
        for (int i = 0; i < _subsetList.Count; i++) _out.Ascii(" /E").Int(i + 1).Sp().Int(firstEmbeddedId + i * 5).Ascii(" 0 R");
        _out.Ascii(" >>");
        if (images.Count > 0)
        {
            _out.Ascii(" /XObject <<");
            for (int i = 0; i < images.Count; i++) _out.Ascii(" /Im").Int(i + 1).Sp().Int(images[i].ObjectNumber).Ascii(" 0 R");
            _out.Ascii(" >>");
        }
        _out.Ascii(" >>");
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
        for (int i = 0; i < _subsetList.Count; i++) WriteEmbeddedFont(_subsetList[i], firstEmbeddedId + i * 5);

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
        _tr = 0;
        _stroke = CssColor.Black;
        _lineWidth = 1;
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
                if (_tw != t.WordSpacing) { _tw = t.WordSpacing; b.Num(t.WordSpacing).Ascii(" Tw\n"); }
                if (_tc != t.CharSpacing) { _tc = t.CharSpacing; b.Num(t.CharSpacing).Ascii(" Tc\n"); }
                float y = PY(t.Baseline, pageTop);
                if (t.Font is StandardFont sf)
                {
                    WriteStandardRun(b, sf, t, 0, t.Text.Length, PX(t.X), y);
                    break;
                }
                _runs.Clear();
                t.Font.Split(t.Text, _runs);
                float x = PX(t.X);
                foreach (var run in _runs)
                {
                    if (run.Font is EmbeddedFont ef) WriteEmbeddedRun(b, ef, t, run.Start, run.Length, x, y);
                    else WriteStandardRun(b, (StandardFont)run.Font, t, run.Start, run.Length, x, y);
                    x += RunAdvance(run.Font, t, run.Start, run.Length);
                }
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
        b.Num(sx).Ascii(" 0 0 ").Num(-sy).Sp().Num(left + tx - vx * sx).Sp().Num(top - ty + vy * sy).Ascii(" cm\n");
        foreach (var shape in img.Shapes)
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
            if (shape.Fill is { } fill)
            {
                WriteColor(b, fill);
                b.Ascii(" rg\n");
            }
            if (shape.Stroke is { } stroke)
            {
                WriteColor(b, stroke);
                b.Ascii(" RG ").Num(shape.StrokeWidth).Ascii(" w ").Int((int)shape.LineCap).Ascii(" J ").Int((int)shape.LineJoin).Ascii(" j ")
                    .Num(shape.MiterLimit).Ascii(" M\n");
            }
            if (shape.Fill != null && shape.Stroke != null) b.Ascii(shape.EvenOdd ? "B*\n" : "B\n");
            else if (shape.Fill != null) b.Ascii(shape.EvenOdd ? "f*\n" : "f\n");
            else b.Ascii("S\n");
        }
        b.Ascii("Q\n");
        _fill = 1; // the colour set inside q/Q is gone: force the next fill colour to be written
    }

    // ------------------------------------------------------------------ text

    private void CollectFonts(TextOp t, bool[] usedFonts)
    {
        if (t.Font is StandardFont sf)
        {
            usedFonts[sf.Index] = true;
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        foreach (var run in _runs)
        {
            if (run.Font is StandardFont s) { usedFonts[s.Index] = true; continue; }
            var subset = Subset(((EmbeddedFont)run.Font).Program);
            string text = t.Text;
            for (int i = run.Start, end = run.Start + run.Length; i < end; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
                subset.Cid(cp);
            }
        }
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
        WriteTextBytes(b, start == 0 && length == t.Text.Length ? t.Text : t.Text.Substring(start, length));
        b.Ascii(" Tj\n");
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
        string text = t.Text;
        bool adjust = t.WordSpacing != 0 && t.Size > 0;
        float spaceAdjust = adjust ? -t.WordSpacing * 1000f / t.Size : 0;
        b.Ascii(adjust ? "[<" : "<");
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            Hex4(b, subset.Cid(cp));
            if (adjust && cp == ' ' && i + 1 < end) b.Ascii(">").Num(spaceAdjust).Ascii("<");
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

    private void WriteMarginText(ByteBuffer b, StandardFont font, string template, int page, int pages, bool top)
    {
        string text = template.Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture))
                              .Replace("{pages}", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        const float size = 9f;
        float w = font.Measure(text, size);
        float x = (_page.Width - w) / 2;
        float marginH = top ? _page.MarginTop : _page.MarginBottom;
        float y = top ? _page.Height - marginH / 2 - size / 3 : marginH / 2 - size / 3;
        if (_tr != 0) { _tr = 0; b.Ascii("0 Tr "); }
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

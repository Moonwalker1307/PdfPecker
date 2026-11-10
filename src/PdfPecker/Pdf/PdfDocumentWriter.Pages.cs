using System.Globalization;
using PdfPecker.Css;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>The pages: their content streams, page objects and link annotations.</summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>
    /// Writes every page's content stream and page object. The link annotations get their numbers here and are written
    /// after the pages, once the structure elements they belong to are numbered.
    /// </summary>
    private void WritePages(WriteState doc)
    {
        var content = ByteBuffer.Rent(32 * 1024);
        doc.PageStreams = new McStream[doc.PageCount];
        doc.ParentKey = doc.PageCount; // keys 0 to pageCount - 1 of the parent tree are the pages'
        for (int p = 0; p < doc.PageCount; p++)
        {
            Cancellation.ThrowIfCancellationRequested();
            content.Clear();
            ResetState();
            // A scaled page is written at the size Chrome gives the paper (see PaperSize).
            float paperW = SetPage(p, out float paperH);
            var pageLinks = WritePageContent(doc, p, content);
            var annotIds = NumberPageAnnotations(doc, p, pageLinks);
            WritePageObject(doc, p, paperW, paperH, annotIds);
            WriteStream(doc.ContentIds[p], content.Span, _compress, extraDict: null);
        }
        content.Return();
    }

    /// <summary>
    /// Writes the content stream of page <paramref name="p"/>, whose setup is current: the background and canvas, the
    /// watermark when it is behind, the flowing content, the running bands and margin texts, and the watermark when it
    /// is in front. Returns the page's links, which become annotations instead of content.
    /// </summary>
    private List<(Placed Link, float Origin, float PageTop, Matrix? Transform)> WritePageContent(WriteState doc, int p, ByteBuffer content)
    {
        float paperDy = _paperDy, pageTop = PageTop(p);
        if (Tagged) { _mc = doc.PageStreams[p] = new McStream(p); _mcOpen = null; }
        if (_page.Scale != 1)
        {
            // Num keeps 3 decimals, too few for a scale such as 0.99916. The content keeps to the top of the page.
            string k = _page.Scale.ToString("0.######", CultureInfo.InvariantCulture);
            content.Ascii("q ").Ascii(k).Ascii(" 0 0 ").Ascii(k).Ascii(" 0 ").Num(paperDy).Ascii(" cm\n");
            _streamSpace = new Matrix(_page.Scale, 0, 0, _page.Scale, 0, paperDy);
        }
        // A named page's own @page background, or the document's.
        uint background = _sections.Count > 1 || _sections[0].Grid.Varies ? _page.Background : doc.PageBackground;
        if (CssColor.IsVisible(background))
        {
            Mark(content, null);
            SetFill(content, background);
            float bleed = _page.BleedWidth;
            if (bleed > 0)
            {
                // The page background reaches to the edge of the bleed, in the scaled content's own units.
                float k = _page.Scale;
                content.Num(-bleed / k).Sp().Num((-bleed - paperDy) / k).Sp().Num((_paper.W + 2 * bleed) / k).Sp()
                    .Num((_paper.H + 2 * bleed) / k).Ascii(" re f\n");
            }
            else content.Ascii("0 0 ").Num(_page.Width).Sp().Num(_page.Height).Ascii(" re f\n");
        }
        _yOrigin = _page.MarginTop;
        _forceArtifact = true;
        foreach (var placed in doc.Canvas[p]) WriteOp(content, placed, 0, doc.ImageIds);
        _forceArtifact = false;
        var wm = doc.Watermark;
        if (wm is { Behind: true }) { Mark(content, null); content.Ascii(WatermarkDo(doc.FirstSetup)); }
        var pageLinks = new List<(Placed Link, float Origin, float PageTop, Matrix? Transform)>();
        _yOrigin = _page.MarginTop + _page.BandTop;
        (_flowTop, _flowBottom) = (pageTop, pageTop + _page.FlowHeight);
        _pageCanvas = (background, doc.Canvas[p], pageTop);
        bool bandsWritten = false;
        foreach (var placed in doc.Pages[p])
        {
            if (placed.Op is FixedBlock fixedBox)
            {
                // Drawn as the bands are, from the top of the page area, between the flow content below and above it.
                var (flowOrigin, flowTop, flowBottom) = (_yOrigin, _flowTop, _flowBottom);
                (_yOrigin, _flowTop, _flowBottom) = (_page.MarginTop, float.NaN, float.NaN);
                // The running bands are the root's in-flow content, so a fixed box without a negative z-index paints
                // over them, as it paints over a header that Chrome prints once in the flow.
                if (!bandsWritten && fixedBox.Order >= 0)
                {
                    WriteBand(doc, content, doc.Bands[p], pageLinks);
                    bandsWritten = true;
                }
                WriteBand(doc, content, fixedBox.Items, pageLinks);
                (_yOrigin, _flowTop, _flowBottom) = (flowOrigin, flowTop, flowBottom);
                continue;
            }
            if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, pageTop, null)); continue; }
            AddNestedLinks(placed, pageLinks, _yOrigin, pageTop);
            WriteOp(content, placed, pageTop, doc.ImageIds);
        }
        _flowTop = _flowBottom = float.NaN;
        _yOrigin = _page.MarginTop;
        if (!bandsWritten) WriteBand(doc, content, doc.Bands[p], pageLinks);
        if (doc.HasMargins)
        {
            Mark(content, null);
            EndText(content);
            if (doc.HeaderTemplate != null) WriteMarginText(content, doc.MarginFont, doc.HeaderTemplate, p + 1, doc.PageCount, top: true);
            if (doc.FooterTemplate != null) WriteMarginText(content, doc.MarginFont, doc.FooterTemplate, p + 1, doc.PageCount, top: false);
        }
        EndText(content);
        if (wm is { Behind: false }) { Mark(content, null); content.Ascii(WatermarkDo(doc.FirstSetup)); }
        EndMarked(content);
        if (_page.Scale != 1) content.Ascii("Q\n");
        _streamSpace = null;
        if (_page.CropMarks || _page.CrossMarks) WritePrinterMarks(content);
        return pageLinks;
    }

    /// <summary>Writes band ops, such as running headers and fixed boxes, as artifacts placed from the top of the page area.</summary>
    private void WriteBand(WriteState doc, ByteBuffer content, List<Placed> items, List<(Placed Link, float Origin, float PageTop, Matrix? Transform)> pageLinks)
    {
        _forceArtifact = true;
        foreach (var placed in items)
        {
            if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, 0, null)); continue; }
            AddNestedLinks(placed, pageLinks, _yOrigin, 0);
            WriteOp(content, placed, 0, doc.ImageIds);
        }
        _forceArtifact = false;
    }

    /// <summary>
    /// Numbers the link annotations of page <paramref name="p"/> and lists them with its widgets. In a tagged file each
    /// annotation gets a parent tree key and joins its structure element. Returns the page's annotations.
    /// </summary>
    private List<int> NumberPageAnnotations(WriteState doc, int p, List<(Placed Link, float Origin, float PageTop, Matrix? Transform)> pageLinks)
    {
        var annotIds = new List<int>();
        // PDF/X allows no annotations on the printed page but trap networks and printer's marks, so links are left out.
        foreach (var (lk, origin, linkTop, transform) in IsPdfX ? new() : pageLinks)
        {
            int id = doc.Next++;
            annotIds.Add(id);
            doc.Annotations.Add((id, lk, origin, linkTop, transform, p));
            if (Tagged)
            {
                // A link annotation belongs to its Link element; one outside the structure (in a running header,
                // say) gets a Link element of its own at the end.
                var linkOp = (LinkOp)lk.Op;
                var link = linkOp.Link ?? new StructElem("Link", Structure!.Root) { Text = linkOp.Target };
                link.Kids.Add(new AnnotRef(id, p));
                _mc!.Annotations.Add((id, link));
                doc.AnnotKeys[id] = doc.ParentKey++;
            }
        }
        foreach (var field in doc.PageFields[p])
        {
            int id = _fieldIds[field];
            annotIds.Add(id);
            if (Tagged)
            {
                // A widget belongs to its Form element; one outside the structure gets a Form element of its own.
                var f = field.Field;
                var form = f.Form ?? new StructElem("Form", Structure!.Root) { Alt = f.Field.Tooltip ?? f.Field.Name };
                form.Kids.Add(new AnnotRef(id, p));
                _mc!.Annotations.Add((id, form));
                doc.AnnotKeys[id] = doc.ParentKey++;
            }
        }
        return annotIds;
    }

    /// <summary>
    /// Draws the @page marks in the outer half of the bleed, over everything else, as WeasyPrint draws them: crop marks
    /// that continue the trim lines at each corner, and a registration cross in a circle at the middle of each side.
    /// They are artifacts in a tagged file.
    /// </summary>
    private void WritePrinterMarks(ByteBuffer content)
    {
        float b = _page.BleedWidth;
        if (b <= 0) return;
        _forceArtifact = true;
        Mark(content, null);
        _forceArtifact = false;
        var state = SaveState();
        // A hairline-thin mark is lost in print; 0.75pt is the CSS pixel WeasyPrint strokes them with.
        content.Ascii("q 0.75 w [] 0 d 0 J\n");
        SetStroke(content, 0xFF000000);
        float w = _paper.W, h = _paper.H, half = b / 2;
        if (_page.CropMarks)
        {
            foreach (float x in new[] { 0f, w })
            {
                // Vertical marks above and below the page, on the left and right trim lines.
                Line(content, x, h + half, x, h + b);
                Line(content, x, -b, x, -half);
            }
            foreach (float y in new[] { 0f, h })
            {
                Line(content, -b, y, -half, y);
                Line(content, w + half, y, w + b, y);
            }
        }
        if (_page.CrossMarks)
        {
            // Each cross fills the outer half of the bleed, with half the line width of the crop marks.
            content.Ascii("0.375 w\n");
            float at = b * 0.75f, arm = b / 4, r = b / 8;
            Cross(content, w / 2, h + at, r, arm);
            Cross(content, w / 2, -at, r, arm);
            Cross(content, -at, h / 2, r, arm);
            Cross(content, w + at, h / 2, r, arm);
        }
        content.Ascii("Q\n");
        RestoreState(state);

        static void Line(ByteBuffer c, float x1, float y1, float x2, float y2)
            => c.Num(x1).Sp().Num(y1).Ascii(" m ").Num(x2).Sp().Num(y2).Ascii(" l S\n");

        static void Cross(ByteBuffer c, float x, float y, float r, float arm)
        {
            // A circle of four Bézier quarters, and the two strokes of the cross through it.
            const float K = 0.5523f;
            float d = r * K;
            c.Num(x + r).Sp().Num(y).Ascii(" m ")
                .Num(x + r).Sp().Num(y + d).Sp().Num(x + d).Sp().Num(y + r).Sp().Num(x).Sp().Num(y + r).Ascii(" c ")
                .Num(x - d).Sp().Num(y + r).Sp().Num(x - r).Sp().Num(y + d).Sp().Num(x - r).Sp().Num(y).Ascii(" c ")
                .Num(x - r).Sp().Num(y - d).Sp().Num(x - d).Sp().Num(y - r).Sp().Num(x).Sp().Num(y - r).Ascii(" c ")
                .Num(x + d).Sp().Num(y - r).Sp().Num(x + r).Sp().Num(y - d).Sp().Num(x + r).Sp().Num(y).Ascii(" c S\n");
            Line(c, x - arm, y, x + arm, y);
            Line(c, x, y - arm, x, y + arm);
        }
    }

    private void WritePageObject(WriteState doc, int p, float paperW, float paperH, List<int> annotIds)
    {
        Begin(doc.PageIds[p]).Ascii("<< /Type /Page /Parent ").Int(doc.PagesId).Ascii(" 0 R /MediaBox [");
        float bleed = _page.BleedWidth;
        if (bleed > 0)
        {
            // The paper is the trim box; the media box and bleed box reach past it by the bleed, so the page's own
            // coordinates, and with them every link and field, stay as they are.
            _out.Num(-bleed).Sp().Num(-bleed).Sp().Num(paperW + bleed).Sp().Num(paperH + bleed).Ascii("] /BleedBox [")
                .Num(-bleed).Sp().Num(-bleed).Sp().Num(paperW + bleed).Sp().Num(paperH + bleed).Ascii("] /TrimBox [0 0 ")
                .Num(paperW).Sp().Num(paperH);
        }
        else if (IsPdfX)
        {
            // PDF/X needs every page's trim box: without a bleed it is the paper.
            _out.Ascii("0 0 ").Num(paperW).Sp().Num(paperH).Ascii("] /TrimBox [0 0 ").Num(paperW).Sp().Num(paperH);
        }
        else _out.Ascii("0 0 ").Num(paperW).Sp().Num(paperH);
        _out.Ascii("] /Resources ").Int(doc.ResourcesId).Ascii(" 0 R /Contents ").Int(doc.ContentIds[p]).Ascii(" 0 R");
        if (annotIds.Count > 0)
        {
            _out.Ascii(" /Annots [");
            foreach (var id in annotIds) _out.Int(id).Ascii(" 0 R ");
            _out.Ascii("]");
        }
        if (Tagged)
        {
            if (_mc!.Elements.Count > 0) _out.Ascii(" /StructParents ").Int(p);
            _out.Ascii(" /Tabs /S"); // PDF/UA: annotations in structure order
        }
        _out.Ascii(" >>");
        End();
    }

    /// <summary>Writes the link annotations the pages collected, in page order.</summary>
    private void WriteLinkAnnotations(WriteState doc)
    {
        foreach (var annotation in doc.Annotations) WriteLinkAnnotation(doc, in annotation);
    }

    /// <summary>
    /// Writes a link annotation: its rectangle on the page, and a jump to an anchor in the document or a URI action.
    /// </summary>
    private void WriteLinkAnnotation(WriteState doc, in (int Id, Placed Link, float Origin, float PageTop, Matrix? Transform, int Page) annotation)
    {
        var (id, placed, origin, linkPageTop, transform, linkPage) = annotation;
        SetPage(linkPage, out _);
        float paperDy = _paperDy;
        var link = (LinkOp)placed.Op;
        float x1 = _page.MarginLeft + link.X, x2 = x1 + link.W;
        float yTop = _page.Height - origin - (placed.Y0 - linkPageTop);
        float yBottom = yTop - (placed.Y1 - placed.Y0);
        float k = _page.Scale;
        Span<float> quad = stackalloc float[8];
        bool turned = false;
        if (transform is { } m)
        {
            // A link in a transformed layer: the rectangle is the bounding box of its corners where the layer draws
            // them. Turned or skewed, the corners themselves go in /QuadPoints, top edge first as in text markup.
            (quad[0], quad[1], quad[2], quad[3], quad[4], quad[5], quad[6], quad[7]) = (x1, yTop, x2, yTop, x1, yBottom, x2, yBottom);
            for (int i = 0; i < 8; i += 2) m.Apply(ref quad[i], ref quad[i + 1]);
            x1 = Math.Min(Math.Min(quad[0], quad[2]), Math.Min(quad[4], quad[6]));
            x2 = Math.Max(Math.Max(quad[0], quad[2]), Math.Max(quad[4], quad[6]));
            yBottom = Math.Min(Math.Min(quad[1], quad[3]), Math.Min(quad[5], quad[7]));
            yTop = Math.Max(Math.Max(quad[1], quad[3]), Math.Max(quad[5], quad[7]));
            turned = m.B != 0 || m.C != 0;
        }
        x1 *= k; x2 *= k; yTop = yTop * k + paperDy; yBottom = yBottom * k + paperDy;
        Begin(id).Ascii("<< /Type /Annot /Subtype /Link /Rect [").Num(x1).Sp().Num(yBottom).Sp().Num(x2).Sp().Num(yTop).Ascii("] ");
        if (turned)
        {
            _out.Ascii("/QuadPoints [");
            for (int i = 0; i < 8; i += 2)
                _out.Num(quad[i] * k).Sp().Num(quad[i + 1] * k + paperDy).Ascii(i < 6 ? " " : "] ");
        }
        _out.Ascii("/F 4 /Border [0 0 0] ");
        if (doc.AnnotKeys.TryGetValue(id, out int key))
        {
            // PDF/UA: a link's description, for assistive technology.
            var text = (link.Link?.Text is { Length: > 0 } t ? t : null) ?? link.Target;
            _out.Ascii("/StructParent ").Int(key).Ascii(" /Contents ");
            WriteTextString(_out, text);
            _out.Sp();
        }
        if (link.Target.StartsWith('#'))
        {
            var anchors = doc.Anchors;
            string fragment = link.Target.Substring(1);
            string anchor = Uri.UnescapeDataString(fragment);
            if (!anchors.TryGetValue(anchor, out float ay) && anchors.TryGetValue(fragment, out ay))
                anchor = fragment;
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
}

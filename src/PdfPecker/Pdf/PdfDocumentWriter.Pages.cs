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
    private List<(Placed Link, float Origin, float PageTop)> WritePageContent(WriteState doc, int p, ByteBuffer content)
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
            content.Ascii("0 0 ").Num(_page.Width).Sp().Num(_page.Height).Ascii(" re f\n");
        }
        _yOrigin = _page.MarginTop;
        _forceArtifact = true;
        foreach (var placed in doc.Canvas[p]) WriteOp(content, placed, 0, doc.ImageIds);
        _forceArtifact = false;
        var wm = doc.Watermark;
        if (wm is { Behind: true }) { Mark(content, null); content.Ascii(WatermarkDo(doc.FirstSetup)); }
        var pageLinks = new List<(Placed Link, float Origin, float PageTop)>();
        _yOrigin = _page.MarginTop + _page.BandTop;
        (_flowTop, _flowBottom) = (pageTop, pageTop + _page.FlowHeight);
        _pageCanvas = (background, doc.Canvas[p], pageTop);
        foreach (var placed in doc.Pages[p])
        {
            if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, pageTop)); continue; }
            AddUntransformedLinks(placed, pageLinks, _yOrigin, pageTop);
            WriteOp(content, placed, pageTop, doc.ImageIds);
        }
        _flowTop = _flowBottom = float.NaN;
        _yOrigin = _page.MarginTop;
        _forceArtifact = true;
        foreach (var placed in doc.Bands[p])
        {
            if (placed.Op is LinkOp) { pageLinks.Add((placed, _yOrigin, 0)); continue; }
            AddUntransformedLinks(placed, pageLinks, _yOrigin, 0);
            WriteOp(content, placed, 0, doc.ImageIds);
        }
        _forceArtifact = false;
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
        return pageLinks;
    }

    /// <summary>
    /// Numbers the link annotations of page <paramref name="p"/> and lists them with its widgets. In a tagged file each
    /// annotation gets a parent tree key and joins its structure element. Returns the page's annotations.
    /// </summary>
    private List<int> NumberPageAnnotations(WriteState doc, int p, List<(Placed Link, float Origin, float PageTop)> pageLinks)
    {
        var annotIds = new List<int>();
        foreach (var (lk, origin, linkTop) in pageLinks)
        {
            int id = doc.Next++;
            annotIds.Add(id);
            doc.Annotations.Add((id, lk, origin, linkTop, p));
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

    private void WritePageObject(WriteState doc, int p, float paperW, float paperH, List<int> annotIds)
    {
        Begin(doc.PageIds[p]).Ascii("<< /Type /Page /Parent ").Int(doc.PagesId).Ascii(" 0 R /MediaBox [0 0 ")
            .Num(paperW).Sp().Num(paperH).Ascii("] /Resources ").Int(doc.ResourcesId).Ascii(" 0 R /Contents ")
            .Int(doc.ContentIds[p]).Ascii(" 0 R");
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
    private void WriteLinkAnnotation(WriteState doc, in (int Id, Placed Link, float Origin, float PageTop, int Page) annotation)
    {
        var (id, placed, origin, linkPageTop, linkPage) = annotation;
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

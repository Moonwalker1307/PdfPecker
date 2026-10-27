using System.Globalization;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Pdf;

/// <summary>
/// The resources the pages share: the fonts and images collected before the pages are written, and the patterns,
/// transparency groups and soft masks that writing them adds, all listed in one resources dictionary.
/// </summary>
internal sealed partial class PdfDocumentWriter
{
    /// <summary>Collects the fonts, images and conic gradients the pages draw with, and the glyphs of the page numbers.</summary>
    private void CollectResources(WriteState doc)
    {
        Action<Placed> collect = p => CollectResources(doc, p);
        foreach (var page in doc.Pages) ForEachLeaf(page, collect);
        foreach (var page in doc.Bands) ForEachLeaf(page, collect);
        foreach (var page in doc.Canvas) ForEachLeaf(page, collect);
        doc.MarginFont = MarginFont ?? StandardFont.Get(FontFamily.Helvetica, false, false);
        if (doc.HasMargins)
        {
            // Page numbers are only known per page: collect every digit.
            foreach (var template in new[] { doc.HeaderTemplate, doc.FooterTemplate })
                if (template != null) CollectFonts(new TextOp(0, 0, 0, doc.MarginFont, MarginTextSize, MarginTextColor, template + "0123456789", 0, 0), doc.UsedFonts);
        }
    }

    private void CollectResources(WriteState doc, Placed p)
    {
        if (p.Op is BackgroundOp { Gradient: { Conic: true, FunctionId: 0 } conic })
        {
            conic.FunctionId = -1; // numbered with the other objects
            _conics.Add(conic);
        }
        if (p.Op is TextOp t) CollectFonts(t, doc.UsedFonts);
        else if (p.Op is SvgOp sv) CollectSvgFonts(sv.Image, doc.UsedFonts);
        else if (p.Op is BackgroundOp { Svg: { } bgSvg }) CollectSvgFonts(bgSvg, doc.UsedFonts);
        else if (p.Op is ImageOp im) AddImage(doc, im.Image);
        else if (p.Op is BackgroundOp { Image: { } bgImage }) AddImage(doc, bgImage);
    }

    /// <summary>Gives an image the next /Im number, unless it has one.</summary>
    private static void AddImage(WriteState doc, PdfImage image)
    {
        if (doc.ImageIds.ContainsKey(image)) return;
        doc.ImageIds[image] = doc.Images.Count + 1;
        doc.Images.Add(image);
    }

    /// <summary>Writes the conic gradients' functions, which are streams and so objects of their own.</summary>
    private void WriteConicFunctions()
    {
        foreach (var conic in _conics)
        {
            WriteStream(conic.FunctionId, conic.ConicFunction(gray: false), _compress, Gradient.ConicFunctionDict(gray: false));
            if (conic.AlphaFunctionId != 0) WriteStream(conic.AlphaFunctionId, conic.ConicFunction(gray: true), _compress, Gradient.ConicFunctionDict(gray: true));
        }
    }

    /// <summary>Writes the tiling patterns of repeated backgrounds, collected while writing the pages.</summary>
    private void WriteTilingPatterns(WriteState doc)
    {
        foreach (var (n, tileContent, tileDict) in _tilings)
        {
            int id = doc.Next++;
            WriteStream(id, tileContent, _compress, tileDict);
            _patterns.Ascii(" /T").Int(n).Sp().Int(id).Ascii(" 0 R");
        }
    }

    /// <summary>
    /// Writes the transparency groups of translucent layers. They share the page resources; a tagged group's marked
    /// content gets its own key in the parent tree.
    /// </summary>
    private void WriteTransparencyGroups(WriteState doc)
    {
        var formIds = doc.FormIds = new int[_forms.Count];
        for (int i = 0; i < _forms.Count; i++)
        {
            formIds[i] = doc.Next++;
            string structParents = "";
            if (_formStreams.Count > i && _formStreams[i] is { Elements.Count: > 0 } fs)
            {
                fs.Key = doc.ParentKey++;
                fs.FormId = formIds[i];
                structParents = " /StructParents " + fs.Key.ToString(CultureInfo.InvariantCulture);
            }
            WriteStream(formIds[i], _forms[i], _compress, "/Type /XObject /Subtype /Form /BBox [-32767 -32767 32767 32767] "
                + (_formIsolated[i] ? "/Group << /S /Transparency /I true >> /Resources " : "/Group << /S /Transparency >> /Resources ")
                + doc.ResourcesId.ToString(CultureInfo.InvariantCulture) + " 0 R"
                + structParents);
        }
    }

    /// <summary>Writes the soft masks of gradient alphas and SVG masks, which the /s graphics states set.</summary>
    private void WriteSoftMasks(WriteState doc)
    {
        var maskIds = doc.MaskIds = new int[_masks.Count];
        for (int i = 0; i < _masks.Count; i++)
        {
            maskIds[i] = doc.Next++;
            WriteStream(maskIds[i], _masks[i].Content, _compress, _masks[i].Dict);
        }
    }

    /// <summary>
    /// Writes the resources dictionary every page, transparency group and pattern shares. It comes last: shading
    /// patterns, graphics states and the other resources it lists are collected while writing the pages.
    /// </summary>
    private void WriteSharedResources(WriteState doc)
    {
        Begin(doc.ResourcesId).Ascii("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI] /Font <<");
        WriteFontEntries(_out, doc);
        _out.Ascii(" >>");
        WriteGraphicsResources(doc);
        if (doc.GlyphResourcesId == 0) return;
        // The colour fonts' glyphs draw no text, and a font among its own resources sends some validators in circles.
        Begin(doc.GlyphResourcesId).Ascii("<< /ProcSet [/PDF /ImageB /ImageC /ImageI]");
        WriteGraphicsResources(doc);
    }

    // The rest of a resources dictionary after its fonts, and its end.
    private void WriteGraphicsResources(WriteState doc)
    {
        if (doc.Images.Count > 0 || doc.Watermark != null || _forms.Count > 0 || _rasters.Count > 0) WriteXObjectResources(doc);
        if (_fillAlphas.Count + _strokeAlphas.Count + _groupAlphas.Count + _blendStates.Count + _layerBlends.Count + _masks.Count > 0)
            WriteExtGStateResources(doc);
        if (_patterns.Length > 0) _out.Ascii(" /Pattern <<").Bytes(_patterns.Span).Ascii(" >>");
        _out.Ascii(" >>");
        End();
    }

    /// <summary>The /XObject entry: the images, the watermark, the transparency groups and the filter results.</summary>
    private void WriteXObjectResources(WriteState doc)
    {
        _out.Ascii(" /XObject <<");
        for (int i = 0; i < doc.Images.Count; i++) _out.Ascii(" /Im").Int(i + 1).Sp().Int(doc.Images[i].ObjectNumber).Ascii(" 0 R");
        if (doc.Watermark != null) _out.Ascii(" /Wm ").Int(doc.WatermarkId).Ascii(" 0 R");
        for (int i = 0; i < _forms.Count; i++) _out.Ascii(" /Fx").Int(i + 1).Sp().Int(doc.FormIds[i]).Ascii(" 0 R");
        for (int i = 0; i < _rasters.Count; i++) _out.Ascii(" /Xr").Int(i + 1).Sp().Int(_rasters[i].ObjectNumber).Ascii(" 0 R");
        _out.Ascii(" >>");
    }

    /// <summary>The /ExtGState entry: the constant alphas, blend modes and soft masks the content streams set.</summary>
    private void WriteExtGStateResources(WriteState doc)
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
                .Int(doc.MaskIds[i]).Ascii(" 0 R >> >>");
        _out.Ascii(" >>");
    }
}

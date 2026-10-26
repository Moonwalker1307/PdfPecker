using PdfPecker.Fonts;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>
/// Colour fonts: a font with COLR colour glyphs is written as Type 3 fonts of 256 glyphs each, as Chrome's print output
/// writes it. A colour glyph's procedure paints its drawing, a transparency group in the shared resources, so the text's
/// opacity applies to the glyph as a whole; the font's other glyphs are their outlines, filled with the text colour.
/// The text stays text: the codes map to Unicode, and the glyphs' widths are the font's.
/// </summary>
internal sealed partial class PdfDocumentWriter
{
    // The drawing of each glyph of a colour subset, by CID: null for a glyph without colour.
    private readonly Dictionary<GlyphSubset, List<SvgShape>?[]> _colorDrawings = new(ReferenceEqualityComparer.Instance);

    /// <summary>The number of Type 3 fonts a colour subset needs: one for every 256 glyphs.</summary>
    private static int ColorFontPages(GlyphSubset subset) => (subset.Glyphs.Count - 1) / 256 + 1;

    /// <summary>
    /// Makes the drawings of the colour glyphs the document uses, before the objects are numbered: their sweep gradients
    /// are conic gradients, whose functions are objects of their own.
    /// </summary>
    private void PrepareColorGlyphs()
    {
        foreach (var subset in _subsetList)
        {
            if (subset.Color is not { } colors) continue;
            var drawings = new List<SvgShape>?[subset.Glyphs.Count];
            for (int cid = 0; cid < drawings.Length; cid++)
            {
                int gid = subset.Glyphs[cid];
                if (!colors.Has(gid)) continue;
                drawings[cid] = colors.Paint(gid) is { } paint ? ColorGlyphDrawing.Draw(subset.Program, paint, subset.Foreground) : new();
                RegisterConics(drawings[cid]!);
            }
            _colorDrawings[subset] = drawings;
        }
    }

    private void RegisterConics(List<SvgShape> shapes)
    {
        foreach (var shape in shapes)
        {
            if (shape.FillGradient is { Conic: true, FunctionId: 0 } conic)
            {
                conic.FunctionId = -1; // numbered with the other objects
                _conics.Add(conic);
            }
            if (shape.Children != null) RegisterConics(shape.Children);
        }
    }

    /// <summary>
    /// Writes a colour subset from <paramref name="id"/> on: for every 256 glyphs a Type 3 font, its descriptor and its
    /// ToUnicode CMap, then a glyph procedure for each glyph.
    /// </summary>
    private void WriteColorFont(GlyphSubset subset, int id, WriteState doc)
    {
        var program = subset.Program;
        var drawings = _colorDrawings[subset];
        var used = subset.Used().ToList();
        int pages = ColorFontPages(subset);
        int firstProc = id + pages * 3;
        // Glyph space is a thousandth of an em, as for the other fonts, so the widths are the same numbers.
        float scale = 1000f / program.UnitsPerEm;
        string baseFont = subset.Tag() + "+" + program.PostScriptName;
        for (int page = 0; page < pages; page++)
        {
            int first = page * 256, last = Math.Min(used.Count, first + 256) - 1;
            int fontId = id + page * 3;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int cid = first; cid <= last; cid++)
            {
                var (x0, y0, x1, y1) = WriteGlyphProcedure(firstProc + cid, program, subset.Glyphs[cid], drawings[cid], scale);
                if (!(x1 > x0 && y1 > y0)) continue;
                minX = Math.Min(minX, x0); minY = Math.Min(minY, y0);
                maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
            }
            if (!(maxX > minX && maxY > minY)) minX = minY = maxX = maxY = 0;

            var bbox = new ByteBuffer(64);
            bbox.Ascii("[").Int((int)Math.Floor(minX)).Sp().Int((int)Math.Floor(minY)).Sp().Int((int)Math.Ceiling(maxX)).Sp()
                .Int((int)Math.Ceiling(maxY)).Ascii("]");
            Begin(fontId).Ascii("<< /Type /Font /Subtype /Type3 /FontBBox ").Bytes(bbox.Span)
                .Ascii(" /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs <<");
            for (int cid = first; cid <= last; cid++) _out.Ascii(" /g").Int(cid - first).Sp().Int(firstProc + cid).Ascii(" 0 R");
            _out.Ascii(" >> /Encoding << /Type /Encoding /Differences [0");
            for (int cid = first; cid <= last; cid++) _out.Ascii(" /g").Int(cid - first);
            _out.Ascii("] >> /FirstChar 0 /LastChar ").Int(last - first).Ascii(" /Widths [");
            for (int cid = first; cid <= last; cid++)
            {
                if (cid > first) _out.Sp();
                _out.Int(program.Advance1000(subset.Glyphs[cid]));
            }
            _out.Ascii("] /FontDescriptor ").Int(fontId + 1).Ascii(" 0 R /ToUnicode ").Int(fontId + 2).Ascii(" 0 R /Resources ")
                .Int(doc.GlyphResourcesId).Ascii(" 0 R >>");
            End();

            // Tagged PDF needs a descriptor for a Type 3 font too; it has no font program.
            Begin(fontId + 1).Ascii("<< /Type /FontDescriptor /FontName /").Ascii(baseFont);
            if (page > 0) _out.Ascii("_").Int(page);
            _out.Ascii(" /Flags 4 /FontBBox ").Bytes(bbox.Span).Ascii(" /ItalicAngle 0 /Ascent ").Int((int)Math.Round(program.Ascender * scale))
                .Ascii(" /Descent ").Int((int)Math.Round(-Math.Abs(program.Descender) * scale)).Ascii(" /CapHeight ")
                .Int((int)Math.Round(program.CapHeight * scale)).Ascii(" /StemV 0 >>");
            End();

            var codes = new List<(int Code, int CodePoint, string? Text)>(last - first + 1);
            for (int cid = first; cid <= last; cid++) codes.Add((cid - first, used[cid].CodePoint, used[cid].Text));
            WriteToUnicode(fontId + 2, codes, oneByte: true);
        }
    }

    /// <summary>
    /// Writes a glyph's procedure: a colour glyph paints its drawing (d0), any other glyph is its outline, filled with
    /// the text colour (d1). Returns the glyph's bounds in glyph space.
    /// </summary>
    private (float, float, float, float) WriteGlyphProcedure(int id, FontProgram program, int gid, List<SvgShape>? drawing, float scale)
    {
        var b = new ByteBuffer(64);
        int width = program.Advance1000(gid);
        if (drawing != null)
        {
            b.Int(width).Ascii(" 0 d0\n");
            var (minX, minY, maxX, maxY) = ColorGlyphDrawing.PaintedBounds(drawing);
            if (drawing.Count > 0 && maxX > minX && maxY > minY)
                b.Ascii("q ").Num6(scale).Ascii(" 0 0 ").Num6(scale).Ascii(" 0 0 cm /Fx").Int(AddColorGlyphForm(drawing)).Ascii(" Do Q\n");
            WriteStream(id, b.Span, _compress, extraDict: null);
            return (minX * scale, minY * scale, maxX * scale, maxY * scale);
        }
        if (ColorGlyphDrawing.Outline(program, gid) is not var (verbs, points))
        {
            b.Int(width).Ascii(" 0 0 0 0 0 d1\n");
            WriteStream(id, b.Span, _compress, extraDict: null);
            return (0, 0, 0, 0);
        }
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i + 1 < points.Length; i += 2)
        {
            x0 = Math.Min(x0, points[i] * scale); x1 = Math.Max(x1, points[i] * scale);
            y0 = Math.Min(y0, points[i + 1] * scale); y1 = Math.Max(y1, points[i + 1] * scale);
        }
        b.Int(width).Ascii(" 0 ").Int((int)Math.Floor(x0)).Sp().Int((int)Math.Floor(y0)).Sp().Int((int)Math.Ceiling(x1)).Sp()
            .Int((int)Math.Ceiling(y1)).Ascii(" d1\n");
        WritePath(b, new SvgShape { Verbs = verbs, Points = points.Select(p => p * scale).ToArray() });
        b.Ascii("f\n");
        WriteStream(id, b.Span, _compress, extraDict: null);
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// Adds a colour glyph's drawing as a transparency group in font units, a stream of its own that starts from an
    /// unknown graphics state, and returns its /Fx number.
    /// </summary>
    private int AddColorGlyphForm(List<SvgShape> drawing)
    {
        var content = new ByteBuffer(1024);
        var saved = SaveState();
        var pageMc = _mc;
        var open = _mcOpen;
        _mc = null;
        _mcOpen = null;
        (_fill, _stroke, _lineWidth, _fillAlpha, _strokeAlpha) = (1, 1, -1, -1, -1);
        _anyAlpha = true;
        var streamSpace = StartStreamSpace();
        WriteSvgShapes(content, drawing, Matrix.Identity);
        _streamSpace = streamSpace;
        _mc = pageMc;
        _mcOpen = open;
        RestoreState(saved);
        _forms.Add(content.Span.ToArray());
        _formIsolated.Add(false);
        _formStreams.Add(null);
        return _forms.Count;
    }
}

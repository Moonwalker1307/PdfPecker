using System.IO.Compression;
using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>Text: fonts, their embedded subsets, glyph runs and strings.</summary>
internal sealed partial class PdfDocumentWriter
{
    private void CollectSvgFonts(SvgImage svg, bool[] usedFonts) => CollectSvgFonts(svg.Shapes, usedFonts);

    /// <summary>
    /// The fonts of SVG text, also inside masks, pattern tiles, embedded drawings, translucent groups and filtered
    /// content. The walk has no depth limit of its own: it must reach every text run the writer draws, and the writer
    /// draws the whole shape tree, whose depth the SVG parser already bounds. A shallower limit here left the fonts of
    /// text in deeply nested groups unregistered, and writing that text then failed.
    /// </summary>
    private void CollectSvgFonts(List<SvgShape> shapes, bool[] usedFonts)
    {
        foreach (var shape in shapes)
        {
            if (shape.Kind is SvgShape.KindMask or SvgShape.KindImage or SvgShape.KindFilter && shape.Children != null) CollectSvgFonts(shape.Children, usedFonts);
            if (shape.FillPattern != null) CollectSvgFonts(shape.FillPattern.Shapes, usedFonts);
            if (shape.StrokePattern != null) CollectSvgFonts(shape.StrokePattern.Shapes, usedFonts);
            if (shape.Font == null || shape.Kind != SvgShape.KindText) continue;
            if (Leaf(shape.Font) is EmbeddedFont ef) AddGlyphs(ef, shape.Text!, 0, shape.Text!.Length, shape.Rtl, SvgTextColor(shape));
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
            if (Leaf(run.Font) is EmbeddedFont ef) AddGlyphs(ef, t.Text, run.Start, run.Length, t.Rtl, t.Color);
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
        if (!(t.StrokeWidth > 0))
        {
            WriteTextRuns(b, t, x, y, FillAndStroke);
            return;
        }
        // Chrome bevels the sharp corners of an outline beyond a miter limit of 4 (PDF's default is 10), which keeps
        // acute glyph corners from growing spikes; the default is put back for the lines drawn after the text.
        b.Ascii("4 M\n");
        // paint-order: stroke draws the outline first and the fill over it.
        if (t.StrokeFirst)
        {
            WriteTextRuns(b, t, x, y, StrokeOnly);
            WriteTextRuns(b, t, x, y, FillOnly);
        }
        else WriteTextRuns(b, t, x, y, FillAndStroke);
        b.Ascii("10 M\n");
    }

    private const int FillAndStroke = 0, StrokeOnly = 1, FillOnly = 2;

    /// <summary>Sets the text rendering mode: an outline (-webkit-text-stroke) in <paramref name="pass"/>, else fill only, or fill and stroke for synthetic bold.</summary>
    private void SetTextPaint(ByteBuffer b, TextOp t, int pass, float syntheticBoldWidth)
    {
        float width = 0;
        if (t.StrokeWidth > 0 && pass != FillOnly)
        {
            width = t.StrokeWidth;
            SetRenderMode(b, pass == StrokeOnly ? 1 : 2);
            SetStroke(b, t.StrokeColor);
        }
        else if (syntheticBoldWidth > 0)
        {
            width = syntheticBoldWidth;
            SetRenderMode(b, 2);
            SetStroke(b, t.Color);
        }
        else SetRenderMode(b, 0);
        if (width > 0 && _lineWidth != width) { _lineWidth = width; b.Num(width).Ascii(" w\n"); }
    }

    private void WriteTextRuns(ByteBuffer b, TextOp t, float x, float y, int pass)
    {
        if (t.Font is StandardFont sf && !EmbedStandard)
        {
            WriteStandardRun(b, sf, t, 0, t.Text.Length, x, y, pass);
            return;
        }
        _runs.Clear();
        t.Font.Split(t.Text, _runs);
        for (int r = 0; r < _runs.Count; r++)
        {
            var run = _runs[t.Rtl ? _runs.Count - 1 - r : r];
            if (Leaf(run.Font) is EmbeddedFont ef) WriteEmbeddedRun(b, ef, t, run.Start, run.Length, x, y, pass);
            else WriteStandardRun(b, (StandardFont)run.Font, t, run.Start, run.Length, x, y, pass);
            x += RunAdvance(run.Font, t, run.Start, run.Length);
        }
    }

    private readonly List<ShapedGlyph> _glyphs = new();

    private void AddGlyphs(EmbeddedFont font, string text, int start, int length, bool rtl, uint color)
    {
        var subset = Subset(font.Program, color);
        font.Shape(text, start, length, _glyphs, rtl);
        for (int k = 0; k < _glyphs.Count; k++) subset.Cid(_glyphs, k, text);
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
        // A colour font's glyphs are one-byte codes of Type 3 fonts, and a glyph of another of them ends the array.
        bool array = font.HasKerning || skipNotdef || subset.Color != null;
        if (subset.Color != null) _textPage = 0;
        int prev = -1;
        b.Ascii(array ? "[<" : "<");
        for (int i = 0; i < _glyphs.Count; i++)
        {
            var g = _glyphs[i];
            if (font.HasKerning && prev >= 0 && (rtl ? font.Program.Kern1000(g.Gid, prev) : font.Program.Kern1000(prev, g.Gid)) is int k && k != 0)
                b.Ascii(">").Int(-k).Ascii("<");
            prev = g.Gid;
            int cid = subset.Cid(_glyphs, i, text);
            if (cid == 0 && skipNotdef) b.Ascii(">").Num(notdefAdjust).Ascii("<");
            else WriteCode(b, font, subset, cid, size);
        }
        b.Ascii(array ? ">] TJ" : "> Tj");
    }

    /// <summary>
    /// The subset of a font's glyphs the document uses, made on first use. A font whose colour glyphs use the text
    /// colour has a subset for each colour, since its Type 3 glyphs draw that colour.
    /// </summary>
    private GlyphSubset Subset(FontProgram program, uint color)
    {
        var colors = program.ColorGlyphs;
        if (colors is { UsesForeground: true })
        {
            var key = (program, color & 0xFFFFFF);
            if (!_colorSubsets.TryGetValue(key, out var colored))
            {
                _colorSubsets[key] = colored = new GlyphSubset(program, colors, color);
                _subsetList.Add(colored);
            }
            return colored;
        }
        if (!_subsets.TryGetValue(program, out var s))
        {
            _subsets[program] = s = new GlyphSubset(program, colors);
            _subsetList.Add(s);
        }
        return s;
    }

    /// <summary>The colour SVG text is drawn in, which the colour glyphs of its font use as the text colour.</summary>
    private static uint SvgTextColor(SvgShape shape) => shape.Fill ?? CssColor.Black;

    private void SetFont(ByteBuffer b, TextFont font, string resource, int index, float size)
    {
        if (ReferenceEquals(_textFont, font) && _textSize == size) return;
        _textFont = font;
        _textSize = size;
        b.Ascii(resource).Int(index).Sp().Num(size).Ascii(" Tf\n");
    }

    /// <summary>Selects the Type 3 font that holds page <paramref name="page"/> (256 glyphs) of a colour font's subset.</summary>
    private void SetColorFont(ByteBuffer b, EmbeddedFont font, GlyphSubset subset, int page, float size)
    {
        if (ReferenceEquals(_textFont, font) && _textSubset == subset && _textPage == page && _textSize == size) return;
        _textFont = font;
        _textSubset = subset;
        _textPage = page;
        _textSize = size;
        WriteFontName(b, subset, page);
        b.Sp().Num(size).Ascii(" Tf\n");
    }

    /// <summary>The resource name of an embedded font: /E1, /E2, ..., and /E1_1, /E1_2, ... for the further Type 3 fonts of a colour font.</summary>
    private void WriteFontName(ByteBuffer b, GlyphSubset subset, int page)
    {
        b.Ascii("/E").Int(_subsetList.IndexOf(subset) + 1);
        if (page > 0) b.Ascii("_").Int(page);
    }

    /// <summary>
    /// Writes the code of a glyph in a hex string inside a TJ array: four hex digits of its CID, or for a colour font one
    /// byte of the Type 3 font its CID is on, ending the array and changing font when that is another one.
    /// </summary>
    private void WriteCode(ByteBuffer b, EmbeddedFont font, GlyphSubset subset, int cid, float size)
    {
        if (subset.Color == null)
        {
            Hex4(b, cid);
            return;
        }
        int page = cid >> 8;
        if (page != _textPage)
        {
            b.Ascii(">] TJ ");
            SetColorFont(b, font, subset, page, size);
            b.Ascii("[<");
        }
        b.Byte(HexDigits[(cid >> 4) & 15]).Byte(HexDigits[cid & 15]);
    }

    private void SetRenderMode(ByteBuffer b, int mode)
    {
        if (_invisibleText) mode = 3;
        if (_tr == mode) return;
        _tr = mode;
        b.Int(mode).Ascii(" Tr\n");
    }

    private void WriteStandardRun(ByteBuffer b, StandardFont font, TextOp t, int start, int length, float x, float y, int pass)
    {
        // Font objects are keyed by the leaf font in SetFont, so an embedded font of the same size is switched back.
        SetFont(b, font, "/F", font.Index + 1, t.Size);
        SetTextPaint(b, t, pass, 0);
        b.Ascii(t.UpsideDown ? "1 0 0 -1 " : "1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");
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

    private void WriteEmbeddedRun(ByteBuffer b, EmbeddedFont font, TextOp t, int start, int length, float x, float y, int pass)
    {
        var subset = Subset(font.Program, t.Color);
        bool color = subset.Color != null;
        if (color) SetColorFont(b, font, subset, _textSubset == subset && ReferenceEquals(_textFont, font) ? _textPage : 0, t.Size);
        else SetFont(b, font, "/E", _subsetList.IndexOf(subset) + 1, t.Size);
        float boldWidth = 0;
        if (font.SyntheticBold)
        {
            // Stroke the outlines in the fill colour, as browsers widen glyphs for a missing bold face.
            float k = Math.Clamp((t.Size - 9f) / 27f, 0f, 1f);
            boldWidth = t.Size * (1f / 24f + (1f / 32f - 1f / 24f) * k);
        }
        SetTextPaint(b, t, pass, boldWidth);
        b.Ascii(font.SyntheticItalic ? t.UpsideDown ? "1 0 0.2 -1 " : "1 0 0.2 1 " : t.UpsideDown ? "1 0 0 -1 " : "1 0 0 1 ").Num(x).Sp().Num(y).Ascii(" Tm ");

        // Word spacing (Tw) only applies to the single-byte code 32, so justified text moves spaces explicitly.
        // PDF/A forbids showing .notdef: characters no font has become blank space of the same width.
        string text = t.Text;
        bool skipNotdef = NoNotdef && t.Size > 0;
        // Kerning moves the glyph after a kerned pair, as the layout measured it.
        bool kern = font.HasKerning && t.Size > 0;
        bool adjust = (t.WordSpacing != 0 || skipNotdef || kern) && t.Size > 0 || color;
        float spaceAdjust = adjust && t.Size > 0 ? -t.WordSpacing * 1000f / t.Size : 0;
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
            int cid = subset.Cid(_glyphs, k, text);
            if (cid == 0 && skipNotdef) b.Ascii(">").Num(notdefAdjust).Ascii("<");
            else WriteCode(b, font, subset, cid, t.Size);
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
            int cid = subset.Cid(_glyphs, k, text);
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
            WriteCode(b, font, subset, cid, size);
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

    /// <summary>Writes the fonts the document uses: the standard fonts it does not embed, then the embedded subsets.</summary>
    private void WriteFonts(WriteState doc)
    {
        for (int i = 0; i < doc.UsedFonts.Length; i++)
        {
            if (!doc.UsedFonts[i]) continue;
            Begin(doc.FontIds[i]).Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /").Ascii(StandardFont.All[i].BaseFont)
                .Ascii(" /Encoding /WinAnsiEncoding >>");
            End();
        }
        for (int i = 0; i < _subsetList.Count; i++)
        {
            if (_subsetList[i].Color != null) WriteColorFont(_subsetList[i], _subsetIds[i], doc);
            else WriteEmbeddedFont(_subsetList[i], _subsetIds[i]);
        }
    }

    /// <summary>The entries of a /Font resource dictionary: /F1, /F2, ... for the standard fonts, /E1, /E2, ... for the subsets.</summary>
    private void WriteFontEntries(ByteBuffer b, WriteState doc)
    {
        for (int i = 0; i < doc.UsedFonts.Length; i++)
            if (doc.UsedFonts[i]) b.Ascii(" /F").Int(i + 1).Sp().Int(doc.FontIds[i]).Ascii(" 0 R");
        for (int i = 0; i < _subsetList.Count; i++)
        {
            int pages = _subsetList[i].Color == null ? 1 : ColorFontPages(_subsetList[i]);
            for (int page = 0; page < pages; page++)
            {
                WriteFontName(b.Sp(), _subsetList[i], page);
                b.Sp().Int(_subsetIds[i] + page * 3).Ascii(" 0 R");
            }
        }
    }

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

        var file = EmbeddedFontFiles.Get(subset, _level);
        WriteStreamObject(id + 3, file.Body, file.Deflated, font.IsCff ? "/Subtype /OpenType" : "/Length1 " + file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        WriteToUnicode(id + 4, used.Select(u => (u.Cid, u.CodePoint, u.Text)).ToList(), oneByte: false);
    }

    /// <summary>
    /// Writes a ToUnicode CMap for the codes of a font: two-byte CIDs, or the one-byte codes of a Type 3 font. Every drawn
    /// glyph but .notdef gets an entry, as PDF/A level U and PDF/UA require; the extra glyphs of a shaped cluster map to
    /// an empty string, so extracted text holds the cluster's characters once.
    /// </summary>
    private void WriteToUnicode(int id, List<(int Code, int CodePoint, string? Text)> used, bool oneByte)
    {
        var cmap = new ByteBuffer(256 + used.Count * 16);
        cmap.Ascii("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Ascii("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n")
            .Ascii(oneByte ? "<00> <FF>" : "<0000> <FFFF>").Ascii("\nendcodespacerange\n");
        var mapped = used.Where(u => u.CodePoint > 0 || u.Text != null).ToList();
        for (int i = 0; i < mapped.Count; i += 100)
        {
            int n = Math.Min(100, mapped.Count - i);
            cmap.Int(n).Ascii(" beginbfchar\n");
            for (int k = i; k < i + n; k++)
            {
                cmap.Byte((byte)'<');
                int code = mapped[k].Code;
                if (oneByte) cmap.Byte(HexDigits[(code >> 4) & 15]).Byte(HexDigits[code & 15]);
                else Hex4(cmap, code);
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
        WriteStream(id, cmap.Span, _compress, extraDict: null);
    }

    private const float MarginTextSize = 9f;
    private const uint MarginTextColor = 0xFF666666;

    private void WriteMarginText(ByteBuffer b, TextFont font, string template, int page, int pages, bool top)
    {
        string text = template.Replace("{page}", page.ToString(System.Globalization.CultureInfo.InvariantCulture))
                              .Replace("{pages}", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        const float size = MarginTextSize;
        float w = font.Measure(text, size);
        float x = (_page.Width - w) / 2;
        float marginH = top ? _page.MarginTop - _page.BoxTop : _page.MarginBottom - _page.BoxBottom;
        float y = top ? _page.Height - marginH / 2 - size / 3 : marginH / 2 - size / 3;
        b.Ascii("BT\n");
        if (_tw != 0) { _tw = 0; b.Ascii("0 Tw\n"); }
        if (_tc != 0) { _tc = 0; b.Ascii("0 Tc\n"); }
        SetFill(b, MarginTextColor);
        WriteTextLine(b, new TextOp(0, 0, 0, font, size, MarginTextColor, text, 0, 0), x, y);
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
                for (int k = 0; k < n; k++) raw.Byte((byte)'%').Ascii(utf8[k].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
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
            PdfSyntax.Hex(b, _crypt.Encrypt(raw), upper: true);
            return;
        }
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            PdfSyntax.Hex(b, raw, upper: true);
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

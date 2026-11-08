using System.Globalization;

namespace PdfPecker.Fonts;

/// <summary>
/// A TrueType/OpenType font that is embedded in the PDF. Bold and italic are synthesized (stroked outlines, slanted
/// text) when the family has no face of the requested weight or style.
/// </summary>
internal sealed class EmbeddedFont : TextFont
{
    public readonly FontProgram Program;
    public readonly bool SyntheticBold, SyntheticItalic;
    private readonly short[] _widths;
    private readonly UnicodeRange? _range;
    private readonly FaceMetrics _faceMetrics;

    /// <summary>
    /// The face's size-adjust: the glyphs are drawn at the font size times this, and every width and metric of the font
    /// is scaled with them, while em-based lengths are not. 1 for a font without it.
    /// </summary>
    public readonly float SizeAdjust;
    private readonly Ligatures? _ligatures; // null when the font has none or they are turned off
    private EmbeddedFont? _withoutLigatures, _withoutKerning, _withoutContextual;
    private bool _noKerning;
    private FeaturesOff _off; // the optional features CSS turned off, which the shaper leaves out
    // Whether all text goes through the shaper because the font substitutes in context (calt, clig or rclt) and those
    // features are on, as browsers apply them to all horizontal text. Decided when the font is made, never per run.
    private bool _shapesInContext;
    private ShapeCache? _shapeCache; // the text this font has shaped, made on first use (see CachesShapes)
    // Whether the font lives as long as the process (a bundled font) rather than one conversion.
    private bool _shared;

    // metrics: line metrics to use instead of the font's own, for a bundled font standing in for a standard font.
    // faceMetrics: the size-adjust and metric overrides of the @font-face rule the font comes from.
    public EmbeddedFont(FontProgram program, bool syntheticBold, bool syntheticItalic, UnicodeRange? range = null, TextFont? metrics = null,
        FaceMetrics? faceMetrics = null)
    {
        Program = program;
        SyntheticBold = syntheticBold;
        SyntheticItalic = syntheticItalic;
        _range = range;
        _widths = program.CharWidths;
        _ligatures = program.Ligatures;
        float upm = program.UnitsPerEm;
        Ascent = program.LineAscender / upm;
        Descent = -Math.Abs(program.LineDescender) / upm;
        LineGap = Math.Max(0, program.LineGapUnits) / upm;
        if (Ascent - Descent <= 0.01f) { Ascent = 0.8f; Descent = -0.2f; }
        if (metrics != null)
        {
            Ascent = metrics.Ascent;
            Descent = metrics.Descent;
            LineGap = metrics.LineGap;
        }
        _faceMetrics = faceMetrics ?? FaceMetrics.None;
        SizeAdjust = _faceMetrics.SizeAdjust;
        if (!_faceMetrics.IsNone)
        {
            // The overrides are fractions of the font size, and size-adjust scales them with the rest of the font, as in
            // Chrome and CSS Fonts level 5.
            float sa = SizeAdjust;
            Ascent = (float.IsNaN(_faceMetrics.Ascent) ? Ascent : _faceMetrics.Ascent) * sa;
            Descent = (float.IsNaN(_faceMetrics.Descent) ? Descent : -_faceMetrics.Descent) * sa;
            LineGap = (float.IsNaN(_faceMetrics.LineGap) ? LineGap : _faceMetrics.LineGap) * sa;
        }
        _shapesInContext = SubstitutesInContext(program, FeaturesOff.None);
    }

    private static bool SubstitutesInContext(FontProgram program, FeaturesOff off)
    {
        var features = program.ContextualFeatures;
        return (features & ContextualFeatures.Rclt) != 0
            || (features & ContextualFeatures.Calt) != 0 && (off & FeaturesOff.Contextual) == 0
            || (features & ContextualFeatures.Clig) != 0 && (off & FeaturesOff.Ligatures) == 0;
    }

    /// <summary>
    /// A width in thousandths of an em of the program, scaled by size-adjust to thousandths of the font size. It is
    /// worked out in doubles and clamped, since a long run at a large size-adjust passes the range of an int, and the
    /// conversion of such a value gives int.MinValue on .NET 8, a negative width.
    /// </summary>
    private int Scaled(int units)
        => SizeAdjust == 1 ? units : (int)Math.Clamp(Math.Round((double)units * SizeAdjust), int.MinValue, int.MaxValue);

    /// <summary>True when the font has a glyph for the code point (and its unicode-range, if any, includes it).</summary>
    public bool Covers(int codePoint)
        => (_range == null || _range.Contains(codePoint)) && Program.GlyphId(codePoint) != 0;

    /// <summary>The advance of a code point in thousandths of the program's em, before size-adjust.</summary>
    public int CharUnits(int codePoint)
        => IsHidden(codePoint) ? 0 : (uint)codePoint < 0x10000 ? _widths[codePoint] : Program.Advance1000(Program.GlyphId(codePoint));

    public override bool HasKerning => !_noKerning && Program.Kerning != null;

    public override int KernUnits(int left, int right) => _noKerning ? 0 : Scaled(Program.Kern1000(Program.GlyphId(left), Program.GlyphId(right)));

    private EmbeddedFont(EmbeddedFont font, FeaturesOff off, bool kerning)
        : this(font.Program, font.SyntheticBold, font.SyntheticItalic, font._range, faceMetrics: font._faceMetrics)
    {
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
        _off = off;
        if ((off & FeaturesOff.Ligatures) != 0) _ligatures = null;
        _shapesInContext = SubstitutesInContext(Program, off);
        _noKerning = !kerning;
        _shared = font._shared;
    }

    // A font with nothing to turn off is its own variant without ligatures: it has neither ligatures drawn without the
    // shaper nor liga or clig lookups that the shaper would leave out.
    public override TextFont WithoutLigatures()
        => (_off & FeaturesOff.Ligatures) != 0 || _ligatures == null && (Program.ContextualFeatures & (ContextualFeatures.Liga | ContextualFeatures.Clig)) == 0
            ? this
            : _withoutLigatures ??= new EmbeddedFont(this, _off | FeaturesOff.Ligatures, kerning: !_noKerning);

    public override TextFont WithoutContextual()
        => (_off & FeaturesOff.Contextual) != 0 || (Program.ContextualFeatures & ContextualFeatures.Calt) == 0
            ? this
            : _withoutContextual ??= new EmbeddedFont(this, _off | FeaturesOff.Contextual, kerning: !_noKerning);

    public override TextFont WithoutKerning()
        => _noKerning || Program.Kerning == null ? this : _withoutKerning ??= new EmbeddedFont(this, _off, kerning: false);

    public override float Metric(FontMetric metric)
    {
        float upm = Program.UnitsPerEm / SizeAdjust;
        return metric switch
        {
            FontMetric.XHeight => Program.XHeight / upm,
            FontMetric.CapHeight => Program.TextCapHeight / upm,
            FontMetric.ZeroWidth => Program.GlyphId('0') is > 0 and var zero ? Program.Advance(zero) / upm : 0,
            FontMetric.IdeographWidth => Program.GlyphId(0x6C34) is > 0 and var water ? Program.Advance(water) / upm : 0,
            FontMetric.UnderlinePosition => -Program.UnderlinePosition / upm,
            FontMetric.EmDescent => Program.EmDescent * SizeAdjust,
            _ => 0,
        };
    }

    public override bool Supports(string features) => !ReferenceEquals(Program.WithFeatures(features), Program);

    private System.Collections.Concurrent.ConcurrentDictionary<string, TextFont>? _variants;

    public override TextFont WithFeatures(string features) => WithFeatures(features, null);

    /// <summary>
    /// This font with OpenType features, drawn with <paramref name="document"/>'s variant of the program when a document
    /// is given, so that the document has one variant of the program throughout. A font of one conversion keeps its
    /// variants, as many as the document has settings; a font the process keeps (see <see cref="Shared"/>) keeps none,
    /// or every new setting a server saw would stay, and the document keeps them instead.
    /// </summary>
    public TextFont WithFeatures(string features, DocumentFontVariants? document)
    {
        var program = document != null ? document.WithFeatures(Program, features) : Program.WithFeatures(features);
        if (ReferenceEquals(program, Program)) return this;
        if (_shared) return new EmbeddedFont(this, program);
        var variants = LazyInitializer.EnsureInitialized(ref _variants, () => new());
        return variants.GetOrAdd(features, _ => new EmbeddedFont(this, program));
    }

    private EmbeddedFont(EmbeddedFont font, FontProgram program)
        : this(program, font.SyntheticBold, font.SyntheticItalic, font._range, faceMetrics: font._faceMetrics)
    {
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
        if (font._ligatures == null) _ligatures = null;
        _off = font._off;
        _shapesInContext = SubstitutesInContext(program, _off);
        _noKerning = font._noKerning;
        _shared = font._shared;
    }

    /// <summary>
    /// True when the text goes through the OpenType shaper: it has characters of a complex script, or the font has
    /// features on that substitute in context, such as contextual alternates or fractions, and the text is not made of
    /// plain words only (see <see cref="PlainWords"/>).
    /// </summary>
    public bool UsesShaper(string s, int start, int length)
        => Program.ShapedFeatures != null || ComplexShaper.Handles(s, start, length) || _shapesInContext && !(CachesShapes && PlainWords(s, start, length));

    /// <summary>
    /// Marks a font that the process keeps for every conversion, such as a bundled font. It keeps no shaped text, so
    /// that the memory a stream of documents leaves behind stays as it was; shaped text is kept only by the fonts of
    /// one conversion whose text all goes through the shaper.
    /// </summary>
    internal bool Shared { init => _shared = value; }

    // Whether the font keeps the text it shapes: all its text goes through the shaper, and it lives for one conversion.
    private bool CachesShapes => _shapesInContext && !_shared;

    /// <summary>The glyphs the font keeps of the text it has shaped, as its cache counts them; for tests.</summary>
    internal int CachedGlyphs => Volatile.Read(ref _shapeCache)?.Count ?? 0;

    /// <summary>
    /// Whether the text, in a font whose text goes through the shaper because it substitutes in context, is all plain
    /// words and spaces: words that the shaper draws with the glyphs the font draws them with otherwise, one per
    /// character apart from standard ligatures and unmoved, and none of which a rule matching a space may change. In
    /// any text such words get those glyphs (see <see cref="OpenTypeLayout.WordContext"/>), so they are measured and
    /// drawn as in a font without such features, which is much faster; their kerning and the rounding of their widths
    /// are the font's own rather than the shaper's.
    /// </summary>
    private bool PlainWords(string s, int start, int length)
    {
        if (Program.WordContext is not { } context) return false;
        var cache = Shapes;
        for (int i = start, end = start + length; i < end;)
        {
            while (i < end && s[i] == ' ') i++;
            int wordEnd = i;
            while (wordEnd < end && s[wordEnd] != ' ') wordEnd++;
            if (wordEnd > i && !PlainWord(context, cache, s, i, wordEnd - i)) return false;
            i = wordEnd;
        }
        return true;
    }

    private bool PlainWord(OpenTypeLayout.WordContext context, ShapeCache cache, string s, int start, int length)
    {
        if (cache.TryGetPlain(s, start, length, out bool plain)) return plain;
        plain = IsPlainWord(context, s, start, length);
        cache.AddPlain(s, start, length, plain);
        return plain;
    }

    private bool IsPlainWord(OpenTypeLayout.WordContext context, string s, int start, int length)
    {
        var layout = Program.Layout!;
        for (int i = start, end = start + length; i < end; i++)
        {
            char c = s[i];
            if (char.IsSurrogate(c) || Text.UnicodeTables.IsDefaultIgnorable(c) || Text.UnicodeTables.Bidi(c) is Text.BidiClass.R or Text.BidiClass.AL)
                return false;
            int gid = Program.GlyphId(c);
            if (gid == 0 || context.MayChange(gid) || (layout.GlyphProps(gid) & OpenTypeLayout.Mark) != 0) return false;
        }
        var shaped = t_window ??= new();
        ComplexShaper.Shape(Program, s, start, length, false, shaped, _off);
        var plain = t_plain ??= new();
        PlainGlyphs(s, start, length, plain, rtl: false);
        if (shaped.Count != plain.Count) return false;
        for (int k = 0; k < shaped.Count; k++)
        {
            var (a, b) = (shaped[k], plain[k]);
            if (a.Gid != b.Gid || a.Start != b.Start || a.Length != b.Length || a.Dx != 0 || a.Dy != 0
                || context.MayChange(a.Gid) || (layout.GlyphProps(a.Gid) & OpenTypeLayout.Mark) != 0)
                return false;
        }
        return true;
    }

    [ThreadStatic] private static List<ShapedGlyph>? t_plain;

    public override bool ShapesInContext => _shapesInContext;

    /// <summary>True when the text may form a ligature, so it has to be shaped glyph by glyph.</summary>
    public bool MayLigate(string s, int start, int length)
    {
        var lig = _ligatures;
        if (lig == null || length < 2) return false;
        int prev = Program.GlyphId(s[start]);
        for (int i = start + 1, end = start + length; i < end; i++)
        {
            int g = Program.GlyphId(s[i]);
            if (lig.StartsLigature(prev, g)) return true;
            prev = g;
        }
        return false;
    }

    /// <summary>The glyphs that draw the text: one per character, apart from ligatures.</summary>
    public void Shape(string s, int start, int length, List<ShapedGlyph> glyphs) => Shape(s, start, length, glyphs, rtl: false);

    /// <summary>
    /// The glyphs that draw the text, in logical order. Returns true when complex shaping positioned them (their
    /// Advance, Dx and Dy apply instead of the font's advances and kerning). Right to left, mirrored characters such as
    /// brackets take their mirror's glyph.
    /// </summary>
    public bool Shape(string s, int start, int length, List<ShapedGlyph> glyphs, bool rtl)
    {
        if (UsesShaper(s, start, length))
        {
            ShapeComplex(s, start, length, rtl, glyphs);
            return true;
        }
        PlainGlyphs(s, start, length, glyphs, rtl);
        return false;
    }

    // The glyphs the font draws the text with outside the shaper: the characters' own, with standard ligatures.
    private void PlainGlyphs(string s, int start, int length, List<ShapedGlyph> glyphs, bool rtl)
    {
        glyphs.Clear();
        var lig = _ligatures;
        bool ligate = false, hidden = false;
        int prev = -1;
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = s[i], n = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); n = 2; }
            int gid = Program.GlyphId(cp);
            if (rtl && Text.UnicodeTables.Mirror(cp) is int m && m != cp && Program.GlyphId(m) is int mg && mg != 0) gid = mg;
            if (lig != null && !ligate && prev >= 0 && lig.StartsLigature(prev, gid)) ligate = true;
            hidden |= IsHidden(cp);
            glyphs.Add(new ShapedGlyph(gid, i, n));
            prev = gid;
            i += n - 1;
        }
        if (ligate) lig!.Apply(glyphs);
        if (hidden) RemoveHidden(s, glyphs);
    }

    // Default ignorable characters draw nothing, not .notdef nor the visible glyph some fonts give joiners and bidi
    // marks. They are taken out after the ligatures, so that a zero width non-joiner still keeps two letters apart.
    private static void RemoveHidden(string s, List<ShapedGlyph> glyphs)
    {
        int kept = 0;
        for (int k = 0; k < glyphs.Count; k++)
        {
            var g = glyphs[k];
            int cp = g.Length == 2 && char.IsHighSurrogate(s[g.Start]) ? char.ConvertToUtf32(s[g.Start], s[g.Start + 1]) : s[g.Start];
            if ((g.Length == 1 || cp >= 0x10000) && IsHidden(cp)) continue;
            glyphs[kept++] = g;
        }
        glyphs.RemoveRange(kept, glyphs.Count - kept);
    }

    [ThreadStatic] private static List<ShapedGlyph>? t_glyphs;

    public override int MeasureUnits(string s, int start, int length)
        => Scaled(MeasureProgramUnits(s, start, length));

    // The width in thousandths of the program's em, before size-adjust.
    private int MeasureProgramUnits(string s, int start, int length)
    {
        if (UsesShaper(s, start, length)) return MeasureComplex(s, start, length);
        if (MayLigate(s, start, length)) return MeasureShaped(s, start, length);
        var w = _widths;
        bool kern = HasKerning;
        int total = 0, prev = -1;
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(s[i], s[++i]);
            // Drawn as nothing, so the letters around it kern as a pair.
            if (IsHidden(cp)) continue;
            total += cp < 0x10000 ? w[cp] : CharUnits(cp);
            if (kern)
            {
                if (prev >= 0) total += Program.Kern1000(Program.GlyphId(prev), Program.GlyphId(cp));
                prev = cp;
            }
        }
        return total;
    }

    private int MeasureShaped(string s, int start, int length)
    {
        var glyphs = t_glyphs ??= new List<ShapedGlyph>();
        Shape(s, start, length, glyphs);
        bool kern = HasKerning;
        int total = 0, prev = -1;
        foreach (var g in glyphs)
        {
            total += g.Length == 1 ? _widths[s[g.Start]] : Program.Advance1000(g.Gid);
            if (kern && prev >= 0) total += Program.Kern1000(prev, g.Gid);
            prev = g.Gid;
        }
        return total;
    }

    private int MeasureComplex(string s, int start, int length)
    {
        bool rtl = ComplexShaper.GuessRtl(s, start, length);
        if (CachesShapes && Shapes.TryGet(s, start, length, rtl, null, out int width)) return width;
        var glyphs = t_glyphs ??= new List<ShapedGlyph>();
        ShapeComplex(s, start, length, rtl, glyphs);
        return ShapeCache.Width(glyphs);
    }

    private ShapeCache Shapes
    {
        get
        {
            if (Volatile.Read(ref _shapeCache) is { } cache) return cache;
            Interlocked.CompareExchange(ref _shapeCache, new ShapeCache(), null);
            return _shapeCache!;
        }
    }

    /// <summary>The OpenType shaper's glyphs for the text, kept for the next time the font shapes the same text.</summary>
    private void ShapeComplex(string s, int start, int length, bool rtl, List<ShapedGlyph> glyphs)
    {
        if (!CachesShapes)
        {
            ComplexShaper.Shape(Program, s, start, length, rtl, glyphs, _off);
            return;
        }
        var cache = Shapes;
        if (cache.TryGet(s, start, length, rtl, glyphs, out _)) return;
        if (rtl || !ShapeWords(s, start, length, glyphs)) ComplexShaper.Shape(Program, s, start, length, rtl, glyphs, _off);
        cache.Add(s, start, length, rtl, glyphs);
    }

    // The words of the text being shaped by ShapeWords, each with the spaces after it, and the glyphs of one shaping call.
    [ThreadStatic] private static List<(int Start, int End, int Glyphs, bool Free)>? t_words;
    [ThreadStatic] private static List<ShapedGlyph>? t_window;

    // Context around the words shaped together, in glyphs, at least: a mark at the start of a word sits on the space
    // before it.
    private const int MinWordContext = 2;

    /// <summary>
    /// Shapes text of a script that is not complex a word at a time, with the glyphs that shaping the whole text gives,
    /// so that a font whose text all goes through the shaper (contextual alternates) shapes each word of a document
    /// about once instead of every line it is measured or drawn in. False when the text cannot be shaped so.
    /// </summary>
    /// <remarks>
    /// The font's lookups reach across a space only as context (see <see cref="OpenTypeLayout.WordContext"/>), so a
    /// word none of whose glyphs a rule matching a space may change, and that has no marks, is shaped in the text as on
    /// its own, together with the spaces after it, whose kerning with the word is the word's. The other words are
    /// shaped together with the words around them as context, as many glyphs as such a rule looks at, and keep only
    /// their own glyphs; words of that kind with fewer glyphs between them are shaped together.
    /// </remarks>
    internal bool ShapeWords(string s, int start, int length, List<ShapedGlyph> output)
    {
        int end = start + length;
        if (Program.ShapedFeatures != null || s.AsSpan(start, length).IndexOf(' ') < 0 || Program.WordContext is not { } context) return false;
        foreach (char c in s.AsSpan(start, length))
            if (char.IsSurrogate(c) || Text.UnicodeTables.IsDefaultIgnorable(c)) return false;
        int script = ComplexShaper.SimpleScript(s, start, length);
        if (script < 0) return false;
        var cache = Shapes;
        var layout = Program.Layout!;
        var words = t_words ??= new();
        var window = t_window ??= new();
        words.Clear();
        for (int i = start; i < end;)
        {
            int next = i;
            while (next < end && s[next] != ' ') next++;
            while (next < end && s[next] == ' ') next++;
            bool free = true;
            for (int k = i; k < next && free; k++)
            {
                int gid = Program.GlyphId(s[k]);
                free = !context.MayChange(gid) && (layout.GlyphProps(gid) & OpenTypeLayout.Mark) == 0;
            }
            int glyphs = next - i;
            if (free)
            {
                window.Clear();
                if (cache.TryAppendWord(script, s, i, next - i, window, out bool cached, out int count))
                {
                    free = cached;
                    glyphs = Math.Min(glyphs, count);
                }
                else
                {
                    ComplexShaper.ShapeAs(Program, s, i, next - i, script, window, _off);
                    foreach (var g in window)
                        free &= !context.MayChange(g.Gid) && (layout.GlyphProps(g.Gid) & OpenTypeLayout.Mark) == 0;
                    cache.AddWord(script, s, i, next - i, window, 0, free);
                    glyphs = Math.Min(glyphs, window.Count);
                }
            }
            words.Add((i, next, glyphs, free));
            i = next;
        }

        output.Clear();
        int reach = Math.Max(context.Reach, MinWordContext);
        for (int w = 0; w < words.Count;)
        {
            var word = words[w];
            if (word.Free)
            {
                if (!cache.TryAppendWord(script, s, word.Start, word.End - word.Start, output, out _, out _))
                {
                    ComplexShaper.ShapeAs(Program, s, word.Start, word.End - word.Start, script, window, _off);
                    output.AddRange(window);
                }
                w++;
                continue;
            }
            // The words shaped together end where enough free words follow to hold all the context of the last.
            int last = w, after = w + 1, seen = 0;
            while (after < words.Count && seen < reach)
            {
                if (!words[after].Free)
                {
                    last = after++;
                    seen = 0;
                }
                else seen += words[after++].Glyphs;
            }
            // Before them, as many free words as a rule looks back over; fewer would have joined the words before.
            int before = w, back = 0;
            while (before > 0 && back < reach) back += words[--before].Glyphs;
            int from = words[w].Start, to = words[last].End;
            ComplexShaper.ShapeAs(Program, s, words[before].Start, words[after - 1].End - words[before].Start, script, window, _off);
            foreach (var g in window)
                if (g.Start >= from && g.Start < to) output.Add(g);
            w = last + 1;
        }
        return true;
    }

    public override float CharWidth(char c, float size)
        => IsHidden(c) ? 0 : SizeAdjust == 1 ? _widths[c] * size / 1000f : _widths[c] * size * SizeAdjust / 1000f;
}

/// <summary>
/// The fonts of a font-family list, tried in order for every character as browsers do: a character the first font
/// lacks is drawn with the next font that has it. The first font gives the line metrics.
/// </summary>
internal sealed class FontChain : TextFont
{
    private readonly TextFont _primary;
    // The font of the font-relative units and font-size-adjust when it is not the primary one: a later face of a family
    // split by unicode-range whose first face lacks the space. The line metrics stay the primary font's: Chrome takes
    // them from that face too, but grows each line for the fonts its glyphs use, which the layout does not, and the
    // primary font's metrics come closer to Chrome's lines when the text uses the first face.
    private readonly TextFont? _metrics;
    // Finds a font for a character the primary lacks, the first after the primary that also passes the test when one
    // is given.
    private readonly Func<int, Func<TextFont, bool>?, TextFont?> _fallback;
    private readonly Dictionary<int, TextFont> _picked = new();
    // The document's feature variants, which every chain of the document takes its fonts' variants from.
    private readonly DocumentFontVariants? _document;

    public FontChain(TextFont primary, Func<int, Func<TextFont, bool>?, TextFont?> fallback) : this(primary, null, fallback)
    {
    }

    public FontChain(TextFont primary, TextFont? metrics, Func<int, Func<TextFont, bool>?, TextFont?> fallback, DocumentFontVariants? document = null)
    {
        _primary = primary;
        _metrics = metrics;
        _fallback = fallback;
        _document = document;
        Ascent = primary.Ascent;
        Descent = primary.Descent;
        LineGap = primary.LineGap;
    }

    public TextFont Primary => _primary;

    private static bool Covers(TextFont f, int cp)
        => f is EmbeddedFont e ? e.Covers(cp) : cp < 0x10000 && StandardFont.CanEncode((char)cp);

    private static int Units(TextFont f, int cp)
        => f is EmbeddedFont e ? e.CharUnits(cp) : ((StandardFont)f).CharUnits((char)cp);

    // Combining marks and joiners are drawn with the font of the character before them when it has them, so that they
    // shape with it, and so is the rest of an emoji sequence: skin tones, tags, the character after a zero width joiner
    // and the second letter of a flag.
    private static bool StaysInRun(int previous, int cp)
        => cp >= 0x0300 && (cp is 0x200C or 0x200D or (>= 0xE0020 and <= 0xE007F) or (>= 0x1F3FB and <= 0x1F3FF)
            || previous == 0x200D || IsRegionalIndicator(previous) && IsRegionalIndicator(cp) || Text.UnicodeTables.IsMark(cp));

    private static bool IsRegionalIndicator(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

    /// <summary>
    /// The font for the text from <paramref name="i"/>, and where the stretch it decides ends. A cluster of several code
    /// points, such as an emoji sequence, is drawn with the first font that has all of it, as Chrome keeps a cluster in
    /// one font, which matters when unicode-range splits a font in parts; otherwise each code point has its own.
    /// </summary>
    private TextFont Next(string s, int i, int end, TextFont? current, ref int previous, out int next)
    {
        int cp = s[i];
        next = i + 1;
        if (char.IsHighSurrogate(s[i]) && next < end && char.IsLowSurrogate(s[next])) cp = char.ConvertToUtf32(s[i], s[next++]);
        bool stays = current != null && StaysInRun(previous, cp);
        if (!stays && next < end && StaysInRun(cp, CodePointAt(s, next, end)))
        {
            int clusterEnd = i + StringInfo.GetNextTextElementLength(s.AsSpan(i, end - i));
            var first = Pick(cp);
            if (clusterEnd > next && PickCluster(first, cp, s, i, clusterEnd) is { } font)
            {
                previous = CodePointBefore(s, clusterEnd);
                next = clusterEnd;
                return font;
            }
        }
        previous = cp;
        // A default ignorable character draws nothing in any font, so it stays in the run it is in rather than start one
        // of a font that has it: that would split the text around it and embed a font for nothing.
        if (IsHidden(cp)) return current ?? _primary;
        return stays && Covers(current!, cp) ? current! : Pick(cp);
    }

    // The first font that has every code point of a cluster. Its test captures the text, so it lives in a method of its
    // own: in Next, the closure's object was made on every character, which made text layout allocate three times as much.
    private TextFont? PickCluster(TextFont first, int cp, string s, int start, int end)
        => CoversAll(first, s, start, end) ? first : _fallback(cp, f => CoversAll(f, s, start, end));

    private static int CodePointAt(string s, int i, int end)
        => char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

    private static int CodePointBefore(string s, int end)
        => end >= 2 && char.IsLowSurrogate(s[end - 1]) && char.IsHighSurrogate(s[end - 2]) ? char.ConvertToUtf32(s[end - 2], s[end - 1]) : s[end - 1];

    // Whether a font has every code point of a cluster that is drawn; joiners and other invisible ones are not.
    private static bool CoversAll(TextFont f, string s, int start, int end)
    {
        for (int i = start; i < end;)
        {
            int cp = CodePointAt(s, i, end);
            if (!Text.UnicodeTables.IsDefaultIgnorable(cp) && !Covers(f, cp)) return false;
            i += cp >= 0x10000 ? 2 : 1;
        }
        return true;
    }

    /// <summary>The font that draws a code point.</summary>
    public TextFont Pick(int cp)
    {
        if (cp < 0x20 || Covers(_primary, cp)) return _primary;
        if (_picked.TryGetValue(cp, out var f)) return f;
        f = _fallback(cp, null) ?? _primary;
        _picked[cp] = f;
        return f;
    }

    // Each stretch of characters drawn with one font is measured as a run of that font, so it is kerned and ligated.
    public override int MeasureUnits(string s, int start, int length)
    {
        int total = 0, runStart = start, end = start + length, previous = 0;
        TextFont? runFont = null;
        for (int i = start, next; i < end; i = next)
        {
            int at = i;
            var f = Next(s, i, end, runFont, ref previous, out next);
            if (ReferenceEquals(f, runFont)) continue;
            if (runFont != null) total += runFont.MeasureUnits(s, runStart, at - runStart);
            runFont = f;
            runStart = at;
        }
        if (runFont != null) total += runFont.MeasureUnits(s, runStart, end - runStart);
        return total;
    }

    public override float CharWidth(char c, float size)
        => Pick(c) is EmbeddedFont { SizeAdjust: not 1 } scaled ? scaled.CharWidth(c, size) : Units(Pick(c), c) * size / 1000f;

    public override bool HasKerning => _primary.HasKerning;

    private FontChain? _withoutLigatures, _withoutKerning, _withoutContextual;

    public override TextFont WithoutLigatures()
        => _withoutLigatures ??= new FontChain(_primary.WithoutLigatures(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutLigatures(), _document);

    public override bool ShapesInContext => _primary.ShapesInContext;

    public override TextFont WithoutContextual()
        => _withoutContextual ??= new FontChain(_primary.WithoutContextual(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutContextual(), _document);

    public override TextFont WithoutKerning()
        => _withoutKerning ??= new FontChain(_primary.WithoutKerning(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutKerning(), _document);

    // The first available font is the one font-size-adjust holds constant.
    public override float Metric(FontMetric metric) => (_metrics ?? _primary).Metric(metric);

    public override bool Supports(string features) => _primary.Supports(features);

    private const int MaxVariants = 64;
    private System.Collections.Concurrent.ConcurrentDictionary<string, FontChain>? _variants;

    public override TextFont WithFeatures(string features)
    {
        var variants = LazyInitializer.EnsureInitialized(ref _variants, () => new());
        if (variants.TryGetValue(features, out var cached)) return cached;
        // A chain belongs to one document's font resolver, so this caps what one stylesheet's feature settings can make
        // of it, as DocumentFontVariants caps the fonts' own variants. Past the cap the chain is used as it is, the same
        // way in every conversion of the document.
        if (variants.Count >= MaxVariants) return this;
        return variants.GetOrAdd(features, k => new FontChain(Variant(_primary, k), _metrics, (cp, accept) => _fallback(cp, accept) is { } f ? Variant(f, k) : null, _document));
    }

    private TextFont Variant(TextFont font, string features) => _document?.WithFeatures(font, features) ?? font.WithFeatures(features);

    public override int KernUnits(int left, int right)
    {
        var f = Pick(left);
        return ReferenceEquals(f, Pick(right)) ? f.KernUnits(left, right) : 0;
    }

    public override void Split(string text, List<FontRun> runs)
    {
        TextFont? current = null;
        int runStart = 0, previous = 0;
        for (int i = 0, next; i < text.Length; i = next)
        {
            int start = i;
            // Marks and joiners stay with the run they are in. Spaces are drawn with the font that measured them (the
            // primary font, as in browsers), or text after a fallback run would be drawn where the layout did not put it.
            var f = Next(text, i, text.Length, current, ref previous, out next);
            if (!ReferenceEquals(f, current))
            {
                if (current != null) runs.Add(new FontRun(current, runStart, start - runStart));
                current = f;
                runStart = start;
            }
        }
        if (current != null) runs.Add(new FontRun(current, runStart, text.Length - runStart));
    }
}

/// <summary>A CSS unicode-range descriptor: a sorted set of code point ranges.</summary>
internal sealed class UnicodeRange
{
    private readonly (int Lo, int Hi)[] _ranges;

    private UnicodeRange((int, int)[] ranges) => _ranges = ranges;

    // The ranges are sorted and merged, so that a font package's range of a CJK font, with hundreds of ranges, is
    // searched in a few steps for each character.
    public bool Contains(int cp)
    {
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (cp < _ranges[mid].Lo) hi = mid - 1;
            else if (cp > _ranges[mid].Hi) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>Parses "U+0000-00FF, U+0131, U+4??". Returns null for an empty or invalid list.</summary>
    /// <remarks>
    /// Parsed without substrings, since a font package registers dozens of faces whose ranges have thousands of parts.
    /// </remarks>
    public static UnicodeRange? Parse(string value)
    {
        var list = new List<(int, int)>();
        var rest = value.AsSpan();
        while (rest.Length > 0)
        {
            int comma = rest.IndexOf(',');
            var part = (comma < 0 ? rest : rest[..comma]).Trim();
            rest = comma < 0 ? default : rest[(comma + 1)..];
            if (part.Length < 3 || (part[0] != 'U' && part[0] != 'u') || part[1] != '+') continue;
            part = part[2..];
            int dash = part.IndexOf('-');
            if (dash >= 0)
            {
                if (TryHex(part[..dash], out int lo) && TryHex(part[(dash + 1)..], out int hi) && lo <= hi) list.Add((lo, hi));
            }
            else if (part.IndexOf('?') >= 0)
            {
                if (TryWildcard(part, '0', out int lo) && TryWildcard(part, 'F', out int hi)) list.Add((lo, hi));
            }
            else if (TryHex(part, out int cp)) list.Add((cp, cp));
        }
        if (list.Count == 0) return null;
        list.Sort();
        var merged = new List<(int, int)>(list.Count) { list[0] };
        for (int i = 1; i < list.Count; i++)
        {
            var (lo, hi) = list[i];
            var (lastLo, lastHi) = merged[^1];
            if (lo <= lastHi + 1) merged[^1] = (lastLo, Math.Max(lastHi, hi));
            else merged.Add((lo, hi));
        }
        return new UnicodeRange(merged.ToArray());
    }

    // "4??" with each ? read as the given digit.
    private static bool TryWildcard(ReadOnlySpan<char> part, char digit, out int value)
    {
        value = 0;
        if (part.Length > 8) return false;
        Span<char> digits = stackalloc char[part.Length];
        for (int i = 0; i < part.Length; i++) digits[i] = part[i] == '?' ? digit : part[i];
        return TryHex(digits, out value);
    }

    private static bool TryHex(ReadOnlySpan<char> s, out int value)
        => int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
           && value >= 0 && value <= 0x10FFFF;
}

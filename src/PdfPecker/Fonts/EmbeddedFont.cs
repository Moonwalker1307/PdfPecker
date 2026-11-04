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
    private ShapeCache? _shapeCache; // the text this font has shaped, made on first use

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
        => (uint)codePoint < 0x10000 ? _widths[codePoint] : Program.Advance1000(Program.GlyphId(codePoint));

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

    public override TextFont WithFeatures(string features)
    {
        var program = Program.WithFeatures(features);
        if (ReferenceEquals(program, Program)) return this;
        var variants = LazyInitializer.EnsureInitialized(ref _variants, () => new());
        // Bounded by the program's own cap: past it the program comes back unchanged and this font is returned above.
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
    }

    /// <summary>
    /// True when the text goes through the OpenType shaper: it has characters of a complex script, or the font has
    /// features on that substitute in context, such as contextual alternates or fractions.
    /// </summary>
    public bool UsesShaper(string s, int start, int length)
        => _shapesInContext || Program.ShapedFeatures != null || ComplexShaper.Handles(s, start, length);

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
        glyphs.Clear();
        var lig = _ligatures;
        bool ligate = false;
        int prev = -1;
        for (int i = start, end = start + length; i < end; i++)
        {
            int cp = s[i], n = 1;
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1])) { cp = char.ConvertToUtf32(s[i], s[i + 1]); n = 2; }
            int gid = Program.GlyphId(cp);
            if (rtl && Text.UnicodeTables.Mirror(cp) is int m && m != cp && Program.GlyphId(m) is int mg && mg != 0) gid = mg;
            if (lig != null && !ligate && prev >= 0 && lig.StartsLigature(prev, gid)) ligate = true;
            glyphs.Add(new ShapedGlyph(gid, i, n));
            prev = gid;
            i += n - 1;
        }
        if (ligate) lig!.Apply(glyphs);
        return false;
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
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[++i]);
                total += CharUnits(cp);
            }
            else total += w[cp];
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
        if (Shapes.TryGet(s, start, length, rtl, null, out int width, out _)) return width;
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

    /// <summary>
    /// The OpenType shaper's glyphs for the text, kept for the next time the font shapes the same text; returns whether
    /// the text is inert (see <see cref="InertInContext"/>).
    /// </summary>
    private bool ShapeComplex(string s, int start, int length, bool rtl, List<ShapedGlyph> glyphs)
    {
        var cache = Shapes;
        if (cache.TryGet(s, start, length, rtl, glyphs, out _, out bool inert)) return inert;
        ComplexShaper.Shape(Program, s, start, length, rtl, glyphs, _off);
        inert = !rtl && IsInert(s, start, length, glyphs);
        cache.Add(s, start, length, rtl, glyphs, inert);
        return inert;
    }

    /// <summary>
    /// Whether the text is inert in context: shaped on its own, every character keeps its own glyph, unmoved, and none
    /// of those glyphs starts a lookup of more than one glyph. Next to other inert text it is then shaped as on its
    /// own, so a run of such words is as wide as its words and the kerning between them, give or take the rounding of
    /// each width to 1/1000 em.
    /// </summary>
    public override bool InertInContext(string s, int start, int length)
    {
        if (length == 0) return true;
        if (!UsesShaper(s, start, length) || ComplexShaper.GuessRtl(s, start, length)) return false;
        var glyphs = t_glyphs ??= new List<ShapedGlyph>();
        return ShapeComplex(s, start, length, false, glyphs);
    }

    private bool IsInert(string s, int start, int length, List<ShapedGlyph> glyphs)
    {
        // Features asked for by font-feature-settings and the complex scripts have lookups of their own.
        if (Program.ShapedFeatures != null || ComplexShaper.Handles(s, start, length) || Program.MultiGlyphStarts is not { } starts) return false;
        int at = start, end = start + length;
        foreach (var g in glyphs)
        {
            if (g.Start != at || g.Dx != 0 || g.Dy != 0) return false;
            if (at >= end) return false;
            int cp = s[at], n = 1;
            if (char.IsHighSurrogate(s[at]) && at + 1 < end && char.IsLowSurrogate(s[at + 1])) { cp = char.ConvertToUtf32(s[at], s[at + 1]); n = 2; }
            if (g.Length != n || g.Gid != Program.GlyphId(cp) || (starts[g.Gid >> 6] >> (g.Gid & 63) & 1) != 0
                || (Program.Layout!.GlyphProps(g.Gid) & OpenTypeLayout.Mark) != 0)
                return false;
            at += n;
        }
        return at == end;
    }

    public override float CharWidth(char c, float size) => SizeAdjust == 1 ? _widths[c] * size / 1000f : _widths[c] * size * SizeAdjust / 1000f;
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

    public FontChain(TextFont primary, Func<int, Func<TextFont, bool>?, TextFont?> fallback) : this(primary, null, fallback)
    {
    }

    public FontChain(TextFont primary, TextFont? metrics, Func<int, Func<TextFont, bool>?, TextFont?> fallback)
    {
        _primary = primary;
        _metrics = metrics;
        _fallback = fallback;
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

    // Only text drawn in one of the fonts is known to be inert: a change of font ends the shaped run.
    public override bool InertInContext(string s, int start, int length)
    {
        int previous = 0;
        TextFont? runFont = null;
        for (int i = start, end = start + length, next; i < end; i = next)
        {
            var f = Next(s, i, end, runFont, ref previous, out next);
            if (runFont != null && !ReferenceEquals(f, runFont)) return false;
            runFont = f;
        }
        return runFont == null || runFont.InertInContext(s, start, length);
    }

    public override float CharWidth(char c, float size)
        => Pick(c) is EmbeddedFont { SizeAdjust: not 1 } scaled ? scaled.CharWidth(c, size) : Units(Pick(c), c) * size / 1000f;

    public override bool HasKerning => _primary.HasKerning;

    private FontChain? _withoutLigatures, _withoutKerning, _withoutContextual;

    public override TextFont WithoutLigatures()
        => _withoutLigatures ??= new FontChain(_primary.WithoutLigatures(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutLigatures());

    public override bool ShapesInContext => _primary.ShapesInContext;

    public override TextFont WithoutContextual()
        => _withoutContextual ??= new FontChain(_primary.WithoutContextual(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutContextual());

    public override TextFont WithoutKerning()
        => _withoutKerning ??= new FontChain(_primary.WithoutKerning(), _metrics, (cp, accept) => _fallback(cp, accept)?.WithoutKerning());

    // The first available font is the one font-size-adjust holds constant.
    public override float Metric(FontMetric metric) => (_metrics ?? _primary).Metric(metric);

    public override bool Supports(string features) => _primary.Supports(features);

    private const int MaxVariants = 64;
    private System.Collections.Concurrent.ConcurrentDictionary<string, FontChain>? _variants;

    public override TextFont WithFeatures(string features)
    {
        var variants = LazyInitializer.EnsureInitialized(ref _variants, () => new());
        if (variants.TryGetValue(features, out var cached)) return cached;
        // Bundled font chains live as long as the process, so ever new feature settings must not grow this forever.
        if (variants.Count >= MaxVariants) return this;
        return variants.GetOrAdd(features, k => new FontChain(_primary.WithFeatures(k), _metrics, (cp, accept) => _fallback(cp, accept)?.WithFeatures(k)));
    }

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

    public bool Contains(int cp)
    {
        foreach (var (lo, hi) in _ranges)
            if (cp >= lo && cp <= hi) return true;
        return false;
    }

    /// <summary>Parses "U+0000-00FF, U+0131, U+4??". Returns null for an empty or invalid list.</summary>
    public static UnicodeRange? Parse(string value)
    {
        var list = new List<(int, int)>();
        foreach (var raw in value.Split(','))
        {
            var part = raw.Trim();
            if (part.Length < 3 || (part[0] != 'U' && part[0] != 'u') || part[1] != '+') continue;
            part = part.Substring(2);
            int dash = part.IndexOf('-');
            if (dash >= 0)
            {
                if (TryHex(part.Substring(0, dash), out int lo) && TryHex(part.Substring(dash + 1), out int hi) && lo <= hi) list.Add((lo, hi));
            }
            else if (part.IndexOf('?') >= 0)
            {
                if (TryHex(part.Replace('?', '0'), out int lo) && TryHex(part.Replace('?', 'F'), out int hi)) list.Add((lo, hi));
            }
            else if (TryHex(part, out int cp)) list.Add((cp, cp));
        }
        return list.Count == 0 ? null : new UnicodeRange(list.ToArray());
    }

    private static bool TryHex(string s, out int value)
        => int.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
           && value >= 0 && value <= 0x10FFFF;
}

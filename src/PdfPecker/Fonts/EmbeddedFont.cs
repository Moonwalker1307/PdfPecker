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
    private readonly Ligatures? _ligatures; // null when the font has none or they are turned off
    private EmbeddedFont? _withoutLigatures, _withoutKerning;
    private bool _noKerning;

    // metrics: line metrics to use instead of the font's own, for a bundled font standing in for a standard font.
    public EmbeddedFont(FontProgram program, bool syntheticBold, bool syntheticItalic, UnicodeRange? range = null, TextFont? metrics = null)
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
    }

    /// <summary>True when the font has a glyph for the code point (and its unicode-range, if any, includes it).</summary>
    public bool Covers(int codePoint)
        => (_range == null || _range.Contains(codePoint)) && Program.GlyphId(codePoint) != 0;

    public int CharUnits(int codePoint)
        => (uint)codePoint < 0x10000 ? _widths[codePoint] : Program.Advance1000(Program.GlyphId(codePoint));

    public override bool HasKerning => !_noKerning && Program.Kerning != null;

    public override int KernUnits(int left, int right) => _noKerning ? 0 : Program.Kern1000(Program.GlyphId(left), Program.GlyphId(right));

    private EmbeddedFont(EmbeddedFont font, bool ligatures, bool kerning) : this(font.Program, font.SyntheticBold, font.SyntheticItalic, font._range)
    {
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
        if (!ligatures) _ligatures = null;
        _noKerning = !kerning;
    }

    public override TextFont WithoutLigatures()
        => _ligatures == null ? this : _withoutLigatures ??= new EmbeddedFont(this, ligatures: false, kerning: !_noKerning);

    public override TextFont WithoutKerning()
        => _noKerning || Program.Kerning == null ? this : _withoutKerning ??= new EmbeddedFont(this, ligatures: _ligatures != null, kerning: false);

    public override float Metric(FontMetric metric)
    {
        float upm = Program.UnitsPerEm;
        return metric switch
        {
            FontMetric.XHeight => Program.XHeight / upm,
            FontMetric.CapHeight => Program.CapHeight / upm,
            FontMetric.ZeroWidth => Program.GlyphId('0') is > 0 and var zero ? Program.Advance(zero) / upm : 0,
            FontMetric.IdeographWidth => Program.GlyphId(0x6C34) is > 0 and var water ? Program.Advance(water) / upm : 0,
            FontMetric.UnderlinePosition => -Program.UnderlinePosition / upm,
            FontMetric.EmDescent => Program.EmDescent,
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

    private EmbeddedFont(EmbeddedFont font, FontProgram program) : this(program, font.SyntheticBold, font.SyntheticItalic, font._range)
    {
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
        if (font._ligatures == null) _ligatures = null;
        _noKerning = font._noKerning;
    }

    /// <summary>
    /// True when the text goes through the OpenType shaper: it has characters of a complex script, or the font has
    /// features on that substitute in context, such as fractions.
    /// </summary>
    public bool UsesShaper(string s, int start, int length) => Program.ShapedFeatures != null || ComplexShaper.Handles(s, start, length);

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
            ComplexShaper.Shape(Program, s, start, length, rtl, glyphs);
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
                if (prev >= 0) total += KernUnits(prev, cp);
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
        var glyphs = t_glyphs ??= new List<ShapedGlyph>();
        ComplexShaper.Shape(Program, s, start, length, ComplexShaper.GuessRtl(s, start, length), glyphs);
        float total = 0;
        foreach (var g in glyphs) total += g.Advance;
        return (int)MathF.Round(total);
    }

    public override float CharWidth(char c, float size) => _widths[c] * size / 1000f;
}

/// <summary>
/// The fonts of a font-family list, tried in order for every character as browsers do: a character the first font
/// lacks is drawn with the next font that has it. The first font gives the line metrics.
/// </summary>
internal sealed class FontChain : TextFont
{
    private readonly TextFont _primary;
    // Finds a font for a character the primary lacks, the first after the primary that also passes the test when one
    // is given.
    private readonly Func<int, Func<TextFont, bool>?, TextFont?> _fallback;
    private readonly Dictionary<int, TextFont> _picked = new();

    public FontChain(TextFont primary, Func<int, Func<TextFont, bool>?, TextFont?> fallback)
    {
        _primary = primary;
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
            if (clusterEnd > next && (CoversAll(first, s, i, clusterEnd) ? first : _fallback(cp, f => CoversAll(f, s, i, clusterEnd))) is { } font)
            {
                previous = CodePointBefore(s, clusterEnd);
                next = clusterEnd;
                return font;
            }
        }
        previous = cp;
        return stays && Covers(current!, cp) ? current! : Pick(cp);
    }

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

    public override float CharWidth(char c, float size) => Units(Pick(c), c) * size / 1000f;

    public override bool HasKerning => _primary.HasKerning;

    private FontChain? _withoutLigatures, _withoutKerning;

    public override TextFont WithoutLigatures()
        => _withoutLigatures ??= new FontChain(_primary.WithoutLigatures(), (cp, accept) => _fallback(cp, accept)?.WithoutLigatures());

    public override TextFont WithoutKerning()
        => _withoutKerning ??= new FontChain(_primary.WithoutKerning(), (cp, accept) => _fallback(cp, accept)?.WithoutKerning());

    // The first font of the list is the one font-size-adjust holds constant.
    public override float Metric(FontMetric metric) => _primary.Metric(metric);

    public override bool Supports(string features) => _primary.Supports(features);

    private const int MaxVariants = 64;
    private System.Collections.Concurrent.ConcurrentDictionary<string, FontChain>? _variants;

    public override TextFont WithFeatures(string features)
    {
        var variants = LazyInitializer.EnsureInitialized(ref _variants, () => new());
        if (variants.TryGetValue(features, out var cached)) return cached;
        // Bundled font chains live as long as the process, so ever new feature settings must not grow this forever.
        if (variants.Count >= MaxVariants) return this;
        return variants.GetOrAdd(features, k => new FontChain(_primary.WithFeatures(k), (cp, accept) => _fallback(cp, accept)?.WithFeatures(k)));
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

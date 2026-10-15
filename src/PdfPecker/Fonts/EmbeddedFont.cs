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
    private EmbeddedFont? _withoutLigatures;

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

    public override bool HasKerning => Program.Kerning != null;

    public override int KernUnits(int left, int right) => Program.Kern1000(Program.GlyphId(left), Program.GlyphId(right));

    private EmbeddedFont(EmbeddedFont font) : this(font.Program, font.SyntheticBold, font.SyntheticItalic, font._range)
    {
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
        _ligatures = null;
    }

    public override TextFont WithoutLigatures()
        => _ligatures == null ? this : _withoutLigatures ??= new EmbeddedFont(this);

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
        if (ComplexShaper.Handles(s, start, length))
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
        if (ComplexShaper.Handles(s, start, length)) return MeasureComplex(s, start, length);
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
    private readonly Func<int, TextFont?> _fallback;   // finds a font for a character the primary lacks
    private readonly Dictionary<int, TextFont> _picked = new();

    public FontChain(TextFont primary, Func<int, TextFont?> fallback)
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
    // shape with it.
    private static bool StaysInRun(int cp) => cp >= 0x0300 && (cp is 0x200C or 0x200D || Text.UnicodeTables.IsMark(cp));

    /// <summary>The font that draws a code point.</summary>
    public TextFont Pick(int cp)
    {
        if (cp < 0x20 || Covers(_primary, cp)) return _primary;
        if (_picked.TryGetValue(cp, out var f)) return f;
        f = _fallback(cp) ?? _primary;
        _picked[cp] = f;
        return f;
    }

    // Each stretch of characters drawn with one font is measured as a run of that font, so it is kerned and ligated.
    public override int MeasureUnits(string s, int start, int length)
    {
        int total = 0, runStart = start, end = start + length;
        TextFont? runFont = null;
        for (int i = start; i < end; i++)
        {
            int at = i, cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < end && char.IsLowSurrogate(s[i + 1])) cp = char.ConvertToUtf32(s[i], s[++i]);
            var f = runFont != null && StaysInRun(cp) && Covers(runFont, cp) ? runFont : Pick(cp);
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

    private FontChain? _withoutLigatures;

    public override TextFont WithoutLigatures()
        => _withoutLigatures ??= new FontChain(_primary.WithoutLigatures(), cp => _fallback(cp)?.WithoutLigatures());

    public override int KernUnits(int left, int right)
    {
        var f = Pick(left);
        return ReferenceEquals(f, Pick(right)) ? f.KernUnits(left, right) : 0;
    }

    public override void Split(string text, List<FontRun> runs)
    {
        TextFont? current = null;
        int runStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int start = i, cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            // Spaces stay with the run they are in, so that a fallback character does not split a run more than needed.
            var f = (cp == ' ' || StaysInRun(cp)) && current != null && Covers(current, cp) ? current : Pick(cp);
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

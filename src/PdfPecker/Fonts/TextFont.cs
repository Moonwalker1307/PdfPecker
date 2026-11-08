namespace PdfPecker.Fonts;

/// <summary>A run of text drawn with one concrete font (a standard font or one embedded font program).</summary>
internal readonly struct FontRun
{
    public readonly TextFont Font;
    public readonly int Start, Length;
    public FontRun(TextFont font, int start, int length) { Font = font; Start = start; Length = length; }
}

/// <summary>
/// Font metrics: the ones font-size-adjust can hold constant (font-size-adjust: ex-height 0.5), and the underline
/// positions of text-underline-position (from-font is the top of the font's underline below the baseline, under the
/// bottom of the em box).
/// </summary>
internal enum FontMetric : byte { XHeight, CapHeight, ZeroWidth, IdeographWidth, UnderlinePosition, EmDescent }

/// <summary>A font as seen by layout: metrics and widths in 1/1000 em units.</summary>
internal abstract class TextFont
{
    // Line metrics as browsers use them (hhea, or OS/2 typo metrics when the font asks for them), in fractions of the em.
    public float Ascent;       // fraction of the em
    public float Descent;      // negative fraction of the em
    public float LineGap;      // fraction of the em; line-height: normal = Ascent - Descent + LineGap

    /// <summary>Width of the text in 1/1000 em units.</summary>
    public abstract int MeasureUnits(string s, int start, int length);

    public float Measure(string s, float size) => MeasureUnits(s, 0, s.Length) * size / 1000f;

    public abstract float CharWidth(char c, float size);

    /// <summary>True when the font kerns pairs of characters.</summary>
    public virtual bool HasKerning => false;

    /// <summary>The kerning between two code points in 1/1000 em, added to the advance of the first.</summary>
    public virtual int KernUnits(int left, int right) => 0;

    /// <summary>
    /// The same font with ligatures turned off, as browsers draw text with letter-spacing or font-variant-ligatures: none.
    /// </summary>
    public virtual TextFont WithoutLigatures() => this;

    /// <summary>
    /// The same font without contextual alternates (calt), as browsers draw text with letter-spacing,
    /// font-variant-ligatures: no-contextual or font-feature-settings: "calt" 0.
    /// </summary>
    public virtual TextFont WithoutContextual() => this;

    /// <summary>
    /// True when all of the font's text is shaped because it substitutes glyphs in context (calt, clig or rclt), so a
    /// run of words can be drawn with other glyphs than its words measured one by one.
    /// </summary>
    public virtual bool ShapesInContext => false;

    /// <summary>The same font without pair kerning, as browsers draw text with font-kerning: none.</summary>
    public virtual TextFont WithoutKerning() => this;

    /// <summary>A metric of the font as a fraction of the em; 0 when the font does not have it.</summary>
    public virtual float Metric(FontMetric metric) => 0;

    /// <summary>Whether the font substitutes glyphs for these OpenType features (a key from <see cref="FontFeatures.Key"/>).</summary>
    public virtual bool Supports(string features) => false;

    /// <summary>The same font with OpenType features applied (a key from <see cref="FontFeatures.Key"/>).</summary>
    public virtual TextFont WithFeatures(string features) => this;

    private const float Px = 0.75f; // one CSS pixel in points

    private static float RoundPx(float pt) => MathF.Floor(pt / Px + 0.5f) * Px;

    /// <summary>
    /// The ascent and descent at a font size (in points), each rounded to whole CSS pixels as Chrome does, so that line
    /// boxes come out the same height.
    /// </summary>
    public void LineMetrics(float size, out float ascent, out float descent)
    {
        ascent = RoundPx(Ascent * size);
        descent = RoundPx(-Descent * size);
    }

    /// <summary>The used value of line-height: normal at a font size (in points): the rounded ascent, descent and line gap.</summary>
    public float NormalLineHeight(float size) => RoundPx(Ascent * size) + RoundPx(-Descent * size) + RoundPx(LineGap * size);

    /// <summary>
    /// Whether a code point is default ignorable and so never drawn: bidi controls, joiners, variation selectors, the
    /// byte order mark and the like, which take no space, as browsers hide them. The shaper removes them itself; the
    /// text it never sees skips them with this. The soft hyphen is left to the layout, which draws it as a hyphen at a
    /// line break.
    /// </summary>
    public static bool IsHidden(int cp) => cp > 0xAD && Text.UnicodeTables.IsDefaultIgnorable(cp);

    /// <summary>
    /// How many characters of the text letter spacing goes after: each code point once, a surrogate pair included,
    /// and none of the hidden ones (see <see cref="IsHidden"/>), which are not drawn and which Chrome spaces neither.
    /// </summary>
    public static int SpacedCharacters(string text, int start, int length)
    {
        int count = 0;
        for (int i = start, end = start + length; i < end; i++)
        {
            char c = text[i];
            if (c <= 0xAD) count++;
            else if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
            {
                if (!IsHidden(char.ConvertToUtf32(c, text[++i]))) count++;
            }
            else if (!IsHidden(c)) count++;
        }
        return count;
    }

    /// <summary>
    /// Whether letter spacing goes after a character measured on its own: not after a hidden one, nor after the second
    /// half of a surrogate pair, whose first half takes it. Plane 14, whose high surrogate is U+DB40, is all hidden.
    /// </summary>
    public static bool Spaced(char c) => c <= 0xAD || !(char.IsLowSurrogate(c) || c == '\uDB40' || IsHidden(c));

    /// <summary>Splits text into runs that each use one concrete font. Most fonts are a single run.</summary>
    public virtual void Split(string text, List<FontRun> runs) => runs.Add(new FontRun(this, 0, text.Length));
}

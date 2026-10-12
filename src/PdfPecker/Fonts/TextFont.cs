namespace PdfPecker.Fonts;

/// <summary>A run of text drawn with one concrete font (a standard font or one embedded font program).</summary>
internal readonly struct FontRun
{
    public readonly TextFont Font;
    public readonly int Start, Length;
    public FontRun(TextFont font, int start, int length) { Font = font; Start = start; Length = length; }
}

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

    /// <summary>Splits text into runs that each use one concrete font. Most fonts are a single run.</summary>
    public virtual void Split(string text, List<FontRun> runs) => runs.Add(new FontRun(this, 0, text.Length));
}

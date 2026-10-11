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
    public float Ascent;       // fraction of the em
    public float Descent;      // negative fraction of the em
    public float LineGap;      // fraction of the em; line-height: normal = Ascent - Descent + LineGap

    /// <summary>Width of the text in 1/1000 em units.</summary>
    public abstract int MeasureUnits(string s, int start, int length);

    public float Measure(string s, float size) => MeasureUnits(s, 0, s.Length) * size / 1000f;

    public abstract float CharWidth(char c, float size);

    /// <summary>The used value of line-height: normal, as a factor of the font size.</summary>
    public virtual float NormalLineHeight => Ascent - Descent + LineGap;

    /// <summary>Splits text into runs that each use one concrete font. Most fonts are a single run.</summary>
    public virtual void Split(string text, List<FontRun> runs) => runs.Add(new FontRun(this, 0, text.Length));
}

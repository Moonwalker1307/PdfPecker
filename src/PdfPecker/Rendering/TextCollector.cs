namespace PdfPecker.Rendering;

/// <summary>
/// One glyph shown on a page, for text extraction: what it stands for and where it sits, in points from the top-left
/// corner of the displayed page (y downward).
/// </summary>
internal struct TextGlyph
{
    public string Text;
    public double X, Y; // the glyph origin on the baseline
    public double EndX, EndY; // the origin advanced by the glyph's width and character spacing, without word spacing
    public double DirX, DirY; // unit vector along the baseline in reading direction of the text space
    public double Size; // the font size as drawn
    public double SpaceWidth; // the width of the font's space as drawn, or 0 when the font has none
    public string Font;
    public bool Moved; // the first glyph after the PDF set the text position, as at the start of a table cell
}

/// <summary>Receives the glyphs <see cref="PageRenderer"/> shows when it runs a page for text instead of pixels.</summary>
internal sealed class TextCollector
{
    /// <summary>
    /// Glyphs kept per page. A small content stream can repeat a long string through forms many times, so this bounds the
    /// memory one page can take; the glyphs past it are not extracted.
    /// </summary>
    public const int MaxGlyphs = 2_000_000;

    public readonly List<TextGlyph> Glyphs = new();

    public void Add(in TextGlyph glyph)
    {
        if (Glyphs.Count < MaxGlyphs) Glyphs.Add(glyph);
    }

    /// <summary>Stretches the last glyph to also cover a following one, for glyphs that marked-content replacement text stands for.</summary>
    public void ExtendLast(double endX, double endY)
    {
        if (Glyphs.Count == 0) return;
        var g = Glyphs[^1];
        g.EndX = endX;
        g.EndY = endY;
        Glyphs[^1] = g;
    }
}

namespace PdfPecker.Fonts;

internal enum FontFamily : byte { Helvetica, Times, Courier }

/// <summary>One of the PDF standard fonts (no embedding needed), with WinAnsi glyph widths.</summary>
internal sealed class StandardFont
{
    public readonly string BaseFont;
    public readonly int Index;          // 0..11, used as resource name /F{Index+1}
    public readonly float Ascent;       // fraction of the em
    public readonly float Descent;      // negative fraction of the em
    private readonly short[] _widths;

    private StandardFont(string baseFont, int index, short[] widths, int ascent, int descent)
    {
        BaseFont = baseFont;
        Index = index;
        _widths = widths;
        Ascent = ascent / 1000f;
        Descent = descent / 1000f;
    }

    public static readonly StandardFont[] All =
    {
        new("Helvetica", 0, StandardFontMetrics.Helvetica, StandardFontMetrics.Helvetica_Ascent, StandardFontMetrics.Helvetica_Descent),
        new("Helvetica-Bold", 1, StandardFontMetrics.Helvetica_Bold, StandardFontMetrics.Helvetica_Bold_Ascent, StandardFontMetrics.Helvetica_Bold_Descent),
        new("Helvetica-Oblique", 2, StandardFontMetrics.Helvetica_Oblique, StandardFontMetrics.Helvetica_Oblique_Ascent, StandardFontMetrics.Helvetica_Oblique_Descent),
        new("Helvetica-BoldOblique", 3, StandardFontMetrics.Helvetica_BoldOblique, StandardFontMetrics.Helvetica_BoldOblique_Ascent, StandardFontMetrics.Helvetica_BoldOblique_Descent),
        new("Times-Roman", 4, StandardFontMetrics.Times_Roman, StandardFontMetrics.Times_Roman_Ascent, StandardFontMetrics.Times_Roman_Descent),
        new("Times-Bold", 5, StandardFontMetrics.Times_Bold, StandardFontMetrics.Times_Bold_Ascent, StandardFontMetrics.Times_Bold_Descent),
        new("Times-Italic", 6, StandardFontMetrics.Times_Italic, StandardFontMetrics.Times_Italic_Ascent, StandardFontMetrics.Times_Italic_Descent),
        new("Times-BoldItalic", 7, StandardFontMetrics.Times_BoldItalic, StandardFontMetrics.Times_BoldItalic_Ascent, StandardFontMetrics.Times_BoldItalic_Descent),
        new("Courier", 8, StandardFontMetrics.Courier, StandardFontMetrics.Courier_Ascent, StandardFontMetrics.Courier_Descent),
        new("Courier-Bold", 9, StandardFontMetrics.Courier_Bold, StandardFontMetrics.Courier_Bold_Ascent, StandardFontMetrics.Courier_Bold_Descent),
        new("Courier-Oblique", 10, StandardFontMetrics.Courier_Oblique, StandardFontMetrics.Courier_Oblique_Ascent, StandardFontMetrics.Courier_Oblique_Descent),
        new("Courier-BoldOblique", 11, StandardFontMetrics.Courier_BoldOblique, StandardFontMetrics.Courier_BoldOblique_Ascent, StandardFontMetrics.Courier_BoldOblique_Descent),
    };

    public static StandardFont Get(FontFamily family, bool bold, bool italic)
        => All[(int)family * 4 + (bold ? 1 : 0) + (italic ? 2 : 0)];

    /// <summary>Width of the text in 1/1000 em units.</summary>
    public int MeasureUnits(string s, int start, int length)
    {
        var w = _widths;
        int total = 0;
        for (int i = start, end = start + length; i < end; i++)
            total += w[WinAnsi.Encode(s[i])];
        return total;
    }

    public float Measure(string s, float size) => MeasureUnits(s, 0, s.Length) * size / 1000f;

    public float CharWidth(char c, float size) => _widths[WinAnsi.Encode(c)] * size / 1000f;
}

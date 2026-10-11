namespace PdfPecker.Fonts;

internal enum FontFamily : byte { Helvetica, Times, Courier }

/// <summary>One of the PDF standard fonts (no embedding needed), with WinAnsi glyph widths.</summary>
internal sealed class StandardFont : TextFont
{
    public readonly string BaseFont;
    public readonly int Index;          // 0..11, used as resource name /F{Index+1}
    private readonly short[] _widths;

    private StandardFont(string baseFont, int index, short[] widths)
    {
        BaseFont = baseFont;
        Index = index;
        _widths = widths;
        // Line metrics of the metric-compatible Liberation font (hhea, 2048 units per em), which is what browsers use in
        // place of Helvetica, Times and Courier (Arial, Times New Roman and Courier New): the AFM ascent and descent
        // describe the glyphs, not the line spacing.
        (int a, int d, int g) = (index / 4) switch { 0 => (1854, 434, 67), 1 => (1825, 443, 87), _ => (1705, 615, 0) };
        Ascent = a / 2048f;
        Descent = -d / 2048f;
        LineGap = g / 2048f;
    }

    public static readonly StandardFont[] All =
    {
        new("Helvetica", 0, StandardFontMetrics.Helvetica),
        new("Helvetica-Bold", 1, StandardFontMetrics.Helvetica_Bold),
        new("Helvetica-Oblique", 2, StandardFontMetrics.Helvetica_Oblique),
        new("Helvetica-BoldOblique", 3, StandardFontMetrics.Helvetica_BoldOblique),
        new("Times-Roman", 4, StandardFontMetrics.Times_Roman),
        new("Times-Bold", 5, StandardFontMetrics.Times_Bold),
        new("Times-Italic", 6, StandardFontMetrics.Times_Italic),
        new("Times-BoldItalic", 7, StandardFontMetrics.Times_BoldItalic),
        new("Courier", 8, StandardFontMetrics.Courier),
        new("Courier-Bold", 9, StandardFontMetrics.Courier_Bold),
        new("Courier-Oblique", 10, StandardFontMetrics.Courier_Oblique),
        new("Courier-BoldOblique", 11, StandardFontMetrics.Courier_BoldOblique),
    };

    public static StandardFont Get(FontFamily family, bool bold, bool italic)
        => All[(int)family * 4 + (bold ? 1 : 0) + (italic ? 2 : 0)];

    /// <summary>True when the WinAnsi encoding has the character itself (not a substitute or '?').</summary>
    public static bool CanEncode(char c) => WinAnsi.IsExact(c);

    /// <summary>Width of the text in 1/1000 em units.</summary>
    public override int MeasureUnits(string s, int start, int length)
    {
        var w = _widths;
        int total = 0;
        for (int i = start, end = start + length; i < end; i++)
            total += w[WinAnsi.Encode(s[i])];
        return total;
    }

    public int CharUnits(char c) => _widths[WinAnsi.Encode(c)];

    public override float CharWidth(char c, float size) => _widths[WinAnsi.Encode(c)] * size / 1000f;
}

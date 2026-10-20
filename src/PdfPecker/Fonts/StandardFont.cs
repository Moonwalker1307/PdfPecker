namespace PdfPecker.Fonts;

internal enum FontFamily : byte { Helvetica, Times, Courier }

/// <summary>One of the PDF standard fonts (no embedding needed), with WinAnsi glyph widths.</summary>
internal sealed class StandardFont : TextFont
{
    public readonly string BaseFont;
    public readonly int Index;          // 0..11, used as resource name /F{Index+1}
    private readonly short[] _widths;
    private readonly Dictionary<int, short>? _kerning; // left << 8 | right (WinAnsi codes) -> 1/1000 em

    private StandardFont? _withoutKerning;

    // The same font with its kerning pairs dropped. It keeps the index, so the writer shows it as the same PDF font.
    private StandardFont(StandardFont font)
    {
        BaseFont = font.BaseFont;
        Index = font.Index;
        _widths = font._widths;
        Ascent = font.Ascent;
        Descent = font.Descent;
        LineGap = font.LineGap;
    }

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
        var pairs = StandardFontMetrics.Kerning[index];
        if (pairs.Length > 0)
        {
            _kerning = new Dictionary<int, short>(pairs.Length);
            foreach (uint p in pairs) _kerning[(int)(p >> 16)] = (short)(p & 0xFFFF);
        }
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
        int total = 0, prev = -1;
        for (int i = start, end = start + length; i < end; i++)
        {
            int c = WinAnsi.Encode(s[i]);
            total += w[c];
            if (_kerning != null)
            {
                if (prev >= 0 && _kerning.TryGetValue(prev << 8 | c, out short k)) total += k;
                prev = c;
            }
        }
        return total;
    }

    public override bool HasKerning => _kerning != null;

    public override TextFont WithoutKerning() => _kerning == null ? this : _withoutKerning ??= new StandardFont(this);

    // The OS/2 x-height and cap height of the Liberation font that browsers draw the family with, like the line metrics.
    public override float Metric(FontMetric metric) => (Index / 4, metric) switch
    {
        (0, FontMetric.XHeight) => 1082 / 2048f,
        (0, FontMetric.CapHeight) => 1409 / 2048f,
        (1, FontMetric.XHeight) => 940 / 2048f,
        (1, FontMetric.CapHeight) => 1341 / 2048f,
        (_, FontMetric.XHeight) => 1082 / 2048f,
        (_, FontMetric.CapHeight) => 1349 / 2048f,
        (0, FontMetric.ZeroWidth) => 0.556f,
        (1, FontMetric.ZeroWidth) => 0.5f,
        (_, FontMetric.ZeroWidth) => 0.6f,
        _ => 0,
    };

    // Kerned like the Liberation font that browsers use in its place.
    public override int KernUnits(int left, int right)
        => _kerning != null && (uint)left < 0x10000 && (uint)right < 0x10000
           && _kerning.TryGetValue(WinAnsi.Encode((char)left) << 8 | WinAnsi.Encode((char)right), out short k) ? k : 0;

    public int CharUnits(char c) => _widths[WinAnsi.Encode(c)];

    public override float CharWidth(char c, float size) => _widths[WinAnsi.Encode(c)] * size / 1000f;
}

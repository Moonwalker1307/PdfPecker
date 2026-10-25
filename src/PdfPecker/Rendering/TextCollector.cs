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
    public int Mcid; // the marked-content id that ties the glyph to a tagged PDF's structure, -1 for none, -2 in an artifact
    public uint Color; // the colour the glyph is painted in, packed R | G << 8 | B << 16
}

/// <summary>
/// A straight horizontal or vertical line a page draws, such as a table border, in points from the top-left corner of
/// the displayed page: a horizontal rule runs from <see cref="Start"/> to <see cref="End"/> along x at height
/// <see cref="Position"/>, a vertical one along y at <see cref="Position"/> across. <see cref="Edge"/> marks the top or
/// bottom edge of a filled area, such as a cell's background, rather than a line drawn as such. <see cref="Color"/> and
/// <see cref="Width"/> are how the line looks, packed R | G &lt;&lt; 8 | B &lt;&lt; 16 and in points across.
/// </summary>
internal readonly record struct PageRule(bool Horizontal, double Position, double Start, double End, bool Edge = false, uint Color = 0, double Width = 0,
    bool Dashed = false);

/// <summary>
/// A box a page fills with one colour, such as a table cell's or a banner's background, in points from the top left, with
/// the radius of its rounded corners, 0 for square ones.
/// </summary>
internal readonly record struct PageFill(double X0, double Y0, double X1, double Y1, uint Color, double Radius = 0);

/// <summary>Receives the glyphs <see cref="PageRenderer"/> shows when it runs a page for text instead of pixels.</summary>
internal sealed class TextCollector
{
    /// <summary>
    /// Rules searched for tables per page. A page that draws more is a drawing, such as a map or a chart, rather than
    /// tables; one rule past this is kept so the search can tell, and the rest are not.
    /// </summary>
    public const int MaxRules = 10_000;

    /// <summary>The thickest filled shape taken as a line rather than an area, in points.</summary>
    public const double MaxRuleThickness = 3;

    /// <summary>The straight lines the page draws, when they are collected for finding tables; null when they are not.</summary>
    public readonly List<PageRule>? Rules;

    /// <summary>
    /// Small filled shapes, such as the dots and squares a list draws as its bullets, collected with the rules; each is a
    /// box in points from the top left of the page.
    /// </summary>
    public readonly List<(double X0, double Y0, double X1, double Y1)>? Bullets;

    /// <summary>The largest bullet shape kept, in points across.</summary>
    public const double MaxBullet = 7;

    /// <summary>The areas the page fills with a plain colour, collected with the rules, in painting order.</summary>
    public readonly List<PageFill>? Fills;

    public TextCollector(bool rules = false)
    {
        if (rules)
        {
            Rules = new List<PageRule>();
            Bullets = new List<(double, double, double, double)>();
            Fills = new List<PageFill>();
        }
    }

    public void AddFill(in PageFill fill)
    {
        if (Fills != null && Fills.Count < MaxRules) Fills.Add(fill);
    }

    public void AddBullet(double x0, double y0, double x1, double y1)
    {
        if (Bullets != null && Bullets.Count < MaxRules) Bullets.Add((x0, y0, x1, y1));
    }

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

    public void AddRule(in PageRule rule)
    {
        if (Rules != null && Rules.Count <= MaxRules) Rules.Add(rule);
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

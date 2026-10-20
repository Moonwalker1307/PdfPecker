namespace PdfPecker.Css;

/// <summary>
/// A block's ::first-line style. Text on the first line takes the first-line value of each property it inherits
/// unchanged from the block; properties an inline element sets itself keep its own value, as the first-line box is the
/// parent of the inline elements on that line.
/// </summary>
internal sealed class FirstLineStyle
{
    public readonly ComputedStyle Block, Line;
    private readonly Dictionary<ComputedStyle, ComputedStyle> _mapped = new(ReferenceEqualityComparer.Instance);

    public FirstLineStyle(ComputedStyle block, ComputedStyle line)
    {
        Block = block;
        Line = line;
    }

    /// <summary>The style of text in style <paramref name="s"/> when it is on the first line.</summary>
    public ComputedStyle Map(ComputedStyle s)
    {
        if (_mapped.TryGetValue(s, out var m)) return m;
        var b = Block;
        var l = Line;
        var c = s.Clone();
        if (s.Color == b.Color) c.Color = l.Color;
        if (s.FontFamily == b.FontFamily && s.FontFamilyName == b.FontFamilyName) { c.FontFamily = l.FontFamily; c.FontFamilyName = l.FontFamilyName; }
        if (s.FontWeight == b.FontWeight && s.Bold == b.Bold) { c.FontWeight = l.FontWeight; c.Bold = l.Bold; }
        if (s.Italic == b.Italic) c.Italic = l.Italic;
        if (s.FontSize == b.FontSize) c.FontSize = l.FontSize;
        if (s.SmallCaps == b.SmallCaps) c.SmallCaps = l.SmallCaps;
        if (s.LetterSpacing == b.LetterSpacing) c.LetterSpacing = l.LetterSpacing;
        if (s.WordSpacing == b.WordSpacing) c.WordSpacing = l.WordSpacing;
        if (s.TextTransform == b.TextTransform) c.TextTransform = l.TextTransform;
        if (s.TextShadows == b.TextShadows) c.TextShadows = l.TextShadows;
        if (s.TextStrokeWidth == b.TextStrokeWidth && s.TextStrokeColor == b.TextStrokeColor) { c.TextStrokeWidth = l.TextStrokeWidth; c.TextStrokeColor = l.TextStrokeColor; }
        if (s.LineHeightKind == b.LineHeightKind && s.LineHeightValue == b.LineHeightValue) { c.LineHeightKind = l.LineHeightKind; c.LineHeightValue = l.LineHeightValue; }
        if (s.TextDecoration == b.TextDecoration && s.DecorationColor == b.DecorationColor && s.DecorationStyle == b.DecorationStyle)
        {
            c.TextDecoration = l.TextDecoration;
            c.DecorationColor = l.DecorationColor;
            c.DecorationStyle = l.DecorationStyle;
            c.DecorationThickness = l.DecorationThickness;
        }
        c.InvalidateFont();
        _mapped[s] = c;
        return c;
    }
}

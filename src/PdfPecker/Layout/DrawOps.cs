using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// A paint instruction in "flow" coordinates: points from the top-left of the first page's content area,
/// with y growing downward and continuing across pages (page n covers y in [n*H, (n+1)*H)).
/// </summary>
/// <remarks>
/// The leaf ops are here; <see cref="RoundRectOp"/>, <see cref="BackgroundOp"/> and the group ops
/// (<see cref="GroupOp"/>, <see cref="ClipOp"/>, <see cref="LayerOp"/>, <see cref="FieldOp"/>) have files of their own.
/// </remarks>
internal abstract class DrawOp
{
    /// <summary>In a tagged PDF, the structure content this op draws; null for artifacts such as backgrounds and borders.</summary>
    public ContentSlot? Tag;

    /// <summary>Moves the op by (dx, dy) in flow coordinates.</summary>
    public abstract void Offset(float dx, float dy);

    /// <summary>
    /// A copy that can be moved on its own: a group's copy starts without ops. A multi-column box draws one copy of a
    /// box split between columns in each of them.
    /// </summary>
    public DrawOp Copy()
    {
        var copy = (DrawOp)MemberwiseClone();
        copy.OwnArrays();
        return copy;
    }

    /// <summary>Gives a copy its own instances of what <see cref="Offset"/> changes in place.</summary>
    protected virtual void OwnArrays() { }
}

/// <summary>A filled rectangle.</summary>
internal sealed class RectOp : DrawOp
{
    public float X, Y, W, H;
    public uint Color;
    /// <summary>A text underline, overline or line-through: Chrome paints these at fractional x.</summary>
    public bool IsDecoration;

    public RectOp(float x, float y, float w, float h, uint color)
    {
        X = x; Y = y; W = w; H = h;
        Color = color;
    }

    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>
/// A border whose four sides are solid and alike, which Chrome strokes as one rectangle instead of filling each side.
/// Viewers draw a thin stroke and a thin fill differently (pdfium widens a filled sliver to whole pixels), so this
/// keeps hairline box borders looking as they do in Chrome's PDFs.
/// </summary>
internal sealed class BorderRectOp : DrawOp
{
    /// <summary>The border box.</summary>
    public float X, Y, W, H;
    /// <summary>The border width.</summary>
    public float Width;
    public uint Color;

    public BorderRectOp(float x, float y, float w, float h, float width, uint color)
    {
        X = x; Y = y; W = w; H = h;
        Width = width;
        Color = color;
    }

    public override void Offset(float dx, float dy) { X += dx; Y += dy; }

    /// <summary>The four sides as filled rectangles, for a border split by a page break.</summary>
    public RectOp[] Sides() =>
    [
        new RectOp(X, Y, W, Width, Color), new RectOp(X, Y + H - Width, W, Width, Color),
        new RectOp(X, Y, Width, H, Color), new RectOp(X + W - Width, Y, Width, H, Color),
    ];
}

/// <summary>A stroked line for dashed or dotted borders.</summary>
internal sealed class LineOp : DrawOp
{
    public float X1, Y1, X2, Y2, Width;
    public uint Color;
    public bool Dotted;
    /// <summary>A solid line with round caps (wavy text decorations); otherwise dashed or dotted.</summary>
    public bool Solid;
    /// <summary>A dash pattern of its own, with butt caps (text decorations); 0 for the pattern of the dotted flag.</summary>
    public float DashLength, DashGap;
    /// <summary>
    /// How far into its pattern the line starts. A dotted line with a <see cref="DashGap"/> but no
    /// <see cref="DashLength"/> has round dots that far apart (borders).
    /// </summary>
    public float DashPhase;

    public LineOp(float x1, float y1, float x2, float y2, float width, uint color, bool dotted)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        Width = width;
        Color = color;
        Dotted = dotted;
    }

    public override void Offset(float dx, float dy) { X1 += dx; X2 += dx; Y1 += dy; Y2 += dy; }
}

/// <summary>A run of text in one font, drawn from (X, Baseline).</summary>
internal sealed class TextOp : DrawOp
{
    public float X, Baseline;
    /// <summary>The top of the line box, which decides the page the text goes on.</summary>
    public float LineTop;
    public TextFont Font;
    public float Size;
    public uint Color;
    public string Text;
    public float WordSpacing, CharSpacing;
    /// <summary>The text is in logical order and drawn right to left (its glyphs reversed).</summary>
    public bool Rtl;
    /// <summary>The outline (-webkit-text-stroke) in points and 0xAARRGGBB; none when the width is 0.</summary>
    public float StrokeWidth;
    public uint StrokeColor;
    /// <summary>paint-order: stroke, the fill is drawn over the stroke.</summary>
    public bool StrokeFirst;
    /// <summary>
    /// Drawn upside down about its baseline: a copy, for background-clip: text, of text in a line that a vertical-lr box
    /// flips back.
    /// </summary>
    public bool UpsideDown;

    public TextOp(float x, float baseline, float lineTop, TextFont font, float size, uint color, string text, float wordSpacing, float charSpacing)
    {
        X = x; Baseline = baseline; LineTop = lineTop;
        Font = font; Size = size; Color = color; Text = text;
        WordSpacing = wordSpacing; CharSpacing = charSpacing;
    }

    /// <summary>Takes the text outline from <paramref name="style"/>.</summary>
    public TextOp WithStroke(ComputedStyle style)
    {
        if (!(style.TextStrokeWidth > 0)) return this;
        StrokeWidth = style.TextStrokeWidth;
        StrokeColor = CssColor.Fade(style.TextStrokeColor ?? style.Color, style.InlineFade);
        StrokeFirst = style.PaintStrokeFirst;
        return this;
    }

    public override void Offset(float dx, float dy) { X += dx; Baseline += dy; LineTop += dy; }
}

/// <summary>A raster image scaled into the box (X, Y, W, H).</summary>
internal sealed class ImageOp : DrawOp
{
    public float X, Y, W, H;
    public PdfImage Image;
    /// <summary>Rounded corners the image is clipped to.</summary>
    public CornerRadii? Radii;

    public ImageOp(float x, float y, float w, float h, PdfImage image)
    {
        X = x; Y = y; W = w; H = h;
        Image = image;
    }

    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>An inline SVG drawing scaled into the box (X, Y, W, H).</summary>
internal sealed class SvgOp : DrawOp
{
    public float X, Y, W, H;
    public SvgImage Image;
    /// <summary>
    /// An SVG file in an img element without a viewBox: Chrome scales its user space to the box on each axis the file
    /// gives a natural width or height for, as if that were the viewBox with preserveAspectRatio="none".
    /// </summary>
    public bool StretchUserSpace;

    public SvgOp(float x, float y, float w, float h, SvgImage image)
    {
        X = x; Y = y; W = w; H = h;
        Image = image;
    }

    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>A link annotation over the box (X, Y, W, H).</summary>
internal sealed class LinkOp : DrawOp
{
    public float X, Y, W, H;
    /// <summary>An absolute URI, or "#id" for an internal destination.</summary>
    public string Target;
    /// <summary>In a tagged PDF, the Link element the annotation belongs to.</summary>
    public StructElem? Link;

    public LinkOp(float x, float y, float w, float h, string target)
    {
        X = x; Y = y; W = w; H = h;
        Target = target;
    }

    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>A filled or stroked circle (list markers).</summary>
internal sealed class CircleOp : DrawOp
{
    public float Cx, Cy, R;
    public uint Color;
    public bool Filled;

    public CircleOp(float cx, float cy, float r, uint color, bool filled)
    {
        Cx = cx; Cy = cy; R = r;
        Color = color;
        Filled = filled;
    }

    public override void Offset(float dx, float dy) { Cx += dx; Cy += dy; }
}

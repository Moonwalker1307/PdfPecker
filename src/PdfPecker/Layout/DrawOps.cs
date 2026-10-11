using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Images;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// A paint instruction in "flow" coordinates: points from the top-left of the first page's content area,
/// with y growing downward and continuing across pages (page n covers y in [n*H, (n+1)*H)).
/// </summary>
internal abstract class DrawOp
{
    public abstract void Offset(float dx, float dy);
}

internal sealed class RectOp : DrawOp
{
    public float X, Y, W, H;
    public uint Color;
    public RectOp(float x, float y, float w, float h, uint color) { X = x; Y = y; W = w; H = h; Color = color; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>A stroked line for dashed or dotted borders.</summary>
internal sealed class LineOp : DrawOp
{
    public float X1, Y1, X2, Y2, Width;
    public uint Color;
    public bool Dotted;
    public LineOp(float x1, float y1, float x2, float y2, float width, uint color, bool dotted)
    { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Width = width; Color = color; Dotted = dotted; }
    public override void Offset(float dx, float dy) { X1 += dx; X2 += dx; Y1 += dy; Y2 += dy; }
}

internal sealed class TextOp : DrawOp
{
    public float X, Baseline, LineTop; // LineTop decides the page
    public StandardFont Font;
    public float Size;
    public uint Color;
    public string Text;
    public float WordSpacing, CharSpacing;

    public TextOp(float x, float baseline, float lineTop, StandardFont font, float size, uint color, string text, float wordSpacing, float charSpacing)
    {
        X = x; Baseline = baseline; LineTop = lineTop; Font = font; Size = size; Color = color; Text = text;
        WordSpacing = wordSpacing; CharSpacing = charSpacing;
    }

    public override void Offset(float dx, float dy) { X += dx; Baseline += dy; LineTop += dy; }
}

internal sealed class ImageOp : DrawOp
{
    public float X, Y, W, H;
    public PdfImage Image;
    public ImageOp(float x, float y, float w, float h, PdfImage image) { X = x; Y = y; W = w; H = h; Image = image; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>An inline SVG drawing scaled into the box (X, Y, W, H).</summary>
internal sealed class SvgOp : DrawOp
{
    public float X, Y, W, H;
    public SvgImage Image;
    public SvgOp(float x, float y, float w, float h, SvgImage image) { X = x; Y = y; W = w; H = h; Image = image; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

internal sealed class LinkOp : DrawOp
{
    public float X, Y, W, H;
    public string Target; // an absolute URI, or "#id" for an internal destination
    public LinkOp(float x, float y, float w, float h, string target) { X = x; Y = y; W = w; H = h; Target = target; }
    public override void Offset(float dx, float dy) { X += dx; Y += dy; }
}

/// <summary>A filled or stroked circle (list markers).</summary>
internal sealed class CircleOp : DrawOp
{
    public float Cx, Cy, R;
    public uint Color;
    public bool Filled;
    public CircleOp(float cx, float cy, float r, uint color, bool filled) { Cx = cx; Cy = cy; R = r; Color = color; Filled = filled; }
    public override void Offset(float dx, float dy) { Cx += dx; Cy += dy; }
}

/// <summary>A slot reserved before an element's content, filled with its background and borders once its size is known.</summary>
internal sealed class GroupOp : DrawOp
{
    public readonly List<DrawOp> Ops = new(2);
    public override void Offset(float dx, float dy) { foreach (var o in Ops) o.Offset(dx, dy); }
}

internal static class BoxPainter
{
    /// <summary>Paints background and borders of a border box into <paramref name="group"/>.</summary>
    public static void Paint(GroupOp group, ComputedStyle s, float x, float y, float w, float h)
    {
        if (!s.Visible) return;
        if (CssColor.IsVisible(s.BackgroundColor) && w > 0 && h > 0)
            group.Ops.Add(new RectOp(x, y, w, h, s.BackgroundColor));
        if (!s.HasBorder) return;
        float bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom, bl = s.BorderLeft;
        Side(group, s.BorderTopStyle, bt, s.BorderTopColor ?? s.Color, x, y, w, bt, horizontal: true);
        Side(group, s.BorderBottomStyle, bb, s.BorderBottomColor ?? s.Color, x, y + h - bb, w, bb, horizontal: true);
        Side(group, s.BorderLeftStyle, bl, s.BorderLeftColor ?? s.Color, x, y, bl, h, horizontal: false);
        Side(group, s.BorderRightStyle, br, s.BorderRightColor ?? s.Color, x + w - br, y, br, h, horizontal: false);
    }

    public static void Side(GroupOp group, BorderStyle style, float width, uint color, float x, float y, float w, float h, bool horizontal)
    {
        if (width <= 0 || style == BorderStyle.None || !CssColor.IsVisible(color)) return;
        switch (style)
        {
            case BorderStyle.Dashed:
            case BorderStyle.Dotted:
                if (horizontal) group.Ops.Add(new LineOp(x, y + h / 2, x + w, y + h / 2, h, color, style == BorderStyle.Dotted));
                else group.Ops.Add(new LineOp(x + w / 2, y, x + w / 2, y + h, w, color, style == BorderStyle.Dotted));
                break;
            case BorderStyle.Double when width >= 2.25f:
                if (horizontal)
                {
                    float t = h / 3;
                    group.Ops.Add(new RectOp(x, y, w, t, color));
                    group.Ops.Add(new RectOp(x, y + h - t, w, t, color));
                }
                else
                {
                    float t = w / 3;
                    group.Ops.Add(new RectOp(x, y, t, h, color));
                    group.Ops.Add(new RectOp(x + w - t, y, t, h, color));
                }
                break;
            default:
                group.Ops.Add(new RectOp(x, y, w, h, color));
                break;
        }
    }
}

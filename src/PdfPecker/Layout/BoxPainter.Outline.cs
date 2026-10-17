using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>The outline: a border-like ring outside the border box that takes no room in the layout.</summary>
internal static partial class BoxPainter
{
    /// <summary>
    /// Paints the outline around a border box: outside it, outline-offset away, following its rounded corners. It takes
    /// no room in the layout.
    /// </summary>
    public static void PaintOutline(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii)
    {
        float width = s.OutlineWidth, off = s.OutlineOffset;
        uint color = s.OutlineColor ?? s.Color;
        if (width <= 0 || !CssColor.IsVisible(color) || w <= 0 || h <= 0) return;
        float grow = off + width;
        float ox = x - grow, oy = y - grow, ow = w + 2 * grow, oh = h + 2 * grow;
        if (ow <= 0 || oh <= 0) return;
        var style = s.OutlineStyle;
        bool roomInside = 2 * width < ow && 2 * width < oh;
        if (radii is { } r)
        {
            PaintRoundedOutline(group, style, width, color, ox, oy, ow, oh, r.Outset(grow), roomInside);
            return;
        }
        if (style == BorderStyle.Solid && roomInside)
        {
            group.Ops.Add(new BorderRectOp(ox, oy, ow, oh, width, color));
            return;
        }
        Side(group, style, width, color, ox, oy, ow, width, horizontal: true, SideTop);
        Side(group, style, width, color, ox, oy + oh - width, ow, width, horizontal: true, SideBottom);
        Side(group, style, width, color, ox, oy, width, oh, horizontal: false, SideLeft);
        Side(group, style, width, color, ox + ow - width, oy, width, oh, horizontal: false, SideRight);
    }

    /// <summary>An outline with rounded corners: a dashed or dotted stroke, a double ring, or one ring.</summary>
    private static void PaintRoundedOutline(GroupOp group, BorderStyle style, float width, uint color, float ox, float oy, float ow, float oh,
        CornerRadii outer, bool roomInside)
    {
        if (style is BorderStyle.Dashed or BorderStyle.Dotted && roomInside)
        {
            group.Ops.Add(new RoundRectOp(ox, oy, ow, oh, outer, color) { StrokeWidth = width, Style = style });
            return;
        }
        float iw = Math.Max(0, ow - 2 * width), ih = Math.Max(0, oh - 2 * width);
        var inner = outer.Inset(width, width, width, width);
        if (style == BorderStyle.Double && width >= MinDoubleWidth)
        {
            // Two rings, each a third of the width, with the middle third left open.
            float t = width / 3;
            group.Ops.Add(new RoundRectOp(ox, oy, ow, oh, outer, color).WithHole(ox + t, oy + t, ow - 2 * t, oh - 2 * t, outer.Inset(t, t, t, t)));
            group.Ops.Add(new RoundRectOp(ox + 2 * t, oy + 2 * t, ow - 4 * t, oh - 4 * t, outer.Inset(2 * t, 2 * t, 2 * t, 2 * t) ?? default, color)
                .WithHole(ox + width, oy + width, iw, ih, inner));
            return;
        }
        group.Ops.Add(new RoundRectOp(ox, oy, ow, oh, outer, color).WithHole(ox + width, oy + width, iw, ih, inner));
    }
}

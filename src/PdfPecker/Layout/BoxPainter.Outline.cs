using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>The outline: a border-like ring outside the border box that takes no room in the layout.</summary>
internal static partial class BoxPainter
{
    /// <summary>
    /// outline-style: auto draws Chrome's focus ring whatever the outline's width: two pixels wide, centred on the
    /// outline-offset line, with corners rounded by four pixels where the box has none of its own.
    /// </summary>
    private const float FocusRingWidth = 2 * CssPixel, FocusRingRadius = 4 * CssPixel;

    /// <summary>
    /// Paints the outline around a border box: outside it, outline-offset away, following its rounded corners. It takes
    /// no room in the layout, and its sides are drawn as a border's are, so its corners and dashes match Chrome's.
    /// </summary>
    public static void PaintOutline(GroupOp group, ComputedStyle s, float x, float y, float w, float h, CornerRadii? radii)
    {
        uint color = s.OutlineColor ?? s.Color;
        float width = s.OutlineAuto ? FocusRingWidth : s.OutlineWidth, off = s.OutlineOffset;
        if (width <= 0 || !CssColor.IsVisible(color) || w <= 0 || h <= 0) return;
        float grow = s.OutlineAuto ? off + width / 2 : off + width;
        float ox = x - grow, oy = y - grow, ow = w + 2 * grow, oh = h + 2 * grow;
        if (ow <= 0 || oh <= 0) return;
        var style = s.OutlineStyle;
        bool roomInside = 2 * width < ow && 2 * width < oh;
        if (s.OutlineAuto)
        {
            var ring = radii?.Outset(grow) ?? new CornerRadii(FocusRingRadius, FocusRingRadius, FocusRingRadius, FocusRingRadius,
                FocusRingRadius, FocusRingRadius, FocusRingRadius, FocusRingRadius);
            ring = CornerRadii.Fit(ring.TLx, ring.TLy, ring.TRx, ring.TRy, ring.BRx, ring.BRy, ring.BLx, ring.BLy, ow, oh) ?? default;
            group.Ops.Add(new RoundRectOp(ox, oy, ow, oh, ring, color)
                .WithHole(ox + width, oy + width, Math.Max(0, ow - 2 * width), Math.Max(0, oh - 2 * width), ring.Inset(width, width, width, width)));
            return;
        }
        var sides = new BorderSides(new BorderSide(width, style, color));
        if (radii is { } r)
        {
            PaintRoundedBorder(group, sides, ox, oy, ow, oh, r.Outset(grow));
            return;
        }
        if (roomInside && (Is3D(style) || sides.Top.IsDoubled))
        {
            // Mitred corners, like a box's border in these styles.
            PaintRoundedBorder(group, sides, ox, oy, ow, oh, default);
            return;
        }
        if (style == BorderStyle.Solid && roomInside)
        {
            group.Ops.Add(new BorderRectOp(ox, oy, ow, oh, width, color));
            return;
        }
        bool trim = roomInside && SharesTranslucentCorners(sides);
        Side(group, style, width, color, ox, oy, ow, width, horizontal: true, SideTop);
        Side(group, style, width, color, ox, oy + oh - width, ow, width, horizontal: true, SideBottom);
        Side(group, style, width, color, ox, oy, width, oh, horizontal: false, SideLeft, trim);
        Side(group, style, width, color, ox + ow - width, oy, width, oh, horizontal: false, SideRight, trim);
    }
}

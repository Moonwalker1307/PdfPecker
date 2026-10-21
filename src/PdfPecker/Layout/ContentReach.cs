namespace PdfPecker.Layout;

/// <summary>
/// How far laid-out content reaches to the right, as Chrome measures a printed document's scrollable overflow: the
/// right edges of boxes, and of lines wider than their box, wherever they are positioned or translated to. Content
/// inside a box that clips its overflow counts only up to the clip, unless the box is the root element or the body,
/// whose overflow belongs to the viewport. Content beyond the left edge does not count.
/// </summary>
internal static class ContentReach
{
    /// <summary>The rightmost point the ops reach, or negative infinity when they hold no box.</summary>
    public static float Of(List<DrawOp> ops)
    {
        float reach = float.NegativeInfinity;
        Scan(ops, 1, 0, float.PositiveInfinity, ref reach);
        return reach;
    }

    /// <summary>
    /// Scans ops whose x maps to the page as <c>a * x + b</c>, painted only left of <paramref name="limit"/>.
    /// A transform is followed along the line through its origin, which is exact for translations and scales and an
    /// estimate for rotations and skews, whose reach depends on the box's height as well.
    /// </summary>
    private static void Scan(List<DrawOp> ops, float a, float b, float limit, ref float reach)
    {
        foreach (var op in ops)
        {
            if (op is not GroupOp g) continue;
            if (!float.IsNaN(g.Reach)) reach = Math.Max(reach, Math.Min(a * g.Reach + b, limit));
            float ca = a, cb = b, cLimit = limit;
            if (g is ClipOp { OfViewport: false } clip && clip.W < ClipOp.Far && a > 0) cLimit = Math.Min(limit, a * (clip.X + clip.W) + b);
            else if (g is LayerOp { Transform: { } m } layer)
            {
                // x' = ox + A (x - ox) + E on the line through the origin, y = oy.
                cb = a * (layer.OriginX - m.A * layer.OriginX + m.E) + b;
                ca = a * m.A;
            }
            Scan(g.Ops, ca, cb, cLimit, ref reach);
        }
    }
}

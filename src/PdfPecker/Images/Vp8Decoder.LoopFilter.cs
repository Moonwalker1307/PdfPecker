namespace PdfPecker.Images;

internal sealed partial class Vp8Decoder
{
    private enum LoopFilter
    {
        None,
        /// <summary>Only the two luma samples next to an edge change.</summary>
        Simple,
        /// <summary>Up to three samples on each side of an edge change, in all three planes.</summary>
        Normal,
    }

    // A macroblock edge is filtered with a limit this much higher than its inner edges.
    private const int MacroblockEdgeLimitBoost = 4;

    /// <summary>
    /// Filters the edges of one macroblock, in libwebp's order: the left edge, the inner vertical edges, the top edge
    /// and the inner horizontal edges. The picture is filtered after it is decoded, a macroblock at a time in raster
    /// order, so each filter sees the edges before it already filtered.
    /// </summary>
    private void FilterMacroblock(int mbX, int mbY, in FilterStrength f)
    {
        int limit = f.Limit;
        if (limit == 0) return;
        int edgeLimit = limit + MacroblockEdgeLimitBoost;
        Span<byte> luma = _y.Samples;
        int yStride = _y.Stride, y = mbY * 16 * yStride + mbX * 16;
        if (_filter == LoopFilter.Simple)
        {
            if (mbX > 0) SimpleFilter16(luma, y, 1, yStride, edgeLimit);
            if (f.FilterInnerEdges)
                for (int k = 4; k < 16; k += 4) SimpleFilter16(luma, y + k, 1, yStride, limit);
            if (mbY > 0) SimpleFilter16(luma, y, yStride, 1, edgeLimit);
            if (f.FilterInnerEdges)
                for (int k = 4; k < 16; k += 4) SimpleFilter16(luma, y + k * yStride, yStride, 1, limit);
            return;
        }

        Span<byte> u = _u.Samples, v = _v.Samples;
        int uvStride = _u.Stride, uv = mbY * 8 * uvStride + mbX * 8;
        int inner = f.InnerLimit, hev = f.HighEdgeVarianceThreshold;
        if (mbX > 0)
        {
            FilterEdge(luma, y, 1, yStride, 16, edgeLimit, inner, hev, isMacroblockEdge: true);
            FilterEdge(u, uv, 1, uvStride, 8, edgeLimit, inner, hev, isMacroblockEdge: true);
            FilterEdge(v, uv, 1, uvStride, 8, edgeLimit, inner, hev, isMacroblockEdge: true);
        }
        if (f.FilterInnerEdges)
        {
            for (int k = 4; k < 16; k += 4) FilterEdge(luma, y + k, 1, yStride, 16, limit, inner, hev, isMacroblockEdge: false);
            FilterEdge(u, uv + 4, 1, uvStride, 8, limit, inner, hev, isMacroblockEdge: false);
            FilterEdge(v, uv + 4, 1, uvStride, 8, limit, inner, hev, isMacroblockEdge: false);
        }
        if (mbY > 0)
        {
            FilterEdge(luma, y, yStride, 1, 16, edgeLimit, inner, hev, isMacroblockEdge: true);
            FilterEdge(u, uv, uvStride, 1, 8, edgeLimit, inner, hev, isMacroblockEdge: true);
            FilterEdge(v, uv, uvStride, 1, 8, edgeLimit, inner, hev, isMacroblockEdge: true);
        }
        if (f.FilterInnerEdges)
        {
            for (int k = 4; k < 16; k += 4) FilterEdge(luma, y + k * yStride, yStride, 1, 16, limit, inner, hev, isMacroblockEdge: false);
            FilterEdge(u, uv + 4 * uvStride, uvStride, 1, 8, limit, inner, hev, isMacroblockEdge: false);
            FilterEdge(v, uv + 4 * uvStride, uvStride, 1, 8, limit, inner, hev, isMacroblockEdge: false);
        }
    }

    private static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    private static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    /// <summary>
    /// The simple filter across the 16 luma samples of an edge that starts at <paramref name="p"/>:
    /// <paramref name="step"/> crosses the edge, <paramref name="along"/> runs along it.
    /// </summary>
    private static void SimpleFilter16(Span<byte> plane, int p, int step, int along, int limit)
    {
        int threshold = 2 * limit + 1;
        for (int i = 0; i < 16; i++, p += along)
            if (EdgeDifference(plane, p, step) <= threshold) FilterCommon(plane, p, step);
    }

    /// <summary>How much the samples change across the edge, weighting the two next to it four times.</summary>
    private static int EdgeDifference(ReadOnlySpan<byte> plane, int p, int step)
    {
        int p1 = plane[p - 2 * step], p0 = plane[p - step], q0 = plane[p], q1 = plane[p + step];
        return 4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1);
    }

    /// <summary>
    /// The normal filter across an edge of <paramref name="size"/> samples. It leaves an edge whose samples change
    /// too much (a real edge in the picture) alone; where they change a lot right at the edge (high edge variance)
    /// it adjusts only the two samples next to it, elsewhere three on each side of a macroblock edge, two of an inner one.
    /// </summary>
    private static void FilterEdge(Span<byte> plane, int p, int step, int along, int size, int limit, int innerLimit, int hevThreshold, bool isMacroblockEdge)
    {
        int threshold = 2 * limit + 1;
        for (int i = 0; i < size; i++, p += along)
        {
            int p3 = plane[p - 4 * step], p2 = plane[p - 3 * step], p1 = plane[p - 2 * step], p0 = plane[p - step];
            int q0 = plane[p], q1 = plane[p + step], q2 = plane[p + 2 * step], q3 = plane[p + 3 * step];
            if (4 * Math.Abs(p0 - q0) + Math.Abs(p1 - q1) > threshold) continue;
            if (Math.Abs(p3 - p2) > innerLimit || Math.Abs(p2 - p1) > innerLimit || Math.Abs(p1 - p0) > innerLimit
                || Math.Abs(q3 - q2) > innerLimit || Math.Abs(q2 - q1) > innerLimit || Math.Abs(q1 - q0) > innerLimit) continue;
            if (Math.Abs(p1 - p0) > hevThreshold || Math.Abs(q1 - q0) > hevThreshold) FilterCommon(plane, p, step);
            else if (isMacroblockEdge) FilterMacroblockEdge(plane, p, step);
            else FilterInnerEdge(plane, p, step);
        }
    }

    /// <summary>Moves the two samples next to the edge towards each other.</summary>
    private static void FilterCommon(Span<byte> plane, int p, int step)
    {
        int p1 = plane[p - 2 * step], p0 = plane[p - step], q0 = plane[p], q1 = plane[p + step];
        int a = 3 * (q0 - p0) + SClip1(p1 - q1);
        int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3);
        plane[p - step] = Clip8(p0 + a2);
        plane[p] = Clip8(q0 - a1);
    }

    /// <summary>Adjusts two samples on each side of an inner edge, the outer ones half as much.</summary>
    private static void FilterInnerEdge(Span<byte> plane, int p, int step)
    {
        int p1 = plane[p - 2 * step], p0 = plane[p - step], q0 = plane[p], q1 = plane[p + step];
        int a = 3 * (q0 - p0);
        int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3), a3 = (a1 + 1) >> 1;
        plane[p - 2 * step] = Clip8(p1 + a3);
        plane[p - step] = Clip8(p0 + a2);
        plane[p] = Clip8(q0 - a1);
        plane[p + step] = Clip8(q1 - a3);
    }

    /// <summary>Adjusts three samples on each side of a macroblock edge, by 27, 18 and 9 parts of the difference.</summary>
    private static void FilterMacroblockEdge(Span<byte> plane, int p, int step)
    {
        int p2 = plane[p - 3 * step], p1 = plane[p - 2 * step], p0 = plane[p - step];
        int q0 = plane[p], q1 = plane[p + step], q2 = plane[p + 2 * step];
        int a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
        int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
        plane[p - 3 * step] = Clip8(p2 + a3);
        plane[p - 2 * step] = Clip8(p1 + a2);
        plane[p - step] = Clip8(p0 + a1);
        plane[p] = Clip8(q0 - a1);
        plane[p + step] = Clip8(q1 - a2);
        plane[p + 2 * step] = Clip8(q2 - a3);
    }
}

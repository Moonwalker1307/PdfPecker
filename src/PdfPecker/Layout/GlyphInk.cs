using System.Runtime.CompilerServices;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Layout;

/// <summary>
/// Where a glyph's outline crosses a horizontal band, for text-decoration-skip-ink: the left and right ends of its ink
/// between two heights, as Skia's text intercepts give them to Chrome. The outlines come from the renderer's font
/// readers and are flattened once per glyph.
/// </summary>
internal static class GlyphInk
{
    // Curves are flattened to a thousandth of an em, finer than any gap in a decoration line shows.
    private const float FlattenTolerance = 0.001f;

    private static readonly ConditionalWeakTable<FontProgram, Outlines> Fonts = new();

    /// <summary>A font's outline reader and the glyphs flattened so far, shared by conversions running in parallel.</summary>
    private sealed class Outlines
    {
        private readonly IGlyphSource? _source;
        private readonly Dictionary<int, float[]?> _glyphs = new();
        private readonly FlatPath _flat = new();

        public Outlines(FontProgram program)
        {
            var tables = program.Tables.Select(t => (TagName(t.Tag), t.Offset, t.Length));
            var trueType = TrueTypeFont.Parse(program.Data, tables);
            _source = trueType?.CffTable is { } cff ? CffFont.Parse(cff) : trueType;
        }

        /// <summary>The glyph's contours in ems, y up: x and y pairs, with a pair of NaNs after each contour.</summary>
        public float[]? Glyph(int gid)
        {
            lock (_glyphs)
            {
                if (_glyphs.TryGetValue(gid, out var points)) return points;
                points = Flatten(gid);
                _glyphs[gid] = points;
                return points;
            }
        }

        private float[]? Flatten(int gid)
        {
            if (_source == null || gid < 0 || gid >= _source.GlyphCount) return null;
            PathData? path;
            try
            {
                path = _source.GetGlyph(gid);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
            {
                return null;
            }
            if (path == null || path.IsEmpty) return null;
            _flat.Flatten(path, _source.FontMatrix, FlattenTolerance);
            var points = new List<float>(_flat.Count * 2 + _flat.ContourCount * 2);
            for (int c = 0; c < _flat.ContourCount; c++)
            {
                for (int p = _flat.Starts[c], end = _flat.ContourEnd(c); p < end; p++)
                {
                    points.Add(_flat.Pts[p * 2]);
                    points.Add(_flat.Pts[p * 2 + 1]);
                }
                points.Add(float.NaN);
                points.Add(float.NaN);
            }
            return points.ToArray();
        }

        private static string TagName(uint tag) => new([(char)(tag >> 24), (char)(tag >> 16 & 0xFF), (char)(tag >> 8 & 0xFF), (char)(tag & 0xFF)]);
    }

    /// <summary>
    /// The horizontal extent, in ems from the glyph's origin, of the ink of a glyph between the heights
    /// <paramref name="low"/> and <paramref name="high"/> (ems above the baseline), with the glyph slanted by
    /// <paramref name="skew"/> (a synthetic italic); false when no ink is in the band.
    /// </summary>
    public static bool Intercept(FontProgram program, int gid, float low, float high, float skew, out float left, out float right)
    {
        left = float.MaxValue;
        right = float.MinValue;
        var points = Fonts.GetValue(program, p => new Outlines(p)).Glyph(gid);
        if (points == null) return false;
        int first = 0;
        for (int i = 0; i + 1 < points.Length; i += 2)
        {
            if (!float.IsNaN(points[i])) continue;
            // Each contour is closed: its last point joins its first.
            for (int k = first; k < i; k += 2)
            {
                int next = k + 2 < i ? k + 2 : first;
                Clip(points[k], points[k + 1], points[next], points[next + 1], low, high, skew, ref left, ref right);
            }
            first = i + 2;
        }
        return left <= right;
    }

    /// <summary>
    /// The horizontal extent of a glyph's whole ink, in ems from its origin, as a glyph's bounding box gives it to
    /// Chrome; false when the glyph has no outline.
    /// </summary>
    public static bool Extent(FontProgram program, int gid, out float left, out float right)
        => Intercept(program, gid, float.NegativeInfinity, float.PositiveInfinity, 0, out left, out right);

    /// <summary>Widens [left, right] by the part of the edge from (x0, y0) to (x1, y1) that lies between the heights.</summary>
    private static void Clip(float x0, float y0, float x1, float y1, float low, float high, float skew, ref float left, ref float right)
    {
        if (Math.Max(y0, y1) < low || Math.Min(y0, y1) > high) return;
        float t0 = 0, t1 = 1;
        if (y1 != y0)
        {
            float ta = (low - y0) / (y1 - y0), tb = (high - y0) / (y1 - y0);
            t0 = Math.Clamp(Math.Min(ta, tb), 0, 1);
            t1 = Math.Clamp(Math.Max(ta, tb), 0, 1);
        }
        for (int e = 0; e < 2; e++)
        {
            float t = e == 0 ? t0 : t1;
            float y = y0 + (y1 - y0) * t;
            float x = x0 + (x1 - x0) * t + skew * y;
            if (x < left) left = x;
            if (x > right) right = x;
        }
    }
}

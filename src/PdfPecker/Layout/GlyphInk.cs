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

    // The standard fonts take the outlines of their bundled replacements, read straight from the font files: the full
    // embeddable font, with its character map and widths, would cost a first conversion with an underline more time
    // and memory than the outlines it needs.
    private static readonly Lazy<Outlines>[] StandardFonts = StandardFont.All.Select(sf => new Lazy<Outlines>(() => new Outlines(BundledFonts.Data(sf)))).ToArray();

    /// <summary>A glyph's contours in ems, y up (x and y pairs, with a pair of NaNs after each contour), and their lowest and highest y.</summary>
    private sealed class Contours(float[] points, float bottom, float top)
    {
        public readonly float[] Points = points;
        public readonly float Bottom = bottom, Top = top;
    }

    /// <summary>A font's outline reader and the glyphs flattened so far, shared by conversions running in parallel.</summary>
    private sealed class Outlines
    {
        private readonly IGlyphSource? _source;
        private readonly Dictionary<int, Contours?> _glyphs = new();
        private readonly FlatPath _flat = new();

        public Outlines(FontProgram program)
        {
            var tables = program.Tables.Select(t => (TagName(t.Tag), t.Offset, t.Length));
            var trueType = TrueTypeFont.Parse(program.Data, tables);
            _source = trueType?.CffTable is { } cff ? CffFont.Parse(cff) : trueType;
        }

        public Outlines(byte[]? data) => _source = data == null ? null : TrueTypeFont.Parse(data);

        /// <summary>The glyph of a character in the font's Unicode character map, 0 when it has none.</summary>
        public int GlyphId(char c) => _source is TrueTypeFont trueType ? trueType.Lookup(3, 1, c) : 0;

        public Contours? Glyph(int gid)
        {
            lock (_glyphs)
            {
                if (_glyphs.TryGetValue(gid, out var contours)) return contours;
                contours = Flatten(gid);
                _glyphs[gid] = contours;
                return contours;
            }
        }

        private Contours? Flatten(int gid)
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
            var points = new float[(_flat.Count + _flat.ContourCount) * 2];
            float bottom = float.MaxValue, top = float.MinValue;
            int n = 0;
            for (int c = 0; c < _flat.ContourCount; c++)
            {
                for (int p = _flat.Starts[c], end = _flat.ContourEnd(c); p < end; p++)
                {
                    float x = _flat.Pts[p * 2], y = _flat.Pts[p * 2 + 1];
                    points[n++] = x;
                    points[n++] = y;
                    if (y < bottom) bottom = y;
                    if (y > top) top = y;
                }
                points[n++] = float.NaN;
                points[n++] = float.NaN;
            }
            if (n < points.Length) Array.Resize(ref points, n);
            return new Contours(points, bottom, top);
        }

        private static string TagName(uint tag) => new([(char)(tag >> 24), (char)(tag >> 16 & 0xFF), (char)(tag >> 8 & 0xFF), (char)(tag & 0xFF)]);
    }

    /// <summary>
    /// The horizontal extent, in ems from the glyph's origin, of the ink of a glyph between the heights
    /// <paramref name="low"/> and <paramref name="high"/> (ems above the baseline), with the glyph slanted by
    /// <paramref name="skew"/> (a synthetic italic); false when no ink is in the band.
    /// </summary>
    public static bool Intercept(FontProgram program, int gid, float low, float high, float skew, out float left, out float right)
        => Intercept(Fonts.GetValue(program, p => new Outlines(p)).Glyph(gid), low, high, skew, out left, out right);

    /// <summary>
    /// <see cref="Intercept(FontProgram, int, float, float, float, out float, out float)"/> for a character in a standard
    /// font, with the outlines of the font's bundled replacement, which has the same widths.
    /// </summary>
    public static bool Intercept(StandardFont font, char c, float low, float high, float skew, out float left, out float right)
    {
        var outlines = StandardFonts[font.Index].Value;
        return Intercept(outlines.Glyph(outlines.GlyphId(c)), low, high, skew, out left, out right);
    }

    private static bool Intercept(Contours? contours, float low, float high, float skew, out float left, out float right)
    {
        left = float.MaxValue;
        right = float.MinValue;
        // A glyph that stays above or below the band has no edge in it: most glyphs stay above an underline.
        if (contours == null || contours.Top < low || contours.Bottom > high) return false;
        var points = contours.Points;
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

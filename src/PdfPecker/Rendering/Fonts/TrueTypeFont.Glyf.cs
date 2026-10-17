using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>The glyf table: simple glyph outlines, composite glyphs and the conversion of quadratic contours to cubics.</summary>
internal sealed partial class TrueTypeFont
{
    private const int GlyphHeaderSize = 10; // numberOfContours and the bounding box

    // The flags of a simple glyph's points.
    private const byte OnCurvePoint = 1, XShortVector = 2, YShortVector = 4, RepeatFlag = 8, XIsSameOrPositive = 16, YIsSameOrPositive = 32;

    // The flags of a composite glyph's components.
    private const int Arg1And2AreWords = 1, ArgsAreXYValues = 2, WeHaveAScale = 8, MoreComponents = 0x20, WeHaveAnXAndYScale = 0x40, WeHaveATwoByTwo = 0x80;
    private const double F2Dot14One = 16384.0;

    /// <summary>Composite glyphs nest at most this deep; the budget guards against exponential nesting below that.</summary>
    private const int MaxCompositeDepth = 8;
    private const int ComponentBudgetPerGlyph = 256;

    /// <summary>A composite glyph reads at most this many components.</summary>
    private const int MaxComponents = 64;

    public bool HasGlyf => _glyf >= 0 && _loca.Length > 1;

    private readonly record struct Component(int Gid, Matrix Transform);

    /// <summary>The points of a simple glyph, with the index of the last point of each contour.</summary>
    private readonly struct SimpleGlyph(int[] xs, int[] ys, bool[] onCurve, int[] contourEnds)
    {
        public int[] Xs => xs;
        public int[] Ys => ys;
        public bool[] OnCurve => onCurve;
        public int ContourCount => contourEnds.Length;

        /// <summary>
        /// The last point of a contour, clamped to the points that exist; a contour whose declared end lies before its
        /// first point is empty (last &lt; first).
        /// </summary>
        public int LastPoint(int contour) => Math.Min(contourEnds[contour], xs.Length - 1);
    }

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _glyphCache.Length || !HasGlyf) return null;
        var cached = Volatile.Read(ref _glyphCache[gid]);
        if (cached != null) return cached;
        var path = new PathData();
        try
        {
            int budget = ComponentBudgetPerGlyph;
            AppendGlyph(path, gid, Matrix.Identity, 0, ref budget);
        }
        catch (IndexOutOfRangeException)
        {
            path.Clear();
        }
        Volatile.Write(ref _glyphCache[gid], path);
        return path;
    }

    /// <summary>Glyph contours as raw TrueType points (for the font pack generator).</summary>
    public bool TryGetRawContours(int gid, List<List<(int X, int Y, bool On)>> contours)
        => RawGlyph(gid, Matrix.Identity, 0, contours);

    /// <summary>
    /// The glyf offset of a glyph's data; false when the glyph is empty or starts past the table. A damaged loca entry
    /// can give an offset before the file, which the reads then reject.
    /// </summary>
    private bool TryGetGlyphData(int gid, out int at)
    {
        int start = _loca[gid], end = _loca[gid + 1];
        at = _glyf + start;
        return end > start && start < _glyfLength;
    }

    private bool RawGlyph(int gid, Matrix transform, int depth, List<List<(int X, int Y, bool On)>> output)
    {
        if (depth > MaxCompositeDepth || gid < 0 || gid + 1 >= _loca.Length) return false;
        if (!TryGetGlyphData(gid, out int at)) return true;
        int contourCount = _font.S16(at);
        if (contourCount < 0)
        {
            foreach (var component in ReadComponents(at))
                RawGlyph(component.Gid, component.Transform.Multiply(transform), depth + 1, output);
            return true;
        }
        var glyph = ReadSimpleGlyph(at, contourCount);
        int first = 0;
        for (int contour = 0; contour < glyph.ContourCount; contour++)
        {
            var points = new List<(int, int, bool)>();
            int last = glyph.LastPoint(contour);
            for (int p = first; p <= last; p++)
            {
                var (x, y) = transform.Apply(glyph.Xs[p], glyph.Ys[p]);
                points.Add(((int)Math.Round(x), (int)Math.Round(y), glyph.OnCurve[p]));
            }
            output.Add(points);
            if (last >= first) first = last + 1;
        }
        return true;
    }

    private void AppendGlyph(PathData path, int gid, Matrix transform, int depth, ref int budget)
    {
        if (depth > MaxCompositeDepth || --budget < 0 || gid < 0 || gid + 1 >= _loca.Length) return;
        if (!TryGetGlyphData(gid, out int at)) return;
        int contourCount = _font.S16(at);
        if (contourCount < 0)
        {
            foreach (var component in ReadComponents(at))
                AppendGlyph(path, component.Gid, component.Transform.Multiply(transform), depth + 1, ref budget);
            return;
        }
        var glyph = ReadSimpleGlyph(at, contourCount);
        int first = 0;
        for (int contour = 0; contour < glyph.ContourCount; contour++)
        {
            int last = glyph.LastPoint(contour);
            if (last < first) continue; // malformed (non-increasing) end point: skip, never revisit points
            AddContour(path, glyph.Xs, glyph.Ys, glyph.OnCurve, first, last, transform);
            first = last + 1;
        }
    }

    // Reads all the component records before any component is drawn, so that a damaged record loses the whole glyph
    // rather than part of it.
    private List<Component> ReadComponents(int at)
    {
        var data = Data;
        int p = at + GlyphHeaderSize;
        var components = new List<Component>();
        while (true)
        {
            int flags = _font.U16(p), gid = _font.U16(p + 2);
            p += 4;
            double dx, dy;
            if ((flags & Arg1And2AreWords) != 0)
            {
                dx = _font.S16(p);
                dy = _font.S16(p + 2);
                p += 4;
            }
            else
            {
                dx = (sbyte)data[p];
                dy = (sbyte)data[p + 1];
                p += 2;
            }
            if ((flags & ArgsAreXYValues) == 0) dx = dy = 0; // point matching: not supported, place at origin
            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & WeHaveAScale) != 0)
            {
                a = d = F2Dot14(p);
                p += 2;
            }
            else if ((flags & WeHaveAnXAndYScale) != 0)
            {
                a = F2Dot14(p);
                d = F2Dot14(p + 2);
                p += 4;
            }
            else if ((flags & WeHaveATwoByTwo) != 0)
            {
                a = F2Dot14(p);
                b = F2Dot14(p + 2);
                c = F2Dot14(p + 4);
                d = F2Dot14(p + 6);
                p += 8;
            }
            components.Add(new Component(gid, new Matrix(a, b, c, d, dx, dy)));
            if ((flags & MoreComponents) == 0 || components.Count > MaxComponents) break;
        }
        return components;
    }

    private double F2Dot14(int at) => _font.S16(at) / F2Dot14One;

    private SimpleGlyph ReadSimpleGlyph(int at, int contourCount)
    {
        var data = Data;
        var contourEnds = new int[contourCount];
        int p = at + GlyphHeaderSize;
        for (int i = 0; i < contourCount; i++, p += 2) contourEnds[i] = _font.U16(p);
        int pointCount = contourCount == 0 ? 0 : contourEnds[contourCount - 1] + 1;
        int instructionLength = _font.U16(p);
        p += 2 + instructionLength;

        var flags = new byte[pointCount];
        for (int i = 0; i < pointCount;)
        {
            byte flag = data[p++];
            flags[i++] = flag;
            if ((flag & RepeatFlag) != 0)
            {
                int repeats = data[p++];
                for (int k = 0; k < repeats && i < pointCount; k++) flags[i++] = flag;
            }
        }
        var xs = new int[pointCount];
        var ys = new int[pointCount];
        var onCurve = new bool[pointCount];
        p = ReadCoordinates(p, flags, XShortVector, XIsSameOrPositive, xs);
        for (int i = 0; i < pointCount; i++) onCurve[i] = (flags[i] & OnCurvePoint) != 0;
        ReadCoordinates(p, flags, YShortVector, YIsSameOrPositive, ys);
        return new SimpleGlyph(xs, ys, onCurve, contourEnds);
    }

    // Coordinates are deltas: one unsigned byte with its sign in a flag, a signed word, or none when the flag says
    // "same". Returns the position after them.
    private int ReadCoordinates(int p, byte[] flags, byte shortVector, byte sameOrPositive, int[] values)
    {
        var data = Data;
        int value = 0;
        for (int i = 0; i < flags.Length; i++)
        {
            byte flag = flags[i];
            if ((flag & shortVector) != 0)
            {
                int delta = data[p++];
                value += (flag & sameOrPositive) != 0 ? delta : -delta;
            }
            else if ((flag & sameOrPositive) == 0)
            {
                value += _font.S16(p);
                p += 2;
            }
            values[i] = value;
        }
        return p;
    }

    /// <summary>Adds one quadratic TrueType contour as cubic segments.</summary>
    public static void AddContour(PathData path, int[] xs, int[] ys, bool[] on, int first, int last, in Matrix m)
    {
        int n = last - first + 1;
        if (n < 2) return;
        int firstOnCurve = -1;
        for (int i = 0; i < n; i++)
        {
            if (on[first + i])
            {
                firstOnCurve = i;
                break;
            }
        }
        double startX, startY;
        int walkFrom;
        if (firstOnCurve < 0)
        {
            // No on-curve point: start at the implied point between the first two control points.
            startX = (xs[first] + xs[first + 1]) / 2.0;
            startY = (ys[first] + ys[first + 1]) / 2.0;
            walkFrom = 1;
        }
        else
        {
            startX = xs[first + firstOnCurve];
            startY = ys[first + firstOnCurve];
            walkFrom = firstOnCurve + 1;
        }
        var (mx, my) = m.Apply(startX, startY);
        path.Move(mx, my);
        double curX = startX, curY = startY, ctrlX = 0, ctrlY = 0;
        bool pending = false;
        int steps = firstOnCurve < 0 ? n : n - 1;
        for (int k = 0; k < steps; k++)
        {
            int i = first + (walkFrom + k) % n;
            double x = xs[i], y = ys[i];
            if (on[i])
            {
                if (pending)
                {
                    Quad(path, curX, curY, ctrlX, ctrlY, x, y, m);
                }
                else
                {
                    var (tx, ty) = m.Apply(x, y);
                    path.Line(tx, ty);
                }
                curX = x;
                curY = y;
                pending = false;
            }
            else
            {
                if (pending)
                {
                    // Two control points in a row imply an on-curve point midway between them.
                    double midX = (ctrlX + x) / 2, midY = (ctrlY + y) / 2;
                    Quad(path, curX, curY, ctrlX, ctrlY, midX, midY, m);
                    curX = midX;
                    curY = midY;
                }
                ctrlX = x;
                ctrlY = y;
                pending = true;
            }
        }
        if (pending) Quad(path, curX, curY, ctrlX, ctrlY, startX, startY, m);
        path.ClosePath();
    }

    // A quadratic segment as the cubic with the same shape.
    private static void Quad(PathData path, double x0, double y0, double qx, double qy, double x1, double y1, in Matrix m)
    {
        double c1x = x0 + 2.0 / 3 * (qx - x0), c1y = y0 + 2.0 / 3 * (qy - y0);
        double c2x = x1 + 2.0 / 3 * (qx - x1), c2y = y1 + 2.0 / 3 * (qy - y1);
        var (ax, ay) = m.Apply(c1x, c1y);
        var (bx, by) = m.Apply(c2x, c2y);
        var (ex, ey) = m.Apply(x1, y1);
        path.Cubic(ax, ay, bx, by, ex, ey);
    }
}

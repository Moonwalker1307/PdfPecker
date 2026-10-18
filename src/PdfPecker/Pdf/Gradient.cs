using PdfPecker.Css;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

internal enum GradientSpread : byte { Pad, Reflect, Repeat }

/// <summary>The geometry of a gradient: along a line, between two circles, or around a centre.</summary>
internal enum GradientKind : byte { Linear, Radial, Conic }

/// <summary>What a shading paints: the colours flattened on their backdrop, the unblended colours, or the alphas.</summary>
internal enum GradientChannel : byte { Flattened, Straight, Alpha }

/// <summary>
/// A resolved linear, radial or conic gradient, written as a PDF shading pattern. Geometry is in gradient space;
/// <see cref="Transform"/> maps it into the space of the shape it paints.
/// </summary>
internal sealed class Gradient
{
    // A repeating gradient is tiled over at most this many periods, and with at most this many pieces of its colour
    // function in all (periods times the pieces of one period); past that it is painted once, padded.
    private const int MaxTiledPeriods = 256;
    private const int MaxTiledPieces = 16_384;
    // The domain of a conic gradient's calculator function, in gradient space; far larger than any page.
    private const string ConicDomain = "-100000 100000 -100000 100000";
    // Below this sum of |dx| and |dy| a point counts as the centre of a conic gradient, where atan is undefined.
    private const string ConicCentreTolerance = "0.0001";

    public GradientKind Kind;
    public bool Radial => Kind == GradientKind.Radial;
    public bool Conic => Kind == GradientKind.Conic;
    // Linear: from (X0, Y0) to (X1, Y1). Radial: from the focal circle (X0, Y0, R0) to the end circle (X1, Y1, R1).
    // Conic: centred on (X0, Y0).
    public float X0, Y0, R0, X1, Y1, R1;
    /// <summary>
    /// The start angle of a conic gradient: the colour depends on the angle around (X0, Y0), clockwise from this many
    /// degrees (0 is up). PDF has no such shading, so it is written as a function shading whose PostScript calculator function
    /// computes the angle, as Chrome does; the function is a stream object numbered <see cref="FunctionId"/> (and
    /// <see cref="AlphaFunctionId"/> for the alphas of translucent stops).
    /// </summary>
    public float From;
    // Repeating conic gradients: the stops cover [RepeatStart, RepeatStart + RepeatPeriod] of the turn, repeated.
    public float RepeatStart, RepeatPeriod;
    public int FunctionId, AlphaFunctionId;
    public float[] Offsets = Array.Empty<float>(); // ascending, in [0, 1]
    public uint[] Colors = Array.Empty<uint>();    // opaque 0xFFRRGGBB
    // Translucent stops, kept apart for painting with a soft mask: the stop colours unblended, and their alphas.
    // Null when every stop is opaque.
    public uint[]? StraightColors;
    public uint[]? Alphas;
    public GradientSpread Spread;
    public Matrix Transform = Matrix.Identity;

    /// <summary>Sorts out stop offsets: clamps them to [0, 1], makes them non-decreasing and flattens colours on a backdrop.</summary>
    public static (float[] Offsets, uint[] Colors) NormalizeStops(List<(float Offset, uint Color)> stops, uint backdrop)
    {
        var offsets = new float[stops.Count];
        var colors = new uint[stops.Count];
        float last = 0;
        for (int i = 0; i < stops.Count; i++)
        {
            last = offsets[i] = Math.Max(last, Math.Clamp(stops[i].Offset, 0, 1));
            colors[i] = Flatten(stops[i].Color, backdrop);
        }
        return (offsets, colors);
    }

    /// <summary>
    /// Keeps translucent stops for a soft mask: <see cref="StraightColors"/> and <see cref="Alphas"/>. A fully transparent
    /// stop takes the colour of its nearest visible neighbour, so that fading to transparent does not pass through
    /// black (browsers interpolate premultiplied colours).
    /// </summary>
    public void KeepAlpha(List<(float Offset, uint Color)> stops)
    {
        bool translucent = false;
        foreach (var s in stops) if (s.Color >> 24 != 0xFF) translucent = true;
        if (!translucent || stops.Count != Offsets.Length) return;
        int n = stops.Count;
        var colors = new uint[n];
        var alphas = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint c = stops[i].Color;
            alphas[i] = c >> 24;
            if (alphas[i] == 0)
            {
                int best = -1;
                for (int d = 1; d < n && best < 0; d++)
                {
                    if (i - d >= 0 && stops[i - d].Color >> 24 != 0) best = i - d;
                    else if (i + d < n && stops[i + d].Color >> 24 != 0) best = i + d;
                }
                if (best >= 0) c = stops[best].Color;
            }
            colors[i] = 0xFF000000 | (c & 0xFFFFFF);
        }
        StraightColors = colors;
        Alphas = alphas;
    }

    /// <summary>A copy with every stop colour mapped (a filter's colour functions); the alphas stay.</summary>
    public Gradient Recolored(Func<uint, uint> map)
    {
        var g = (Gradient)MemberwiseClone();
        g.FunctionId = g.AlphaFunctionId = 0;
        g.Colors = Colors.Select(map).ToArray();
        g.StraightColors = StraightColors?.Select(map).ToArray();
        return g;
    }

    /// <summary>
    /// Blends a translucent colour over an opaque backdrop. The writer paints translucent stops through a soft mask
    /// (<see cref="KeepAlpha"/>); the flattened colours are what the gradient is painted with otherwise.
    /// </summary>
    public static uint Flatten(uint c, uint backdrop)
    {
        uint a = c >> 24;
        if (a == 0xFF) return c;
        uint Blend(int shift) => ((c >> shift & 0xFF) * a + (backdrop >> shift & 0xFF) * (255 - a)) / 255;
        return 0xFF000000 | Blend(16) << 16 | Blend(8) << 8 | Blend(0);
    }

    // ------------------------------------------------------------------ PDF output

    /// <summary>One piece of a colour function: linear from <see cref="C0"/> at <see cref="From"/> to <see cref="C1"/> at <see cref="To"/>.</summary>
    private readonly record struct Segment(float From, float To, uint C0, uint C1);

    /// <summary>
    /// Writes a shading pattern dictionary. <paramref name="toPage"/> maps the painted shape's space to the page;
    /// the bounds, in that same space, decide how far a repeating gradient has to be tiled.
    /// </summary>
    public void WritePattern(Pdf.ByteBuffer b, Matrix toPage, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened, GradientTilingBudget? budget = null)
    {
        var m = Transform.Then(toPage);
        b.Ascii("<< /PatternType 2 /Matrix [").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
            .Ascii("] /Shading ");
        WriteShading(b, minX, minY, maxX, maxY, channel, budget);
        b.Ascii(" >>");
    }

    /// <summary>
    /// Writes the shading dictionary in gradient space (before <see cref="Transform"/>). The bounds are in the painted
    /// shape's space, as for <see cref="WritePattern"/>. A repeating gradient is tiled only while
    /// <paramref name="budget"/>, the document's, allows it.
    /// </summary>
    public void WriteShading(Pdf.ByteBuffer b, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened, GradientTilingBudget? budget = null)
    {
        var colors = channel switch
        {
            GradientChannel.Straight => StraightColors ?? Colors,
            GradientChannel.Alpha => Alphas ?? Colors,
            _ => Colors,
        };
        bool gray = channel == GradientChannel.Alpha && Alphas != null;
        if (Conic) WriteConicShading(b, minX, minY, maxX, maxY, gray);
        else WriteAxialShading(b, minX, minY, maxX, maxY, colors, gray, budget);
    }

    /// <summary>
    /// A conic gradient: a function shading over the painted bounds, a little padded, whose calculator function
    /// (<see cref="ConicFunction"/>, written as its own object) does the rest.
    /// </summary>
    private void WriteConicShading(Pdf.ByteBuffer b, float minX, float minY, float maxX, float maxY, bool gray)
    {
        // The domain is in gradient space: map the painted bounds back through the transform (a scaled slice of a
        // border image).
        var tm = Transform;
        if (!(tm.A == 1 && tm.B == 0 && tm.C == 0 && tm.D == 1 && tm.E == 0 && tm.F == 0) && tm.TryInvert(out var inv))
        {
            Span<(float X, float Y)> corners = stackalloc (float, float)[4];
            MapCorners(inv, minX, minY, maxX, maxY, corners);
            minX = minY = float.MaxValue;
            maxX = maxY = float.MinValue;
            foreach (var (x, y) in corners)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        b.Ascii(gray ? "<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [" : "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [")
            .Num(minX - 1).Sp().Num(maxX + 1).Sp().Num(minY - 1).Sp().Num(maxY + 1).Ascii("] /Function ")
            .Int(gray ? AlphaFunctionId : FunctionId).Ascii(" 0 R >>");
    }

    /// <summary>
    /// A linear (axial, type 2) or radial (type 3) shading. Repeat and reflect are written as one copy of the colour
    /// function per period the painted bounds cover, the geometry stretched to match.
    /// </summary>
    private void WriteAxialShading(Pdf.ByteBuffer b, float minX, float minY, float maxX, float maxY, uint[] colors, bool gray,
        GradientTilingBudget? budget)
    {
        b.Ascii("<< /ShadingType ").Int(Radial ? 3 : 2).Ascii(gray ? " /ColorSpace /DeviceGray /Coords [" : " /ColorSpace /DeviceRGB /Coords [");
        var (t0, t1) = TilingRange(minX, minY, maxX, maxY, colors, budget);
        float Lerp(float a, float c, float t) => a + (c - a) * t;
        if (Radial)
            b.Num(Lerp(X0, X1, t0)).Sp().Num(Lerp(Y0, Y1, t0)).Sp().Num(Math.Max(0, Lerp(R0, R1, t0))).Sp()
             .Num(Lerp(X0, X1, t1)).Sp().Num(Lerp(Y0, Y1, t1)).Sp().Num(Math.Max(0, Lerp(R0, R1, t1)));
        else
            b.Num(Lerp(X0, X1, t0)).Sp().Num(Lerp(Y0, Y1, t0)).Sp().Num(Lerp(X0, X1, t1)).Sp().Num(Lerp(Y0, Y1, t1));
        b.Ascii("] /Domain [").Num(t0).Sp().Num(t1).Ascii("] /Extend [true true] /Function ");
        if (t0 == 0 && t1 == 1) WriteFunction(b, colors, gray);
        else WriteTiledFunction(b, t0, t1, colors, gray);
        b.Ascii(" >>");
    }

    /// <summary>
    /// The whole periods [t0, t1] of the gradient parameter that the painted bounds cover: [0, 1] unless the gradient
    /// repeats or reflects and the tiling stays within <see cref="MaxTiledPeriods"/>, <see cref="MaxTiledPieces"/> and
    /// the document's <paramref name="budget"/>.
    /// </summary>
    private (float T0, float T1) TilingRange(float minX, float minY, float maxX, float maxY, uint[] colors, GradientTilingBudget? budget)
    {
        if (Spread == GradientSpread.Pad || ParameterRange(minX, minY, maxX, maxY) is not (var lo, var hi)) return (0, 1);
        float t0 = (float)Math.Floor(lo);
        float t1 = (float)Math.Ceiling(hi);
        if (Radial) t0 = Math.Max(t0, 0);
        if (t1 - t0 > MaxTiledPeriods || t1 <= t0) return (0, 1);
        long pieces = (long)(t1 - t0) * Segments(colors).Count;
        if (pieces > MaxTiledPieces || budget?.TrySpend(pieces) == false) return (0, 1);
        return (t0, t1);
    }

    /// <summary>A stitching function with one copy of the stops per period, mirrored on odd periods when reflecting.</summary>
    private void WriteTiledFunction(Pdf.ByteBuffer b, float t0, float t1, uint[] colors, bool gray)
    {
        b.Ascii("<< /FunctionType 3 /Domain [").Num(t0).Sp().Num(t1).Ascii("] /Functions [");
        for (float t = t0; t < t1; t++) WriteFunction(b, colors, gray);
        b.Ascii("] /Bounds [");
        for (float t = t0 + 1; t < t1; t++) b.Num(t).Sp();
        b.Ascii("] /Encode [");
        for (float t = t0; t < t1; t++)
        {
            bool mirrored = Spread == GradientSpread.Reflect && ((long)t & 1) != 0;
            b.Ascii(mirrored ? "1 0 " : "0 1 ");
        }
        b.Ascii("] >>");
    }

    /// <summary>
    /// The pieces of the colour function over [0, 1]: constant before the first stop and after the last, linear
    /// between consecutive stops. Zero-length pieces (hard colour changes) are dropped, and there is always one.
    /// </summary>
    private List<Segment> Segments(uint[] colors)
    {
        var segs = new List<Segment>(Offsets.Length + 1);
        if (Offsets[0] > 0) segs.Add(new(0, Offsets[0], colors[0], colors[0]));
        for (int i = 0; i + 1 < Offsets.Length; i++)
            if (Offsets[i + 1] > Offsets[i]) segs.Add(new(Offsets[i], Offsets[i + 1], colors[i], colors[i + 1]));
        if (Offsets[^1] < 1) segs.Add(new(Offsets[^1], 1, colors[^1], colors[^1]));
        if (segs.Count == 0) segs.Add(new(0, 1, colors[^1], colors[^1]));
        return segs;
    }

    /// <summary>
    /// The colour function over [0, 1], one exponential function per segment, stitched. Gray colours are alphas
    /// (0-255), one component.
    /// </summary>
    private void WriteFunction(Pdf.ByteBuffer b, uint[] colors, bool gray)
    {
        var segs = Segments(colors);
        if (segs.Count == 1)
        {
            Exponential(b, segs[0].C0, segs[0].C1, gray);
            return;
        }
        b.Ascii("<< /FunctionType 3 /Domain [0 1] /Functions [");
        foreach (var s in segs) Exponential(b, s.C0, s.C1, gray);
        b.Ascii("] /Bounds [");
        for (int i = 1; i < segs.Count; i++) b.Num6(segs[i].From).Sp();
        b.Ascii("] /Encode [");
        for (int i = 0; i < segs.Count; i++) b.Ascii("0 1 ");
        b.Ascii("] >>");
    }

    private static void Exponential(Pdf.ByteBuffer b, uint c0, uint c1, bool gray)
    {
        b.Ascii("<< /FunctionType 2 /Domain [0 1] /C0 [");
        if (gray) b.Num(c0 / 255f); else Rgb(b, c0);
        b.Ascii("] /C1 [");
        if (gray) b.Num(c1 / 255f); else Rgb(b, c1);
        b.Ascii("] /N 1 >>");
    }

    /// <summary>The dictionary entries of a conic gradient's function stream.</summary>
    public static string ConicFunctionDict(bool gray)
        => "/FunctionType 4 /Domain [" + ConicDomain + "] /Range [" + (gray ? "0 1" : "0 1 0 1 0 1") + "]";

    /// <summary>
    /// The PostScript calculator program of a conic gradient: (x, y) in gradient space, y down, to a colour (or, for
    /// <paramref name="gray"/>, the alpha).
    /// </summary>
    public byte[] ConicFunction(bool gray)
    {
        // Translucent stops are painted unblended through a soft mask holding the alphas (the gray function).
        var colors = gray ? Alphas! : StraightColors ?? Colors;
        var b = new Pdf.ByteBuffer(256);
        // Stack: x y. The angle of (x - cx, y - cy) clockwise from up is atan(x - cx, cy - y) in degrees; the centre
        // itself (atan of 0, 0 is an error) takes angle 0.
        b.Ascii("{ ").Num(Y0).Ascii(" exch sub exch ").Num(X0).Ascii(" sub exch 2 copy abs exch abs add ").Ascii(ConicCentreTolerance)
            .Ascii(" lt { pop pop 0 } { atan } ifelse ").Num(From).Ascii(" sub dup 0 lt { 360 add } if 360 div ");
        if (RepeatPeriod > 0) b.Num6(RepeatStart).Ascii(" sub ").Num6(RepeatPeriod).Ascii(" div dup floor sub ");

        // The same segments as for the other gradients, as nested ifelse tests on t, which is on the stack.
        var segs = Segments(colors);
        for (int i = 0; i < segs.Count; i++)
        {
            bool last = i == segs.Count - 1;
            if (!last) b.Ascii("dup ").Num6(segs[i].To).Ascii(" le { ");
            ConicSegment(b, segs[i], gray);
            if (!last) b.Ascii("} { ");
        }
        for (int i = 0; i < segs.Count - 1; i++) b.Ascii("} ifelse ");
        b.Ascii("}");
        return b.Span.ToArray();
    }

    /// <summary>The calculator code that replaces t on the stack by the colour of one segment at t.</summary>
    private static void ConicSegment(Pdf.ByteBuffer b, Segment seg, bool gray)
    {
        var (from, to, c0, c1) = seg;
        if (c0 == c1)
        {
            b.Ascii("pop ");
            Channels(b, c0, gray);
            return;
        }
        // s = (t - from) / (to - from), then each channel c0 + s * (c1 - c0).
        b.Num6(from).Ascii(" sub ").Num6(1 / (to - from)).Ascii(" mul ");
        if (gray)
        {
            b.Num6((c1 - (float)c0) / 255f).Ascii(" mul ").Num6(c0 / 255f).Ascii(" add ");
            return;
        }
        for (int shift = 16; shift >= 0; shift -= 8)
        {
            float v0 = Channel(c0, shift), v1 = Channel(c1, shift);
            if (shift > 0) b.Ascii("dup ");
            b.Num6(v1 - v0).Ascii(" mul ").Num6(v0).Ascii(" add ");
            if (shift > 0) b.Ascii("exch ");
        }
    }

    /// <summary>One 8-bit channel of a packed colour, as a fraction of 255.</summary>
    private static float Channel(uint c, int shift) => (c >> shift & 0xFF) / 255f;

    private static void Channels(Pdf.ByteBuffer b, uint c, bool gray)
    {
        if (gray) b.Num6(c / 255f).Sp();
        else b.Num6(Channel(c, 16)).Sp().Num6(Channel(c, 8)).Sp().Num6(Channel(c, 0)).Sp();
    }

    private static void Rgb(Pdf.ByteBuffer b, uint c)
        => b.Num(Channel(c, 16)).Sp().Num(Channel(c, 8)).Sp().Num(Channel(c, 0));

    /// <summary>The four corners of a box, clockwise from its top-left, mapped through <paramref name="m"/>.</summary>
    private static void MapCorners(Matrix m, float minX, float minY, float maxX, float maxY, Span<(float X, float Y)> corners)
    {
        corners[0] = (minX, minY);
        corners[1] = (maxX, minY);
        corners[2] = (maxX, maxY);
        corners[3] = (minX, maxY);
        foreach (ref var c in corners) m.Apply(ref c.X, ref c.Y);
    }

    /// <summary>
    /// The range of the gradient parameter over a box given in the painted shape's space; null when the box is empty
    /// or the geometry degenerate.
    /// </summary>
    private (float Lo, float Hi)? ParameterRange(float minX, float minY, float maxX, float maxY)
    {
        if (!Transform.TryInvert(out var inv) || !(maxX >= minX) || !(maxY >= minY)) return null;
        Span<(float X, float Y)> corners = stackalloc (float, float)[4];
        MapCorners(inv, minX, minY, maxX, maxY, corners);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var (x, y) in corners)
        {
            float t;
            if (Radial)
            {
                // Assumes the focal point at the centre, which is how repeating radial gradients are used.
                float dr = R1 - R0;
                if (dr <= 0) return null;
                float d = MathF.Sqrt((x - X1) * (x - X1) + (y - Y1) * (y - Y1));
                lo = Math.Min(lo, 0);
                t = (d - R0) / dr;
            }
            else
            {
                float dx = X1 - X0, dy = Y1 - Y0, len2 = dx * dx + dy * dy;
                if (len2 <= 0) return null;
                t = ((x - X0) * dx + (y - Y0) * dy) / len2;
            }
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return (lo, hi);
    }
}

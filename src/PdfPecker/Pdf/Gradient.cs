using PdfPecker.Css;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

internal enum GradientSpread : byte { Pad, Reflect, Repeat }

/// <summary>What a shading paints: the colours flattened on their backdrop, the unblended colours, or the alphas.</summary>
internal enum GradientChannel : byte { Flattened, Straight, Alpha }

/// <summary>
/// A resolved linear, radial or conic gradient, written as a PDF shading pattern. Geometry is in gradient space;
/// <see cref="Transform"/> maps it into the space of the shape it paints.
/// </summary>
internal sealed class Gradient
{
    public bool Radial;
    // Linear: from (X0, Y0) to (X1, Y1). Radial: from the focal circle (X0, Y0, R0) to the end circle (X1, Y1, R1).
    // Conic: centred on (X0, Y0).
    public float X0, Y0, R0, X1, Y1, R1;
    /// <summary>
    /// A conic gradient: the colour depends on the angle around (X0, Y0), clockwise from <see cref="From"/> degrees
    /// (0 is up). PDF has no such shading, so it is written as a function shading whose PostScript calculator function
    /// computes the angle, as Chrome does; the function is a stream object numbered <see cref="FunctionId"/> (and
    /// <see cref="AlphaFunctionId"/> for the alphas of translucent stops).
    /// </summary>
    public bool Conic;
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

    /// <summary>Blends a translucent colour over an opaque backdrop: the writer does not emit transparency.</summary>
    public static uint Flatten(uint c, uint backdrop)
    {
        uint a = c >> 24;
        if (a == 0xFF) return c;
        uint Blend(int shift) => ((c >> shift & 0xFF) * a + (backdrop >> shift & 0xFF) * (255 - a)) / 255;
        return 0xFF000000 | Blend(16) << 16 | Blend(8) << 8 | Blend(0);
    }

    // ------------------------------------------------------------------ PDF output

    /// <summary>
    /// Writes a shading pattern dictionary. <paramref name="toPage"/> maps the painted shape's space to the page;
    /// the bounds, in that same space, decide how far a repeating gradient has to be tiled.
    /// </summary>
    public void WritePattern(Pdf.ByteBuffer b, Matrix toPage, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened)
    {
        var m = Transform.Then(toPage);
        b.Ascii("<< /PatternType 2 /Matrix [").Num6(m.A).Sp().Num6(m.B).Sp().Num6(m.C).Sp().Num6(m.D).Sp().Num(m.E).Sp().Num(m.F)
            .Ascii("] /Shading ");
        WriteShading(b, minX, minY, maxX, maxY, channel);
        b.Ascii(" >>");
    }

    /// <summary>
    /// Writes the shading dictionary in gradient space (before <see cref="Transform"/>). The bounds are in the painted
    /// shape's space, as for <see cref="WritePattern"/>.
    /// </summary>
    public void WriteShading(Pdf.ByteBuffer b, float minX, float minY, float maxX, float maxY,
        GradientChannel channel = GradientChannel.Flattened)
    {
        var colors = channel switch
        {
            GradientChannel.Straight => StraightColors ?? Colors,
            GradientChannel.Alpha => Alphas ?? Colors,
            _ => Colors,
        };
        bool gray = channel == GradientChannel.Alpha && Alphas != null;
        if (Conic)
        {
            // The domain is in gradient space: map the painted bounds back through the transform (a scaled slice of
            // a border image).
            var tm = Transform;
            if (!(tm.A == 1 && tm.B == 0 && tm.C == 0 && tm.D == 1 && tm.E == 0 && tm.F == 0) && tm.TryInvert(out var inv))
            {
                float ax = minX, ay = minY, bx = maxX, by = maxY;
                minX = minY = float.MaxValue;
                maxX = maxY = float.MinValue;
                Span<float> xs = stackalloc float[] { ax, bx, bx, ax };
                Span<float> ys = stackalloc float[] { ay, ay, by, by };
                for (int k = 0; k < 4; k++)
                {
                    float px = xs[k], py = ys[k];
                    inv.Apply(ref px, ref py);
                    minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
                }
            }
            // A function shading over the painted bounds, a little padded; the function does the rest.
            b.Ascii(gray ? "<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [" : "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [")
                .Num(minX - 1).Sp().Num(maxX + 1).Sp().Num(minY - 1).Sp().Num(maxY + 1).Ascii("] /Function ")
                .Int(gray ? AlphaFunctionId : FunctionId).Ascii(" 0 R >>");
            return;
        }
        b.Ascii("<< /ShadingType ").Int(Radial ? 3 : 2).Ascii(gray ? " /ColorSpace /DeviceGray /Coords [" : " /ColorSpace /DeviceRGB /Coords [");

        // Tiling range: gradient parameter values t covered by the shape (repeat and reflect only).
        float t0 = 0, t1 = 1;
        if (Spread != GradientSpread.Pad && Range(minX, minY, maxX, maxY, out float lo, out float hi))
        {
            t0 = (float)Math.Floor(lo);
            t1 = (float)Math.Ceiling(hi);
            if (Radial) t0 = Math.Max(t0, 0);
            if (t1 - t0 > 256 || t1 <= t0) { t0 = 0; t1 = 1; }
        }
        float Lerp(float a, float c, float t) => a + (c - a) * t;
        if (Radial)
            b.Num(Lerp(X0, X1, t0)).Sp().Num(Lerp(Y0, Y1, t0)).Sp().Num(Math.Max(0, Lerp(R0, R1, t0))).Sp()
             .Num(Lerp(X0, X1, t1)).Sp().Num(Lerp(Y0, Y1, t1)).Sp().Num(Math.Max(0, Lerp(R0, R1, t1)));
        else
            b.Num(Lerp(X0, X1, t0)).Sp().Num(Lerp(Y0, Y1, t0)).Sp().Num(Lerp(X0, X1, t1)).Sp().Num(Lerp(Y0, Y1, t1));
        b.Ascii("] /Domain [").Num(t0).Sp().Num(t1).Ascii("] /Extend [true true] /Function ");
        if (t0 == 0 && t1 == 1) WriteFunction(b, colors, gray);
        else
        {
            // One copy of the stops per period, mirrored on odd periods when reflecting.
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
        b.Ascii(" >>");
    }

    /// <summary>
    /// The colour function over [0, 1]: constant before the first stop and after the last, linear in between. Gray
    /// colours are alphas (0-255), one component.
    /// </summary>
    private void WriteFunction(Pdf.ByteBuffer b, uint[] colors, bool gray)
    {
        // Segments between consecutive stops; zero-length ones (hard colour changes) are dropped.
        var segs = new List<(float From, float To, uint C0, uint C1)>(Offsets.Length + 1);
        if (Offsets[0] > 0) segs.Add((0, Offsets[0], colors[0], colors[0]));
        for (int i = 0; i + 1 < Offsets.Length; i++)
            if (Offsets[i + 1] > Offsets[i]) segs.Add((Offsets[i], Offsets[i + 1], colors[i], colors[i + 1]));
        if (Offsets[^1] < 1) segs.Add((Offsets[^1], 1, colors[^1], colors[^1]));
        if (segs.Count == 0) segs.Add((0, 1, colors[^1], colors[^1]));

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
        => "/FunctionType 4 /Domain [-100000 100000 -100000 100000] /Range [" + (gray ? "0 1" : "0 1 0 1 0 1") + "]";

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
        b.Ascii("{ ").Num(Y0).Ascii(" exch sub exch ").Num(X0).Ascii(" sub exch 2 copy abs exch abs add 0.0001 lt { pop pop 0 } { atan } ifelse ")
            .Num(From).Ascii(" sub dup 0 lt { 360 add } if 360 div ");
        if (RepeatPeriod > 0) b.Num6(RepeatStart).Ascii(" sub ").Num6(RepeatPeriod).Ascii(" div dup floor sub ");

        // Segments between consecutive stops, as for the other gradients; t is on the stack.
        var segs = new List<(float From, float To, uint C0, uint C1)>(Offsets.Length + 1);
        if (Offsets[0] > 0) segs.Add((0, Offsets[0], colors[0], colors[0]));
        for (int i = 0; i + 1 < Offsets.Length; i++)
            if (Offsets[i + 1] > Offsets[i]) segs.Add((Offsets[i], Offsets[i + 1], colors[i], colors[i + 1]));
        if (Offsets[^1] < 1) segs.Add((Offsets[^1], 1, colors[^1], colors[^1]));
        if (segs.Count == 0) segs.Add((0, 1, colors[^1], colors[^1]));
        for (int i = 0; i < segs.Count; i++)
        {
            var (from, to, c0, c1) = segs[i];
            bool last = i == segs.Count - 1;
            if (!last) b.Ascii("dup ").Num6(to).Ascii(" le { ");
            if (c0 == c1)
            {
                b.Ascii("pop ");
                Channels(b, c0, gray);
            }
            else
            {
                // s = (t - from) / (to - from), then each channel c0 + s * (c1 - c0).
                b.Num6(from).Ascii(" sub ").Num6(1 / (to - from)).Ascii(" mul ");
                if (gray) b.Num6((c1 - (float)c0) / 255f).Ascii(" mul ").Num6(c0 / 255f).Ascii(" add ");
                else
                {
                    for (int shift = 16; shift >= 0; shift -= 8)
                    {
                        float v0 = (c0 >> shift & 0xFF) / 255f, v1 = (c1 >> shift & 0xFF) / 255f;
                        if (shift > 0) b.Ascii("dup ");
                        b.Num6(v1 - v0).Ascii(" mul ").Num6(v0).Ascii(" add ");
                        if (shift > 0) b.Ascii("exch ");
                    }
                }
            }
            if (!last) b.Ascii("} { ");
        }
        for (int i = 0; i < segs.Count - 1; i++) b.Ascii("} ifelse ");
        b.Ascii("}");
        return b.Span.ToArray();
    }

    private static void Channels(Pdf.ByteBuffer b, uint c, bool gray)
    {
        if (gray) b.Num6(c / 255f).Sp();
        else b.Num6((c >> 16 & 0xFF) / 255f).Sp().Num6((c >> 8 & 0xFF) / 255f).Sp().Num6((c & 0xFF) / 255f).Sp();
    }

    private static void Rgb(Pdf.ByteBuffer b, uint c)
        => b.Num((c >> 16 & 0xFF) / 255f).Sp().Num((c >> 8 & 0xFF) / 255f).Sp().Num((c & 0xFF) / 255f);

    /// <summary>The range of the gradient parameter over a box given in the painted shape's space.</summary>
    private bool Range(float minX, float minY, float maxX, float maxY, out float lo, out float hi)
    {
        lo = float.MaxValue;
        hi = float.MinValue;
        if (!Transform.TryInvert(out var inv) || !(maxX >= minX) || !(maxY >= minY)) return false;
        Span<float> xs = stackalloc float[] { minX, maxX, maxX, minX };
        Span<float> ys = stackalloc float[] { minY, minY, maxY, maxY };
        for (int i = 0; i < 4; i++)
        {
            float x = xs[i], y = ys[i];
            inv.Apply(ref x, ref y);
            float t;
            if (Radial)
            {
                // Assumes the focal point at the centre, which is how repeating radial gradients are used.
                float dr = R1 - R0;
                if (dr <= 0) return false;
                float d = MathF.Sqrt((x - X1) * (x - X1) + (y - Y1) * (y - Y1));
                lo = Math.Min(lo, 0);
                t = (d - R0) / dr;
            }
            else
            {
                float dx = X1 - X0, dy = Y1 - Y0, len2 = dx * dx + dy * dy;
                if (len2 <= 0) return false;
                t = ((x - X0) * dx + (y - Y0) * dy) / len2;
            }
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return true;
    }
}

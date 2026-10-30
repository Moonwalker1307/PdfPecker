using System.Runtime.CompilerServices;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Svg;

namespace PdfPecker.Pdf;

/// <summary>
/// Turns the paint tree of a colour glyph (<see cref="ColorGlyphs"/>) into drawing shapes in font units, y up, which the
/// writer paints like an inline SVG: glyph outlines filled with colours and gradients, clips, soft masks for the
/// Porter-Duff composites and transparency groups for the blend modes. PDF has no sweep gradients, so they become
/// conic gradients over the whole turn, and it has no additive blending, so the plus mode screens the source onto the
/// backdrop.
/// </summary>
internal static class ColorGlyphDrawing
{
    // A sweep gradient's colour line is laid out over the turn with at most this many stops; a repeating one that would
    // need more is drawn padded.
    private const int MaxSweepStops = 4096;
    // Below this length, in font units, a gradient's geometry counts as degenerate and paints nothing.
    private const float Degenerate = 1e-4f;

    private static readonly string?[] BlendModes =
    [
        "Screen", "Overlay", "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight", "Difference",
        "Exclusion", "Multiply", "Hue", "Saturation", "Color", "Luminosity",
    ];
    private const int FirstBlendMode = 13;

    private static readonly ConditionalWeakTable<FontProgram, Outlines> FontOutlines = new();

    /// <summary>A rectangle in font units.</summary>
    private readonly record struct Box(float MinX, float MinY, float MaxX, float MaxY)
    {
        public bool IsEmpty => !(MaxX > MinX && MaxY > MinY);

        public Box Intersect(Box o) => new(Math.Max(MinX, o.MinX), Math.Max(MinY, o.MinY), Math.Min(MaxX, o.MaxX), Math.Min(MaxY, o.MaxY));
    }

    /// <summary>
    /// The shapes of a glyph's colour drawing, with the text colour put in for the foreground. A colour or gradient
    /// outside any glyph outline fills the clip box around it, or else the font's bounding box.
    /// </summary>
    public static List<SvgShape> Draw(FontProgram program, ColorPaint paint, uint foreground)
    {
        var outlines = FontOutlines.GetValue(program, p => new Outlines(p));
        var area = new Box(program.XMin, program.YMin, program.XMax, program.YMax);
        if (area.IsEmpty) area = new Box(0, -program.UnitsPerEm / 4f, program.UnitsPerEm, program.UnitsPerEm);
        var shapes = new List<SvgShape>();
        new Painter(outlines, foreground).Paint(paint, Matrix.Identity, area, shapes);
        return shapes;
    }

    /// <summary>The outline of a glyph in font units, y up, as path verbs and points; null when it has none.</summary>
    public static (byte[] Verbs, float[] Points)? Outline(FontProgram program, int gid)
        => FontOutlines.GetValue(program, p => new Outlines(p)).Glyph(gid);

    private sealed class Painter(Outlines outlines, uint foreground)
    {
        public void Paint(ColorPaint paint, Matrix m, Box clip, List<SvgShape> into)
        {
            if (clip.IsEmpty) return;
            switch (paint)
            {
                case ColorLayers layers:
                    foreach (var layer in layers.Layers) Paint(layer, m, clip, into);
                    break;
                case ColorTransform t:
                    Paint(t.Paint, t.Matrix.Then(m), clip, into);
                    break;
                case ColorClipBox box:
                {
                    var path = new PathBuilder();
                    Rect(path, box.XMin, box.YMin, box.XMax, box.YMax, m);
                    var shape = Shape(path);
                    if (shape == null) return;
                    shape.Kind = SvgShape.KindClip;
                    into.Add(shape);
                    Paint(box.Paint, m, clip.Intersect(Bounds(shape)), into);
                    into.Add(new SvgShape { Kind = SvgShape.KindEndClip });
                    break;
                }
                case ColorGlyphClip glyph:
                    PaintGlyph(glyph, m, clip, into);
                    break;
                case ColorSolid or ColorGradient:
                {
                    // An unclipped colour or gradient fills what the clips around it leave.
                    var path = new PathBuilder();
                    Rect(path, clip.MinX, clip.MinY, clip.MaxX, clip.MaxY, Matrix.Identity);
                    if (Shape(path) is { } shape && Fill(shape, paint, Matrix.Identity, m)) into.Add(shape);
                    break;
                }
                case ColorComposite c:
                    PaintComposite(c, m, clip, into);
                    break;
            }
        }

        /// <summary>
        /// A glyph outline: filled straight away when its paint is a colour or a gradient (through transforms),
        /// else a clip around whatever its paint draws.
        /// </summary>
        private void PaintGlyph(ColorGlyphClip glyph, Matrix m, Box clip, List<SvgShape> into)
        {
            if (outlines.Glyph(glyph.Gid) is not var (verbs, points)) return;
            var path = new PathBuilder();
            for (int i = 0, k = 0; i < verbs.Length; i++)
            {
                int n = verbs[i] switch { SvgShape.CurveTo => 6, SvgShape.Close => 0, _ => 2 };
                for (int j = 0; j < n; j += 2)
                {
                    float x = points[k + j], y = points[k + j + 1];
                    m.Apply(ref x, ref y);
                    path.Points.Add(x);
                    path.Points.Add(y);
                }
                path.Verbs.Add(verbs[i]);
                k += n;
            }
            if (Shape(path) is not { } shape) return;
            var inner = glyph.Paint;
            var paintSpace = Matrix.Identity;
            while (inner is ColorTransform t)
            {
                paintSpace = t.Matrix.Then(paintSpace);
                inner = t.Paint;
            }
            if (inner is ColorSolid or ColorGradient)
            {
                if (Fill(shape, inner, paintSpace, m)) into.Add(shape);
                return;
            }
            shape.Kind = SvgShape.KindClip;
            into.Add(shape);
            Paint(glyph.Paint, m, clip.Intersect(Bounds(shape)), into);
            into.Add(new SvgShape { Kind = SvgShape.KindEndClip });
        }

        /// <summary>
        /// Gives a path its fill: a colour, or a gradient whose geometry is in a space that <paramref name="paintSpace"/>
        /// and then <paramref name="m"/> map to font units. False when it paints nothing.
        /// </summary>
        private bool Fill(SvgShape shape, ColorPaint paint, Matrix paintSpace, Matrix m)
        {
            if (paint is ColorSolid solid)
            {
                uint color = solid.Resolve(foreground);
                if (color >> 24 == 0) return false;
                shape.Fill = color;
                return true;
            }
            var gradient = Gradient((ColorGradient)paint, paintSpace.Then(m));
            if (gradient == null) return false;
            shape.FillGradient = gradient;
            return true;
        }

        private void PaintComposite(ColorComposite c, Matrix m, Box clip, List<SvgShape> into)
        {
            var source = new List<SvgShape>();
            var backdrop = new List<SvgShape>();
            if (c.Mode != ColorComposite.Dest) Paint(c.Source, m, clip, source);
            if (c.Mode != ColorComposite.Src) Paint(c.Backdrop, m, clip, backdrop);
            switch (c.Mode)
            {
                case ColorComposite.Clear:
                    break;
                case ColorComposite.Src:
                    into.AddRange(source);
                    break;
                case ColorComposite.Dest:
                    into.AddRange(backdrop);
                    break;
                case ColorComposite.DestOver:
                    into.AddRange(source);
                    into.AddRange(backdrop);
                    break;
                case ColorComposite.SrcIn:
                    Masked(source, backdrop, inverted: false, clip, into);
                    break;
                case ColorComposite.DestIn:
                    Masked(backdrop, source, inverted: false, clip, into);
                    break;
                case ColorComposite.SrcOut:
                    Masked(source, backdrop, inverted: true, clip, into);
                    break;
                case ColorComposite.DestOut:
                    Masked(backdrop, source, inverted: true, clip, into);
                    break;
                case ColorComposite.SrcAtop:
                    // The backdrop where the source is not, and the source where the backdrop is.
                    Masked(backdrop, source, inverted: true, clip, into);
                    Masked(source, backdrop, inverted: false, clip, into);
                    break;
                case ColorComposite.DestAtop:
                    Masked(source, backdrop, inverted: true, clip, into);
                    Masked(backdrop, source, inverted: false, clip, into);
                    break;
                case ColorComposite.Xor:
                    Masked(source, backdrop, inverted: true, clip, into);
                    Masked(backdrop, source, inverted: true, clip, into);
                    break;
                case >= FirstBlendMode and < FirstBlendMode + 15:
                {
                    if (source.Count == 0)
                    {
                        into.AddRange(backdrop);
                        break;
                    }
                    // An isolated group, so that the source blends with the backdrop only, not with what the glyph is
                    // drawn on.
                    var blended = Group(source);
                    blended.BlendMode = BlendModes[c.Mode - FirstBlendMode];
                    var children = new List<SvgShape>(backdrop) { blended };
                    var group = Group(children);
                    group.Isolated = true;
                    into.Add(group);
                    break;
                }
                case ColorComposite.Plus when source.Count > 0:
                {
                    // PDF cannot add colours: screen is the blend mode closest to it, lighter where both paint.
                    var blended = Group(source);
                    blended.BlendMode = "Screen";
                    var group = Group(new List<SvgShape>(backdrop) { blended });
                    group.Isolated = true;
                    into.Add(group);
                    break;
                }
                default:
                    // Source over, and modes this reader does not know.
                    into.AddRange(backdrop);
                    into.AddRange(source);
                    break;
            }
        }

        /// <summary>
        /// Draws <paramref name="content"/> through a soft mask made of <paramref name="mask"/>'s alpha, or of what it
        /// leaves (one minus its alpha) when <paramref name="inverted"/>.
        /// </summary>
        private static void Masked(List<SvgShape> content, List<SvgShape> mask, bool inverted, Box clip, List<SvgShape> into)
        {
            if (content.Count == 0) return;
            if (mask.Count == 0)
            {
                if (inverted) into.AddRange(content);
                return;
            }
            var region = BoundsOf(content).Intersect(clip);
            if (region.IsEmpty) return;
            var path = new PathBuilder();
            Rect(path, region.MinX, region.MinY, region.MaxX, region.MaxY, Matrix.Identity);
            var shape = Shape(path)!;
            shape.Kind = SvgShape.KindMask;
            if (inverted)
            {
                // A luminosity mask: white where nothing is drawn, darkened by the mask's shapes painted black.
                var white = Shape(path)!;
                white.Fill = 0xFFFFFFFF;
                var children = new List<SvgShape> { white };
                Blacken(mask, children);
                shape.Children = children;
            }
            else
            {
                shape.Children = mask;
                shape.MaskAlpha = true;
            }
            into.Add(shape);
            // A group of its own, as a composite's paints are layers of their own: the content may need a soft mask of
            // its own, for a gradient's alpha or a mask inside it, and PDF has one soft mask at a time, which a group
            // starts without.
            var group = Group(content);
            group.Isolated = true;
            into.Add(group);
            into.Add(new SvgShape { Kind = SvgShape.KindEndMask });
        }

        /// <summary>Copies shapes painted black with their own alphas; what masks and clips are made of stays as it is.</summary>
        private static void Blacken(List<SvgShape> shapes, List<SvgShape> into)
        {
            foreach (var s in shapes)
            {
                if (s.Kind == SvgShape.KindPath)
                {
                    var c = s.Recolored(color => color & 0xFF000000);
                    into.Add(c);
                }
                else if (s.Kind == SvgShape.KindImage && s.Children != null)
                {
                    var children = new List<SvgShape>();
                    Blacken(s.Children, children);
                    var group = s.Recolored(color => color);
                    group.Children = children;
                    into.Add(group);
                }
                else into.Add(s);
            }
        }

        /// <summary>A gradient in font units, or null when its geometry or colour line paints nothing.</summary>
        private Gradient? Gradient(ColorGradient g, Matrix toFont)
        {
            if (g.Stops.Length == 0) return null;
            var stops = g.Stops.Select(s => (s.Offset, Color: s.Foreground ? s.Color & 0xFF000000 | foreground & 0xFFFFFF : s.Color))
                .OrderBy(s => s.Offset).ToList();
            var spread = g.Extend switch { ColorExtend.Repeat => GradientSpread.Repeat, ColorExtend.Reflect => GradientSpread.Reflect, _ => GradientSpread.Pad };
            if (g.Kind == ColorGradientKind.Sweep) return Sweep(g, stops, toFont);

            // The colour line's stops are put on [0, 1] by moving the geometry to the first and last stop.
            float t0 = stops[0].Offset, t1 = stops[^1].Offset;
            if (t1 - t0 < Degenerate)
            {
                if (spread != GradientSpread.Pad) return null;
                // All stops at one offset: the first colour before it, the last after it.
                t1 = t0 + Degenerate;
                stops = [(t0, stops[0].Color), (t1, stops[^1].Color)];
            }
            var result = new Gradient { Spread = spread, Transform = toFont };
            if (g.Kind == ColorGradientKind.Linear)
            {
                // The gradient runs from p0 to p3, the foot of p1 on the line through p0 perpendicular to p0 p2.
                float nx = g.Y0 - g.Y2, ny = g.X2 - g.X0;
                float len2 = nx * nx + ny * ny;
                float dx = g.X1 - g.X0, dy = g.Y1 - g.Y0;
                if (len2 < Degenerate || dx * dx + dy * dy < Degenerate) return null;
                float k = (dx * nx + dy * ny) / len2;
                float x3 = g.X0 + nx * k, y3 = g.Y0 + ny * k;
                if ((x3 - g.X0) * (x3 - g.X0) + (y3 - g.Y0) * (y3 - g.Y0) < Degenerate) return null;
                result.Kind = GradientKind.Linear;
                result.X0 = g.X0 + (x3 - g.X0) * t0; result.Y0 = g.Y0 + (y3 - g.Y0) * t0;
                result.X1 = g.X0 + (x3 - g.X0) * t1; result.Y1 = g.Y0 + (y3 - g.Y0) * t1;
            }
            else
            {
                result.Kind = GradientKind.Radial;
                result.X0 = g.X0 + (g.X1 - g.X0) * t0; result.Y0 = g.Y0 + (g.Y1 - g.Y0) * t0; result.R0 = g.R0 + (g.R1 - g.R0) * t0;
                result.X1 = g.X0 + (g.X1 - g.X0) * t1; result.Y1 = g.Y0 + (g.Y1 - g.Y0) * t1; result.R1 = g.R0 + (g.R1 - g.R0) * t1;
                if (result.R0 < 0 && result.R1 < 0) return null;
            }
            for (int i = 0; i < stops.Count; i++) stops[i] = ((stops[i].Offset - t0) / (t1 - t0), stops[i].Color);
            SetStops(result, stops);
            return result;
        }

        /// <summary>
        /// A sweep gradient as a conic gradient over the whole turn: the colour line is laid out over the angles from 0
        /// to 360 degrees, with its extend mode applied outside the sweep's own angles.
        /// </summary>
        private static Gradient? Sweep(ColorGradient g, List<(float Offset, uint Color)> stops, Matrix toFont)
        {
            float a0 = g.StartAngle, a1 = g.EndAngle;
            var turn = new List<(float Offset, uint Color)>();
            float t0 = stops[0].Offset, t1 = stops[^1].Offset, period = t1 - t0;
            // A repeating colour line of no length has no colour to repeat, and paints nothing, as in Chrome.
            if (period < Degenerate && g.Extend != ColorExtend.Pad) return null;
            if (Math.Abs(a1 - a0) < Degenerate)
            {
                // A sweep of no angle: padded, the first colour before its angle and the last from it on.
                if (g.Extend != ColorExtend.Pad) return null;
                float s = Math.Clamp(a0 / 360, 0, 1);
                turn.Add((0, stops[0].Color));
                turn.Add((s, stops[0].Color));
                turn.Add((s, stops[^1].Color));
                turn.Add((1, stops[^1].Color));
            }
            else
            {
                // The breaks of the colour function over the turn: its ends, each stop and, for a repeating line, each
                // stop in every period the turn covers.
                float TAt(float s) => (s * 360 - a0) / (a1 - a0);
                float SAt(float t) => (a0 + t * (a1 - a0)) / 360;
                var breaks = new List<float> { 0, 1 };
                bool periodic = g.Extend != ColorExtend.Pad && period >= Degenerate;
                if (periodic)
                {
                    float lo = Math.Min(TAt(0), TAt(1)), hi = Math.Max(TAt(0), TAt(1));
                    double first = Math.Floor((lo - t0) / period), last = Math.Ceiling((hi - t0) / period);
                    if ((last - first + 1) * stops.Count > MaxSweepStops) periodic = false;
                    else
                        for (double k = first; k <= last; k++)
                            foreach (var stop in stops)
                            {
                                float t = (float)(stop.Offset + k * period);
                                if (g.Extend == ColorExtend.Reflect && ((long)k & 1) != 0) t = (float)(t0 + (k + 1) * period - (stop.Offset - t0));
                                breaks.Add(SAt(t));
                            }
                }
                if (!periodic) foreach (var stop in stops) breaks.Add(SAt(stop.Offset));
                breaks.RemoveAll(s => !(s >= 0 && s <= 1));
                breaks.Sort();
                const float Side = 1e-6f;
                float prev = float.NaN;
                foreach (float s in breaks)
                {
                    if (s == prev) continue;
                    prev = s;
                    uint before = ColorAt(stops, TAt(Math.Max(0, s - Side)), periodic ? g.Extend : ColorExtend.Pad);
                    uint after = ColorAt(stops, TAt(Math.Min(1, s + Side)), periodic ? g.Extend : ColorExtend.Pad);
                    if (s > 0) turn.Add((s, before));
                    if (s < 1 && (after != before || s == 0)) turn.Add((s, after));
                }
            }
            // The conic gradient's angles go clockwise from up in its own space: turned a quarter, they are the sweep's
            // angles counter-clockwise from the x axis in font units.
            var result = new Gradient
            {
                Kind = GradientKind.Conic,
                Transform = new Matrix(0, 1, -1, 0, g.X0, g.Y0).Then(toFont),
            };
            SetStops(result, turn);
            return result;
        }

        /// <summary>The colour of a colour line at <paramref name="t"/>, with its extend mode outside its stops.</summary>
        private static uint ColorAt(List<(float Offset, uint Color)> stops, float t, ColorExtend extend)
        {
            float t0 = stops[0].Offset, t1 = stops[^1].Offset, period = t1 - t0;
            if (extend != ColorExtend.Pad && period > 0)
            {
                float u = (t - t0) / period;
                float k = MathF.Floor(u);
                float f = u - k;
                if (extend == ColorExtend.Reflect && ((long)k & 1) != 0) f = 1 - f;
                t = t0 + f * period;
            }
            if (t <= t0) return stops[0].Color;
            if (t >= t1) return stops[^1].Color;
            for (int i = 1; i < stops.Count; i++)
            {
                if (t > stops[i].Offset) continue;
                var (o0, c0) = stops[i - 1];
                var (o1, c1) = stops[i];
                float f = o1 > o0 ? (t - o0) / (o1 - o0) : 1;
                return Lerp(c0, c1, f);
            }
            return stops[^1].Color;
        }

        private static uint Lerp(uint c0, uint c1, float f)
        {
            uint Channel(int shift) => (uint)MathF.Round((c0 >> shift & 0xFF) + ((c1 >> shift & 0xFF) - (float)(c0 >> shift & 0xFF)) * f) & 0xFF;
            return Channel(24) << 24 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }

        /// <summary>Sets a gradient's stops: opaque colours, and the alphas apart when some stop is translucent.</summary>
        private static void SetStops(Gradient g, List<(float Offset, uint Color)> stops)
        {
            float last = 0;
            g.Offsets = new float[stops.Count];
            g.Colors = new uint[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                last = g.Offsets[i] = Math.Max(last, Math.Clamp(stops[i].Offset, 0, 1));
                g.Colors[i] = 0xFF000000 | stops[i].Color & 0xFFFFFF;
            }
            g.KeepAlpha(stops);
        }

        private static SvgShape Group(List<SvgShape> children)
        {
            var b = BoundsOf(children);
            return new SvgShape
            {
                Kind = SvgShape.KindImage, ImageMatrix = Matrix.Identity, Children = children,
                MinX = b.MinX, MinY = b.MinY, MaxX = b.MaxX, MaxY = b.MaxY,
            };
        }
    }

    /// <summary>A rectangle's outline, mapped through <paramref name="m"/>.</summary>
    private static void Rect(PathBuilder path, float x0, float y0, float x1, float y1, Matrix m)
    {
        void Point(float x, float y, bool move)
        {
            m.Apply(ref x, ref y);
            if (move) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        Point(x0, y0, true);
        Point(x1, y0, false);
        Point(x1, y1, false);
        Point(x0, y1, false);
        path.Close();
    }

    /// <summary>A path shape with its bounds, or null for a path with no points.</summary>
    private static SvgShape? Shape(PathBuilder path)
    {
        if (path.Points.Count == 0) return null;
        var shape = new SvgShape { Verbs = path.Verbs.ToArray(), Points = path.Points.ToArray() };
        shape.MinX = shape.MinY = float.MaxValue;
        shape.MaxX = shape.MaxY = float.MinValue;
        for (int i = 0; i + 1 < shape.Points.Length; i += 2)
        {
            shape.MinX = Math.Min(shape.MinX, shape.Points[i]); shape.MaxX = Math.Max(shape.MaxX, shape.Points[i]);
            shape.MinY = Math.Min(shape.MinY, shape.Points[i + 1]); shape.MaxY = Math.Max(shape.MaxY, shape.Points[i + 1]);
        }
        return shape;
    }

    private static Box Bounds(SvgShape s) => new(s.MinX, s.MinY, s.MaxX, s.MaxY);

    /// <summary>The bounds of what shapes paint (empty when they paint nothing).</summary>
    public static (float MinX, float MinY, float MaxX, float MaxY) PaintedBounds(List<SvgShape> shapes)
    {
        var b = BoundsOf(shapes);
        return (b.MinX, b.MinY, b.MaxX, b.MaxY);
    }

    private static Box BoundsOf(List<SvgShape> shapes)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var s in shapes)
        {
            if (s.Kind is not (SvgShape.KindPath or SvgShape.KindImage)) continue;
            minX = Math.Min(minX, s.MinX); minY = Math.Min(minY, s.MinY);
            maxX = Math.Max(maxX, s.MaxX); maxY = Math.Max(maxY, s.MaxY);
        }
        return new Box(minX, minY, maxX, maxY);
    }

    /// <summary>A font's glyph outlines in font units, read once each, shared by documents converted in parallel.</summary>
    private sealed class Outlines
    {
        private readonly IGlyphSource? _source;
        private readonly float _toFontUnits;
        private readonly Dictionary<int, (byte[], float[])?> _glyphs = new();

        public Outlines(FontProgram program)
        {
            _source = FontProgramOutlines.Source(program);
            _toFontUnits = program.UnitsPerEm;
        }

        public (byte[] Verbs, float[] Points)? Glyph(int gid)
        {
            lock (_glyphs)
            {
                if (_glyphs.TryGetValue(gid, out var outline)) return outline;
                outline = Read(gid);
                _glyphs[gid] = outline;
                return outline;
            }
        }

        private (byte[], float[])? Read(int gid)
        {
            if (_source == null || gid < 0 || gid >= _source.GlyphCount) return null;
            Rendering.Graphics.PathData? path;
            try
            {
                path = _source.GetGlyph(gid);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException)
            {
                SwallowedErrors.Record(ex);
                return null;
            }
            if (path == null || path.IsEmpty) return null;
            var fm = _source.FontMatrix;
            var points = new float[path.Points.Count];
            for (int i = 0; i + 1 < points.Length; i += 2)
            {
                double x = path.Points[i], y = path.Points[i + 1];
                points[i] = (float)((fm.A * x + fm.C * y + fm.E) * _toFontUnits);
                points[i + 1] = (float)((fm.B * x + fm.D * y + fm.F) * _toFontUnits);
            }
            return (path.Verbs.ToArray(), points);
        }
    }
}

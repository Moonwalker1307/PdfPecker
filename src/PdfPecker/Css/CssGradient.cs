using System.Globalization;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Css;

/// <summary>
/// A CSS linear-gradient(), radial-gradient(), conic-gradient() or repeating variant, as specified. Lengths that depend on the box are
/// kept until <see cref="Resolve"/> knows its size.
/// </summary>
internal sealed class CssGradient
{
    private enum RadialSize : byte { FarthestCorner, FarthestSide, ClosestCorner, ClosestSide, Explicit }

    // A colour stop takes at most two positions ("red 10% 20%" is two stops of the same colour).
    private const int MaxStopPositions = 2;
    // Conic stop angles are kept as percentages of the full turn.
    private const double DegreesPerPercent = 3.6;
    // A repeating gradient whose stops span less than this fraction of the line is painted as its last colour.
    private const float MinRepeatPeriod = 1e-4f;
    // Stops added between two stops of a gradient that interpolates in another colour space, and in all at most.
    private const int StepsPerPair = 16, MaxSubdividedStops = 1024;
    // A radial gradient with no size (a centre on the box edge, or a zero length) still gets this radius.
    private const float MinRadius = 0.01f;

    // The units of a linear gradient's angle, in degrees. Parsed as float, not with ColorFunctions.TryAngle, which
    // works in double and would round some angles differently.
    private static readonly (string Unit, float Scale)[] AngleUnits = { ("deg", 1), ("grad", 0.9f), ("rad", 180f / MathF.PI), ("turn", 360) };

    public GradientKind Kind;
    public bool Repeating;
    public bool Radial => Kind == GradientKind.Radial;
    public bool Conic => Kind == GradientKind.Conic;
    private float? _angle;          // degrees, clockwise from "to top"
    private int _toX, _toY = 1;     // "to <side-or-corner>" when there is no angle: -1 left/top, 1 right/bottom
    private bool _circle;
    private RadialSize _size;
    private Length _sizeX, _sizeY;
    private Length _posX = new(50, LengthUnit.Percent), _posY = new(50, LengthUnit.Percent);
    private readonly List<(uint Color, Length? Position)> _stops = new();
    private string? _space;         // the colour interpolation space ("in oklch"), or null for sRGB
    private string _hueMethod = "shorter";

    /// <summary>Parses one gradient function; null when it is not a gradient this engine draws.</summary>
    public static CssGradient? Parse(string value, uint currentColor, float em, float rem, float vw, float vh)
    {
        var v = value.Trim();
        int paren = v.IndexOf('(');
        if (paren < 0 || !v.EndsWith(')')) return null;
        var name = v.Substring(0, paren).Trim().ToLowerInvariant();
        (GradientKind Kind, bool Repeating)? function = name switch
        {
            "linear-gradient" => (GradientKind.Linear, false),
            "repeating-linear-gradient" => (GradientKind.Linear, true),
            "radial-gradient" => (GradientKind.Radial, false),
            "repeating-radial-gradient" => (GradientKind.Radial, true),
            "conic-gradient" => (GradientKind.Conic, false),
            "repeating-conic-gradient" => (GradientKind.Conic, true),
            _ => null, // -webkit-gradient() and prefixed legacy syntaxes are not supported
        };
        if (function is not (var kind, var repeating)) return null;
        var g = new CssGradient { Kind = kind, Repeating = repeating };
        var args = CssParser.SplitTopLevel(v.Substring(paren + 1, v.Length - paren - 2), ',');
        if (args.Count == 0) return null;
        int first = 0;
        var head = CssParser.Tokens(args[0].Trim().ToLowerInvariant());
        if (head.Count > 0 && !CssColor.TryParse(head[0], currentColor, out _))
        {
            if (!g.TakeInterpolationMethod(head)) return null;
            bool ok = head.Count == 0 || g.Kind switch
            {
                GradientKind.Conic => g.ParseConicHead(head, em, rem, vw, vh),
                GradientKind.Radial => g.ParseRadialShape(head, em, rem, vw, vh),
                _ => g.ParseDirection(head),
            };
            if (!ok) return null;
            first = 1;
        }
        for (int i = first; i < args.Count; i++)
            if (!g.ParseStop(args[i], currentColor, em, rem, vw, vh)) return null;
        return g._stops.Count > 0 ? g : null;
    }

    /// <summary>One colour stop: a colour and up to <see cref="MaxStopPositions"/> positions, or a colour hint.</summary>
    private bool ParseStop(string arg, uint currentColor, float em, float rem, float vw, float vh)
    {
        var toks = CssParser.Tokens(arg.Trim());
        if (toks.Count == 0) return false;
        if (!CssColor.TryParse(toks[0], currentColor, out uint color))
            return toks.Count == 1; // a colour hint: interpolation midpoints are not supported
        if (toks.Count == 1)
        {
            _stops.Add((color, null));
            return true;
        }
        for (int k = 1; k < toks.Count && k <= MaxStopPositions; k++)
        {
            if (!TryStopPosition(toks[k].ToLowerInvariant(), em, rem, vw, vh, out var pos)) return false;
            _stops.Add((color, pos));
        }
        return true;
    }

    private bool TryStopPosition(string t, float em, float rem, float vw, float vh, out Length pos)
    {
        if (!Conic) return StyleResolver.TryParseLength(t, em, rem, out pos, vw, vh) && !pos.IsAuto;
        // Conic stops are angles, or percentages of the full turn.
        if (t == "0")
        {
            pos = new Length(0, LengthUnit.Percent);
            return true;
        }
        if (ColorFunctions.TryAngle(t, out double deg))
        {
            pos = new Length((float)(deg / DegreesPerPercent), LengthUnit.Percent);
            return true;
        }
        return StyleResolver.TryParseLength(t, em, rem, out pos, vw, vh) && pos.IsPercent && pos.Unit == LengthUnit.Percent;
    }

    /// <summary>
    /// Takes the colour interpolation method (<c>in oklch</c>, <c>in hsl longer hue</c>) out of the gradient's first
    /// argument, which it may start or end. False when the method names no space PdfPecker interpolates in, or gives
    /// a hue method to a space without hue, which makes the gradient invalid as in Chrome.
    /// </summary>
    private bool TakeInterpolationMethod(List<string> t)
    {
        int at = t.IndexOf("in");
        if (at < 0) return true;
        if (at + 1 >= t.Count || !ColorFunctions.IsInterpolationSpace(t[at + 1], out bool polar)) return false;
        int end = at + 2;
        if (end + 1 < t.Count && t[end + 1] == "hue")
        {
            if (!polar || t[end] is not ("shorter" or "longer" or "increasing" or "decreasing")) return false;
            _hueMethod = t[end];
            end += 2;
        }
        // The method comes first or last, never between the parts of a direction, shape or position.
        if (at != 0 && end != t.Count) return false;
        _space = t[at + 1] == "srgb" ? null : t[at + 1];
        t.RemoveRange(at, end - at);
        return true;
    }

    private bool ParseDirection(List<string> t)
    {
        if (t[0] == "to")
        {
            _toX = _toY = 0;
            for (int i = 1; i < t.Count; i++)
            {
                switch (t[i])
                {
                    case "left": _toX = -1; break;
                    case "right": _toX = 1; break;
                    case "top": _toY = -1; break;
                    case "bottom": _toY = 1; break;
                    default: return false;
                }
            }
            return _toX != 0 || _toY != 0;
        }
        if (t.Count != 1) return false;
        var a = t[0];
        foreach (var (unit, scale) in AngleUnits)
        {
            if (!a.EndsWith(unit, StringComparison.Ordinal)) continue;
            if (!float.TryParse(a.AsSpan(0, a.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return false;
            _angle = n * scale;
            return true;
        }
        if (a == "0") { _angle = 0; return true; }
        return false;
    }

    /// <summary>The start of a conic-gradient(): <c>from &lt;angle&gt;</c> and <c>at &lt;position&gt;</c>.</summary>
    private bool ParseConicHead(List<string> t, float em, float rem, float vw, float vh)
    {
        int i = 0;
        bool any = false;
        while (i < t.Count)
        {
            if (t[i] == "from" && i + 1 < t.Count && ColorFunctions.TryAngle(t[i + 1], out double deg))
            {
                _angle = (float)deg;
                i += 2;
            }
            else if (t[i] == "at")
            {
                int end = i + 1;
                while (end < t.Count && t[end] != "from") end++;
                if (!ParsePosition(t.GetRange(i + 1, end - i - 1), em, rem, vw, vh)) return false;
                i = end;
            }
            else return false;
            any = true;
        }
        return any;
    }

    private bool ParseRadialShape(List<string> t, float em, float rem, float vw, float vh)
    {
        bool? circle = null;
        var lengths = new List<Length>();
        bool sizeKeyword = false;
        int i = 0;
        for (; i < t.Count && t[i] != "at"; i++)
        {
            switch (t[i])
            {
                case "circle": circle = true; break;
                case "ellipse": circle = false; break;
                case "closest-side": _size = RadialSize.ClosestSide; sizeKeyword = true; break;
                case "closest-corner": _size = RadialSize.ClosestCorner; sizeKeyword = true; break;
                case "farthest-side": _size = RadialSize.FarthestSide; sizeKeyword = true; break;
                case "farthest-corner": _size = RadialSize.FarthestCorner; sizeKeyword = true; break;
                default:
                    if (!StyleResolver.TryParseLength(t[i], em, rem, out var len, vw, vh) || len.IsAuto) return false;
                    lengths.Add(len);
                    break;
            }
        }
        if (lengths.Count > 0)
        {
            if (sizeKeyword || lengths.Count > 2) return false;
            _size = RadialSize.Explicit;
            _sizeX = lengths[0];
            _sizeY = lengths.Count > 1 ? lengths[1] : lengths[0];
            circle ??= lengths.Count == 1;
            if (circle == true && (lengths.Count > 1 || lengths[0].IsPercent)) return false;
        }
        _circle = circle ?? false;
        return i == t.Count || ParsePosition(t.GetRange(i + 1, t.Count - i - 1), em, rem, vw, vh);
    }

    /// <summary>
    /// The <c>&lt;position&gt;</c> after <c>at</c>: one or two keywords or lengths, horizontal first unless the
    /// keywords say otherwise. The list is reordered in place.
    /// </summary>
    private bool ParsePosition(List<string> pos, float em, float rem, float vw, float vh)
    {
        if (pos.Count == 0 || pos.Count > 2) return false;
        if (pos.Count == 1 && pos[0] is "top" or "bottom")
        {
            _posY = Keyword(pos[0]);
            return true;
        }
        if (pos.Count == 2 && (pos[0] is "top" or "bottom" || pos[1] is "left" or "right")) (pos[0], pos[1]) = (pos[1], pos[0]);
        if (!PositionPart(pos[0], em, rem, vw, vh, out _posX)) return false;
        return pos.Count == 1 || PositionPart(pos[1], em, rem, vw, vh, out _posY);
    }

    private static Length Keyword(string k) => k switch
    {
        "left" or "top" => new Length(0, LengthUnit.Percent),
        "right" or "bottom" => new Length(100, LengthUnit.Percent),
        _ => new Length(50, LengthUnit.Percent),
    };

    private static bool PositionPart(string tok, float em, float rem, float vw, float vh, out Length len)
    {
        if (tok is "left" or "right" or "top" or "bottom" or "center") { len = Keyword(tok); return true; }
        return StyleResolver.TryParseLength(tok, em, rem, out len, vw, vh) && !len.IsAuto;
    }

    // ------------------------------------------------------------------ resolving for a box

    /// <summary>The gradient for a box of the given size, in box coordinates (origin at its top-left corner).</summary>
    public Gradient Resolve(float w, float h, uint backdrop)
    {
        var g = new Gradient { Kind = Kind, Spread = Repeating ? GradientSpread.Repeat : GradientSpread.Pad };
        // The gradient line or ray, which stop lengths are measured against.
        float length = Kind switch
        {
            GradientKind.Conic => ResolveConic(g, w, h),
            GradientKind.Radial => ResolveRadial(g, w, h),
            _ => ResolveLinear(g, w, h),
        };
        var pos = PlaceStops(length);
        if (Repeating) ApplyRepeat(g, pos);
        if (Radial) { g.X0 = g.X1; g.Y0 = g.Y1; }
        var stops = ResolvedStops(g, pos);
        (g.Offsets, g.Colors) = Gradient.NormalizeStops(stops, backdrop);
        g.KeepAlpha(stops);
        return g;
    }

    /// <summary>The centre and start angle; the stops are fractions of the turn, so the length is 1.</summary>
    private float ResolveConic(Gradient g, float w, float h)
    {
        g.X0 = _posX.Resolve(w);
        g.Y0 = _posY.Resolve(h);
        float from = (_angle ?? 0) % 360;
        g.From = from < 0 ? from + 360 : from;
        return 1;
    }

    /// <summary>The gradient line through the centre of the box, long enough that its ends meet the corners.</summary>
    private float ResolveLinear(Gradient g, float w, float h)
    {
        var (dx, dy) = LinearDirection(w, h);
        float length = Math.Abs(w * dx) + Math.Abs(h * dy);
        g.X0 = w / 2 - dx * length / 2;
        g.Y0 = h / 2 - dy * length / 2;
        g.X1 = w / 2 + dx * length / 2;
        g.Y1 = h / 2 + dy * length / 2;
        return length;
    }

    /// <summary>The unit direction of the gradient line, y down.</summary>
    private (float Dx, float Dy) LinearDirection(float w, float h)
    {
        if (_angle is { } deg)
        {
            double rad = deg * Math.PI / 180;
            return ((float)Math.Sin(rad), (float)-Math.Cos(rad));
        }
        if (_toX != 0 && _toY != 0)
        {
            // Corner: perpendicular to the diagonal through the two other corners.
            float n = MathF.Sqrt(w * w + h * h);
            return (n > 0 ? _toX * h / n : _toX, n > 0 ? _toY * w / n : _toY);
        }
        return (_toX, _toY);
    }

    /// <summary>
    /// A circle of the horizontal radius around the origin, which the transform scales to an ellipse and moves to the
    /// centre; the ray is that radius.
    /// </summary>
    private float ResolveRadial(Gradient g, float w, float h)
    {
        float cx = _posX.Resolve(w), cy = _posY.Resolve(h);
        var (rx, ry) = RadialRadii(w, h, cx, cy);
        if (!(rx > 0) || !(ry > 0)) { rx = ry = Math.Max(MinRadius, Math.Max(rx, ry)); }
        g.R1 = rx;
        g.Transform = Matrix.Scale(1, ry / rx).Then(Matrix.Translate(cx, cy));
        return rx;
    }

    /// <summary>The radii of the ending shape for a centre at (cx, cy), from the size keyword or explicit lengths.</summary>
    private (float Rx, float Ry) RadialRadii(float w, float h, float cx, float cy)
    {
        // The distances to the closest or the farthest sides.
        bool closest = _size is RadialSize.ClosestSide or RadialSize.ClosestCorner;
        float sx = closest ? Math.Min(Math.Abs(cx), Math.Abs(w - cx)) : Math.Max(Math.Abs(cx), Math.Abs(w - cx));
        float sy = closest ? Math.Min(Math.Abs(cy), Math.Abs(h - cy)) : Math.Max(Math.Abs(cy), Math.Abs(h - cy));
        switch (_size)
        {
            case RadialSize.Explicit:
            {
                float rx = _sizeX.Resolve(w);
                return (rx, _circle ? rx : _sizeY.Resolve(h));
            }
            case RadialSize.ClosestSide:
            case RadialSize.FarthestSide:
            {
                if (!_circle) return (sx, sy);
                float r = closest ? Math.Min(sx, sy) : Math.Max(sx, sy);
                return (r, r);
            }
            default:
            {
                if (!_circle) return (sx * MathF.Sqrt(2), sy * MathF.Sqrt(2));
                float r = MathF.Sqrt(sx * sx + sy * sy);
                return (r, r);
            }
        }
    }

    /// <summary>
    /// The stop positions as fractions of the gradient line or ray. Missing ones are spread evenly between their
    /// neighbours, and none is before the one ahead of it.
    /// </summary>
    private float[] PlaceStops(float length)
    {
        int count = _stops.Count;
        var pos = new float[count];
        var known = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (_stops[i].Position is not { } p) continue;
            pos[i] = p.Unit == LengthUnit.Percent ? p.Value / 100f : length > 0 ? p.Resolve(length) / length : 0;
            known[i] = true;
        }
        if (!known[0]) { pos[0] = 0; known[0] = true; }
        if (!known[count - 1]) { pos[count - 1] = 1; known[count - 1] = true; }
        for (int i = 1; i < count; i++) if (known[i] && pos[i] < pos[i - 1]) pos[i] = pos[i - 1];
        for (int i = 1; i < count; i++)
        {
            if (known[i]) continue;
            int j = i;
            while (!known[j]) j++;
            for (int k = i; k < j; k++) pos[k] = pos[i - 1] + (pos[j] - pos[i - 1]) * (k - i + 1) / (j - i + 1);
            i = j;
        }
        for (int i = 1; i < count; i++) pos[i] = Math.Max(pos[i], pos[i - 1]);
        return pos;
    }

    /// <summary>
    /// Makes [first stop, last stop] one period of a repeating gradient and rescales the stops to [0, 1] within it. A
    /// degenerate period turns the gradient into a padded one, painted as its last colour.
    /// </summary>
    private void ApplyRepeat(Gradient g, float[] pos)
    {
        int last = pos.Length - 1;
        float start = pos[0], period = pos[last] - pos[0];
        if (period <= MinRepeatPeriod)
        {
            g.Spread = GradientSpread.Pad;
            return;
        }
        if (Conic)
        {
            // The turn repeats the period; the calculator function maps each angle into it.
            g.Spread = GradientSpread.Pad;
            g.RepeatStart = start;
            g.RepeatPeriod = period;
        }
        else if (Radial)
        {
            // Move the geometry so that the period becomes [0, 1] of the parameter.
            g.R0 = g.R1 * start;
            g.R1 *= pos[last];
        }
        else
        {
            float x0 = g.X0, y0 = g.Y0, ddx = g.X1 - g.X0, ddy = g.Y1 - g.Y0;
            g.X0 = x0 + ddx * start; g.Y0 = y0 + ddy * start;
            g.X1 = x0 + ddx * pos[last]; g.Y1 = y0 + ddy * pos[last];
        }
        for (int i = 0; i < pos.Length; i++) pos[i] = (pos[i] - start) / period;
    }

    /// <summary>The stops at their placed offsets, or the last colour alone when there is nothing to interpolate.</summary>
    private List<(float, uint)> ResolvedStops(Gradient g, float[] pos)
    {
        int count = _stops.Count;
        var stops = new List<(float, uint)>(Math.Max(2, count));
        if (count == 1 || Repeating && g.Spread == GradientSpread.Pad && g.RepeatPeriod == 0)
        {
            stops.Add((0, _stops[count - 1].Color));
            stops.Add((1, _stops[count - 1].Color));
        }
        else
        {
            // Offsets past [0, 1] are clipped, keeping the colour interpolated at the edge.
            for (int i = 0; i < count; i++) stops.Add((pos[i], _stops[i].Color));
            if (_space != null) Subdivide(stops, _space, _hueMethod);
            ClipStops(stops);
        }
        return stops;
    }

    /// <summary>
    /// Adds stops between each pair of stops with the colours interpolated in <paramref name="space"/>, since the
    /// PDF interpolates in RGB: sixteen steps per pair keep the curve within a level or two of Chrome's, and the total
    /// stays within <see cref="MaxSubdividedStops"/>.
    /// </summary>
    private static void Subdivide(List<(float Offset, uint Color)> stops, string space, string hueMethod)
    {
        int pairs = stops.Count - 1;
        int steps = Math.Clamp(MaxSubdividedStops / Math.Max(1, pairs), 1, StepsPerPair);
        if (steps == 1) return;
        var result = new List<(float, uint)>(pairs * steps + 1) { stops[0] };
        for (int i = 1; i < stops.Count; i++)
        {
            var (o0, c0) = stops[i - 1];
            var (o1, c1) = stops[i];
            // A hard edge has nothing to interpolate, nor do two stops of the same colour, unless the longer hue
            // takes them once round the hue circle.
            if (o1 > o0 && (c0 != c1 || hueMethod == "longer"))
                for (int k = 1; k < steps; k++)
                {
                    double t = (double)k / steps;
                    result.Add((o0 + (o1 - o0) * (float)t, ColorFunctions.Interpolate(c0, c1, t, space, hueMethod)));
                }
            result.Add(stops[i]);
        }
        stops.Clear();
        stops.AddRange(result);
    }

    /// <summary>Replaces stops outside [0, 1] by the colours interpolated at 0 and 1.</summary>
    private static void ClipStops(List<(float Offset, uint Color)> stops)
    {
        static uint Mix(uint a, uint b, float t)
        {
            uint Ch(int shift) => (uint)Math.Round((a >> shift & 0xFF) + ((float)(b >> shift & 0xFF) - (a >> shift & 0xFF)) * t);
            return Ch(24) << 24 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
        }
        static uint At(List<(float Offset, uint Color)> s, float t)
        {
            if (t <= s[0].Offset) return s[0].Color;
            for (int i = 1; i < s.Count; i++)
                if (t <= s[i].Offset)
                {
                    float span = s[i].Offset - s[i - 1].Offset;
                    return span <= 0 ? s[i].Color : Mix(s[i - 1].Color, s[i].Color, (t - s[i - 1].Offset) / span);
                }
            return s[^1].Color;
        }
        if (stops[0].Offset >= 0 && stops[^1].Offset <= 1) return;
        uint c0 = At(stops, 0), c1 = At(stops, 1);
        var kept = stops.Where(s => s.Offset > 0 && s.Offset < 1).ToList();
        stops.Clear();
        stops.Add((0, c0));
        stops.AddRange(kept);
        stops.Add((1, c1));
    }
}

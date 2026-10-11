using System.Globalization;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Css;

/// <summary>
/// A CSS linear-gradient(), radial-gradient() or repeating variant, as specified. Lengths that depend on the box are
/// kept until <see cref="Resolve"/> knows its size.
/// </summary>
internal sealed class CssGradient
{
    private enum RadialSize : byte { FarthestCorner, FarthestSide, ClosestCorner, ClosestSide, Explicit }

    public bool Radial, Repeating;
    private float? _angle;          // degrees, clockwise from "to top"
    private int _toX, _toY = 1;     // "to <side-or-corner>" when there is no angle: -1 left/top, 1 right/bottom
    private bool _circle;
    private RadialSize _size;
    private Length _sizeX, _sizeY;
    private Length _posX = new(50, LengthUnit.Percent), _posY = new(50, LengthUnit.Percent);
    private readonly List<(uint Color, Length? Position)> _stops = new();

    /// <summary>Parses one gradient function; null when it is not a gradient this engine draws.</summary>
    public static CssGradient? Parse(string value, uint currentColor, float em, float rem, float vw, float vh)
    {
        var v = value.Trim();
        int paren = v.IndexOf('(');
        if (paren < 0 || !v.EndsWith(")", StringComparison.Ordinal)) return null;
        var name = v.Substring(0, paren).Trim().ToLowerInvariant();
        var g = new CssGradient();
        switch (name)
        {
            case "linear-gradient": break;
            case "repeating-linear-gradient": g.Repeating = true; break;
            case "radial-gradient": g.Radial = true; break;
            case "repeating-radial-gradient": g.Radial = g.Repeating = true; break;
            default: return null; // -webkit-gradient() and prefixed legacy syntaxes are not supported
        }
        var args = CssParser.SplitTopLevel(v.Substring(paren + 1, v.Length - paren - 2), ',');
        if (args.Count == 0) return null;
        int first = 0;
        var head = CssParser.Tokens(args[0].Trim().ToLowerInvariant());
        if (head.Count > 0 && !CssColor.TryParse(head[0], currentColor, out _))
        {
            if (g.Radial ? !g.ParseRadialShape(head, em, rem, vw, vh) : !g.ParseDirection(head)) return null;
            first = 1;
        }
        for (int i = first; i < args.Count; i++)
        {
            var toks = CssParser.Tokens(args[i].Trim());
            if (toks.Count == 0) return null;
            if (!CssColor.TryParse(toks[0], currentColor, out uint color))
            {
                if (toks.Count == 1) continue; // a colour hint: interpolation midpoints are not supported
                return null;
            }
            if (toks.Count == 1) { g._stops.Add((color, null)); continue; }
            for (int k = 1; k < toks.Count && k < 3; k++)
            {
                if (!StyleResolver.TryParseLength(toks[k].ToLowerInvariant(), em, rem, out var pos, vw, vh) || pos.IsAuto) return null;
                g._stops.Add((color, pos));
            }
        }
        return g._stops.Count > 0 ? g : null;
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
        (string unit, float scale)[] units = { ("deg", 1), ("grad", 0.9f), ("rad", 180f / MathF.PI), ("turn", 360) };
        foreach (var (unit, scale) in units)
        {
            if (!a.EndsWith(unit, StringComparison.Ordinal)) continue;
            if (!float.TryParse(a.AsSpan(0, a.Length - unit.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return false;
            _angle = n * scale;
            return true;
        }
        if (a == "0") { _angle = 0; return true; }
        return false;
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
        if (i < t.Count)
        {
            // at <position>: one or two keywords or lengths.
            var pos = t.Skip(i + 1).ToList();
            if (pos.Count == 0 || pos.Count > 2) return false;
            if (pos.Count == 1 && pos[0] is "top" or "bottom")
            {
                _posY = Keyword(pos[0]);
                return true;
            }
            if (pos.Count == 2 && (pos[0] is "top" or "bottom" || pos[1] is "left" or "right")) (pos[0], pos[1]) = (pos[1], pos[0]);
            if (!PositionPart(pos[0], em, rem, vw, vh, out _posX)) return false;
            if (pos.Count > 1 && !PositionPart(pos[1], em, rem, vw, vh, out _posY)) return false;
        }
        return true;
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

    /// <summary>The gradient for a box of the given size, in box coordinates (origin at its top-left corner).</summary>
    public Gradient Resolve(float w, float h, uint backdrop)
    {
        var g = new Gradient { Radial = Radial, Spread = Repeating ? GradientSpread.Repeat : GradientSpread.Pad };
        float length; // the gradient line or ray, which stop lengths are measured against
        if (!Radial)
        {
            float dx, dy;
            if (_angle is { } deg)
            {
                double rad = deg * Math.PI / 180;
                dx = (float)Math.Sin(rad);
                dy = (float)-Math.Cos(rad);
            }
            else if (_toX != 0 && _toY != 0)
            {
                // Corner: perpendicular to the diagonal through the two other corners.
                float n = MathF.Sqrt(w * w + h * h);
                dx = n > 0 ? _toX * h / n : _toX;
                dy = n > 0 ? _toY * w / n : _toY;
            }
            else { dx = _toX; dy = _toY; }
            length = Math.Abs(w * dx) + Math.Abs(h * dy);
            g.X0 = w / 2 - dx * length / 2;
            g.Y0 = h / 2 - dy * length / 2;
            g.X1 = w / 2 + dx * length / 2;
            g.Y1 = h / 2 + dy * length / 2;
        }
        else
        {
            float cx = _posX.Resolve(w), cy = _posY.Resolve(h);
            float sx, sy; // closest or farthest sides
            bool closest = _size is RadialSize.ClosestSide or RadialSize.ClosestCorner;
            sx = closest ? Math.Min(Math.Abs(cx), Math.Abs(w - cx)) : Math.Max(Math.Abs(cx), Math.Abs(w - cx));
            sy = closest ? Math.Min(Math.Abs(cy), Math.Abs(h - cy)) : Math.Max(Math.Abs(cy), Math.Abs(h - cy));
            float rx, ry;
            switch (_size)
            {
                case RadialSize.Explicit:
                    rx = _sizeX.Resolve(w);
                    ry = _circle ? rx : _sizeY.Resolve(h);
                    break;
                case RadialSize.ClosestSide:
                case RadialSize.FarthestSide:
                    if (_circle) rx = ry = closest ? Math.Min(sx, sy) : Math.Max(sx, sy);
                    else { rx = sx; ry = sy; }
                    break;
                default:
                    if (_circle) rx = ry = MathF.Sqrt(sx * sx + sy * sy);
                    else { rx = sx * MathF.Sqrt(2); ry = sy * MathF.Sqrt(2); }
                    break;
            }
            if (!(rx > 0) || !(ry > 0)) { rx = ry = Math.Max(0.01f, Math.Max(rx, ry)); }
            g.R1 = length = rx;
            g.Transform = Matrix.Scale(1, ry / rx).Then(Matrix.Translate(cx, cy));
        }

        // Stop positions: missing ones are spread evenly between their neighbours.
        int count = _stops.Count;
        var pos = new float[count];
        var known = new bool[count];
        for (int i = 0; i < count; i++)
        {
            if (_stops[i].Position is not { } p) continue;
            pos[i] = p.IsPercent ? p.Value / 100f : length > 0 ? p.Value / length : 0;
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

        if (Repeating)
        {
            float start = pos[0], period = pos[count - 1] - pos[0];
            if (period <= 1e-4f) g.Spread = GradientSpread.Pad; // degenerate: painted as the last colour below
            else
            {
                // Move the geometry so that [first stop, last stop] becomes one period.
                if (Radial) { g.R0 = g.R1 * start; g.R1 *= pos[count - 1]; }
                else
                {
                    float x0 = g.X0, y0 = g.Y0, ddx = g.X1 - g.X0, ddy = g.Y1 - g.Y0;
                    g.X0 = x0 + ddx * start; g.Y0 = y0 + ddy * start;
                    g.X1 = x0 + ddx * pos[count - 1]; g.Y1 = y0 + ddy * pos[count - 1];
                }
                for (int i = 0; i < count; i++) pos[i] = (pos[i] - start) / period;
            }
        }
        if (Radial) { g.X0 = g.X1; g.Y0 = g.Y1; }

        var stops = new List<(float, uint)>(Math.Max(2, count));
        if (count == 1 || Repeating && g.Spread == GradientSpread.Pad)
        {
            stops.Add((0, _stops[count - 1].Color));
            stops.Add((1, _stops[count - 1].Color));
        }
        else
        {
            // Offsets past [0, 1] are clipped, keeping the colour interpolated at the edge.
            for (int i = 0; i < count; i++) stops.Add((pos[i], _stops[i].Color));
            ClipStops(stops);
        }
        (g.Offsets, g.Colors) = Gradient.NormalizeStops(stops, backdrop);
        return g;
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

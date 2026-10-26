using System.Globalization;
using PdfPecker.Pdf;

namespace PdfPecker.Css;

/// <summary>
/// The legacy gradient syntaxes that Chrome still draws: the prefixed -webkit-linear-gradient() and
/// -webkit-radial-gradient() (and their repeating variants), whose first arguments say where the gradient starts rather
/// than where it goes, and the original -webkit-gradient(), which runs between two points or two circles.
/// </summary>
internal sealed partial class CssGradient
{
    // The largest -webkit-gradient() radius, in pixels; anything larger paints the same on a page.
    private const float MaxLegacyRadius = 1e6f;
    // -webkit-gradient() numbers are CSS pixels.
    private const float PointsPerPixel = 0.75f;

    private bool _alongDiagonal;    // a prefixed gradient towards a corner, which runs along the diagonal
    private Length[]? _points;      // -webkit-gradient(): the start and end points (x0, y0, x1, y1)
    private float _r0, _r1;         // -webkit-gradient(radial): the radii of the start and end circles

    /// <summary>
    /// The arguments before the colour stops of a prefixed gradient. A linear one takes an angle, counted
    /// anticlockwise from the right, or the side or corner it starts from. A radial one takes a position, then a
    /// shape and size, each as its own argument and each optional; contain means closest-side and cover
    /// farthest-corner.
    /// </summary>
    private bool ParsePrefixedHead(List<string> args, uint currentColor, ref int first, float em, float rem, float vw, float vh)
    {
        var head = CssParser.Tokens(args[0].Trim().ToLowerInvariant());
        if (head.Count == 0 || CssColor.TryParse(head[0], currentColor, out _)) return true;
        if (Kind == GradientKind.Linear)
        {
            first = 1;
            if (head.Count == 1 && head[0] is not ("left" or "right" or "top" or "bottom" or "to"))
            {
                if (!ParseDirection(head)) return false;
                _angle = 90 - _angle;
                return true;
            }
            return ParseStartSide(head);
        }
        var (x, y) = (_posX, _posY);
        if (!ParsePosition(head, em, rem, vw, vh)) (_posX, _posY) = (x, y);
        else
        {
            first = 1;
            if (args.Count < 2) return false;
            head = CssParser.Tokens(args[1].Trim().ToLowerInvariant());
            if (head.Count == 0 || CssColor.TryParse(head[0], currentColor, out _)) return true;
        }
        if (!ParsePrefixedShape(head, em, rem, vw, vh)) return false;
        first++;
        return true;
    }

    /// <summary>The side or corner a prefixed linear gradient starts from, one horizontal and one vertical keyword at most.</summary>
    private bool ParseStartSide(List<string> t)
    {
        if (t.Count > 2) return false;
        _toX = _toY = 0;
        foreach (var k in t)
        {
            switch (k)
            {
                case "left" when _toX == 0: _toX = 1; break;
                case "right" when _toX == 0: _toX = -1; break;
                case "top" when _toY == 0: _toY = 1; break;
                case "bottom" when _toY == 0: _toY = -1; break;
                default: return false;
            }
        }
        _alongDiagonal = _toX != 0 && _toY != 0;
        return true;
    }

    /// <summary>The shape and size of a prefixed radial gradient: keywords in any order, or a width and a height.</summary>
    private bool ParsePrefixedShape(List<string> t, float em, float rem, float vw, float vh)
    {
        if (t.Count == 2 && StyleResolver.TryParseLength(t[0], em, rem, out var x, vw, vh) && !x.IsAuto
            && StyleResolver.TryParseLength(t[1], em, rem, out var y, vw, vh) && !y.IsAuto)
        {
            (_size, _sizeX, _sizeY) = (RadialSize.Explicit, x, y);
            return true;
        }
        bool shape = false, size = false;
        foreach (var k in t)
        {
            if (k is "circle" or "ellipse" && !shape) { _circle = k == "circle"; shape = true; continue; }
            if (size) return false;
            size = true;
            switch (k)
            {
                case "closest-side" or "contain": _size = RadialSize.ClosestSide; break;
                case "closest-corner": _size = RadialSize.ClosestCorner; break;
                case "farthest-side": _size = RadialSize.FarthestSide; break;
                case "farthest-corner" or "cover": _size = RadialSize.FarthestCorner; break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>
    /// -webkit-gradient(linear, point, point, stops) or -webkit-gradient(radial, point, radius, point, radius, stops).
    /// A point is a horizontal and a vertical keyword, number of pixels or percentage; a radius is a number of pixels;
    /// a stop is from(colour), to(colour) or color-stop(offset, colour), with the offset a fraction or a percentage.
    /// The stops are sorted by offset, as Chrome sorts them, and a gradient without stops is transparent.
    /// </summary>
    private static CssGradient? ParseWebkitGradient(List<string> args, uint currentColor)
    {
        if (args.Count < 3) return null;
        var type = args[0].Trim().ToLowerInvariant();
        if (type is not ("linear" or "radial")) return null;
        var g = new CssGradient { Kind = type == "radial" ? GradientKind.Radial : GradientKind.Linear, _points = new Length[4] };
        int next;
        if (g.Kind == GradientKind.Linear)
        {
            if (!LegacyPoint(args[1], g._points, 0) || !LegacyPoint(args[2], g._points, 2)) return null;
            next = 3;
        }
        else
        {
            if (args.Count < 5 || !LegacyPoint(args[1], g._points, 0) || !LegacyRadius(args[2], out g._r0)
                || !LegacyPoint(args[3], g._points, 2) || !LegacyRadius(args[4], out g._r1)) return null;
            next = 5;
        }
        var stops = new List<(float Offset, uint Color)>();
        for (int i = next; i < args.Count; i++)
        {
            if (!LegacyStop(args[i].Trim(), currentColor, out var stop)) return null;
            stops.Add(stop);
        }
        // Without stops the gradient is valid and paints nothing.
        if (stops.Count == 0) stops.Add((0, CssColor.Transparent));
        // A stable sort, so stops at the same offset keep their order and make a hard edge.
        foreach (var (offset, color) in stops.Select((s, i) => (s, i)).OrderBy(p => p.s.Offset).ThenBy(p => p.i).Select(p => p.s))
            g._stops.Add((color, new Length(offset * 100, LengthUnit.Percent)));
        return g;
    }

    /// <summary>One point of -webkit-gradient(), written into <paramref name="points"/> at <paramref name="at"/>.</summary>
    private static bool LegacyPoint(string arg, Length[] points, int at)
    {
        var t = CssParser.Tokens(arg.Trim().ToLowerInvariant());
        if (t.Count != 2) return false;
        return LegacyCoordinate(t[0], "left", "right", out points[at]) && LegacyCoordinate(t[1], "top", "bottom", out points[at + 1]);
    }

    private static bool LegacyCoordinate(string t, string start, string end, out Length value)
    {
        if (t == start || t == end || t == "center")
        {
            value = new Length(t == start ? 0 : t == end ? 100 : 50, LengthUnit.Percent);
            return true;
        }
        bool percent = t.EndsWith('%');
        bool ok = float.TryParse(percent ? t.AsSpan(0, t.Length - 1) : t.AsSpan(), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) && float.IsFinite(n);
        value = new Length(!ok ? 0 : percent ? n : n * PointsPerPixel, percent ? LengthUnit.Percent : LengthUnit.Pt);
        return ok;
    }

    private static bool LegacyRadius(string arg, out float radius)
    {
        if (!float.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || !(radius >= 0)) return false;
        radius = Math.Min(radius, MaxLegacyRadius) * PointsPerPixel;
        return true;
    }

    /// <summary>from(colour), to(colour) or color-stop(offset, colour); the offset is clamped to the gradient line.</summary>
    private static bool LegacyStop(string arg, uint currentColor, out (float Offset, uint Color) stop)
    {
        stop = default;
        int paren = arg.IndexOf('(');
        if (paren < 0 || !arg.EndsWith(')')) return false;
        var name = arg.Substring(0, paren).Trim().ToLowerInvariant();
        var inner = arg.Substring(paren + 1, arg.Length - paren - 2);
        if (name is "from" or "to")
        {
            if (!CssColor.TryParse(inner.Trim(), currentColor, out uint color)) return false;
            stop = (name == "from" ? 0 : 1, color);
            return true;
        }
        if (name != "color-stop") return false;
        var parts = CssParser.SplitTopLevel(inner, ',');
        if (parts.Count != 2) return false;
        var o = parts[0].Trim();
        bool percent = o.EndsWith('%');
        if (!float.TryParse(percent ? o.AsSpan(0, o.Length - 1) : o.AsSpan(), NumberStyles.Float, CultureInfo.InvariantCulture, out float offset)
            || !float.IsFinite(offset) || !CssColor.TryParse(parts[1].Trim(), currentColor, out uint c)) return false;
        stop = (Math.Clamp(percent ? offset / 100 : offset, 0, 1), c);
        return true;
    }

    /// <summary>The line or the two circles of -webkit-gradient() in the box; the stops are fractions, so the length is 1.</summary>
    private float ResolvePoints(Gradient g, float w, float h)
    {
        var p = _points!;
        g.X0 = p[0].Resolve(w);
        g.Y0 = p[1].Resolve(h);
        g.X1 = p[2].Resolve(w);
        g.Y1 = p[3].Resolve(h);
        if (Radial) (g.R0, g.R1) = (_r0, _r1);
        return 1;
    }
}

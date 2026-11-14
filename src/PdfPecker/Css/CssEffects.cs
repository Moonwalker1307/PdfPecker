using System.Globalization;
using PdfPecker.Svg;

namespace PdfPecker.Css;

/// <summary>The reference box of a clip-path shape.</summary>
internal enum ShapeBox : byte { BorderBox, PaddingBox, ContentBox, MarginBox }

/// <summary>
/// A clip-path basic shape on an HTML element: inset(), rect(), xywh(), circle(), ellipse(), polygon(), path() or shape(),
/// or only a reference box. Lengths stay unresolved until the reference box is known.
/// </summary>
internal sealed class ClipShape
{
    public enum ShapeKind : byte { Box, Inset, Circle, Ellipse, Polygon, Path }

    // How the four lengths of an inset-like shape are written: inset() as distances in from each side, rect() as the
    // top, right, bottom and left edges measured from the box's top and left, xywh() as a position and a size.
    private enum InsetForm : byte { Inset, Rect, Xywh }

    // A radius keyword of circle() and ellipse(), kept apart from lengths.
    private const float ClosestSide = -1, FarthestSide = -2;

    public ShapeKind Kind;
    public ShapeBox Box;
    public bool EvenOdd;
    // inset: top, right, bottom, left. circle: radius, then the centre. ellipse: rx, ry, then the centre.
    // polygon: x, y pairs. The centre is two lengths measured from the left and top.
    private Length[] _args = Array.Empty<Length>();
    private float[] _keywords = Array.Empty<float>(); // ClosestSide / FarthestSide per radius, 0 when it is a length
    private Length[]? _round;                         // inset(... round ...): 8 radii as border-radius stores them
    private InsetForm _form;                          // inset(), rect() or xywh(), which all resolve to an inset
    private PathBuilder? _path;                       // path(): CSS pixels from the reference box's top-left corner
    private ShapeCommand[]? _commands;                // shape(): the start and the commands, resolved once the box is known

    /// <summary>
    /// Parses a clip-path value other than none and url(), or a shape-outside shape, whose reference box defaults to the
    /// margin box and may not be an SVG box; null when it is not a shape this library knows.
    /// </summary>
    public static ClipShape? Parse(string value, float em, float rem, float vw, float vh, bool shapeOutside = false)
    {
        ClipShape? shape = null;
        ShapeBox? box = null;
        foreach (var token in CssParser.Tokens(value.Trim()))
        {
            var lower = token.ToLowerInvariant();
            if (BoxKeyword(lower) is { } b)
            {
                if (box != null || shapeOutside && lower is "stroke-box" or "view-box" or "fill-box") return null;
                box = b;
                continue;
            }
            int open = token.IndexOf('(');
            if (shape != null || open <= 0 || token[^1] != ')') return null;
            var name = lower.Substring(0, open);
            var args = token.Substring(open + 1, token.Length - open - 2).Trim();
            shape = name switch
            {
                "inset" => ParseInset(args.ToLowerInvariant(), em, rem, vw, vh),
                "rect" => ParseInset(args.ToLowerInvariant(), em, rem, vw, vh, InsetForm.Rect),
                "xywh" => ParseInset(args.ToLowerInvariant(), em, rem, vw, vh, InsetForm.Xywh),
                "circle" => ParseRound(args.ToLowerInvariant(), em, rem, vw, vh, ellipse: false),
                "ellipse" => ParseRound(args.ToLowerInvariant(), em, rem, vw, vh, ellipse: true),
                "polygon" => ParsePolygon(args.ToLowerInvariant(), em, rem, vw, vh),
                "path" => ParsePath(args),
                "shape" => ParseShape(args.ToLowerInvariant(), em, rem, vw, vh),
                _ => null,
            };
            if (shape == null) return null;
        }
        if (shape == null && box == null) return null;
        shape ??= new ClipShape { Kind = ShapeKind.Box };
        shape.Box = box ?? (shapeOutside ? ShapeBox.MarginBox : ShapeBox.BorderBox);
        return shape;
    }

    private static ShapeBox? BoxKeyword(string s) => s switch
    {
        "border-box" or "stroke-box" or "view-box" => ShapeBox.BorderBox,
        "padding-box" => ShapeBox.PaddingBox,
        "content-box" or "fill-box" => ShapeBox.ContentBox,
        "margin-box" => ShapeBox.MarginBox,
        _ => null,
    };

    private static bool Len(string t, float em, float rem, float vw, float vh, out Length len)
        => StyleResolver.TryParseLength(t, em, rem, out len, vw, vh) && !len.IsAuto;

    private static ClipShape? ParseInset(string args, float em, float rem, float vw, float vh, InsetForm form = InsetForm.Inset)
    {
        var tokens = CssParser.Tokens(args);
        int round = tokens.IndexOf("round");
        var sides = new List<Length>(4);
        for (int i = 0; i < (round < 0 ? tokens.Count : round); i++)
        {
            if (sides.Count == 4) return null;
            // rect() takes auto for any edge: the reference box's own edge on that side.
            if (form == InsetForm.Rect && tokens[i] == "auto") sides.Add(new Length(sides.Count is 1 or 2 ? 100 : 0, LengthUnit.Percent));
            else if (!Len(tokens[i], em, rem, vw, vh, out var l)) return null;
            else
            {
                // xywh() gives the width and height last, and neither may be negative.
                if (form == InsetForm.Xywh && sides.Count >= 2 && l.Value < 0) return null;
                sides.Add(l);
            }
        }
        if (form == InsetForm.Inset ? sides.Count == 0 : sides.Count != 4) return null;
        var shape = new ClipShape { Kind = ShapeKind.Inset, _form = form, _args = Expand4(sides) };
        if (round >= 0)
        {
            // The radii follow the border-radius syntax: up to four horizontal radii, then "/" and up to four vertical ones.
            var halves = string.Join(" ", tokens.Skip(round + 1)).Split('/');
            if (halves.Length > 2) return null;
            var h = new List<Length>(4);
            var v = new List<Length>(4);
            for (int half = 0; half < halves.Length; half++)
                foreach (var t in CssParser.Tokens(halves[half]))
                {
                    var into = half == 0 ? h : v;
                    if (!Len(t, em, rem, vw, vh, out var l) || l.Value < 0 || into.Count == 4) return null;
                    into.Add(l);
                }
            if (halves.Length == 2 && v.Count == 0) return null;
            if (h.Count == 0) return null;
            var hs = Expand4(h);
            var vs = v.Count > 0 ? Expand4(v) : hs;
            shape._round = new[] { hs[0], vs[0], hs[1], vs[1], hs[2], vs[2], hs[3], vs[3] };
        }
        return shape;
    }

    private static Length[] Expand4(List<Length> l) => l.Count switch
    {
        1 => new[] { l[0], l[0], l[0], l[0] },
        2 => new[] { l[0], l[1], l[0], l[1] },
        3 => new[] { l[0], l[1], l[2], l[1] },
        _ => new[] { l[0], l[1], l[2], l[3] },
    };

    private static ClipShape? ParseRound(string args, float em, float rem, float vw, float vh, bool ellipse)
    {
        var tokens = CssParser.Tokens(args);
        int at = tokens.IndexOf("at");
        int radii = ellipse ? 2 : 1;
        var lengths = new List<Length>();
        var keywords = new List<float>();
        for (int i = 0; i < (at < 0 ? tokens.Count : at); i++)
        {
            if (lengths.Count == radii) return null;
            if (tokens[i] == "closest-side") { lengths.Add(Length.Zero); keywords.Add(ClosestSide); }
            else if (tokens[i] == "farthest-side") { lengths.Add(Length.Zero); keywords.Add(FarthestSide); }
            else if (Len(tokens[i], em, rem, vw, vh, out var l) && l.Value >= 0) { lengths.Add(l); keywords.Add(0); }
            else return null;
        }
        if (lengths.Count != 0 && lengths.Count != radii) return null;
        while (lengths.Count < radii) { lengths.Add(Length.Zero); keywords.Add(ClosestSide); }
        Length cx = new(50, LengthUnit.Percent), cy = new(50, LengthUnit.Percent);
        if (at >= 0 && !ParsePosition(tokens.Skip(at + 1).ToList(), em, rem, vw, vh, out cx, out cy)) return null;
        lengths.Add(cx);
        lengths.Add(cy);
        return new ClipShape { Kind = ellipse ? ShapeKind.Ellipse : ShapeKind.Circle, _args = lengths.ToArray(), _keywords = keywords.ToArray() };
    }

    /// <summary>A position of one to four values, as both offsets from the left and top edges.</summary>
    private static bool ParsePosition(List<string> t, float em, float rem, float vw, float vh, out Length x, out Length y)
    {
        x = y = new Length(50, LengthUnit.Percent);
        static float? Keyword(string s) => s switch { "left" or "top" => 0, "center" => 50, "right" or "bottom" => 100, _ => null };
        static bool Vertical(string s) => s is "top" or "bottom";
        static bool Horizontal(string s) => s is "left" or "right";
        if (t.Count == 0 || t.Count > 4) return false;
        if (t.Count == 1)
        {
            if (Keyword(t[0]) is { } k)
            {
                if (Vertical(t[0])) y = new Length(k, LengthUnit.Percent);
                else x = new Length(k, LengthUnit.Percent);
                return true;
            }
            return Len(t[0], em, rem, vw, vh, out x);
        }
        if (t.Count == 2)
        {
            // Two keywords may come in either order; otherwise the first is horizontal.
            bool swap = Vertical(t[0]) || Horizontal(t[1]);
            string hx = swap ? t[1] : t[0], vy = swap ? t[0] : t[1];
            if (Vertical(hx) || Horizontal(vy)) return false;
            if (Keyword(hx) is { } kx) x = new Length(kx, LengthUnit.Percent);
            else if (!Len(hx, em, rem, vw, vh, out x)) return false;
            if (Keyword(vy) is { } ky) y = new Length(ky, LengthUnit.Percent);
            else if (!Len(vy, em, rem, vw, vh, out y)) return false;
            return true;
        }
        // Three or four values: keywords, each followed by an optional offset from that edge.
        bool haveX = false, haveY = false;
        for (int i = 0; i < t.Count; i++)
        {
            if (Keyword(t[i]) is not { } k) return false;
            Length off = Length.Zero;
            bool hasOffset = i + 1 < t.Count && Keyword(t[i + 1]) == null;
            if (hasOffset && !Len(t[i + 1], em, rem, vw, vh, out off)) return false;
            // An offset from the right or bottom is measured back from 100%.
            bool far = t[i] is "right" or "bottom";
            Length v = !hasOffset ? new Length(k, LengthUnit.Percent)
                : far ? (off.IsPercent && off.Unit == LengthUnit.Percent ? new Length(100 - off.Value, LengthUnit.Percent) : Length.Calc(-off.Value, 100))
                : off;
            if (t[i] == "center") { if (!haveX) { x = v; haveX = true; } else { y = v; haveY = true; } }
            else if (Vertical(t[i])) { if (haveY) return false; y = v; haveY = true; }
            else { if (haveX) return false; x = v; haveX = true; }
            if (hasOffset) i++;
        }
        return true;
    }

    private static ClipShape? ParsePolygon(string args, float em, float rem, float vw, float vh)
    {
        var shape = new ClipShape { Kind = ShapeKind.Polygon };
        var parts = CssParser.SplitTopLevel(args, ',');
        var pts = new List<Length>();
        for (int i = 0; i < parts.Count; i++)
        {
            var tokens = CssParser.Tokens(parts[i].Trim());
            if (i == 0 && tokens.Count == 1 && tokens[0] is "evenodd" or "nonzero")
            {
                shape.EvenOdd = tokens[0] == "evenodd";
                continue;
            }
            if (tokens.Count != 2 || !Len(tokens[0], em, rem, vw, vh, out var x) || !Len(tokens[1], em, rem, vw, vh, out var y)) return null;
            pts.Add(x);
            pts.Add(y);
        }
        if (pts.Count < 2) return null;
        shape._args = pts.ToArray();
        return shape;
    }

    private static ClipShape? ParsePath(string args)
    {
        var shape = new ClipShape { Kind = ShapeKind.Path };
        var parts = CssParser.SplitTopLevel(args, ',');
        if (parts.Count == 2)
        {
            var rule = parts[0].Trim().ToLowerInvariant();
            if (rule is not ("evenodd" or "nonzero")) return null;
            shape.EvenOdd = rule == "evenodd";
            parts.RemoveAt(0);
        }
        if (parts.Count != 1) return null;
        var d = parts[0].Trim();
        if (d.Length < 2 || d[0] is not ('"' or '\'') || d[^1] != d[0]) return null;
        shape._path = PathParser.Parse(d.Substring(1, d.Length - 2));
        return shape._path == null ? null : shape;
    }

    /// <summary>
    /// The shape as a path in points, for the reference box (x, y, w, h); <paramref name="radii"/> are the box's rounded
    /// corners, which a shape that is only a box keeps.
    /// </summary>
    public (byte[] Verbs, float[] Points) Resolve(float x, float y, float w, float h, Layout.CornerRadii? radii)
    {
        var p = new PathBuilder();
        switch (Kind)
        {
            case ShapeKind.Box:
                RoundedRect(p, x, y, w, h, radii);
                break;
            case ShapeKind.Inset:
            {
                var (ix, iy, iw, ih, cr) = ResolveInset(w, h);
                RoundedRect(p, x + ix, y + iy, iw, ih, cr);
                break;
            }
            case ShapeKind.Circle:
            case ShapeKind.Ellipse:
            {
                var (cx, cy, rx, ry) = ResolveEllipse(w, h);
                Ellipse(p, x + cx, y + cy, rx, ry);
                break;
            }
            case ShapeKind.Polygon:
                for (int i = 0; i + 1 < _args.Length; i += 2)
                {
                    float px = x + _args[i].Resolve(w), py = y + _args[i + 1].Resolve(h);
                    if (i == 0) p.MoveTo(px, py);
                    else p.LineTo(px, py);
                }
                p.Close();
                break;
            case ShapeKind.Path:
            {
                // Path data is in CSS pixels from the box's top-left corner.
                var src = PathIn(w, h)!;
                p.Verbs.AddRange(src.Verbs);
                for (int i = 0; i + 1 < src.Points.Count; i += 2)
                {
                    p.Points.Add(x + src.Points[i] * 0.75f);
                    p.Points.Add(y + src.Points[i + 1] * 0.75f);
                }
                break;
            }
        }
        return (p.Verbs.ToArray(), p.Points.ToArray());
    }

    /// <summary>An inset(), rect() or xywh() shape in a w × h reference box: its rectangle from the box's top-left corner and its radii.</summary>
    public (float X, float Y, float W, float H, Layout.CornerRadii? Radii) ResolveInset(float w, float h)
    {
        float t, r, b, l;
        switch (_form)
        {
            case InsetForm.Rect:
                t = _args[0].Resolve(h); r = w - _args[1].Resolve(w); b = h - _args[2].Resolve(h); l = _args[3].Resolve(w);
                break;
            case InsetForm.Xywh:
                l = _args[0].Resolve(w); t = _args[1].Resolve(h); r = w - l - _args[2].Resolve(w); b = h - t - _args[3].Resolve(h);
                break;
            default:
                t = _args[0].Resolve(h); r = _args[1].Resolve(w); b = _args[2].Resolve(h); l = _args[3].Resolve(w);
                break;
        }
        // Insets that add up to more than the box are scaled down together.
        if (l + r > w && l + r > 0) { float k = w / (l + r); l *= k; r *= k; }
        if (t + b > h && t + b > 0) { float k = h / (t + b); t *= k; b *= k; }
        float iw = Math.Max(0, w - l - r), ih = Math.Max(0, h - t - b);
        Layout.CornerRadii? cr = null;
        if (_round is { } rr)
            // As in Chrome, percentages refer to the reference box, not to the inset rectangle, before the radii are fitted.
            cr = Layout.CornerRadii.Fit(rr[0].Resolve(w), rr[1].Resolve(h), rr[2].Resolve(w), rr[3].Resolve(h),
                rr[4].Resolve(w), rr[5].Resolve(h), rr[6].Resolve(w), rr[7].Resolve(h), iw, ih);
        return (l, t, iw, ih, cr);
    }

    /// <summary>A circle() or ellipse() in a w × h reference box: its centre from the box's top-left corner and its radii.</summary>
    public (float Cx, float Cy, float Rx, float Ry) ResolveEllipse(float w, float h)
    {
        bool circle = Kind == ShapeKind.Circle;
        int c = circle ? 1 : 2;
        float cx = _args[c].Resolve(w), cy = _args[c + 1].Resolve(h);
        float Radius(int i, float extent, float centre)
        {
            if (_keywords[i] == ClosestSide) return Math.Max(0, Math.Min(centre, extent - centre));
            if (_keywords[i] == FarthestSide) return Math.Max(centre, extent - centre);
            return _args[i].Resolve(extent);
        }
        float rx, ry;
        if (circle)
        {
            if (_keywords[0] == ClosestSide) rx = Math.Max(0, Math.Min(Math.Min(cx, w - cx), Math.Min(cy, h - cy)));
            else if (_keywords[0] == FarthestSide) rx = Math.Max(Math.Max(cx, w - cx), Math.Max(cy, h - cy));
            // A percentage is of the box's diagonal divided by the square root of two.
            else rx = _args[0].Resolve(MathF.Sqrt((w * w + h * h) / 2));
            ry = rx;
        }
        else
        {
            rx = Radius(0, w, cx);
            ry = Radius(1, h, cy);
        }
        return (cx, cy, rx, ry);
    }

    /// <summary>The corners of a polygon() in a w × h reference box, as x, y pairs from the box's top-left corner.</summary>
    public float[] ResolvePolygon(float w, float h)
    {
        var points = new float[_args.Length & ~1];
        for (int i = 0; i + 1 < _args.Length; i += 2)
        {
            points[i] = _args[i].Resolve(w);
            points[i + 1] = _args[i + 1].Resolve(h);
        }
        return points;
    }

    /// <summary>
    /// The data of a path() or shape() shape in a w × h point reference box, in CSS pixels from the box's top-left
    /// corner.
    /// </summary>
    public PathBuilder? PathIn(float w, float h)
    {
        if (_commands == null) return _path;
        var p = new PathBuilder();
        BuildShape(p, _commands, w, h);
        return p;
    }

    // The control point distance of a quarter circle as a cubic Bézier curve.
    private const float Kappa = 0.5523f;

    // ------------------------------------------------------------------ shape() (CSS Shapes 2 §3.1)

    private enum Command : byte { From, Move, Line, HLine, VLine, Curve, Smooth, Arc, Close }

    // Where a control point is measured from: the default (the reference box for "to", the command's start for "by"),
    // or the start, the end or the reference box's origin, as "from" says.
    private enum Anchor : byte { Default, Start, End, Origin }

    /// <summary>One command of a shape(): its end point and, by kind, its control points or its arc.</summary>
    private sealed class ShapeCommand
    {
        public Command Kind;
        public bool By;
        // The end point; hline and vline use only the coordinate of their axis.
        public Length X, Y;
        public Length[] Controls = Array.Empty<Length>();
        public Anchor[] Anchors = Array.Empty<Anchor>();
        public Length Rx, Ry;
        // One arc radius given: the other is the same, and a percentage is of the box's diagonal over the root of two.
        public bool OneRadius;
        public bool Clockwise, Large;
        public float Rotate;
    }

    private static ClipShape? ParseShape(string args, float em, float rem, float vw, float vh)
    {
        var parts = CssParser.SplitTopLevel(args, ',');
        if (parts.Count < 2) return null;
        var shape = new ClipShape { Kind = ShapeKind.Path };
        var head = CssParser.Tokens(parts[0].Trim());
        if (head.Count > 0 && head[0] is "evenodd" or "nonzero")
        {
            shape.EvenOdd = head[0] == "evenodd";
            head.RemoveAt(0);
        }
        if (head.Count < 2 || head[0] != "from" || !ParsePosition(head.GetRange(1, head.Count - 1), em, rem, vw, vh, out var fx, out var fy))
            return null;
        var commands = new ShapeCommand[parts.Count];
        commands[0] = new ShapeCommand { Kind = Command.From, X = fx, Y = fy };
        for (int i = 1; i < parts.Count; i++)
            if ((commands[i] = ParseCommand(CssParser.Tokens(parts[i].Trim()), em, rem, vw, vh)!) == null) return null;
        shape._commands = commands;
        return shape;
    }

    private static ShapeCommand? ParseCommand(List<string> t, float em, float rem, float vw, float vh)
    {
        if (t is ["close"]) return new ShapeCommand { Kind = Command.Close };
        if (t.Count < 3 || t[1] is not ("to" or "by")) return null;
        var c = new ShapeCommand { By = t[1] == "by" };
        c.Kind = t[0] switch
        {
            "move" => Command.Move, "line" => Command.Line, "hline" => Command.HLine, "vline" => Command.VLine,
            "curve" => Command.Curve, "smooth" => Command.Smooth, "arc" => Command.Arc,
            _ => Command.Close,
        };
        if (c.Kind == Command.Close) return null;
        if (c.Kind is Command.HLine or Command.VLine)
        {
            if (t.Count != 3) return null;
            bool h = c.Kind == Command.HLine;
            float? keyword = c.By ? null : t[2] switch
            {
                "center" => 50,
                "left" or "x-start" when h => 0, "right" or "x-end" when h => 100,
                "top" or "y-start" when !h => 0, "bottom" or "y-end" when !h => 100,
                _ => null,
            };
            Length v;
            if (keyword is { } k) v = new Length(k, LengthUnit.Percent);
            else if (!Len(t[2], em, rem, vw, vh, out v)) return null;
            if (h) c.X = v;
            else c.Y = v;
            return c;
        }
        // The end point runs up to "with" (curves) or "of" (arcs).
        int end = 2;
        while (end < t.Count && t[end] is not ("with" or "of")) end++;
        if (!Point(t.GetRange(2, end - 2), c.By, em, rem, vw, vh, out c.X, out c.Y)) return null;
        var rest = t.GetRange(end, t.Count - end);
        switch (c.Kind)
        {
            case Command.Move or Command.Line:
                return rest.Count == 0 ? c : null;
            case Command.Curve or Command.Smooth:
            {
                if (rest.Count == 0) return c.Kind == Command.Smooth ? c : null;
                if (rest[0] != "with") return null;
                var halves = string.Join(" ", rest.Skip(1)).Split('/');
                if (halves.Length > (c.Kind == Command.Curve ? 2 : 1)) return null;
                var controls = new List<Length>(4);
                var anchors = new List<Anchor>(2);
                foreach (var half in halves)
                {
                    var ct = CssParser.Tokens(half.Trim());
                    var anchor = Anchor.Default;
                    if (ct.Count >= 4 && ct[^2] == "from")
                    {
                        anchor = ct[^1] switch { "start" => Anchor.Start, "end" => Anchor.End, "origin" => Anchor.Origin, _ => Anchor.Default };
                        if (anchor == Anchor.Default || ct.Count != 4) return null;
                        ct.RemoveRange(2, 2);
                    }
                    Length x, y;
                    if (anchor != Anchor.Default)
                    {
                        if (!Len(ct[0], em, rem, vw, vh, out x) || !Len(ct[1], em, rem, vw, vh, out y)) return null;
                    }
                    // Without "from", a control point of "to" is a position in the box, and of "by" a pair from the start.
                    else if (!Point(ct, c.By, em, rem, vw, vh, out x, out y)) return null;
                    controls.Add(x);
                    controls.Add(y);
                    anchors.Add(anchor);
                }
                c.Controls = controls.ToArray();
                c.Anchors = anchors.ToArray();
                return c;
            }
            default:
            {
                if (rest.Count < 2 || rest[0] != "of") return null;
                int i = 1;
                if (!Len(rest[i++], em, rem, vw, vh, out c.Rx) || c.Rx.Value < 0) return null;
                if (i < rest.Count && Len(rest[i], em, rem, vw, vh, out var ry))
                {
                    if (ry.Value < 0) return null;
                    c.Ry = ry;
                    i++;
                }
                else (c.Ry, c.OneRadius) = (c.Rx, true);
                bool sweep = false, size = false, rotate = false;
                for (; i < rest.Count; i++)
                {
                    switch (rest[i])
                    {
                        case "cw" or "ccw" when !sweep: (c.Clockwise, sweep) = (rest[i] == "cw", true); break;
                        case "large" or "small" when !size: (c.Large, size) = (rest[i] == "large", true); break;
                        case "rotate" when !rotate && i + 1 < rest.Count && CssMath.TryParseAngle(rest[i + 1], out float a) && float.IsFinite(a):
                            (c.Rotate, rotate) = (a, true);
                            i++;
                            break;
                        default: return null;
                    }
                }
                return c;
            }
        }
    }

    /// <summary>A point of a command: a position in the reference box for "to", a pair of lengths for "by".</summary>
    private static bool Point(List<string> t, bool by, float em, float rem, float vw, float vh, out Length x, out Length y)
    {
        if (!by) return ParsePosition(t, em, rem, vw, vh, out x, out y);
        x = y = Length.Zero;
        return t.Count == 2 && Len(t[0], em, rem, vw, vh, out x) && Len(t[1], em, rem, vw, vh, out y);
    }

    /// <summary>Draws a shape() into <paramref name="p"/> in CSS pixels, for a w × h point reference box.</summary>
    private static void BuildShape(PathBuilder p, ShapeCommand[] commands, float w, float h)
    {
        const float Px = 1 / 0.75f;
        float diagonal = MathF.Sqrt((w * w + h * h) / 2);
        float sx = 0, sy = 0, cx = 0, cy = 0;          // the subpath's start and the current point, in pixels
        float lastX = 0, lastY = 0;                    // the last curve's second control point, for smooth
        // What the last command was: 1 a cubic curve, 2 a quadratic one. As in SVG, a smooth cubic mirrors only a cubic's
        // control point and a smooth quadratic only a quadratic's; otherwise the control point is the current point.
        int last = 0;
        foreach (var c in commands)
        {
            float ex = cx, ey = cy;
            if (c.Kind != Command.Close)
            {
                float dx = c.X.Resolve(w) * Px, dy = c.Y.Resolve(h) * Px;
                if (c.Kind == Command.HLine) ex = c.By ? cx + dx : dx;
                else if (c.Kind == Command.VLine) ey = c.By ? cy + dy : dy;
                else (ex, ey) = c.By ? (cx + dx, cy + dy) : (dx, dy);
            }
            (float X, float Y) Control(int i)
            {
                float x = c.Controls[2 * i].Resolve(w) * Px, y = c.Controls[2 * i + 1].Resolve(h) * Px;
                return c.Anchors[i] switch
                {
                    Anchor.Start => (cx + x, cy + y),
                    Anchor.End => (ex + x, ey + y),
                    Anchor.Origin => (x, y),
                    _ => c.By ? (cx + x, cy + y) : (x, y),
                };
            }
            int curve = 0;
            switch (c.Kind)
            {
                case Command.From or Command.Move:
                    p.MoveTo(ex, ey);
                    (sx, sy) = (ex, ey);
                    break;
                case Command.Line or Command.HLine or Command.VLine:
                    p.LineTo(ex, ey);
                    break;
                case Command.Curve when c.Anchors.Length == 2:
                {
                    var (x1, y1) = Control(0);
                    var (x2, y2) = Control(1);
                    p.CurveTo(x1, y1, x2, y2, ex, ey);
                    (lastX, lastY, curve) = (x2, y2, 1);
                    break;
                }
                case Command.Curve:
                {
                    var (qx, qy) = Control(0);
                    Quadratic(p, cx, cy, qx, qy, ex, ey);
                    (lastX, lastY, curve) = (qx, qy, 2);
                    break;
                }
                case Command.Smooth:
                {
                    // The first control point mirrors the last curve's second one about the current point.
                    int kind = c.Anchors.Length == 1 ? 1 : 2;
                    float rx = last == kind ? 2 * cx - lastX : cx, ry = last == kind ? 2 * cy - lastY : cy;
                    curve = kind;
                    if (kind == 1)
                    {
                        var (x2, y2) = Control(0);
                        p.CurveTo(rx, ry, x2, y2, ex, ey);
                        (lastX, lastY) = (x2, y2);
                    }
                    else
                    {
                        Quadratic(p, cx, cy, rx, ry, ex, ey);
                        (lastX, lastY) = (rx, ry);
                    }
                    break;
                }
                case Command.Arc:
                {
                    float arx, ary;
                    if (c.OneRadius) arx = ary = c.Rx.Resolve(diagonal) * Px;
                    else (arx, ary) = (c.Rx.Resolve(w) * Px, c.Ry.Resolve(h) * Px);
                    ArcConverter.Append(p, cx, cy, arx, ary, c.Rotate, c.Large, c.Clockwise, ex, ey);
                    break;
                }
                case Command.Close:
                    p.Close();
                    (ex, ey) = (sx, sy);
                    break;
            }
            (cx, cy, last) = (ex, ey, curve);
        }
    }

    private static void Quadratic(PathBuilder p, float x0, float y0, float qx, float qy, float x, float y)
    {
        const float TwoThirds = 2f / 3;
        p.CurveTo(x0 + TwoThirds * (qx - x0), y0 + TwoThirds * (qy - y0), x + TwoThirds * (qx - x), y + TwoThirds * (qy - y), x, y);
    }

    private static void Ellipse(PathBuilder p, float cx, float cy, float rx, float ry)
    {
        float kx = rx * Kappa, ky = ry * Kappa;
        p.MoveTo(cx + rx, cy);
        p.CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        p.CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        p.CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        p.CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        p.Close();
    }

    /// <summary>A rectangle with rounded corners, clockwise from the top-left corner.</summary>
    internal static void RoundedRect(PathBuilder p, float x, float y, float w, float h, Layout.CornerRadii? radii)
    {
        if (radii is not { } r || r.IsZero)
        {
            p.MoveTo(x, y);
            p.LineTo(x + w, y);
            p.LineTo(x + w, y + h);
            p.LineTo(x, y + h);
            p.Close();
            return;
        }
        if (r.Shape is { } shape)
        {
            var points = shape.Polygon(x, y, w, h);
            p.MoveTo(points[0].X, points[0].Y);
            for (int i = 1; i < points.Count; i++) p.LineTo(points[i].X, points[i].Y);
            p.Close();
            return;
        }
        float x1 = x + w, y1 = y + h;
        p.MoveTo(x + r.TLx, y);
        p.LineTo(x1 - r.TRx, y);
        if (r.TRx > 0) p.CurveTo(x1 - r.TRx + r.TRx * Kappa, y, x1, y + r.TRy - r.TRy * Kappa, x1, y + r.TRy);
        p.LineTo(x1, y1 - r.BRy);
        if (r.BRx > 0) p.CurveTo(x1, y1 - r.BRy + r.BRy * Kappa, x1 - r.BRx + r.BRx * Kappa, y1, x1 - r.BRx, y1);
        p.LineTo(x + r.BLx, y1);
        if (r.BLx > 0) p.CurveTo(x + r.BLx - r.BLx * Kappa, y1, x, y1 - r.BLy + r.BLy * Kappa, x, y1 - r.BLy);
        p.LineTo(x, y + r.TLy);
        if (r.TLx > 0) p.CurveTo(x, y + r.TLy - r.TLy * Kappa, x + r.TLx - r.TLx * Kappa, y, x + r.TLx, y);
        p.Close();
    }
}

/// <summary>One function of a filter property: a colour function with its amount, blur() or drop-shadow().</summary>
internal readonly struct FilterFunction
{
    public enum FilterKind : byte { Grayscale, Sepia, Saturate, HueRotate, Invert, Brightness, Contrast, Opacity, Blur, DropShadow }

    public readonly FilterKind Kind;
    public readonly float Amount;            // the colour functions' amount (hue-rotate: degrees); blur(): the deviation in points
    public readonly float X, Y, Blur;        // drop-shadow(): offsets and blur radius in points
    public readonly uint? Color;             // drop-shadow(): its colour, null for currentColor

    public FilterFunction(FilterKind kind, float amount, float x = 0, float y = 0, float blur = 0, uint? color = null)
    {
        Kind = kind; Amount = amount; X = x; Y = y; Blur = blur; Color = color;
    }

    public bool IsColor => Kind is not (FilterKind.Blur or FilterKind.DropShadow or FilterKind.Opacity);

    /// <summary>Parses a filter value: a list of filter functions. Returns an empty array for none and null when invalid.</summary>
    public static FilterFunction[]? Parse(string value, float em, float rem, float vw, float vh)
    {
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return Array.Empty<FilterFunction>();
        var list = new List<FilterFunction>();
        foreach (var token in CssParser.Tokens(value))
        {
            int open = token.IndexOf('(');
            if (open <= 0 || token[^1] != ')') return null;
            var name = token.Substring(0, open).ToLowerInvariant();
            var arg = token.Substring(open + 1, token.Length - open - 2).Trim();
            var lower = arg.ToLowerInvariant();
            FilterKind kind;
            switch (name)
            {
                case "grayscale": kind = FilterKind.Grayscale; break;
                case "sepia": kind = FilterKind.Sepia; break;
                case "saturate": kind = FilterKind.Saturate; break;
                case "invert": kind = FilterKind.Invert; break;
                case "brightness": kind = FilterKind.Brightness; break;
                case "contrast": kind = FilterKind.Contrast; break;
                case "opacity": kind = FilterKind.Opacity; break;
                case "hue-rotate":
                {
                    float deg = 0;
                    if (lower.Length > 0 && lower != "0" && !CssMath.TryParseAngle(lower, out deg)) return null;
                    list.Add(new FilterFunction(FilterKind.HueRotate, deg));
                    continue;
                }
                case "blur":
                {
                    var len = Length.Zero;
                    if (lower.Length > 0 && (!StyleResolver.TryParseLength(lower, em, rem, out len, vw, vh) || len.IsAuto || len.IsPercent || len.Value < 0))
                        return null;
                    list.Add(new FilterFunction(FilterKind.Blur, len.Value));
                    continue;
                }
                case "drop-shadow":
                {
                    // Parsed like one text shadow: offsets, an optional blur radius and an optional colour.
                    if (BoxShadow.Parse(arg, em, rem, vw, vh) is not { Length: 1 } sh || sh[0].Inset || sh[0].Spread != 0) return null;
                    list.Add(new FilterFunction(FilterKind.DropShadow, 0, sh[0].X, sh[0].Y, sh[0].Blur, sh[0].Color));
                    continue;
                }
                default: return null;
            }
            float amount = 1;
            if (lower.Length > 0)
            {
                bool percent = lower.EndsWith('%');
                if (!float.TryParse(percent ? lower.AsSpan(0, lower.Length - 1) : lower.AsSpan(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount)
                    || !float.IsFinite(amount) || amount < 0)
                    return null;
                if (percent) amount /= 100;
            }
            // These cannot go past their full effect.
            if (kind is FilterKind.Grayscale or FilterKind.Sepia or FilterKind.Invert or FilterKind.Opacity) amount = Math.Min(amount, 1);
            list.Add(new FilterFunction(kind, amount));
        }
        return list.ToArray();
    }

    /// <summary>
    /// The 4 × 5 colour matrix of a colour function or opacity() (rows R, G, B, A; columns R, G, B, A, offset), as the
    /// Filter Effects specification defines them.
    /// </summary>
    public float[] Matrix()
    {
        float a = Amount, k = 1 - Amount;
        switch (Kind)
        {
            case FilterKind.Grayscale:
                return new[]
                {
                    0.2126f + 0.7874f * k, 0.7152f - 0.7152f * k, 0.0722f - 0.0722f * k, 0, 0,
                    0.2126f - 0.2126f * k, 0.7152f + 0.2848f * k, 0.0722f - 0.0722f * k, 0, 0,
                    0.2126f - 0.2126f * k, 0.7152f - 0.7152f * k, 0.0722f + 0.9278f * k, 0, 0,
                    0, 0, 0, 1, 0,
                };
            case FilterKind.Sepia:
                return new[]
                {
                    0.393f + 0.607f * k, 0.769f - 0.769f * k, 0.189f - 0.189f * k, 0, 0,
                    0.349f - 0.349f * k, 0.686f + 0.314f * k, 0.168f - 0.168f * k, 0, 0,
                    0.272f - 0.272f * k, 0.534f - 0.534f * k, 0.131f + 0.869f * k, 0, 0,
                    0, 0, 0, 1, 0,
                };
            case FilterKind.Saturate:
                return new[]
                {
                    0.213f + 0.787f * a, 0.715f - 0.715f * a, 0.072f - 0.072f * a, 0, 0,
                    0.213f - 0.213f * a, 0.715f + 0.285f * a, 0.072f - 0.072f * a, 0, 0,
                    0.213f - 0.213f * a, 0.715f - 0.715f * a, 0.072f + 0.928f * a, 0, 0,
                    0, 0, 0, 1, 0,
                };
            case FilterKind.HueRotate:
            {
                float r = a * MathF.PI / 180, c = MathF.Cos(r), s = MathF.Sin(r);
                return new[]
                {
                    0.213f + c * 0.787f - s * 0.213f, 0.715f - c * 0.715f - s * 0.715f, 0.072f - c * 0.072f + s * 0.928f, 0, 0,
                    0.213f - c * 0.213f + s * 0.143f, 0.715f + c * 0.285f + s * 0.140f, 0.072f - c * 0.072f - s * 0.283f, 0, 0,
                    0.213f - c * 0.213f - s * 0.787f, 0.715f - c * 0.715f + s * 0.715f, 0.072f + c * 0.928f + s * 0.072f, 0, 0,
                    0, 0, 0, 1, 0,
                };
            }
            case FilterKind.Invert:
                return new[] { 1 - 2 * a, 0, 0, 0, a, 0, 1 - 2 * a, 0, 0, a, 0, 0, 1 - 2 * a, 0, a, 0, 0, 0, 1, 0 };
            case FilterKind.Brightness:
                return new[] { a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, a, 0, 0, 0, 0, 0, 1, 0 };
            case FilterKind.Contrast:
            {
                float o = 0.5f - 0.5f * a;
                return new[] { a, 0, 0, 0, o, 0, a, 0, 0, o, 0, 0, a, 0, o, 0, 0, 0, 1, 0 };
            }
            case FilterKind.Opacity:
                return new[] { 1f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, a, 0 };
            default:
                return new[] { 1f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0 };
        }
    }
}

/// <summary>The colour functions of a filter, applied in order to straight sRGB colours (each step clamped, as on pixels).</summary>
internal sealed class ColorFilter
{
    private readonly float[][] _matrices;
    private readonly Dictionary<uint, uint> _cache = new();

    public ColorFilter(IEnumerable<FilterFunction> functions) => _matrices = functions.Where(f => f.IsColor).Select(f => f.Matrix()).ToArray();

    public bool IsIdentity => _matrices.Length == 0;

    /// <summary>A text key that identifies the filter, for caching filtered images.</summary>
    public string Key => string.Join(";", _matrices.Select(m => string.Join(",", m.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

    /// <summary>Filters a 0xAARRGGBB colour; the alpha is kept.</summary>
    public uint Apply(uint argb)
    {
        lock (_cache)
            if (_cache.TryGetValue(argb, out uint cached)) return cached;
        float r = ((argb >> 16) & 255) / 255f, g = ((argb >> 8) & 255) / 255f, b = (argb & 255) / 255f;
        Apply(ref r, ref g, ref b);
        uint result = argb & 0xFF000000 | (uint)MathF.Round(r * 255) << 16 | (uint)MathF.Round(g * 255) << 8 | (uint)MathF.Round(b * 255);
        lock (_cache)
            if (_cache.Count < 4096) _cache[argb] = result;
        return result;
    }

    /// <summary>Filters one straight colour with channels in [0, 1].</summary>
    public void Apply(ref float r, ref float g, ref float b)
    {
        foreach (var m in _matrices)
        {
            float nr = m[0] * r + m[1] * g + m[2] * b + m[3] + m[4];
            float ng = m[5] * r + m[6] * g + m[7] * b + m[8] + m[9];
            float nb = m[10] * r + m[11] * g + m[12] * b + m[13] + m[14];
            r = Math.Clamp(nr, 0, 1);
            g = Math.Clamp(ng, 0, 1);
            b = Math.Clamp(nb, 0, 1);
        }
    }
}

/// <summary>The blend modes of mix-blend-mode and background-blend-mode, as PDF blend mode names.</summary>
internal static class BlendModes
{
    /// <summary>The PDF name of a CSS blend mode; null for normal, and for a value that is not a blend mode.</summary>
    public static string? PdfName(string lower, out bool valid)
    {
        valid = true;
        switch (lower)
        {
            case "normal": return null;
            case "multiply": return "Multiply";
            case "screen": return "Screen";
            case "overlay": return "Overlay";
            case "darken": return "Darken";
            case "lighten": return "Lighten";
            case "color-dodge": return "ColorDodge";
            case "color-burn": return "ColorBurn";
            case "hard-light": return "HardLight";
            case "soft-light": return "SoftLight";
            case "difference": return "Difference";
            case "exclusion": return "Exclusion";
            case "hue": return "Hue";
            case "saturation": return "Saturation";
            case "color": return "Color";
            case "luminosity": return "Luminosity";
            // plus-lighter has no PDF blend mode: like an unknown value, it is ignored.
            default: valid = false; return null;
        }
    }
}

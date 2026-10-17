using System.Runtime.CompilerServices;

namespace PdfPecker.Svg;

/// <summary>Parses SVG path data into absolute move/line/cubic commands. Quadratic curves and arcs become cubics.</summary>
/// <remarks>
/// As SVG asks, an error in the data ends the path where it occurs and keeps the commands before it. A command whose
/// numbers run out or do not parse adds nothing, so the path ends after the last complete command.
/// </remarks>
internal static class PathParser
{
    /// <summary>The path that <paramref name="d"/> draws, or null when it draws nothing.</summary>
    public static PathBuilder? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return null;
        var path = new PathBuilder();
        new PathDataParser(d, path).ParseCommands();
        return path.Verbs.Count == 0 ? null : path;
    }

    /// <summary>Reads the commands one after another, keeping the current point and the last control point.</summary>
    private ref struct PathDataParser
    {
        // The command before the first command letter, which numbers cannot repeat.
        private const char NoCommand = ' ';

        // A quadratic Bézier is the cubic whose control points lie two thirds of the way from each end to its one.
        private const float TwoThirds = 2f / 3;

        private readonly PathBuilder _path;
        private NumberLexer _lexer;
        private float _x, _y;               // the current point
        private float _startX, _startY;     // the start of the subpath, where a closepath returns to
        private float _controlX, _controlY; // the last control point, which S and T mirror
        private char _previous;             // the last command run, as written
        private bool _hasCurrentPoint;

        public PathDataParser(string data, PathBuilder path)
        {
            _path = path;
            _lexer = new NumberLexer(data, 0);
            _previous = NoCommand;
        }

        public void ParseCommands()
        {
            char command = NoCommand;
            while (TryNextCommand(ref command) && Execute(command))
            {
                // S and T look at the command as written, so a moveto's extra pairs count as the lineto they are.
                _previous = command;
                _path.EndCommand();
            }
        }

        // Moves to the next command letter, or, at numbers, repeats the command before (a moveto as a lineto). False at
        // the end, and at anything else, which is an error: other characters, numbers before the first command, and
        // numbers after a closepath, which takes none.
        private bool TryNextCommand(ref char command)
        {
            if (!_lexer.TryPeek(out char next)) return false;
            if (char.IsLetter(next))
            {
                _lexer.Skip();
                command = next;
                return true;
            }
            if (command is NoCommand or 'Z' or 'z' || !NumberLexer.IsNumberStart(next)) return false;
            if (command == 'M') command = 'L';
            else if (command == 'm') command = 'l';
            return true;
        }

        // Runs one command; false when its numbers are missing or bad, or the letter is not a command.
        private bool Execute(char command)
        {
            // Relative coordinates are offsets from the current point and absolute ones offsets from zero. Adding the
            // zero is not a no-op: it turns a -0 into 0, which the output has always had.
            bool relative = char.IsLower(command);
            float originX = relative ? _x : 0, originY = relative ? _y : 0;
            return char.ToUpperInvariant(command) switch
            {
                'M' => MoveTo(originX, originY),
                'Z' => ClosePath(),
                'L' => LineTo(originX, originY),
                'H' => HorizontalLineTo(originX),
                'V' => VerticalLineTo(originY),
                'C' => CurveTo(originX, originY),
                'S' => SmoothCurveTo(originX, originY),
                'Q' => QuadraticCurveTo(originX, originY),
                'T' => SmoothQuadraticCurveTo(originX, originY),
                'A' => ArcTo(originX, originY),
                _ => false,
            };
        }

        private bool MoveTo(float originX, float originY)
        {
            if (!TryPoint(out float x, out float y)) return false;
            _x = _startX = originX + x;
            _y = _startY = originY + y;
            _path.MoveTo(_x, _y);
            _hasCurrentPoint = true;
            ControlAtCurrentPoint();
            return true;
        }

        private bool ClosePath()
        {
            if (_hasCurrentPoint) _path.Close();
            _x = _startX;
            _y = _startY;
            ControlAtCurrentPoint();
            return true;
        }

        private bool LineTo(float originX, float originY)
        {
            if (!TryPoint(out float x, out float y)) return false;
            LineToPoint(originX + x, originY + y);
            return true;
        }

        private bool HorizontalLineTo(float originX)
        {
            if (!_lexer.TryNumber(out float x)) return false;
            LineToPoint(originX + x, _y);
            return true;
        }

        private bool VerticalLineTo(float originY)
        {
            if (!_lexer.TryNumber(out float y)) return false;
            LineToPoint(_x, originY + y);
            return true;
        }

        private bool CurveTo(float originX, float originY)
        {
            if (!TryPoint(out float x1, out float y1) || !TryPoint(out float x2, out float y2) || !TryPoint(out float x, out float y)) return false;
            CurveToPoint(originX + x1, originY + y1, originX + x2, originY + y2, originX + x, originY + y);
            return true;
        }

        private bool SmoothCurveTo(float originX, float originY)
        {
            if (!TryPoint(out float x2, out float y2) || !TryPoint(out float x, out float y)) return false;
            // The first control point mirrors the second one of a cubic just before; otherwise it is the current point.
            bool reflect = _previous is 'C' or 'c' or 'S' or 's';
            float x1 = reflect ? 2 * _x - _controlX : _x, y1 = reflect ? 2 * _y - _controlY : _y;
            CurveToPoint(x1, y1, originX + x2, originY + y2, originX + x, originY + y);
            return true;
        }

        private bool QuadraticCurveTo(float originX, float originY)
        {
            if (!TryPoint(out float qx, out float qy) || !TryPoint(out float x, out float y)) return false;
            QuadraticToPoint(originX + qx, originY + qy, originX + x, originY + y);
            return true;
        }

        private bool SmoothQuadraticCurveTo(float originX, float originY)
        {
            if (!TryPoint(out float x, out float y)) return false;
            // The control point mirrors that of a quadratic just before; otherwise it is the current point.
            bool reflect = _previous is 'Q' or 'q' or 'T' or 't';
            float qx = reflect ? 2 * _x - _controlX : _x, qy = reflect ? 2 * _y - _controlY : _y;
            QuadraticToPoint(qx, qy, originX + x, originY + y);
            return true;
        }

        private bool ArcTo(float originX, float originY)
        {
            if (!_lexer.TryNumber(out float rx) || !_lexer.TryNumber(out float ry) || !_lexer.TryNumber(out float rotation)
                || !_lexer.TryFlag(out bool largeArc) || !_lexer.TryFlag(out bool sweep) || !TryPoint(out float x, out float y))
                return false;
            StartSubpathIfNone();
            float endX = originX + x, endY = originY + y;
            ArcConverter.Append(_path, _x, _y, rx, ry, rotation, largeArc, sweep, endX, endY);
            _x = endX;
            _y = endY;
            ControlAtCurrentPoint();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryPoint(out float x, out float y)
        {
            y = 0;
            return _lexer.TryNumber(out x) && _lexer.TryNumber(out y);
        }

        private void LineToPoint(float x, float y)
        {
            StartSubpathIfNone();
            _x = x;
            _y = y;
            _path.LineTo(x, y);
            ControlAtCurrentPoint();
        }

        private void CurveToPoint(float x1, float y1, float x2, float y2, float x, float y)
        {
            StartSubpathIfNone();
            _path.CurveTo(x1, y1, x2, y2, x, y);
            _controlX = x2;
            _controlY = y2;
            _x = x;
            _y = y;
        }

        private void QuadraticToPoint(float qx, float qy, float x, float y)
        {
            StartSubpathIfNone();
            _path.CurveTo(_x + TwoThirds * (qx - _x), _y + TwoThirds * (qy - _y), x + TwoThirds * (qx - x), y + TwoThirds * (qy - y), x, y);
            _controlX = qx;
            _controlY = qy;
            _x = x;
            _y = y;
        }

        // Path data should start with a moveto; a drawing command before one starts its subpath at the current point.
        private void StartSubpathIfNone()
        {
            if (_hasCurrentPoint) return;
            _path.MoveTo(_x, _y);
            _hasCurrentPoint = true;
        }

        // After anything but a cubic or a quadratic, a smooth curve has no control point to mirror but the current point.
        private void ControlAtCurrentPoint()
        {
            _controlX = _x;
            _controlY = _y;
        }
    }
}

/// <summary>Converts an endpoint-parameterised elliptical arc (SVG 1.1 appendix F.6) into cubic Béziers.</summary>
internal static class ArcConverter
{
    // Each cubic covers at most a quarter turn, which keeps it within a fraction of a percent of the ellipse; the
    // tolerance keeps a sweep of exactly a quarter turn from rounding up to two.
    private const double MaxSegmentSweep = Math.PI / 2;
    private const double SegmentTolerance = 1e-6;

    /// <summary>Appends the arc from (x1, y1) to (x2, y2), whose current point the path already has.</summary>
    public static void Append(PathBuilder path, float x1, float y1, float rx, float ry, float angle, bool largeArc, bool sweep, float x2, float y2)
    {
        // An arc that ends where it starts is left out, and one with a zero radius is a straight line (F.6.2).
        if (x1 == x2 && y1 == y2) return;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0 || ry == 0)
        {
            path.LineTo(x2, y2);
            return;
        }

        // F.6.5 step 1: the start point in the ellipse's axes, relative to the middle of the chord.
        double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;

        // F.6.6: radii too small to span the chord grow in proportion until they just do.
        double lambda = x1p * x1p / (rx * (double)rx) + y1p * y1p / (ry * (double)ry);
        double radiusX = rx, radiusY = ry;
        if (lambda > 1)
        {
            double scale = Math.Sqrt(lambda);
            radiusX *= scale;
            radiusY *= scale;
        }

        // Steps 2 and 3: the centre, on the side the two flags choose, first in the ellipse's axes and then in user space.
        double numerator = radiusX * radiusX * radiusY * radiusY - radiusX * radiusX * y1p * y1p - radiusY * radiusY * x1p * x1p;
        double denominator = radiusX * radiusX * y1p * y1p + radiusY * radiusY * x1p * x1p;
        double coefficient = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
        if (largeArc == sweep) coefficient = -coefficient;
        double cxp = coefficient * radiusX * y1p / radiusY, cyp = -coefficient * radiusY * x1p / radiusX;
        var ellipse = new Ellipse(
            cos * cxp - sin * cyp + (x1 + x2) / 2.0, sin * cxp + cos * cyp + (y1 + y2) / 2.0, radiusX, radiusY, cos, sin);

        // Step 4: the start angle and the signed sweep, which the sweep flag sets turning the right way.
        double startX = (x1p - cxp) / radiusX, startY = (y1p - cyp) / radiusY;
        double theta = AngleBetween(1, 0, startX, startY);
        double delta = AngleBetween(startX, startY, (-x1p - cxp) / radiusX, (-y1p - cyp) / radiusY);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / MaxSegmentSweep - SegmentTolerance));
        double step = delta / segments;
        // The distance of the control points along the tangent, for a unit circle and a segment of this sweep.
        double handle = 4.0 / 3.0 * Math.Tan(step / 4);
        double from = theta;
        for (int i = 0; i < segments; i++)
        {
            double to = from + step;
            double cosFrom = Math.Cos(from), sinFrom = Math.Sin(from), cosTo = Math.Cos(to), sinTo = Math.Sin(to);
            var (ax1, ay1) = ellipse.PointAt(cosFrom - handle * sinFrom, sinFrom + handle * cosFrom);
            var (ax2, ay2) = ellipse.PointAt(cosTo + handle * sinTo, sinTo - handle * cosTo);
            // The last segment ends exactly on the end point rather than where the rounded angles put it.
            var (ax, ay) = i == segments - 1 ? (x2, y2) : ellipse.PointAt(cosTo, sinTo);
            path.CurveTo((float)ax1, (float)ay1, (float)ax2, (float)ay2, (float)ax, (float)ay);
            from = to;
        }
    }

    // The signed angle from vector u to vector v.
    private static double AngleBetween(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);

    /// <summary>The arc's ellipse: a unit circle scaled by the radii, rotated and moved to the centre.</summary>
    private readonly struct Ellipse(double centerX, double centerY, double radiusX, double radiusY, double cos, double sin)
    {
        public (double X, double Y) PointAt(double unitX, double unitY)
        {
            double ex = unitX * radiusX, ey = unitY * radiusY;
            return (cos * ex - sin * ey + centerX, sin * ex + cos * ey + centerY);
        }
    }
}

using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>The Type 1 charstring interpreter, including flex and the other OtherSubrs through callothersubr.</summary>
internal sealed partial class Type1Font
{
    // The one-byte operators.
    private static class Op
    {
        public const int HStem = 1, VStem = 3, VMoveTo = 4, RLineTo = 5, HLineTo = 6, VLineTo = 7, RRCurveTo = 8, ClosePath = 9;
        public const int CallSubr = 10, Return = 11, Escape = 12, Hsbw = 13, EndChar = 14, RMoveTo = 21, HMoveTo = 22, VHCurveTo = 30, HVCurveTo = 31;
    }

    // The operators after an escape byte.
    private static class EscOp
    {
        public const int Seac = 6, Sbw = 7, Div = 12, CallOtherSubr = 16, Pop = 17, SetCurrentPoint = 33;
    }

    // The OtherSubrs with a meaning of their own: flex and hint replacement; others just hand their arguments back.
    private const int OtherSubrFlexEnd = 0, OtherSubrFlexStart = 1, OtherSubrFlexPoint = 2, OtherSubrHintReplacement = 3;
    private const int FlexPointCount = 7; // the reference point and the two curves' six points

    // Number encoding: bytes from 32 up are numbers, 255 starts a 32-bit integer.
    private const int FirstNumberByte = 32, LastSmallIntByte = 246, SmallIntBias = 139;
    private const int FirstPositiveByte = 247, LastPositiveByte = 250, FirstNegativeByte = 251, LastNegativeByte = 254;
    private const int TwoByteBias = 108;

    private const int StackCapacity = 64;
    private const int MaxSubrDepth = 10;

    /// <summary>Charstring tokens per glyph, which stops subroutines that call each other without end.</summary>
    private const int TokenBudgetPerGlyph = 200_000;

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _cache.Length) return null;
        var cached = Volatile.Read(ref _cache[gid]);
        if (cached != null) return cached;
        var glyph = new Type1Glyph(this);
        try
        {
            glyph.Run(_charStrings[gid], 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            // A damaged charstring draws what it managed to before the damage.
        }
        var path = glyph.Finish();
        _widths[gid] = glyph.Width;
        Volatile.Write(ref _cache[gid], path);
        return path;
    }

    /// <summary>Interprets one glyph's charstring: the pen, the operand stack, the PostScript stack and flex state.</summary>
    private sealed class Type1Glyph
    {
        private readonly Type1Font _font;
        private readonly PathData _path = new();
        private readonly double[] _stack = new double[StackCapacity];
        private int _depth;
        private readonly Stack<double> _postScript = new();
        private readonly List<(double X, double Y)> _flexPoints = new();
        private int _budget = TokenBudgetPerGlyph;
        private double _x, _y, _sidebearingX;
        private bool _open, _ended, _flex;

        public Type1Glyph(Type1Font font) => _font = font;

        public double Width { get; private set; }

        public PathData Finish()
        {
            if (_open) _path.ClosePath();
            return _path;
        }

        public void Run(byte[] charstring, int depth)
        {
            if (depth > MaxSubrDepth) return;
            var st = _stack;
            int p = 0;
            while (p < charstring.Length && !_ended && --_budget > 0)
            {
                int b = charstring[p++];
                if (b >= FirstNumberByte)
                {
                    double value = ReadNumber(charstring, b, ref p);
                    if (_depth < st.Length) st[_depth++] = value; // a full stack drops the number
                    continue;
                }
                switch (b)
                {
                    case Op.Hsbw:
                        if (_depth >= 2)
                        {
                            _sidebearingX = st[0];
                            Width = st[1];
                            _x = st[0];
                            _y = 0;
                        }
                        _depth = 0;
                        break;
                    case Op.ClosePath:
                        if (_open)
                        {
                            _path.ClosePath();
                            _open = false;
                        }
                        _depth = 0;
                        break;
                    case Op.RMoveTo:
                        if (_depth >= 2) MoveTo(_x + st[_depth - 2], _y + st[_depth - 1]);
                        _depth = 0;
                        break;
                    case Op.HMoveTo:
                        if (_depth >= 1) MoveTo(_x + st[_depth - 1], _y);
                        _depth = 0;
                        break;
                    case Op.VMoveTo:
                        if (_depth >= 1) MoveTo(_x, _y + st[_depth - 1]);
                        _depth = 0;
                        break;
                    case Op.RLineTo:
                        if (_depth >= 2) LineTo(_x + st[0], _y + st[1]);
                        _depth = 0;
                        break;
                    case Op.HLineTo:
                        if (_depth >= 1) LineTo(_x + st[0], _y);
                        _depth = 0;
                        break;
                    case Op.VLineTo:
                        if (_depth >= 1) LineTo(_x, _y + st[0]);
                        _depth = 0;
                        break;
                    case Op.RRCurveTo:
                        if (_depth >= 6) Curve(st[0], st[1], st[2], st[3], st[4], st[5]);
                        _depth = 0;
                        break;
                    case Op.VHCurveTo:
                        if (_depth >= 4) Curve(0, st[0], st[1], st[2], st[3], 0);
                        _depth = 0;
                        break;
                    case Op.HVCurveTo:
                        if (_depth >= 4) Curve(st[0], 0, st[1], st[2], 0, st[3]);
                        _depth = 0;
                        break;
                    case Op.CallSubr:
                    {
                        if (_depth < 1) break;
                        int index = (int)st[--_depth];
                        if (index >= 0 && index < _font._subrs.Count) Run(_font._subrs[index], depth + 1);
                        break;
                    }
                    case Op.Return:
                        return;
                    case Op.EndChar:
                        if (_open)
                        {
                            _path.ClosePath();
                            _open = false;
                        }
                        _ended = true;
                        return;
                    case Op.HStem: case Op.VStem:
                        _depth = 0;
                        break;
                    case Op.Escape:
                        if (Escape(charstring[p++])) return;
                        break;
                    default:
                        _depth = 0;
                        break;
                }
            }
        }

        private static double ReadNumber(byte[] charstring, int b, ref int p)
        {
            if (b <= LastSmallIntByte) return b - SmallIntBias;
            if (b <= LastPositiveByte) return (b - FirstPositiveByte) * 256 + charstring[p++] + TwoByteBias;
            if (b <= LastNegativeByte) return -(b - FirstNegativeByte) * 256 - charstring[p++] - TwoByteBias;
            int value = charstring[p] << 24 | charstring[p + 1] << 16 | charstring[p + 2] << 8 | charstring[p + 3];
            p += 4;
            return value;
        }

        // The escaped operators; true when the charstring ends here (seac).
        private bool Escape(int op)
        {
            var st = _stack;
            switch (op)
            {
                case EscOp.Sbw:
                    if (_depth >= 4)
                    {
                        _sidebearingX = st[0];
                        _x = st[0];
                        _y = st[1];
                        Width = st[2];
                    }
                    _depth = 0;
                    return false;
                case EscOp.Seac:
                    if (_depth >= 5) Seac(st[0], st[1], st[2], (int)st[3], (int)st[4]);
                    _ended = true;
                    return true;
                case EscOp.Div:
                    if (_depth >= 2)
                    {
                        st[_depth - 2] = st[_depth - 1] == 0 ? 0 : st[_depth - 2] / st[_depth - 1];
                        _depth--;
                    }
                    return false;
                case EscOp.CallOtherSubr:
                    CallOtherSubr();
                    return false;
                case EscOp.Pop:
                    if (_depth < st.Length) st[_depth++] = _postScript.Count > 0 ? _postScript.Pop() : 0;
                    return false;
                case EscOp.SetCurrentPoint:
                    if (_depth >= 2)
                    {
                        _x = st[0];
                        _y = st[1];
                    }
                    _depth = 0;
                    return false;
                default: // dotsection, vstem3, hstem3
                    _depth = 0;
                    return false;
            }
        }

        // "arg1 ... argn n othersubr# callothersubr": the arguments are taken off the stack and, for the OtherSubrs
        // the interpreter does not know, pushed to the PostScript stack for the pops that follow.
        private void CallOtherSubr()
        {
            var st = _stack;
            if (_depth < 2)
            {
                _depth = 0;
                return;
            }
            int otherSubr = (int)st[--_depth];
            int count = Math.Clamp((int)st[--_depth], 0, _depth);
            var args = new double[count];
            for (int i = 0; i < count; i++) args[i] = st[_depth - count + i];
            _depth -= count;
            switch (otherSubr)
            {
                case OtherSubrFlexStart:
                    _flex = true;
                    _flexPoints.Clear();
                    break;
                case OtherSubrFlexPoint:
                    break;
                case OtherSubrFlexEnd:
                    // The rmovetos since the start collected the reference point and six control points.
                    _flex = false;
                    if (_flexPoints.Count >= FlexPointCount)
                    {
                        var f = _flexPoints;
                        CurveTo(f[1], f[2], f[3]);
                        CurveTo(f[4], f[5], f[6]);
                    }
                    // The end point comes back through two pops, for setcurrentpoint.
                    _postScript.Push(_y);
                    _postScript.Push(_x);
                    break;
                case OtherSubrHintReplacement:
                    _postScript.Push(OtherSubrHintReplacement);
                    break;
                default:
                    for (int i = 0; i < count; i++) _postScript.Push(args[i]);
                    break;
            }
        }

        private void Seac(double accentSidebearingX, double adx, double ady, int baseChar, int accentChar)
        {
            if (baseChar is < 0 or > 255 || accentChar is < 0 or > 255) return;
            int baseGid = Encodings.Standard[baseChar] is { } baseName ? _font.GlyphByName(baseName) : -1;
            int accentGid = Encodings.Standard[accentChar] is { } accentName ? _font.GlyphByName(accentName) : -1;
            int count = _font._charStrings.Count;
            if (baseGid >= 0 && baseGid < count && _font.GetGlyph(baseGid) is { } basePath) basePath.AppendTransformed(_path, Matrix.Identity);
            if (accentGid >= 0 && accentGid < count && _font.GetGlyph(accentGid) is { } accentPath)
                accentPath.AppendTransformed(_path, Matrix.Translation(adx - accentSidebearingX, ady));
        }

        // While flex is on, the moves collect its points instead of starting contours.
        private void MoveTo(double x, double y)
        {
            _x = x;
            _y = y;
            if (_flex)
            {
                _flexPoints.Add((x, y));
                return;
            }
            if (_open) _path.ClosePath();
            _path.Move(x, y);
            _open = true;
        }

        private void LineTo(double x, double y)
        {
            BeginContour();
            _x = x;
            _y = y;
            _path.Line(x, y);
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2, x3 = x2 + dx3, y3 = y2 + dy3;
            CurveTo((x1, y1), (x2, y2), (x3, y3));
        }

        private void CurveTo((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) end)
        {
            BeginContour();
            _path.Cubic(c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y);
            _x = end.X;
            _y = end.Y;
        }

        // A line or curve without a preceding move starts its contour at the current point.
        private void BeginContour()
        {
            if (_open) return;
            _path.Move(_x, _y);
            _open = true;
        }
    }
}

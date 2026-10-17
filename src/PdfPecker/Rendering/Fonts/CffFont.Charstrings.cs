using PdfPecker.Fonts.Cff;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>The Type 2 charstring interpreter that builds a glyph's outline.</summary>
internal sealed partial class CffFont
{
    // The one-byte operators.
    private static class Op
    {
        public const int HStem = 1, VStem = 3, VMoveTo = 4, RLineTo = 5, HLineTo = 6, VLineTo = 7, RRCurveTo = 8, CallSubr = 10, Return = 11;
        public const int Escape = 12, EndChar = 14, HStemHm = 18, HintMask = 19, CntrMask = 20, RMoveTo = 21, HMoveTo = 22, VStemHm = 23;
        public const int RCurveLine = 24, RLineCurve = 25, VVCurveTo = 26, HHCurveTo = 27, CallGSubr = 29, VHCurveTo = 30, HVCurveTo = 31;
    }

    // The operators after an escape byte.
    private static class EscOp
    {
        public const int And = 3, Or = 4, Not = 5, Abs = 9, Add = 10, Sub = 11, Div = 12, Neg = 14, Eq = 15, Drop = 18;
        public const int Put = 20, Get = 21, IfElse = 22, Random = 23, Mul = 24, Sqrt = 26, Dup = 27, Exch = 28, Index = 29, Roll = 30;
        public const int HFlex = 34, Flex = 35, HFlex1 = 36, Flex1 = 37;
    }

    private const int StackCapacity = 96;
    private const int TransientArraySize = 32;

    // The arguments the flex operators need, counted from the bottom of the stack; flex's trailing flex depth is not used.
    private const int FlexArgs = 12, HFlexArgs = 7, HFlex1Args = 9, Flex1Args = 11;

    // The accented-character form of endchar (seac) takes adx ady bchar achar.
    private const int SeacArgs = 4;

    /// <summary>
    /// Interprets one glyph's charstring. The width, the pen position, the stack and the hint count live here, as
    /// does the budget that stops a charstring whose subroutines call each other without end.
    /// </summary>
    private sealed class Type2Glyph
    {
        private readonly CffFont _font;
        private readonly PrivateValues _private;
        private readonly int _gid;
        private readonly bool _seacComponents;
        private readonly PathData _path = new();
        private readonly Type2Stack _stack = new(StackCapacity);
        private readonly double[] _transient = new double[TransientArraySize];
        private CharstringBudget _budget = new(int.MaxValue); // the per-glyph limit alone bounds a glyph
        private double _x, _y;
        private double _width = double.NaN;
        private int _stems;
        private bool _haveWidth, _open, _ended;

        public Type2Glyph(CffFont font, PrivateValues values, int gid, bool seacComponents)
        {
            _font = font;
            _private = values;
            _gid = gid;
            _seacComponents = seacComponents;
        }

        /// <summary>The advance the charstring declared, or the Private DICT's default.</summary>
        public double Width => double.IsNaN(_width) ? _private.DefaultWidth : _width;

        private int Depth
        {
            get => _stack.Count;
            set => _stack.Count = value;
        }

        /// <summary>Closes an open contour and returns the outline.</summary>
        public PathData Finish()
        {
            if (_open) _path.ClosePath();
            return _path;
        }

        /// <summary>Runs the charstring bytes in [start, end); subroutines call back in with the next depth.</summary>
        public void Run(int start, int end, int depth)
        {
            if (depth > Type2.MaxSubrDepth) return;
            ReadOnlySpan<byte> data = _font._data;
            var st = _stack.Items;
            int p = start;
            while (p < end && !_ended && _budget.Spend())
            {
                int b = data[p++];
                if (Type2.IsNumber(b))
                {
                    _stack.PushNumber(data, b, ref p); // a full stack drops the number
                    continue;
                }
                switch (b)
                {
                    case Op.HStem: case Op.VStem: case Op.HStemHm: case Op.VStemHm:
                        TakeWidth(Depth % 2 == 1);
                        _stems += Depth / 2;
                        Depth = 0;
                        break;
                    case Op.HintMask: case Op.CntrMask:
                        // Stem hints before the first mask may omit their vstem operator.
                        TakeWidth(Depth % 2 == 1);
                        _stems += Depth / 2;
                        Depth = 0;
                        p += Type2.MaskBytes(_stems);
                        break;
                    case Op.RMoveTo:
                        TakeWidth(Depth > 2);
                        MoveTo(_x + Arg(Depth - 2), _y + Arg(Depth - 1));
                        Depth = 0;
                        break;
                    case Op.HMoveTo:
                        TakeWidth(Depth > 1);
                        MoveTo(_x + Arg(Depth - 1), _y);
                        Depth = 0;
                        break;
                    case Op.VMoveTo:
                        TakeWidth(Depth > 1);
                        MoveTo(_x, _y + Arg(Depth - 1));
                        Depth = 0;
                        break;
                    case Op.RLineTo:
                        for (int i = 0; i + 1 < Depth; i += 2) LineTo(_x + st[i], _y + st[i + 1]);
                        Depth = 0;
                        break;
                    case Op.HLineTo: case Op.VLineTo:
                        AlternatingLines(horizontalFirst: b == Op.HLineTo);
                        Depth = 0;
                        break;
                    case Op.RRCurveTo:
                        for (int i = 0; i + 5 < Depth; i += 6) Curve(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                        Depth = 0;
                        break;
                    case Op.RCurveLine:
                    {
                        int i = 0;
                        for (; i + 5 < Depth - 2; i += 6) Curve(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                        if (i + 1 < Depth) LineTo(_x + st[i], _y + st[i + 1]);
                        Depth = 0;
                        break;
                    }
                    case Op.RLineCurve:
                    {
                        int i = 0;
                        for (; i + 1 < Depth - 6; i += 2) LineTo(_x + st[i], _y + st[i + 1]);
                        if (i + 5 < Depth) Curve(st[i], st[i + 1], st[i + 2], st[i + 3], st[i + 4], st[i + 5]);
                        Depth = 0;
                        break;
                    }
                    case Op.VVCurveTo:
                    {
                        // An odd argument count starts with a dx1 for the first curve only.
                        int i = 0;
                        double dx1 = Depth % 4 == 1 ? st[i++] : 0;
                        for (; i + 3 < Depth; i += 4)
                        {
                            Curve(dx1, st[i], st[i + 1], st[i + 2], 0, st[i + 3]);
                            dx1 = 0;
                        }
                        Depth = 0;
                        break;
                    }
                    case Op.HHCurveTo:
                    {
                        int i = 0;
                        double dy1 = Depth % 4 == 1 ? st[i++] : 0;
                        for (; i + 3 < Depth; i += 4)
                        {
                            Curve(st[i], dy1, st[i + 1], st[i + 2], st[i + 3], 0);
                            dy1 = 0;
                        }
                        Depth = 0;
                        break;
                    }
                    case Op.VHCurveTo: case Op.HVCurveTo:
                        AlternatingCurves(horizontalFirst: b == Op.HVCurveTo);
                        Depth = 0;
                        break;
                    case Op.CallSubr: case Op.CallGSubr:
                    {
                        if (Depth < 1) return;
                        var subrs = b == Op.CallSubr ? _private.Subrs : _font._globalSubrs;
                        int index = (int)_stack.Pop() + Type2.SubrBias(subrs.Count);
                        if (index < 0 || index >= subrs.Count) break;
                        var (subrStart, subrEnd) = subrs.Range(index);
                        Run(subrStart, subrEnd, depth + 1);
                        break;
                    }
                    case Op.Return:
                        return;
                    case Op.EndChar:
                        EndChar();
                        return;
                    case Op.Escape:
                        Escape(data[p++]);
                        break;
                    default:
                        Depth = 0;
                        break;
                }
            }
        }

        // hlineto and vlineto: one coordinate per line, alternating between horizontal and vertical.
        private void AlternatingLines(bool horizontalFirst)
        {
            var st = _stack.Items;
            bool horizontal = horizontalFirst;
            for (int i = 0; i < Depth; i++, horizontal = !horizontal)
            {
                if (horizontal) LineTo(_x + st[i], _y);
                else LineTo(_x, _y + st[i]);
            }
        }

        // hvcurveto and vhcurveto: four arguments per curve, alternating the start tangent; a fifth argument on the
        // last curve gives the other coordinate of its end point.
        private void AlternatingCurves(bool horizontalFirst)
        {
            var st = _stack.Items;
            bool horizontal = horizontalFirst;
            int i = 0;
            while (i + 3 < Depth)
            {
                bool last = Depth - i == 5;
                if (horizontal) Curve(st[i], 0, st[i + 1], st[i + 2], last ? st[i + 4] : 0, st[i + 3]);
                else Curve(0, st[i], st[i + 1], st[i + 2], st[i + 3], last ? st[i + 4] : 0);
                i += last ? 5 : 4;
                horizontal = !horizontal;
            }
        }

        private void EndChar()
        {
            var st = _stack.Items;
            TakeWidth(Depth == 1 || Depth == 1 + SeacArgs);
            if (Depth >= SeacArgs)
            {
                // Accented character (seac): base and accent from the standard encoding.
                double ady = st[Depth - 3], adx = st[Depth - 4];
                int baseChar = (int)st[Depth - 2], accentChar = (int)st[Depth - 1];
                Seac(adx, ady, baseChar, accentChar);
            }
            if (_open)
            {
                _path.ClosePath();
                _open = false;
            }
            _ended = true;
            Depth = 0;
        }

        private void Seac(double adx, double ady, int baseChar, int accentChar)
        {
            // A component's own seac is not followed, and a component that is the glyph itself is left out.
            if (!_seacComponents || baseChar < 0 || baseChar > 255 || accentChar < 0 || accentChar > 255) return;
            int baseGid = Encodings.Standard[baseChar] is { } baseName ? _font.GlyphByName(baseName) : -1;
            int accentGid = Encodings.Standard[accentChar] is { } accentName ? _font.GlyphByName(accentName) : -1;
            if (baseGid > 0 && baseGid != _gid && _font.SeacComponent(baseGid) is { } basePath) basePath.AppendTransformed(_path, Matrix.Identity);
            if (accentGid > 0 && accentGid != _gid && _font.SeacComponent(accentGid) is { } accentPath)
                accentPath.AppendTransformed(_path, Matrix.Translation(adx, ady));
        }

        // The escaped operators: the flex curves clear the stack like the other path operators; the arithmetic and
        // storage operators work on the stack and leave the rest of it alone.
        private void Escape(int op)
        {
            var st = _stack.Items;
            int n = Depth;
            switch (op)
            {
                case EscOp.Flex:
                    if (n >= FlexArgs)
                    {
                        Curve(st[0], st[1], st[2], st[3], st[4], st[5]);
                        Curve(st[6], st[7], st[8], st[9], st[10], st[11]);
                    }
                    break;
                case EscOp.HFlex:
                    if (n >= HFlexArgs)
                    {
                        // The second curve returns to the starting y.
                        double y0 = _y;
                        Curve(st[0], 0, st[1], st[2], st[3], 0);
                        Curve(st[4], 0, st[5], y0 - _y, st[6], 0);
                    }
                    break;
                case EscOp.HFlex1:
                    if (n >= HFlex1Args)
                    {
                        Curve(st[0], st[1], st[2], st[3], st[4], 0);
                        Curve(st[5], 0, st[6], st[7], st[8], -(st[1] + st[3] + st[7]));
                    }
                    break;
                case EscOp.Flex1:
                    if (n >= Flex1Args)
                    {
                        // The last point's missing coordinate is the one along which the flex moves least.
                        double x0 = _x, y0 = _y;
                        double dx = st[0] + st[2] + st[4] + st[6] + st[8], dy = st[1] + st[3] + st[5] + st[7] + st[9];
                        Curve(st[0], st[1], st[2], st[3], st[4], st[5]);
                        if (Math.Abs(dx) > Math.Abs(dy)) Curve(st[6], st[7], st[8], st[9], st[10], y0 - (_y + st[7] + st[9]));
                        else Curve(st[6], st[7], st[8], st[9], x0 - (_x + st[6] + st[8]), st[10]);
                    }
                    break;
                // Arithmetic operators (rarely used).
                case EscOp.And: if (n >= 2) { st[n - 2] = st[n - 2] != 0 && st[n - 1] != 0 ? 1 : 0; Depth--; } return;
                case EscOp.Or: if (n >= 2) { st[n - 2] = st[n - 2] != 0 || st[n - 1] != 0 ? 1 : 0; Depth--; } return;
                case EscOp.Not: if (n >= 1) st[n - 1] = st[n - 1] != 0 ? 0 : 1; return;
                case EscOp.Abs: if (n >= 1) st[n - 1] = Math.Abs(st[n - 1]); return;
                case EscOp.Add: if (n >= 2) { st[n - 2] += st[n - 1]; Depth--; } return;
                case EscOp.Sub: if (n >= 2) { st[n - 2] -= st[n - 1]; Depth--; } return;
                case EscOp.Div: if (n >= 2) { st[n - 2] = st[n - 1] == 0 ? 0 : st[n - 2] / st[n - 1]; Depth--; } return;
                case EscOp.Neg: if (n >= 1) st[n - 1] = -st[n - 1]; return;
                case EscOp.Eq: if (n >= 2) { st[n - 2] = st[n - 2] == st[n - 1] ? 1 : 0; Depth--; } return;
                case EscOp.Drop: if (n >= 1) Depth--; return;
                case EscOp.Put:
                    if (n >= 2)
                    {
                        int i = (int)st[n - 1];
                        if (i >= 0 && i < _transient.Length) _transient[i] = st[n - 2];
                        Depth -= 2;
                    }
                    return;
                case EscOp.Get:
                    if (n >= 1)
                    {
                        int i = (int)st[n - 1];
                        st[n - 1] = i >= 0 && i < _transient.Length ? _transient[i] : 0;
                    }
                    return;
                case EscOp.IfElse: if (n >= 4) { st[n - 4] = st[n - 2] <= st[n - 1] ? st[n - 4] : st[n - 3]; Depth -= 3; } return;
                case EscOp.Random: if (n < st.Length) { st[n] = 0.5; Depth++; } return; // a fixed value keeps rendering repeatable
                case EscOp.Mul: if (n >= 2) { st[n - 2] *= st[n - 1]; Depth--; } return;
                case EscOp.Sqrt: if (n >= 1) st[n - 1] = Math.Sqrt(Math.Abs(st[n - 1])); return;
                case EscOp.Dup: if (n >= 1 && n < st.Length) { st[n] = st[n - 1]; Depth++; } return;
                case EscOp.Exch: if (n >= 2) (st[n - 1], st[n - 2]) = (st[n - 2], st[n - 1]); return;
                case EscOp.Index:
                    if (n >= 1)
                    {
                        int i = (int)st[n - 1];
                        st[n - 1] = i >= 0 && i < n - 1 ? st[n - 2 - i] : 0;
                    }
                    return;
                case EscOp.Roll:
                    if (n >= 2) Roll((int)st[n - 2], (int)st[n - 1]);
                    return;
            }
            Depth = 0;
        }

        // Rotates the top count elements (below the two operands) by shift places.
        private void Roll(int count, int shift)
        {
            var st = _stack.Items;
            Depth -= 2;
            if (count <= 0 || count > Depth) return;
            shift = ((shift % count) + count) % count;
            var rolled = new double[count];
            Array.Copy(st, Depth - count, rolled, 0, count);
            for (int k = 0; k < count; k++) st[Depth - count + (k + shift) % count] = rolled[k];
        }

        private double Arg(int i) => i >= 0 && i < Depth ? _stack[i] : 0;

        // The first stack-clearing operator may carry the advance width as an extra leading argument.
        private void TakeWidth(bool hasExtra)
        {
            if (_haveWidth) return;
            _haveWidth = true;
            if (hasExtra && Depth > 0)
            {
                _width = _private.NominalWidth + _stack[0];
                Array.Copy(_stack.Items, 1, _stack.Items, 0, Depth - 1);
                Depth--;
            }
            else
            {
                _width = _private.DefaultWidth;
            }
        }

        private void MoveTo(double x, double y)
        {
            if (_open) _path.ClosePath();
            _x = x;
            _y = y;
            _path.Move(x, y);
            _open = true;
        }

        private void LineTo(double x, double y)
        {
            if (!_open) MoveTo(_x, _y);
            _x = x;
            _y = y;
            _path.Line(x, y);
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            if (!_open) MoveTo(_x, _y);
            double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2, x3 = x2 + dx3, y3 = y2 + dy3;
            _path.Cubic(x1, y1, x2, y2, x3, y3);
            _x = x3;
            _y = y3;
        }
    }
}

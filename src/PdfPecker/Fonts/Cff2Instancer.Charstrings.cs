using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>Running CFF2 charstrings into outlines, and writing those as CFF charstrings.</summary>
internal static partial class Cff2Instancer
{
    // Type 2 charstring operators. CFF2 has no return or endchar, but both are harmless and some fonts have them.
    private const int Hstem = 1, Vstem = 3, Vmoveto = 4, Rlineto = 5, Hlineto = 6, Vlineto = 7, Rrcurveto = 8;
    private const int Callsubr = 10, Return = 11, EscapeOperator = 12, Endchar = 14, Vsindex = 15, Blend = 16;
    private const int Hstemhm = 18, Hintmask = 19, Cntrmask = 20, Rmoveto = 21, Hmoveto = 22, Vstemhm = 23;
    private const int Rcurveline = 24, Rlinecurve = 25, Vvcurveto = 26, Hhcurveto = 27, Callgsubr = 29, Vhcurveto = 30, Hvcurveto = 31;

    // Escaped (12 x) operators: the flex family, each with the operand count it needs.
    private const int Hflex = 34, Flex = 35, Hflex1 = 36, Flex1 = 37;
    private const int HflexOperands = 7, FlexOperands = 12, Hflex1Operands = 9, Flex1Operands = 11;

    // The CFF2 limit on the operand stack.
    private const int MaxStack = 513;

    // A font past the budget is treated as damaged and left as it is.
    private const int FontCharstringBudget = 100_000_000;

    // Type 2 integers: one byte for -107 to 107, two bytes up to ±1131, otherwise a 16-bit number after operator 28.
    private const int OneByteLimit = 107, TwoByteLimit = 1131, TwoByteBias = 108, OneByteBias = 139;
    private const int PositiveTwoByteFirst = 247, NegativeTwoByteFirst = 251, ShortIntPrefix = 28;

    // The outline's operators, as recorded and as written.
    private enum PathOp : byte { MoveTo, LineTo, CurveTo }

    /// <summary>Runs CFF2 charstrings into an outline in absolute coordinates, then writes it as a CFF charstring.</summary>
    private sealed class Charstring
    {
        private readonly byte[] _d;
        private readonly CffIndex _gsubrs;
        private readonly VariationStore? _store;
        private readonly Dictionary<int, float[]> _scalars = new();
        private readonly Type2Stack _stack = new(MaxStack);

        // The outline: an operator per segment, and its points (one for a move or a line, three for a curve) as x, y
        // pairs in absolute coordinates.
        private readonly List<PathOp> _ops = new();
        private readonly List<double> _points = new();
        private readonly List<byte> _encoded = new();

        private CffIndex _subrs;
        private float[] _blend = Array.Empty<float>();
        private double _x, _y;
        private int _stems, _depth;
        private CharstringBudget _budget = new(FontCharstringBudget);

        public Charstring(byte[] d, CffIndex gsubrs, VariationStore? store)
        {
            _d = d;
            _gsubrs = gsubrs;
            _store = store;
        }

        // The region scalars of a store subtable, which blends weigh their deltas with.
        private float[] Scalars(int vsindex)
        {
            if (_store == null) return Array.Empty<float>();
            if (!_scalars.TryGetValue(vsindex, out var s)) _scalars[vsindex] = s = _store.RegionScalars(vsindex);
            return s;
        }

        public void Run(int start, int end, CffIndex subrs, int vsindex)
        {
            _subrs = subrs;
            _blend = Scalars(vsindex);
            _stack.Clear();
            _ops.Clear();
            _points.Clear();
            _x = _y = 0;
            _stems = 0;
            _depth = 0;
            _budget.StartGlyph();
            Execute(start, end);
        }

        private void Execute(int pos, int end)
        {
            // The glyph's own charstring counts as the first level here.
            if (++_depth > Type2.MaxSubrDepth) throw new FormatException("CFF2 subroutines nest too deep.");
            ReadOnlySpan<byte> d = _d;
            while (pos < end)
            {
                if (!_budget.Spend()) throw new FormatException("CFF2 charstrings call too many subroutines.");
                int b = d[pos++];
                if (Type2.IsNumber(b))
                {
                    if (!_stack.PushNumber(d, b, ref pos)) throw new FormatException("CFF2 stack overflow.");
                    continue;
                }
                switch (b)
                {
                    case Return:
                        _depth--;
                        return;
                    case Endchar:
                        _stack.Clear();
                        pos = end;
                        break;
                    case Hintmask:
                    case Cntrmask:
                        // Arguments left on the stack are vertical stems; the mask has a bit per stem.
                        CountStems();
                        pos += Type2.MaskBytes(_stems);
                        break;
                    case Callsubr:
                    case Callgsubr:
                        CallSubroutine(b == Callsubr ? _subrs : _gsubrs);
                        break;
                    case EscapeOperator:
                        RunEscaped(d[pos++]);
                        break;
                    default:
                        RunOperator(b);
                        break;
                }
            }
            _depth--;
        }

        private void CallSubroutine(CffIndex index)
        {
            int i = (int)_stack.Pop() + Type2.SubrBias(index.Count);
            if ((uint)i >= (uint)index.Count) throw new FormatException("Bad CFF2 subroutine.");
            var (start, end) = index.Range(i);
            Execute(start, end);
        }

        private void CountStems()
        {
            _stems += _stack.Count / 2;
            _stack.Clear();
        }

        // The operators that act on the stack and clear it: hints, the path and the variation operators.
        private void RunOperator(int op)
        {
            var s = _stack;
            switch (op)
            {
                case Hstem: case Vstem: case Hstemhm: case Vstemhm:
                    CountStems();
                    return;
                case Vsindex:
                    _blend = Scalars((int)s[^1]);
                    break;
                case Blend:
                    RunBlend();
                    return; // the blended values stay on the stack
                case Rmoveto: Move(s[^2], s[^1]); break;
                case Hmoveto: Move(s[^1], 0); break;
                case Vmoveto: Move(0, s[^1]); break;
                case Rlineto:
                    for (int i = 0; i + 1 < s.Count; i += 2) Line(s[i], s[i + 1]);
                    break;
                case Hlineto: AlternatingLines(horizontal: true); break;
                case Vlineto: AlternatingLines(horizontal: false); break;
                case Rrcurveto:
                    for (int i = 0; i + 5 < s.Count; i += 6) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
                    break;
                case Rcurveline: CurvesThenLine(); break;
                case Rlinecurve: LinesThenCurve(); break;
                case Vvcurveto: VerticalCurves(); break;
                case Hhcurveto: HorizontalCurves(); break;
                case Vhcurveto: AlternatingCurves(horizontal: false); break;
                case Hvcurveto: AlternatingCurves(horizontal: true); break;
            }
            s.Clear();
        }

        // n default values, n × regions deltas, then n: the values get their deltas weighted by the region scalars.
        private void RunBlend()
        {
            var s = _stack;
            int count = (int)s.Pop();
            int k = _blend.Length, first = s.Count - count * (k + 1);
            if (count < 0 || first < 0) throw new FormatException("Bad CFF2 blend.");
            for (int i = 0; i < count; i++)
                for (int j = 0; j < k; j++)
                    s[first + i] += s[first + count + i * k + j] * _blend[j];
            s.Count = first + count;
        }

        private void AlternatingLines(bool horizontal)
        {
            var s = _stack;
            for (int i = 0; i < s.Count; i++)
            {
                if (horizontal) Line(s[i], 0); else Line(0, s[i]);
                horizontal = !horizontal;
            }
        }

        private void CurvesThenLine()
        {
            var s = _stack;
            int i = 0;
            for (; i + 5 < s.Count - 2; i += 6) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
            if (i + 1 < s.Count) Line(s[i], s[i + 1]);
        }

        private void LinesThenCurve()
        {
            var s = _stack;
            int i = 0;
            for (; i + 1 < s.Count - 6; i += 2) Line(s[i], s[i + 1]);
            if (i + 5 < s.Count) Curve(s[i], s[i + 1], s[i + 2], s[i + 3], s[i + 4], s[i + 5]);
        }

        // vvcurveto: curves that start and end vertically, the first with an optional horizontal start.
        private void VerticalCurves()
        {
            var s = _stack;
            int i = 0;
            double dx1 = 0;
            if (s.Count % 4 == 1) dx1 = s[i++];
            for (; i + 3 < s.Count; i += 4)
            {
                Curve(dx1, s[i], s[i + 1], s[i + 2], 0, s[i + 3]);
                dx1 = 0;
            }
        }

        // hhcurveto: curves that start and end horizontally, the first with an optional vertical start.
        private void HorizontalCurves()
        {
            var s = _stack;
            int i = 0;
            double dy1 = 0;
            if (s.Count % 4 == 1) dy1 = s[i++];
            for (; i + 3 < s.Count; i += 4)
            {
                Curve(s[i], dy1, s[i + 1], s[i + 2], s[i + 3], 0);
                dy1 = 0;
            }
        }

        // vhcurveto and hvcurveto: curves whose tangents alternate; a fifth operand of the last ends it off the axis.
        private void AlternatingCurves(bool horizontal)
        {
            var s = _stack;
            for (int i = 0; i + 3 < s.Count; i += 4)
            {
                double last = s.Count - i == 5 ? s[i + 4] : 0;
                if (horizontal) Curve(s[i], 0, s[i + 1], s[i + 2], last, s[i + 3]);
                else Curve(0, s[i], s[i + 1], s[i + 2], s[i + 3], last);
                horizontal = !horizontal;
            }
        }

        // The flex operators draw their two curves; any other escaped operator, or a flex short of operands, only clears.
        private void RunEscaped(int op)
        {
            var s = _stack;
            switch (op)
            {
                case Flex when s.Count >= FlexOperands:
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                    break;
                case Hflex when s.Count >= HflexOperands:
                    Curve(s[0], 0, s[1], s[2], s[3], 0);
                    Curve(s[4], 0, s[5], -s[2], s[6], 0);
                    break;
                case Hflex1 when s.Count >= Hflex1Operands:
                    Curve(s[0], s[1], s[2], s[3], s[4], 0);
                    Curve(s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                    break;
                case Flex1 when s.Count >= Flex1Operands:
                {
                    // The last point is on the axis the curves travel furthest along.
                    double dx = s[0] + s[2] + s[4] + s[6] + s[8], dy = s[1] + s[3] + s[5] + s[7] + s[9];
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    if (Math.Abs(dx) > Math.Abs(dy)) Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                    else Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                    break;
                }
            }
            s.Clear();
        }

        private void Move(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            AddSegment(PathOp.MoveTo);
        }

        private void Line(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            AddSegment(PathOp.LineTo);
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = _x + dx1, y1 = _y + dy1, x2 = x1 + dx2, y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            _ops.Add(PathOp.CurveTo);
            _points.Add(x1);
            _points.Add(y1);
            _points.Add(x2);
            _points.Add(y2);
            _points.Add(_x);
            _points.Add(_y);
        }

        // A move or a line to the current point.
        private void AddSegment(PathOp op)
        {
            _ops.Add(op);
            _points.Add(_x);
            _points.Add(_y);
        }

        /// <summary>The outline as a Type 2 charstring with the width first, and its bounding box.</summary>
        public byte[] Encode(int width, out (short, short, short, short)? box)
        {
            var o = _encoded;
            o.Clear();
            if (width != 0) Number(o, width);
            int cx = 0, cy = 0;
            int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
            int point = 0;
            for (int segment = 0; segment < _ops.Count; segment++)
            {
                var op = _ops[segment];
                // A path must start with a moveto.
                if (segment == 0 && op != PathOp.MoveTo)
                {
                    Number(o, 0);
                    Number(o, 0);
                    o.Add(Rmoveto);
                }
                int pointEnd = point + (op == PathOp.CurveTo ? 6 : 2);
                for (; point < pointEnd; point += 2)
                {
                    // Points are rounded where they are, so the rounding does not add up along the path.
                    int x = (int)Math.Round(_points[point]), y = (int)Math.Round(_points[point + 1]);
                    Number(o, x - cx);
                    Number(o, y - cy);
                    cx = x;
                    cy = y;
                    if (op == PathOp.MoveTo) continue;
                    xMin = Math.Min(xMin, x); yMin = Math.Min(yMin, y);
                    xMax = Math.Max(xMax, x); yMax = Math.Max(yMax, y);
                }
                o.Add((byte)(op switch { PathOp.MoveTo => Rmoveto, PathOp.LineTo => Rlineto, _ => Rrcurveto }));
            }
            o.Add(Endchar);
            box = xMin <= xMax ? ((short)xMin, (short)yMin, (short)xMax, (short)yMax) : null;
            return o.ToArray();
        }

        // An integer in its shortest Type 2 form; one beyond 16 bits is clamped.
        private static void Number(List<byte> o, int v)
        {
            if (v >= -OneByteLimit && v <= OneByteLimit) o.Add((byte)(v + OneByteBias));
            else if (v >= TwoByteBias && v <= TwoByteLimit)
            {
                v -= TwoByteBias;
                o.Add((byte)(PositiveTwoByteFirst + (v >> 8)));
                o.Add((byte)v);
            }
            else if (v >= -TwoByteLimit && v <= -TwoByteBias)
            {
                v = -v - TwoByteBias;
                o.Add((byte)(NegativeTwoByteFirst + (v >> 8)));
                o.Add((byte)v);
            }
            else
            {
                v = Math.Clamp(v, short.MinValue, short.MaxValue);
                o.Add(ShortIntPrefix);
                o.Add((byte)(v >> 8));
                o.Add((byte)v);
            }
        }
    }
}

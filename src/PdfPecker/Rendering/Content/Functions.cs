using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

/// <summary>A PDF function (types 0, 2, 3 and 4) mapping m inputs to n outputs.</summary>
internal abstract class PdfFunction
{
    protected double[] Domain = { 0, 1 };
    protected double[]? Range;
    public int Inputs => Domain.Length / 2;
    public virtual int Outputs => Range != null ? Range.Length / 2 : 1;

    public abstract void Eval(ReadOnlySpan<double> input, Span<double> output);

    protected double ClipInput(double v, int i)
    {
        if (2 * i + 1 >= Domain.Length) return v;
        double lo = Domain[2 * i], hi = Domain[2 * i + 1];
        return v < lo ? lo : v > hi ? hi : double.IsNaN(v) ? lo : v;
    }

    protected void ClipOutput(Span<double> output)
    {
        if (Range == null) return;
        for (int i = 0; i < output.Length && 2 * i + 1 < Range.Length; i++)
            output[i] = Math.Clamp(output[i], Range[2 * i], Math.Max(Range[2 * i], Range[2 * i + 1]));
    }

    /// <summary>Parses a function object; an array of 1-output functions is combined into one n-output function.</summary>
    public static PdfFunction? Create(PdfFile file, PdfObject? obj, int depth = 0)
    {
        if (depth > 8) return null;
        obj = file.Resolve(obj);
        if (obj is PdfArray arr)
        {
            var parts = new List<PdfFunction>();
            foreach (var item in arr.Items)
            {
                var f = Create(file, item, depth + 1);
                if (f == null) return null;
                parts.Add(f);
            }
            return parts.Count == 0 ? null : new ArrayFunction(parts);
        }
        if (obj is not PdfDict d) return null;
        int type = file.GetInt(d, "FunctionType", -1);
        PdfFunction? fn = type switch
        {
            0 when d is PdfStream s => SampledFunction.Create(file, s),
            2 => new ExponentialFunction(file, d),
            3 => StitchingFunction.Create(file, d, depth),
            4 when d is PdfStream s4 => PostScriptFunction.Create(file, s4),
            _ => null,
        };
        if (fn == null) return null;
        fn.Domain = file.GetNumbers(d, "Domain") is { Length: >= 2 } dom ? dom : fn.Domain;
        fn.Range = file.GetNumbers(d, "Range") is { Length: >= 2 } rng ? rng : null;
        return fn;
    }

    private sealed class ArrayFunction : PdfFunction
    {
        private readonly List<PdfFunction> _parts;
        public ArrayFunction(List<PdfFunction> parts) { _parts = parts; Domain = new double[] { double.MinValue, double.MaxValue }; }
        public override int Outputs => _parts.Count;

        public override void Eval(ReadOnlySpan<double> input, Span<double> output)
        {
            Span<double> one = stackalloc double[8];
            for (int i = 0; i < _parts.Count && i < output.Length; i++)
            {
                _parts[i].Eval(input, one);
                output[i] = one[0];
            }
        }
    }
}

internal sealed class SampledFunction : PdfFunction
{
    private int[] _size = Array.Empty<int>();
    private double[] _encode = Array.Empty<double>(), _decode = Array.Empty<double>();
    private double[] _samples = Array.Empty<double>();
    private int _outputs;

    public override int Outputs => _outputs;

    public static SampledFunction? Create(PdfFile file, PdfStream s)
    {
        var size = file.GetNumbers(s, "Size");
        var range = file.GetNumbers(s, "Range");
        var domain = file.GetNumbers(s, "Domain");
        int bps = file.GetInt(s, "BitsPerSample", 8);
        if (size == null || range == null || domain == null || bps is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)) return null;
        int m = domain.Length / 2, n = range.Length / 2;
        if (m < 1 || m > 4 || n < 1 || size.Length < m) return null;
        var f = new SampledFunction { _outputs = n, _size = new int[m] };
        long total = n;
        for (int i = 0; i < m; i++)
        {
            f._size[i] = Math.Max(1, (int)size[i]);
            total *= f._size[i];
            if (total > 16_000_000) return null;
        }
        f._encode = file.GetNumbers(s, "Encode") ?? Enumerable.Range(0, m).SelectMany(i => new double[] { 0, f._size[i] - 1 }).ToArray();
        f._decode = file.GetNumbers(s, "Decode") ?? range;
        if (f._encode.Length < 2 * m || f._decode.Length < 2 * n) return null;
        var data = file.DecodeStream(s);
        f._samples = new double[total];
        double max = bps == 32 ? uint.MaxValue : (1L << bps) - 1;
        long bitPos = 0;
        for (long i = 0; i < total; i++)
        {
            long v = 0;
            for (int b = 0; b < bps; b++, bitPos++)
            {
                long byteIndex = bitPos >> 3;
                int bit = byteIndex < data.Length ? (data[byteIndex] >> (7 - (int)(bitPos & 7))) & 1 : 0;
                v = (v << 1) | (uint)bit;
            }
            f._samples[i] = v / max;
        }
        return f;
    }

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        int m = _size.Length, n = _outputs;
        // Multilinear interpolation over the 2^m corners of the enclosing cell.
        Span<int> i0 = stackalloc int[m];
        Span<double> frac = stackalloc double[m];
        for (int i = 0; i < m; i++)
        {
            double x = ClipInput(i < input.Length ? input[i] : 0, i);
            double d0 = Domain[2 * i], d1 = Domain[2 * i + 1];
            double e = d1 == d0 ? _encode[2 * i] : _encode[2 * i] + (x - d0) * (_encode[2 * i + 1] - _encode[2 * i]) / (d1 - d0);
            e = Math.Clamp(e, 0, _size[i] - 1);
            int k = Math.Min((int)e, _size[i] - 2);
            if (k < 0) k = 0;
            i0[i] = k;
            frac[i] = _size[i] == 1 ? 0 : e - k;
        }
        for (int j = 0; j < n && j < output.Length; j++)
        {
            double sum = 0;
            int corners = 1 << m;
            for (int c = 0; c < corners; c++)
            {
                double w = 1;
                long idx = 0, stride = n;
                for (int i = 0; i < m; i++)
                {
                    int bit = (c >> i) & 1;
                    int pos = Math.Min(i0[i] + bit, _size[i] - 1);
                    w *= bit == 1 ? frac[i] : 1 - frac[i];
                    idx += pos * stride;
                    stride *= _size[i];
                }
                if (w != 0) sum += w * _samples[idx + j];
            }
            double dlo = _decode[2 * j], dhi = _decode[2 * j + 1];
            output[j] = dlo + sum * (dhi - dlo);
        }
        ClipOutput(output.Slice(0, Math.Min(n, output.Length)));
    }
}

internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0, _c1;
    private readonly double _n;

    public ExponentialFunction(PdfFile file, PdfDict d)
    {
        _c0 = file.GetNumbers(d, "C0") ?? new double[] { 0 };
        _c1 = file.GetNumbers(d, "C1") ?? new double[] { 1 };
        _n = file.GetNumber(d, "N", 1);
    }

    public override int Outputs => Math.Min(_c0.Length, _c1.Length);

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        double x = ClipInput(input.Length > 0 ? input[0] : 0, 0);
        double xn = _n == 1 ? x : Math.Pow(x, _n);
        if (double.IsNaN(xn)) xn = 0;
        int k = Math.Min(Outputs, output.Length);
        for (int i = 0; i < k; i++) output[i] = _c0[i] + xn * (_c1[i] - _c0[i]);
        ClipOutput(output.Slice(0, k));
    }
}

internal sealed class StitchingFunction : PdfFunction
{
    private PdfFunction[] _fns = Array.Empty<PdfFunction>();
    private double[] _bounds = Array.Empty<double>(), _encode = Array.Empty<double>();

    public override int Outputs => Range != null ? Range.Length / 2 : _fns.Length > 0 ? _fns[0].Outputs : 1;

    public static StitchingFunction? Create(PdfFile file, PdfDict d, int depth)
    {
        if (file.GetArray(d, "Functions") is not PdfArray fa || fa.Count == 0) return null;
        var fns = new PdfFunction[fa.Count];
        for (int i = 0; i < fns.Length; i++)
        {
            var f = Create(file, fa[i], depth + 1);
            if (f == null) return null;
            fns[i] = f;
        }
        var bounds = file.GetNumbers(d, "Bounds") ?? Array.Empty<double>();
        var encode = file.GetNumbers(d, "Encode") ?? Enumerable.Repeat(new double[] { 0, 1 }, fns.Length).SelectMany(x => x).ToArray();
        if (encode.Length < 2 * fns.Length || bounds.Length < fns.Length - 1) return null;
        return new StitchingFunction { _fns = fns, _bounds = bounds, _encode = encode };
    }

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        double x = ClipInput(input.Length > 0 ? input[0] : 0, 0);
        int k = 0;
        while (k < _fns.Length - 1 && x >= _bounds[k]) k++;
        double lo = k == 0 ? Domain[0] : _bounds[k - 1];
        double hi = k == _fns.Length - 1 ? Domain[1] : _bounds[k];
        double e0 = _encode[2 * k], e1 = _encode[2 * k + 1];
        double t = hi == lo ? e0 : e0 + (x - lo) * (e1 - e0) / (hi - lo);
        Span<double> one = stackalloc double[1];
        one[0] = t;
        _fns[k].Eval(one, output);
        ClipOutput(output);
    }
}

/// <summary>A type 4 function: a PostScript calculator program, compiled to a flat instruction list.</summary>
internal sealed class PostScriptFunction : PdfFunction
{
    private enum Op : byte
    {
        Push, Abs, Add, Atan, Ceiling, Cos, Cvi, Cvr, Div, Exp, Floor, Idiv, Ln, Log, Mod, Mul, Neg, Round, Sin, Sqrt, Sub,
        Truncate, And, Or, Not, Xor, Bitshift, Eq, Ne, Gt, Ge, Lt, Le, True, False, Copy, Dup, Exch, Index, Pop, Roll,
        JumpIfFalse, Jump,
    }

    private readonly List<(Op Op, double Arg)> _code = new();

    public static PostScriptFunction? Create(PdfFile file, PdfStream s)
    {
        var data = file.DecodeStream(s);
        var lx = new PdfLexer(data);
        var f = new PostScriptFunction();
        if (lx.Next() != TokenKind.BraceOpen) return null;
        try
        {
            f.Compile(lx, 0);
        }
        catch (FormatException)
        {
            return null;
        }
        return f;
    }

    private void Compile(PdfLexer lx, int depth)
    {
        if (depth > 50) throw new FormatException();
        while (true)
        {
            var k = lx.Next();
            switch (k)
            {
                case TokenKind.Eof: return;
                case TokenKind.BraceClose: return;
                case TokenKind.Integer:
                case TokenKind.Real:
                    _code.Add((Op.Push, lx.Number));
                    continue;
                case TokenKind.BraceOpen:
                {
                    // { proc } if   or   { a } { b } ifelse
                    int jf = _code.Count;
                    _code.Add((Op.JumpIfFalse, 0));
                    Compile(lx, depth + 1);
                    int save = lx.Pos;
                    var k2 = lx.Next();
                    if (k2 == TokenKind.BraceOpen)
                    {
                        int j = _code.Count;
                        _code.Add((Op.Jump, 0));
                        _code[jf] = (Op.JumpIfFalse, _code.Count);
                        Compile(lx, depth + 1);
                        _code[j] = (Op.Jump, _code.Count);
                        if (lx.Next() != TokenKind.Keyword || !lx.KeywordIs("ifelse")) throw new FormatException();
                    }
                    else
                    {
                        if (k2 != TokenKind.Keyword || !lx.KeywordIs("if")) { lx.Pos = save; throw new FormatException(); }
                        _code[jf] = (Op.JumpIfFalse, _code.Count);
                    }
                    continue;
                }
                case TokenKind.Keyword:
                {
                    var name = lx.KeywordString();
                    Op op = name switch
                    {
                        "abs" => Op.Abs, "add" => Op.Add, "atan" => Op.Atan, "ceiling" => Op.Ceiling, "cos" => Op.Cos,
                        "cvi" => Op.Cvi, "cvr" => Op.Cvr, "div" => Op.Div, "exp" => Op.Exp, "floor" => Op.Floor,
                        "idiv" => Op.Idiv, "ln" => Op.Ln, "log" => Op.Log, "mod" => Op.Mod, "mul" => Op.Mul, "neg" => Op.Neg,
                        "round" => Op.Round, "sin" => Op.Sin, "sqrt" => Op.Sqrt, "sub" => Op.Sub, "truncate" => Op.Truncate,
                        "and" => Op.And, "or" => Op.Or, "not" => Op.Not, "xor" => Op.Xor, "bitshift" => Op.Bitshift,
                        "eq" => Op.Eq, "ne" => Op.Ne, "gt" => Op.Gt, "ge" => Op.Ge, "lt" => Op.Lt, "le" => Op.Le,
                        "true" => Op.True, "false" => Op.False, "copy" => Op.Copy, "dup" => Op.Dup, "exch" => Op.Exch,
                        "index" => Op.Index, "pop" => Op.Pop, "roll" => Op.Roll,
                        _ => throw new FormatException(),
                    };
                    _code.Add((op, 0));
                    continue;
                }
                default:
                    throw new FormatException();
            }
        }
    }

    public override int Outputs => Range != null ? Range.Length / 2 : 1;

    public override void Eval(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> st = stackalloc double[128];
        int sp = 0;
        for (int i = 0; i < Inputs && sp < st.Length; i++) st[sp++] = ClipInput(i < input.Length ? input[i] : 0, i);
        int steps = 0;
        for (int pc = 0; pc < _code.Count; pc++)
        {
            if (++steps > 100_000) break;
            var (op, arg) = _code[pc];
            if (sp >= st.Length - 4) break;
            switch (op)
            {
                case Op.Push: st[sp++] = arg; break;
                case Op.True: st[sp++] = 1; break;
                case Op.False: st[sp++] = 0; break;
                case Op.Jump: pc = (int)arg - 1; break;
                case Op.JumpIfFalse:
                    if (sp < 1) goto done;
                    if (st[--sp] == 0) pc = (int)arg - 1;
                    break;
                case Op.Dup:
                    if (sp < 1) goto done;
                    st[sp] = st[sp - 1]; sp++;
                    break;
                case Op.Pop: if (sp > 0) sp--; break;
                case Op.Exch:
                    if (sp < 2) goto done;
                    (st[sp - 1], st[sp - 2]) = (st[sp - 2], st[sp - 1]);
                    break;
                case Op.Copy:
                {
                    if (sp < 1) goto done;
                    int n = (int)st[--sp];
                    if (n < 0 || n > sp || sp + n >= st.Length) goto done;
                    for (int k = 0; k < n; k++) st[sp + k] = st[sp - n + k];
                    sp += n;
                    break;
                }
                case Op.Index:
                {
                    if (sp < 1) goto done;
                    int n = (int)st[sp - 1];
                    if (n < 0 || n >= sp - 1) goto done;
                    st[sp - 1] = st[sp - 2 - n];
                    break;
                }
                case Op.Roll:
                {
                    if (sp < 2) goto done;
                    int j = (int)st[--sp], n = (int)st[--sp];
                    if (n <= 0 || n > sp) break;
                    j = ((j % n) + n) % n;
                    if (j == 0) break;
                    Span<double> tmp = stackalloc double[n];
                    st.Slice(sp - n, n).CopyTo(tmp);
                    for (int k = 0; k < n; k++) st[sp - n + (k + j) % n] = tmp[k];
                    break;
                }
                default:
                {
                    // Unary and binary operators.
                    bool unary = op is Op.Abs or Op.Ceiling or Op.Cos or Op.Cvi or Op.Cvr or Op.Floor or Op.Ln or Op.Log or Op.Neg
                        or Op.Round or Op.Sin or Op.Sqrt or Op.Truncate or Op.Not;
                    if (unary)
                    {
                        if (sp < 1) goto done;
                        double a = st[sp - 1];
                        st[sp - 1] = op switch
                        {
                            Op.Abs => Math.Abs(a),
                            Op.Ceiling => Math.Ceiling(a),
                            Op.Cos => Math.Cos(a * Math.PI / 180),
                            Op.Cvi => Math.Truncate(a),
                            Op.Cvr => a,
                            Op.Floor => Math.Floor(a),
                            Op.Ln => Math.Log(a),
                            Op.Log => Math.Log10(a),
                            Op.Neg => -a,
                            Op.Round => Math.Floor(a + 0.5),
                            Op.Sin => Math.Sin(a * Math.PI / 180),
                            Op.Sqrt => Math.Sqrt(a),
                            Op.Truncate => Math.Truncate(a),
                            Op.Not => a == 0 ? 1 : a == 1 ? 0 : ~(long)a,
                            _ => a,
                        };
                        break;
                    }
                    if (sp < 2) goto done;
                    double y = st[--sp], x = st[sp - 1];
                    st[sp - 1] = op switch
                    {
                        Op.Add => x + y,
                        Op.Sub => x - y,
                        Op.Mul => x * y,
                        Op.Div => y == 0 ? 0 : x / y,
                        Op.Idiv => (long)y == 0 ? 0 : (long)x / (long)y,
                        Op.Mod => (long)y == 0 ? 0 : (long)x % (long)y,
                        Op.Exp => Math.Pow(x, y),
                        Op.Atan => (Math.Atan2(x, y) * 180 / Math.PI + 360) % 360,
                        Op.And => (long)x & (long)y,
                        Op.Or => (long)x | (long)y,
                        Op.Xor => (long)x ^ (long)y,
                        Op.Bitshift => y >= 0 ? (long)x << (int)Math.Min(63, y) : (long)x >> (int)Math.Min(63, -y),
                        Op.Eq => x == y ? 1 : 0,
                        Op.Ne => x != y ? 1 : 0,
                        Op.Gt => x > y ? 1 : 0,
                        Op.Ge => x >= y ? 1 : 0,
                        Op.Lt => x < y ? 1 : 0,
                        Op.Le => x <= y ? 1 : 0,
                        _ => x,
                    };
                    break;
                }
            }
        }
        done:
        int outs = Math.Min(Outputs, output.Length);
        for (int i = 0; i < outs; i++)
        {
            int si = sp - outs + i;
            output[i] = si >= 0 ? st[si] : 0;
            if (double.IsNaN(output[i]) || double.IsInfinity(output[i])) output[i] = 0;
        }
        ClipOutput(output.Slice(0, outs));
    }
}

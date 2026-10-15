using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The CSS math functions <c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>, the stepped value functions
/// <c>round()</c>, <c>mod()</c>, <c>rem()</c>, the sign-related <c>abs()</c> and <c>sign()</c>, the trigonometric functions
/// and <c>pow()</c>, <c>sqrt()</c>, <c>hypot()</c>, <c>log()</c> and <c>exp()</c>, nested in any combination, on numbers,
/// lengths and angles.
/// Font- and viewport-relative units are resolved while parsing; percentages stay symbolic until layout resolves them
/// against the property's own reference (the containing block's width for <c>width</c>, the parent's font size for
/// <c>font-size</c>, and so on). A sum of points and a percentage is kept as two numbers; anything else that depends
/// on a percentage, such as <c>min(100%, 600px)</c>, is kept as a function of the reference length.
/// </summary>
internal static class CssMath
{
    private const int MaxDepth = 32;

    /// <summary>True when <paramref name="v"/> contains a math function call (<c>calc(</c>, <c>min(</c>, <c>round(</c> and so on).</summary>
    public static bool HasMath(string v)
    {
        if (v.IndexOf('(') < 0) return false;
        for (int i = 0; i < v.Length; i++)
            if (FunctionAt(v, i) > 0) return true;
        return false;
    }

    /// <summary>True when the whole of <paramref name="v"/> is a single math function call.</summary>
    public static bool IsMathFunction(string v)
    {
        int open = FunctionAt(v, 0);
        return open > 0 && v.Length > 0 && v[^1] == ')' && MatchingParen(v, open - 1) == v.Length - 1;
    }

    /// <summary>Parses a math function in a length context.</summary>
    public static bool TryParseLength(string v, float em, float rem, float vw, float vh, out Length len)
    {
        len = default;
        var p = new Parser(v.ToLowerInvariant(), em, rem, vw, vh);
        if (!p.TryParseAll(out var t) || t.Kind != TermKind.Length) return false;
        if (t.Fn == null)
        {
            if (!float.IsFinite(t.Pt) || !float.IsFinite(t.Pct)) return false;
            len = t.Pct == 0 ? Length.Pt(t.Pt) : t.Pt == 0 ? new Length(t.Pct, LengthUnit.Percent) : Length.Calc(t.Pt, t.Pct);
        }
        else len = Length.Calc(t.Fn);
        return true;
    }

    /// <summary>Parses a math function whose result is a plain number (no units).</summary>
    public static bool TryParseNumber(string v, out float number)
    {
        number = 0;
        var p = new Parser(v.ToLowerInvariant(), 12, 12, 0, 0);
        if (!p.TryParseAll(out var t) || t.Kind != TermKind.Number || !float.IsFinite(t.Pt)) return false;
        number = t.Pt;
        return true;
    }

    /// <summary>Parses a math function whose result is an angle, in degrees.</summary>
    public static bool TryParseAngle(string v, out float degrees)
    {
        degrees = 0;
        var p = new Parser(v.ToLowerInvariant(), 12, 12, 0, 0);
        if (!p.TryParseAll(out var t) || t.Kind != TermKind.Angle || !float.IsFinite(t.Pt)) return false;
        degrees = t.Pt;
        return true;
    }

    /// <summary>
    /// Replaces each math function in <paramref name="v"/> whose result is a plain number with that number, so that
    /// properties that take numbers (<c>opacity</c>, <c>z-index</c>, <c>flex-grow</c>, <c>line-height</c>, colour
    /// components) can read it. Functions that produce lengths are left for the length parser.
    /// </summary>
    public static string FoldNumbers(string v)
    {
        StringBuilder? sb = null;
        int copied = 0;
        char quote = '\0';
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\') i++;
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            int open = FunctionAt(v, i);
            if (open <= 0) continue;
            int close = MatchingParen(v, open - 1);
            if (close < 0) break;
            string call = v.Substring(i, close + 1 - i);
            string? folded = TryParseNumber(call, out float n) ? n.ToString("0.######", CultureInfo.InvariantCulture)
                : TryParseAngle(call, out float deg) ? deg.ToString("0.######", CultureInfo.InvariantCulture) + "deg" : null;
            if (folded != null)
            {
                sb ??= new StringBuilder(v.Length);
                sb.Append(v, copied, i - copied).Append(folded);
                copied = close + 1;
            }
            i = close;
        }
        if (sb == null) return v;
        return sb.Append(v, copied, v.Length - copied).ToString();
    }

    /// <summary>When a math function's name starts at <paramref name="i"/>, the index just past its "("; otherwise -1.</summary>
    private static int FunctionAt(string v, int i)
    {
        if (i > 0 && (char.IsLetterOrDigit(v[i - 1]) || v[i - 1] is '-' or '_')) return -1;
        int start = i;
        if (string.Compare(v, i, "-webkit-", 0, 8, StringComparison.OrdinalIgnoreCase) == 0) start += 8;
        foreach (var name in Names)
            if (start + name.Length < v.Length && v[start + name.Length] == '('
                && string.Compare(v, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return start + name.Length + 1;
        return -1;
    }

    // Longer names first where one is a prefix of another (atan2 before atan).
    private static readonly string[] Names =
    {
        "calc", "min", "max", "clamp", "round", "mod", "rem", "abs", "sign", "sin", "cos", "tan", "asin", "acos", "atan2", "atan",
        "pow", "sqrt", "hypot", "log", "exp",
    };

    private static int MatchingParen(string v, int open)
    {
        int depth = 0;
        for (int i = open; i < v.Length; i++)
        {
            if (v[i] == '(') depth++;
            else if (v[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private enum TermKind : byte { Number, Length, Angle }

    /// <summary>
    /// A value during evaluation: a number, an angle in degrees, or a length that is <c>Pt + Pct% of the reference</c>
    /// or, once a function has mixed percentages with other units, a function of the reference length.
    /// </summary>
    private readonly record struct Term(TermKind Kind, float Pt, float Pct = 0, Func<float, float>? Fn = null)
    {
        public bool IsNumber => Kind == TermKind.Number;
        public bool IsConstant => Fn == null && Pct == 0;
        public Func<float, float> AsFn()
        {
            if (Fn != null) return Fn;
            float pt = Pt, pct = Pct;
            return basis => pt + pct * basis / 100f;
        }
    }

    private ref struct Parser
    {
        private readonly string _s;
        private readonly float _em, _rem, _vw, _vh;
        private int _pos, _depth;

        public Parser(string s, float em, float rem, float vw, float vh)
        {
            _s = s; _em = em; _rem = rem; _vw = vw; _vh = vh; _pos = 0; _depth = 0;
        }

        public bool TryParseAll(out Term t)
        {
            t = default;
            SkipWs();
            if (!TryPrimary(out t)) return false;
            SkipWs();
            return _pos == _s.Length;
        }

        private void SkipWs()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
        }

        private bool TrySum(out Term t)
        {
            if (!TryProduct(out t)) return false;
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] is not ('+' or '-')) return true;
                float sign = _s[_pos++] == '-' ? -1 : 1;
                SkipWs();
                if (!TryProduct(out var r) || r.Kind != t.Kind) return false;
                t = Add(t, r, sign);
            }
        }

        private bool TryProduct(out Term t)
        {
            if (!TryPrimary(out t)) return false;
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] is not ('*' or '/')) return true;
                bool divide = _s[_pos++] == '/';
                SkipWs();
                if (!TryPrimary(out var r)) return false;
                if (divide)
                {
                    if (!r.IsNumber) return false;
                    if (r.Pt == 0)
                    {
                        // Division by zero gives infinity (clamped by the property), as in Chrome; 0/0 is NaN, which fails.
                        if (!t.IsConstant || t.Pt == 0) return false;
                        t = t with { Pt = t.Pt > 0 ? float.PositiveInfinity : float.NegativeInfinity };
                        continue;
                    }
                    t = Scale(t, 1 / r.Pt);
                }
                else if (r.IsNumber) t = Scale(t, r.Pt);
                else if (t.IsNumber) t = Scale(r, t.Pt);
                else return false;
            }
        }

        private bool TryPrimary(out Term t)
        {
            t = default;
            SkipWs();
            if (_pos >= _s.Length) return false;
            char c = _s[_pos];
            if (c == '(') return Group(out t, ')');
            if (char.IsDigit(c) || c == '.' || (c is '+' or '-') && _pos + 1 < _s.Length && (char.IsDigit(_s[_pos + 1]) || _s[_pos + 1] == '.'))
                return TryDimension(out t);

            int nameStart = _pos;
            if (string.CompareOrdinal(_s, _pos, "-webkit-", 0, 8) == 0) _pos += 8;
            int identStart = _pos;
            while (_pos < _s.Length && (char.IsLetter(_s[_pos]) || _s[_pos] == '-' || _pos > identStart && char.IsDigit(_s[_pos]))) _pos++;
            var name = _s.AsSpan(identStart, _pos - identStart);
            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                if (name.SequenceEqual("calc")) return Group(out t, ')', alreadyOpen: true);
                if (name.SequenceEqual("min") || name.SequenceEqual("max")) return MinMax(out t, name.SequenceEqual("min"));
                if (name.SequenceEqual("clamp")) return Clamp(out t);
                return Function(name.ToString(), out t);
            }
            if (identStart != nameStart) return false;
            if (name.SequenceEqual("pi")) { t = new Term(TermKind.Number, MathF.PI); return true; }
            if (name.SequenceEqual("e")) { t = new Term(TermKind.Number, MathF.E); return true; }
            if (name.SequenceEqual("infinity")) { t = new Term(TermKind.Number, float.PositiveInfinity); return true; }
            if (name.SequenceEqual("-infinity")) { t = new Term(TermKind.Number, float.NegativeInfinity); return true; }
            return false;
        }

        private bool Group(out Term t, char close, bool alreadyOpen = false)
        {
            t = default;
            if (!alreadyOpen) _pos++;
            if (++_depth > MaxDepth) return false;
            bool ok = TrySum(out t);
            _depth--;
            SkipWs();
            if (!ok || _pos >= _s.Length || _s[_pos] != close) return false;
            _pos++;
            return true;
        }

        private bool Arguments(List<Term> args, bool sameKind)
        {
            if (++_depth > MaxDepth) return false;
            try
            {
                while (true)
                {
                    if (!TrySum(out var a)) return false;
                    if (sameKind && args.Count > 0 && a.Kind != args[0].Kind) return false;
                    args.Add(a);
                    SkipWs();
                    if (_pos >= _s.Length) return false;
                    char c = _s[_pos++];
                    if (c == ')') return true;
                    if (c != ',') return false;
                }
            }
            finally { _depth--; }
        }

        private bool MinMax(out Term t, bool min)
        {
            t = default;
            var args = new List<Term>(2);
            if (!Arguments(args, sameKind: true)) return false;
            t = args[0];
            for (int i = 1; i < args.Count; i++) t = Pick(t, args[i], min);
            return true;
        }

        private bool Clamp(out Term t)
        {
            t = default;
            var args = new List<Term>(3);
            if (!Arguments(args, sameKind: true) || args.Count != 3) return false;
            // clamp(MIN, VAL, MAX) = max(MIN, min(VAL, MAX)): MIN wins when it exceeds MAX.
            t = Pick(args[0], Pick(args[1], args[2], min: true), min: false);
            return true;
        }

        private bool TryDimension(out Term t)
        {
            t = default;
            int start = _pos;
            if (_s[_pos] is '+' or '-') _pos++;
            while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
            if (_pos < _s.Length && _s[_pos] == '.') { _pos++; while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++; }
            // An exponent, but not the "e" of "em"/"ex".
            if (_pos + 1 < _s.Length && _s[_pos] == 'e'
                && (char.IsDigit(_s[_pos + 1]) || _s[_pos + 1] is '+' or '-' && _pos + 2 < _s.Length && char.IsDigit(_s[_pos + 2])))
            {
                _pos += 2;
                while (_pos < _s.Length && char.IsDigit(_s[_pos])) _pos++;
            }
            int numEnd = _pos;
            if (_pos < _s.Length && _s[_pos] == '%') _pos++;
            else while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
            if (numEnd == _pos)
            {
                if (!float.TryParse(_s.AsSpan(start, numEnd - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)) return false;
                t = new Term(TermKind.Number, n);
                return true;
            }
            if (ColorFunctions.TryAngle(_s.Substring(start, _pos - start), out double deg))
            {
                t = new Term(TermKind.Angle, (float)deg);
                return true;
            }
            if (!StyleResolver.TryParseLength(_s.Substring(start, _pos - start), _em, _rem, out var len, _vw, _vh) || len.IsAuto) return false;
            t = len.Unit == LengthUnit.Percent ? new Term(TermKind.Length, 0, len.Value) : new Term(TermKind.Length, len.Value);
            return true;
        }

        private static Term Add(Term a, Term b, float sign)
        {
            if (a.Fn == null && b.Fn == null) return new Term(a.Kind, a.Pt + sign * b.Pt, a.Pct + sign * b.Pct);
            var fa = a.AsFn();
            var fb = b.AsFn();
            return new Term(TermKind.Length, 0, 0, basis => fa(basis) + sign * fb(basis));
        }

        private static Term Scale(Term a, float k)
        {
            if (a.Fn == null) return new Term(a.Kind, a.Pt * k, a.Pct * k);
            var fa = a.Fn;
            return new Term(TermKind.Length, 0, 0, basis => fa(basis) * k);
        }

        private static Term Pick(Term a, Term b, bool min)
        {
            if (a.IsConstant && b.IsConstant) return min ? (a.Pt <= b.Pt ? a : b) : (a.Pt >= b.Pt ? a : b);
            // Plain percentages compare directly, as the reference length is never negative: min(50%, 80%) is 50%.
            if (a.Fn == null && b.Fn == null && a.Pt == 0 && b.Pt == 0)
                return min ? (a.Pct <= b.Pct ? a : b) : (a.Pct >= b.Pct ? a : b);
            var fa = a.AsFn();
            var fb = b.AsFn();
            return new Term(TermKind.Length, 0, 0, min ? basis => MathF.Min(fa(basis), fb(basis)) : basis => MathF.Max(fa(basis), fb(basis)));
        }

        /// <summary>The functions after clamp(): stepped values, signs, trigonometry and exponentials.</summary>
        private bool Function(string name, out Term t)
        {
            t = default;
            string? strategy = null;
            if (name == "round")
            {
                // An optional rounding strategy comes first.
                SkipWs();
                foreach (var s in new[] { "nearest", "up", "down", "to-zero" })
                    if (string.CompareOrdinal(_s, _pos, s, 0, s.Length) == 0 && _pos + s.Length < _s.Length && _s[_pos + s.Length] is ',' or ' ')
                    {
                        strategy = s;
                        _pos += s.Length;
                        SkipWs();
                        if (_pos >= _s.Length || _s[_pos++] != ',') return false;
                        break;
                    }
            }
            var args = new List<Term>(2);
            if (!Arguments(args, sameKind: false)) return false;
            switch (name)
            {
                case "round":
                {
                    if (args.Count == 1)
                    {
                        if (!args[0].IsNumber) return false;
                        args.Add(new Term(TermKind.Number, 1));
                    }
                    if (args.Count != 2 || args[0].Kind != args[1].Kind) return false;
                    var mode = strategy ?? "nearest";
                    t = Binary(args[0], args[1], (a, b) => RoundTo(a, b, mode));
                    return true;
                }
                case "mod":
                case "rem":
                {
                    if (args.Count != 2 || args[0].Kind != args[1].Kind) return false;
                    bool mod = name == "mod";
                    t = Binary(args[0], args[1], (a, b) =>
                    {
                        if (b == 0 || float.IsInfinity(a)) return float.NaN;
                        if (float.IsInfinity(b)) return mod && (a < 0) != (b < 0) && a != 0 ? float.NaN : a;
                        float q = a / b;
                        return a - b * (mod ? MathF.Floor(q) : MathF.Truncate(q));
                    });
                    return true;
                }
                case "abs":
                    if (args.Count != 1) return false;
                    t = Unary(args[0], MathF.Abs);
                    return true;
                case "sign":
                    if (args.Count != 1 || !args[0].IsConstant) return false;
                    t = new Term(TermKind.Number, MathF.Sign(args[0].Pt));
                    return true;
                case "sin":
                case "cos":
                case "tan":
                {
                    if (args.Count != 1 || args[0].Kind == TermKind.Length) return false;
                    // A number is in radians.
                    double rad = args[0].Kind == TermKind.Angle ? args[0].Pt * Math.PI / 180 : args[0].Pt;
                    double r = name == "sin" ? Math.Sin(rad) : name == "cos" ? Math.Cos(rad) : Math.Tan(rad);
                    t = new Term(TermKind.Number, (float)r);
                    return true;
                }
                case "asin":
                case "acos":
                case "atan":
                {
                    if (args.Count != 1 || !args[0].IsNumber) return false;
                    double x = args[0].Pt;
                    double r = name == "asin" ? Math.Asin(x) : name == "acos" ? Math.Acos(x) : Math.Atan(x);
                    t = new Term(TermKind.Angle, (float)(r * 180 / Math.PI));
                    return true;
                }
                case "atan2":
                {
                    if (args.Count != 2 || args[0].Kind != args[1].Kind || !args[0].IsConstant || !args[1].IsConstant) return false;
                    t = new Term(TermKind.Angle, (float)(Math.Atan2(args[0].Pt, args[1].Pt) * 180 / Math.PI));
                    return true;
                }
                case "pow":
                    if (args.Count != 2 || !args[0].IsNumber || !args[1].IsNumber) return false;
                    t = new Term(TermKind.Number, MathF.Pow(args[0].Pt, args[1].Pt));
                    return true;
                case "sqrt":
                    if (args.Count != 1 || !args[0].IsNumber) return false;
                    t = new Term(TermKind.Number, MathF.Sqrt(args[0].Pt));
                    return true;
                case "exp":
                    if (args.Count != 1 || !args[0].IsNumber) return false;
                    t = new Term(TermKind.Number, MathF.Exp(args[0].Pt));
                    return true;
                case "log":
                {
                    if (args.Count is < 1 or > 2 || !args[0].IsNumber || (args.Count == 2 && !args[1].IsNumber)) return false;
                    double r = Math.Log(args[0].Pt);
                    if (args.Count == 2) r /= Math.Log(args[1].Pt);
                    t = new Term(TermKind.Number, (float)r);
                    return true;
                }
                case "hypot":
                {
                    if (args.Count == 0) return false;
                    foreach (var a in args)
                        if (a.Kind != args[0].Kind) return false;
                    if (args.All(a => a.IsConstant))
                    {
                        double sum = 0;
                        foreach (var a in args) sum += (double)a.Pt * a.Pt;
                        t = new Term(args[0].Kind, (float)Math.Sqrt(sum));
                        return true;
                    }
                    var fns = args.Select(a => a.AsFn()).ToArray();
                    t = new Term(TermKind.Length, 0, 0, basis =>
                    {
                        double sum = 0;
                        foreach (var f in fns) { double x = f(basis); sum += x * x; }
                        return (float)Math.Sqrt(sum);
                    });
                    return true;
                }
            }
            return false;
        }

        private static float RoundTo(float a, float b, string mode)
        {
            if (b == 0 || float.IsInfinity(a) && float.IsInfinity(b)) return float.NaN;
            if (float.IsInfinity(a)) return a;
            b = MathF.Abs(b);
            if (float.IsInfinity(b))
                return mode switch { "up" => a > 0 ? float.PositiveInfinity : 0, "down" => a < 0 ? float.NegativeInfinity : 0, _ => 0 };
            double q = (double)a / b;
            double r = mode switch
            {
                "up" => Math.Ceiling(q),
                "down" => Math.Floor(q),
                "to-zero" => Math.Truncate(q),
                _ => Math.Floor(q + 0.5), // a half rounds towards +infinity
            };
            return (float)(r * b);
        }

        private static Term Unary(Term a, Func<float, float> f)
        {
            if (a.IsConstant) return new Term(a.Kind, f(a.Pt));
            var fa = a.AsFn();
            return new Term(TermKind.Length, 0, 0, basis => f(fa(basis)));
        }

        private static Term Binary(Term a, Term b, Func<float, float, float> f)
        {
            if (a.IsConstant && b.IsConstant) return new Term(a.Kind, f(a.Pt, b.Pt));
            var fa = a.AsFn();
            var fb = b.AsFn();
            return new Term(TermKind.Length, 0, 0, basis => f(fa(basis), fb(basis)));
        }
    }
}

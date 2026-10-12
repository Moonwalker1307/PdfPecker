using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The CSS math functions <c>calc()</c>, <c>min()</c>, <c>max()</c> and <c>clamp()</c>, nested in any combination.
/// Font- and viewport-relative units are resolved while parsing; percentages stay symbolic until layout resolves them
/// against the property's own reference (the containing block's width for <c>width</c>, the parent's font size for
/// <c>font-size</c>, and so on). A sum of points and a percentage is kept as two numbers; anything else that depends
/// on a percentage, such as <c>min(100%, 600px)</c>, is kept as a function of the reference length.
/// </summary>
internal static class CssMath
{
    private const int MaxDepth = 32;

    /// <summary>True when <paramref name="v"/> contains a math function call (<c>calc(</c>, <c>min(</c>, <c>max(</c>, <c>clamp(</c>).</summary>
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
        if (!p.TryParseAll(out var t) || t.IsNumber) return false;
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
        if (!p.TryParseAll(out var t) || !t.IsNumber || !float.IsFinite(t.Pt)) return false;
        number = t.Pt;
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
            if (TryParseNumber(v.Substring(i, close + 1 - i), out float n))
            {
                sb ??= new StringBuilder(v.Length);
                sb.Append(v, copied, i - copied).Append(n.ToString("0.######", CultureInfo.InvariantCulture));
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

    private static readonly string[] Names = { "calc", "min", "max", "clamp" };

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

    /// <summary>
    /// A value during evaluation: a number, or a length that is <c>Pt + Pct% of the reference</c>, or, once min/max
    /// have mixed percentages with other units, a function of the reference length.
    /// </summary>
    private readonly record struct Term(bool IsNumber, float Pt, float Pct = 0, Func<float, float>? Fn = null)
    {
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
                if (!TryProduct(out var r) || r.IsNumber != t.IsNumber) return false;
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
                    if (!r.IsNumber || r.Pt == 0) return false;
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
            while (_pos < _s.Length && (char.IsLetter(_s[_pos]) || _s[_pos] == '-')) _pos++;
            var name = _s.AsSpan(identStart, _pos - identStart);
            if (_pos < _s.Length && _s[_pos] == '(')
            {
                _pos++;
                if (name.SequenceEqual("calc")) return Group(out t, ')', alreadyOpen: true);
                if (name.SequenceEqual("min") || name.SequenceEqual("max")) return MinMax(out t, name.SequenceEqual("min"));
                if (name.SequenceEqual("clamp")) return Clamp(out t);
                return false;
            }
            if (identStart != nameStart) return false;
            if (name.SequenceEqual("pi")) { t = new Term(true, MathF.PI); return true; }
            if (name.SequenceEqual("e")) { t = new Term(true, MathF.E); return true; }
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

        private bool Arguments(List<Term> args)
        {
            if (++_depth > MaxDepth) return false;
            try
            {
                while (true)
                {
                    if (!TrySum(out var a)) return false;
                    if (args.Count > 0 && a.IsNumber != args[0].IsNumber) return false;
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
            if (!Arguments(args)) return false;
            t = args[0];
            for (int i = 1; i < args.Count; i++) t = Pick(t, args[i], min);
            return true;
        }

        private bool Clamp(out Term t)
        {
            t = default;
            var args = new List<Term>(3);
            if (!Arguments(args) || args.Count != 3) return false;
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
                t = new Term(true, n);
                return true;
            }
            if (!StyleResolver.TryParseLength(_s.Substring(start, _pos - start), _em, _rem, out var len, _vw, _vh) || len.IsAuto) return false;
            t = len.Unit == LengthUnit.Percent ? new Term(false, 0, len.Value) : new Term(false, len.Value);
            return true;
        }

        private static Term Add(Term a, Term b, float sign)
        {
            if (a.Fn == null && b.Fn == null) return new Term(a.IsNumber, a.Pt + sign * b.Pt, a.Pct + sign * b.Pct);
            var fa = a.AsFn();
            var fb = b.AsFn();
            return new Term(false, 0, 0, basis => fa(basis) + sign * fb(basis));
        }

        private static Term Scale(Term a, float k)
        {
            if (a.Fn == null) return new Term(a.IsNumber, a.Pt * k, a.Pct * k);
            var fa = a.Fn;
            return new Term(false, 0, 0, basis => fa(basis) * k);
        }

        private static Term Pick(Term a, Term b, bool min)
        {
            if (a.IsConstant && b.IsConstant) return min ? (a.Pt <= b.Pt ? a : b) : (a.Pt >= b.Pt ? a : b);
            // Plain percentages compare directly, as the reference length is never negative: min(50%, 80%) is 50%.
            if (a.Fn == null && b.Fn == null && a.Pt == 0 && b.Pt == 0)
                return min ? (a.Pct <= b.Pct ? a : b) : (a.Pct >= b.Pct ? a : b);
            var fa = a.AsFn();
            var fb = b.AsFn();
            return new Term(false, 0, 0, min ? basis => MathF.Min(fa(basis), fb(basis)) : basis => MathF.Max(fa(basis), fb(basis)));
        }
    }
}

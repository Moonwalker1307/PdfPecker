using System.Globalization;
using System.Runtime.CompilerServices;

namespace PdfPecker.Svg;

/// <summary>
/// Reads SVG numbers: "1.5.5" is two numbers, "-1-2" is two, exponents are allowed and the whitespace or comma between
/// numbers is optional. Path data also reads its command letters and arc flags through it.
/// </summary>
internal ref struct NumberLexer
{
    private readonly ReadOnlySpan<char> _text;
    private int _position;

    public NumberLexer(ReadOnlySpan<char> text, int position)
    {
        _text = text;
        _position = position;
    }

    /// <summary>The next character after any separators, or false when only separators are left.</summary>
    public bool TryPeek(out char next)
    {
        _position = SkipSeparators(_text, _position);
        if (_position >= _text.Length)
        {
            next = '\0';
            return false;
        }
        next = _text[_position];
        return true;
    }

    /// <summary>Moves past the character <see cref="TryPeek"/> returned.</summary>
    public void Skip() => _position++;

    /// <summary>
    /// Reads the next number. False when none starts here, or when its text has digits that are not ASCII; either way
    /// the caller stops reading, so where the lexer then stands does not matter.
    /// </summary>
    public bool TryNumber(out float value)
    {
        value = 0;
        // One pass finds the end of the number and adds up a plain decimal on the way. It works on locals rather than
        // the fields, which keeps the loop out of memory.
        var text = _text;
        int start = SkipSeparators(text, _position);
        _position = start;
        if (start >= text.Length || !IsNumberStart(text[start])) return false;
        var plain = new PlainDecimal();
        int i = start;
        if (text[i] is '+' or '-')
        {
            plain.Negative = text[i] == '-';
            i++;
        }
        // Digits with at most one decimal point among them, so that a second point starts the next number.
        bool digits = false, point = false;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits = true;
                plain.AddDigit(c);
            }
            else if (c == '.' && !point)
            {
                point = true;
                plain.StartFraction();
            }
            else if (char.IsDigit(c))
            {
                digits = true;
                plain.GiveUp();
            }
            else break;
        }
        if (!digits) return false;
        int end = SkipExponent(text, i);
        if (end != i) plain.GiveUp();
        _position = end;
        if (plain.TryGetValue(out double magnitude))
        {
            value = (float)magnitude;
            return true;
        }
        return float.TryParse(text[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>An arc flag: a single 0 or 1, which may be followed directly by the next number.</summary>
    public bool TryFlag(out bool flag)
    {
        flag = false;
        if (!TryPeek(out char next) || next is not ('0' or '1')) return false;
        flag = next == '1';
        _position++;
        return true;
    }

    /// <summary>Whether a number may start with this character: a digit, a sign or a decimal point.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumberStart(char c) => char.IsDigit(c) || c is '-' or '+' or '.';

    // The space and the comma come first only because they are the usual separators.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SkipSeparators(ReadOnlySpan<char> text, int i)
    {
        while (i < text.Length && (text[i] is ' ' or ',' || char.IsWhiteSpace(text[i]))) i++;
        return i;
    }

    // An "e" is an exponent only when digits follow it, after an optional sign; otherwise it is left for the next
    // token, as in "1e" or "2E+".
    private static int SkipExponent(ReadOnlySpan<char> text, int i)
    {
        int end = i;
        if (end >= text.Length || text[end] is not ('e' or 'E')) return i;
        end++;
        if (end < text.Length && text[end] is '+' or '-') end++;
        if (end >= text.Length || !char.IsDigit(text[end])) return i;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return end;
    }

    /// <summary>
    /// The fast path for a sign, ASCII digits and a decimal point with at most 15 significant digits, by far the most
    /// common numbers in path data. Anything else (an exponent, more digits, other digits) gives up, and float.TryParse
    /// reads the text instead.
    /// </summary>
    /// <remarks>
    /// A fraction of more than 15 digits that has fewer significant ones, such as 0.0000000000000001, is divided by
    /// 10^15 only and so reads too large: a known quirk, kept so that existing output does not change.
    /// </remarks>
    private struct PlainDecimal
    {
        // Up to this many significant digits a plain decimal fits a long exactly.
        private const int MaxDigits = 15;

        public bool Negative;
        private long _mantissa;
        private int _significantDigits;
        private int _fractionDigits;
        private bool _inFraction;
        private bool _gaveUp;

        private static ReadOnlySpan<double> PowersOfTen => [1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15];

        public void AddDigit(char digit)
        {
            if (_gaveUp) return;
            if (_significantDigits >= MaxDigits)
            {
                _gaveUp = true;
                return;
            }
            _mantissa = _mantissa * 10 + (digit - '0');
            if (_mantissa != 0) _significantDigits++;
            if (_inFraction) _fractionDigits++;
        }

        public void StartFraction() => _inFraction = true;

        public void GiveUp() => _gaveUp = true;

        public readonly bool TryGetValue(out double value)
        {
            value = 0;
            if (_gaveUp) return false;
            double magnitude = _fractionDigits > 0 ? _mantissa / PowersOfTen[Math.Min(_fractionDigits, PowersOfTen.Length - 1)] : _mantissa;
            value = Negative ? -magnitude : magnitude;
            return true;
        }
    }
}

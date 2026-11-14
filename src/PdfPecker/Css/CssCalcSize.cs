namespace PdfPecker.Css;

/// <summary>
/// How calc-size() changes a size taken from a keyword (auto, min-content, max-content or fit-content): the box's size
/// in its box-sizing box is <c>Scale × size + Offset</c>, where the offset may hold a percentage of the containing
/// block.
/// </summary>
internal sealed record CalcSizeAdjust(float Scale, float OffsetPt, float OffsetPct)
{
    public float Apply(float size, float basis) => Length.Clamp(Scale * size + OffsetPt + OffsetPct * basis / 100f);

    /// <summary>This adjustment applied after <paramref name="inner"/>, for calc-size() nested in a basis.</summary>
    public CalcSizeAdjust After(CalcSizeAdjust inner)
        => new(Scale * inner.Scale, Scale * inner.OffsetPt + OffsetPt, Scale * inner.OffsetPct + OffsetPct);
}

/// <summary>
/// calc-size() of CSS Values 5, as Chrome ships it: <c>calc-size(basis, calculation)</c>, where <c>size</c> in the
/// calculation stands for the basis. A length or percentage basis makes an ordinary calc(); a keyword basis keeps the
/// keyword, adjusted by a calculation linear in <c>size</c>; <c>any</c> takes a calculation without <c>size</c>.
/// </summary>
internal static class CssCalcSize
{
    private const int MaxDepth = 8;

    public static bool Is(string lower) => lower.StartsWith("calc-size(", StringComparison.Ordinal);

    /// <summary>
    /// Reads a calc-size() value. Gives either <paramref name="value"/>, a plain value for the property to parse (a
    /// calc() or a keyword), or a keyword in <paramref name="value"/> with the <paramref name="adjust"/> to apply to the
    /// size it gives. False when the value is not valid.
    /// </summary>
    public static bool TryRead(string lower, float em, float rem, float vw, float vh, out string value, out CalcSizeAdjust? adjust)
        => TryRead(lower, em, rem, vw, vh, 0, out value, out adjust);

    private static bool TryRead(string lower, float em, float rem, float vw, float vh, int depth, out string value, out CalcSizeAdjust? adjust)
    {
        value = "";
        adjust = null;
        lower = lower.Trim();
        if (depth > MaxDepth || !Is(lower) || lower[^1] != ')' || MediaQuery.MatchingParen(lower, 9) != lower.Length - 1) return false;
        var inner = lower.Substring(10, lower.Length - 11);
        int comma = TopLevelComma(inner);
        if (comma < 0) return false;
        var basis = inner.Substring(0, comma).Trim();
        var calculation = inner.Substring(comma + 1).Trim();
        if (calculation.Length == 0 || TopLevelComma(calculation) >= 0) return false;

        CalcSizeAdjust? innerAdjust = null;
        if (Is(basis))
        {
            if (!TryRead(basis, em, rem, vw, vh, depth + 1, out basis, out innerAdjust)) return false;
        }
        if (basis == "any")
        {
            if (HasSize(calculation)) return false;
            value = "calc(" + calculation + ")";
            return CssMath.TryParseLength(value, em, rem, vw, vh, out _);
        }
        if (basis is "auto" or "min-content" or "max-content" or "fit-content")
        {
            value = basis;
            if (calculation == "size")
            {
                adjust = innerAdjust;
                return true;
            }
            // The calculation must be linear in size: read it at three sizes and check that they line up.
            if (!Sample(calculation, 0, em, rem, vw, vh, out var at0) || !Sample(calculation, 100, em, rem, vw, vh, out var at100)
                || !Sample(calculation, 1000, em, rem, vw, vh, out var at1000)) return false;
            if (at0.Pct != at100.Pct || at0.Pct != at1000.Pct) return false;
            float scale = (at100.Pt - at0.Pt) / 75f;
            if (MathF.Abs(at1000.Pt - (at0.Pt + scale * 750f)) > 0.01f * MathF.Max(1, MathF.Abs(at1000.Pt))) return false;
            var own = new CalcSizeAdjust(scale, at0.Pt, at0.Pct);
            adjust = innerAdjust == null ? own : own.After(innerAdjust);
            return true;
        }
        if (innerAdjust != null) return false;
        // A length or percentage basis: size is that value, and the whole is a calc().
        value = "calc(" + ReplaceSize(calculation, "(" + basis + ")") + ")";
        return CssMath.TryParseLength(value, em, rem, vw, vh, out _);
    }

    /// <summary>The calculation with size at <paramref name="px"/> pixels, as points plus a percentage.</summary>
    private static bool Sample(string calculation, float px, float em, float rem, float vw, float vh, out (float Pt, float Pct) result)
    {
        result = default;
        var text = "calc(" + ReplaceSize(calculation, "(" + px.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px)") + ")";
        if (!CssMath.TryParseLength(text, em, rem, vw, vh, out var len) || !len.IsLinear) return false;
        result = len.Unit switch
        {
            LengthUnit.Pt => (len.Value, 0),
            LengthUnit.Percent => (0, len.Value),
            LengthUnit.Calc => (len.Value, len.Pct),
            _ => (float.NaN, 0),
        };
        return float.IsFinite(result.Pt) && float.IsFinite(result.Pct);
    }

    private static bool HasSize(string s) => ReplaceSize(s, "") != s;

    /// <summary>Replaces the keyword size, as a whole word, with <paramref name="replacement"/>.</summary>
    private static string ReplaceSize(string s, string replacement)
    {
        var sb = new System.Text.StringBuilder(s.Length + 16);
        int i = 0;
        while (i < s.Length)
        {
            int k = s.IndexOf("size", i, StringComparison.Ordinal);
            if (k < 0) break;
            bool start = k == 0 || !IsNameChar(s[k - 1]);
            bool end = k + 4 == s.Length || !IsNameChar(s[k + 4]) && s[k + 4] != '(';
            sb.Append(s, i, k - i).Append(start && end ? replacement : "size");
            i = k + 4;
        }
        return sb.Append(s, i, s.Length - i).ToString();
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    private static int TopLevelComma(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }
}

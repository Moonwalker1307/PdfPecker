using System.Buffers;
using System.Globalization;
using System.Text;

namespace PdfPecker.Css;

/// <summary>
/// The font-relative units that need a font's metrics: <c>ex</c>, <c>ch</c>, <c>cap</c>, <c>ic</c> and <c>lh</c>, and
/// their root versions <c>rex</c>, <c>rch</c>, <c>rcap</c>, <c>ric</c> and <c>rlh</c>. The style resolver rewrites them
/// to points before a value is parsed, because the length parser has the font size but not the font.
/// </summary>
internal static class CssUnits
{
    // A value is rewritten at most this many times, so a long list of lengths cannot make the scan quadratic.
    private const int MaxRewrites = 1000;

    // The letters the metric units start with.
    private static readonly SearchValues<char> UnitInitials = SearchValues.Create("cCeEiIlLrR");

    /// <summary>Whether the unit takes the font's metrics to resolve.</summary>
    public static bool IsMetricUnit(ReadOnlySpan<char> unit)
    {
        if (unit.Length is < 2 or > 4) return false;
        if (unit[0] is 'r' or 'R') unit = unit.Slice(1);
        return unit.Equals("ex", StringComparison.OrdinalIgnoreCase) || unit.Equals("ch", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("cap", StringComparison.OrdinalIgnoreCase) || unit.Equals("ic", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("lh", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a value has a number with a metric unit outside strings and url().</summary>
    public static bool HasMetricUnit(string value)
    {
        // A metric unit follows a digit and starts with one of a few letters. Every declaration is checked, and most
        // values fail this test, which spares them the scan for numbers.
        var span = value.AsSpan();
        int digit = span.IndexOfAnyInRange('0', '9');
        if (digit < 0 || span.Slice(digit).IndexOfAny(UnitInitials) < 0) return false;
        int i = 0;
        while (NextNumber(value, ref i, out _, out _, out int unitStart, out int unitEnd))
            if (IsMetricUnit(value.AsSpan(unitStart, unitEnd - unitStart))) return true;
        return false;
    }

    /// <summary>
    /// Replaces every number with a metric unit by its length in points. <paramref name="pointsPerUnit"/> gets the unit
    /// in lower case and returns the points one unit is worth, or null to leave the number as it is.
    /// </summary>
    public static string Rewrite(string value, Func<string, float?> pointsPerUnit)
    {
        StringBuilder? sb = null;
        int copied = 0, i = 0, rewrites = 0;
        while (rewrites < MaxRewrites && NextNumber(value, ref i, out int start, out int numberEnd, out int unitStart, out int unitEnd))
        {
            if (!IsMetricUnit(value.AsSpan(unitStart, unitEnd - unitStart))) continue;
            if (!float.TryParse(value.AsSpan(start, numberEnd - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float n)
                || pointsPerUnit(value.Substring(unitStart, unitEnd - unitStart).ToLowerInvariant()) is not float pt) continue;
            float result = n * pt;
            if (!float.IsFinite(result)) continue;
            sb ??= new StringBuilder(value.Length + 8);
            sb.Append(value, copied, start - copied).Append(result.ToString("0.#####", CultureInfo.InvariantCulture)).Append("pt");
            copied = unitEnd;
            rewrites++;
        }
        if (sb == null) return value;
        return sb.Append(value, copied, value.Length - copied).ToString();
    }

    // Finds the next number followed by a unit: its start (with a sign), the end of the number, and the unit's extent.
    // Numbers inside strings and url() and the digits of identifiers (h1, --a2, #fff) are not numbers.
    private static bool NextNumber(string v, ref int i, out int start, out int numberEnd, out int unitStart, out int unitEnd)
    {
        start = numberEnd = unitStart = unitEnd = 0;
        while (i < v.Length)
        {
            char c = v[i];
            if (c is '"' or '\'')
            {
                int close = v.IndexOf(c, i + 1);
                i = close < 0 ? v.Length : close + 1;
                continue;
            }
            if ((c is 'u' or 'U') && string.Compare(v, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !IsIdentChar(v[i - 1])))
            {
                int close = v.IndexOf(')', i + 4);
                i = close < 0 ? v.Length : close + 1;
                continue;
            }
            bool digit = c is >= '0' and <= '9';
            if (!digit && !(c == '.' && i + 1 < v.Length && v[i + 1] is >= '0' and <= '9')) { i++; continue; }

            int s = i;
            if (s > 0 && v[s - 1] is '-' or '+') s--;
            bool boundary = s == 0 || !(IsIdentChar(v[s - 1]) || v[s - 1] is '.' or '#' or '\\');
            int j = i;
            while (j < v.Length && v[j] is >= '0' and <= '9') j++;
            if (j < v.Length && v[j] == '.') { j++; while (j < v.Length && v[j] is >= '0' and <= '9') j++; }
            if (j + 1 < v.Length && v[j] is 'e' or 'E')
            {
                int k = j + 1;
                if (v[k] is '+' or '-') k++;
                if (k < v.Length && v[k] is >= '0' and <= '9')
                {
                    while (k < v.Length && v[k] is >= '0' and <= '9') k++;
                    j = k;
                }
            }
            int u = j;
            while (u < v.Length && v[u] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) u++;
            i = Math.Max(u, i + 1);
            if (!boundary || u == j) continue;
            start = s; numberEnd = j; unitStart = j; unitEnd = u;
            return true;
        }
        return false;
    }

    private static bool IsIdentChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' || c > 0x7F;
}

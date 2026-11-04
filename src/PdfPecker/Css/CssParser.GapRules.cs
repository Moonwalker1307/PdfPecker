using System.Text;

namespace PdfPecker.Css;

/// <summary>The shorthands of the gap decoration properties (CSS Gaps 1).</summary>
internal static partial class CssParser
{
    /// <summary>
    /// Expands a gap decoration shorthand into its longhands; false when <paramref name="name"/> is not one. The
    /// rule-, column-rule- and row-rule- shorthands of a kind set the column longhands, the row longhands or both.
    /// </summary>
    private static bool ExpandGapRuleShorthand(string name, string value, LonghandWriter longhands)
    {
        switch (name)
        {
            case "column-rule":
            case "row-rule":
                ExpandGapRule(value, name, longhands);
                return true;
            case "rule":
                ExpandGapRule(value, "column-rule", longhands);
                ExpandGapRule(value, "row-rule", longhands);
                return true;
            case "rule-color":
            case "rule-style":
            case "rule-width":
            case "rule-break":
            case "rule-visibility-items":
                longhands.Add("column-" + name, value);
                longhands.Add("row-" + name, value);
                return true;
            case "rule-inset":
            case "rule-inset-cap":
            case "rule-inset-junction":
            case "rule-inset-start":
            case "rule-inset-end":
                ExpandRuleInset("column-" + name, value, longhands);
                ExpandRuleInset("row-" + name, value, longhands);
                return true;
            case "column-rule-inset":
            case "column-rule-inset-cap":
            case "column-rule-inset-junction":
            case "column-rule-inset-start":
            case "column-rule-inset-end":
            case "row-rule-inset":
            case "row-rule-inset-cap":
            case "row-rule-inset-junction":
            case "row-rule-inset-start":
            case "row-rule-inset-end":
                ExpandRuleInset(name, value, longhands);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// column-rule or row-rule: width, style and colour in any order, or a comma-separated list of them with repeat()
    /// notations, which becomes a list of the same shape in each longhand. One rule gives each longhand one value, as
    /// column-rule always did. A list with an entry that is not a rule sets nothing, as in Chrome, rather than the
    /// longhands that happen to accept their part of it.
    /// </summary>
    private static void ExpandGapRule(string value, string prefix, LonghandWriter longhands)
    {
        // The list splitter drops empty entries, which make the whole list invalid.
        if (HasEmptyEntry(value)) return;
        var entries = SplitTopLevel(value, ',');
        if (entries.Count <= 1 && !value.TrimStart().StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = LineParts.Parse(value, outline: false);
            longhands.Add(prefix + "-width", parts.Width);
            longhands.Add(prefix + "-style", parts.Style);
            longhands.Add(prefix + "-color", parts.Color);
            return;
        }
        if (!IsGapRuleList(entries)) return;
        StringBuilder width = new(), style = new(), color = new();
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (width.Length > 0)
            {
                width.Append(", ");
                style.Append(", ");
                color.Append(", ");
            }
            if (entry.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) && entry.EndsWith(')'))
            {
                var args = SplitTopLevel(entry.Substring(7, entry.Length - 8), ',');
                // The longhands check the count and the values; a repeat without rules is left for them to refuse.
                var count = args.Count > 0 ? args[0].Trim() : "";
                width.Append("repeat(").Append(count);
                style.Append("repeat(").Append(count);
                color.Append("repeat(").Append(count);
                for (int i = 1; i < args.Count; i++)
                {
                    var parts = LineParts.Parse(args[i], outline: false);
                    width.Append(", ").Append(parts.Width);
                    style.Append(", ").Append(parts.Style);
                    color.Append(", ").Append(parts.Color);
                }
                width.Append(')');
                style.Append(')');
                color.Append(')');
            }
            else
            {
                var parts = LineParts.Parse(entry, outline: false);
                width.Append(parts.Width);
                style.Append(parts.Style);
                color.Append(parts.Color);
            }
        }
        longhands.Add(prefix + "-width", width.ToString());
        longhands.Add(prefix + "-style", style.ToString());
        longhands.Add(prefix + "-color", color.ToString());
    }

    /// <summary>
    /// Whether every entry of a column-rule or row-rule list is a rule, or a repeat() of rules with a count: no empty
    /// entry, no repeat() inside a repeat(), and no rule with a second width, style or colour or a colour that does not
    /// parse. The longhands check the counts and the widths' units.
    /// </summary>
    private static bool IsGapRuleList(List<string> entries)
    {
        foreach (var raw in entries)
        {
            var entry = raw.Trim();
            if (!entry.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsGapRule(entry)) return false;
                continue;
            }
            if (!entry.EndsWith(')')) return false;
            var body = entry.Substring(7, entry.Length - 8);
            if (HasEmptyEntry(body)) return false;
            var args = SplitTopLevel(body, ',');
            if (args.Count < 2) return false;
            for (int i = 1; i < args.Count; i++)
                if (!IsGapRule(args[i].Trim())) return false;
        }
        return true;
    }

    /// <summary>Whether a comma-separated list has an empty entry: a leading, trailing or doubled comma, or nothing at all.</summary>
    private static bool HasEmptyEntry(string list)
    {
        int depth = 0;
        bool content = false;
        foreach (char c in list)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == ',' && depth == 0)
            {
                if (!content) return true;
                content = false;
            }
            else if (!char.IsWhiteSpace(c)) content = true;
        }
        return !content;
    }

    /// <summary>One rule of a list: a width, a style and a colour in any order, each at most once and one at least.</summary>
    private static bool IsGapRule(string entry)
    {
        var tokens = Tokens(entry);
        if (tokens.Count is 0 or > 3) return false;
        bool width = false, style = false, color = false;
        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();
            ref bool seen = ref color;
            if (IsBorderStyle(lower)) seen = ref style;
            else if (IsLineWidth(lower) || CssMath.IsMathFunction(lower)) seen = ref width;
            else if (!lower.Equals("currentcolor", StringComparison.Ordinal) && !CssColor.TryParse(token, 0, out _)) return false;
            if (seen) return false;
            seen = true;
        }
        return true;
    }

    /// <summary>
    /// The rule-inset shorthands of one direction (<paramref name="name"/> starts with column- or row-), which set the
    /// four longhands -cap-start, -cap-end, -junction-start and -junction-end:
    /// <c>-inset: cap-start cap-end? [/ junction-start junction-end?]?</c>, where missing junction values copy the cap
    /// values and a missing end copies its start; <c>-inset-cap</c> and <c>-inset-junction: start end?</c>; and
    /// <c>-inset-start</c> and <c>-inset-end: value</c>, which set the cap and the junction alike.
    /// </summary>
    private static void ExpandRuleInset(string name, string value, LonghandWriter longhands)
    {
        int at = name.IndexOf("-inset", StringComparison.Ordinal);
        string prefix = string.Concat(name.AsSpan(0, at + 6), "-"), which = name.Substring(at + 6).TrimStart('-');
        switch (which)
        {
            case "":
            {
                var halves = value.Split('/');
                if (halves.Length > 2) return;
                var cap = Tokens(halves[0]);
                var junction = halves.Length == 2 ? Tokens(halves[1]) : cap;
                if (cap.Count is < 1 or > 2 || junction.Count is < 1 or > 2) return;
                longhands.Add(prefix + "cap-start", cap[0]);
                longhands.Add(prefix + "cap-end", cap[^1]);
                longhands.Add(prefix + "junction-start", junction[0]);
                longhands.Add(prefix + "junction-end", junction[^1]);
                break;
            }
            case "cap":
            case "junction":
            {
                var values = Tokens(value);
                if (values.Count is < 1 or > 2) return;
                longhands.Add(prefix + which + "-start", values[0]);
                longhands.Add(prefix + which + "-end", values[^1]);
                break;
            }
            default:
            {
                // -start or -end: one value for the cap and the junction.
                var values = Tokens(value);
                if (values.Count != 1) return;
                longhands.Add(prefix + "cap-" + which, values[0]);
                longhands.Add(prefix + "junction-" + which, values[0]);
                break;
            }
        }
    }
}

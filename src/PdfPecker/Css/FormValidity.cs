using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// The constraint validation of form controls for :valid and :invalid, as Chrome sees a page nobody has typed into
/// (HTML, "Constraints"). A control is checked against required, type (email, url), pattern, min, max and step;
/// minlength and maxlength count only after the user edits the value, so they never fail here. Disabled and read-only
/// controls, hidden inputs, reset and plain buttons and controls in a datalist are no candidates and match neither.
/// A form or fieldset is invalid when a control in it is. Results are kept on the elements, so a radio group or a form
/// is looked at once however many rules ask.
/// </summary>
internal static class FormValidity
{
    /// <summary>Longest pattern attribute compiled; a longer one constrains nothing.</summary>
    private const int MaxPatternLength = 2048;

    /// <summary>Patterns kept compiled, at most.</summary>
    private const int MaxCachedPatterns = 128;

    /// <summary>
    /// The time a pattern that needs backtracking may take to match a value. Patterns that run without backtracking
    /// have no timeout: their time is linear in the value, and a timeout would make the answer depend on the machine's
    /// load (the first match also builds the matcher, which on a busy machine took longer than a short timeout).
    /// </summary>
    private static readonly TimeSpan BacktrackingTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>Backtracking patterns that may take the full timeout in one style pass before the rest are no longer matched.</summary>
    private const int MaxSlowPatterns = 8;

    // The compiled patterns are a cache that outlives a document, like a buffer pool. The slow patterns are counted for
    // one style pass, from Selector.BeginMatching, which resets them, and selector matching reaches this class through
    // static methods that know no conversion; so the count stays per thread rather than in the ConversionContext, which
    // names it among the limits it does not hold.
    [ThreadStatic] private static Dictionary<string, Regex?>? _patterns;
    [ThreadStatic] private static int _slowPatterns;

    /// <summary>Whether the element matches :valid (true), :invalid (false) or neither (null).</summary>
    public static bool? Of(Element e)
    {
        if (e.Validity == 0) e.Validity = Compute(e);
        return e.Validity switch { 1 => true, 2 => false, _ => null };
    }

    private static byte Compute(Element e)
    {
        switch (e.Name)
        {
            case "form":
            case "fieldset":
                return HasInvalidDescendant(e) ? (byte)2 : (byte)1;
            case "input":
            case "select":
            case "textarea":
            case "button":
                if (!IsCandidate(e)) return 3;
                return Satisfies(e) ? (byte)1 : (byte)2;
            default:
                return 3;
        }
    }

    /// <summary>Whether a control below the element is invalid; each element's answer is kept, so nested fieldsets cost one walk.</summary>
    private static bool HasInvalidDescendant(Element e)
    {
        if (e.SubtreeValidity != 0) return e.SubtreeValidity == 2;
        bool invalid = false;
        foreach (var c in e.Children)
        {
            if (c is not Element child) continue;
            if (child.Name is "input" or "select" or "textarea" or "button" && Of(child) == false || HasInvalidDescendant(child))
            {
                invalid = true;
                break;
            }
        }
        e.SubtreeValidity = invalid ? (byte)2 : (byte)1;
        return invalid;
    }

    private static bool IsCandidate(Element e)
    {
        if (Compound.IsDisabledControl(e)) return false;
        for (var x = e.Parent; x != null; x = x.Parent)
            if (x.Name == "datalist") return false;
        if (e.Name == "button") return ButtonType(e) == "submit";
        if (e.Name == "textarea") return !e.HasAttribute("readonly");
        if (e.Name != "input") return true;
        string type = InputType(e);
        if (type is "hidden" or "reset" or "button") return false;
        return !(e.HasAttribute("readonly") && TakesReadonly(type));
    }

    private static bool Satisfies(Element e)
    {
        switch (e.Name)
        {
            case "button": return true;
            case "textarea": return !e.HasAttribute("required") || e.InnerText().Length > 0;
            case "select": return SelectSatisfies(e);
        }
        string type = InputType(e);
        bool required = e.HasAttribute("required");
        switch (type)
        {
            case "submit" or "image" or "range" or "color": return true;
            case "checkbox": return !required || e.HasAttribute("checked");
            case "radio": return RadioGroupSatisfies(e);
            case "file": return !required;
        }
        string raw = e.GetAttribute("value") ?? "";
        if (type is "number" or "date" or "month" or "week" or "time" or "datetime-local")
        {
            double? value = ParseValue(type, raw);
            if (value == null) return !required; // the value sanitizes to the empty string
            return InRangeAndStep(e, type, value.Value);
        }
        string value1 = StripNewlines(raw);
        if (type is "email" or "url") value1 = value1.Trim(' ', '\t', '\n', '\f', '\r');
        if (value1.Length == 0) return !required;
        if (type == "email")
        {
            if (e.HasAttribute("multiple"))
            {
                foreach (var part in value1.Split(','))
                {
                    var address = part.Trim(' ', '\t', '\n', '\f', '\r');
                    if (!IsEmail(address) || !MatchesPattern(e, address)) return false;
                }
                return true;
            }
            if (!IsEmail(value1)) return false;
        }
        else if (type == "url" && !IsAbsoluteUrl(value1)) return false;
        return !TakesPattern(type) || MatchesPattern(e, value1);
    }

    /// <summary>
    /// A required select must have a selected option, and in a drop-down list (no multiple, size 1) that option must
    /// not be the placeholder: a first option child with an empty value.
    /// </summary>
    private static bool SelectSatisfies(Element select)
    {
        if (!select.HasAttribute("required")) return true;
        bool multiple = select.HasAttribute("multiple");
        int size = Invariant.TryParseInt(select.GetAttribute("size")?.Trim() ?? "", out int n) && n > 0 ? n : 1;
        Element? first = null, selected = null, firstEnabled = null;
        Element? placeholder = null;
        bool firstChild = true;
        foreach (var option in Options(select))
        {
            first ??= option;
            if (firstChild && ReferenceEquals(option.Parent, select) && OptionValue(option).Length == 0) placeholder = option;
            firstChild = false;
            if (option.HasAttribute("selected"))
            {
                // In a drop-down list the last selected option wins; in a list box any selected one will do.
                selected = option;
                if (multiple) break;
            }
            if (firstEnabled == null && !option.HasAttribute("disabled")) firstEnabled = option;
        }
        if (!ReferenceEquals(placeholder, first)) placeholder = null;
        if (multiple || size > 1) return selected != null;
        selected ??= firstEnabled;
        return selected != null && !ReferenceEquals(selected, placeholder);
    }

    /// <summary>The option elements of a select: its option children and those of its optgroup children, in order.</summary>
    private static IEnumerable<Element> Options(Element select)
    {
        foreach (var c in select.Children)
        {
            if (c is not Element child) continue;
            if (child.Name == "option") yield return child;
            else if (child.Name == "optgroup")
                foreach (var g in child.Children)
                    if (g is Element { Name: "option" } option) yield return option;
        }
    }

    private static string OptionValue(Element option)
        => option.GetAttribute("value") ?? string.Join(' ', option.InnerText().Split((char[])[' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A radio button is invalid when a button of its group is required and none is checked. The groups of a form are
    /// found in one walk, and every button of a group gets its answer.
    /// </summary>
    private static bool RadioGroupSatisfies(Element radio)
    {
        string? name = radio.GetAttribute("name");
        if (string.IsNullOrEmpty(name)) return !radio.HasAttribute("required") || radio.HasAttribute("checked");
        var owner = radio.Parent;
        while (owner != null && owner.Name != "form" && owner.Parent != null) owner = owner.Parent;
        if (owner == null) return true;
        var groups = new Dictionary<string, (List<Element> Members, bool Required, bool Checked)>(StringComparer.Ordinal);
        CollectRadios(owner, owner, groups);
        foreach (var (_, group) in groups)
        {
            byte validity = !group.Required || group.Checked ? (byte)1 : (byte)2;
            foreach (var member in group.Members)
                if (member.Validity == 0 && IsCandidate(member)) member.Validity = validity;
        }
        return groups.TryGetValue(name, out var mine) && (!mine.Required || mine.Checked);
    }

    private static void CollectRadios(Element owner, Element e, Dictionary<string, (List<Element> Members, bool Required, bool Checked)> groups)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element child) continue;
            // A nested form belongs to itself (the parser does not make them, but a script-free DOM can still hold one).
            if (child.Name == "form" && !ReferenceEquals(child, owner)) continue;
            if (child.Name == "input" && InputType(child) == "radio" && child.GetAttribute("name") is { Length: > 0 } name)
            {
                if (!groups.TryGetValue(name, out var g)) g = (new List<Element>(), false, false);
                g.Members.Add(child);
                groups[name] = (g.Members, g.Required || child.HasAttribute("required"), g.Checked || child.HasAttribute("checked"));
            }
            CollectRadios(owner, child, groups);
        }
    }

    /// <summary>
    /// Whether a number, date or time value lies within min and max and on a step from min. Without a min the step
    /// base is the value attribute itself, so the step cannot fail. A time range with max before min wraps midnight.
    /// </summary>
    private static bool InRangeAndStep(Element e, string type, double value)
    {
        double? min = ParseValue(type, e.GetAttribute("min") ?? "");
        double? max = ParseValue(type, e.GetAttribute("max") ?? "");
        if (type == "time" && min is { } lo && max is { } hi && hi < lo)
        {
            if (value < lo && value > hi) return false;
        }
        else if (value < min || value > max) return false;
        if (min is not { } baseValue) return true;
        string? step = e.GetAttribute("step")?.Trim();
        if (step != null && step.Equals("any", StringComparison.OrdinalIgnoreCase)) return true;
        var (scale, defaultStep) = StepUnits(type);
        double stepSize = (step != null && ParseFloat(step) is { } s && s > 0 ? s : defaultStep) * scale;
        if (type == "month") stepSize = Math.Max(1, Math.Round(stepSize));
        double steps = (value - baseValue) / stepSize;
        return Math.Abs(steps - Math.Round(steps)) <= 1e-9 * Math.Max(1, Math.Abs(steps));
    }

    /// <summary>The unit a step counts in, in the type's value units, and the step when none is given (HTML's step scale factor and default step).</summary>
    private static (double Scale, double Default) StepUnits(string type) => type switch
    {
        "date" => (86_400_000, 1),
        "week" => (604_800_000, 1),
        "month" => (1, 1),
        "time" or "datetime-local" => (1000, 60),
        _ => (1, 1),
    };

    /// <summary>
    /// A value as a number: the number itself, milliseconds since 1970 for dates, weeks, times and local date-times,
    /// and months since January 1970 for a month; null when the string is not a valid value of the type.
    /// </summary>
    internal static double? ParseValue(string type, string s)
    {
        if (s.Length == 0) return null;
        switch (type)
        {
            case "number": return ParseFloat(s);
            case "date": return ParseDate(s, out var date) && s.Length == 10 ? Milliseconds(date) : null;
            case "month":
            {
                if (s.Length != 7 || s[4] != '-' || !Digits(s, 0, 4, out int y) || !Digits(s, 5, 2, out int m) || y < 1 || m is < 1 or > 12) return null;
                return (y - 1970) * 12.0 + m - 1;
            }
            case "week":
            {
                if (s.Length != 8 || s[4] != '-' || s[5] != 'W' || !Digits(s, 0, 4, out int y) || !Digits(s, 6, 2, out int w) || y < 1 || w < 1) return null;
                if (w > ISOWeek.GetWeeksInYear(y)) return null;
                return Milliseconds(ISOWeek.ToDateTime(y, w, DayOfWeek.Monday));
            }
            case "time": return ParseTime(s, 0, s.Length);
            case "datetime-local":
            {
                int t = s.IndexOfAny(['T', ' ']);
                if (t != 10 || !ParseDate(s, out var day) || ParseTime(s, 11, s.Length - 11) is not { } ms) return null;
                return Milliseconds(day) + ms;
            }
            default: return null;
        }
    }

    private static bool ParseDate(string s, out DateTime date)
    {
        date = default;
        if (s.Length < 10 || s[4] != '-' || s[7] != '-' || !Digits(s, 0, 4, out int y) || !Digits(s, 5, 2, out int m) || !Digits(s, 8, 2, out int d)) return false;
        if (y < 1 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }

    /// <summary>HH:MM, HH:MM:SS or HH:MM:SS.fff in milliseconds after midnight, from <paramref name="length"/> characters at <paramref name="start"/>.</summary>
    private static double? ParseTime(string s, int start, int length)
    {
        if (length < 5 || s[start + 2] != ':' || !Digits(s, start, 2, out int h) || !Digits(s, start + 3, 2, out int m) || h > 23 || m > 59) return null;
        double ms = (h * 60 + m) * 60_000.0;
        if (length == 5) return ms;
        if (length < 8 || s[start + 5] != ':' || !Digits(s, start + 6, 2, out int sec) || sec > 59) return null;
        ms += sec * 1000;
        if (length == 8) return ms;
        if (length < 10 || length > 12 || s[start + 8] != '.' || !Digits(s, start + 9, length - 9, out int fraction)) return null;
        return ms + fraction * Math.Pow(10, 3 - (length - 9));
    }

    private static double Milliseconds(DateTime date) => (date - DateTime.UnixEpoch).TotalMilliseconds;

    private static bool Digits(string s, int start, int count, out int value)
    {
        value = 0;
        if (start + count > s.Length) return false;
        for (int i = start; i < start + count; i++)
        {
            if (s[i] is < '0' or > '9') return false;
            value = value * 10 + (s[i] - '0');
        }
        return true;
    }

    /// <summary>A valid floating-point number of HTML: an optional minus, digits with an optional fraction (or only a fraction), an optional exponent.</summary>
    private static double? ParseFloat(string s)
    {
        int i = 0, n = s.Length;
        if (i < n && s[i] == '-') i++;
        int intStart = i;
        while (i < n && char.IsAsciiDigit(s[i])) i++;
        bool intDigits = i > intStart;
        if (i < n && s[i] == '.')
        {
            int fracStart = ++i;
            while (i < n && char.IsAsciiDigit(s[i])) i++;
            if (i == fracStart) return null;
        }
        else if (!intDigits) return null;
        if (i < n && s[i] is 'e' or 'E')
        {
            i++;
            if (i < n && s[i] is '+' or '-') i++;
            int expStart = i;
            while (i < n && char.IsAsciiDigit(s[i])) i++;
            if (i == expStart) return null;
        }
        if (i != n || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return null;
        return v;
    }

    /// <summary>HTML's valid email address: a local part of allowed characters, "@", and dot-separated labels of at most 63 letters, digits and inner hyphens.</summary>
    private static bool IsEmail(string s)
    {
        int at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        for (int i = 0; i < at; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || ".!#$%&'*+/=?^_`{|}~-".Contains(s[i]))) return false;
        foreach (var label in s.AsSpan(at + 1).ToString().Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (char c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }
        return true;
    }

    /// <summary>An absolute URL: a scheme, and for the schemes with hosts, a host after "//".</summary>
    private static bool IsAbsoluteUrl(string s)
    {
        int colon = s.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(s[0])) return false;
        for (int i = 1; i < colon; i++)
            if (!(char.IsAsciiLetterOrDigit(s[i]) || s[i] is '+' or '-' or '.')) return false;
        var scheme = s.Substring(0, colon).ToLowerInvariant();
        if (scheme is "http" or "https" or "ftp" or "ws" or "wss")
            return Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
        return true;
    }

    /// <summary>Whether the value matches the pattern attribute as a whole; a pattern that does not compile constrains nothing.</summary>
    private static bool MatchesPattern(Element e, string value)
    {
        if (e.GetAttribute("pattern") is not { } pattern || Pattern(pattern) is not { } regex) return true;
        if (_slowPatterns >= MaxSlowPatterns) return true;
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            _slowPatterns++;
            return true;
        }
    }

    /// <summary>
    /// The compiled pattern, anchored at both ends as browsers anchor it. It runs without backtracking, in time linear
    /// in the value; a pattern that needs backtracking (back references, lookarounds) gets a short timeout instead.
    /// </summary>
    private static Regex? Pattern(string pattern)
    {
        if (pattern.Length > MaxPatternLength) return null;
        _patterns ??= new Dictionary<string, Regex?>(StringComparer.Ordinal);
        if (_patterns.TryGetValue(pattern, out var cached)) return cached;
        if (_patterns.Count >= MaxCachedPatterns) _patterns.Clear();
        Regex? regex;
        string anchored = "^(?:" + pattern + @")\z";
        try
        {
            regex = new Regex(anchored, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException)
        {
            try { regex = new Regex(anchored, RegexOptions.CultureInvariant, BacktrackingTimeout); }
            catch (ArgumentException) { regex = null; }
        }
        catch (ArgumentException)
        {
            regex = null;
        }
        _patterns[pattern] = regex;
        return regex;
    }

    /// <summary>Forgets the slow patterns counted for the previous document.</summary>
    internal static void Reset() => _slowPatterns = 0;

    private static string StripNewlines(string s) => s.IndexOfAny(['\n', '\r']) < 0 ? s : s.Replace("\n", "").Replace("\r", "");

    private static bool TakesReadonly(string type)
        => type is "text" or "search" or "url" or "tel" or "email" or "password" or "date" or "month" or "week" or "time" or "datetime-local" or "number";

    private static bool TakesPattern(string type) => type is "text" or "search" or "url" or "tel" or "email" or "password";

    /// <summary>The input's type, with an unknown one as text, as browsers treat it.</summary>
    private static string InputType(Element e)
    {
        string type = (e.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
        return type is "text" or "search" or "url" or "tel" or "email" or "password" or "date" or "month" or "week" or "time"
            or "datetime-local" or "number" or "range" or "color" or "checkbox" or "radio" or "file" or "submit" or "image"
            or "reset" or "button" or "hidden" ? type : "text";
    }

    private static string ButtonType(Element e)
    {
        string type = (e.GetAttribute("type") ?? "submit").Trim().ToLowerInvariant();
        return type is "reset" or "button" ? type : "submit";
    }
}

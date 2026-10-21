using System.Globalization;
using System.Text;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// Evaluates the content property of ::before, ::after and margin boxes, and the values of string-set: strings,
/// attr(), counter(), counters(), quotes, and the paged-media functions string(), content(), target-counter(),
/// target-text() and leader().
/// </summary>
internal static class GeneratedContent
{
    // The quotes quotes: auto uses for a language, as Chrome does (from CLDR): outer open and close, then inner open and close.
    private static readonly string[] English = { "“", "”", "‘", "’" };
    private static readonly Dictionary<string, string[]> LanguageQuotes = new(StringComparer.Ordinal)
    {
        ["de"] = new[] { "„", "“", "‚", "‘" },
        ["cs"] = new[] { "„", "“", "‚", "‘" },
        ["sk"] = new[] { "„", "“", "‚", "‘" },
        ["sl"] = new[] { "„", "“", "‚", "‘" },
        ["hr"] = new[] { "„", "“", "‚", "‘" },
        ["et"] = new[] { "„", "“", "‚", "‘" },
        ["sr"] = new[] { "„", "“", "‘", "‘" },
        ["bg"] = new[] { "„", "“", "„", "“" },
        ["lt"] = new[] { "„", "“", "„", "“" },
        ["fr"] = new[] { "«", "»", "«", "»" },
        ["fr-ch"] = new[] { "«", "»", "‹", "›" },
        ["fr-ca"] = new[] { "«", "»", "”", "“" },
        ["it"] = new[] { "«", "»", "“", "”" },
        ["el"] = new[] { "«", "»", "“", "”" },
        ["ca"] = new[] { "«", "»", "“", "”" },
        ["nb"] = new[] { "«", "»", "‘", "’" },
        ["nn"] = new[] { "«", "»", "‘", "’" },
        ["no"] = new[] { "«", "»", "‘", "’" },
        ["ru"] = new[] { "«", "»", "„", "“" },
        ["uk"] = new[] { "«", "»", "„", "“" },
        ["nl"] = new[] { "‘", "’", "‘", "’" },
        ["sv"] = new[] { "”", "”", "’", "’" },
        ["fi"] = new[] { "”", "”", "’", "’" },
        ["he"] = new[] { "”", "”", "’", "’" },
        ["pl"] = new[] { "„", "”", "«", "»" },
        ["hu"] = new[] { "„", "”", "»", "«" },
        ["ro"] = new[] { "„", "”", "«", "»" },
        ["ja"] = new[] { "「", "」", "『", "』" },
        ["zh-hant"] = new[] { "「", "」", "『", "』" },
        ["ar"] = new[] { "”", "“", "’", "‘" },
    };

    /// <summary>The quotes of a language tag: the longest prefix of the tag that has its own, else English.</summary>
    private static string[] QuotesFor(string? lang)
    {
        if (string.IsNullOrEmpty(lang)) return English;
        var tag = lang.Trim().ToLowerInvariant().Replace('_', '-');
        while (tag.Length > 0)
        {
            if (LanguageQuotes.TryGetValue(tag, out var found)) return found;
            int dash = tag.LastIndexOf('-');
            if (dash < 0) break;
            tag = tag.Substring(0, dash);
        }
        return English;
    }

    // The string of an open-quote or close-quote at a nesting depth; the last pair serves the deeper levels.
    private static string Quote(ComputedStyle? style, int depth, bool open)
    {
        var quotes = style?.Quotes;
        if (quotes == null)
        {
            var auto = QuotesFor(style?.Lang);
            return auto[(Math.Min(depth, 1) << 1) + (open ? 0 : 1)];
        }
        if (quotes.Length == 0) return "";
        int pair = Math.Min(depth, quotes.Length / 2 - 1);
        return quotes[(pair << 1) + (open ? 0 : 1)];
    }

    // Generated text far beyond any real use (a counters() over runaway nesting) is cut here.
    private const int MaxLength = 10_000;

    /// <summary>
    /// How many characters of content values one document may evaluate and generate in all. Each value is evaluated for
    /// every element it applies to, so a long string in one rule would otherwise be copied onto thousands of elements:
    /// 240 KB of HTML generated 200 million characters, which took gigabytes and minutes and ignored cancellation while
    /// it was laid out. Real documents stay far below this; past it, generated content is left empty.
    /// </summary>
    internal const int MaxDocumentLength = 4_000_000;

    /// <summary>
    /// The longest string a list marker, quote, text-overflow or hyphenate-character may be. Each is drawn for every
    /// element or line it applies to, so it is cut here rather than copied in full onto each of them.
    /// </summary>
    internal const int MaxMarkLength = 64;

    /// <summary>The text to show, or null when the value is invalid or generates no box.</summary>
    public static string? Evaluate(string value, Element host, StyleResolver resolver, ComputedStyle? style = null)
    {
        if (resolver.GeneratedBudget <= 0) return "";
        resolver.GeneratedBudget -= value.Length;
        var text = EvaluateCore(value, host, resolver, style);
        if (text == null) return null;
        if (text.Length > resolver.GeneratedBudget) text = Cut(text, Math.Max(0, resolver.GeneratedBudget));
        resolver.GeneratedBudget -= text.Length;
        return text;
    }

    private static string? EvaluateCore(string value, Element host, StyleResolver resolver, ComputedStyle? style)
    {
        style ??= host.Style;
        var sb = new StringBuilder();
        int i = 0, n = value.Length;
        while (i < n)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '"' or '\'')
            {
                if (!ReadString(value, ref i, sb)) return null;
                continue;
            }
            int start = i;
            while (i < n && (char.IsLetterOrDigit(value[i]) || value[i] is '-' or '_')) i++;
            var name = value.Substring(start, i - start).ToLowerInvariant();
            if (name.Length == 0) return null;
            if (i < n && value[i] == '(')
            {
                int close = FindClose(value, i);
                if (close < 0) return null;
                var args = SplitArgs(value.Substring(i + 1, close - i - 1));
                i = close + 1;
                if (!Function(name, args, host, resolver, sb)) return null;
                continue;
            }
            switch (name)
            {
                case "open-quote":
                    sb.Append(Quote(style, resolver.QuoteDepth, open: true));
                    resolver.QuoteDepth++;
                    break;
                case "close-quote":
                    if (resolver.QuoteDepth > 0) resolver.QuoteDepth--;
                    sb.Append(Quote(style, resolver.QuoteDepth, open: false));
                    break;
                case "no-open-quote": resolver.QuoteDepth++; break;
                case "no-close-quote": if (resolver.QuoteDepth > 0) resolver.QuoteDepth--; break;
                default: return null;
            }
            if (sb.Length > MaxLength) break;
        }
        return sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
    }

    private static bool Function(string name, List<string> args, Element host, StyleResolver resolver, StringBuilder sb)
    {
        switch (name)
        {
            case "attr":
                if (args.Count == 0) return false;
                sb.Append(host.GetAttribute(args[0].Split(' ')[0].ToLowerInvariant()) ?? "");
                return true;
            case "counter":
            {
                if (args.Count is < 1 or > 2) return false;
                var values = resolver.CounterValues(args[0]);
                if (!TryStyle(resolver, args.Count == 2 ? args[1] : "decimal", out var counterStyle)) return false;
                sb.Append(Format(counterStyle, values.Count > 0 ? values[^1] : 0));
                return true;
            }
            case "counters":
            {
                if (args.Count is < 2 or > 3) return false;
                // The separator must be a quoted string; "counters(item, )" or an unquoted word drops the declaration.
                if (args[1].Length == 0 || args[1][0] is not ('"' or '\'')) return false;
                var sep = new StringBuilder();
                int k = 0;
                if (!ReadString(args[1], ref k, sep)) return false;
                var values = resolver.CounterValues(args[0]);
                if (!TryStyle(resolver, args.Count == 3 ? args[2] : "decimal", out var countersStyle)) return false;
                if (values.Count == 0) sb.Append(Format(countersStyle, 0));
                for (int v = 0; v < values.Count; v++)
                {
                    if (v > 0) sb.Append(sep);
                    sb.Append(Format(countersStyle, values[v]));
                }
                return true;
            }
            case "string":
            {
                // A running string, in a margin box: string(name[, first | start | last | first-except]).
                if (args.Count is < 1 or > 2) return false;
                var paged = resolver.Paged;
                if (paged.Running != null)
                    sb.Append(paged.Running.Get(args[0].Trim(), paged.CurrentPage, args.Count == 2 ? args[1].Trim().ToLowerInvariant() : "first"));
                return true;
            }
            case "content":
            {
                // The element's own text (string-set): content(), content(text), content(before | after | first-letter).
                var which = args.Count > 0 ? args[0].Trim().ToLowerInvariant() : "text";
                sb.Append(TextOf(host, which));
                return true;
            }
            case "target-counter":
            {
                // target-counter(attr(href), page[, style]): a counter's value at the element a link points to.
                if (args.Count is < 2 or > 3) return false;
                var paged = resolver.Paged;
                paged.UsesTargets = true;
                var id = TargetId(args[0], host);
                var counter = args[1].Trim();
                if (!TryStyle(resolver, args.Count == 3 ? args[2] : "decimal", out var targetStyle)) return false;
                int value = 0;
                if (id != null)
                {
                    if (counter == "page") value = paged.PageOfId?.Invoke(id) ?? 0;
                    else
                    {
                        paged.WantedCounters.Add(counter);
                        if (paged.CounterSnapshots.TryGetValue(id, out var values) && values.TryGetValue(counter, out int v)) value = v;
                    }
                }
                sb.Append(Format(targetStyle, value));
                return true;
            }
            case "target-text":
            {
                // target-text(attr(href)[, content | before | after | first-letter]): the text of the element linked to.
                if (args.Count is < 1 or > 2) return false;
                resolver.Paged.UsesTargets = true;
                var id = TargetId(args[0], host);
                var target = id != null ? resolver.ElementById(host, id) : null;
                if (target != null)
                {
                    var which = args.Count == 2 ? args[1].Trim().ToLowerInvariant() : "content";
                    sb.Append(TextOf(target, which == "content" ? "text" : which));
                }
                return true;
            }
            case "leader":
            {
                // leader(dotted | solid | space | "string"): fills the rest of the line; laid out by the inline layout.
                if (args.Count != 1) return false;
                var a = args[0].Trim();
                string? pattern = a.ToLowerInvariant() switch
                {
                    "dotted" => ". ",
                    "solid" => "_",
                    "space" => " ",
                    _ => a.Length > 0 && a[0] is '"' or '\'' ? UnquoteString(a) : null,
                };
                if (string.IsNullOrEmpty(pattern)) return false;
                sb.Append(LeaderStart).Append(pattern.Replace(LeaderStart, ' ').Replace(LeaderEnd, ' ')).Append(LeaderEnd);
                return true;
            }
            case "url":
            case "image-set":
            case "linear-gradient":
            case "radial-gradient":
                return true; // images are not drawn as generated content
            default:
                return false;
        }
    }

    /// <summary>The counter style a counter function names, with none as a style that writes nothing.</summary>
    private static bool TryStyle(StyleResolver resolver, string name, out CounterStyle? style)
    {
        style = null;
        var n = name.Trim();
        if (n.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (!resolver.CounterStyles.TryGet(n, out var found)) return false;
        style = found;
        return true;
    }

    private static string Format(CounterStyle? style, int value) => style?.Represent(value) ?? "";

    /// <summary>
    /// Marks a leader in generated text: LeaderStart, the pattern, LeaderEnd. These are interlinear annotation
    /// characters, which never occur in documents; the inline layout turns the run into a leader.
    /// </summary>
    internal const char LeaderStart = '\uFFF9', LeaderEnd = '\uFFFB';

    /// <summary>The id a target function points to: attr(href) or a "#id" string or url(#id); null when not a fragment.</summary>
    private static string? TargetId(string arg, Element host)
    {
        var a = arg.Trim();
        string? url;
        if (a.StartsWith("attr(", StringComparison.OrdinalIgnoreCase) && a.EndsWith(')'))
            url = host.GetAttribute(a.Substring(5, a.Length - 6).Trim().Split(' ')[0].ToLowerInvariant());
        else if (a.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && a.EndsWith(')'))
        {
            var inner = a.Substring(4, a.Length - 5).Trim();
            url = UnquoteString(inner) ?? inner;
        }
        else url = UnquoteString(a);
        if (url == null) return null;
        url = url.Trim();
        if (!url.StartsWith('#') || url.Length < 2) return null;
        var id = url.Substring(1);
        try { return Uri.UnescapeDataString(id); }
        catch (UriFormatException) { return id; }
    }

    /// <summary>An element's text for content() and target-text(): its own text, its ::before or ::after, or its first letter.</summary>
    internal static string TextOf(Element e, string which)
    {
        switch (which)
        {
            case "before":
            case "after":
            {
                var name = which == "before" ? "::before" : "::after";
                foreach (var c in e.Children)
                    if (c is Element { Generated: true } g && g.Name == name) return Collapse(g.InnerText());
                return "";
            }
            case "first-letter":
            {
                var text = OwnText(e);
                return text.Length == 0 ? "" : char.IsSurrogatePair(text, 0) ? text.Substring(0, 2) : text.Substring(0, 1);
            }
            default:
                return OwnText(e);
        }
    }

    /// <summary>The element's text without its generated content, whitespace collapsed.</summary>
    private static string OwnText(Element e)
    {
        var sb = new StringBuilder();
        Append(e, sb);
        return Collapse(sb.ToString());

        static void Append(Element e, StringBuilder sb)
        {
            foreach (var c in e.Children)
            {
                if (c is TextNode t) sb.Append(t.Text);
                else if (c is Element { Generated: false } ce) Append(ce, sb);
            }
        }
    }

    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The text of a whole quoted CSS string, with escapes decoded.</summary>
    internal static string? UnquoteString(string v)
    {
        int i = 0;
        var sb = new StringBuilder();
        return v.Length >= 2 && v[0] is '"' or '\'' && ReadString(v, ref i, sb) && i == v.Length ? sb.ToString() : null;
    }

    /// <summary><see cref="UnquoteString"/> cut to <see cref="MaxMarkLength"/>, for strings drawn once per element or line.</summary>
    internal static string? UnquoteMark(string v) => UnquoteString(v) is { } s ? Cut(s, MaxMarkLength) : null;

    /// <summary><paramref name="s"/> cut to at most <paramref name="length"/> characters without splitting a surrogate pair.</summary>
    internal static string Cut(string s, int length)
    {
        if (s.Length <= length) return s;
        if (length > 0 && char.IsHighSurrogate(s[length - 1])) length--;
        return s.Substring(0, length);
    }

    /// <summary>Reads a quoted string with CSS escapes (\" \\ \A \201C) at <paramref name="i"/>.</summary>
    private static bool ReadString(string v, ref int i, StringBuilder sb)
    {
        char quote = v[i++];
        while (i < v.Length)
        {
            char c = v[i];
            if (c == quote) { i++; return true; }
            if (c == '\\' && i + 1 < v.Length)
            {
                i++;
                if (Uri.IsHexDigit(v[i]))
                {
                    int start = i;
                    while (i < v.Length && i - start < 6 && Uri.IsHexDigit(v[i])) i++;
                    int code = int.Parse(v.AsSpan(start, i - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (i < v.Length && v[i] == ' ') i++; // one space ends the escape
                    if (code == 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF) code = 0xFFFD;
                    sb.Append(char.ConvertFromUtf32(code));
                    continue;
                }
                if (v[i] is '\n' or '\r') { i++; continue; } // an escaped newline continues the string
                sb.Append(v[i++]);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return false;
    }

    private static int FindClose(string v, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = open; i < v.Length; i++)
        {
            char c = v[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<string> SplitArgs(string s)
    {
        var list = new List<string>();
        int start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == ',') { list.Add(s.Substring(start, i - start).Trim()); start = i + 1; }
        }
        var last = s.Substring(start).Trim();
        if (last.Length > 0 || list.Count > 0) list.Add(last);
        return list;
    }
}

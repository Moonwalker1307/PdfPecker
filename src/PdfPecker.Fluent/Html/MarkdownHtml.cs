using System.Text;

namespace PdfPecker.Fluent.Html;

/// <summary>
/// Writes the HTML that PdfPecker's Markdown converter makes of a <c>Markdown</c> call. Raw HTML in the Markdown is
/// already escaped by the converter, so every <c>&lt;</c> in its output starts a tag of its own and every attribute
/// value is quoted and escaped. That lets this class read the tags safely: it drops links that are not web, mail or
/// in-document addresses, and gives ids a later Markdown call repeats (a heading, a footnote) new names, so each
/// call's footnotes and heading links lead to its own text.
/// </summary>
internal static class MarkdownHtml
{
    /// <summary>The class of the element around each call's HTML, which the rules in <see cref="Css"/> are scoped to.</summary>
    public const string ClassName = "md";

    // The look of PdfPecker's Markdown stylesheet, scoped to the Markdown and reset from the fluent base rules (which
    // keep line breaks in paragraphs and give tables fixed columns). The font, its size and colour come from the
    // surrounding text style, so headings and code scale with it.
    public const string Css =
        ".md{line-height:1.5;white-space:normal}" +
        ".md p,.md h1,.md h2,.md h3,.md h4,.md h5,.md h6{white-space:normal}" +
        ".md h1,.md h2,.md h3,.md h4,.md h5,.md h6{margin:1.4em 0 .6em;line-height:1.25;font-weight:bold;page-break-after:avoid}" +
        ".md h1{font-size:2em;padding-bottom:.25em;border-bottom:1px solid #d1d9e0}" +
        ".md h2{font-size:1.5em;padding-bottom:.25em;border-bottom:1px solid #d1d9e0}" +
        ".md h3{font-size:1.25em}.md h4{font-size:1em}.md h5{font-size:.875em}.md h6{font-size:.85em;color:#59636e}" +
        ".md p,.md blockquote,.md ul,.md ol,.md dl,.md table,.md pre{margin:0 0 1em}" +
        ".md>:first-child{margin-top:0}.md>:last-child{margin-bottom:0}" +
        ".md a{color:#0969da}.md strong{font-weight:bold}.md del{color:#59636e}" +
        ".md ul,.md ol{padding-left:2em}" +
        ".md ul ul,.md ul ol,.md ol ol,.md ol ul{margin:0}" +
        ".md li+li{margin-top:.25em}.md li>p{margin:.5em 0}" +
        ".md ul.contains-task-list{list-style-type:none;padding-left:1.2em}" +
        ".md input[type=checkbox]{display:inline-block;width:.75em;height:.75em;border:1px solid #59636e;margin:0 .4em 0 0}" +
        ".md input[checked]{background-color:#0969da;border-color:#0969da}" +
        ".md blockquote{margin-left:0;margin-right:0;padding:0 1em;color:#59636e;border-left:.25em solid #d1d9e0}" +
        ".md code{font-family:Courier,monospace;font-size:.9em;background-color:#eff1f3}" +
        ".md pre{font-family:Courier,monospace;font-size:.85em;line-height:1.45;background-color:#f6f8fa;padding:10px 12px;white-space:pre-wrap}" +
        ".md pre code{font-size:1em;background-color:transparent}" +
        ".md hr{border:0;border-top:2px solid #d1d9e0;margin:1.5em 0}" +
        ".md table{width:auto;table-layout:auto;border-collapse:collapse}" +
        ".md th,.md td{border:1px solid #d1d9e0;padding:4px 10px;text-align:start}" +
        ".md th{font-weight:bold;background-color:#f6f8fa}" +
        ".md tr:nth-child(even) td{background-color:#f6f8fa}" +
        ".md [align=left]{text-align:left}.md [align=center]{text-align:center}.md [align=right]{text-align:right}" +
        ".md img{display:inline;max-width:100%}" +
        ".md dt{font-weight:bold;margin-top:.75em}.md dt:first-child{margin-top:0}" +
        ".md dd{margin:0 0 0 1.5em}.md dd>p{margin:.5em 0}" +
        ".md sup.footnote-ref{font-size:.75em}" +
        ".md section.footnotes{font-size:.875em;color:#59636e;border-top:1px solid #d1d9e0;margin-top:2em;padding-top:.75em}" +
        ".md section.footnotes li>p{margin:.25em 0}";

    /// <summary>
    /// Appends the converter's HTML, wrapped in an element of class <see cref="ClassName"/>. <paramref name="ids"/>
    /// holds the ids the Markdown written before has taken, and takes this call's. <paramref name="band"/> is true in a
    /// header or footer.
    /// </summary>
    public static void Write(StringBuilder sb, string html, HashSet<string> ids, bool band = false)
    {
        int begin = sb.Length;
        WriteHtml(sb, html, ids);
        if (band) SplitBraces(sb, begin);
    }

    /// <summary>
    /// Puts an empty element after each brace in the text from <paramref name="begin"/> on, as the writer does for text
    /// in a header or footer: PdfPecker replaces page-number tokens such as {page} only within one text node, so the
    /// caller's braces stay as written.
    /// </summary>
    private static void SplitBraces(StringBuilder sb, int begin)
    {
        var html = sb.ToString(begin, sb.Length - begin);
        if (!html.Contains('{', StringComparison.Ordinal)) return;
        sb.Length = begin;
        bool inTag = false, quoted = false;
        foreach (char c in html)
        {
            sb.Append(c);
            if (inTag)
            {
                if (c == '"') quoted = !quoted;
                else if (c == '>' && !quoted) inTag = false;
            }
            else if (c == '<') inTag = true;
            else if (c == '{') sb.Append("<span></span>");
        }
    }

    private static void WriteHtml(StringBuilder sb, string html, HashSet<string> ids)
    {
        Dictionary<string, string>? renamed = null;
        for (int i = html.IndexOf('<'); i >= 0; i = html.IndexOf('<', i + 1))
        {
            foreach (var (name, value) in Attributes(html, i))
            {
                if (name != "id" || ids.Add(value)) continue;
                renamed ??= new(StringComparer.Ordinal);
                if (renamed.ContainsKey(value)) continue;
                int n = 2;
                string unique;
                do unique = value + "-" + n++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                while (!ids.Add(unique));
                renamed.Add(value, unique);
            }
        }

        sb.Append("<div class=\"").Append(ClassName).Append("\">");
        int copied = 0;
        for (int i = html.IndexOf('<'); i >= 0; i = html.IndexOf('<', i + 1))
        {
            foreach (var (name, value, start, end) in AttributeSpans(html, i))
            {
                string? replacement = name switch
                {
                    "id" when renamed != null && renamed.TryGetValue(value, out var id) => " id=\"" + id + "\"",
                    "href" when value.StartsWith('#') => renamed != null && renamed.TryGetValue(value[1..], out var target)
                        ? " href=\"#" + target + "\""
                        : null,
                    "href" when !Links.IsSafe(Unescape(value)) => "",
                    _ => null,
                };
                if (replacement == null) continue;
                sb.Append(html, copied, start - copied).Append(replacement);
                copied = end;
            }
        }
        sb.Append(html, copied, html.Length - copied).Append("</div>");
    }

    private static IEnumerable<(string Name, string Value)> Attributes(string html, int tag)
    {
        foreach (var (name, value, _, _) in AttributeSpans(html, tag)) yield return (name, value);
    }

    /// <summary>
    /// The attributes of the tag that starts at <paramref name="tag"/>, each with the span from the space before its
    /// name to the end of its value. The converter writes every value in double quotes, with quotes in it escaped.
    /// </summary>
    private static List<(string Name, string Value, int Start, int End)> AttributeSpans(string html, int tag)
    {
        var attributes = new List<(string, string, int, int)>();
        int i = tag + 1;
        while (i < html.Length && html[i] is not (' ' or '>')) i++;
        while (i < html.Length && html[i] == ' ')
        {
            int start = i++;
            int nameStart = i;
            while (i < html.Length && html[i] is not ('=' or ' ' or '>' or '/')) i++;
            string name = html[nameStart..i];
            if (i + 1 < html.Length && html[i] == '=' && html[i + 1] == '"')
            {
                int close = html.IndexOf('"', i + 2);
                if (close < 0) break;
                attributes.Add((name, html[(i + 2)..close], start, close + 1));
                i = close + 1;
            }
        }
        return attributes;
    }

    private static string Unescape(string value)
        => value.Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
}

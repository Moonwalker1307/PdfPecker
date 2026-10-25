using System.Globalization;
using System.Text;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// Writes the structured HTML of PDF pages: headings, paragraphs, lists and tables in reading order, as text that
/// flows, with nothing placed at a position on the page.
/// </summary>
internal static class FlowHtmlWriter
{
    /// <summary>The style sheet of the structured output: plain, readable and the same for every document.</summary>
    public static void WriteStyles(StringBuilder sb)
    {
        sb.Append("body{max-width:60em;margin:0 auto;padding:16px;font-family:system-ui,sans-serif;line-height:1.45;color:#111;background:#fff}\n");
        sb.Append(".pdf-page+.pdf-page{border-top:1px solid #ccc;margin-top:2em}\n");
        sb.Append("table{border-collapse:collapse;margin:1em 0}\n");
        sb.Append("th,td{border:1px solid #bbb;padding:.2em .5em;text-align:start;vertical-align:top}\n");
        sb.Append("th{background:#f2f2f2}\n");
        sb.Append(".pdf-columns{display:flex;flex-wrap:wrap;gap:0 2em}\n");
        sb.Append(".pdf-columns>div{flex:1 1 15em;min-width:0}\n");
    }

    /// <summary>A page's blocks in a <c>section</c> of class <c>pdf-page</c>.</summary>
    /// <param name="sb">Receives the HTML.</param>
    /// <param name="pageNumber">The page number, from 1, which gives the section its id; 0 for a section without an id.</param>
    /// <param name="blocks">The page's blocks in reading order.</param>
    /// <param name="pageIds">Maps a page index to its page number in the output, or 0 when the page is left out.</param>
    public static void WritePage(StringBuilder sb, int pageNumber, List<FlowBlock> blocks, Func<int, int> pageIds)
    {
        sb.Append("<section class=\"pdf-page\"");
        if (pageNumber > 0) sb.Append(" id=\"page-").Append(pageNumber.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(">\n");
        WriteBlocks(sb, blocks, pageIds);
        sb.Append("</section>\n");
    }

    private static void WriteBlocks(StringBuilder sb, List<FlowBlock> blocks, Func<int, int> pageIds)
    {
        string? list = null; // the list element open, "ul" or "ol"
        foreach (var block in blocks)
        {
            string? wanted = block is TextBlock { Kind: TextKind.Bullet } ? "ul" : block is TextBlock { Kind: TextKind.Numbered } ? "ol" : null;
            if (list != null && list != wanted) { sb.Append("</").Append(list).Append(">\n"); list = null; }
            if (wanted != null && list == null) { sb.Append('<').Append(wanted).Append(">\n"); list = wanted; }
            switch (block)
            {
                case TextBlock t: WriteText(sb, t, pageIds); break;
                case TableBlock table: WriteTable(sb, table, pageIds); break;
                case GroupBlock group:
                    sb.Append("<div class=\"pdf-columns\">\n");
                    foreach (var part in group.Parts)
                    {
                        sb.Append("<div>\n");
                        WriteBlocks(sb, part, pageIds);
                        sb.Append("</div>\n");
                    }
                    sb.Append("</div>\n");
                    break;
            }
        }
        if (list != null) sb.Append("</").Append(list).Append(">\n");
    }

    private static void WriteText(StringBuilder sb, TextBlock block, Func<int, int> pageIds)
    {
        string tag = block.Kind switch
        {
            TextKind.Heading => "h" + block.Level.ToString(CultureInfo.InvariantCulture),
            TextKind.Bullet or TextKind.Numbered => "li",
            _ => "p",
        };
        sb.Append('<').Append(tag);
        if (block.Kind == TextKind.Numbered) sb.Append(" value=\"").Append(block.Number.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (block.Lines.Count(l => l.RightToLeft) * 2 > block.Lines.Count) sb.Append(" dir=\"rtl\"");
        sb.Append('>');
        // A list item's marker is the list's own.
        int skip = block.Kind is TextKind.Bullet or TextKind.Numbered ? 1 : 0;
        // Lines that end at one edge but start at different places, or share a centre, are set right-aligned or centred
        // one by one, as an address block in a corner is; each is a line of its own.
        bool aligned = block.Lines.Count >= 2 && block.Lines.Max(l => l.X0) - block.Lines.Min(l => l.X0) > 4
            && (block.Lines.Max(l => l.X1) - block.Lines.Min(l => l.X1) <= 2
                || block.Lines.Max(l => l.X0 + l.X1) - block.Lines.Min(l => l.X0 + l.X1) <= 4);
        WriteLines(sb, block.Lines, aligned ? double.MaxValue : block.Lines.Max(l => l.X1), skip, block.Kind != TextKind.Heading, false, pageIds);
        sb.Append("</").Append(tag).Append(">\n");
    }

    private static void WriteTable(StringBuilder sb, TableBlock table, Func<int, int> pageIds)
    {
        sb.Append("<table");
        if (table.RightToLeft) sb.Append(" dir=\"rtl\"");
        sb.Append(">\n");
        // Each grid position is covered by at most one cell; positions no cell covers are empty cells.
        var at = new Dictionary<(int, int), TableCell>();
        var covered = new HashSet<(int, int)>();
        foreach (var c in table.Cells)
        {
            at[(c.Row, table.RightToLeft ? c.Column + c.ColumnSpan - 1 : c.Column)] = c;
            for (int r = c.Row; r < c.Row + c.RowSpan; r++)
                for (int k = c.Column; k < c.Column + c.ColumnSpan; k++) covered.Add((r, k));
        }
        for (int r = 0; r < table.Rows; r++)
        {
            bool header = r < table.HeaderRows;
            if (r == 0 && header) sb.Append("<thead>\n");
            else if (r == table.HeaderRows) sb.Append("<tbody>\n");
            sb.Append("<tr>");
            string cellTag = header ? "th" : "td";
            for (int i = 0; i < table.Columns; i++)
            {
                int c = table.RightToLeft ? table.Columns - 1 - i : i;
                if (at.TryGetValue((r, c), out var cell))
                {
                    sb.Append('<').Append(cellTag);
                    if (cell.RowSpan > 1) sb.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    if (cell.ColumnSpan > 1) sb.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    sb.Append('>');
                    if (cell.Lines.Count > 0)
                    {
                        // Text can reach as far from the right side of a cell as it starts from the left.
                        double padding = Math.Max(0, cell.Lines.Min(l => l.X0) - cell.X0);
                        WriteLines(sb, cell.Lines, Math.Max(cell.X1 - padding, cell.Lines.Max(l => l.X1)), 0, !header, true, pageIds);
                    }
                    sb.Append("</").Append(cellTag).Append('>');
                }
                else if (!covered.Contains((r, c))) sb.Append('<').Append(cellTag).Append("></").Append(cellTag).Append('>');
            }
            sb.Append("</tr>\n");
            if (header && r == table.HeaderRows - 1) sb.Append("</thead>\n");
        }
        if (table.Rows > table.HeaderRows) sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    /// <summary>
    /// Lines of text, joined by a space where the text wrapped and by a line break where the next word would have fit on
    /// the line before, so the PDF broke the line on purpose. A word broken by a hyphen at the end of a line is joined.
    /// </summary>
    /// <param name="sb">Receives the HTML.</param>
    /// <param name="lines">The lines, from the top.</param>
    /// <param name="right">Where the lines could reach on the right.</param>
    /// <param name="skip">Words to leave out at the start, such as a list item's marker.</param>
    /// <param name="bold">False where the element is bold anyway, as headings and header cells are.</param>
    /// <param name="cell">Whether the lines are a table cell's.</param>
    /// <param name="pageIds">Maps a page index to its page number in the output, or 0 when the page is left out.</param>
    private static void WriteLines(StringBuilder sb, List<FlowLine> lines, double right, int skip, bool bold, bool cell, Func<int, int> pageIds)
    {
        // Each word with what goes before it: nothing, a space or a line break.
        var words = new List<(FlowWord Word, string Before)>();
        FlowLine? previous = null;
        foreach (var line in lines)
        {
            int start = previous == null ? skip : 0;
            if (start >= line.Words.Count) { previous ??= line; continue; }
            for (int k = start; k < line.Words.Count; k++)
            {
                string before = words.Count == 0 ? "" : " ";
                if (k == start && previous != null && words.Count > 0)
                {
                    var next = line.Words[k];
                    bool fits = !line.RightToLeft && previous.X1 + 0.3 * next.Size + (next.X1 - next.X0) < right - 1;
                    // The lines of a table cell are often separate values, such as an address; text that wrapped goes on
                    // in lower case.
                    if (fits || (cell && next.Text.Length > 0 && !char.IsLower(next.Text[0]) && !Hyphenated(words[^1].Word.Text, "a"))) before = "<br>";
                    else if (Hyphenated(words[^1].Word.Text, next.Text)) before = "";
                }
                words.Add((line.Words[k], before));
            }
            previous = line;
        }
        WriteWords(sb, words, bold, pageIds);
    }

    private static bool Hyphenated(string end, string next)
        => end.Length >= 3 && end[^1] is '-' or '‐' && char.IsLetter(end[^2]) && next.Length > 0 && char.IsLower(next[0]);

    /// <summary>Words with what goes before each, with runs of bold, italic and linked words marked as such.</summary>
    private static void WriteWords(StringBuilder sb, List<(FlowWord Word, string Before)> words, bool bold, Func<int, int> pageIds)
    {
        for (int i = 0; i < words.Count;)
        {
            var w = words[i].Word;
            bool b = bold && w.Bold;
            int j = i + 1;
            while (j < words.Count && (bold && words[j].Word.Bold) == b && words[j].Word.Italic == w.Italic
                && words[j].Word.Href == w.Href && words[j].Word.LinkPage == w.LinkPage) j++;
            sb.Append(words[i].Before);
            string? href = w.Href;
            if (href == null && w.LinkPage >= 0 && pageIds(w.LinkPage) is int page and > 0)
                href = "#page-" + page.ToString(CultureInfo.InvariantCulture);
            if (href != null) { sb.Append("<a href=\""); HtmlPageWriter.AppendEscaped(sb, href, attribute: true); sb.Append("\">"); }
            if (b) sb.Append("<b>");
            if (w.Italic) sb.Append("<i>");
            for (int k = i; k < j; k++)
            {
                if (k > i) sb.Append(words[k].Before);
                HtmlPageWriter.AppendEscaped(sb, words[k].Word.Text, attribute: false);
            }
            if (w.Italic) sb.Append("</i>");
            if (b) sb.Append("</b>");
            if (href != null) sb.Append("</a>");
            i = j;
        }
    }
}

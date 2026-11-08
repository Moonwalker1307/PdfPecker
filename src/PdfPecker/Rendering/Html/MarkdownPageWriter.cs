using System.Globalization;
using System.Text;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// Writes the structure of PDF pages as GitHub Flavored Markdown: the same headings, paragraphs, lists and tables as the
/// structured HTML, with text escaped so that nothing in it reads as Markdown syntax.
/// </summary>
internal static class MarkdownPageWriter
{
    /// <summary>The most digits CommonMark allows in an ordered list item's number.</summary>
    private const int MaxListNumber = 999_999_999;

    /// <summary>A page's blocks, each followed by a blank line, or by a line break between the items of one list.</summary>
    public static void WritePage(StringBuilder sb, List<FlowBlock> blocks)
    {
        var state = new ListState();
        WriteBlocks(sb, blocks, ref state);
    }

    /// <summary>The list being written: its kind, and the marker that tells it from a list just before it.</summary>
    private struct ListState
    {
        public string? Kind; // "ul" or "ol", or null outside a list
        public char Bullet, Delimiter;
        public int Next; // the number Markdown gives the next item of an ordered list
    }

    private static void WriteBlocks(StringBuilder sb, List<FlowBlock> blocks, ref ListState list)
    {
        for (int i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case TextBlock text when text.Lines.Count > 0:
                    WriteText(sb, text, ref list);
                    break;
                case TableBlock table when table.Rows > 0 && table.Columns > 0:
                    EndList(sb, ref list);
                    WriteTable(sb, table);
                    break;
                case ImageBlock image:
                {
                    EndList(sb, ref list);
                    // Pictures side by side, as a row of product photos is, stay on one line.
                    var row = new List<ImageBlock> { image };
                    while (i + 1 < blocks.Count && blocks[i + 1] is ImageBlock next && Math.Abs(next.Y0 - image.Y0) < 4)
                    {
                        row.Add(next);
                        i++;
                    }
                    row.Sort((a, b) => a.X0.CompareTo(b.X0));
                    for (int k = 0; k < row.Count; k++)
                    {
                        if (k > 0) sb.Append(' ');
                        sb.Append("![](data:").Append(row[k].MediaType).Append(";base64,").Append(Convert.ToBase64String(row[k].Data)).Append(')');
                    }
                    sb.Append("\n\n");
                    break;
                }
                case GroupBlock group:
                    // Columns are read in the order PdfText reads them, from the right for right-to-left text.
                    foreach (var part in TextReadingOrder.SideBySide(group) ? TextReadingOrder.Parts(group) : group.Parts) WriteBlocks(sb, part, ref list);
                    break;
            }
        }
        EndList(sb, ref list);
    }

    /// <summary>Ends a list with the blank line that sets it apart from the block after it.</summary>
    private static void EndList(StringBuilder sb, ref ListState list)
    {
        if (list.Kind == null) return;
        sb.Append('\n');
        list.Kind = null;
    }

    private static void WriteText(StringBuilder sb, TextBlock block, ref ListState list)
    {
        // Lines set right-aligned or centred one by one, as an address block in a corner is, are each a line of their own.
        var (right, centred) = FlowHtmlWriter.Alignment(block.Lines);
        double reach = right || centred ? double.MaxValue : FlowStyles.Max(block.Lines, l => l.X1);
        if (block.Kind == TextKind.Heading)
        {
            EndList(sb, ref list);
            var words = FlowHtmlWriter.Words(block.Lines, reach, 0, false);
            if (words.Count == 0) return;
            sb.Append('#', Math.Clamp(block.Level, 1, 6)).Append(' ');
            WriteWords(sb, words, Breaks.Space, "", bold: false, heading: true);
            sb.Append("\n\n");
            return;
        }

        // Markdown numbers lists only with digits; a list counted in letters or Roman numerals keeps its own markers.
        bool numbered = block.Kind == TextKind.Numbered && block.ListType is null or "1" && block.Number is >= 0 and <= MaxListNumber;
        bool item = block.Kind is TextKind.Bullet or TextKind.Numbered;
        int skip = item && (numbered || block.Kind == TextKind.Bullet) ? block.MarkerWords : 0;
        var text = FlowHtmlWriter.Words(block.Lines, reach, skip, false);
        if (!item)
        {
            EndList(sb, ref list);
            if (text.Count == 0) return;
            WriteWords(sb, text, Breaks.Backslash, "", bold: true, heading: false);
            sb.Append("\n\n");
            return;
        }

        string kind = numbered ? "ol" : "ul";
        // Markdown numbers an ordered list on from its first item, so an item numbered otherwise starts a list of its own.
        if (list.Kind != null && (list.Kind != kind || block.NewList || numbered && block.Number != list.Next))
        {
            // A list right after another list is told apart by its marker, as a blank line would only join them.
            char bullet = list.Kind == "ul" && kind == "ul" ? (list.Bullet == '-' ? '*' : '-') : '-';
            char delimiter = list.Kind == "ol" && kind == "ol" ? (list.Delimiter == '.' ? ')' : '.') : '.';
            sb.Append('\n');
            list = new ListState { Kind = kind, Bullet = bullet, Delimiter = delimiter };
        }
        else if (list.Kind == null) list = new ListState { Kind = kind, Bullet = '-', Delimiter = '.' };
        list.Next = block.Number + 1;
        string marker = numbered
            ? block.Number.ToString(CultureInfo.InvariantCulture) + list.Delimiter + " "
            : list.Bullet + " ";
        sb.Append(marker);
        // The lines of an item after a line break are indented to its text, so they stay part of the item.
        if (text.Count > 0) WriteWords(sb, text, Breaks.Backslash, new string(' ', marker.Length), bold: true, heading: false);
        sb.Append('\n');
    }

    private static void WriteTable(StringBuilder sb, TableBlock table)
    {
        // Markdown tables have no spans: a spanned cell's text is in its first row and column, and where several header
        // rows are joined it is in every column it spans, so each column's header reads whole.
        var text = new string?[table.Rows, table.Columns];
        var owner = new TableCell?[table.Rows, table.Columns];
        foreach (var cell in table.Cells)
        {
            if (cell.Row < 0 || cell.Row >= table.Rows || cell.Column < 0 || cell.Column >= table.Columns) continue;
            text[cell.Row, Anchor(cell)] = cell.Lines.Count == 0 ? "" : CellText(cell, cell.Row < table.HeaderRows);
            for (int r = cell.Row; r < Math.Min(table.Rows, cell.Row + cell.RowSpan); r++)
                for (int c = cell.Column; c < Math.Min(table.Columns, cell.Column + cell.ColumnSpan); c++) owner[r, c] = cell;
        }

        // Columns are written in reading order, from the right in a right-to-left table, and a spanned cell's text is in
        // the column it is read from first.
        int Column(int i) => table.RightToLeft ? table.Columns - 1 - i : i;
        int Anchor(TableCell cell) => table.RightToLeft ? Math.Min(table.Columns - 1, cell.Column + cell.ColumnSpan - 1) : cell.Column;

        // Markdown has one header row; several are joined column by column, as "2024 Q1" under a "2024" spanning two.
        // A single header row keeps a spanned cell's text in its first column, as body rows do.
        int headerRows = Math.Min(table.HeaderRows, table.Rows);
        sb.Append('|');
        for (int i = 0; i < table.Columns; i++)
        {
            int c = Column(i);
            var parts = new List<string>();
            for (int r = 0; r < headerRows; r++)
            {
                var cell = owner[r, c];
                string? part = cell == null ? text[r, c] : headerRows > 1 || Anchor(cell) == c ? text[cell.Row, Anchor(cell)] : null;
                if (!string.IsNullOrEmpty(part) && (parts.Count == 0 || parts[^1] != part)) parts.Add(part);
            }
            sb.Append(' ').Append(string.Join(" ", parts)).Append(" |");
        }
        sb.Append("\n|");
        for (int i = 0; i < table.Columns; i++) sb.Append(" --- |");
        sb.Append('\n');
        for (int r = headerRows; r < table.Rows; r++)
        {
            sb.Append('|');
            for (int i = 0; i < table.Columns; i++) sb.Append(' ').Append(text[r, Column(i)]).Append(" |");
            sb.Append('\n');
        }
        sb.Append('\n');
    }

    private static string CellText(TableCell cell, bool header)
    {
        var sb = new StringBuilder();
        // A header cell is bold anyway.
        WriteWords(sb, FlowHtmlWriter.Words(cell.Lines, FlowHtmlWriter.CellReach(cell), 0, true), Breaks.Html, "", bold: !header, heading: false);
        return sb.ToString();
    }

    /// <summary>How a line break the PDF made on purpose is written.</summary>
    private enum Breaks
    {
        /// <summary>A backslash at the end of the line, CommonMark's hard line break.</summary>
        Backslash,
        /// <summary>A <c>&lt;br&gt;</c>, as a table cell is one line.</summary>
        Html,
        /// <summary>A space, as a heading is one line.</summary>
        Space,
    }

    /// <summary>
    /// Words with what goes before each, with runs of bold, italic and linked words marked as such. A word joined to the
    /// one before it, as the halves of a hyphenated word are, stays in that word's run, as emphasis cannot start inside
    /// a word.
    /// </summary>
    private static void WriteWords(StringBuilder sb, List<(FlowWord Word, string Before)> words, Breaks breaks, string indent, bool bold,
        bool heading)
    {
        // A table cell is inline text, where nothing at its start reads as a block.
        bool lineStart = breaks != Breaks.Html;
        for (int i = 0; i < words.Count;)
        {
            var w = words[i].Word;
            bool b = bold && w.Bold;
            int j = i + 1;
            while (j < words.Count && (words[j].Before.Length == 0
                || (bold && words[j].Word.Bold) == b && words[j].Word.Italic == w.Italic && words[j].Word.Href == w.Href)) j++;
            if (i > 0) Before(words[i].Before);
            if (w.Href != null) { sb.Append('['); lineStart = false; }
            if (b) { sb.Append("**"); lineStart = false; }
            if (w.Italic) { sb.Append('*'); lineStart = false; }
            for (int k = i; k < j; k++)
            {
                if (k > i) Before(words[k].Before);
                AppendEscaped(sb, words[k].Word.Text, lineStart, heading);
                lineStart = false;
            }
            if (w.Italic) sb.Append('*');
            if (b) sb.Append("**");
            if (w.Href != null) sb.Append("](").Append(Destination(w.Href)).Append(')');
            i = j;
        }

        void Before(string before)
        {
            if (before != "<br>") sb.Append(before);
            else if (breaks == Breaks.Html) sb.Append("<br>");
            else if (breaks == Breaks.Space) sb.Append(' ');
            else
            {
                sb.Append("\\\n").Append(indent);
                lineStart = true;
            }
        }
    }

    /// <summary>
    /// A link's address, with the characters that would end it percent-encoded. Addresses are absolute and of the schemes
    /// <see cref="PageLinks"/> allows, so they hold no spaces.
    /// </summary>
    private static string Destination(string href)
        => href.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal)
            .Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal);

    /// <summary>
    /// Text with a backslash before every character that could start Markdown syntax: emphasis, links, code, raw HTML,
    /// entities and table pipes anywhere, and headings, block quotes, list markers and thematic breaks at the start of a
    /// line. Control characters become spaces.
    /// </summary>
    internal static void AppendEscaped(StringBuilder sb, string text, bool lineStart, bool heading)
    {
        // Where the delimiter of what would be an ordered list item's number is, as in "1." at the start of a line.
        int digits = 0;
        if (lineStart)
            while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits == 0 || digits > 9 || digits >= text.Length || text[digits] is not ('.' or ')')) digits = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c)) { sb.Append(' '); continue; }
            bool escape = c switch
            {
                '\\' or '`' or '*' or '[' or ']' or '<' or '>' or '&' or '|' or '~' => true,
                // An underscore inside a word cannot start or end emphasis.
                '_' => i == 0 || i == text.Length - 1 || !char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(text[i + 1]),
                '#' => heading || (lineStart && i == 0),
                '-' or '+' or '=' => lineStart && i == 0,
                '.' or ')' => lineStart && i == digits,
                _ => false,
            };
            if (escape) sb.Append('\\');
            sb.Append(c);
        }
    }
}

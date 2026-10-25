using System.Globalization;
using System.Text;

namespace PdfPecker.Rendering.Html;

/// <summary>
/// Writes the structured HTML of PDF pages: headings, paragraphs, lists and tables in reading order, as text that
/// flows, with nothing placed at a position on the page.
/// </summary>
internal static class FlowHtmlWriter
{
    /// <summary>
    /// The style sheet of the structured output: plain, readable and the same for every document. With
    /// <paramref name="styled"/>, it leaves fonts, colours, borders and widths to each page's own classes.
    /// </summary>
    public static void WriteStyles(StringBuilder sb, bool styled = false)
    {
        if (styled)
        {
            sb.Append("body{margin:0;padding:16px;line-height:1.3;background:#fff}\n");
            sb.Append(".pdf-page{margin:0 auto}\n");
            sb.Append(".pdf-page+.pdf-page{border-top:1px solid #ccc;margin-top:2em;padding-top:1em}\n");
            sb.Append("p,h1,h2,h3,h4,h5,h6,ul,ol{margin:.4em 0}\n");
            sb.Append("table{border-collapse:collapse;margin:.6em 0}\n");
            sb.Append("th,td{padding:.2em .4em;text-align:start;vertical-align:top}\n");
            sb.Append("img{max-width:100%;height:auto}\n");
            sb.Append(".pdf-columns{display:flex;flex-wrap:wrap;gap:0 2em}\n");
            sb.Append(".pdf-columns>div{flex:1 1 15em;min-width:0}\n");
            return;
        }
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
    /// <param name="styles">The page's looks, for output with styles; null for plain output.</param>
    public static void WritePage(StringBuilder sb, int pageNumber, List<FlowBlock> blocks, Func<int, int> pageIds, FlowStyles? styles = null)
    {
        // The page's classes are made while its content is written, and go before it.
        StringBuilder? content = null;
        if (styles != null)
        {
            content = new StringBuilder();
            WriteBlocks(content, blocks, pageIds, styles);
        }
        string? section = styles?.Class(styles.Section());
        styles?.WriteStyleElement(sb);
        sb.Append("<section class=\"pdf-page");
        if (section != null) sb.Append(' ').Append(section);
        sb.Append('"');
        if (pageNumber > 0) sb.Append(" id=\"page-").Append(pageNumber.ToString(CultureInfo.InvariantCulture)).Append('"');
        sb.Append(">\n");
        if (content != null) sb.Append(content);
        else WriteBlocks(sb, blocks, pageIds, null);
        sb.Append("</section>\n");
    }

    private static void WriteBlocks(StringBuilder sb, List<FlowBlock> blocks, Func<int, int> pageIds, FlowStyles? styles)
    {
        string? list = null, listType = null; // the list element open, "ul" or "ol", and how it counts
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            string? wanted = block is TextBlock { Kind: TextKind.Bullet } ? "ul" : block is TextBlock { Kind: TextKind.Numbered } ? "ol" : null;
            string? type = wanted == "ol" && block is TextBlock { ListType: "a" or "A" or "i" or "I" } t0 ? t0.ListType : null;
            if (list != null && (list != wanted || listType != type || block is TextBlock { NewList: true }))
            {
                sb.Append("</").Append(list).Append(">\n");
                list = null;
            }
            if (wanted != null && list == null)
            {
                sb.Append('<').Append(wanted);
                if (type != null) sb.Append(" type=\"").Append(type).Append('"');
                sb.Append(">\n");
                (list, listType) = (wanted, type);
            }
            switch (block)
            {
                case TextBlock t: WriteText(sb, t, pageIds, styles); break;
                case TableBlock table: WriteTable(sb, table, pageIds, styles); break;
                case ImageBlock image:
                {
                    // Pictures side by side, as a row of product photos is, stay side by side.
                    var row = new List<ImageBlock> { image };
                    while (i + 1 < blocks.Count && blocks[i + 1] is ImageBlock next && Math.Abs(next.Y0 - image.Y0) < 4)
                    {
                        row.Add(next);
                        i++;
                    }
                    row.Sort((a, b) => a.X0.CompareTo(b.X0));
                    WriteImages(sb, row, styles);
                    break;
                }
                case GroupBlock group:
                    sb.Append("<div class=\"pdf-columns\">\n");
                    foreach (var part in group.Parts)
                    {
                        sb.Append("<div>\n");
                        WriteBlocks(sb, part, pageIds, styles);
                        sb.Append("</div>\n");
                    }
                    sb.Append("</div>\n");
                    break;
            }
        }
        if (list != null) sb.Append("</").Append(list).Append(">\n");
    }

    private static void WriteText(StringBuilder sb, TextBlock block, Func<int, int> pageIds, FlowStyles? styles)
    {
        string tag = block.Kind switch
        {
            TextKind.Heading => "h" + block.Level.ToString(CultureInfo.InvariantCulture),
            TextKind.Bullet or TextKind.Numbered => "li",
            _ => "p",
        };
        // Lines that end at one edge but start at different places, or share a centre, are set right-aligned or centred
        // one by one, as an address block in a corner is; each is a line of its own.
        bool right = block.Lines.Count >= 2 && block.Lines.Max(l => l.X0) - block.Lines.Min(l => l.X0) > 4
            && block.Lines.Max(l => l.X1) - block.Lines.Min(l => l.X1) <= 2;
        bool centred = block.Lines.Count >= 2 && block.Lines.Max(l => l.X0) - block.Lines.Min(l => l.X0) > 4
            && block.Lines.Max(l => l.X0 + l.X1) - block.Lines.Min(l => l.X0 + l.X1) <= 4;
        TextStyle? around = null;
        sb.Append('<').Append(tag);
        if (styles != null)
        {
            var d = new StringBuilder();
            around = TextDeclarations(d, styles, block.Lines, styles.Base, block.Kind == TextKind.Heading);
            double x0 = block.Lines.Min(l => l.X0), x1 = block.Lines.Max(l => l.X1);
            // One line at the right of the page, or in its middle, keeps its place.
            if (!right && !centred && block.Lines.Count == 1 && x0 > (styles.Left + styles.Right) / 2 && styles.Right - x1 < 6) right = true;
            if (!right && !centred && block.Lines.Count == 1 && x0 - styles.Left > 24
                && Math.Abs((x0 + x1) / 2 - (styles.Left + styles.Right) / 2) < 6) centred = true;
            double top = block.Lines.Min(l => l.Top), bottom = block.Lines.Max(l => l.Bottom);
            if (styles.Background(x0, top, x1, bottom) is { } fill)
            {
                // A box filled around the text, such as a banner, or a badge only as wide as its text.
                d.Append("background:").Append(FlowStyles.Hex(fill.Color)).Append(';');
                bool fit = fill.X1 - fill.X0 < 0.7 * (styles.Right - styles.Left);
                // A box as wide as the text column stretches with the window, so its text keeps the same space on both
                // sides rather than the room the line left on the page.
                double left = Math.Max(0, x0 - fill.X0), rightSpace = fit ? Math.Max(0, fill.X1 - x1) : left;
                if (block.Lines.Count == 1)
                {
                    // One line keeps the height of its box, centred in it as the line of a badge or button is.
                    d.Append("line-height:").Append(FlowStyles.Pt(fill.Y1 - fill.Y0)).Append(";padding:0 ").Append(FlowStyles.Pt(rightSpace))
                        .Append(" 0 ").Append(FlowStyles.Pt(left)).Append(';');
                }
                else
                {
                    d.Append("padding:").Append(FlowStyles.Pt(Math.Max(0, top - fill.Y0))).Append(' ').Append(FlowStyles.Pt(rightSpace))
                        .Append(' ').Append(FlowStyles.Pt(Math.Max(0, fill.Y1 - bottom))).Append(' ').Append(FlowStyles.Pt(left)).Append(';');
                }
                if (fill.Radius >= 1) d.Append("border-radius:").Append(FlowStyles.Pt(fill.Radius)).Append(';');
                if (fit)
                {
                    d.Append("width:fit-content;");
                    if (fill.X0 > (styles.Left + styles.Right) / 2) d.Append("margin-left:auto;");
                    right = centred = false;
                }
            }
            if (right) d.Append("text-align:right;");
            else if (centred) d.Append("text-align:center;");
            // A line drawn across the text just above it, such as one that sets a footnote apart.
            double size = block.Lines[0].Size;
            if (styles.Border(true, (top, top - 2.2 * size, top - 0.1 * size), x0, x1) is { } line)
                d.Append("border-top:").Append(FlowStyles.BorderValue(line)).Append(";padding-top:").Append(FlowStyles.Pt(Math.Max(0, top - line.Position))).Append(';');
            if (d.Length > 0) sb.Append(" class=\"").Append(styles.Class(d.ToString())).Append('"');
        }
        if (block.Kind == TextKind.Numbered) sb.Append(" value=\"").Append(block.Number.ToString(CultureInfo.InvariantCulture)).Append('"');
        if (block.Lines.Count(l => l.RightToLeft) * 2 > block.Lines.Count) sb.Append(" dir=\"rtl\"");
        sb.Append('>');
        // A list item's marker is the list's own.
        int skip = block.Kind is TextKind.Bullet or TextKind.Numbered ? block.MarkerWords : 0;
        bool aligned = block.Lines.Count >= 2 && (right || centred);
        WriteLines(sb, block.Lines, aligned ? double.MaxValue : block.Lines.Max(l => l.X1), skip, block.Kind != TextKind.Heading, false, pageIds,
            styles, around);
        sb.Append("</").Append(tag).Append(">\n");
    }

    private static void WriteTable(StringBuilder sb, TableBlock table, Func<int, int> pageIds, FlowStyles? styles)
    {
        TableGrid? grid = null;
        TextStyle? around = null;
        sb.Append("<table");
        if (styles != null)
        {
            grid = FlowStyles.Grid(table);
            var d = new StringBuilder();
            around = TextDeclarations(d, styles, table.Cells.SelectMany(c => c.Lines).ToList(), styles.Base, false);
            double width = grid.X[^1].Side - grid.X[0].Side, room = styles.Right - styles.Left;
            if (room > 0 && width > 0) d.Append("width:").Append(HtmlPageWriter.Number(Math.Min(100, Math.Round(width / room * 100)))).Append("%;");
            // A table on the right half of the page, such as an invoice's totals, stays on the right.
            if (grid.X[0].Side > (styles.Left + styles.Right) / 2 - 1) d.Append("margin-left:auto;");
            sb.Append(" class=\"").Append(styles.Class(d.ToString())).Append('"');
        }
        if (table.RightToLeft) sb.Append(" dir=\"rtl\"");
        sb.Append(">\n");
        if (styles != null && grid != null && table.Columns > 1)
        {
            // The columns keep the share of the table's width they have on the page.
            double width = grid.X[^1].Side - grid.X[0].Side;
            sb.Append("<colgroup>");
            for (int i = 0; i < table.Columns; i++)
            {
                int c = table.RightToLeft ? table.Columns - 1 - i : i;
                double share = width > 0 ? (grid.X[c + 1].Side - grid.X[c].Side) / width * 100 : 0;
                sb.Append("<col class=\"").Append(styles.Class("width:" + HtmlPageWriter.Number(Math.Round(share, 1)) + "%;")).Append("\">");
            }
            sb.Append("</colgroup>\n");
        }
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
                    TextStyle? inCell = around;
                    if (styles != null) inCell = WriteCellClass(sb, styles, grid!, table, cell, header, around!.Value);
                    sb.Append('>');
                    if (cell.Images != null)
                        foreach (var image in cell.Images) WriteImages(sb, [image], styles, inCell: true);
                    if (cell.Lines.Count > 0)
                    {
                        // Text can reach as far from the right side of a cell as it starts from the left.
                        double padding = Math.Max(0, cell.Lines.Min(l => l.X0) - cell.X0);
                        WriteLines(sb, cell.Lines, Math.Max(cell.X1 - padding, cell.Lines.Max(l => l.X1)), 0, !header, true, pageIds, styles, inCell);
                    }
                    sb.Append("</").Append(cellTag).Append('>');
                }
                else if (!covered.Contains((r, c)))
                {
                    sb.Append('<').Append(cellTag);
                    if (styles != null) WriteCellClass(sb, styles, grid!, table, new TableCell { Row = r, Column = c }, header, around!.Value);
                    sb.Append("></").Append(cellTag).Append('>');
                }
            }
            sb.Append("</tr>\n");
            if (header && r == table.HeaderRows - 1) sb.Append("</thead>\n");
        }
        if (table.Rows > table.HeaderRows) sb.Append("</tbody>\n");
        sb.Append("</table>\n");
    }

    /// <summary>
    /// A table cell's class: the borders drawn along its sides, its background, where its text sits in it and how its text
    /// differs from the table's; returns the cell's usual text style.
    /// </summary>
    private static TextStyle WriteCellClass(StringBuilder sb, FlowStyles styles, TableGrid grid, TableBlock table, TableCell cell, bool header,
        TextStyle around)
    {
        int last = Math.Min(table.Columns, cell.Column + cell.ColumnSpan), end = Math.Min(table.Rows, cell.Row + cell.RowSpan);
        double x0 = grid.X[cell.Column].Side, x1 = grid.X[last].Side, y0 = grid.Y[cell.Row].Side, y1 = grid.Y[end].Side;
        var d = new StringBuilder();
        var style = cell.Lines.Count > 0 ? TextDeclarations(d, styles, cell.Lines, around, header) : around;
        // A cell's fill is found under the whole cell or, where the grid is wider than the fill, under its text.
        var fill = styles.Background(x0, y0, x1, y1);
        if (fill == null && cell.Lines.Count > 0)
            fill = styles.Background(cell.Lines.Min(l => l.X0), cell.Lines.Min(l => l.Top), cell.Lines.Max(l => l.X1), cell.Lines.Max(l => l.Bottom));
        if (fill is { } f) d.Append("background:").Append(FlowStyles.Hex(f.Color)).Append(';');
        Side("border-top", true, grid.Y[cell.Row], x0, x1);
        Side("border-bottom", true, grid.Y[end], x0, x1);
        Side(table.RightToLeft ? "border-right" : "border-left", false, grid.X[cell.Column], y0, y1);
        Side(table.RightToLeft ? "border-left" : "border-right", false, grid.X[last], y0, y1);
        if (cell.Lines.Count > 0)
        {
            double left = cell.Lines.Min(l => l.X0) - x0, right = x1 - cell.Lines.Max(l => l.X1);
            bool flush = cell.Lines.Max(l => l.X1) - cell.Lines.Min(l => l.X1) <= 2;
            if (left > 4 && Math.Abs(left - right) <= 3) d.Append("text-align:center;");
            else if (left > 4 && flush && right < left / 2 && !table.RightToLeft) d.Append("text-align:right;");
        }
        if (d.Length > 0) sb.Append(" class=\"").Append(styles.Class(d.ToString())).Append('"');
        return style;

        void Side(string property, bool horizontal, (double Side, double From, double To) at, double from, double to)
        {
            if (styles.Border(horizontal, at, from, to) is { } rule) d.Append(property).Append(':').Append(FlowStyles.BorderValue(rule)).Append(';');
        }
    }

    /// <summary>
    /// The declarations that set some lines' usual text where text would otherwise look like <paramref name="around"/>,
    /// and the weight of a heading or header cell whose text is not bold; returns the lines' usual text style. Text too
    /// light to see on the page keeps the colour around it unless something behind it is filled.
    /// </summary>
    private static TextStyle TextDeclarations(StringBuilder d, FlowStyles styles, List<FlowLine> lines, TextStyle around, bool boldByDefault)
    {
        var words = lines.SelectMany(l => l.Words).ToList();
        var style = FlowStyles.Usual(words) ?? around;
        if (FlowStyles.Light(style.Color) && styles.Background(lines.Min(l => l.X0), lines.Min(l => l.Top), lines.Max(l => l.X1), lines.Max(l => l.Bottom)) == null)
            style = style with { Color = around.Color };
        FlowStyles.Text(d, style, around);
        if (boldByDefault && words.Count(w => w.Bold) * 2 < words.Count) d.Append("font-weight:normal;");
        return style;
    }

    /// <summary>
    /// Pictures in a row as image elements, each as wide as on the page and, with styles, the row at the side of the page
    /// it sits on and the pictures as far apart.
    /// </summary>
    private static void WriteImages(StringBuilder sb, List<ImageBlock> row, FlowStyles? styles, bool inCell = false)
    {
        sb.Append(inCell ? "<div>" : "<p");
        if (styles != null && !inCell)
        {
            // A nested picture's place comes from the part or cell it is in, not from the page's sides.
            double x0 = row[0].X0, x1 = row[^1].X1, middle = (styles.Left + styles.Right) / 2;
            if (!row[0].Nested && x0 > middle) sb.Append(" class=\"").Append(styles.Class("text-align:right;")).Append('"');
            else if (!row[0].Nested && Math.Abs((x0 + x1) / 2 - middle) < 6 && x0 - styles.Left > 24)
                sb.Append(" class=\"").Append(styles.Class("text-align:center;")).Append('"');
        }
        if (!inCell) sb.Append('>');
        for (int k = 0; k < row.Count; k++)
        {
            var image = row[k];
            // Without styles a space keeps pictures apart; with styles each keeps the gap it has on the page.
            if (k > 0 && styles == null) sb.Append(' ');
            sb.Append("<img ");
            if (styles == null)
            {
                // A CSS pixel is 3/4 of a point.
                int px = Math.Max(1, (int)Math.Round((image.X1 - image.X0) * 4 / 3));
                sb.Append("alt=\"\" width=\"").Append(px.ToString(CultureInfo.InvariantCulture)).Append('"');
            }
            else
            {
                var d = new StringBuilder("width:").Append(FlowStyles.Pt(image.X1 - image.X0)).Append(';');
                // A picture cropped on the page is cropped the same way here, at the same shape whatever its width.
                double dw = image.Drawn.X1 - image.Drawn.X0, dh = image.Drawn.Y1 - image.Drawn.Y0, w = image.X1 - image.X0, h = image.Y1 - image.Y0;
                if (dw - w > 1 || dh - h > 1)
                {
                    d.Append("aspect-ratio:").Append(HtmlPageWriter.Number(Math.Round(w / h, 3))).Append(";object-fit:cover;object-position:")
                        .Append(Percent(image.X0 - image.Drawn.X0, dw - w)).Append(' ').Append(Percent(image.Y0 - image.Drawn.Y0, dh - h)).Append(';');
                    static string Percent(double offset, double room)
                        => room <= 1 ? "50%" : Math.Round(Math.Clamp(offset / room * 100, 0, 100)).ToString(CultureInfo.InvariantCulture) + "%";
                }
                if (k + 1 < row.Count) d.Append("margin-right:").Append(FlowStyles.Pt(Math.Max(0, row[k + 1].X0 - image.X1))).Append(';');
                sb.Append("class=\"").Append(styles.Class(d.ToString())).Append("\" alt=\"\"");
            }
            sb.Append(" src=\"data:").Append(image.MediaType).Append(";base64,").Append(Convert.ToBase64String(image.Data)).Append("\">");
        }
        sb.Append(inCell ? "</div>" : "</p>\n");
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
    /// <param name="styles">The page's looks, for output with styles; null for plain output.</param>
    /// <param name="around">How the text of the element the lines are in looks, with styles.</param>
    private static void WriteLines(StringBuilder sb, List<FlowLine> lines, double right, int skip, bool bold, bool cell, Func<int, int> pageIds,
        FlowStyles? styles = null, TextStyle? around = null)
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
        WriteWords(sb, words, bold, pageIds, styles, around);
    }

    private static bool Hyphenated(string end, string next)
        => end.Length >= 3 && end[^1] is '-' or '‐' && char.IsLetter(end[^2]) && next.Length > 0 && char.IsLower(next[0]);

    /// <summary>Words with what goes before each, with runs of bold, italic and linked words marked as such.</summary>
    private static void WriteWords(StringBuilder sb, List<(FlowWord Word, string Before)> words, bool bold, Func<int, int> pageIds,
        FlowStyles? styles, TextStyle? around)
    {
        for (int i = 0; i < words.Count;)
        {
            var w = words[i].Word;
            bool b = bold && w.Bold;
            TextStyle? style = styles != null ? TextStyle.Of(w) : null;
            // Text too light to see keeps the colour around it, as the element's own colour does.
            if (style is { } light && around is { } a && FlowStyles.Light(light.Color) && !FlowStyles.Light(a.Color)) style = light with { Color = a.Color };
            int j = i + 1;
            while (j < words.Count && (bold && words[j].Word.Bold) == b && words[j].Word.Italic == w.Italic
                && words[j].Word.Href == w.Href && words[j].Word.LinkPage == w.LinkPage
                && (style == null || Same(TextStyle.Of(words[j].Word), style.Value))) j++;
            sb.Append(words[i].Before);
            string? href = w.Href;
            if (href == null && w.LinkPage >= 0 && pageIds(w.LinkPage) is int page and > 0)
                href = "#page-" + page.ToString(CultureInfo.InvariantCulture);
            if (href != null) { sb.Append("<a href=\""); HtmlPageWriter.AppendEscaped(sb, href, attribute: true); sb.Append("\">"); }
            string? span = null;
            if (style is { } s && around is { } outer && s != outer)
            {
                var d = new StringBuilder();
                FlowStyles.Text(d, s, outer);
                span = styles!.Class(d.ToString());
                sb.Append("<span class=\"").Append(span).Append("\">");
            }
            if (b) sb.Append("<b>");
            if (w.Italic) sb.Append("<i>");
            for (int k = i; k < j; k++)
            {
                if (k > i) sb.Append(words[k].Before);
                if (style != null && words[k].Word.ColorRuns != null) WriteColored(sb, words[k].Word, style.Value, styles!);
                else HtmlPageWriter.AppendEscaped(sb, words[k].Word.Text, attribute: false);
            }
            if (w.Italic) sb.Append("</i>");
            if (b) sb.Append("</b>");
            if (span != null) sb.Append("</span>");
            if (href != null) sb.Append("</a>");
            i = j;
        }
    }

    /// <summary>A word whose colour changes within it, such as a row of rating stars, with its parts in their colours.</summary>
    private static void WriteColored(StringBuilder sb, FlowWord word, TextStyle style, FlowStyles styles)
    {
        string text = word.Text;
        uint color = word.Color & 0xFFFFFF;
        int from = 0;
        foreach (var (start, next) in word.ColorRuns!.Append((text.Length, color)))
        {
            // A word shortened after it was read, as dot leaders are, keeps the parts it still has.
            int end = Math.Clamp(start, from, text.Length);
            if (end > from)
            {
                string part = text[from..end];
                if (color == style.Color) HtmlPageWriter.AppendEscaped(sb, part, attribute: false);
                else
                {
                    sb.Append("<span class=\"").Append(styles.Class("color:" + FlowStyles.Hex(color) + ";")).Append("\">");
                    HtmlPageWriter.AppendEscaped(sb, part, attribute: false);
                    sb.Append("</span>");
                }
            }
            from = end;
            color = next & 0xFFFFFF;
        }
    }

    /// <summary>Whether a word's style is that of the run before it, a light colour taken as the colour around it.</summary>
    private static bool Same(TextStyle next, TextStyle run)
        => next.Family == run.Family && next.Size == run.Size && (next.Color == run.Color || FlowStyles.Light(next.Color) && !FlowStyles.Light(run.Color));
}

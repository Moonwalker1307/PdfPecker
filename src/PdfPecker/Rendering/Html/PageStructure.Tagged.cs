namespace PdfPecker.Rendering.Html;

internal static partial class PageStructure
{
    /// <summary>Structure types whose text is part of the element around them, as a link or emphasis is part of its paragraph.</summary>
    private static readonly HashSet<string> InlineTypes = new(StringComparer.Ordinal)
    {
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu",
        "WT", "WP", "Em", "Strong", "Sub", "Lbl",
    };

    /// <summary>Structure types that are one paragraph each, whatever they hold.</summary>
    private static readonly HashSet<string> ParagraphTypes = new(StringComparer.Ordinal)
    {
        "P", "Caption", "Title", "Figure", "Formula", "Form", "FENote",
    };

    /// <summary>
    /// Of the text a page shows, outside artifacts such as running headers, the share the structure tree must account
    /// for; a page tagged less fully is read from its layout instead.
    /// </summary>
    private const double TaggedCoverage = 0.9;

    /// <summary>How near, in points, an artifact's glyph must be to the same glyph read from the tags to be a copy of it.</summary>
    private const double CopyCell = 2;

    /// <summary>Grid positions a tagged table's cells may cover, spans included; a larger table is read as its cells' text.</summary>
    private const int MaxTaggedCells = 100_000;

    /// <summary>The most columns a table's rows with cells left out are lined up with.</summary>
    private const int MaxPlacedColumns = 100;

    /// <summary>
    /// Builds a page of a tagged PDF from its structure tree, which says exactly what each piece of text is: headings,
    /// paragraphs, list items with their labels, and tables with their header rows and spanned cells. Text the tree
    /// does not hold, such as running headers and footers marked as artifacts, is read from its layout and placed by
    /// its height on the page, before the first block below it. Null when the tree accounts for too little of the
    /// page's text, other than artifacts, to be trusted.
    /// </summary>
    public static List<FlowBlock>? BuildTagged(StructureTree tree, int page, List<TextGlyph> glyphs, double width, double height,
        List<PageLink> links, CancellationToken cancellationToken)
    {
        var reader = new TaggedPage(tree, page, glyphs, width, height, links, cancellationToken);
        reader.Container(tree.Root);
        int taken = 0, left = 0;
        var rest = new List<TextGlyph>();
        // Artifacts that copy the text read at nearly the same place, such as a text shadow, are left out.
        var read = new HashSet<(string, int, int)>();
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (!reader.Taken[i] || string.IsNullOrWhiteSpace(glyphs[i].Text)) continue;
            taken += glyphs[i].Text.Length;
            read.Add((glyphs[i].Text, (int)Math.Floor(glyphs[i].X / CopyCell), (int)Math.Floor(glyphs[i].Y / CopyCell)));
        }
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (reader.Taken[i] || string.IsNullOrWhiteSpace(g.Text)) continue;
            if (g.Mcid == -2 && IsCopy(g)) continue;
            rest.Add(g);
            if (g.Mcid != -2) left += g.Text.Length;
        }
        if (taken == 0 || taken < TaggedCoverage * (taken + left)) return null;
        if (rest.Count == 0) return reader.Blocks;
        var blocks = reader.Blocks;
        foreach (var block in Build(TextLayout.Build(rest, page + 1, width, height, true, cancellationToken), null, null, links, cancellationToken))
        {
            int at = blocks.FindIndex(b => b.Top > block.Top);
            blocks.Insert(at < 0 ? blocks.Count : at, block);
        }
        return blocks;

        bool IsCopy(in TextGlyph g)
        {
            int x = (int)Math.Floor(g.X / CopyCell), y = (int)Math.Floor(g.Y / CopyCell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (read.Contains((g.Text, x + dx, y + dy))) return true;
            return false;
        }
    }

    /// <summary>
    /// Puts tables found from a page's layout into the blocks of its tags, where the tags hold the same text as
    /// paragraphs only: some writers tag a grid of labels and values, or any table without header cells, as text.
    /// </summary>
    public static List<FlowBlock> AddLayoutTables(List<FlowBlock> tagged, List<FlowBlock> layout)
    {
        foreach (var table in layout.SelectMany(Tables))
        {
            var words = table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).ToList();
            if (words.Count == 0) continue;
            double x0 = words.Min(w => w.X0) - 1, x1 = words.Max(w => w.X1) + 1, y0 = words.Min(w => w.Top) - 1, y1 = words.Max(w => w.Bottom) + 1;
            bool Inside(FlowWord w) => w.CenterX > x0 && w.CenterX < x1 && w.CenterY > y0 && w.CenterY < y1;
            // The paragraphs the table's text is in must hold no other text, and no tagged table may share the area.
            var inside = new List<int>();
            bool clash = false;
            int count = 0;
            for (int i = 0; i < tagged.Count && !clash; i++)
            {
                var blockWords = Words(tagged[i]).ToList();
                int within = blockWords.Count(Inside);
                if (within == 0) continue;
                if (within < blockWords.Count || tagged[i] is not TextBlock) clash = true;
                inside.Add(i);
                count += within;
            }
            if (clash || inside.Count < 2 || count != words.Count) continue;
            for (int k = inside.Count - 1; k > 0; k--) tagged.RemoveAt(inside[k]);
            tagged[inside[0]] = table;
        }
        return tagged;

        static IEnumerable<TableBlock> Tables(FlowBlock block) => block switch
        {
            TableBlock t => new[] { t },
            GroupBlock g => g.Parts.SelectMany(p => p).SelectMany(Tables),
            _ => Enumerable.Empty<TableBlock>(),
        };

        static IEnumerable<FlowWord> Words(FlowBlock block) => block switch
        {
            TextBlock t => t.Lines.SelectMany(l => l.Words),
            TableBlock t => t.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words),
            GroupBlock g => g.Parts.SelectMany(p => p).SelectMany(Words),
            _ => Enumerable.Empty<FlowWord>(),
        };
    }

    /// <summary>Reads one page's part of a structure tree into blocks, taking each glyph it places.</summary>
    private sealed class TaggedPage
    {
        public readonly List<FlowBlock> Blocks = new();
        public readonly bool[] Taken;
        private readonly int _page;
        private readonly List<TextGlyph> _glyphs;
        private readonly Dictionary<int, List<int>> _byMcid = new();
        private readonly double _width, _height;
        private readonly List<PageLink> _links;
        private readonly CancellationToken _cancel;

        public TaggedPage(StructureTree tree, int page, List<TextGlyph> glyphs, double width, double height, List<PageLink> links,
            CancellationToken cancel)
        {
            _page = page;
            _glyphs = glyphs;
            _width = width;
            _height = height;
            _links = links;
            _cancel = cancel;
            Taken = new bool[glyphs.Count];
            for (int i = 0; i < glyphs.Count; i++)
            {
                if (glyphs[i].Mcid < 0) continue;
                if (!_byMcid.TryGetValue(glyphs[i].Mcid, out var list)) _byMcid[glyphs[i].Mcid] = list = new List<int>();
                list.Add(i);
            }
            DropCopies();
        }

        /// <summary>
        /// Takes, without reading, marked content that repeats the text of earlier marked content a little offset, as
        /// a text shadow drawn as a second copy of the text is when its writer tags both copies.
        /// </summary>
        private void DropCopies()
        {
            var seen = new Dictionary<string, List<(double X, double Y, double Size)>>(StringComparer.Ordinal);
            foreach (var (_, list) in _byMcid.OrderBy(e => e.Value[0]))
            {
                var first = _glyphs[list[0]];
                var text = string.Concat(list.Select(i => _glyphs[i].Text));
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!seen.TryGetValue(text, out var places)) seen[text] = places = new List<(double, double, double)>();
                double reach = 0.3 * first.Size;
                if (places.Exists(p => Math.Abs(p.X - first.X) <= reach && Math.Abs(p.Y - first.Y) <= reach && Math.Abs(p.Size - first.Size) <= 0.01 * first.Size))
                {
                    foreach (int i in list) Taken[i] = true;
                    continue;
                }
                places.Add((first.X, first.Y, first.Size));
            }
        }

        /// <summary>
        /// An element that holds other elements, such as a section: its own text and inline children between block
        /// children are paragraphs.
        /// </summary>
        public void Container(StructureTree.Node node)
        {
            var run = new List<int>();
            foreach (var kid in node.Kids)
            {
                if (kid is StructureTree.Content c) Add(c, run);
                else if (kid is StructureTree.Node n && n.OnPage(_page))
                {
                    if (InlineTypes.Contains(n.Type)) Gather(n, run, null);
                    else
                    {
                        Text(run, TextKind.Paragraph, 0);
                        run = new List<int>();
                        Block(n);
                    }
                }
            }
            Text(run, TextKind.Paragraph, 0);
        }

        private void Block(StructureTree.Node node)
        {
            _cancel.ThrowIfCancellationRequested();
            switch (node.Type)
            {
                case "Table": Table(node); break;
                case "L": List(node); break;
                case "LI":
                    string? type = null;
                    Item(node, ref type, true);
                    break;
                case "H": Text(Gather(node), TextKind.Heading, 2); break;
                case "H1" or "H2" or "H3" or "H4" or "H5" or "H6": Text(Gather(node), TextKind.Heading, node.Type[1] - '0'); break;
                default:
                    if (ParagraphTypes.Contains(node.Type)) Text(Gather(node), TextKind.Paragraph, 0);
                    else Container(node);
                    break;
            }
        }

        private void List(StructureTree.Node list)
        {
            string? type = null; // how the list numbers its items, once its first label says
            bool first = true;
            foreach (var kid in list.Kids)
            {
                if (kid is StructureTree.Node n && n.OnPage(_page))
                {
                    if (n.Type == "LI")
                    {
                        Item(n, ref type, first);
                        first = false;
                    }
                    else Block(n);
                }
                else if (kid is StructureTree.Content c)
                {
                    var run = new List<int>();
                    Add(c, run);
                    Text(run, TextKind.Paragraph, 0);
                }
            }
        }

        /// <summary>
        /// A list item: its label, such as "3." or a bullet, gives the kind of list and the number, and the rest is the
        /// item's text. Lists inside the item follow it.
        /// </summary>
        private void Item(StructureTree.Node item, ref string? type, bool first)
        {
            var label = new List<int>();
            var nested = new List<StructureTree.Node>();
            foreach (var kid in item.Kids)
                if (kid is StructureTree.Node { Type: "Lbl" } lbl) Gather(lbl, label, null);
            var body = new List<int>();
            Gather(item, body, n =>
            {
                if (n.Type == "Lbl" && item.Kids.Contains(n)) return true;
                if (n.Type != "L") return false;
                nested.Add(n);
                return true;
            });
            var lines = Lines(body);
            if (lines.Count > 0)
            {
                var block = new TextBlock { Kind = TextKind.Bullet, MarkerWords = 0, NewList = first };
                if (label.Count > 0)
                {
                    var text = string.Concat(label.Select(i => _glyphs[i].Text)).Trim();
                    if (Label(text, first, ref type, out int number))
                    {
                        block.Kind = TextKind.Numbered;
                        block.Number = number;
                        block.ListType = type;
                    }
                    else if (text.Length > 0 && !IsBullet(text))
                    {
                        // A label the list's own numbers cannot show, such as "iv." or "b)", stays in the text, which is
                        // then a paragraph, as a list would add a marker of its own.
                        lines = Lines(body.Concat(label).ToList());
                        block.Kind = TextKind.Paragraph;
                    }
                }
                else if (Marker(lines[0], out int number) is var kind and not TextKind.Paragraph and not TextKind.Heading)
                {
                    // The label is not tagged apart, so the first word is the marker.
                    block.Kind = kind;
                    block.Number = number;
                    block.MarkerWords = 1;
                }
                block.Top = lines.Min(l => l.Top);
                block.Left = lines.Min(l => l.X0);
                block.Lines.AddRange(lines);
                Blocks.Add(block);
            }
            foreach (var list in nested) List(list);
        }

        /// <summary>
        /// A table from its rows and cells, wherever the rows sit: directly in the table or in its head, body and foot.
        /// The rows of the head, or else the leading rows of header cells only, are its header.
        /// </summary>
        private void Table(StructureTree.Node node)
        {
            // The head's rows come first and the foot's last, wherever the tree has them, as HTML may put a foot first.
            var rows = new List<(StructureTree.Node Row, bool Head)>();
            var foot = new List<(StructureTree.Node Row, bool Head)>();
            var other = new List<StructureTree.Node>();
            int heads = 0;
            foreach (var kid in node.Kids)
            {
                if (kid is not StructureTree.Node n || !n.OnPage(_page)) continue;
                if (n.Type == "TR") rows.Add((n, false));
                else if (n.Type is "THead" or "TBody" or "TFoot")
                {
                    foreach (var r in n.Kids)
                    {
                        if (r is not StructureTree.Node { Type: "TR" } tr || !tr.OnPage(_page)) continue;
                        if (n.Type == "TFoot") foot.Add((tr, false));
                        else if (n.Type == "THead") rows.Insert(heads++, (tr, true));
                        else rows.Add((tr, false));
                    }
                }
                else other.Add(n);
            }
            rows.AddRange(foot);
            // The grid positions the cells cover, spans included, bound the work; a table past them is read as text.
            long area = 0;
            for (int r = 0; r < rows.Count; r++)
                foreach (var kid in rows[r].Row.Kids)
                    if (kid is StructureTree.Node cellNode) area += (long)Math.Min(cellNode.RowSpan, rows.Count - r) * cellNode.ColumnSpan;
            if (rows.Count == 0 || area > MaxTaggedCells)
            {
                Container(node);
                return;
            }
            foreach (var n in other.Where(n => n.Type == "Caption")) Block(n);
            var table = new TableBlock { Rows = rows.Count };
            var covered = new HashSet<(int, int)>();
            int header = 0;
            bool leading = true;
            for (int r = 0; r < rows.Count; r++)
            {
                int column = 0;
                bool allHeader = true, any = false;
                foreach (var kid in rows[r].Row.Kids)
                {
                    if (kid is not StructureTree.Node cellNode) continue;
                    while (covered.Contains((r, column))) column++;
                    int rowSpan = Math.Min(cellNode.RowSpan, rows.Count - r), columnSpan = cellNode.ColumnSpan;
                    var cell = new TableCell { Row = r, Column = column, RowSpan = rowSpan, ColumnSpan = columnSpan };
                    if (cellNode.OnPage(_page)) cell.Lines.AddRange(Lines(Gather(cellNode)));
                    table.Cells.Add(cell);
                    for (int y = r; y < r + rowSpan; y++)
                        for (int x = column; x < column + columnSpan; x++) covered.Add((y, x));
                    column += columnSpan;
                    allHeader &= cellNode.Type == "TH";
                    any = true;
                }
                leading &= rows[r].Head || (any && allHeader);
                if (leading) header = r + 1;
            }
            table.Columns = covered.Count == 0 ? 0 : covered.Max(c => c.Item2) + 1;
            var lines = table.Cells.SelectMany(c => c.Lines).ToList();
            if (lines.Count == 0 || table.Columns == 0) return;
            PlaceShortRows(table, covered);
            // A header cell spanning rows takes the rows it spans into the header; a header of every row is none.
            for (bool grew = true; grew && header > 0;)
            {
                int reach = table.Cells.Where(c => c.Row < header).Select(c => c.Row + c.RowSpan).DefaultIfEmpty(header).Max();
                grew = reach > header;
                header = Math.Max(header, reach);
            }
            table.HeaderRows = header < table.Rows ? header : 0;
            // Each column's sides, from its cells' text, tell a wrapped line from a line break.
            var sides = new (double X0, double X1)[table.Columns];
            Array.Fill(sides, (double.MaxValue, double.MinValue));
            foreach (var c in table.Cells.Where(c => c.ColumnSpan == 1 && c.Lines.Count > 0))
                sides[c.Column] = (Math.Min(sides[c.Column].X0, c.Lines.Min(l => l.X0)), Math.Max(sides[c.Column].X1, c.Lines.Max(l => l.X1)));
            foreach (var c in table.Cells)
            {
                var first = sides[c.Column];
                var last = sides[c.Column + c.ColumnSpan - 1];
                c.X0 = first.X0 != double.MaxValue ? first.X0 : c.Lines.Count > 0 ? c.Lines.Min(l => l.X0) : 0;
                c.X1 = last.X1 != double.MinValue ? last.X1 : c.Lines.Count > 0 ? c.Lines.Max(l => l.X1) : 0;
                if (c.Lines.Count > 0) c.X1 = Math.Max(c.X1, c.Lines.Max(l => l.X1));
            }
            table.Top = lines.Min(l => l.Top);
            table.Left = lines.Min(l => l.X0);
            SetDirection(table);
            Blocks.Add(table);
            foreach (var n in other.Where(n => n.Type != "Caption")) Block(n);
        }

        /// <summary>
        /// Reads a list label as a number and the way the list counts: "3." in decimal, "b)" in letters, "iv." in roman
        /// numerals. A single letter that could be either is a letter, unless it starts a list as "i" or continues a
        /// list of roman numerals.
        /// </summary>
        private static bool Label(string text, bool first, ref string? type, out int number)
        {
            number = 0;
            var core = text.Trim().TrimStart('(').TrimEnd('.', ')', ':').Trim();
            if (core.Length is 0 or > 9) return false;
            if (core.All(char.IsAsciiDigit))
            {
                type = "1";
                return Invariant.TryParseInt(core, out number);
            }
            bool lower = core.All(char.IsAsciiLetterLower);
            if (!lower && !core.All(char.IsAsciiLetterUpper)) return false;
            string roman = lower ? "i" : "I";
            bool isRoman = Roman(core, out int value);
            if (core.Length == 1 && !(isRoman && (type == roman || first && core is "i" or "I")))
            {
                type = lower ? "a" : "A";
                number = char.ToLowerInvariant(core[0]) - 'a' + 1;
                return true;
            }
            if (!isRoman) return false;
            type = roman;
            number = value;
            return true;
        }

        private static bool Roman(string text, out int value)
        {
            value = 0;
            int previous = 0;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                int digit = char.ToLowerInvariant(text[i]) switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, 'd' => 500, 'm' => 1000, _ => 0 };
                if (digit == 0) return false;
                value += digit < previous ? -digit : digit;
                previous = Math.Max(previous, digit);
            }
            return value is > 0 and < 4000;
        }

        /// <summary>
        /// Moves the cells of rows that have fewer than the table's columns to the columns their text sits in. Some
        /// writers leave empty cells out of the tree, which would otherwise shift the cells after them to the left.
        /// The columns' sides come from the rows that fill every column with cells of their own.
        /// </summary>
        private static void PlaceShortRows(TableBlock table, HashSet<(int, int)> covered)
        {
            var byRow = table.Cells.GroupBy(c => c.Row).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Column).ToList());
            var full = byRow.Values.Where(r => r.Count == table.Columns && r.TrueForAll(c => c.RowSpan == 1 && c.ColumnSpan == 1)).ToList();
            var shortRows = byRow.Values.Where(r => !Enumerable.Range(0, table.Columns).All(x => covered.Contains((r[0].Row, x)))).ToList();
            if (table.Columns > MaxPlacedColumns || full.Count == 0 || shortRows.Count == 0 || table.Cells.Exists(c => c.RowSpan > 1)) return;
            var sides = new (double X0, double X1)[table.Columns];
            Array.Fill(sides, (double.MaxValue, double.MinValue));
            foreach (var c in full.SelectMany(r => r).Where(c => c.Lines.Count > 0))
                sides[c.Column] = (Math.Min(sides[c.Column].X0, c.Lines.Min(l => l.X0)), Math.Max(sides[c.Column].X1, c.Lines.Max(l => l.X1)));
            if (Array.Exists(sides, s => s.X0 == double.MaxValue)) return;
            foreach (var row in shortRows)
            {
                // The cells keep their order; the columns they go to are the ones whose sides their text lines up with
                // best over the whole row, its left side, right side or centre, whichever the cell is aligned by.
                int n = row.Count, columns = table.Columns;
                var cost = new double[n, columns];
                var from = new int[n, columns];
                for (int k = 0; k < n; k++)
                {
                    var cell = row[k];
                    for (int x = 0; x < columns; x++)
                    {
                        cost[k, x] = double.MaxValue;
                        if (x + cell.ColumnSpan > columns) continue;
                        double before = 0;
                        from[k, x] = -1;
                        if (k > 0)
                        {
                            before = double.MaxValue;
                            for (int p = 0; p + row[k - 1].ColumnSpan <= x; p++)
                                if (cost[k - 1, p] < before) (before, from[k, x]) = (cost[k - 1, p], p);
                            if (before == double.MaxValue) continue;
                        }
                        cost[k, x] = before + Misalignment(cell, sides[x], sides[x + cell.ColumnSpan - 1]);
                    }
                }
                int end = -1;
                for (int x = 0; x < columns; x++)
                    if (cost[n - 1, x] < double.MaxValue && (end < 0 || cost[n - 1, x] < cost[n - 1, end])) end = x;
                if (end < 0) continue;
                for (int k = n - 1; k >= 0; k--)
                {
                    row[k].Column = end;
                    end = from[k, end];
                }
            }
        }

        /// <summary>How far a cell's text is from lining up with a column by its left side, right side or centre.</summary>
        private static double Misalignment(TableCell cell, (double X0, double X1) first, (double X0, double X1) last)
        {
            if (cell.Lines.Count == 0) return 0;
            double x0 = cell.Lines.Min(l => l.X0), x1 = cell.Lines.Max(l => l.X1);
            return Math.Min(Math.Min(Math.Abs(x0 - first.X0), Math.Abs(x1 - last.X1)), Math.Abs(x0 + x1 - first.X0 - last.X1) / 2);
        }

        private void Text(List<int> glyphs, TextKind kind, int level)
        {
            var lines = Lines(glyphs);
            if (lines.Count == 0) return;
            var block = new TextBlock { Kind = kind, Level = Math.Clamp(level, 1, 6), Top = lines.Min(l => l.Top), Left = lines.Min(l => l.X0) };
            block.Lines.AddRange(lines);
            Blocks.Add(block);
        }

        /// <summary>The lines of the given glyphs, laid out as the page sets them.</summary>
        private List<FlowLine> Lines(List<int> glyphs)
        {
            if (glyphs.Count == 0) return new List<FlowLine>();
            glyphs.Sort();
            var subset = new List<TextGlyph>(glyphs.Count);
            int previous = -1;
            foreach (int i in glyphs)
                if (i != previous) subset.Add(_glyphs[previous = i]);
            return PageStructure.Lines(TextLayout.Build(subset, _page + 1, _width, _height, true, _cancel), _links);
        }

        private List<int> Gather(StructureTree.Node node)
        {
            var list = new List<int>();
            Gather(node, list, null);
            return list;
        }

        /// <summary>Adds the glyphs of an element and its children on this page, leaving out the children <paramref name="skip"/> names.</summary>
        private void Gather(StructureTree.Node node, List<int> into, Func<StructureTree.Node, bool>? skip)
        {
            foreach (var kid in node.Kids)
            {
                if (kid is StructureTree.Content c) Add(c, into);
                else if (kid is StructureTree.Node n && n.OnPage(_page) && !(skip?.Invoke(n) ?? false)) Gather(n, into, skip);
            }
        }

        private void Add(StructureTree.Content content, List<int> into)
        {
            if (content.Page != _page || !_byMcid.TryGetValue(content.Mcid, out var glyphs)) return;
            foreach (int i in glyphs)
            {
                // Content the tree names twice is read the first time.
                if (Taken[i]) continue;
                Taken[i] = true;
                into.Add(i);
            }
        }
    }
}

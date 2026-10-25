namespace PdfPecker.Rendering.Html;

internal static partial class PageStructure
{
    /// <summary>Rules this close across are one line: a border drawn twice, or the two borders of neighbouring cells.</summary>
    private const double SameLine = 1;

    /// <summary>Rules with a gap this small along them are one line.</summary>
    private const double Join = 2;

    /// <summary>How far a rule may stop short of another and still meet it.</summary>
    private const double Reach = 2;

    /// <summary>
    /// Grid lines this close are one boundary, as in tables whose cells each draw their own border a little apart; grid
    /// lines up to <see cref="Gutter"/> apart with no text between them are one boundary too.
    /// </summary>
    private const double Close = 4, Gutter = 15;

    /// <summary>
    /// More horizontal and vertical lines than this, multiplied, is a drawing such as a map or a chart rather than
    /// tables, and is not searched for them.
    /// </summary>
    private const long MaxCrossings = 4_000_000;

    private readonly record struct Segment(double Pos, double Start, double End);

    /// <summary>
    /// Finds the tables the page outlines with rules: grids of lines that cross, and stacks of horizontal lines between
    /// rows. Their words are marked as used and the tables added to <paramref name="blocks"/>.
    /// </summary>
    private static void FindRuledTables(List<FlowLine> lines, List<PageRule> rules, double body, List<FlowBlock> blocks, CancellationToken cancellationToken)
    {
        var h = Merge(rules.Where(r => r.Horizontal && !r.Edge).Select(r => new Segment(r.Position, r.Start, r.End)));
        var v = Merge(rules.Where(r => !r.Horizontal).Select(r => new Segment(r.Position, r.Start, r.End)));
        if ((long)h.Count * v.Count > MaxCrossings) return;
        var words = lines.SelectMany(l => l.Words).ToList();

        // Lines that cross or meet belong to one grid.
        var parent = new int[h.Count + v.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        for (int i = 0; i < h.Count; i++)
        {
            if ((i & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < v.Count; j++)
            {
                if (v[j].Pos < h[i].Start - Reach || v[j].Pos > h[i].End + Reach || h[i].Pos < v[j].Start - Reach || h[i].Pos > v[j].End + Reach) continue;
                int a = Find(i), b = Find(h.Count + j);
                if (a != b) parent[a] = b;
            }
        }
        var groups = new Dictionary<int, (List<Segment> H, List<Segment> V)>();
        for (int i = 0; i < parent.Length; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = (new List<Segment>(), new List<Segment>());
            if (i < h.Count) g.H.Add(h[i]); else g.V.Add(v[i - h.Count]);
        }

        // Grids, smallest first: a frame around a table, or around a box that holds one, is not a table itself.
        var grids = groups.Values.Where(g => g.H.Count > 0 && g.V.Count > 0).Select(g => (g.H, g.V, Box: Bounds(g.H, g.V)))
            .OrderBy(g => (g.Box.X1 - g.Box.X0) * (g.Box.Y1 - g.Box.Y0)).ToList();
        var found = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (var (gh, gv, box) in grids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Exists(f => f.X0 >= box.X0 - Reach && f.X1 <= box.X1 + Reach && f.Y0 >= box.Y0 - Reach && f.Y1 <= box.Y1 + Reach)) continue;
            var inside = words.FindAll(w => !w.Used && Inside(w, box));
            if (inside.Count == 0) continue;
            var block = Grid(gh, gv, box, inside, body);
            if (block == null) continue;
            foreach (var w in inside) w.Used = true;
            blocks.Add(block);
            found.Add(box);
        }

        // Horizontal lines that meet no vertical one, and the edges of backgrounds: lines between the rows of a table
        // without column lines, or the tops and bottoms of shaded rows.
        var loose = Merge(groups.Values.Where(g => g.V.Count == 0).SelectMany(g => g.H)
            .Concat(rules.Where(r => r.Edge).Select(r => new Segment(r.Position, r.Start, r.End))));
        foreach (var stack in Stacks(loose))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuledRows(stack, words, body, blocks);
        }
    }

    private static bool Inside(FlowWord w, (double X0, double Y0, double X1, double Y1) box)
        => w.CenterX > box.X0 && w.CenterX < box.X1 && w.CenterY > box.Y0 && w.CenterY < box.Y1;

    private static (double X0, double Y0, double X1, double Y1) Bounds(List<Segment> h, List<Segment> v)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var s in h) { x0 = Math.Min(x0, s.Start); x1 = Math.Max(x1, s.End); y0 = Math.Min(y0, s.Pos); y1 = Math.Max(y1, s.Pos); }
        foreach (var s in v) { y0 = Math.Min(y0, s.Start); y1 = Math.Max(y1, s.End); x0 = Math.Min(x0, s.Pos); x1 = Math.Max(x1, s.Pos); }
        return (x0, y0, x1, y1);
    }

    /// <summary>Rules of one direction, with rules on one line that touch or overlap joined into one.</summary>
    private static List<Segment> Merge(IEnumerable<Segment> rules)
    {
        var list = rules.ToList();
        list.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        var result = new List<Segment>();
        for (int i = 0; i < list.Count;)
        {
            int j = i + 1;
            while (j < list.Count && list[j].Pos - list[j - 1].Pos <= SameLine) j++;
            var cluster = list.GetRange(i, j - i);
            double pos = cluster.Average(s => s.Pos);
            cluster.Sort((a, b) => a.Start.CompareTo(b.Start));
            double start = cluster[0].Start, end = cluster[0].End;
            foreach (var s in cluster)
            {
                if (s.Start <= end + Join) { end = Math.Max(end, s.End); continue; }
                result.Add(new Segment(pos, start, end));
                (start, end) = (s.Start, s.End);
            }
            result.Add(new Segment(pos, start, end));
            i = j;
        }
        return result;
    }

    /// <summary>One boundary of a grid: where it lies and the rules that draw it.</summary>
    private sealed class GridLine
    {
        public double Pos;
        public readonly List<Segment> Rules = new();
        public bool Edge; // an outside edge of the grid, which bounds every cell next to it
    }

    /// <summary>The boundaries of a grid in one direction, from its rules across that direction and its outside edges.</summary>
    private static List<GridLine> Boundaries(List<Segment> across, double min, double max, Func<FlowWord, double> centre, List<FlowWord> words)
    {
        var points = across.Select(s => (s.Pos, (Segment?)s)).ToList();
        points.Add((min, null));
        points.Add((max, null));
        points.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        var lines = new List<GridLine>();
        foreach (var (pos, rule) in points)
        {
            if (lines.Count == 0 || pos - lines[^1].Pos > Close) lines.Add(new GridLine { Pos = pos });
            var line = lines[^1];
            if (rule is { } r) line.Rules.Add(r); else line.Edge = true;
        }
        // Neighbouring boundaries with nothing between them, as in tables with space between their cells, are one.
        for (int i = 1; i < lines.Count; i++)
        {
            var a = lines[i - 1];
            var b = lines[i];
            if (b.Pos - a.Pos > Gutter || words.Exists(w => centre(w) > a.Pos && centre(w) < b.Pos)) continue;
            a.Pos = (a.Pos + b.Pos) / 2;
            a.Rules.AddRange(b.Rules);
            a.Edge |= b.Edge;
            lines.RemoveAt(i--);
        }
        foreach (var line in lines)
            if (line.Edge && line != lines[0] && line != lines[^1]) line.Edge = false;
        lines[0].Edge = lines[^1].Edge = true;
        return lines;
    }

    /// <summary>Whether a boundary is drawn along most of the stretch from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static bool Drawn(GridLine line, double from, double to)
    {
        if (line.Edge) return true;
        double covered = 0;
        foreach (var s in line.Rules) covered += Math.Max(0, Math.Min(to, s.End) - Math.Max(from, s.Start));
        return covered >= 0.5 * (to - from);
    }

    /// <summary>
    /// The table a grid of crossing rules outlines, or null when it is not one: cells come from the boundaries drawn
    /// between them, and a cell no boundary divides spans several rows or columns. A grid of one row or one column takes
    /// its other direction from the text, as tables with only row lines or only column lines have.
    /// </summary>
    private static FlowBlock? Grid(List<Segment> h, List<Segment> v, (double X0, double Y0, double X1, double Y1) box, List<FlowWord> words, double body)
    {
        var xs = Boundaries(v, box.X0, box.X1, w => w.CenterX, words);
        var ys = Boundaries(h, box.Y0, box.Y1, w => w.CenterY, words);
        int columns = xs.Count - 1, rows = ys.Count - 1;
        if (columns < 1 || rows < 1 || (columns == 1 && rows == 1)) return null;
        if ((long)columns * rows > 100_000) return null;

        // Cells that no drawn boundary separates are one cell.
        var cell = new int[rows * columns];
        for (int i = 0; i < cell.Length; i++) cell[i] = i;
        int Find(int i)
        {
            while (cell[i] != i) i = cell[i] = cell[cell[i]];
            return i;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) cell[Math.Max(a, b)] = Math.Min(a, b);
        }
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                if (c + 1 < columns && !Drawn(xs[c + 1], ys[r].Pos, ys[r + 1].Pos)) Union(r * columns + c, r * columns + c + 1);
                if (r + 1 < rows && !Drawn(ys[r + 1], xs[c].Pos, xs[c + 1].Pos)) Union(r * columns + c, (r + 1) * columns + c);
            }
        var cells = new Dictionary<int, TableCell>();
        var area = new Dictionary<int, int>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                int root = Find(r * columns + c);
                area[root] = area.GetValueOrDefault(root) + 1;
                if (!cells.TryGetValue(root, out var tc))
                {
                    cells[root] = new TableCell { Row = r, Column = c, X0 = xs[c].Pos, X1 = xs[c + 1].Pos };
                    continue;
                }
                tc.RowSpan = Math.Max(tc.RowSpan, r - tc.Row + 1);
                tc.ColumnSpan = Math.Max(tc.ColumnSpan, c - tc.Column + 1);
                tc.X1 = Math.Max(tc.X1, xs[c + 1].Pos);
            }
        // A cell that is not a rectangle means the lines are a drawing, not a table.
        foreach (var (root, tc) in cells)
            if (area[root] != tc.RowSpan * tc.ColumnSpan) return null;

        if (columns == 1 || rows == 1)
        {
            // Only row lines or only column lines: the text gives the other direction.
            var bands = new List<List<FlowWord>>();
            if (columns == 1)
            {
                for (int r = 0; r < rows; r++) bands.Add(words.FindAll(w => w.CenterY > ys[r].Pos && w.CenterY < ys[r + 1].Pos));
                return RowsTable(bands, null, body, box.X0);
            }
            return ColumnsTable(xs, box, words, body);
        }

        var texts = new Dictionary<int, List<FlowWord>>();
        foreach (var w in words)
        {
            int c = Index(xs, w.CenterX), r = Index(ys, w.CenterY);
            int root = Find(r * columns + c);
            if (!texts.TryGetValue(root, out var list)) texts[root] = list = new List<FlowWord>();
            list.Add(w);
        }
        if (texts.Count < 2) return null;
        if (UnderRuled(texts.Values))
        {
            // The rules outline groups of rows and columns rather than cells, so the rows come from the lines and the
            // columns from the text, as in a table with rules only between some of its rows.
            var stack = ys.Select(y => new Segment(y.Pos, box.X0, box.X1)).ToList();
            var found = new List<FlowBlock>();
            RuledRows(stack, words, body, found);
            return found.Count == 1 ? found[0] : null;
        }
        var table = new TableBlock { Rows = rows, Columns = columns, Top = box.Y0, Left = box.X0 };
        foreach (var (root, tc) in cells)
        {
            if (texts.TryGetValue(root, out var list)) tc.Lines.AddRange(CellLines(list));
            table.Cells.Add(tc);
        }
        return Finish(table);
    }

    /// <summary>
    /// Whether cells hold many rows each: two or more cells of four or more lines whose lines sit on shared baselines,
    /// one of them a column of numbers, as when a table draws lines only around groups of columns. Wrapped text in
    /// neighbouring cells shares baselines too, which is why a column of numbers must show the lines are rows.
    /// </summary>
    private static bool UnderRuled(IEnumerable<List<FlowWord>> cells)
    {
        var tall = cells.Select(CellLines).Where(l => l.Count >= 4).ToList();
        if (tall.Count < 2) return false;
        foreach (var lines in tall)
        {
            if (lines.Count(l => l.Words.TrueForAll(w => IsFigure(w.Text))) < 0.7 * lines.Count) continue;
            int shared = lines.Count(l => tall.Exists(o => o != lines && o.Exists(x => Math.Abs(x.Bottom - l.Bottom) <= 0.25 * l.Size)));
            if (shared >= 0.8 * lines.Count) return true;
        }
        return false;
    }

    /// <summary>Whether a word is a number, an amount or a mark that stands in a column of numbers, such as a dash.</summary>
    private static bool IsFigure(string text)
    {
        bool digit = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c)) digit = true;
            else if (c is not ('.' or ',' or '-' or '–' or '—' or '%' or '(' or ')' or '+' or '$' or '€' or '£' or 'X')) return false;
        }
        return digit || text is "–" or "—" or "-" or "(X)" or "X";
    }

    /// <summary>The index of the band between two boundaries that <paramref name="pos"/> falls in.</summary>
    private static int Index(List<GridLine> lines, double pos)
    {
        int i = 0;
        while (i < lines.Count - 2 && pos >= lines[i + 1].Pos) i++;
        return i;
    }

    /// <summary>
    /// A grid of one row with column lines: a table when the text of its columns sits on shared baselines, which then
    /// make its rows; otherwise boxes side by side, read one after the other.
    /// </summary>
    private static FlowBlock? ColumnsTable(List<GridLine> xs, (double X0, double Y0, double X1, double Y1) box, List<FlowWord> words, double body)
    {
        var parts = new List<List<FlowWord>>();
        for (int c = 0; c + 1 < xs.Count; c++)
            parts.Add(words.FindAll(w => w.CenterX >= xs[c].Pos && w.CenterX < xs[c + 1].Pos));
        var lines = parts.Select(p => CellLines(p)).ToList();
        int filled = lines.Count(l => l.Count > 0);
        if (filled < 2) return null;
        // Baselines shared by text in two or more columns make rows.
        int shared = 0, total = 0;
        for (int c = 0; c < lines.Count; c++)
            foreach (var line in lines[c])
            {
                total++;
                bool match = false;
                for (int d = 0; d < lines.Count && !match; d++)
                    if (d != c) match = lines[d].Exists(o => Math.Abs(o.Bottom - line.Bottom) <= 0.25 * line.Size);
                if (match) shared++;
            }
        if (total >= 4 && shared >= 0.8 * total)
        {
            var rows = BaselineRows(words);
            var table = new TableBlock { Columns = xs.Count - 1, Top = box.Y0, Left = box.X0 };
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c + 1 < xs.Count; c++)
                {
                    var tc = new TableCell { Row = r, Column = c, X0 = xs[c].Pos, X1 = xs[c + 1].Pos };
                    tc.Lines.AddRange(CellLines(rows[r].FindAll(w => w.CenterX >= xs[c].Pos && w.CenterX < xs[c + 1].Pos)));
                    table.Cells.Add(tc);
                }
            table.Rows = rows.Count;
            return Finish(table);
        }
        var group = new GroupBlock { Top = box.Y0, Left = box.X0 };
        foreach (var part in lines)
            if (part.Count > 0) group.Parts.Add(Flow(part, body, columns: false));
        return group;
    }

    /// <summary>
    /// Words grouped into rows by baseline, from the top. A word takes the baseline of the largest text of its line, so a
    /// raised footnote mark stays in the row of the text it belongs to.
    /// </summary>
    private static List<List<FlowWord>> BaselineRows(List<FlowWord> words)
    {
        var baseline = new Dictionary<int, FlowWord>();
        foreach (var w in words)
            if (!baseline.TryGetValue(w.Line, out var b) || w.Size > b.Size) baseline[w.Line] = w;
        var sorted = words.OrderBy(w => baseline[w.Line].Bottom).ToList();
        var rows = new List<List<FlowWord>>();
        double last = double.MinValue, size = 0;
        foreach (var w in sorted)
        {
            var b = baseline[w.Line];
            if (rows.Count == 0 || b.Bottom - last > 0.25 * Math.Max(size, b.Size)) { rows.Add(new List<FlowWord>()); size = 0; }
            rows[^1].Add(w);
            last = b.Bottom;
            size = Math.Max(size, b.Size);
        }
        return rows;
    }

    /// <summary>A cell's words as lines, from the top, each in reading order.</summary>
    private static List<FlowLine> CellLines(List<FlowWord> words)
    {
        var lines = new List<FlowLine>();
        foreach (var g in words.GroupBy(w => w.Line))
        {
            var list = g.OrderBy(w => w.Order).ToList();
            // Words of one text line that a cell boundary does not split but that lie far apart, as when two columns
            // share a baseline, stay in one line; the line keeps their reading order.
            lines.Add(new FlowLine(list, g.Key, false));
        }
        lines.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.X0.CompareTo(b.X0));
        return lines;
    }

    /// <summary>
    /// Runs of horizontal rules of the same length, one under the other, such as lines between table rows. Stacks of
    /// three or more are kept.
    /// </summary>
    private static List<List<Segment>> Stacks(List<Segment> rules)
    {
        var stacks = new List<List<Segment>>();
        foreach (var r in rules)
        {
            var stack = stacks.Find(s => Math.Abs(s[^1].Start - r.Start) <= 3 && Math.Abs(s[^1].End - r.End) <= 3
                && r.Pos - s[^1].Pos is >= Close and <= 100);
            if (stack == null) stacks.Add(stack = new List<Segment>());
            stack.Add(r);
        }
        // A gap much wider than the others, such as the space above a table, is not a row of it.
        var result = new List<List<Segment>>();
        foreach (var stack in stacks)
        {
            if (stack.Count < 3) continue;
            var gaps = stack.Zip(stack.Skip(1), (a, b) => b.Pos - a.Pos).OrderBy(d => d).ToList();
            double usual = gaps[gaps.Count / 2];
            var part = new List<Segment> { stack[0] };
            for (int i = 1; i < stack.Count; i++)
            {
                if (stack[i].Pos - stack[i - 1].Pos > 3 * usual)
                {
                    if (part.Count >= 3) result.Add(part);
                    part = new List<Segment>();
                }
                part.Add(stack[i]);
            }
            if (part.Count >= 3) result.Add(part);
        }
        return result;
    }

    /// <summary>
    /// The tables a stack of row rules outlines, with their columns from the text. Each band between the rules is a row,
    /// except in tables with rules only above and below the header and at the end, whose few bands hold many lines: then
    /// the lines are the rows. Rows sit the same distance below the rule above them and above the rule below them, so a
    /// band whose text does not, such as the space between a table and the text after it, ends the table. A row just
    /// above the first rule or just below the last, set the same way, belongs to the table too.
    /// </summary>
    private static void RuledRows(List<Segment> stack, List<FlowWord> words, double body, List<FlowBlock> blocks)
    {
        double x0 = stack.Min(s => s.Start), x1 = stack.Max(s => s.End);
        bool Within(FlowWord w) => !w.Used && w.CenterX > x0 - 1 && w.CenterX < x1 + 1;
        var bands = new List<List<FlowWord>>();
        for (int i = 0; i + 1 < stack.Count; i++)
            bands.Add(words.FindAll(w => Within(w) && w.CenterY > stack[i].Pos && w.CenterY < stack[i + 1].Pos));
        if (!bands.Exists(b => b.Count > 0)) return;

        // How far text sits from the rule above and below it within a band.
        var above = new double[bands.Count];
        var below = new double[bands.Count];
        for (int i = 0; i < bands.Count; i++)
        {
            if (bands[i].Count == 0) continue;
            above[i] = bands[i].Min(w => w.Top) - stack[i].Pos;
            below[i] = stack[i + 1].Pos - bands[i].Max(w => w.Bottom);
        }
        var filled = Enumerable.Range(0, bands.Count).Where(i => bands[i].Count > 0).ToList();
        double usualAbove = filled.Select(i => above[i]).OrderBy(d => d).ElementAt(filled.Count / 2);
        double usualBelow = filled.Select(i => below[i]).OrderBy(d => d).ElementAt(filled.Count / 2);
        double height = filled.Select(i => stack[i + 1].Pos - stack[i].Pos).OrderBy(d => d).ElementAt(filled.Count / 2);
        // With only a few bands, as in tables with rules only around the header and at the end, there is nothing to compare.
        // A band of wrapped rows is taller than the others, so only its top or its bottom sits as the others' do.
        double usualSize = filled.Select(i => bands[i].Max(w => w.Size)).OrderBy(d => d).ElementAt(filled.Count / 2);
        bool Fits(int i) => filled.Count <= 3 || (Math.Abs(above[i] - usualAbove) <= 3 && Math.Abs(below[i] - usualBelow) <= 3)
            || ((Math.Abs(above[i] - usualAbove) <= 3 || Math.Abs(below[i] - usualBelow) <= 3)
                && bands[i].Max(w => w.Size) <= 1.1 * usualSize);

        for (int i = 0; i < bands.Count;)
        {
            // An empty band no taller than a rule's thickness, as between a border and a background's edge, keeps a run going.
            if (bands[i].Count == 0 || !Fits(i)) { i++; continue; }
            int j = i;
            var rows = new List<List<FlowWord>>();
            var groups = new List<int>();
            while (j < bands.Count && ((bands[j].Count > 0 && Fits(j)) || (bands[j].Count == 0 && stack[j + 1].Pos - stack[j].Pos <= Close)))
            {
                if (bands[j].Count > 0)
                {
                    // A band with two or more lines of several cells holds several rows, one to a line, as the sections
                    // of a receipt between its dashed lines do; otherwise it is one row, whose cells may wrap.
                    var lines = BaselineRows(bands[j]);
                    if (lines.Count(l => Chunks(l).Count >= 2) >= 2 && !Wraps(lines))
                        foreach (var line in lines) { rows.Add(line); groups.Add(j); }
                    else { rows.Add(bands[j]); groups.Add(-1); }
                }
                j++;
            }
            double first = stack[i].Pos, last = stack[j].Pos;
            // A header above the first rule, set as close to it as the rows' text is to the rule below them, may have
            // lines above its first that wrap its cells.
            var aboveFirst = BaselineRows(words.FindAll(w => Within(w) && w.Bottom < first + 1 && w.Top > first - 4 * height));
            if (aboveFirst.Count > 0 && first - aboveFirst[^1].Max(w => w.Bottom) is var under && Math.Abs(under - usualBelow) <= 3 && under > -1
                && aboveFirst[^1].Max(w => w.Size) <= 1.1 * usualSize)
            {
                var top = aboveFirst[^1];
                for (int k = aboveFirst.Count - 2; k >= 0; k--)
                {
                    var line = aboveFirst[k];
                    if (top.Min(w => w.Top) - line.Max(w => w.Bottom) > 0.6 * line.Max(w => w.Size)) break;
                    top = line.Concat(top).ToList();
                }
                if (Columns(rows.Prepend(top).ToList(), body) is { } withTop && RowCells(top, withTop, 0).Count(c => c.Lines.Count > 0) >= 2)
                {
                    rows.Insert(0, top);
                    groups.Insert(0, -1);
                }
            }
            var bottom = words.FindAll(w => Within(w) && w.Top > last - 1 && w.Bottom < last + 1.5 * height);
            if (bottom.Count > 0 && Math.Abs(bottom.Min(w => w.Top) - last - usualAbove) <= 3 && BaselineRows(bottom).Count == 1 && bottom.Max(w => w.Size) <= 1.1 * usualSize
                && Columns(rows.Append(bottom).ToList(), body) is { } withBottom && RowCells(bottom, withBottom, 0).Count(c => c.Lines.Count > 0) >= 2)
            {
                rows.Add(bottom);
                groups.Add(-1);
            }
            if (RowsTable(rows, groups, body, x0) is { } table) blocks.Add(table);
            i = j;
        }
    }

    /// <summary>
    /// Whether lines side by side are wrapped running text, such as cards in a row: in two or more columns, text in one
    /// line goes on in lower case on the next, starting where it starts. A label wrapping in one column is not.
    /// </summary>
    private static bool Wraps(List<List<FlowWord>> lines)
    {
        var starts = new List<double>();
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            var next = Chunks(lines[i + 1]);
            foreach (var chunk in Chunks(lines[i]))
                if (next.Exists(n => Math.Abs(n[0].X0 - chunk[0].X0) <= AlignTolerance && n[0].Text is { Length: > 0 } t && char.IsLower(t[0]))
                    && !starts.Exists(x => Math.Abs(x - chunk[0].X0) <= AlignTolerance))
                    starts.Add(chunk[0].X0);
        }
        return starts.Count >= 2;
    }

    /// <summary>
    /// A table of given rows whose columns come from how the text lines up, or null when it does not line up in two or
    /// more columns. Rows of one group, the lines of one band, that continue the row above are joined to it; rows not in
    /// a group, -1 or no groups at all, never are.
    /// </summary>
    private static TableBlock? RowsTable(List<List<FlowWord>> rows, List<int>? groups, double body, double x0)
    {
        if (groups != null) groups = groups.Where((_, i) => rows[i].Count > 0).ToList();
        rows = rows.Where(r => r.Count > 0).ToList();
        if (rows.Count < 2) return null;
        var bands = Columns(rows, body);
        if (bands == null) return null;
        var table = new TableBlock { Columns = bands.Count, Top = rows.Min(r => r.Min(w => w.Top)), Left = x0 };
        var cells = rows.Select((r, i) => RowCells(r, bands, i)).ToList();
        if (Stacked(cells, bands) is { } stacked)
        {
            table.Rows = 2;
            table.Cells.AddRange(cells[0]);
            table.Cells.AddRange(stacked);
            foreach (var w in rows.SelectMany(r => r)) w.Used = true;
            return Finish(table);
        }
        AddRows(table, cells, groups, bands);
        if (table.Cells.Count(c => c.Lines.Count > 0) < 3) return null;
        // A row or two of cells of running text, such as cards side by side, is text to read rather than a table.
        if (table.Rows <= 2 && table.Cells.Where(c => c.Lines.Count > 0).Average(c => c.Lines.Sum(l => l.Words.Count)) >= 8) return null;
        // The rows are certain here, but the columns come from the text; a column of three or more entries must line up.
        for (int c = 0; c < bands.Count; c++)
        {
            var entries = table.Cells.FindAll(t => t.Column == c && t.ColumnSpan == 1 && t.Lines.Count > 0);
            if (entries.Count >= 3 && !LinedUp(entries)) return null;
        }
        foreach (var w in rows.SelectMany(r => r)) w.Used = true;
        SplitHeader(table, bands);
        SpanHeadings(table);
        return Finish(table);
    }

    /// <summary>
    /// Splits a first row of stacked lines into two header rows where a line labels a group of columns, such as
    /// "Revenue" over "Medical" and "Family": the label spans its columns in the first row and the text under it fills
    /// them in the second, while the cells of the other columns span both rows.
    /// </summary>
    private static void SplitHeader(TableBlock table, List<Band> bands)
    {
        if (bands.Count < 3) return;
        for (int row = 0; row < Math.Min(2, table.Rows - 1); row++)
            if (SplitHeader(table, bands, row)) return;
    }

    private static bool SplitHeader(TableBlock table, List<Band> bands, int row)
    {
        var header = table.Cells.Where(c => c.Row == row).SelectMany(c => c.Lines).SelectMany(l => l.Words).ToList();
        var lines = BaselineRows(header);
        if (lines.Count < 2) return false;
        // Where each column's words start and end: halfway to the columns beside it.
        double Left(int c) => c == 0 ? double.MinValue : (bands[c - 1].X1 + bands[c].X0) / 2;
        double Right(int c) => c == bands.Count - 1 ? double.MaxValue : (bands[c].X1 + bands[c + 1].X0) / 2;
        for (int i = 0; i + 1 < lines.Count; i++)
        {
            var below = lines.Skip(i + 1).SelectMany(l => l).ToList();
            bool Under(int c) => below.Exists(w => w.CenterX > Left(c) && w.CenterX < Right(c));
            // A label over a group of columns crosses into two or more of them, or sits centred over them.
            var groups = new List<(int Lo, int Hi)>();
            foreach (var chunk in Chunks(lines[i]))
            {
                // A label of a group is a word or a few, not a sentence that runs across columns.
                if (chunk.Count > 4) continue;
                double x0 = chunk.Min(w => w.X0), x1 = chunk.Max(w => w.X1), mid = (x0 + x1) / 2;
                for (int lo = 0; lo < bands.Count; lo++)
                    for (int hi = lo + 1; hi < bands.Count; hi++)
                    {
                        if (x0 < Left(lo) || x1 > Right(hi) || groups.Exists(g => g.Lo <= hi && lo <= g.Hi)) continue;
                        bool across = x0 < bands[lo].X1 && x1 > bands[hi].X0 && hi == lo + 1;
                        bool centred = Math.Abs((bands[lo].X0 + bands[hi].X1) / 2 - mid) <= 1.5 * AlignTolerance;
                        if ((across || centred) && Enumerable.Range(lo, hi - lo + 1).Count(Under) >= 2) groups.Add((lo, hi));
                    }
            }
            if (groups.Count == 0) continue;
            double labelBottom = lines[i].Max(x => x.Bottom) + 0.5;
            int GroupOf(FlowWord w) => groups.FindIndex(g => w.CenterX > Left(g.Lo) && w.CenterX < Right(g.Hi));
            var labels = groups.Select(_ => new List<FlowWord>()).ToList();
            var under = new List<FlowWord>();
            var others = new List<FlowWord>();
            foreach (var w in header)
            {
                int g = GroupOf(w);
                if (g < 0) others.Add(w);
                else if (w.Bottom <= labelBottom) labels[g].Add(w);
                else under.Add(w);
            }
            var cells = new List<TableCell>();
            for (int g = 0; g < groups.Count; g++)
            {
                var (lo, hi) = groups[g];
                var cell = new TableCell { Row = row, Column = lo, ColumnSpan = hi - lo + 1, X0 = bands[lo].X0, X1 = bands[hi].X1 };
                cell.Lines.AddRange(CellLines(labels[g]));
                cells.Add(cell);
            }
            foreach (var c in RowCells(others, bands, row))
            {
                if (groups.Exists(g => g.Lo < c.Column + c.ColumnSpan && c.Column <= g.Hi)) return false;
                c.RowSpan = 2;
                cells.Add(c);
            }
            var second = RowCells(under, bands, row + 1);
            if (second.Exists(c => c.ColumnSpan > 1 || !groups.Exists(g => c.Column >= g.Lo && c.Column + c.ColumnSpan - 1 <= g.Hi))) return false;
            cells.AddRange(second);
            table.Cells.RemoveAll(c => c.Row == row);
            foreach (var c in table.Cells)
                if (c.Row > row) c.Row++;
            table.Cells.AddRange(cells);
            table.Cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
            table.Rows++;
            table.HeaderRows = row + 2;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Marks the header rows: the first row, or the rows of a header split into levels, when their text is bold and the
    /// text of the other rows is not all bold. A cell may span rows within the header but not out of it.
    /// </summary>
    private static TableBlock Finish(TableBlock table)
    {
        int rows = Math.Max(1, table.HeaderRows);
        // A header cell spanning rows, as over a header split into levels, takes the rows it spans into the header.
        for (bool grew = true; grew;)
        {
            int reach = table.Cells.Where(c => c.Row < rows).Select(c => c.Row + c.RowSpan).DefaultIfEmpty(rows).Max();
            grew = reach > rows;
            rows = Math.Max(rows, reach);
        }
        var head = table.Cells.Where(c => c.Row < rows && c.Lines.Count > 0).ToList();
        var rest = table.Cells.Where(c => c.Row >= rows).SelectMany(c => c.Lines).SelectMany(l => l.Words).ToList();
        bool header = head.Count > 0 && table.Rows > rows && head.TrueForAll(c => c.Lines.TrueForAll(l => l.Words.TrueForAll(w => w.Bold)))
            && rest.Exists(w => !w.Bold) && !table.Cells.Exists(c => c.Row < rows && c.Row + c.RowSpan > rows);
        table.HeaderRows = header ? rows : 0;
        SetDirection(table);
        return table;
    }

    /// <summary>Sets a table right-to-left when most of its lines are, and its lines with it.</summary>
    private static void SetDirection(TableBlock table)
    {
        var all = table.Cells.SelectMany(c => c.Lines).ToList();
        table.RightToLeft = all.Count > 0 && all.Count(l => l.Words.Exists(w => IsRightToLeft(w.Text))) > all.Count / 2;
        if (table.RightToLeft)
            foreach (var c in table.Cells)
                for (int i = 0; i < c.Lines.Count; i++) c.Lines[i] = new FlowLine(c.Lines[i].Words, c.Lines[i].Id, true);
    }

    private static bool IsRightToLeft(string text)
    {
        foreach (char c in text)
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
        return false;
    }
}

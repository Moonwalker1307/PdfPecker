using System.Runtime.InteropServices;

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
    /// The facing sides of the boxes of neighbouring cells, which each draw their own border, are at most this far apart:
    /// the border spacing and one border's width, up to some 10px of spacing.
    /// </summary>
    private const double Spacing = 10;

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
        var all = CollectionsMarshal.AsSpan(rules);
        int downs = 0, edges = 0;
        foreach (ref readonly var r in all)
        {
            if (!r.Horizontal) downs++;
            else if (r.Edge) edges++;
        }
        var across = new List<Segment>(all.Length - downs - edges);
        var down = new List<Segment>(downs);
        foreach (ref readonly var r in all)
        {
            if (!r.Horizontal) down.Add(new Segment(r.Position, r.Start, r.End));
            else if (!r.Edge) across.Add(new Segment(r.Position, r.Start, r.End));
        }
        var h = Merge(across);
        var v = Merge(down);
        if ((long)h.Count * v.Count > MaxCrossings) return;
        int count = 0;
        foreach (var l in lines) count += l.Words.Count;
        var words = new List<FlowWord>(count);
        foreach (var l in lines) words.AddRange(l.Words);

        // Lines that cross or meet belong to one grid.
        var parent = new int[h.Count + v.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b) parent[a] = b;
        }
        for (int i = 0; i < h.Count; i++)
        {
            if ((i & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < v.Count; j++)
            {
                if (v[j].Pos < h[i].Start - Reach || v[j].Pos > h[i].End + Reach || h[i].Pos < v[j].Start - Reach || h[i].Pos > v[j].End + Reach) continue;
                Union(i, h.Count + j);
            }
        }
        var strict = Roots();
        var groups = Groups(i => strict[i]);

        // Tables with separate borders, the CSS default, draw each cell's box on its own, the border spacing and a border's
        // width from the next: the facing sides of two cells of a row run the same length side by side a few points apart,
        // as do those of two cells of a column. Such sides join the boxes into one grid. Boxes side by side or one under
        // another are as often cards or panels, so a joined grid, and each grid it joins, is a table only with two rows
        // and two columns or more; a row of cells whose lines meet is read with the rest of its table.
        long work = 0;
        Pair(h, 0, wider: false);
        Pair(v, h.Count, wider: false);
        var close = Roots();
        var joined = Holds(strict, close);
        var separate = Groups(i => joined[close[i]] == -1 ? close[i] : -1);

        // Wider border spacing sets the facing sides further apart, as far as the gap between cards in a grid or between
        // the fields in a table's cells. Boxes joined over such gaps make a grid of their own, tried after all the grids
        // above, so a table around such fields is read whole, and only where its boxes fill the grid and hold short
        // entries, as a table's cells do, rather than the title and lines of text of a card.
        Pair(h, 0, wider: true);
        Pair(v, h.Count, wider: true);
        var wide = Roots();
        var joinedWide = Holds(close, wide);
        var spaced = Groups(i => joinedWide[wide[i]] == -1 ? wide[i] : -1);

        // Grids, smallest first: a frame around a table, or around a box that holds one, is not a table itself.
        static IEnumerable<(List<Segment> H, List<Segment> V, bool Joined, bool Spaced, (double X0, double Y0, double X1, double Y1) Box)> BySize(
            IEnumerable<(List<Segment> H, List<Segment> V, bool Joined, bool Spaced)> list)
            => list.Where(g => g.H.Count > 0 && g.V.Count > 0).Select(g => (g.H, g.V, g.Joined, g.Spaced, Box: Bounds(g.H, g.V)))
                .OrderBy(g => (g.Box.X1 - g.Box.X0) * (g.Box.Y1 - g.Box.Y0));
        var grids = BySize(groups.Select(e => (e.Value.H, e.Value.V, Joined: joined[close[e.Key]] == -1, Spaced: false))
                .Concat(separate.Values.Select(g => (g.H, g.V, Joined: true, Spaced: false))))
            .Concat(BySize(spaced.Values.Select(g => (g.H, g.V, Joined: true, Spaced: true)))).ToList();
        var found = new List<(double X0, double Y0, double X1, double Y1)>();
        foreach (var (gh, gv, boxes, wideApart, box) in grids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Exists(f => f.X0 >= box.X0 - Reach && f.X1 <= box.X1 + Reach && f.Y0 >= box.Y0 - Reach && f.Y1 <= box.Y1 + Reach)) continue;
            var inside = new List<FlowWord>();
            foreach (var w in words)
                if (!w.Used && Inside(w, box)) inside.Add(w);
            if (inside.Count == 0) continue;
            // Cells that each draw their own border draw every column boundary, so a grid joined over wider spacing has
            // none to find from breaks, and the dashes of its frames' borders would read as such breaks.
            var breaks = wideApart ? new List<Segment>() : Breaks(rules, gh, gv, box, inside);
            var block = Grid(gh, gv.Concat(breaks).ToList(), box, inside, body, boxes, wideApart);
            // Columns drawn only by breaks in the row lines say nothing of a row whose one bold label spans them.
            if (breaks.Count > 0 && block is TableBlock broken) SpanHeadings(broken);
            if (block == null) continue;
            foreach (var w in inside) w.Used = true;
            blocks.Add(block);
            found.Add(box);
        }

        // Horizontal lines that meet no vertical one, and the edges of backgrounds: lines between the rows of a table
        // without column lines, or the tops and bottoms of shaded rows.
        var loose = new List<Segment>();
        foreach (var g in groups.Values)
            if (g.V.Count == 0) loose.AddRange(g.H);
        foreach (var r in rules)
            if (r.Edge) loose.Add(new Segment(r.Position, r.Start, r.End));
        loose = Merge(loose);
        foreach (var stack in Stacks(loose))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuledRows(stack, words, body, blocks);
        }

        // The lines of each group, by the group a line's index maps to; -1 leaves a line out.
        Dictionary<int, (List<Segment> H, List<Segment> V)> Groups(Func<int, int> group)
        {
            var result = new Dictionary<int, (List<Segment> H, List<Segment> V)>();
            for (int i = 0; i < parent.Length; i++)
            {
                int root = group(i);
                if (root < 0) continue;
                if (!result.TryGetValue(root, out var g)) result[root] = g = (new List<Segment>(), new List<Segment>());
                if (i < h.Count) g.H.Add(h[i]); else g.V.Add(v[i - h.Count]);
            }
            return result;
        }

        // Joins lines of one direction, sorted by position, that run the same length up to Close apart, or wider than
        // that up to Spacing. A page of so many lines that comparing them takes long is a drawing, and its lines stay as
        // they are.
        void Pair(List<Segment> list, int offset, bool wider)
        {
            double max = wider ? Spacing : Close;
            for (int i = 0; i < list.Count; i++)
            {
                if ((i & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int j = i + 1; j < list.Count && list[j].Pos - list[i].Pos <= max; j++)
                {
                    if (++work > MaxCrossings) return;
                    if ((!wider || list[j].Pos - list[i].Pos > Close) && Math.Abs(list[j].Start - list[i].Start) <= SameLine
                        && Math.Abs(list[j].End - list[i].End) <= SameLine)
                        Union(offset + i, offset + j);
                }
            }
        }

        int[] Roots()
        {
            var roots = new int[parent.Length];
            for (int i = 0; i < parent.Length; i++) roots[i] = Find(i);
            return roots;
        }

        // For each group of outer, the one group of inner it holds, or -1 once it holds two.
        static Dictionary<int, int> Holds(int[] inner, int[] outer)
        {
            var holds = new Dictionary<int, int>();
            for (int i = 0; i < outer.Length; i++)
            {
                if (!holds.TryGetValue(outer[i], out int first)) holds[outer[i]] = inner[i];
                else if (first >= 0 && first != inner[i]) holds[outer[i]] = -1;
            }
            return holds;
        }
    }

    /// <summary>
    /// Column boundaries a grid shows only by where its row lines are broken: some tables draw each row's line cell by
    /// cell and no lines between the columns. A place inside the grid where most of its row lines have a piece end and
    /// the next start is such a boundary.
    /// </summary>
    private static List<Segment> Breaks(List<PageRule> rules, List<Segment> h, List<Segment> v, (double X0, double Y0, double X1, double Y1) box,
        List<FlowWord> words)
    {
        var breaks = new List<Segment>();
        // Only a grid with no vertical line inside it that runs down most of it, such as one only in the header: where
        // there are such lines, the breaks are where rows meet them. Checked first, as most grids have such lines and
        // finding the ends compares every row line with every other.
        if (h.Count < 3 || v.Exists(s => s.Pos > box.X0 + Close && s.Pos < box.X1 - Close && s.End - s.Start >= 0.5 * (box.Y1 - box.Y0)))
            return breaks;
        var inBox = rules.FindAll(r => r.Horizontal && !r.Edge && r.Position >= box.Y0 - Reach && r.Position <= box.Y1 + Reach);
        // A line is broken at a place no piece of it, or of a line drawn over it, runs across.
        bool Broken(double x, double y) => !inBox.Exists(r => Math.Abs(r.Position - y) <= SameLine && r.Start < x - Join && r.End > x + Join);
        // A line that ends where a side of its own starts or ends is the top or bottom of a box inside the grid, such as a
        // form field's, rather than a row line broken at a column. The grid's own lines are not such sides.
        List<PageRule>? sides = null;
        bool Corner(double x, double y)
        {
            sides ??= rules.FindAll(r => !r.Horizontal && r.Position > box.X0 + Close && r.Position < box.X1 - Close
                && r.End >= box.Y0 - Reach && r.Start <= box.Y1 + Reach);
            return sides.Exists(r => Math.Abs(r.Position - x) <= Reach && (Math.Abs(r.Start - y) <= Reach || Math.Abs(r.End - y) <= Reach))
                && !v.Exists(s => Math.Abs(s.Pos - x) <= SameLine && s.Start <= y + Reach && s.End >= y - Reach);
        }
        var ends = new List<(double X, double Y)>();
        foreach (var r in inBox)
        {
            if (r.Start > box.X0 + Close && r.Start < box.X1 - Close && Broken(r.Start, r.Position) && !Corner(r.Start, r.Position)) ends.Add((r.Start, r.Position));
            if (r.End > box.X0 + Close && r.End < box.X1 - Close && Broken(r.End, r.Position) && !Corner(r.End, r.Position)) ends.Add((r.End, r.Position));
        }
        if (ends.Count == 0) return breaks;
        ends.Sort((a, b) => a.X.CompareTo(b.X));
        // Row lines that cross the whole grid, which a column boundary must break.
        int across = h.Count(s => s.Start <= box.X0 + Close && s.End >= box.X1 - Close);
        // The row lines, one per place, with the grid's top and bottom: tables with collapsed borders draw the lines
        // above and below two rows together.
        var rows = new List<double>();
        foreach (double y in h.Select(s => s.Pos).Append(box.Y0).Append(box.Y1).OrderBy(y => y))
            if (rows.Count == 0 || y - rows[^1] > Close) rows.Add(y);
        for (int i = 0; i < ends.Count;)
        {
            int j = i + 1;
            while (j < ends.Count && ends[j].X - ends[i].X <= SameLine) j++;
            var at = ends.GetRange(i, j - i);
            int lines = at.Select(e => Math.Round(e.Y / SameLine)).Distinct().Count();
            if (across >= 3 && lines >= 3 && lines * 2 >= across)
            {
                // Some writers break every row line at every column, so the breaks say nothing of a cell that spans
                // columns: the boundary runs down each row except those whose text crosses it, with a word across it or
                // words on either side no further apart than words within a cell.
                double x = at.Average(e => e.X);
                for (int k = 0; k + 1 < rows.Count; k++)
                {
                    double top = rows[k], bottom = rows[k + 1];
                    var band = words.FindAll(w => w.CenterY > top && w.CenterY < bottom);
                    bool crossed = band.Exists(w => w.X0 < x - 1 && w.X1 > x + 1)
                        || band.Exists(a => a.X1 <= x + 1 && band.Exists(b => b.X0 >= x - 1 && b.X0 >= a.X1 - 1 && b.X0 - a.X1 < CellGap * a.Size
                            && Math.Abs(b.Bottom - a.Bottom) <= 0.3 * a.Size));
                    if (!crossed) breaks.Add(new Segment(x, top, bottom));
                }
            }
            i = j;
        }
        return breaks;
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
    private static List<Segment> Merge(List<Segment> list)
    {
        var result = new List<Segment>();
        if (!AnyOrder(list))
        {
            MergeSorted(list, result);
            return result;
        }
        // With no ties that could come out differently, any sort gives the same lines, and sorting the places as
        // numbers is several times faster than sorting through a comparison. The rules are sorted in place, as the list
        // is the caller's copy.
        var items = CollectionsMarshal.AsSpan(list);
        var keys = new double[items.Length];
        for (int k = 0; k < items.Length; k++) keys[k] = items[k].Pos;
        keys.AsSpan().Sort(items);
        for (int i = 0; i < items.Length;)
        {
            int j = i + 1;
            while (j < items.Length && keys[j] - keys[j - 1] <= SameLine) j++;
            double sum = 0;
            for (int k = i; k < j; k++) sum += keys[k];
            double pos = sum / (j - i);
            if (j - i > 1)
            {
                for (int k = i; k < j; k++) keys[k] = items[k].Start;
                keys.AsSpan(i, j - i).Sort(items.Slice(i, j - i));
            }
            AddJoined(items, i, j, pos, result);
            i = j;
        }
        return result;
    }

    /// <summary>
    /// Whether the order of rules that sort the same cannot change what <see cref="Merge"/> makes of them: every place
    /// is a finite number and every rule runs forwards, so rules on one line in either order join the same, and no
    /// start is a negative zero, which sorts with zero but would be kept as it is.
    /// </summary>
    private static bool AnyOrder(List<Segment> list)
    {
        foreach (var s in list)
            if (!double.IsFinite(s.Pos) || double.IsNaN(s.Start) || double.IsNaN(s.End) || s.End + Join < s.Start
                || (s.Start == 0 && double.IsNegative(s.Start)))
                return false;
        return true;
    }

    /// <summary>
    /// The joining for any rules, damaged ones included, with the sorts whose order of ties the lines can depend on.
    /// </summary>
    private static void MergeSorted(List<Segment> list, List<Segment> result)
    {
        list.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        var items = CollectionsMarshal.AsSpan(list);
        for (int i = 0; i < list.Count;)
        {
            int j = i + 1;
            while (j < list.Count && list[j].Pos - list[j - 1].Pos <= SameLine) j++;
            // Sorting the cluster in place leaves the clusters after it as they are.
            double sum = 0;
            for (int k = i; k < j; k++) sum += list[k].Pos;
            double pos = sum / (j - i);
            list.Sort(i, j - i, StartOrder.Instance);
            AddJoined(items, i, j, pos, result);
            i = j;
        }
    }

    /// <summary>
    /// Adds the rules of one line, from <paramref name="i"/> to <paramref name="j"/> sorted by where they start, joined
    /// where they touch.
    /// </summary>
    private static void AddJoined(Span<Segment> items, int i, int j, double pos, List<Segment> result)
    {
        double start = items[i].Start, end = items[i].End;
        for (int k = i; k < j; k++)
        {
            var s = items[k];
            if (s.Start <= end + Join) { end = Math.Max(end, s.End); continue; }
            result.Add(new Segment(pos, start, end));
            (start, end) = (s.Start, s.End);
        }
        result.Add(new Segment(pos, start, end));
    }

    private sealed class StartOrder : IComparer<Segment>
    {
        public static readonly StartOrder Instance = new();
        public int Compare(Segment a, Segment b) => a.Start.CompareTo(b.Start);
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
    private static bool Drawn(GridLine line, double from, double to, bool edges = true)
    {
        if (line.Edge && edges) return true;
        double covered = 0;
        foreach (var s in line.Rules) covered += Math.Max(0, Math.Min(to, s.End) - Math.Max(from, s.Start));
        return covered >= 0.5 * (to - from);
    }

    /// <summary>
    /// The table a grid of crossing rules outlines, or null when it is not one: cells come from the boundaries drawn
    /// between them, and a cell no boundary divides spans several rows or columns. A grid of one row or one column takes
    /// its other direction from the text, as tables with only row lines or only column lines have. A grid of
    /// <paramref name="boxes"/>, cells that each draw their own border, or a part of one needs two rows and two columns.
    /// </summary>
    private static FlowBlock? Grid(List<Segment> h, List<Segment> v, (double X0, double Y0, double X1, double Y1) box, List<FlowWord> words, double body,
        bool boxes = false, bool spaced = false)
    {
        var xs = Boundaries(v, box.X0, box.X1, w => w.CenterX, words);
        var ys = Boundaries(h, box.Y0, box.Y1, w => w.CenterY, words);
        int columns = xs.Count - 1, rows = ys.Count - 1;
        if (columns < 1 || rows < 1 || (columns == 1 && rows == 1) || (boxes && (columns == 1 || rows == 1))) return null;
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
        // Each cell under its root, the first of its places from the top left, which is also where the scan meets it
        // first, so the cells are in the order the scan finds them.
        var cells = new TableCell?[rows * columns];
        var area = new int[rows * columns];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                int root = Find(r * columns + c);
                area[root]++;
                if (cells[root] is not { } tc)
                {
                    cells[root] = new TableCell { Row = r, Column = c, X0 = xs[c].Pos, X1 = xs[c + 1].Pos };
                    continue;
                }
                tc.RowSpan = Math.Max(tc.RowSpan, r - tc.Row + 1);
                tc.ColumnSpan = Math.Max(tc.ColumnSpan, c - tc.Column + 1);
                tc.X1 = Math.Max(tc.X1, xs[c + 1].Pos);
            }
        // A cell that is not a rectangle means the lines are a drawing, not a table.
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] is { } tc && area[i] != tc.RowSpan * tc.ColumnSpan) return null;
        // In a table whose cells each draw their own border every cell is a box, along the outside of the grid too: a
        // place no box covers, as beside a box set under a row of others, means boxes laid out on their own.
        if (spaced)
            foreach (var tc in cells)
                if (tc != null && !(Drawn(xs[tc.Column], ys[tc.Row].Pos, ys[tc.Row + tc.RowSpan].Pos, edges: false)
                    && Drawn(xs[tc.Column + tc.ColumnSpan], ys[tc.Row].Pos, ys[tc.Row + tc.RowSpan].Pos, edges: false)
                    && Drawn(ys[tc.Row], xs[tc.Column].Pos, xs[tc.Column + tc.ColumnSpan].Pos, edges: false)
                    && Drawn(ys[tc.Row + tc.RowSpan], xs[tc.Column].Pos, xs[tc.Column + tc.ColumnSpan].Pos, edges: false)))
                    return null;

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

        // The cell each word is in, and then each cell's words in a list of the size it needs.
        var xp = Positions(xs);
        var yp = Positions(ys);
        var at = new int[words.Count];
        var counts = new int[cells.Length];
        int filled = 0;
        for (int k = 0; k < words.Count; k++)
        {
            var w = words[k];
            int root = at[k] = Find(Index(yp, w.CenterY) * columns + Index(xp, w.CenterX));
            if (counts[root]++ == 0) filled++;
        }
        var texts = new List<FlowWord>?[cells.Length];
        for (int k = 0; k < words.Count; k++) (texts[at[k]] ??= new List<FlowWord>(counts[at[k]])).Add(words[k]);
        if (filled < 2) return null;
        // Each cell's lines, worked out once for the check below and the table.
        var cellLines = new List<FlowLine>?[cells.Length];
        var withText = new List<List<FlowLine>>(filled);
        for (int i = 0; i < texts.Length; i++)
            if (texts[i] is { } list) withText.Add(cellLines[i] = CellLines(list));
        // Boxes joined over wider spacing are as often cards, each with a title and some lines of text; most cells of a
        // table hold one line.
        if (spaced && withText.Count(l => l.Count > 1) * 3 > withText.Count) return null;
        if (UnderRuled(withText))
        {
            // The rules outline groups of rows and columns rather than cells, so the rows come from the lines and the
            // columns from the text, as in a table with rules only between some of its rows.
            var stack = ys.Select(y => new Segment(y.Pos, box.X0, box.X1)).ToList();
            var found = new List<FlowBlock>();
            RuledRows(stack, words, body, found);
            return found.Count == 1 ? found[0] : null;
        }
        var table = new TableBlock { Rows = rows, Columns = columns, Top = box.Y0, Left = box.X0 };
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] is not { } tc) continue;
            if (cellLines[i] is { } lines) tc.Lines = lines;
            table.Cells.Add(tc);
        }
        return Finish(table);
    }

    /// <summary>
    /// Whether cells hold many rows each: two or more cells of four or more lines whose lines sit on shared baselines,
    /// one of them a column of numbers, as when a table draws lines only around groups of columns. Wrapped text in
    /// neighbouring cells shares baselines too, which is why a column of numbers must show the lines are rows.
    /// </summary>
    private static bool UnderRuled(List<List<FlowLine>> cells)
    {
        var tall = cells.Where(l => l.Count >= 4).ToList();
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

    /// <summary>
    /// The index of the band between two boundaries, at the sorted places <paramref name="lines"/>, that
    /// <paramref name="pos"/> falls in.
    /// </summary>
    private static int Index(double[] lines, double pos)
    {
        // The last band whose start pos reaches, found by halving: the boundaries are sorted.
        int lo = 0, hi = lines.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (pos >= lines[mid]) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The lines' positions on their own, which <see cref="Index"/> searches without reading each line.</summary>
    private static double[] Positions(List<GridLine> lines)
    {
        var positions = new double[lines.Count];
        for (int i = 0; i < positions.Length; i++) positions[i] = lines[i].Pos;
        return positions;
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
        // Most cells hold the words of one text line in reading order, which are then the cell's one line as they
        // are; every caller hands over a list of its own that it does not use again.
        if (InOneLine(words))
        {
            int rtl = 0;
            foreach (var w in words) if (IsRightToLeft(w.Text)) rtl++;
            return new List<FlowLine>(1) { new FlowLine(words, words[0].Line, rtl * 2 > words.Count) };
        }
        // The words of each text line, the lines in the order their first words come. Words mostly come line by line,
        // so the last line is tried first, and the others are found through a dictionary.
        var groups = new List<(int Line, List<FlowWord> Words)>();
        Dictionary<int, int>? index = null;
        foreach (var w in words)
        {
            int g = groups.Count - 1;
            if (g >= 0 && groups[g].Line != w.Line)
            {
                if (index == null)
                {
                    index = new Dictionary<int, int>();
                    for (int k = 0; k < groups.Count; k++) index[groups[k].Line] = k;
                }
                if (!index.TryGetValue(w.Line, out g)) g = -1;
            }
            if (g >= 0) { groups[g].Words.Add(w); continue; }
            index?.Add(w.Line, groups.Count);
            groups.Add((w.Line, new List<FlowWord> { w }));
        }
        var lines = new List<FlowLine>(groups.Count);
        foreach (var (line, inLine) in groups)
        {
            // In reading order; the words mostly are already, and a stable sort keeps words with the same place as
            // they came.
            var list = inLine;
            for (int i = 1; i < list.Count; i++)
                if (list[i - 1].Order > list[i].Order) { list = list.OrderBy(w => w.Order).ToList(); break; }
            int rightToLeft = 0;
            foreach (var w in list) if (IsRightToLeft(w.Text)) rightToLeft++;
            // Words of one text line that a cell boundary does not split but that lie far apart, as when two columns
            // share a baseline, stay in one line; the line keeps their reading order. A line cut from a wider one is
            // right to left when most of its own words are, as a column of Hebrew beside another is.
            lines.Add(new FlowLine(list, line, rightToLeft * 2 > list.Count));
        }
        lines.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.X0.CompareTo(b.X0));
        return lines;
    }

    /// <summary>Whether words are all of one text line and already in its reading order.</summary>
    private static bool InOneLine(List<FlowWord> words)
    {
        if (words.Count == 0) return false;
        for (int i = 1; i < words.Count; i++)
            if (words[i].Line != words[0].Line || words[i - 1].Order > words[i].Order) return false;
        return true;
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
            int reach = rows;
            bool any = false;
            foreach (var c in table.Cells)
                if (c.Row < rows) { reach = any ? Math.Max(reach, c.Row + c.RowSpan) : c.Row + c.RowSpan; any = true; }
            grew = reach > rows;
            rows = Math.Max(rows, reach);
        }
        // The header is bold where the rest of the table is not all bold, and no cell spans out of it.
        bool headText = false, headBold = true, restPlain = false, spansOut = false;
        foreach (var c in table.Cells)
        {
            if (c.Row < rows)
            {
                if (c.Row + c.RowSpan > rows) spansOut = true;
                if (c.Lines.Count == 0) continue;
                headText = true;
                foreach (var l in c.Lines) if (!l.Words.TrueForAll(w => w.Bold)) headBold = false;
            }
            else if (!restPlain)
                foreach (var l in c.Lines) if (l.Words.Exists(w => !w.Bold)) { restPlain = true; break; }
        }
        table.HeaderRows = headText && table.Rows > rows && headBold && restPlain && !spansOut ? rows : 0;
        SetDirection(table);
        return table;
    }

    /// <summary>Sets a table right-to-left when most of its lines are, and its lines with it.</summary>
    private static void SetDirection(TableBlock table)
    {
        int lines = 0, rightToLeft = 0;
        foreach (var c in table.Cells)
            foreach (var l in c.Lines)
            {
                lines++;
                if (l.Words.Exists(w => IsRightToLeft(w.Text))) rightToLeft++;
            }
        table.RightToLeft = lines > 0 && rightToLeft > lines / 2;
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

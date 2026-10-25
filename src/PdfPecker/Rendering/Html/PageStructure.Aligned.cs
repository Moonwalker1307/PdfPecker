namespace PdfPecker.Rendering.Html;

internal static partial class PageStructure
{
    /// <summary>A gap between words wider than this many font sizes separates the cells of a row.</summary>
    private const double CellGap = 0.75;

    /// <summary>Lines further apart than this many font sizes are not rows of one table.</summary>
    private const double RowGap = 2.2;

    /// <summary>Entries of a column whose left sides, right sides or centres are this close line up.</summary>
    private const double AlignTolerance = 2;

    private readonly record struct Band(double X0, double X1);

    /// <summary>A row's words, from the left, split where the gap between two is wide enough to separate cells.</summary>
    private static List<List<FlowWord>> Chunks(List<FlowWord> row)
    {
        var sorted = row.OrderBy(w => w.X0).ToList();
        var chunks = new List<List<FlowWord>>();
        FlowWord? previous = null;
        foreach (var w in sorted)
        {
            // A bullet is the marker of the text after it, though set further from it than a space.
            bool marker = chunks.Count == 1 && chunks[0].Count == 1 && IsBullet(chunks[0][0].Text) && w.X0 - previous!.X1 <= 1.5 * w.Size;
            if (previous == null || (!marker && w.X0 - previous.X1 > CellGap * Math.Max(w.Size, previous.Size))) chunks.Add(new List<FlowWord>());
            chunks[^1].Add(w);
            if (previous == null || w.X1 > previous.X1) previous = w;
        }
        return chunks;
    }

    /// <summary>How many cells a line's words make, counted once per line.</summary>
    private static int CellCount(FlowLine line)
    {
        if (line.Cells < 0) line.Cells = Chunks(line.Words).Count;
        return line.Cells;
    }

    /// <summary>
    /// The columns of rows of text: the stretches across the page that some text covers, separated by gaps no row has
    /// text in. Rows with fewer cells than most do not count, nor, when a row spanning the whole width leaves no gap,
    /// rows of one cell; such rows span columns. Null when there are not two columns.
    /// </summary>
    private static List<Band>? Columns(List<List<FlowWord>> rows, double body)
    {
        var chunked = rows.Select(Chunks).ToList();
        double minGap = 0.4 * body;
        // Rows of fewer cells than most, such as group headings and totals, often span columns, so the columns come
        // from the others.
        int usual = chunked.Select(c => c.Count).OrderBy(n => n).ElementAt(chunked.Count / 2);
        var bands = Coverage(chunked.Where(c => c.Count >= usual).ToList(), minGap);
        if (bands.Count < 2)
        {
            var multi = chunked.Where(c => c.Count >= 2).ToList();
            if (multi.Count < 2) return null;
            bands = Coverage(multi, minGap);
        }
        bands = SplitBands(chunked, bands, body);
        bands = JoinLoneBands(chunked, bands);
        bands = SplitAtChannels(rows, bands);
        return bands.Count >= 2 ? bands : null;
    }

    /// <summary>
    /// Splits columns at a gap no word crosses in any row, wider than a space, where some row has two cells, one on
    /// either side, and two or more rows have text on each side. A right-aligned number set close to a left-aligned
    /// unit after it reads as one cell in each row, but the header shows they are two columns.
    /// </summary>
    private static List<Band> SplitBands(List<List<List<FlowWord>>> rows, List<Band> bands, double body)
    {
        var words = rows.SelectMany(r => r).SelectMany(c => c).OrderBy(w => w.X0).ToList();
        var cuts = new List<(double X0, double X1)>();
        double reach = double.MaxValue;
        foreach (var w in words)
        {
            double g0 = reach, g1 = w.X0;
            reach = reach == double.MaxValue ? w.X1 : Math.Max(reach, w.X1);
            if (g0 == double.MaxValue || g1 - g0 < 0.4 * body) continue;
            if (!bands.Exists(b => b.X0 < g0 && b.X1 > g1)) continue; // already between columns
            int left = 0, right = 0;
            bool shown = false;
            foreach (var row in rows)
            {
                for (int k = 1; k < row.Count && !shown; k++)
                    shown = row[k - 1].Max(x => x.X1) <= g1 && row[k].Min(x => x.X0) >= g0;
                if (row.Exists(c => c.Exists(x => x.X1 <= g0))) left++;
                if (row.Exists(c => c.Exists(x => x.X0 >= g1))) right++;
            }
            if (shown && left >= 2 && right >= 2) cuts.Add((g0, g1));
        }
        if (cuts.Count == 0) return bands;
        var result = new List<Band>();
        foreach (var band in bands)
        {
            double start = band.X0;
            foreach (var (g0, g1) in cuts)
            {
                if (g0 <= band.X0 || g1 >= band.X1) continue;
                result.Add(new Band(start, g0));
                start = g1;
            }
            result.Add(new Band(start, band.X1));
        }
        return result;
    }

    /// <summary>
    /// Splits a column where every row leaves the same narrow gap, as listings set in tight columns do: a number and a
    /// code beside it, or a phone number and a date, a space apart. The gap must run through every row's text, most rows
    /// need text on both sides that lines up at the gap, and the text must vary from row to row on one side and differ on
    /// the other, which the words of a value repeated in every row, such as a city's two-word name, do not.
    /// </summary>
    private static List<Band> SplitAtChannels(List<List<FlowWord>> rows, List<Band> bands)
    {
        // The usual space between words; a gap no wider is a space within a value, as between a phone number's groups.
        var spaces = new List<double>();
        foreach (var line in rows.SelectMany(r => r).GroupBy(w => w.Line))
        {
            var sorted = line.OrderBy(w => w.X0).ToList();
            for (int k = 1; k < sorted.Count; k++)
            {
                double gap = sorted[k].X0 - sorted[k - 1].X1;
                if (gap > 0 && gap < CellGap * sorted[k].Size) spaces.Add(gap);
            }
        }
        if (spaces.Count == 0) return bands;
        spaces.Sort();
        double space = spaces[spaces.Count / 2];
        var result = new List<Band>();
        var pending = new Stack<Band>(Enumerable.Reverse(bands));
        while (pending.Count > 0)
        {
            var band = pending.Pop();
            var inBand = rows.Select(r => r.Where(w => w.CenterX > band.X0 - 0.5 && w.CenterX < band.X1 + 0.5).OrderBy(w => w.X0).ToList())
                .Where(r => r.Count > 0).ToList();
            if (inBand.Count < 4) { result.Add(band); continue; }
            double size = inBand.SelectMany(r => r).Select(w => w.Size).OrderBy(x => x).ElementAt(inBand.Sum(r => r.Count) / 2);
            // A header's words are often set closer than the columns under them; they must not cross the gap, but may
            // narrow it.
            var header = inBand[0];
            var spans = inBand.Skip(1).SelectMany(r => r).Select(w => (w.X0, w.X1)).OrderBy(x => x.X0).ToList();
            bool split = false;
            double reach = spans[0].X1;
            for (int k = 1; k < spans.Count && !split; k++)
            {
                double g0 = reach, g1 = spans[k].X0;
                reach = Math.Max(reach, spans[k].X1);
                if (g1 - g0 < Math.Max(0.25 * size, 1.3 * space) || header.Exists(w => w.X0 < (g0 + g1) / 2 && w.X1 > (g0 + g1) / 2)) continue;
                int both = 0, leftAligned = 0, rightAligned = 0;
                var lefts = new HashSet<string>(StringComparer.Ordinal);
                var rights = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in inBand)
                {
                    var left = r.FindAll(w => w.X1 <= g0);
                    var right = r.FindAll(w => w.X0 >= g1);
                    if (left.Count == 0 || right.Count == 0) continue;
                    both++;
                    lefts.Add(string.Join(" ", left.Select(w => w.Text)));
                    rights.Add(string.Join(" ", right.Select(w => w.Text)));
                    if (g0 - left.Max(w => w.X1) <= AlignTolerance) leftAligned++;
                    if (right.Min(w => w.X0) - g1 <= AlignTolerance) rightAligned++;
                }
                if (both < 0.8 * inBand.Count || Math.Max(leftAligned, rightAligned) < 0.7 * both) continue;
                if (Math.Min(lefts.Count, rights.Count) < 2 || Math.Max(lefts.Count, rights.Count) < 0.5 * both) continue;
                pending.Push(new Band(g1, band.X1));
                pending.Push(new Band(band.X0, g0));
                split = true;
            }
            if (!split) result.Add(band);
        }
        return result;
    }

    /// <summary>
    /// Joins a column that only one row has text in to a neighbouring column that row leaves empty, as a header set
    /// on the left of a column of numbers set on the right is one column with it, though their text does not overlap.
    /// </summary>
    private static List<Band> JoinLoneBands(List<List<List<FlowWord>>> rows, List<Band> bands)
    {
        while (bands.Count > 2)
        {
            // Which rows have text in each column.
            var used = bands.Select(_ => new HashSet<int>()).ToList();
            for (int r = 0; r < rows.Count; r++)
                foreach (var chunk in rows[r])
                {
                    double x0 = chunk.Min(w => w.X0), x1 = chunk.Max(w => w.X1);
                    for (int b = 0; b < bands.Count; b++)
                        if (x1 > bands[b].X0 && x0 < bands[b].X1) used[b].Add(r);
                }
            int join = -1, into = -1;
            double best = double.MaxValue;
            for (int b = 0; b < bands.Count; b++)
            {
                if (used[b].Count != 1) continue;
                foreach (int n in new[] { b - 1, b + 1 })
                {
                    if (n < 0 || n >= bands.Count || used[n].Count < 2 || used[n].Overlaps(used[b])) continue;
                    double gap = n < b ? bands[b].X0 - bands[n].X1 : bands[n].X0 - bands[b].X1;
                    if (gap < best) { best = gap; join = b; into = n; }
                }
            }
            if (join < 0) break;
            var merged = new Band(Math.Min(bands[join].X0, bands[into].X0), Math.Max(bands[join].X1, bands[into].X1));
            bands[Math.Min(join, into)] = merged;
            bands.RemoveAt(Math.Max(join, into));
        }
        return bands;
    }

    /// <summary>
    /// The stretches across the page that the rows' cells cover, separated by gaps at least <paramref name="minGap"/>
    /// wide. In a table of many rows, a gap only one row's text crosses, such as a header whose words are set closer
    /// than the columns under them, still separates two columns; that row's cell then spans them.
    /// </summary>
    private static List<Band> Coverage(List<List<List<FlowWord>>> rows, double minGap)
    {
        var spans = rows.SelectMany(r => r).Select(c => new Band(c.Min(w => w.X0), c.Max(w => w.X1))).OrderBy(b => b.X0).ToList();
        var bands = new List<Band>();
        foreach (var s in spans)
        {
            if (bands.Count > 0 && s.X0 < bands[^1].X1 + minGap) bands[^1] = new Band(bands[^1].X0, Math.Max(bands[^1].X1, s.X1));
            else bands.Add(s);
        }
        if (rows.Count < 8) return bands;

        // The stretches two or more rows cover.
        var events = new List<(double X, int Step)>();
        foreach (var s in spans) { events.Add((s.X0, 1)); events.Add((s.X1, -1)); }
        events.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Step.CompareTo(b.Step));
        var cores = new List<Band>();
        int depth = 0;
        double start = 0;
        foreach (var (x, step) in events)
        {
            int before = depth;
            depth += step;
            if (before <= 1 && depth > 1) start = x;
            else if (before > 1 && depth <= 1)
            {
                if (cores.Count > 0 && start < cores[^1].X1 + minGap) cores[^1] = new Band(cores[^1].X0, x);
                else cores.Add(new Band(start, x));
            }
        }
        var result = new List<Band>();
        foreach (var band in bands)
        {
            var inner = cores.FindAll(c => c.X0 >= band.X0 && c.X1 <= band.X1);
            if (inner.Count < 2) { result.Add(band); continue; }
            inner[0] = new Band(band.X0, inner[0].X1);
            inner[^1] = new Band(inner[^1].X0, band.X1);
            result.AddRange(inner);
        }
        return result;
    }

    /// <summary>
    /// The cells of one row of words on the given columns. Words set a space apart stay in one cell, which spans the
    /// columns they reach, as a note under an order line does; a wider gap on a column boundary separates cells.
    /// </summary>
    private static List<TableCell> RowCells(List<FlowWord> row, List<Band> bands, int r)
    {
        int BandOf(FlowWord w, bool last)
        {
            int found = -1;
            for (int b = 0; b < bands.Count; b++)
            {
                if (w.X1 <= bands[b].X0 + 0.5 || w.X0 >= bands[b].X1 - 0.5) continue;
                found = b;
                if (!last) break;
            }
            if (found >= 0) return found;
            // A word in a gap belongs to the nearest column.
            double best = double.MaxValue;
            for (int b = 0; b < bands.Count; b++)
            {
                double d = Math.Max(0, Math.Max(bands[b].X0 - w.CenterX, w.CenterX - bands[b].X1));
                if (d < best) { best = d; found = b; }
            }
            return found;
        }

        var items = new List<(List<FlowWord> Words, int Lo, int Hi)>();
        foreach (var chunk in Chunks(row))
        {
            int start = 0;
            for (int k = 1; k <= chunk.Count; k++)
            {
                if (k < chunk.Count && (BandOf(chunk[k], false) == BandOf(chunk[k - 1], true)
                    || chunk[k].X0 - chunk[k - 1].X1 < 0.4 * Math.Max(chunk[k].Size, chunk[k - 1].Size))) continue;
                var piece = chunk.GetRange(start, k - start);
                items.Add((piece, piece.Min(w => BandOf(w, false)), piece.Max(w => BandOf(w, true))));
                start = k;
            }
        }
        items.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        var cells = new List<TableCell>();
        for (int i = 0; i < items.Count;)
        {
            int lo = items[i].Lo, hi = items[i].Hi, j = i + 1;
            while (j < items.Count && items[j].Lo <= hi) hi = Math.Max(hi, items[j++].Hi);
            var cell = new TableCell { Row = r, Column = lo, ColumnSpan = hi - lo + 1, X0 = bands[lo].X0, X1 = bands[hi].X1 };
            cell.Lines.AddRange(CellLines(items.GetRange(i, j - i).SelectMany(t => t.Words).ToList()));
            cells.Add(cell);
            i = j;
        }
        return cells;
    }

    /// <summary>
    /// Makes a row whose only text is one bold label, such as a group heading, span the table, as it does in the HTML
    /// such tables are made from; where its text sits in the row says nothing of a column.
    /// </summary>
    private static void SpanHeadings(TableBlock table)
    {
        if (table.Columns < 3) return;
        foreach (var row in table.Cells.GroupBy(c => c.Row))
        {
            var filled = row.Where(c => c.Lines.Count > 0).ToList();
            if (filled.Count != 1 || row.Key == 0) continue;
            var cell = filled[0];
            var words = cell.Lines.SelectMany(l => l.Words).ToList();
            if (!words.TrueForAll(w => w.Bold) || !words.Exists(w => w.Text.Any(char.IsLetter))) continue;
            table.Cells.RemoveAll(c => c.Row == row.Key && c != cell);
            cell.Column = 0;
            cell.ColumnSpan = table.Columns;
        }
    }

    /// <summary>
    /// Finds tables and text columns among lines no border encloses, from text that lines up in columns over several
    /// lines; they are added to <paramref name="blocks"/> and the other lines to <paramref name="plain"/>.
    /// </summary>
    private static void FindAlignedTables(List<FlowLine> lines, double body, List<FlowBlock> blocks, List<FlowLine> plain)
    {
        for (int i = 0; i < lines.Count;)
        {
            int j = i + 1;
            double bottom = lines[i].Bottom;
            while (j < lines.Count && lines[j].Top - bottom <= RowGap * Math.Max(lines[j].Size, body))
                bottom = Math.Max(bottom, lines[j++].Bottom);
            SideBySide(lines.GetRange(i, j - i), body, blocks, plain);
            i = j;
        }
    }

    /// <summary>
    /// Finds the runs of lines in a stretch that hold two parts side by side, such as a company's address beside the
    /// invoice number and date, or an article beside a box floated to its right: a gap wider than
    /// <see cref="SideGap"/> runs down every line of the run, and the lines on either side do not share baselines and
    /// font sizes, as the rows of a table would. Each part is read on its own; the lines between such runs are searched
    /// for tables.
    /// </summary>
    private static void SideBySide(List<FlowLine> lines, double body, List<FlowBlock> blocks, List<FlowLine> plain)
    {
        double gapWidth = Math.Max(SideGap * body, 12);
        double minX = lines.Min(l => l.X0), maxX = lines.Max(l => l.X1);
        var pending = new List<FlowLine>();
        for (int i = 0; i < lines.Count;)
        {
            // The gaps every line from i on leaves, narrowed line by line while one stays wide enough.
            var common = Gaps(lines[i], minX, maxX, gapWidth);
            int j = i + 1;
            (double X0, double X1) best = default;
            int bestEnd = -1;
            // The run's text must reach both sides of a gap: its leftmost word end and rightmost word start.
            double leftEnd = lines[i].Words.Min(w => w.X1), rightStart = lines[i].Words.Max(w => w.X0);
            while (common.Count > 0)
            {
                foreach (var g in common)
                {
                    if (j - i < 2 || g.X0 <= minX || g.X1 >= maxX || leftEnd > g.X0 || rightStart < g.X1) continue;
                    best = g;
                    bestEnd = j;
                }
                if (j >= lines.Count) break;
                common = Intersect(common, Gaps(lines[j], minX, maxX, gapWidth), gapWidth);
                leftEnd = Math.Min(leftEnd, lines[j].Words.Min(w => w.X1));
                rightStart = Math.Max(rightStart, lines[j].Words.Max(w => w.X0));
                j++;
            }
            if (bestEnd > 0 && Separate(lines.GetRange(i, bestEnd - i), (best.X0 + best.X1) / 2) is { } group)
            {
                Aligned(pending, body, blocks, plain);
                pending = new List<FlowLine>();
                blocks.Add(group);
                i = bestEnd;
                continue;
            }
            pending.Add(lines[i++]);
        }
        Aligned(pending, body, blocks, plain);

        GroupBlock? Separate(List<FlowLine> run, double cut)
        {
            var left = Split(run, w => w.CenterX < cut);
            var right = Split(run, w => w.CenterX > cut);
            static double Baseline(FlowLine l) => l.Bottom - 0.2 * l.Size;
            var (few, many) = left.Count <= right.Count ? (left, right) : (right, left);
            // The cells of a table row share a baseline and, nearly always, a font size.
            int shared = few.Count(l => many.Exists(o => Math.Abs(Baseline(o) - Baseline(l)) <= 0.25 * Math.Max(l.Size, o.Size)
                && Math.Abs(o.Size - l.Size) <= 0.1 * Math.Max(l.Size, o.Size)));
            // Lists side by side line up as a table's rows do, but a table's cells seldom start with list markers.
            bool lists = left.Count(l => Marker(l, out _) != TextKind.Paragraph) >= 2 && right.Count(l => Marker(l, out _) != TextKind.Paragraph) >= 2;
            if (shared > 0.3 * few.Count && !lists) return null;
            var group = new GroupBlock { Top = run.Min(l => l.Top), Left = run.Min(l => l.X0) };
            group.Parts.Add(Flow(left, body, columns: true));
            group.Parts.Add(Flow(right, body, columns: true));
            foreach (var l in run) foreach (var w in l.Words) w.Used = true;
            return group;
        }
    }

    /// <summary>A gap this many body font sizes wide between two parts of a page sets them side by side.</summary>
    private const double SideGap = 1.5;

    /// <summary>The stretches from <paramref name="minX"/> to <paramref name="maxX"/> a line leaves empty, at least <paramref name="width"/> wide.</summary>
    private static List<(double X0, double X1)> Gaps(FlowLine line, double minX, double maxX, double width)
    {
        var gaps = new List<(double, double)>();
        double at = minX;
        foreach (var w in line.Words.OrderBy(w => w.X0))
        {
            if (w.X0 - at >= width) gaps.Add((at, w.X0));
            at = Math.Max(at, w.X1);
        }
        if (maxX - at >= width) gaps.Add((at, maxX));
        return gaps;
    }

    private static List<(double X0, double X1)> Intersect(List<(double X0, double X1)> a, List<(double X0, double X1)> b, double width)
    {
        var result = new List<(double, double)>();
        foreach (var x in a)
            foreach (var y in b)
            {
                double x0 = Math.Max(x.X0, y.X0), x1 = Math.Min(x.X1, y.X1);
                if (x1 - x0 >= width) result.Add((x0, x1));
            }
        return result;
    }

    /// <summary>The lines rebuilt from their words that <paramref name="keep"/> selects.</summary>
    private static List<FlowLine> Split(List<FlowLine> lines, Func<FlowWord, bool> keep)
    {
        var result = new List<FlowLine>();
        foreach (var line in lines)
        {
            var words = line.Words.Where(keep).ToList();
            if (words.Count > 0) result.Add(new FlowLine(words, line.Id, line.RightToLeft));
        }
        return result;
    }

    /// <summary>The tables in a stretch of lines with no wide gap between them.</summary>
    private static void Aligned(List<FlowLine> lines, double body, List<FlowBlock> blocks, List<FlowLine> plain)
    {
        // A table starts and ends with a row of two or more cells; lines before and after are text.
        int first = lines.FindIndex(l => CellCount(l) >= 2), last = lines.FindLastIndex(l => CellCount(l) >= 2);
        if (first < 0 || last - first < 1)
        {
            plain.AddRange(lines);
            return;
        }
        // A line that goes on in lower case right under the last row is the end of a cell that wrapped.
        while (last + 1 < lines.Count && lines[last + 1].Words[0].Text is { Length: > 0 } t && char.IsLower(t[0])
            && lines[last + 1].Top - lines[last].Bottom <= 0.6 * lines[last + 1].Size)
            last++;
        plain.AddRange(lines.GetRange(0, first));
        plain.AddRange(lines.GetRange(last + 1, lines.Count - last - 1));
        var run = lines.GetRange(first, last - first + 1);
        // A line of running text, many words in one cell across most of the width, is not a row of a table.
        double x0 = run.Min(l => l.X0), x1 = run.Max(l => l.X1);
        int prose = run.FindIndex(l => l.Words.Count >= 6 && CellCount(l) == 1 && l.X1 - l.X0 >= 0.6 * (x1 - x0));
        if (prose >= 0)
        {
            Aligned(run.GetRange(0, prose), body, blocks, plain);
            plain.Add(run[prose]);
            Aligned(run.GetRange(prose + 1, run.Count - prose - 1), body, blocks, plain);
            return;
        }
        var bands = Columns(run.Where(l => CellCount(l) >= 2).Select(l => l.Words).ToList(), body);
        if (bands != null)
        {
            // A line of one cell across several columns, such as a heading between two tables, ends one table.
            for (int k = 0; k < run.Count; k++)
            {
                var chunks = Chunks(run[k].Words);
                if (chunks.Count != 1 || RowCells(run[k].Words, bands, 0)[0].ColumnSpan == 1) continue;
                Aligned(run.GetRange(0, k), body, blocks, plain);
                plain.Add(run[k]);
                Aligned(run.GetRange(k + 1, run.Count - k - 1), body, blocks, plain);
                return;
            }
        }
        var block = bands == null ? null : AlignedTable(run, bands, body);
        if (block == null) plain.AddRange(run);
        else blocks.Add(block);
    }

    /// <summary>
    /// The table, or the text columns, that lines make on the given columns, or null when the text does not line up
    /// well enough to be sure: every column needs two entries, most of them lined up on one side or the centre.
    /// </summary>
    private static FlowBlock? AlignedTable(List<FlowLine> run, List<Band> bands, double body)
    {
        var rows = run.Select((l, r) => RowCells(l.Words, bands, r)).ToList();
        if (rows.Count(r => r.Count >= 2) < 2) return null;
        int words = 0, entries = 0;
        for (int c = 0; c < bands.Count; c++)
        {
            var cells = rows.SelectMany(r => r).Where(t => t.Column == c && t.ColumnSpan == 1).ToList();
            if (cells.Count < 2 || !(LinedUp(cells) || c == 0 && Indented(cells))) return null;
            words += cells.Sum(t => t.Lines.Sum(l => l.Words.Count));
            entries += cells.Count;
        }

        // Columns of running text are read one after the other rather than as a table: lines of several words, or lines
        // that fill their column and go on in lower case on the next.
        int wraps = 0;
        for (int r = 0; r + 1 < rows.Count; r++)
            foreach (var cell in rows[r])
            {
                var next = rows[r + 1].Find(c => c.Column == cell.Column && c.ColumnSpan == cell.ColumnSpan);
                var band = bands[cell.Column];
                if (next != null && cell.ColumnSpan == 1 && cell.Lines[^1].X1 - cell.Lines[^1].X0 >= 0.7 * (band.X1 - band.X0)
                    && next.Lines[0].Words[0].Text is { Length: > 0 } t && char.IsLower(t[0]))
                    wraps++;
            }
        if ((bands.Count <= 3 && run.Count >= 5 && words >= 4 * entries) || (wraps >= 2 && wraps >= 0.25 * (rows.Count - 1)))
        {
            var group = new GroupBlock { Top = run.Min(l => l.Top), Left = bands[0].X0 };
            for (int c = 0; c < bands.Count; c++)
            {
                var part = rows.SelectMany(r => r).Where(t => t.Column == c).SelectMany(t => t.Lines).ToList();
                group.Parts.Add(Flow(part, body, columns: false));
            }
            foreach (var l in run) foreach (var w in l.Words) w.Used = true;
            return group;
        }

        if (ListColumns(run, rows, bands, body) is { } lists) return lists;
        var table = new TableBlock { Columns = bands.Count, Top = run.Min(l => l.Top), Left = bands[0].X0 };
        if (Stacked(rows, bands) is { } stacked)
        {
            // A header over one stack of lines per column, such as "Billed to", "Invoice number" and "Amount due" over
            // an address, a number and a date: each stack is one cell.
            table.Rows = 2;
            table.Cells.AddRange(rows[0]);
            table.Cells.AddRange(stacked);
            foreach (var l in run) foreach (var w in l.Words) w.Used = true;
            return Finish(table);
        }
        AddRows(table, rows, run.Select(_ => 0).ToList(), bands);
        // Numbered clauses set with a hanging indent, a short number beside sentences, are paragraphs.
        if (bands.Count == 2)
        {
            var numbers = table.Cells.FindAll(c => c.Column == 0 && c.ColumnSpan == 1 && c.Lines.Count > 0);
            var texts = table.Cells.FindAll(c => c.Column == 1 && c.Lines.Count > 0);
            if (numbers.TrueForAll(c => c.Lines.Sum(l => l.Words.Count) <= 2) && texts.Count > 0
                && texts.Average(c => c.Lines.Sum(l => l.Words.Count)) >= 8)
                return null;
        }
        foreach (var l in run) foreach (var w in l.Words) w.Used = true;
        SpanHeadings(table);
        return Finish(table);
    }

    /// <summary>
    /// The parts side by side, read one after the other, when a column holds a list, such as delivery terms and payment
    /// terms numbered beside a box of totals: its items start with list markers in two or more rows. Each list is a part
    /// of its own, and the columns between lists, which may still be a table, are one part. Null when no column is a list.
    /// </summary>
    private static GroupBlock? ListColumns(List<FlowLine> run, List<List<TableCell>> rows, List<Band> bands, double body)
    {
        var list = new bool[bands.Count];
        for (int c = 0; c < bands.Count; c++)
        {
            var entries = rows.SelectMany(r => r).Where(t => t.Column == c && t.ColumnSpan == 1 && t.Lines.Count > 0).ToList();
            int items = entries.Count(t => Marker(t.Lines[0], out _) != TextKind.Paragraph);
            list[c] = items >= 2 && items * 2 >= entries.Count;
        }
        if (!list.Contains(true)) return null;
        var group = new GroupBlock { Top = run.Min(l => l.Top), Left = bands[0].X0 };
        for (int c = 0; c < bands.Count;)
        {
            int end = c + 1;
            if (!list[c]) while (end < bands.Count && !list[end]) end++;
            double from = c == 0 ? double.MinValue : (bands[c - 1].X1 + bands[c].X0) / 2;
            double to = end == bands.Count ? double.MaxValue : (bands[end - 1].X1 + bands[end].X0) / 2;
            var part = Split(run, w => w.CenterX > from && w.CenterX < to);
            if (part.Count > 0) group.Parts.Add(Flow(part, body, columns: !list[c]));
            c = end;
        }
        foreach (var l in run) foreach (var w in l.Words) w.Used = true;
        return group;
    }

    /// <summary>
    /// The cells of rows under a header when, in every column, they are one short stack of lines, at most a line
    /// apart, on baselines the other columns mostly do not share: the columns are blocks side by side under their
    /// labels rather than rows. Null when they are rows, as in a table whose wrapped cells sit beside figures set once per row.
    /// </summary>
    private static List<TableCell>? Stacked(List<List<TableCell>> rows, List<Band> bands)
    {
        if (rows.Count < 3 || rows[0].Count != bands.Count || rows[0].Exists(c => c.ColumnSpan > 1)) return null;
        var body = rows.Skip(1).SelectMany(r => r).ToList();
        if (body.Exists(c => c.ColumnSpan > 1)) return null;
        var lines = body.SelectMany(c => c.Lines.Select(l => (Line: l, c.Column))).ToList();
        int shared = lines.Count(a => lines.Exists(b => b.Column != a.Column && Math.Abs(b.Line.Bottom - a.Line.Bottom) <= 0.25 * a.Line.Size));
        if (shared > 0.6 * lines.Count) return null;
        var cells = new List<TableCell>();
        for (int c = 0; c < bands.Count; c++)
        {
            var column = lines.Where(x => x.Column == c).Select(x => x.Line).OrderBy(l => l.Top).ToList();
            if (column.Count == 0) continue;
            if (column.Count > 8) return null;
            for (int k = 1; k < column.Count; k++)
                if (column[k].Top - column[k - 1].Bottom > 1.5 * Math.Min(column[k].Size, column[k - 1].Size)) return null;
            var cell = new TableCell { Row = 1, Column = c, X0 = bands[c].X0, X1 = bands[c].X1 };
            cell.Lines.AddRange(column);
            cells.Add(cell);
        }
        return cells.Count >= 2 ? cells : null;
    }

    /// <summary>
    /// Adds rows of cells to a table, joining a row to the one above when it continues it and both are in the same
    /// group; a group of -1, or no groups, joins nothing.
    /// </summary>
    private static void AddRows(TableBlock table, List<List<TableCell>> rows, List<int>? groups, List<Band> bands)
    {
        int row = -1;
        List<TableCell>? previous = null;
        double previousBottom = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = rows[r];
            if (r + 1 < rows.Count && groups != null && groups[r] >= 0 && groups[r] == groups[r + 1] && Leads(cells, rows[r + 1]))
            {
                // The first line of a label that wraps, beside figures set level with its last line, goes with them.
                var lead = cells.First(c => c.Lines.Count > 0);
                rows[r + 1].First(c => c.Column == lead.Column).Lines.InsertRange(0, lead.Lines);
                continue;
            }
            double top = cells.Min(c => c.Lines.Min(l => l.Top)), size = cells.Max(c => c.Lines.Max(l => l.Size));
            if (previous != null && groups != null && groups[r] >= 0 && groups[r] == groups[r - 1]
                && Continues(cells, previous, bands, top - previousBottom, size))
            {
                foreach (var c in cells) previous.First(p => p.Column == c.Column).Lines.AddRange(c.Lines);
            }
            else
            {
                row++;
                foreach (var c in cells) { c.Row = row; table.Cells.Add(c); }
                previous = cells;
            }
            previousBottom = cells.Max(c => c.Lines.Max(l => l.Bottom));
        }
        table.Rows = row + 1;
    }

    /// <summary>
    /// Whether a row of one cell starts the cell below it: the row below has more cells, and text in the same column that
    /// goes on in lower case as close under it as the lines of a paragraph, starting near where it starts.
    /// </summary>
    private static bool Leads(List<TableCell> cells, List<TableCell> next)
    {
        var filled = cells.FindAll(c => c.Lines.Count > 0);
        if (filled.Count != 1 || next.Count(c => c.Lines.Count > 0) < 2) return false;
        var lead = filled[0];
        var below = next.Find(c => c.Column == lead.Column && c.ColumnSpan == lead.ColumnSpan && c.Lines.Count > 0);
        if (below == null || below.Lines[0].Words[0].Text is not { Length: > 0 } t || !char.IsLower(t[0])) return false;
        var last = lead.Lines[^1];
        return below.Lines[0].Top - last.Bottom <= 0.6 * last.Size && Math.Abs(below.Lines[0].X0 - lead.Lines[0].X0) <= last.Size;
    }

    /// <summary>Whether most of a column's entries share their left side, their right side or their centre.</summary>
    private static bool LinedUp(List<TableCell> cells)
    {
        static int Most(List<double> values)
        {
            values.Sort();
            int best = 0;
            for (int i = 0, j = 0; i < values.Count; i++)
            {
                while (values[i] - values[j] > AlignTolerance) j++;
                best = Math.Max(best, i - j + 1);
            }
            return best;
        }
        var lefts = cells.Select(c => c.Lines.Min(l => l.X0)).ToList();
        var rights = cells.Select(c => c.Lines.Max(l => l.X1)).ToList();
        var centres = cells.Select(c => (c.Lines.Min(l => l.X0) + c.Lines.Max(l => l.X1)) / 2).ToList();
        int most = Math.Max(Most(lefts), Math.Max(Most(rights), Most(centres)));
        return most >= Math.Max(2, 0.7 * cells.Count);
    }

    /// <summary>
    /// Whether the row labels of a table's first column line up in a few levels of indent, as group headings and the
    /// rows under them do: nearly all entries start at one of at most three places, each a little in from the one
    /// before, and the outermost and the next level each hold two or more entries.
    /// </summary>
    private static bool Indented(List<TableCell> cells)
    {
        var lefts = cells.Select(c => c.Lines.Min(l => l.X0)).OrderBy(x => x).ToList();
        double size = cells.Max(c => c.Lines.Max(l => l.Size));
        var levels = new List<(double X, int Count)>();
        foreach (double x in lefts)
        {
            if (levels.Count > 0 && x - levels[^1].X <= AlignTolerance) levels[^1] = (levels[^1].X, levels[^1].Count + 1);
            else levels.Add((x, 1));
        }
        int kept = 0;
        for (int i = 0; i < levels.Count && i < 3; i++)
        {
            if (i > 0 && levels[i].X - levels[i - 1].X > 1.5 * size) break;
            kept += levels[i].Count;
        }
        return levels.Count >= 2 && levels[0].Count >= 2 && levels[1].Count >= 2 && kept >= 0.9 * cells.Count;
    }

    /// <summary>
    /// Whether a line of cells continues the cells of the row above, as the second line of a wrapped cell does: set as
    /// close as the lines of a paragraph, in fewer columns than the row and only in columns it has text in, starting
    /// where that text starts, and either leaving the first column empty or following text that filled its column.
    /// </summary>
    private static bool Continues(List<TableCell> cells, List<TableCell> previous, List<Band> bands, double gap, double size)
    {
        if (gap > 0.6 * size || cells.Count(c => c.Lines.Count > 0) >= previous.Count(c => c.Lines.Count > 0)) return false;
        bool firstEmpty = !cells.Exists(c => c.Column == 0);
        foreach (var c in cells)
        {
            var above = previous.Find(p => p.Column == c.Column && p.ColumnSpan == c.ColumnSpan);
            if (above == null || above.Lines.Count == 0) return false;
            if (Math.Abs(c.Lines.Min(l => l.X0) - above.Lines[^1].X0) > AlignTolerance) return false;
            var band = bands[c.Column];
            if (!firstEmpty && above.Lines[^1].X1 - above.Lines[^1].X0 < 0.75 * (band.X1 - band.X0)) return false;
        }
        return true;
    }
}

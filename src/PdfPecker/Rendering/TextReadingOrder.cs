using System.Text;
using PdfPecker.Rendering.Html;

namespace PdfPecker.Rendering;

/// <summary>
/// Puts the lines of a page in reading order: the order of the blocks the structured HTML and Markdown are written
/// from, which follow a tagged PDF's structure tree and read text columns and boxes side by side one after the other.
/// Each word goes to the part of the page whose block covers it, a line that runs across parts is split, and the lines
/// of a part keep their order on the page, so a table's rows stay lines as they are.
/// </summary>
internal static class TextReadingOrder
{
    /// <summary>The side of the squares, in points, that the blocks' words are filed under to find the one a word is in.</summary>
    private const double Cell = 12;

    /// <summary>
    /// The fewest lines each part of a layout's side-by-side group must hold to be read on its own. A single line beside
    /// other text, such as a figure at the end of a row of labels, is more often the end of that row than a column.
    /// </summary>
    private const int MinPartLines = 2;

    /// <summary>How much of the shorter of two side-by-side parts must sit beside the other for them to be read apart.</summary>
    private const double MinOverlap = 0.5;

    /// <summary>
    /// The characters, spaces included, each part's lines must hold on average for side-by-side parts that split lines
    /// to be read apart: a narrow column of running text has some twenty or more, a label or a figure a dozen or fewer.
    /// </summary>
    private const int RunningText = 16;

    /// <summary>A word covering more squares than this is checked against every word on the page instead.</summary>
    private const int MaxCells = 64;

    /// <summary>The most words of a page covering more than <see cref="MaxCells"/> squares.</summary>
    private const int MaxLarge = 256;

    public static PdfTextPage Arrange(PdfTextPage page, List<FlowBlock> blocks, bool tagged, CancellationToken cancellationToken)
    {
        var parts = new List<List<FlowWord>>();
        var untagged = new List<bool>();
        if (tagged) foreach (var block in blocks) Tagged(block, block.Untagged, parts, untagged);
        else Layout(blocks, parts);
        if (parts.Count < 2) return page;

        var grid = new Dictionary<(int, int), List<(FlowWord Word, int Part)>>();
        var large = new List<(FlowWord Word, int Part)>();
        for (int p = 0; p < parts.Count; p++)
        {
            foreach (var w in parts[p])
            {
                var (c0, r0, c1, r1) = Cells(w.X0, w.Top, w.X1, w.Bottom);
                if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCells)
                {
                    // Every word is checked against each large word, so a page of many, as hostile input can make,
                    // keeps its lines as they are rather than take time that grows with their square.
                    if (large.Count >= MaxLarge) return page;
                    large.Add((w, p));
                    continue;
                }
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        if (!grid.TryGetValue((c, r), out var list)) grid[(c, r)] = list = new List<(FlowWord, int)>();
                        list.Add((w, p));
                    }
            }
        }

        // The part each word of the page's upright lines is in; text in other directions stays after it, as it was.
        var lines = page.Lines;
        var wordParts = new int[lines.Count][];
        // On a tagged page, where in its part each line's first word is, so the lines of an element that runs on from
        // one column to the next, which the tags hold in their order, keep that order rather than the page's.
        var wordRanks = tagged ? new int[lines.Count][] : null;
        var ranks = tagged ? new Dictionary<FlowWord, int>(ReferenceEqualityComparer.Instance) : null;
        if (ranks != null)
            foreach (var part in parts)
                for (int i = 0; i < part.Count; i++) ranks.TryAdd(part[i], i);
        int lastPart = -1;
        for (int l = 0; l < lines.Count; l++)
        {
            if ((l & 0xFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var words = lines[l].Words;
            var found = wordParts[l] = new int[words.Count];
            if (lines[l].Angle != 0)
            {
                Array.Fill(found, int.MaxValue);
                continue;
            }
            var rank = wordRanks != null ? wordRanks[l] = new int[words.Count] : null;
            for (int i = 0; i < words.Count; i++)
            {
                found[i] = PartOf(words[i], out var match);
                if (rank != null) rank[i] = match != null && ranks!.TryGetValue(match, out int r) ? r : int.MaxValue;
            }
            // On a tagged page, text the tags leave out that shares a line with tagged text, such as a list item's
            // number, goes with that text.
            if (untagged.Count > 0 && Array.Exists(found, p => p >= 0 && !untagged[p]))
                for (int i = 0; i < found.Length; i++) if (found[i] >= 0 && untagged[found[i]]) found[i] = -1;
            // Words no block holds, such as the dots of a leader or a copy of text drawn as its shadow, go with the
            // words beside them on their line, or with the line before.
            int known = Array.FindIndex(found, p => p >= 0);
            if (known < 0) Array.Fill(found, Math.Max(lastPart, 0));
            else
            {
                for (int i = 0; i < known; i++) found[i] = found[known];
                for (int i = known + 1; i < found.Length; i++) if (found[i] < 0) found[i] = found[i - 1];
            }
            lastPart = found[^1];
        }

        // On a tagged page, a line of text the tags leave out, such as a running header or the value shown in a form
        // field, is placed by its height on the page: before the first tagged part below it.
        double[]? place = null;
        if (untagged.Contains(true))
        {
            var tops = parts.Select(p => p.Min(w => w.Top)).ToArray();
            place = new double[lines.Count];
            for (int l = 0; l < lines.Count; l++)
            {
                place[l] = double.NaN;
                if (lines[l].Angle != 0 || !Array.TrueForAll(wordParts[l], p => untagged[p])) continue;
                int below = Enumerable.Range(0, parts.Count).FirstOrDefault(p => !untagged[p] && tops[p] > lines[l].Y, parts.Count);
                place[l] = below - 0.5;
            }
        }

        // The pieces of each line in each part, ordered by part and then as the lines are on the page.
        var pieces = new List<(double Part, int Line, List<PdfTextWord>? Words)>(lines.Count);
        var pieceRanks = new List<int>(lines.Count);
        for (int l = 0; l < lines.Count; l++)
        {
            var found = wordParts[l];
            bool whole = Array.TrueForAll(found, p => p == found[0]);
            if (whole || place != null && !double.IsNaN(place[l]))
            {
                pieces.Add((place != null && !double.IsNaN(place[l]) ? place[l] : found[0], l, null));
                pieceRanks.Add(wordRanks == null || wordRanks[l] == null ? 0 : wordRanks[l].Min());
                continue;
            }
            var byPart = new Dictionary<int, List<PdfTextWord>>();
            var order = new List<int>();
            for (int i = 0; i < found.Length; i++)
            {
                if (!byPart.TryGetValue(found[i], out var list)) { byPart[found[i]] = list = new List<PdfTextWord>(); order.Add(found[i]); }
                list.Add(lines[l].Words[i]);
            }
            foreach (int p in order)
            {
                pieces.Add((p, l, byPart[p]));
                int least = int.MaxValue;
                if (wordRanks != null && wordRanks[l] != null)
                    for (int i = 0; i < found.Length; i++) if (found[i] == p) least = Math.Min(least, wordRanks[l][i]);
                pieceRanks.Add(wordRanks == null ? 0 : least);
            }
        }
        var sorted = pieces.Select((piece, i) => (piece, i)).OrderBy(e => e.piece.Part)
            .ThenBy(e => pieceRanks[e.i])
            .ThenBy(e => e.i).Select(e => e.piece).ToList();
        bool unchanged = true;
        for (int k = 0; k < sorted.Count && unchanged; k++) unchanged = sorted[k].Words == null && sorted[k].Line == k;
        if (unchanged) return page;

        var result = new List<PdfTextLine>(sorted.Count);
        var sb = new StringBuilder();
        for (int k = 0; k < sorted.Count; k++)
        {
            var (_, l, words) = sorted[k];
            var line = words == null ? lines[l] : Piece(lines[l], words);
            if (k > 0)
            {
                var (_, previousLine, previousWords) = sorted[k - 1];
                var previous = result[^1];
                // Lines that follow each other on the page as they did keep the break between them. Elsewhere the space
                // between the lines tells, as it does on the page, and going back up to the next column or box starts
                // a paragraph.
                bool paragraph = words == null && previousWords == null && previousLine == l - 1 ? line.ParagraphBefore
                    : line.Angle != previous.Angle || line.Baseline <= previous.Baseline
                        || TextLayout.ParagraphBreak(previous.Baseline, previous.Size, line.Baseline, line.Size);
                sb.Append(paragraph ? "\n\n" : "\n");
            }
            sb.Append(line.Text);
            result.Add(line);
        }
        return new PdfTextPage(page.PageNumber, page.Width, page.Height, sb.ToString(), result);

        int PartOf(PdfTextWord word, out FlowWord? match)
        {
            double x0 = word.X, y0 = word.Y, x1 = word.X + word.Width, y1 = word.Y + word.Height;
            int best = -1;
            FlowWord? bestWord = null;
            double most = 0;
            var (c0, r0, c1, r1) = Cells(x0, y0, x1, y1);
            if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > MaxCells)
            {
                // A huge word is looked up by its centre alone.
                c0 = c1 = At((x0 + x1) / 2);
                r0 = r1 = At((y0 + y1) / 2);
            }
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                    if (grid.TryGetValue((c, r), out var list))
                        foreach (var (w, p) in list) Consider(w, p);
            foreach (var (w, p) in large) Consider(w, p);
            match = bestWord;
            return best;

            void Consider(FlowWord w, int p)
            {
                double overlap = Math.Max(0, Math.Min(x1, w.X1) - Math.Max(x0, w.X0)) * Math.Max(0, Math.Min(y1, w.Bottom) - Math.Max(y0, w.Top));
                if (overlap > most || (overlap == most && overlap > 0 && p < best)) { most = overlap; best = p; bestWord = w; }
            }
        }
    }

    /// <summary>The square a position is in; a position that is not a number, as a damaged PDF can give, counts as 0.</summary>
    private static int At(double v) => double.IsNaN(v) ? 0 : (int)Math.Clamp(Math.Floor(v / Cell), -1_000_000, 1_000_000);

    private static (int C0, int R0, int C1, int R1) Cells(double x0, double y0, double x1, double y1)
    {
        int c0 = At(x0), r0 = At(y0);
        return (c0, r0, Math.Max(c0, At(x1)), Math.Max(r0, At(y1)));
    }

    /// <summary>
    /// The words of a tagged page's blocks, a part per block in the order of the tags: the tags say where each
    /// paragraph, list item and table is, so their order is the reading order.
    /// </summary>
    private static void Tagged(FlowBlock block, bool outside, List<List<FlowWord>> parts, List<bool> untagged)
    {
        if (block is GroupBlock g)
        {
            foreach (var part in Parts(g))
                foreach (var b in part) Tagged(b, outside, parts, untagged);
        }
        else if (Words(block) is { Count: > 0 } words)
        {
            parts.Add(words);
            untagged.Add(outside);
        }
    }

    /// <summary>
    /// The words of a page read from its layout: the blocks between two sets of side-by-side parts are one part, whose
    /// lines keep their order on the page, as the paragraphs and tables found there say nothing a line does not
    /// already; each side-by-side part is read whole before the next.
    /// </summary>
    private static void Layout(List<FlowBlock> blocks, List<List<FlowWord>> parts)
    {
        List<FlowWord>? current = null;
        foreach (var block in Columns(blocks))
        {
            if (block is GroupBlock g && SideBySide(g))
            {
                current = null;
                foreach (var part in Parts(g)) Layout(part, parts);
                continue;
            }
            if (current != null)
            {
                AddWords(block, current);
                continue;
            }
            var words = Words(block);
            if (words.Count > 0) parts.Add(current = words);
        }
    }

    /// <summary>
    /// The blocks with each run of text columns as one group. The layout ends a group of side-by-side parts where the
    /// columns' lines stop lining up, as where one column starts a paragraph and the other does not, so one run of
    /// columns can come as two groups, one after the other, and the lines of the longer column, where the other has
    /// ended or not yet begun, as a block of their own above or below the group. Reading those apart would read the
    /// top of every column before the rest of the first.
    /// </summary>
    internal static List<FlowBlock> Columns(List<FlowBlock> blocks)
    {
        var result = new List<FlowBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            if (block is GroupBlock g && SideBySide(g) && result.Count > 0)
            {
                // A second group under the first, its parts under the first's parts.
                if (result[^1] is GroupBlock previous && SideBySide(previous) && Continues(previous, g))
                {
                    var joined = Join(previous, g);
                    if (SideBySide(joined))
                    {
                        result[^1] = joined;
                        continue;
                    }
                }
                // Lines just above the group that sit in one of its columns are that column's start. A later column can
                // start higher than the first, as where the first opens with a heading and the space above it, so its
                // heading and first lines can lie above the group, one block after the other.
                GroupBlock? started = null;
                if (result[^1] is not GroupBlock)
                {
                    // The parts' extents grow with each block that joins, rather than being measured again.
                    var extents = g.Parts.Select(Extent).ToList();
                    int first = g.Parts.IndexOf(Parts(g).First());
                    while (result.Count > 0 && result[^1] is not GroupBlock && ColumnOf(extents, first, result[^1], above: true) is int top)
                    {
                        var start = result[^1];
                        started ??= Join(g, null);
                        started.Parts[top].Insert(0, start);
                        started.Top = Math.Min(started.Top, start.Top);
                        var e = Extent([start]);
                        extents[top] = (Math.Min(extents[top].X0, e.X0), Math.Max(extents[top].X1, e.X1), Math.Min(extents[top].Top, e.Top),
                            Math.Max(extents[top].Bottom, e.Bottom), Math.Max(extents[top].Size, e.Size));
                        result.RemoveAt(result.Count - 1);
                        if (top == first) break;
                    }
                }
                if (started != null)
                {
                    result.Add(started);
                    continue;
                }
            }
            // Lines just below a group that sit in one of its columns are that column's end.
            if (block is not GroupBlock && result.Count > 0 && result[^1] is GroupBlock group && SideBySide(group)
                && ColumnOf(group.Parts.Select(Extent).ToList(), group.Parts.IndexOf(Parts(group).First()), block, above: false) is int bottom)
            {
                var joined = Join(group, null);
                joined.Parts[bottom].Add(block);
                result[^1] = joined;
                continue;
            }
            result.Add(block);
        }
        return result;

        // Whether the second group's parts are the first's columns going on: as many, each under the one before it,
        // overlapping it across and no further below it than the space between two paragraphs. Boxes set side by side
        // in rows, as a grid of cards, have more space between the rows than their text has between its lines.
        static bool Continues(GroupBlock first, GroupBlock second)
        {
            if (first.Parts.Count != second.Parts.Count) return false;
            var a = first.Parts.Select(Extent).ToList();
            var b = second.Parts.Select(Extent).ToList();
            for (int i = 0; i < a.Count; i++)
            {
                double gap = b[i].Top - a[i].Bottom;
                if (gap < -Math.Max(a[i].Size, b[i].Size) / 2 || gap > 1.5 * Math.Max(a[i].Size, b[i].Size)) return false;
                if (Math.Min(a[i].X1, b[i].X1) - Math.Max(a[i].X0, b[i].X0) <= 0) return false;
                for (int j = 0; j < b.Count; j++)
                    if (j != i && Math.Min(a[i].X1, b[j].X1) - Math.Max(a[i].X0, b[j].X0) > 0) return false;
            }
            return true;
        }

        static GroupBlock Join(GroupBlock first, GroupBlock? second)
        {
            var joined = new GroupBlock { Top = first.Top, Left = first.Left };
            for (int i = 0; i < first.Parts.Count; i++)
            {
                var part = new List<FlowBlock>(first.Parts[i]);
                if (second != null) part.AddRange(second.Parts[i]);
                joined.Parts.Add(part);
            }
            return joined;
        }

        // The column a block above or below the group belongs to: the one part it lies across, as wide as that part's
        // lines, as running text that goes on from the column is, and no further from the part's first or last line
        // than the space between two paragraphs. A heading or a label over one column is shorter and is left where it
        // is. A heading above the part that is read first heads the group, as a section's title over a grid of cards
        // does, rather than its first column. Above a later part, where no title of the whole group can be, a heading
        // or a short line starts that part when it is no further from it than a line's height.
        static int? ColumnOf(List<(double X0, double X1, double Top, double Bottom, double Size)> parts, int first, FlowBlock block, bool above)
        {
            var words = Words(block);
            if (words.Count == 0) return null;
            double x0 = words.Min(w => w.X0), x1 = words.Max(w => w.X1), top = words.Min(w => w.Top), bottom = words.Max(w => w.Bottom);
            double size = words.Max(w => w.Size);
            int? found = null;
            for (int i = 0; i < parts.Count; i++)
            {
                var e = parts[i];
                if (Math.Min(x1, e.X1) - Math.Max(x0, e.X0) <= 0) continue;
                if (found != null || x0 < e.X0 - size || x1 > e.X1 + size) return null;
                double gap = above ? e.Top - bottom : top - e.Bottom;
                if (gap < -size / 2) return null;
                bool wide = x1 - x0 >= 0.8 * (e.X1 - e.X0);
                if (above && i != first ? gap > (wide ? 1.5 : 1) * Math.Max(size, e.Size)
                    : !wide || gap > 1.5 * Math.Max(size, e.Size) || above && block is TextBlock { Kind: TextKind.Heading }) return null;
                found = i;
            }
            return found;
        }

        static (double X0, double X1, double Top, double Bottom, double Size) Extent(List<FlowBlock> part)
        {
            var words = part.SelectMany(Words).ToList();
            if (words.Count == 0) return (0, 0, 0, 0, 0);
            return (words.Min(w => w.X0), words.Max(w => w.X1), words.Min(w => w.Top), words.Max(w => w.Bottom), words.Max(w => w.Size));
        }
    }

    private static List<FlowWord> Words(FlowBlock block)
    {
        var words = new List<FlowWord>();
        AddWords(block, words);
        return words;
    }

    /// <summary>Adds the words of a block to <paramref name="words"/>, line by line, cell by cell, part by part.</summary>
    private static void AddWords(FlowBlock block, List<FlowWord> words)
    {
        switch (block)
        {
            // Word by word, as a table's lines hold a word or two each, too few for a range to copy faster.
            case TextBlock t:
                foreach (var line in t.Lines)
                    foreach (var w in line.Words) words.Add(w);
                break;
            case TableBlock t:
                foreach (var cell in t.Cells)
                    foreach (var line in cell.Lines)
                        foreach (var w in line.Words) words.Add(w);
                break;
            case GroupBlock g:
                foreach (var part in g.Parts)
                    foreach (var b in part) AddWords(b, words);
                break;
        }
    }

    /// <summary>
    /// Whether a group the layout sets side by side is read one part after the other. Every part needs a few lines of
    /// its own and must sit beside the next part over most of its height, as columns do, rather than above or below
    /// it with only a gap at one side in common. Where reading the parts apart splits lines, every part must also hold
    /// running text, a good many letters to the line: short entries beside other text, as a grid or labels beside their
    /// values, are more often a table that has no rules to show it, and read across.
    /// </summary>
    internal static bool SideBySide(GroupBlock group)
    {
        var owner = new Dictionary<int, int>(); // the part each of the page's lines is in, or -1 for several
        bool running = true;
        List<(double Top, double Bottom)>? previous = null;
        for (int p = 0; p < group.Parts.Count; p++)
        {
            var lines = new Dictionary<int, (double Top, double Bottom, double Size)>();
            int letters = 0;
            foreach (var block in group.Parts[p])
                foreach (var w in Words(block))
                {
                    letters += w.Text.Length + 1;
                    lines[w.Line] = lines.TryGetValue(w.Line, out var l)
                        ? (Math.Min(l.Top, w.Top), Math.Max(l.Bottom, w.Bottom), Math.Max(l.Size, w.Size))
                        : (w.Top, w.Bottom, w.Size);
                }
            if (lines.Count < MinPartLines) return false;
            running &= letters >= RunningText * lines.Count;
            foreach (int line in lines.Keys) owner[line] = owner.TryGetValue(line, out int other) && other != p ? -1 : p;
            var spans = Spans(lines.Values);
            if (previous != null && Overlap(previous, spans) < MinOverlap * Math.Min(Length(previous), Length(spans))) return false;
            previous = spans;
        }
        return running || !owner.ContainsValue(-1);

        // The stretches down the page a part's lines cover, with the space between the lines of a paragraph filled in.
        static List<(double Top, double Bottom)> Spans(IEnumerable<(double Top, double Bottom, double Size)> lines)
        {
            var spans = new List<(double Top, double Bottom)>();
            foreach (var (top, bottom, size) in lines.OrderBy(l => l.Top))
            {
                if (spans.Count > 0 && top - spans[^1].Bottom <= size) spans[^1] = (spans[^1].Top, Math.Max(spans[^1].Bottom, bottom));
                else spans.Add((top, bottom));
            }
            return spans;
        }

        static double Length(List<(double Top, double Bottom)> spans) => spans.Sum(s => s.Bottom - s.Top);

        static double Overlap(List<(double Top, double Bottom)> a, List<(double Top, double Bottom)> b)
        {
            double overlap = 0;
            foreach (var x in a)
                foreach (var y in b)
                    overlap += Math.Max(0, Math.Min(x.Bottom, y.Bottom) - Math.Max(x.Top, y.Top));
            return overlap;
        }
    }

    /// <summary>A group's parts in reading order: from the left, or from the right where most of their text is right to left.</summary>
    internal static IEnumerable<List<FlowBlock>> Parts(GroupBlock group)
    {
        int rtl = 0, ltr = 0;
        foreach (var part in group.Parts)
            foreach (var block in part) Count(block, ref rtl, ref ltr);
        return rtl > ltr ? Enumerable.Reverse(group.Parts) : group.Parts;

        static void Count(FlowBlock block, ref int rtl, ref int ltr)
        {
            switch (block)
            {
                case TextBlock t:
                    foreach (var line in t.Lines)
                        if (line.RightToLeft) rtl += line.Words.Count; else ltr += line.Words.Count;
                    break;
                case TableBlock t:
                    int words = t.Cells.Sum(c => c.Lines.Sum(l => l.Words.Count));
                    if (t.RightToLeft) rtl += words; else ltr += words;
                    break;
                case GroupBlock g:
                    foreach (var part in g.Parts)
                        foreach (var b in part) Count(b, ref rtl, ref ltr);
                    break;
            }
        }
    }

    /// <summary>The words of a line that are in one part, as a line of their own on the same baseline.</summary>
    private static PdfTextLine Piece(PdfTextLine line, List<PdfTextWord> words)
    {
        double x0 = double.MaxValue, x1 = double.MinValue, size = 0;
        foreach (var w in words)
        {
            x0 = Math.Min(x0, w.X);
            x1 = Math.Max(x1, w.X + w.Width);
            size = Math.Max(size, w.FontSize);
        }
        var (x, y, width, height) = TextLayout.Box(x0, x1, line.Baseline, size, 1, 0);
        return new PdfTextLine(string.Join(' ', words.Select(w => w.Text)), words, x, y, width, height, line.Angle, line.RightToLeft)
        {
            Baseline = line.Baseline,
            Size = size,
        };
    }
}

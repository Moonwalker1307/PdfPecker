using System.Text;
using PdfPecker.Text;

namespace PdfPecker.Rendering;

/// <summary>
/// Rebuilds reading order from the glyphs of a page: glyphs on one baseline form a line, gaps wider than a fraction of
/// the font size become spaces, lines go from top to bottom, and right-to-left runs come out in logical order.
/// </summary>
internal static class TextLayout
{
    private struct Item
    {
        public int Glyph; // index into the page's glyphs, or -1 for a space this layout inserts
        public double U0, U1, V, Size;
    }

    private sealed class Line
    {
        public readonly List<Item> Items = new();
        public double V, Size;
    }

    public static PdfTextPage Build(List<TextGlyph> glyphs, int pageNumber, double width, double height, bool expandLigatures,
        CancellationToken cancellationToken = default)
    {
        // Glyphs whose baselines point the same way are laid out together; rotated text gets its own lines.
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            int angle = (int)Math.Round(Math.Atan2(g.DirY, g.DirX) * 180 / Math.PI);
            if (angle < 0) angle += 360;
            angle %= 360;
            if (!groups.TryGetValue(angle, out var list)) groups[angle] = list = new List<int>();
            list.Add(i);
        }

        var lines = new List<PdfTextLine>();
        var sb = new StringBuilder();
        var attached = new MarkTargets(glyphs);
        foreach (var angle in groups.Keys.OrderBy(a => a))
        {
            double rad = angle * Math.PI / 180, cx = Math.Cos(rad), cy = Math.Sin(rad);
            var groupLines = BuildLines(glyphs, groups[angle], cx, cy, attached, cancellationToken);
            Line? previous = null;
            foreach (var line in groupLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var textLine = MakeLine(glyphs, line, cx, cy, angle, expandLigatures);
                if (textLine == null) continue;
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                    // Single-spaced text has lines 1.1 to 1.5 font sizes apart, so a wider gap is taken as a paragraph break.
                    if (previous == null || line.V - previous.V > 1.75 * Math.Min(line.Size, previous.Size)) sb.Append('\n');
                }
                sb.Append(textLine.Text);
                lines.Add(textLine);
                previous = line;
            }
        }
        return new PdfTextPage(pageNumber, width, height, sb.ToString(), lines);
    }

    /// <summary>Clusters glyphs into lines by baseline, top to bottom, each sorted along the baseline.</summary>
    private static List<Line> BuildLines(List<TextGlyph> glyphs, List<int> indices, double cx, double cy, MarkTargets attached,
        CancellationToken cancellationToken)
    {
        var items = new List<Item>(indices.Count);
        foreach (int i in indices)
        {
            var g = glyphs[i];
            // Line space: u along the baseline, v across it, growing down the page for unrotated text.
            double u0 = g.X * cx + g.Y * cy, u1 = g.EndX * cx + g.EndY * cy, v = -g.X * cy + g.Y * cx;
            if (u1 < u0) (u0, u1) = (u1, u0);
            items.Add(new Item { Glyph = i, U0 = u0, U1 = u1, V = v, Size = Math.Max(g.Size, 1e-3) });
        }
        // Combining marks (Arabic vowel signs, Hebrew points, accents drawn on their own) are often placed above or
        // below their letter, off its baseline; they are joined to the letter they sit on after the lines are built.
        var marks = items.Where(it => IsMark(glyphs[it.Glyph].Text)).ToList();
        if (marks.Count > 0 && marks.Count < items.Count) items.RemoveAll(it => IsMark(glyphs[it.Glyph].Text));
        else marks.Clear();
        items.Sort((a, b) => a.V != b.V ? a.V.CompareTo(b.V) : a.U0.CompareTo(b.U0));

        var lines = new List<Line>();
        Line? current = null;
        foreach (var it in items)
        {
            // Superscripts and subscripts sit within about half a font size of their line's baseline.
            if (current != null && it.V - current.V <= 0.5 * (current.Size + it.Size) / 2)
            {
                current.Items.Add(it);
                if (it.Size > current.Size) { current.Size = it.Size; current.V = it.V; }
                continue;
            }
            current = new Line { V = it.V, Size = it.Size };
            current.Items.Add(it);
            lines.Add(current);
        }
        lines = SeparateBaselines(lines);
        foreach (var line in lines) line.Items.Sort((a, b) => a.U0 != b.U0 ? a.U0.CompareTo(b.U0) : a.Glyph.CompareTo(b.Glyph));
        marks.Sort((a, b) => a.Glyph.CompareTo(b.Glyph));
        for (int m = 0; m < marks.Count; m++)
        {
            if ((m & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!AttachToNeighbour(glyphs, marks[m], cx, cy, attached)) AttachMark(glyphs, lines, marks[m], attached);
        }
        // The lines of this group are written out next, so they must see the marks joined to their letters.
        attached.Flush();
        return lines;
    }

    /// <summary>
    /// Splits a line whose glyphs sit on baselines that are close but distinct and overlap along the line, such as two
    /// small lines set beside one large heading, which the tolerance for superscripts would otherwise join and
    /// interleave letter by letter. The glyphs of each baseline stay with the line while they overlap none of its
    /// glyphs, so a superscript between words stays in its line; a baseline that overlaps goes to a line of its own.
    /// </summary>
    private static List<Line> SeparateBaselines(List<Line> lines)
    {
        List<Line>? result = null;
        for (int l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            // The line's baselines: runs of glyphs, sorted across the line, less than a fifth of the smaller size apart.
            List<List<Item>>? groups = null;
            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (var it in line.Items) { minV = Math.Min(minV, it.V); maxV = Math.Max(maxV, it.V); }
            if (maxV - minV > 0.1 * line.Size)
            {
                var sorted = line.Items.OrderBy(it => it.V).ToList();
                groups = new List<List<Item>> { new() { sorted[0] } };
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].V - sorted[i - 1].V > 0.2 * Math.Min(sorted[i].Size, sorted[i - 1].Size)) groups.Add(new List<Item>());
                    groups[^1].Add(sorted[i]);
                }
            }
            if (groups == null || groups.Count < 2)
            {
                result?.Add(line);
                continue;
            }
            result ??= lines.GetRange(0, l);
            // The largest text first: it is the line's own; the rest join it while they overlap nothing in it.
            groups.Sort((a, b) => b.Max(it => it.Size).CompareTo(a.Max(it => it.Size)) is var c and not 0 ? c : b.Count.CompareTo(a.Count));
            while (groups.Count > 0)
            {
                var kept = new List<Item>(groups[0]);
                kept.Sort((a, b) => a.U0.CompareTo(b.U0));
                var deferred = new List<List<Item>>();
                for (int g = 1; g < groups.Count; g++)
                {
                    if (Overlaps(kept, groups[g])) { deferred.Add(groups[g]); continue; }
                    kept.AddRange(groups[g]);
                    kept.Sort((a, b) => a.U0.CompareTo(b.U0));
                }
                var split = new Line();
                double size = 0;
                foreach (var it in kept)
                {
                    split.Items.Add(it);
                    if (it.Size > size) { size = it.Size; split.V = it.V; }
                }
                split.Size = size;
                result.Add(split);
                groups = deferred;
            }
        }
        if (result == null) return lines;
        result.Sort((a, b) => a.V.CompareTo(b.V));
        return result;

        // Whether any glyph of the group covers more than a third of a glyph of the line, sorted along it.
        static bool Overlaps(List<Item> line, List<Item> group)
        {
            foreach (var it in group)
            {
                int lo = 0, hi = line.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (line[mid].U0 < it.U1) lo = mid + 1; else hi = mid;
                }
                // Glyphs that start before this one ends; the widest glyph is at most a few sizes, so few are checked.
                for (int k = lo - 1; k >= 0 && line[k].U0 > it.U0 - 4 * Math.Max(it.Size, line[k].Size); k--)
                {
                    double overlap = Math.Min(it.U1, line[k].U1) - Math.Max(it.U0, line[k].U0);
                    if (overlap > Math.Min(it.U1 - it.U0, line[k].U1 - line[k].U0) / 3) return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Appends a combining mark to the letter drawn next to it in the content: the one after it for right-to-left
    /// letters, which PDFs draw in visual order with each mark before its letter, else the one before it. Marks are
    /// often placed over the edge of their letter, so this is more reliable than position, which only checks it.
    /// </summary>
    private static bool AttachToNeighbour(List<TextGlyph> glyphs, Item mark, double cx, double cy, MarkTargets attached)
    {
        int prev = attached.PreviousLetter(mark.Glyph), next = attached.NextLetter(mark.Glyph);
        bool rtl = next < glyphs.Count && attached.ClassOf(next) is BidiClass.R or BidiClass.AL;
        foreach (int target in rtl ? new[] { next, prev } : new[] { prev, next })
        {
            if (target < 0 || target >= glyphs.Count) continue;
            var g = glyphs[target];
            double u0 = g.X * cx + g.Y * cy, u1 = g.EndX * cx + g.EndY * cy, v = -g.X * cy + g.Y * cx;
            if (u1 < u0) (u0, u1) = (u1, u0);
            double centre = (mark.U0 + mark.U1) / 2;
            double d = centre < u0 ? u0 - centre : centre > u1 ? centre - u1 : 0;
            if (Math.Abs(v - mark.V) > 1.2 * Math.Max(g.Size, mark.Size) || d > 0.5 * mark.Size || attached.IsSpace(target)) continue;
            attached.Append(target, mark.Glyph);
            return true;
        }
        return false;
    }

    /// <summary>Appends a combining mark to the letter it is drawn on: the nearest line, then the glyph under its centre.</summary>
    private static void AttachMark(List<TextGlyph> glyphs, List<Line> lines, Item mark, MarkTargets attached)
    {
        int lo = 0, hi = lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].V < mark.V) lo = mid + 1; else hi = mid;
        }
        Line? best = null;
        for (int k = Math.Max(0, lo - 1); k <= Math.Min(lines.Count - 1, lo); k++)
            if (best == null || Math.Abs(lines[k].V - mark.V) < Math.Abs(best.V - mark.V)) best = lines[k];
        var target = -1;
        if (best != null && Math.Abs(best.V - mark.V) <= 1.2 * Math.Max(best.Size, mark.Size))
        {
            double centre = (mark.U0 + mark.U1) / 2, bestDistance = double.MaxValue;
            // The items are sorted along the baseline, so only the few that start just before the mark's centre and the
            // first one after it are looked at: a letter further back would have to be drawn across many others to be
            // nearer, and looking at every letter for every mark is quadratic on a line of many marks.
            var items = best.Items;
            int end = FirstAfter(items, centre);
            for (int k = Math.Max(0, end - MarkWindow); k <= end && k < items.Count; k++)
            {
                var it = items[k];
                double d = centre < it.U0 ? it.U0 - centre : centre > it.U1 ? centre - it.U1 : 0;
                if (d < bestDistance) { bestDistance = d; target = it.Glyph; }
            }
            if (bestDistance > mark.Size) target = -1;
        }
        if (target < 0)
        {
            // A mark with no letter near it is kept as a character of its own on the nearest line.
            if (best == null) lines.Add(best = new Line { V = mark.V, Size = mark.Size });
            best.Items.Insert(FirstAfter(best.Items, mark.U0), mark);
            return;
        }
        attached.Append(target, mark.Glyph);
    }

    /// <summary>How many items before a mark's centre <see cref="AttachMark"/> considers as the letter it sits on.</summary>
    private const int MarkWindow = 32;

    /// <summary>The index of the first of <paramref name="items"/>, sorted by start, that starts after <paramref name="u"/>.</summary>
    private static int FirstAfter(List<Item> items, double u)
    {
        int lo = 0, hi = items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (items[mid].U0 > u) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// The marks joined to letters while a group's lines are built. A letter can collect any number of marks, so they
    /// are gathered in a builder and written into its text once, instead of copying the growing text for every mark;
    /// the letter's bidi class and whether it is a space are kept up to date as marks arrive, so reading them never
    /// rescans that text. The nearest letters on either side of each glyph are found once, in one pass each way, so a
    /// long run of marks is not walked again for every mark in it. Whether a glyph is a mark never changes, as glyphs
    /// always have text and adding marks to a letter leaves it a letter.
    /// </summary>
    private sealed class MarkTargets
    {
        private readonly List<TextGlyph> _glyphs;
        private readonly StringBuilder?[] _text;
        private readonly BidiClass?[] _class;
        private readonly bool?[] _space;
        private readonly List<int> _pending = new();
        private int[]? _previous, _next;

        public MarkTargets(List<TextGlyph> glyphs)
        {
            _glyphs = glyphs;
            _text = new StringBuilder?[glyphs.Count];
            _class = new BidiClass?[glyphs.Count];
            _space = new bool?[glyphs.Count];
        }

        /// <summary>The nearest glyph before <paramref name="i"/> that is not a mark, or -1.</summary>
        public int PreviousLetter(int i)
        {
            if (_previous == null) FindLetters();
            return _previous![i];
        }

        /// <summary>The nearest glyph after <paramref name="i"/> that is not a mark, or the glyph count.</summary>
        public int NextLetter(int i)
        {
            if (_next == null) FindLetters();
            return _next![i];
        }

        private void FindLetters()
        {
            int n = _glyphs.Count;
            var isMark = new bool[n];
            for (int i = 0; i < n; i++) isMark[i] = IsMark(_glyphs[i].Text);
            _previous = new int[n];
            _next = new int[n];
            for (int i = 0, last = -1; i < n; i++)
            {
                _previous[i] = last;
                if (!isMark[i]) last = i;
            }
            for (int i = n - 1, last = n; i >= 0; i--)
            {
                _next[i] = last;
                if (!isMark[i]) last = i;
            }
        }

        public BidiClass ClassOf(int i) => _class[i] ??= TextLayout.ClassOf(Text(i));

        public bool IsSpace(int i) => _space[i] ??= TextLayout.IsSpace(Text(i));

        /// <summary>Joins the text of the mark glyph <paramref name="mark"/> to glyph <paramref name="target"/>.</summary>
        public void Append(int target, int mark)
        {
            string text = Text(mark);
            var cls = ClassOf(target);
            bool space = IsSpace(target);
            if (_text[target] is not { } sb)
            {
                _text[target] = sb = new StringBuilder(_glyphs[target].Text);
                _pending.Add(target);
            }
            sb.Append(text);
            // Reading on from where the letter's own text left off gives the class of the joined text.
            _class[target] = TextLayout.ClassOf(text, cls);
            _space[target] = space && TextLayout.IsSpace(text);
        }

        /// <summary>Writes the marks gathered so far into the text of their letters.</summary>
        public void Flush()
        {
            foreach (int i in _pending)
            {
                var g = _glyphs[i];
                g.Text = _text[i]!.ToString();
                _glyphs[i] = g;
                _text[i] = null;
            }
            _pending.Clear();
        }

        private string Text(int i) => _text[i] is { } sb ? sb.ToString() : _glyphs[i].Text;
    }

    private static bool IsMark(string text)
    {
        foreach (char c in text)
            if (!UnicodeTables.IsMark(c)) return false;
        return text.Length > 0;
    }

    private static bool IsSpace(string s)
    {
        foreach (char c in s) if (!char.IsWhiteSpace(c)) return false;
        return true;
    }

    private static PdfTextLine? MakeLine(List<TextGlyph> glyphs, Line line, double cx, double cy, int angle, bool expandLigatures)
    {
        // Visual order with spaces: a glyph drawn twice in the same place (fake bold) counts once, and a gap wider than
        // a fraction of the font's space becomes a space when the PDF does not draw one.
        var visual = new List<Item>(line.Items.Count + 8);
        Item? prev = null;
        foreach (var it in line.Items)
        {
            var g = glyphs[it.Glyph];
            if (prev is Item p)
            {
                var pg = glyphs[p.Glyph];
                if (pg.Text == g.Text && Math.Abs(it.U0 - p.U0) < 0.1 * it.Size && Math.Abs(it.V - p.V) < 0.1 * it.Size) continue;
                bool spaceDrawn = IsSpace(pg.Text) || IsSpace(g.Text);
                double size = Math.Min(it.Size, p.Size);
                double space = Math.Min(pg.SpaceWidth > 0 ? pg.SpaceWidth : double.MaxValue, g.SpaceWidth > 0 ? g.SpaceWidth : double.MaxValue);
                double threshold = space < double.MaxValue ? Math.Clamp(0.5 * space, 0.08 * size, 0.2 * size) : 0.15 * size;
                // Text placed on its own, such as the next table cell, needs only a small gap to be a separate word.
                if (g.Moved) threshold = Math.Min(threshold, 0.1 * size);
                if (!spaceDrawn && it.U0 - p.U1 > threshold)
                    visual.Add(new Item { Glyph = -1, U0 = p.U1, U1 = it.U0, V = it.V, Size = size });
            }
            visual.Add(it);
            prev = it;
        }

        var order = LogicalOrder(glyphs, visual, out bool rtl);

        var words = new List<PdfTextWord>();
        var lineText = new StringBuilder();
        var word = new StringBuilder();
        double wu0 = double.MaxValue, wu1 = double.MinValue, wv = 0, wsize = 0;
        string font = "";
        double lu0 = double.MaxValue, lu1 = double.MinValue, lsize = 0;

        void Flush()
        {
            if (word.Length == 0) return;
            var (x, y, w, h) = Box(wu0, wu1, wv, wsize, cx, cy);
            words.Add(new PdfTextWord(word.ToString(), x, y, w, h, wsize, font)
            {
                OriginX = wu0 * cx - wv * cy,
                OriginY = wu0 * cy + wv * cx,
                Advance = wu1 - wu0,
            });
            if (lineText.Length > 0) lineText.Append(' ');
            lineText.Append(word);
            word.Clear();
            wu0 = double.MaxValue; wu1 = double.MinValue; wsize = 0;
        }

        foreach (int k in order)
        {
            var it = visual[k];
            if (it.Glyph < 0 || IsSpace(glyphs[it.Glyph].Text)) { Flush(); continue; }
            var g = glyphs[it.Glyph];
            if (word.Length == 0) { font = g.Font; wv = it.V; }
            AppendText(word, g.Text, expandLigatures);
            wu0 = Math.Min(wu0, it.U0); wu1 = Math.Max(wu1, it.U1);
            if (it.Size > wsize) { wsize = it.Size; wv = it.V; }
            lu0 = Math.Min(lu0, it.U0); lu1 = Math.Max(lu1, it.U1); lsize = Math.Max(lsize, it.Size);
        }
        Flush();
        if (words.Count == 0) return null;
        var (lx, ly, lw, lh) = Box(lu0, lu1, line.V, lsize, cx, cy);
        return new PdfTextLine(lineText.ToString(), words, lx, ly, lw, lh, angle == 0 ? 0 : 360 - angle, rtl);
    }

    /// <summary>The page box, in points from the top left, of a run from u0 to u1 on the baseline v of the line frame.</summary>
    private static (double X, double Y, double W, double H) Box(double u0, double u1, double v, double size, double cx, double cy)
    {
        // Ascent and descent are taken as the usual 0.8 and 0.2 of the font size, as the fonts' own metrics often lie.
        double v0 = v - 0.8 * size, v1 = v + 0.2 * size;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (u, vv) in new[] { (u0, v0), (u1, v0), (u0, v1), (u1, v1) })
        {
            double x = u * cx - vv * cy, y = u * cy + vv * cx;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static void AppendText(StringBuilder sb, string text, bool expandLigatures)
    {
        foreach (char c in text)
        {
            // Latin ligature characters are what a PDF says when it maps a ligature glyph to one code point.
            string? expanded = expandLigatures ? c switch
            {
                'ﬀ' => "ff", 'ﬁ' => "fi", 'ﬂ' => "fl", 'ﬃ' => "ffi", 'ﬄ' => "ffl", 'ﬅ' => "ſt", 'ﬆ' => "st",
                _ => null,
            } : null;
            if (expanded != null) sb.Append(expanded);
            else if (c != '­' && c != '\0') sb.Append(c);
        }
    }

    /// <summary>
    /// The order to read a line's items in. The items are in visual order, left to right; a line with right-to-left
    /// letters is given bidi levels from its strong characters, and reordering by those levels (rule L2, which undoes
    /// itself) turns visual order back into logical order.
    /// </summary>
    private static int[] LogicalOrder(List<TextGlyph> glyphs, List<Item> visual, out bool rtl)
    {
        int n = visual.Count;
        var cls = new BidiClass[n];
        int rCount = 0, lCount = 0;
        for (int i = 0; i < n; i++)
        {
            var c = visual[i].Glyph < 0 ? BidiClass.WS : ClassOf(glyphs[visual[i].Glyph].Text);
            cls[i] = c;
            if (c is BidiClass.R or BidiClass.AL) rCount++;
            else if (c == BidiClass.L) lCount++;
        }
        rtl = rCount > lCount;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        if (rCount == 0) return order;

        var levels = new byte[n];
        // Strong and number levels first: right-to-left letters at 1, left-to-right letters at the next even level, and
        // numbers left to right inside right-to-left text.
        var dir = new int[n]; // 1 left to right, 2 right to left, 3 number, 0 neutral
        for (int i = 0; i < n; i++)
        {
            dir[i] = cls[i] switch
            {
                BidiClass.L => 1,
                BidiClass.R or BidiClass.AL => 2,
                BidiClass.EN or BidiClass.AN => 3,
                _ => 0,
            };
        }
        // The nearest strong or number direction on each side of every item, and the nearest strong one, found in one
        // pass each way so a long run of neutrals is not walked again for every item in it.
        var leftAny = new int[n];
        var leftStrong = new int[n];
        var rightAny = new int[n];
        var rightStrong = new int[n];
        for (int i = 0, any = 0, strong = 0; i < n; i++)
        {
            leftAny[i] = any;
            leftStrong[i] = strong;
            if (dir[i] != 0) any = dir[i];
            if (dir[i] is 1 or 2) strong = dir[i];
        }
        for (int i = n - 1, any = 0, strong = 0; i >= 0; i--)
        {
            rightAny[i] = any;
            rightStrong[i] = strong;
            if (dir[i] != 0) any = dir[i];
            if (dir[i] is 1 or 2) strong = dir[i];
        }
        for (int i = 0; i < n; i++)
        {
            int d = dir[i];
            if (d == 0)
            {
                // A neutral between two runs of the same direction takes it; numbers count as right to left here.
                int left = leftAny[i], right = rightAny[i];
                bool leftR = left is 2 or 3, rightR = right is 2 or 3;
                if (left == 1 && right == 1) d = 1;
                else if (leftR && rightR) d = 2;
                else d = rtl ? 2 : 1;
            }
            else if (d == 3)
            {
                // Numbers next to right-to-left text are read left to right within it.
                int left = leftStrong[i], right = rightStrong[i];
                d = rtl || left == 2 || right == 2 ? 3 : 1;
            }
            levels[i] = d switch
            {
                1 => (byte)(rtl ? 2 : 0),
                2 => 1,
                _ => 2, // numbers
            };
        }
        return Bidi.VisualOrder(levels);
    }

    /// <summary>
    /// The bidi class of a glyph's text: its first strong character, else its first number or space. Passing the class
    /// of earlier text as <paramref name="result"/> continues from it, giving the class of the two joined.
    /// </summary>
    private static BidiClass ClassOf(string text, BidiClass result = BidiClass.ON)
    {
        if (result is BidiClass.L or BidiClass.R or BidiClass.AL) return result;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            if (cp > 0xFFFF) i++;
            var c = UnicodeTables.Bidi(cp);
            if (c is BidiClass.L or BidiClass.R or BidiClass.AL) return c;
            if (c is BidiClass.EN or BidiClass.AN && result == BidiClass.ON) result = c;
            else if (c == BidiClass.WS && result == BidiClass.ON) result = BidiClass.WS;
        }
        return result;
    }
}

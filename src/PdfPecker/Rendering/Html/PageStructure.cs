namespace PdfPecker.Rendering.Html;

/// <summary>A word of a page as the structured output sees it: its text, box in points from the top left, and style.</summary>
internal sealed class FlowWord
{
    public required string Text;
    public double X0, X1, Top, Bottom, Size;
    public bool Bold, Italic;
    public int Line; // the text line the word came from
    public int Order; // the word's place in that line's reading order
    public string? Href; // a web address the word links to
    public int LinkPage = -1; // or the index of the page it links to
    public bool Used; // taken by a table, so not part of the flowing text

    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Top + Bottom) / 2;
}

/// <summary>Words that share a baseline, in reading order.</summary>
internal sealed class FlowLine
{
    public readonly List<FlowWord> Words;
    public readonly double X0, X1, Top, Bottom, Size;
    public readonly int Id;
    public readonly bool RightToLeft;
    public int Cells = -1; // how many cells the gaps between its words make, once counted

    public FlowLine(List<FlowWord> words, int id, bool rightToLeft)
    {
        Words = words;
        Id = id;
        RightToLeft = rightToLeft;
        X0 = Top = double.MaxValue;
        X1 = Bottom = double.MinValue;
        foreach (var w in words)
        {
            X0 = Math.Min(X0, w.X0); X1 = Math.Max(X1, w.X1);
            Top = Math.Min(Top, w.Top); Bottom = Math.Max(Bottom, w.Bottom);
            Size = Math.Max(Size, w.Size);
        }
    }
}

internal abstract class FlowBlock
{
    public double Top, Left;
}

internal enum TextKind { Paragraph, Heading, Bullet, Numbered }

/// <summary>A paragraph, heading or list item: its lines, read one after the other.</summary>
internal sealed class TextBlock : FlowBlock
{
    public TextKind Kind;
    public int Level; // the heading level, 1 to 6
    public int Number; // the number of a numbered list item
    public readonly List<FlowLine> Lines = new();
}

internal sealed class TableCell
{
    public int Row, Column, RowSpan = 1, ColumnSpan = 1;
    public double X0, X1; // the cell's sides, which tell a wrapped line from a line break
    public readonly List<FlowLine> Lines = new();
}

/// <summary>A table: its cells on a grid of rows and columns, columns counted from the left of the page.</summary>
internal sealed class TableBlock : FlowBlock
{
    public int Rows, Columns;
    /// <summary>The number of rows at the top that are the table's header, often 0 or 1.</summary>
    public int HeaderRows;
    public bool RightToLeft;
    public readonly List<TableCell> Cells = new();
}

/// <summary>Parts of a page that sit side by side, such as text columns or framed boxes, read one part after the other.</summary>
internal sealed class GroupBlock : FlowBlock
{
    public readonly List<List<FlowBlock>> Parts = new();
}

/// <summary>
/// Finds the structure of a page from its words and lines: tables from the borders the page draws and from text that
/// lines up in columns, text columns, headings by size, list items by their markers, and paragraphs by the space
/// between lines. What it cannot tell for certain stays plain text in reading order, as a wrong table is worse than
/// none.
/// </summary>
internal static partial class PageStructure
{
    /// <summary>Pages of more lines or words than these are not searched for tables.</summary>
    private const int MaxLines = 1500, MaxWords = 20_000;

    /// <summary>Text this much larger than the body text is a heading.</summary>
    private const double HeadingRatio = 1.2;

    public static List<FlowBlock> Build(PdfTextPage text, List<PageRule>? rules, List<(double X0, double Y0, double X1, double Y1)>? bullets,
        List<PageLink> links, CancellationToken cancellationToken)
    {
        var lines = Lines(text, links);
        if (bullets is { Count: > 0 }) AddBullets(lines, bullets);
        var blocks = new List<FlowBlock>();
        if (lines.Count == 0) return blocks;
        double body = BodySize(lines);
        // The search for tables compares lines with lines and rules with words; a page past these bounds, such as a
        // dense listing or a page of generated text, is read as paragraphs so it takes bounded time.
        bool search = lines.Count <= MaxLines && lines.Sum(l => l.Words.Count) <= MaxWords;
        if (search && rules is { Count: > 0 and <= TextCollector.MaxRules }) FindRuledTables(lines, rules, body, blocks, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        blocks.AddRange(Flow(Remaining(lines), body, columns: search));
        Sort(blocks);
        return blocks;
    }

    private static void Sort(List<FlowBlock> blocks)
        => blocks.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));

    /// <summary>The page's lines as flow lines, with each word's style and link.</summary>
    private static List<FlowLine> Lines(PdfTextPage text, List<PageLink> links)
    {
        var lines = new List<FlowLine>(text.Lines.Count);
        foreach (var line in text.Lines)
        {
            var words = new List<FlowWord>(line.Words.Count);
            foreach (var w in line.Words)
            {
                var word = new FlowWord
                {
                    Text = w.Text, X0 = w.X, X1 = w.X + w.Width, Top = w.Y, Bottom = w.Y + w.Height, Size = w.FontSize,
                    Bold = IsBold(w.FontName), Italic = IsItalic(w.FontName), Line = lines.Count, Order = words.Count,
                };
                foreach (var link in links)
                {
                    if (word.CenterX >= link.X && word.CenterX <= link.X + link.Width && word.CenterY >= link.Y && word.CenterY <= link.Y + link.Height)
                    {
                        word.Href = link.Uri;
                        if (link.Uri == null) word.LinkPage = link.Page;
                        break;
                    }
                }
                words.Add(word);
            }
            DropLeaders(words);
            if (!line.RightToLeft) JoinSpacedLetters(words);
            if (words.Count > 0) lines.Add(new FlowLine(words, lines.Count, line.RightToLeft));
        }
        return lines;
    }

    /// <summary>
    /// Removes dot leaders, the rows of dots that lead the eye from a label to its figure or page number: four or more
    /// dots, with or without spaces, at the end of a word or as words of their own. They say nothing once the label and
    /// the figure share a table row.
    /// </summary>
    private static void DropLeaders(List<FlowWord> words)
    {
        static int Dots(string text)
        {
            int dots = 0;
            foreach (char c in text)
            {
                if (c is '.' or '·' or '…') dots += c == '…' ? 3 : 1;
                else if (c != ' ') return -1;
            }
            return dots;
        }
        for (int i = 0; i < words.Count;)
        {
            int j = i, dots = 0;
            while (j < words.Count && Dots(words[j].Text) is var d and > 0) { dots += d; j++; }
            if (j > i && dots >= 4)
            {
                // The word before keeps the leader's place, so the label still reaches as far as it did, but stays a
                // cell's gap short of the figure after it, which the leader kept apart from the label.
                if (i > 0)
                {
                    double reach = words[j - 1].X1;
                    if (j < words.Count) reach = Math.Min(reach, words[j].X0 - (CellGap + 0.1) * words[j].Size);
                    words[i - 1].X1 = Math.Max(words[i - 1].X1, reach);
                }
                words.RemoveRange(i, j - i);
                continue;
            }
            i = Math.Max(j, i + 1);
        }
        foreach (var w in words)
        {
            // Trailing dots of the word itself, as in "Robbery. . . . .", counted from the end.
            int end = w.Text.Length, dots = 0;
            while (end > 0 && w.Text[end - 1] is '.' or ' ' or '·') { if (w.Text[end - 1] != ' ') dots++; end--; }
            if (dots >= 4 && end > 0) w.Text = w.Text[..end];
        }
        for (int i = 0; i < words.Count; i++) words[i].Order = i;
    }

    /// <summary>
    /// Joins the letters of letter-spaced text, such as a heading set "S E R V I C E", back into words: in a line of
    /// mostly single letters, letters closer than the usual gap between them by a little are one word, and the wider
    /// gaps are the spaces.
    /// </summary>
    private static void JoinSpacedLetters(List<FlowWord> words)
    {
        if (words.Count < 5 || words.Count(w => w.Text.Length == 1 && char.IsLetter(w.Text[0])) < 0.7 * words.Count) return;
        var gaps = new List<double>(words.Count - 1);
        for (int i = 1; i < words.Count; i++) gaps.Add(words[i].X0 - words[i - 1].X1);
        if (gaps.Exists(g => g < -1)) return; // not in order along the line
        gaps.Sort();
        double usual = gaps[gaps.Count / 2], limit = Math.Max(1.6 * usual, usual + 1);
        if (usual > 0.5 * words[0].Size) return;
        for (int i = words.Count - 1; i > 0; i--)
        {
            var a = words[i - 1];
            var b = words[i];
            if (b.X0 - a.X1 > limit) continue;
            a.Text += b.Text;
            a.X1 = b.X1;
            a.Top = Math.Min(a.Top, b.Top);
            a.Bottom = Math.Max(a.Bottom, b.Bottom);
            words.RemoveAt(i);
        }
        for (int i = 0; i < words.Count; i++) words[i].Order = i;
    }

    /// <summary>
    /// Puts a bullet word at the start of each line that a small filled shape sits just before, centred on its text, as
    /// the bullets of a list are drawn; the line is then read as a list item like one that starts with a bullet letter.
    /// </summary>
    private static void AddBullets(List<FlowLine> lines, List<(double X0, double Y0, double X1, double Y1)> bullets)
    {
        var sorted = bullets.OrderBy(b => (b.Y0 + b.Y1) / 2).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var first = line.Words.MinBy(w => w.X0)!;
            double centre = (first.Top + first.Bottom) / 2, half = (first.Bottom - first.Top) / 2;
            // The bullets whose centres are within the first word's height, found by binary search.
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if ((sorted[mid].Y0 + sorted[mid].Y1) / 2 < centre - half) lo = mid + 1; else hi = mid;
            }
            for (int k = lo; k < sorted.Count && (sorted[k].Y0 + sorted[k].Y1) / 2 <= centre + half; k++)
            {
                var b = sorted[k];
                double size = b.X1 - b.X0;
                if (b.X1 > first.X0 || first.X0 - b.X1 > 2 * first.Size || size > 0.6 * first.Size) continue;
                var words = new List<FlowWord>(line.Words.Count + 1)
                {
                    new() { Text = "•", X0 = b.X0, X1 = b.X1, Top = first.Top, Bottom = first.Bottom, Size = first.Size, Line = first.Line, Order = -1 },
                };
                words.AddRange(line.Words);
                lines[i] = new FlowLine(words, line.Id, line.RightToLeft);
                break;
            }
        }
    }

    private static bool IsBold(string font)
        => font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Heavy", StringComparison.OrdinalIgnoreCase) || font.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || font.Contains("Demi", StringComparison.OrdinalIgnoreCase);

    private static bool IsItalic(string font)
        => font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

    /// <summary>The font size most of the page's letters are in.</summary>
    private static double BodySize(List<FlowLine> lines)
    {
        var counts = new Dictionary<double, int>();
        foreach (var line in lines)
            foreach (var w in line.Words)
            {
                double size = Math.Round(w.Size * 2) / 2;
                counts[size] = counts.GetValueOrDefault(size) + w.Text.Length;
            }
        double best = 0;
        int most = -1;
        foreach (var (size, count) in counts)
            if (count > most || (count == most && size < best)) { best = size; most = count; }
        return Math.Max(best, 1);
    }

    /// <summary>The lines rebuilt from the words no table has taken.</summary>
    private static List<FlowLine> Remaining(List<FlowLine> lines)
    {
        var result = new List<FlowLine>(lines.Count);
        foreach (var line in lines)
        {
            if (!line.Words.Exists(w => w.Used)) { result.Add(line); continue; }
            var words = line.Words.FindAll(w => !w.Used);
            if (words.Count > 0) result.Add(new FlowLine(words, line.Id, line.RightToLeft));
        }
        return result;
    }

    /// <summary>
    /// The blocks of lines no border encloses: tables and text columns found from how the text lines up, then
    /// paragraphs, headings and list items. <paramref name="columns"/> is false inside a part already split off, which
    /// is not split again.
    /// </summary>
    private static List<FlowBlock> Flow(List<FlowLine> lines, double body, bool columns)
    {
        lines.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.X0.CompareTo(b.X0));
        var blocks = new List<FlowBlock>();
        var plain = new List<FlowLine>();
        if (columns) FindAlignedTables(lines, body, blocks, plain);
        else plain.AddRange(lines);
        blocks.AddRange(Paragraphs(plain, body));
        Sort(blocks);
        return blocks;
    }

    /// <summary>Groups lines, sorted from the top, into paragraphs, headings and list items.</summary>
    private static List<FlowBlock> Paragraphs(List<FlowLine> lines, double body)
    {
        var blocks = new List<FlowBlock>();
        if (lines.Count == 0) return blocks;
        lines.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.X0.CompareTo(b.X0));
        double typical = TypicalGap(lines);
        TextBlock? current = null;
        FlowLine? previous = null;
        double textStart = 0; // where the text of the current list item starts, after its marker
        double left = 0, right = 0; // how far the current block's lines reach
        foreach (var line in lines)
        {
            var marker = Marker(line, out int number);
            bool heading = line.Size >= HeadingRatio * body;
            bool joins = current != null && previous != null && marker == TextKind.Paragraph && !Numbered(line)
                // A line all in bold, alone, is a heading of the text after it, as in "Payment terms" over its terms.
                && !(current.Lines.Count == 1 && previous.Words.TrueForAll(w => w.Bold) && !line.Words.TrueForAll(w => w.Bold))
                && (current.Kind == TextKind.Heading) == heading
                && Math.Abs(line.Size - previous.Size) <= 0.1 * Math.Max(line.Size, previous.Size)
                && line.Top - previous.Bottom <= typical + 0.35 * Math.Min(line.Size, previous.Size)
                && line.Top - previous.Bottom > -0.5 * line.Size
                && line.X0 < right && line.X1 > left
                && (current.Kind is not (TextKind.Bullet or TextKind.Numbered) || line.X0 >= textStart - 1);
            if (joins)
            {
                current!.Lines.Add(line);
                (left, right) = (Math.Min(left, line.X0), Math.Max(right, line.X1));
                previous = line;
                continue;
            }
            (left, right) = (line.X0, line.X1);
            current = new TextBlock { Kind = heading && marker == TextKind.Paragraph ? TextKind.Heading : marker, Number = number, Top = line.Top, Left = line.X0 };
            if (current.Kind == TextKind.Heading) current.Level = HeadingLevel(line.Size, body);
            textStart = line.Words.Count > 1 && marker != TextKind.Paragraph ? line.Words.OrderBy(w => w.X0).ElementAt(1).X0 : line.X0;
            current.Lines.Add(line);
            blocks.Add(current);
            previous = line;
        }
        // A heading of many lines is large body text, such as a title page, rather than a heading, and a lone large
        // letter or number is a logo, an initial or a page number.
        foreach (var block in blocks)
            if (block is TextBlock { Kind: TextKind.Heading } t && (t.Lines.Count > 4 || Letters(t) < 2)) t.Kind = TextKind.Paragraph;
        return blocks;
    }

    private static int Letters(TextBlock block)
    {
        int letters = 0;
        foreach (var line in block.Lines)
            foreach (var word in line.Words)
                foreach (char c in word.Text)
                    if (char.IsLetter(c) && ++letters >= 2) return letters;
        return letters;
    }

    private static int HeadingLevel(double size, double body)
    {
        double ratio = size / body;
        return ratio >= 1.75 ? 1 : ratio >= 1.4 ? 2 : 3;
    }

    /// <summary>The usual space between the lines of a paragraph, from the gaps between neighbouring lines of one size.</summary>
    private static double TypicalGap(List<FlowLine> lines)
    {
        var gaps = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            var a = lines[i - 1];
            var b = lines[i];
            double gap = b.Top - a.Bottom;
            if (Math.Abs(a.Size - b.Size) > 0.1 * a.Size || gap < -0.5 * a.Size || gap > 1.5 * a.Size) continue;
            if (b.X0 >= a.X1 || b.X1 <= a.X0) continue;
            gaps.Add(gap);
        }
        if (gaps.Count == 0) return 0.3 * lines[0].Size;
        gaps.Sort();
        // The lower quartile: paragraphs spaced apart are fewer than the lines inside them, and must not count.
        return gaps[gaps.Count / 4];
    }

    /// <summary>Whether a line starts with the number of a clause, such as "2.1" or "(a)", which starts a paragraph.</summary>
    private static bool Numbered(FlowLine line)
    {
        var first = line.Words[0].Text.AsSpan();
        if (line.Words.Count < 2 || first.Length > 12) return false;
        if (first.Length >= 3 && first[0] == '(' && first[^1] == ')')
        {
            var inner = first[1..^1];
            if (inner.Length > 4 || !char.IsLetterOrDigit(inner[0])) return false;
            if (inner.Length == 1) return true;
            foreach (char c in inner)
                if (c is not ('i' or 'v' or 'x' or 'I' or 'V' or 'X')) return false;
            return true;
        }
        if (first[^1] == '.') first = first[..^1];
        int dots = 0;
        bool digit = true;
        foreach (char c in first)
        {
            if (c == '.') { if (!digit) return false; dots++; digit = false; }
            else if (char.IsAsciiDigit(c)) digit = true;
            else return false;
        }
        return dots >= 1 && digit;
    }

    private static bool IsBullet(string text)
        => text.Length == 1 && text[0] is '•' or '◦' or '▪' or '▫' or '‣' or '⁃' or '●' or '○' or '■' or '□' or '–' or '—' or '-' or '*' or '·' or '➢' or '►' or '✓';

    /// <summary>Whether a line starts with a list marker: a bullet, or a number followed by a full stop or bracket.</summary>
    private static TextKind Marker(FlowLine line, out int number)
    {
        number = 0;
        var first = line.Words[0].Text;
        if (line.Words.Count < 2) return TextKind.Paragraph;
        if (IsBullet(first)) return TextKind.Bullet;
        if (first.Length is >= 2 and <= 4 && first[0] != '0' && first[^1] is '.' or ')'
            && int.TryParse(first.AsSpan(0, first.Length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
            return TextKind.Numbered;
        return TextKind.Paragraph;
    }
}

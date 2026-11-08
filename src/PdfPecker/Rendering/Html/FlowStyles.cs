using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfPecker.Rendering.Html;

/// <summary>A picture of a page, placed in the flow of the structured output where it sits on the page.</summary>
internal sealed class ImageBlock : FlowBlock
{
    public double X0, X1, Y0, Y1; // the box it shows in on the page, in points from the top left
    public (double X0, double Y0, double X1, double Y1) Drawn; // the box of the whole picture, larger where a clip crops it
    public required string MediaType;
    public required byte[] Data;
    public bool Nested; // in a side-by-side part or a table cell, where the page's sides say nothing of its place
}

/// <summary>
/// The looks of a page for structured HTML with styles: fonts, colours, backgrounds, border lines, alignment and widths,
/// read from what the page draws and written as CSS classes of the page's own. Nothing is placed at a position: the
/// classes only say how the flowing text and tables look.
/// </summary>
internal sealed class FlowStyles
{
    /// <summary>A fill that covers this much of the page is the page's own background, not a box's.</summary>
    private const double PageFill = 0.5;

    /// <summary>How far, in points, a line may be from the side of a cell and still be its border.</summary>
    private const double BorderReach = 3;

    private readonly List<PageFill> _fills;
    private readonly PageRule[] _horizontal, _vertical; // the lines across and down the page, by their position
    private readonly double _width, _height;
    private readonly string _prefix;
    private readonly Dictionary<string, string> _classes = new(StringComparer.Ordinal);
    private readonly StringBuilder _css = new();

    // The font last asked for and its family: words in a row mostly share their font, and a page is written on one thread.
    private string? _font, _family;

    // The styles met while some words are counted, in the order met, with their letters; the index finds a style among
    // many, as a page that sets each word in a size of its own has.
    private readonly List<TextStyle> _styles = new();
    private readonly List<int> _letters = new();
    private Dictionary<TextStyle, int>? _styleIndex;
    private bool _indexed;
    private int _last; // the index of the style counted last, which the next word mostly shares

    private (double Start, double End)[] _spans = new (double, double)[16]; // the pieces of a line being measured
    private Dictionary<(double Width, bool Dashed, uint Color), string>? _borderValues;

    // The vertical border found last, as a table cell asks for its right side just before the cell on its right asks
    // for its left.
    private (double Side, double From, double To, double Start, double End) _lastAt;
    private PageRule? _lastBorder;
    private bool _hasLast;

    /// <summary>The page's usual text: the font, size and colour most of its letters have, which the page section sets.</summary>
    public readonly TextStyle Base;

    /// <summary>The left and right of the page's text, in points from the left of the page.</summary>
    public readonly double Left, Right;

    public FlowStyles(int page, double width, double height, List<PageFill>? fills, List<PageRule>? rules, List<FlowBlock> blocks)
    {
        _prefix = "p" + page.ToString(CultureInfo.InvariantCulture) + "-";
        _width = width;
        _height = height;
        _fills = fills?.FindAll(f => (f.X1 - f.X0) * (f.Y1 - f.Y0) < PageFill * width * height) ?? new List<PageFill>();
        _horizontal = ByPosition(rules, horizontal: true);
        _vertical = ByPosition(rules, horizontal: false);
        double left = double.MaxValue, right = double.MinValue;
        StartCount();
        foreach (var line in AllLines(blocks))
        {
            foreach (var w in line.Words) Count(w);
            left = Math.Min(left, line.X0);
            right = Math.Max(right, line.X1);
        }
        foreach (var image in blocks.OfType<ImageBlock>())
        {
            left = Math.Min(left, image.X0);
            right = Math.Max(right, image.X1);
        }
        (Left, Right) = left < right ? (left, right) : (0, width);
        Base = Usual() ?? new TextStyle("", 12, 0);
    }

    /// <summary>
    /// The lines across or down the page, not the edges of filled areas, by their position, those at one position in
    /// the order the page draws them.
    /// </summary>
    private static PageRule[] ByPosition(List<PageRule>? rules, bool horizontal)
    {
        if (rules == null) return Array.Empty<PageRule>();
        // The lines are read in place, as copying each of them out of the list costs more than sorting them.
        var all = CollectionsMarshal.AsSpan(rules);
        int n = 0;
        bool sorted = true;
        double last = 0;
        foreach (ref readonly var r in all)
        {
            if (r.Horizontal != horizontal || r.Edge) continue;
            if (n > 0 && r.Position.CompareTo(last) < 0) sorted = false;
            last = r.Position;
            n++;
        }
        var lines = new PageRule[n];
        if (sorted)
        {
            n = 0;
            foreach (ref readonly var r in all)
                if (r.Horizontal == horizontal && !r.Edge) lines[n++] = r;
            return lines;
        }
        // The positions are sorted as integers that keep their order, and the lines at one position are put back in the
        // order the page draws them, as a stable sort leaves them.
        var keys = new long[n];
        var order = new int[n];
        n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].Horizontal != horizontal || all[i].Edge) continue;
            keys[n] = SortKey(all[i].Position);
            order[n++] = i;
        }
        Array.Sort(keys, order);
        for (int i = 0; i < n;)
        {
            int j = i + 1;
            while (j < n && keys[j] == keys[i]) j++;
            if (j - i > 1) Array.Sort(order, i, j - i);
            i = j;
        }
        for (int i = 0; i < n; i++) lines[i] = all[order[i]];
        return lines;
    }

    /// <summary>
    /// An integer for a position that sorts as <see cref="double.CompareTo(double)"/> does: NaN first, and 0 and -0 as one.
    /// </summary>
    private static long SortKey(double position)
    {
        if (double.IsNaN(position)) return long.MinValue;
        long bits = BitConverter.DoubleToInt64Bits(position == 0 ? 0.0 : position);
        return bits >= 0 ? bits : bits ^ long.MaxValue;
    }

    /// <summary>The class of the given declarations, made the first time they are asked for.</summary>
    public string Class(string declarations)
    {
        if (!_classes.TryGetValue(declarations, out var name))
        {
            name = _prefix + (_classes.Count + 1).ToString(CultureInfo.InvariantCulture);
            _classes[declarations] = name;
            _css.Append('.').Append(name).Append('{').Append(declarations).Append("}\n");
        }
        return name;
    }

    /// <summary>The page's classes as a style element, which goes before the page's content.</summary>
    public void WriteStyleElement(StringBuilder sb)
    {
        if (_css.Length == 0) return;
        sb.Append("<style>\n").Append(_css).Append("</style>\n");
    }

    /// <summary>The section's own declarations: the page's usual text, and as wide as the text on the page.</summary>
    public string Section()
    {
        var d = new StringBuilder();
        d.Append("max-width:").Append(Pt(Math.Min(_width, Right - Left + 2))).Append(';');
        Text(d, Base, null);
        return d.ToString();
    }

    /// <summary>How a word looks: the family of its font, its size to the half point and its colour.</summary>
    public TextStyle StyleOf(FlowWord w)
    {
        if (_family == null || !ReferenceEquals(w.Font, _font))
        {
            _family = Family(w.Font);
            _font = w.Font;
        }
        return new TextStyle(_family, Math.Round(w.Size * 2) / 2, w.Color & 0xFFFFFF);
    }

    /// <summary>
    /// The style most of the letters of some lines' words have, or null for no words, with the number of words and of
    /// bold words.
    /// </summary>
    public TextStyle? Usual(List<FlowLine> lines, out int words, out int bold)
    {
        words = bold = 0;
        StartCount();
        foreach (var line in lines)
        {
            foreach (var w in line.Words)
            {
                Count(w);
                if (w.Bold) bold++;
            }
            words += line.Words.Count;
        }
        return Usual();
    }

    private void StartCount()
    {
        _styles.Clear();
        _letters.Clear();
        _styleIndex?.Clear();
        _indexed = false;
        _last = 0;
    }

    private void Count(FlowWord w)
    {
        var style = StyleOf(w);
        int i = -1;
        if (_last < _styles.Count && Same(_styles[_last], style)) i = _last;
        else if (_indexed)
        {
            if (!_styleIndex!.TryGetValue(style, out i)) i = -1;
        }
        else
        {
            for (int k = 0; k < _styles.Count && i < 0; k++)
                if (Same(_styles[k], style)) i = k;
        }
        if (i < 0)
        {
            i = _styles.Count;
            _styles.Add(style);
            _letters.Add(0);
            if (_indexed) _styleIndex![style] = i;
            else if (_styles.Count > 16)
            {
                // Past a few styles, looking each one up is cheaper than comparing with all.
                _styleIndex ??= new Dictionary<TextStyle, int>();
                for (int k = 0; k < _styles.Count; k++) _styleIndex[_styles[k]] = k;
                _indexed = true;
            }
        }
        _letters[i] += w.Text.Length;
        _last = i;
    }

    /// <summary>Whether two styles are equal, as the record's own equality says, with the cheap comparisons first.</summary>
    private static bool Same(in TextStyle a, in TextStyle b) => a.Color == b.Color && a.Size.Equals(b.Size) && string.Equals(a.Family, b.Family);

    /// <summary>The style of the words counted that most letters have, ties going to the first style met.</summary>
    private TextStyle? Usual()
    {
        TextStyle? best = null;
        int most = 0;
        for (int i = 0; i < _styles.Count; i++)
            if (_letters[i] > most) (best, most) = (_styles[i], _letters[i]);
        return best;
    }

    /// <summary>
    /// The declarations that set text in <paramref name="style"/> where it would otherwise be in <paramref name="around"/>;
    /// all of them when <paramref name="around"/> is null.
    /// </summary>
    public static void Text(StringBuilder d, TextStyle style, TextStyle? around)
    {
        if (around == null || style.Family != around.Value.Family)
        {
            if (style.Family.Length > 0) d.Append("font-family:").Append(style.Family).Append(';');
        }
        if (around == null || style.Size != around.Value.Size) d.Append("font-size:").Append(Pt(style.Size)).Append(';');
        if (around == null || style.Color != around.Value.Color) d.Append("color:").Append(Hex(style.Color)).Append(';');
    }

    /// <summary>
    /// The colour a box is filled with: the last plain fill painted under its middle that covers most of it, or null when
    /// it is on the page's own background. White over nothing else is the page, not a fill.
    /// </summary>
    public PageFill? Background(double x0, double y0, double x1, double y1)
    {
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        for (int i = _fills.Count - 1; i >= 0; i--)
        {
            var f = _fills[i];
            if (cx < f.X0 || cx > f.X1 || cy < f.Y0 || cy > f.Y1) continue;
            // A fill only as big as a few letters, such as a highlight, is not the box's background.
            double w = Math.Min(x1, f.X1) - Math.Max(x0, f.X0), h = Math.Min(y1, f.Y1) - Math.Max(y0, f.Y0);
            if (w < 0.6 * (x1 - x0) || h < 0.6 * (y1 - y0)) continue;
            if ((f.Color & 0xFFFFFF) == 0xFFFFFF)
            {
                // White over a coloured fill is white; white over the page is the page.
                bool over = false;
                for (int k = i - 1; k >= 0 && !over; k--)
                    over = cx >= _fills[k].X0 && cx <= _fills[k].X1 && cy >= _fills[k].Y0 && cy <= _fills[k].Y1;
                return over ? f : null;
            }
            return f;
        }
        return null;
    }

    /// <summary>
    /// The line drawn along one side of a box, if any: a horizontal line for its top or bottom, or a vertical one for its
    /// left or right, within the span <paramref name="at"/> allows and running along most of the side from
    /// <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public PageRule? Border(bool horizontal, (double Side, double From, double To) at, double from, double to)
    {
        if (horizontal) return FindBorder(true, at, from, to);
        var key = (at.Side, at.From, at.To, from, to);
        if (!_hasLast || !key.Equals(_lastAt))
        {
            _lastBorder = FindBorder(false, at, from, to);
            _lastAt = key;
            _hasLast = true;
        }
        return _lastBorder;
    }

    private PageRule? FindBorder(bool horizontal, (double Side, double From, double To) at, double from, double to)
    {
        var rules = horizontal ? _horizontal : _vertical;
        PageRule? found = null;
        double place = double.NaN; // the last position looked at, as lines at one place come together
        for (int i = First(rules, at.From); i < rules.Length && rules[i].Position <= at.To; i = NextPlace(rules, i, place))
        {
            double position = rules[i].Position;
            place = position;
            if (found != null && Math.Abs(position - at.Side) >= Math.Abs(found.Value.Position - at.Side)) continue;
            if (Covered(rules, position, from, to, out int pieces) < 0.6 * (to - from)) continue;
            // A line drawn as many short pieces, as a dashed or dotted border often is, is one dashed line.
            found = pieces >= 4 ? rules[i] with { Dashed = true } : rules[i];
        }
        return found;
    }

    /// <summary>
    /// The index of the first line after <paramref name="i"/> more than half a point past <paramref name="place"/>, the
    /// position of line <paramref name="i"/>. The lines from there on only grow in position, so those at the place come
    /// in one run, which can be as long as a table has rows.
    /// </summary>
    private static int NextPlace(PageRule[] rules, int i, double place)
    {
        int lo = i, hi = i + 1, step = 1;
        while (hi < rules.Length && Math.Abs(rules[hi].Position - place) <= 0.5)
        {
            lo = hi;
            hi += step;
            if (step < 1 << 20) step *= 2;
        }
        hi = Math.Min(hi, rules.Length);
        while (hi - lo > 1)
        {
            int mid = lo + (hi - lo) / 2;
            if (Math.Abs(rules[mid].Position - place) <= 0.5) lo = mid;
            else hi = mid;
        }
        return hi;
    }

    /// <summary>The index of the first line at or past a position.</summary>
    private static int First(PageRule[] rules, double position)
    {
        int lo = 0, hi = rules.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (rules[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// How much of the span from <paramref name="from"/> to <paramref name="to"/> the lines at one place cover, and in
    /// how many pieces. Only the first few hundred pieces count, which is plenty to tell.
    /// </summary>
    private double Covered(PageRule[] rules, double position, double from, double to, out int pieces)
    {
        int n = 0;
        for (int i = First(rules, position - 0.5); i < rules.Length && rules[i].Position <= position + 0.5 && n < 500; i++)
        {
            if (!(rules[i].End > from && rules[i].Start < to)) continue;
            if (n == _spans.Length) Array.Resize(ref _spans, n * 2);
            _spans[n++] = (Math.Max(from, rules[i].Start), Math.Min(to, rules[i].End));
        }
        var spans = _spans.AsSpan(0, n);
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        double covered = 0, reach = double.MinValue;
        pieces = 0;
        foreach (var (start, end) in spans)
        {
            if (start > reach + 0.5) pieces++;
            if (end > reach)
            {
                covered += end - Math.Max(start, reach);
                reach = end;
            }
        }
        return covered;
    }

    /// <summary>
    /// The sides of a table's columns and rows on the page, from where its cells' text sits, with the span of each side
    /// in which a line drawn along it is that side's border: the gap between neighbouring columns or rows, and a little
    /// outside the table at its edges.
    /// </summary>
    public static TableGrid Grid(TableBlock table)
    {
        int n = table.Columns, m = table.Rows;
        var lo = Filled(n, double.MaxValue);
        var hi = Filled(n, double.MinValue);
        var top = Filled(m, double.MaxValue);
        var bottom = Filled(m, double.MinValue);
        double size = 0;
        foreach (var c in table.Cells)
        {
            if (c.Lines.Count == 0) continue;
            int last = Math.Min(n - 1, c.Column + c.ColumnSpan - 1), end = Math.Min(m - 1, c.Row + c.RowSpan - 1);
            lo[c.Column] = Math.Min(lo[c.Column], Math.Min(c.X0, Min(c.Lines, l => l.X0)));
            hi[last] = Math.Max(hi[last], Math.Max(c.X1, Max(c.Lines, l => l.X1)));
            top[c.Row] = Math.Min(top[c.Row], Min(c.Lines, l => l.Top));
            bottom[end] = Math.Max(bottom[end], Max(c.Lines, l => l.Bottom));
            size = Math.Max(size, Max(c.Lines, l => l.Size));
        }
        if (size <= 0) size = 10;
        FillGaps(lo, hi);
        FillGaps(top, bottom);
        var x = new (double Side, double From, double To)[n + 1];
        var y = new (double Side, double From, double To)[m + 1];
        x[0] = (lo[0], lo[0] - size, lo[0] + 0.5);
        x[n] = (hi[n - 1], hi[n - 1] - 0.5, hi[n - 1] + size);
        for (int k = 1; k < n; k++) x[k] = Between(hi[k - 1], lo[k]);
        y[0] = (top[0] - 0.25 * size, top[0] - 1.2 * size, top[0] + 0.5);
        y[m] = (bottom[m - 1] + 0.25 * size, bottom[m - 1] - 0.5, bottom[m - 1] + 1.2 * size);
        for (int k = 1; k < m; k++) y[k] = Between(bottom[k - 1], top[k]);
        return new TableGrid(x, y);

        static double[] Filled(int count, double value)
        {
            var a = new double[count];
            Array.Fill(a, value);
            return a;
        }

        // A column or row with no text of its own takes the room between its neighbours.
        static void FillGaps(double[] start, double[] end)
        {
            for (int i = 0; i < start.Length; i++)
            {
                if (start[i] == double.MaxValue) start[i] = i > 0 ? end[i - 1] : end[i] != double.MinValue ? end[i] : 0;
                if (end[i] == double.MinValue) end[i] = i + 1 < start.Length && start[i + 1] != double.MaxValue ? start[i + 1] : start[i];
                if (end[i] < start[i]) end[i] = start[i];
            }
        }

        static (double, double, double) Between(double end, double start)
            => end <= start ? ((end + start) / 2, end - 0.5, start + 0.5) : ((end + start) / 2, start - BorderReach, end + BorderReach);
    }

    /// <summary>
    /// Puts a picture among a page's blocks: at the top of the side-by-side part or table cell whose text starts just
    /// below it, as a product photo heads its card, or else before the first block below its top.
    /// </summary>
    public static void Place(List<FlowBlock> blocks, ImageBlock image)
    {
        double cx = (image.X0 + image.X1) / 2, h = image.Y1 - image.Y0;
        // The text under a picture that belongs with it starts within a few lines of its bottom.
        bool Below(double top) => top >= image.Y1 - 0.5 * h && top <= image.Y1 + 40;
        foreach (var block in blocks)
        {
            if (block is GroupBlock group)
            {
                foreach (var part in group.Parts)
                {
                    var lines = AllLines(part).ToList();
                    if (lines.Count == 0 || cx < Min(lines, l => l.X0) - 4 || cx > Max(lines, l => l.X1) + 4 || !Below(Min(lines, l => l.Top))) continue;
                    image.Top = Min(lines, l => l.Top) - 0.01;
                    image.Nested = true;
                    int k = 0;
                    while (k < part.Count && part[k].Top <= image.Top) k++;
                    part.Insert(k, image);
                    return;
                }
            }
            else if (block is TableBlock table && table.Cells.Count > 0)
            {
                var grid = Grid(table);
                for (int c = 0; c < table.Columns; c++)
                {
                    if (cx < grid.X[c].Side || cx > grid.X[c + 1].Side) continue;
                    // The first row whose text starts under the picture.
                    foreach (var cell in table.Cells.Where(t => t.Column <= c && c < t.Column + t.ColumnSpan && t.Lines.Count > 0).OrderBy(t => t.Row))
                    {
                        if (!Below(Min(cell.Lines, l => l.Top))) continue;
                        image.Nested = true;
                        (cell.Images ??= new List<ImageBlock>()).Add(image);
                        return;
                    }
                }
            }
        }
        int i = 0;
        while (i < blocks.Count && blocks[i].Top <= image.Y0 + 1) i++;
        blocks.Insert(i, image);
    }

    /// <summary>A border declaration for a line: as thick as it is drawn, at least a hairline, in its colour.</summary>
    public string BorderValue(PageRule rule)
    {
        // A table's lines mostly look alike, so each look is written once.
        _borderValues ??= new Dictionary<(double Width, bool Dashed, uint Color), string>();
        if (!_borderValues.TryGetValue((rule.Width, rule.Dashed, rule.Color), out var value))
        {
            value = Pt(Math.Max(0.5, Math.Min(rule.Width > 0 ? rule.Width : 1, 6))) + (rule.Dashed ? " dashed " : " solid ") + Hex(rule.Color);
            _borderValues[(rule.Width, rule.Dashed, rule.Color)] = value;
        }
        return value;
    }

    /// <summary>
    /// The least of a value over some lines, as <see cref="Enumerable.Min{TSource}(IEnumerable{TSource}, Func{TSource, double})"/> gives
    /// it, NaN when any is NaN, without the enumerator it takes for a list.
    /// </summary>
    /// <exception cref="InvalidOperationException">There are no lines.</exception>
    public static double Min(List<FlowLine> lines, Func<FlowLine, double> value)
    {
        if (lines.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
        double min = value(lines[0]);
        if (double.IsNaN(min)) return min;
        for (int i = 1; i < lines.Count; i++)
        {
            double x = value(lines[i]);
            if (x < min) min = x;
            else if (double.IsNaN(x)) return x;
        }
        return min;
    }

    /// <summary>
    /// The greatest of a value over some lines, as <see cref="Enumerable.Max{TSource}(IEnumerable{TSource}, Func{TSource, double})"/>
    /// gives it, NaN only when all are NaN.
    /// </summary>
    /// <exception cref="InvalidOperationException">There are no lines.</exception>
    public static double Max(List<FlowLine> lines, Func<FlowLine, double> value)
    {
        if (lines.Count == 0) throw new InvalidOperationException("Sequence contains no elements");
        int i = 0;
        double max = value(lines[0]);
        while (double.IsNaN(max))
        {
            if (++i == lines.Count) return max;
            max = value(lines[i]);
        }
        for (i++; i < lines.Count; i++)
        {
            double x = value(lines[i]);
            if (x > max) max = x;
        }
        return max;
    }

    /// <summary>Every line of text in some blocks, those in tables and side-by-side parts included.</summary>
    public static IEnumerable<FlowLine> AllLines(IEnumerable<FlowBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextBlock t:
                    foreach (var l in t.Lines) yield return l;
                    break;
                case TableBlock table:
                    foreach (var c in table.Cells)
                        foreach (var l in c.Lines) yield return l;
                    break;
                case GroupBlock group:
                    foreach (var part in group.Parts)
                        foreach (var l in AllLines(part)) yield return l;
                    break;
            }
        }
    }

    public static string Hex(uint color)
        => string.Create(7, color, static (chars, c) =>
        {
            const string digits = "0123456789abcdef";
            chars[0] = '#';
            for (int i = 0; i < 3; i++)
            {
                uint b = (c >> (8 * i)) & 0xFF;
                chars[1 + 2 * i] = digits[(int)(b >> 4)];
                chars[2 + 2 * i] = digits[(int)(b & 0xF)];
            }
        });

    public static string Pt(double points) => HtmlPageWriter.Number(Math.Round(points * 2) / 2) + "pt";

    /// <summary>Whether a colour is so light that text in it needs a dark background to be seen.</summary>
    public static bool Light(uint color)
        => 0.299 * (color & 0xFF) + 0.587 * ((color >> 8) & 0xFF) + 0.114 * ((color >> 16) & 0xFF) > 230;

    /// <summary>
    /// A CSS font-family list for a PDF font: the family's own name, when it reads as one, before the generic family it
    /// belongs to. Only letters, digits and spaces of the name are kept, as it comes from the PDF.
    /// </summary>
    public static string Family(string fontName)
    {
        if (Families.TryGetValue(fontName, out var known)) return known;
        var family = FamilyOf(fontName);
        // A page names few fonts; the cache stays small whatever a PDF holds.
        if (Families.Count < 1000) Families.TryAdd(fontName, family);
        return family;
    }

    private static readonly string[] Suffixes = { "PSMT", "MT", "PS", "Std", "LT" };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Families = new(StringComparer.Ordinal);

    private static string FamilyOf(string fontName)
    {
        // "ABCDEF+Arial-BoldMT" and "TimesNewRomanPS-ItalicMT" name the families Arial and Times New Roman.
        string name = fontName;
        int plus = name.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) name = name[(plus + 1)..];
        int cut = 0;
        while (cut < name.Length && name[cut] is not ('-' or ',')) cut++;
        if (cut > 0) name = name[..cut];
        foreach (var suffix in Suffixes)
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal)) { name = name[..^suffix.Length]; break; }
        var spaced = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsAsciiLetterOrDigit(c)) { if (spaced.Length > 0 && spaced[^1] != ' ') spaced.Append(' '); continue; }
            if (i > 0 && char.IsAsciiLetterUpper(c) && char.IsAsciiLetterLower(name[i - 1]) && spaced.Length > 0 && spaced[^1] != ' ') spaced.Append(' ');
            spaced.Append(c);
        }
        string family = spaced.ToString().Trim();
        string lower = family.ToLowerInvariant();
        string generic = lower.Contains("mono", StringComparison.Ordinal) || lower.Contains("courier", StringComparison.Ordinal)
            || lower.Contains("consol", StringComparison.Ordinal) || lower.Contains("code", StringComparison.Ordinal) ? "monospace"
            : !lower.Contains("sans", StringComparison.Ordinal) && (lower.Contains("serif", StringComparison.Ordinal) || lower.Contains("times", StringComparison.Ordinal)
                || lower.Contains("georgia", StringComparison.Ordinal) || lower.Contains("garamond", StringComparison.Ordinal)
                || lower.Contains("cambria", StringComparison.Ordinal) || lower.Contains("roman", StringComparison.Ordinal)
                || lower.Contains("minion", StringComparison.Ordinal) || lower.Contains("book", StringComparison.Ordinal)) ? "serif"
            : "sans-serif";
        // Names such as "F1" or "T1_0" are a writer's labels, not families, and PdfPecker's own fallback fonts are not
        // installed anywhere.
        if (family.Length < 3 || !family.Any(char.IsAsciiLetterLower) || family.StartsWith("Pdf Pecker", StringComparison.Ordinal)) return generic;
        string quoted = family.Contains(' ', StringComparison.Ordinal) ? "\"" + family + "\"" : family;
        return lower switch
        {
            "helvetica" or "arial" => "Arial,Helvetica,sans-serif",
            "times" or "times new roman" => "\"Times New Roman\",Times,serif",
            "courier" or "courier new" => "\"Courier New\",Courier,monospace",
            _ => quoted + "," + generic,
        };
    }
}

/// <summary>
/// The sides of a table's columns (<see cref="X"/>) and rows (<see cref="Y"/>) on the page, each with the span in which a
/// line drawn along it counts as a border there.
/// </summary>
internal sealed record TableGrid((double Side, double From, double To)[] X, (double Side, double From, double To)[] Y);

/// <summary>How a run of text looks: its CSS font family, size in points to the half point, and colour.</summary>
internal readonly record struct TextStyle(string Family, double Size, uint Color);

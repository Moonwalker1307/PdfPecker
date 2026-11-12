using System.Runtime.CompilerServices;
using PdfPecker.Css;
using FloatLines = PdfPecker.Layout.LayoutEngine.FloatLines;

namespace PdfPecker.Layout;

/// <summary>
/// Line breaking: the items of a paragraph are taken in chunks between break opportunities, and a chunk that does not
/// fit starts the next line, unless the style lets it break between characters.
/// </summary>
internal sealed partial class InlineLayout
{
    /// <summary>
    /// Breaks items into lines. With <paramref name="after"/>, a list matching <paramref name="items"/> item for item,
    /// the lines after the first are made from that list instead (::first-line).
    /// </summary>
    private List<Line> BreakLines(List<Item> items, float availW, LineIndent indent, FloatLines? fl = null, List<Item>? after = null, ComputedStyle? block = null, int hyphenLimit = -1)
    {
        var breaker = new LineBreaker(this, items, availW, indent, fl, after, hyphenLimit: hyphenLimit);
        breaker.Run();
        if (breaker.ClonesEdges) AddCloneEdges(breaker.Lines);
        // text-wrap: balance and pretty choose the breaks again once the greedy ones are known; beside floats, on pages
        // of different widths and with a ::first-line style the lines keep their greedy breaks.
        if (block is { TextWrap: not TextWrapStyle.Auto } && fl == null && after == null && !breaker.ClonesEdges)
            return WrapLines(items, breaker.Lines, block, availW, indent);
        return breaker.Lines;
    }

    /// <summary>Whether an inline box among the items gives each of its line fragments its own padding and borders (box-decoration-break: clone).</summary>
    private static bool HasCloneEdges(List<Item> items)
    {
        foreach (var it in items)
            if (it.Deco is { } d && (d.CloneLeft > 0 || d.CloneRight > 0)) return true;
        return false;
    }

    /// <summary>The last item of a line that takes part in it, or -1.</summary>
    private static int LastContent(List<Item> items)
    {
        int k = items.Count - 1;
        while (k >= 0 && (items[k].IsMarker || items[k].Kind == ItemKind.Float)) k--;
        return k;
    }

    /// <summary>The first item from <paramref name="k"/> on that takes part in a line, or with <paramref name="skipSpaces"/> the first that is not a space either; -1 when there is none.</summary>
    private static int FirstContent(List<Item> items, int k, bool skipSpaces = false)
    {
        while (k < items.Count && (items[k].IsMarker || items[k].Kind == ItemKind.Float || skipSpaces && items[k].Kind == ItemKind.Space)) k++;
        return k < items.Count ? k : -1;
    }

    /// <summary>
    /// The padding and borders that box-decoration-break: clone adds where a line breaks between <paramref name="last"/>
    /// and <paramref name="next"/>: on the right of the line, or on the left of the next line, of each cloned box that
    /// holds both.
    /// </summary>
    private static float CloneEdges(Item last, Item next, bool right)
    {
        var d = right ? CommonDeco(last.Deco, next.Deco) : CommonDeco(next.Deco, last.Deco);
        return d == null ? 0 : right ? d.CloneRight : d.CloneLeft;
    }

    /// <summary>
    /// box-decoration-break: clone: where a line breaks inside an inline box, the line ends with the box's right
    /// padding and border and the next line starts with its left ones, as spacers of the box. The line breaker kept
    /// room for the left ones only: as in Chrome, the right edge a break adds may overflow the line rather than move
    /// the last word down.
    /// </summary>
    private void AddCloneEdges(List<Line> lines)
    {
        for (int li = 0; li + 1 < lines.Count; li++)
        {
            var a = lines[li].Items;
            var b = lines[li + 1].Items;
            int last = LastContent(a), next = FirstContent(b, 0);
            if (last < 0 || next < 0) continue;
            Item lastItem = a[last], nextItem = b[next];
            // The end of the line: the inner box's edge first.
            int at = last + 1;
            for (var d = CommonDeco(lastItem.Deco, nextItem.Deco); d != null && d.CloneRight > 0; d = d.Parent)
                if (d.Style.BoxDecorationClone && d.Right > 0)
                {
                    a.Insert(at++, CloneEdge(d, d.Right, lastItem));
                    lines[li].Width += d.Right;
                }
            // The start of the next line: the outer box's edge first.
            for (var d = CommonDeco(nextItem.Deco, lastItem.Deco); d != null && d.CloneLeft > 0; d = d.Parent)
                if (d.Style.BoxDecorationClone && d.Left > 0)
                {
                    b.Insert(next, CloneEdge(d, d.Left, nextItem));
                    lines[li + 1].Width += d.Left;
                }
        }
    }

    /// <summary>A spacer of <paramref name="width"/> for the edge of cloned box <paramref name="d"/>, placed like its neighbour on the line.</summary>
    private Item CloneEdge(InlineDeco d, float width, Item neighbour)
    {
        var edge = Rent(ItemKind.Spacer, d.Style);
        edge.Width = width;
        edge.Deco = d;
        edge.Tag = null;
        edge.Href = neighbour.Href;
        edge.RelX = neighbour.RelX;
        edge.RelY = neighbour.RelY;
        edge.Level = neighbour.Level;
        edge.Para = neighbour.Para;
        return edge;
    }

    /// <summary>Whether overflow-wrap, word-break or line-break lets a word break between any two characters.</summary>
    private static bool BreaksAnywhere(ComputedStyle s) =>
        s.OverflowWrap != OverflowWrap.Normal || s.WordBreak == WordBreak.BreakAll || s.LineBreak == LineBreak.Anywhere;

    /// <summary>Whether word-break: break-all or line-break: anywhere applies: a chunk breaks at the end of the line instead of moving down.</summary>
    private static bool BreaksAtLineEnd(ComputedStyle s) => s.WordBreak == WordBreak.BreakAll || s.LineBreak == LineBreak.Anywhere;

    /// <summary>The text shown where a line breaks at a soft hyphen or hyphenation point (hyphenate-character).</summary>
    private static string HyphenText(ComputedStyle s) => s.HyphenateCharacter ?? "-";

    internal static float HyphenWidth(ComputedStyle s) => s.HyphenateCharacter is { } h
        ? TextWidth(s.Font, h, 0, h.Length, s.TextSize, s.LetterSpacing)
        : CharAdvance(s.Font, '-', s.TextSize, s.LetterSpacing);

    /// <summary>
    /// The state of breaking one paragraph: the line being filled, its room (which floats beside it reduce) and the
    /// finished lines. The current line's width and whether it holds anything but markers are kept up to date, so
    /// checking them costs nothing.
    /// </summary>
    private ref struct LineBreaker
    {
        private readonly InlineLayout _inline;
        private readonly float _availW;
        private readonly LineIndent _indent;
        private readonly FloatLines? _fl;
        // text-wrap: where each line must start, or null for greedy breaks throughout.
        private readonly byte[]? _plan;
        private int _count;
        private List<Item> _items;
        private List<Item>? _after;
        public readonly List<Line> Lines;
        private Line _cur;
        private bool _hasContent;
        private float _curW, _avail;
        // box-decoration-break: clone: whether any box clones its edges, and the last item of the line before, until the
        // left edges it gives this line are counted.
        public readonly bool ClonesEdges;
        private Item? _edgeAfter;
        // hyphenate-limit-lines: how many lines in a row, directly before the current one, ended at a hyphenation break.
        private int _hyphenatedRun;
        // hyphenate-limit-lines of the block container, which applies to the whole paragraph whatever the inline styles say; -1 = no limit.
        private readonly int _hyphenLimit;

        public LineBreaker(InlineLayout inline, List<Item> items, float availW, LineIndent indent, FloatLines? fl, List<Item>? after, byte[]? plan = null, int hyphenLimit = -1)
        {
            _inline = inline;
            _hyphenLimit = hyphenLimit;
            _plan = plan;
            _items = items;
            _availW = availW;
            _indent = indent;
            _fl = fl;
            _after = after;
            _count = items.Count;
            ClonesEdges = HasCloneEdges(items) || after != null && HasCloneEdges(after);
            _edgeAfter = null;
            Lines = inline.RentLineList();
            _cur = inline.RentLine();
            inline._engine.NoteMarkerLine(_cur);
            fl?.Begin(Lines);
            _avail = (fl?.Avail ?? availW) - indent.First;
        }

        /// <summary>The room of the current line, after floats changed it, less the line's indent.</summary>
        private void TakeFloatRoom() => _avail = _fl!.Avail - _indent.Of(Lines, Lines.Count);

        private void Add(Item it)
        {
            if (_edgeAfter != null && !it.IsMarker && it.Kind != ItemKind.Float)
            {
                _curW += CloneEdges(_edgeAfter, it, right: false);
                _edgeAfter = null;
            }
            if (!_hasContent && it.Kind == ItemKind.Text && it.Length > 0)
            {
                // text-spacing-trim at the start of a line: the punctuation that starts it has no neighbour before it.
                var started = TrimLineStart(it, wrapped: Lines.Count > 0 && !Lines[^1].Forced);
                _curW += started.Width - it.Width;
                it = started;
            }
            _cur.Items.Add(it);
            if (!it.IsMarker) _hasContent = true;
        }

        /// <summary>Ends the current line and starts the next; <see cref="Resume"/> then finds where it starts.</summary>
        private void Finish(bool forced)
        {
            var li = _cur.Items;
            // Trailing collapsible/breakable spaces hang and are not counted.
            while (li.Count > 0 && li[^1] is { Kind: ItemKind.Space } trailing && (trailing.Breakable || IsCollapsible(trailing.Style)))
                li.RemoveAt(li.Count - 1);
            // A line broken at a soft hyphen shows a hyphen.
            if (!forced && li.Count > 0 && li[^1] is { Kind: ItemKind.Text, BreakAfter: BreakAfter.SoftHyphen } shy)
            {
                li[^1] = shy.CloneWithText(shy.Text + HyphenText(shy.Style), shy.Width + shy.TrimEnd + HyphenWidth(shy.Style));
                _hyphenatedRun++;
            }
            else _hyphenatedRun = 0;
            float width = 0;
            foreach (var it in li) width += it.Width;
            _cur.Width = width;
            _cur.Forced = forced;
            _fl?.End(_cur);
            _edgeAfter = ClonesEdges && LastContent(li) is var last and >= 0 ? li[last] : null;
            Lines.Add(_cur);
            _cur = _inline.RentLine();
            _hasContent = false;
            _curW = 0;
            _fl?.Begin(Lines);
            _avail = (_fl?.Avail ?? _availW) - _indent.Of(Lines, Lines.Count);
        }

        /// <summary>
        /// The index the line after a finished one starts at: <paramref name="next"/>, or, once the first line is
        /// finished, the same place in the usual items, which replace the ::first-line ones. Both lists hold the same
        /// characters, but small capitals, bidi levels and hyphenation can cut them into different items, so the place
        /// is found by counting characters, and one for every other item, up to <paramref name="offset"/> characters
        /// into item <paramref name="next"/>. A usual word that the first line ends inside is cut there.
        /// </summary>
        private int Resume(int next, int offset = 0)
        {
            if (_after is not { } after) return next;
            _after = null;
            int target = offset;
            for (int k = 0; k < next; k++) target += ContentUnits(_items[k]);
            _items = after;
            _count = after.Count;
            int at = 0;
            for (int m = 0; m < after.Count; m++)
            {
                if (at >= target) return m;
                var it = after[m];
                if (at + ContentUnits(it) > target)
                {
                    int cut = target - at, length = it.Length - cut;
                    var s = it.Style;
                    var rest = it.Slice(cut, length, ItemWidth(it, it.Start + cut, length));
                    var items = new List<Item>(after.Count - m) { rest };
                    for (int r = m + 1; r < after.Count; r++) items.Add(after[r]);
                    _items = items;
                    _count = items.Count;
                    return 0;
                }
                at += ContentUnits(it);
            }
            return after.Count;
        }

        public void Run()
        {
            int i = 0;
            while (i < _count)
            {
                var it = _items[i];
                if (it.Kind == ItemKind.Float)
                {
                    if (_fl != null)
                    {
                        _fl.Float(it, _curW, _hasContent);
                        TakeFloatRoom();
                    }
                    i++;
                    continue;
                }
                if (it.Kind == ItemKind.Break)
                {
                    Add(it);
                    Finish(forced: true);
                    i = Resume(i + 1);
                    continue;
                }
                if (it.Kind == ItemKind.Space && it.Breakable)
                {
                    if (!_hasContent && IsCollapsible(it.Style)) { i++; continue; }
                    _curW += it.Width + KernWithLast(it);
                    Add(it);
                    i++;
                    continue;
                }
                i = PlaceChunk(i);
            }
            if (_cur.Items.Count > 0) Finish(forced: true);
            else if (Lines.Count > 0) Lines[^1].Forced = true;
            _inline.ReturnLine(_cur); // the empty line Finish started
        }

        // The chunk helpers run once per word of the document, so they are inlined into PlaceChunk as they were
        // when it was one loop.
        /// <summary>
        /// The end of the chunk that starts at <paramref name="i"/>: the items up to the next break opportunity, with
        /// their width and whether any of them may break between characters.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ChunkEnd(int i, out float width, out bool splittable)
        {
            int j = i;
            width = 0;
            splittable = false;
            while (j < _count)
            {
                var g = _items[j];
                if (g.Kind == ItemKind.Break || g.Kind == ItemKind.Space && g.Breakable) break;
                width += g.Width;
                if (g.IsText && BreaksAnywhere(g.Style)) splittable = true;
                j++;
                if (g.BreakAfter != BreakAfter.None) break;
            }
            return j;
        }

        /// <summary>The kerning between the items of the chunk from <paramref name="i"/> to <paramref name="j"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly float KernWithin(int i, int j)
        {
            float kern = 0;
            for (int k = i + 1; k < j; k++) kern += PairKern(_items[k - 1], _items[k]);
            return kern;
        }

        /// <summary>The kerning between the current line's last item and <paramref name="it"/>, if it joins the line.</summary>
        private readonly float KernWithLast(Item it) => _cur.Items.Count > 0 ? PairKern(_cur.Items[^1], it) : 0;

        /// <summary>The room a chunk needs: its width, plus the hyphen a soft hyphen shows if the line breaks after it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Needed(int i, int j, float width)
        {
            if (j > i && _items[j - 1].BreakAfter == BreakAfter.SoftHyphen) width += HyphenWidth(_items[j - 1].Style);
            return width;
        }

        /// <summary>Places the chunk that starts at <paramref name="i"/> on this line or the next; returns the index after it.</summary>
        private int PlaceChunk(int i)
        {
            int j = ChunkEnd(i, out float gw, out bool splittable);
            // The line is as wide as it is drawn: with the kerning between the chunk's items and between the line's last
            // item and the chunk, as browsers shape the whole run (a space before "A" or "T" kerns in many fonts).
            gw += KernWithin(i, j);
            float need = Needed(i, j, gw) + KernWithLast(_items[i]);
            var context = ContextOf(i, j);
            need += context;
            // word-break: break-all and line-break: anywhere break the chunk at the end of the line instead of moving it to
            // the next.
            bool breakAll = splittable && _items[i].IsText && BreaksAtLineEnd(_items[i].Style);

            // A planned paragraph breaks where the plan says, and greedily where it has no say.
            bool planned = _plan is { } plan && plan[i] != PlanGreedy;
            bool full = planned ? _plan![i] == PlanBreak : _curW + need > _avail + Eps && !breakAll;
            // text-spacing-trim: a chunk that ends with closing punctuation fits when the punctuation's blank half goes.
            if (full && !planned && !breakAll && TrimLineEnd(j) is { } trimmed && _curW + need - (_items[j - 1].Width - trimmed.Width) <= _avail + Eps)
            {
                _curW += gw - (_items[j - 1].Width - trimmed.Width) + KernWithLast(_items[i]);
                for (int k = i; k < j - 1; k++) Add(_items[k]);
                Add(trimmed);
                return j;
            }
            if (_hasContent && full)
            {
                // hyphenate-limit-lines: a line that would be the one hyphenated line too many ends earlier instead.
                if (_plan == null && HyphenLimitReached() && MoveHyphenatedTail(i) is var moved and >= 0)
                {
                    bool firstLine = _after != null;
                    Finish(forced: false);
                    // After a ::first-line, the moved words are found again in the usual items.
                    return firstLine ? Resume(moved) : moved;
                }
                bool switching = _after != null;
                Finish(forced: false);
                if (switching)
                {
                    // The chunk moves to the second line, in its usual style, and is taken again from the usual items.
                    i = Resume(i);
                    j = ChunkEnd(i, out gw, out splittable);
                    if (j == i) return i;
                    gw += KernWithin(i, j);
                    need = Needed(i, j, gw) + context;
                }
                // On the new line the chunk starts a run of its own.
                need -= context;
                context = ContextOf(i, j);
                need += context;
            }
            // Too narrow beside the floats: the line moves down past them until the chunk fits.
            if (_fl != null && !_hasContent && need > _avail + Eps)
                while (_fl.Lower())
                {
                    TakeFloatRoom();
                    if (need <= _avail + Eps) break;
                }
            // The kerning with the line's last item counts here too: a space before "A" narrows the word just enough that
            // a box as wide as its content (measured with that kerning) holds it, so it must not break inside it.
            if (splittable && _curW + gw + KernWithLast(_items[i]) + context > _avail + Eps)
            {
                // A chunk wider than the line that overflow-wrap or word-break lets break between characters.
                for (int k = i; k < j; k++)
                    if (AddSplittable(k) is var resume and >= 0) return resume;
            }
            else
            {
                // Fits, or may not break: a word too wide for the line overflows it, as in browsers.
                _curW += gw + KernWithLast(_items[i]) + context;
                for (int k = i; k < j; k++) Add(_items[k]);
            }
            return j;
        }

        /// <summary>
        /// The change to the line's width when the chunk from <paramref name="i"/> to <paramref name="j"/> joins it, from
        /// a font whose lookups reach across items: the line draws its last run of text and the chunk as one shaped run,
        /// which can be wider or narrower than their words measured one by one (see ContextBetweenItems), and Chrome
        /// breaks the line by the width it draws.
        /// </summary>
        /// <remarks>
        /// Shaping the whole run again for every chunk would make breaking a line quadratic in its length, so the change
        /// is measured where the chunk meets the run: from the start of the run's last two words to the chunk's last
        /// word, with and without the chunk. Contextual lookups look a few glyphs around the one they change, which that
        /// window holds; the line is still aligned by its whole run.
        /// </remarks>
        private readonly float ContextOf(int i, int j)
        {
            bool inContext = false;
            for (int k = i; k < j && !inContext; k++) inContext = _items[k].IsText && _items[k].Style.Font.ShapesInContext;
            if (!inContext) return 0;
            for (int k = i + 1; k < j; k++)
                if (!JoinedItems(_items[k - 1], _items[k])) return ContextOfMixed(i, j);
            var li = _cur.Items;
            int n = li.Count, end = n + j - i;
            // The last word of the run the chunk continues, and the start of the window: the word before it.
            int lastWord = -1, from = -1;
            if (n > 0 && JoinedItems(li[n - 1], _items[i]))
                for (int m = n - 1, words = 0; m >= 0 && words < 2; m--)
                {
                    if (m < n - 1 && !JoinedItems(li[m], li[m + 1])) break;
                    if (!IsWord(li[m])) continue;
                    if (lastWord < 0) lastWord = m;
                    from = m;
                    words++;
                }
            int newLast = -1;
            for (int k = end - 1; k >= n && newLast < 0; k--)
                if (IsWord(At(k, i))) newLast = k;
            if (newLast < 0) return 0;
            if (lastWord < 0)
            {
                // The chunk starts a run, which only a chunk of several items draws differently.
                for (int k = n; k < newLast && from < 0; k++)
                    if (IsWord(At(k, i))) from = k;
                if (from < 0) return 0;
                lastWord = from;
            }
            var first = At(from, i);
            var font = first.Style.Font;
            float units = Shaped(from, newLast, i) - Shaped(from, lastWord, i), kern = 0;
            for (int m = lastWord + 1; m <= newLast; m++)
            {
                var it = At(m, i);
                units -= font.MeasureUnits(it.Source, it.Start, it.Length);
                kern += PairKern(At(m - 1, i), it);
            }
            return units * first.Style.TextSize / 1000f - kern;
        }

        /// <summary>A word of a run that ContextBetweenItems shapes.</summary>
        private static bool IsWord(Item it) => it.IsText && it.Length > 0 && it.Level == 0 && it.Style.Font.ShapesInContext;

        /// <summary>
        /// The width of items <paramref name="from"/> to <paramref name="to"/> of the line followed by the chunk at
        /// <paramref name="i"/> (see At), shaped as one run, in 1/1000 em.
        /// </summary>
        private readonly int Shaped(int from, int to, int i)
        {
            var text = (_inline._contextText ??= new System.Text.StringBuilder(256)).Clear();
            for (int m = from; m <= to; m++)
            {
                var it = At(m, i);
                text.Append(it.Source, it.Start, it.Length);
            }
            var run = text.ToString();
            return At(from, i).Style.Font.MeasureUnits(run, 0, run.Length);
        }

        /// <summary>Item <paramref name="k"/> of the line followed by the chunk that starts at <paramref name="i"/>.</summary>
        private readonly Item At(int k, int i) => k < _cur.Items.Count ? _cur.Items[k] : _items[i + k - _cur.Items.Count];

        /// <summary>
        /// <see cref="ContextOf"/> for a chunk whose items are drawn as more than one run, such as a word with a part
        /// in another style: the change of the line's last run and the chunk, measured whole.
        /// </summary>
        private readonly float ContextOfMixed(int i, int j)
        {
            var li = _cur.Items;
            int from = li.Count;
            if (from > 0 && JoinedItems(li[from - 1], _items[i]))
                for (from--; from > 0 && JoinedItems(li[from - 1], li[from]); from--) { }
            var scratch = _inline._contextItems;
            scratch.Clear();
            for (int k = from; k < li.Count; k++) scratch.Add(li[k]);
            float before = ContextBetweenItems(scratch, null, LastText(scratch, scratch.Count));
            for (int k = i; k < j; k++) scratch.Add(_items[k]);
            float after = ContextBetweenItems(scratch, null, LastText(scratch, scratch.Count));
            scratch.Clear();
            return after - before;
        }

        /// <summary>Whether two neighbouring items are drawn in one run of text (see JoinsRun).</summary>
        private static bool JoinedItems(Item a, Item b) => IsTextOrSpace(a) && a.Level == 0 && JoinsRun(a, b, a);

        /// <summary>The index of the last text item among the first <paramref name="count"/>, or -1.</summary>
        private static int LastText(List<Item> items, int count)
        {
            for (int k = count - 1; k >= 0; k--)
                if (items[k].IsText) return k;
            return -1;
        }

        /// <summary>Whether the current line, if it ended here, would be hyphenated beyond hyphenate-limit-lines.</summary>
        private readonly bool HyphenLimitReached()
        {
            var li = _cur.Items;
            if (li.Count == 0 || li[^1] is not { Kind: ItemKind.Text, BreakAfter: BreakAfter.SoftHyphen }) return false;
            return _hyphenLimit >= 0 && _hyphenatedRun >= _hyphenLimit;
        }

        /// <summary>
        /// Takes the words after the current line's last space (or other plain break opportunity) off the line, so that
        /// it ends there without a hyphen, and returns the index in the items the next line starts with, where those
        /// words are placed again. Something visible must stay on the line, not only spaces that pre-wrap keeps. Returns
        /// -1 and leaves the line as it is when it has no earlier break opportunity (a line of one hyphenated word still
        /// breaks at its hyphen), or when its items cannot be matched with the paragraph's, as after a float or
        /// text-spacing-trim changed them: those lines may keep a hyphenated end beyond the limit.
        /// </summary>
        private int MoveHyphenatedTail(int chunkStart)
        {
            var li = _cur.Items;
            int cut = -1;
            for (int k = li.Count - 2; k >= 0 && cut < 0; k--)
            {
                if (li[k] is not ({ Kind: ItemKind.Space, Breakable: true } or { BreakAfter: BreakAfter.Break })) continue;
                for (int v = k; v >= 0 && cut < 0; v--)
                    if (li[v].Kind != ItemKind.Space && !li[v].IsMarker) cut = k + 1;
            }
            if (cut <= 0 || cut >= li.Count) return -1;
            int moved = li.Count - cut;
            if (chunkStart < moved) return -1;
            for (int t = 1; t <= moved; t++)
                if (!ReferenceEquals(_items[chunkStart - t], li[li.Count - t])) return -1;
            // The line's width loses the moved items and the kerning in front of each, as the chunks were measured. What
            // else it holds, such as the left edges box-decoration-break: clone gives its first item, stays.
            for (int k = cut; k < li.Count; k++) _curW -= li[k].Width + PairKern(li[k - 1], li[k]);
            li.RemoveRange(cut, moved);
            return chunkStart - moved;
        }

        /// <summary>
        /// The last item of the chunk that ends before <paramref name="j"/> with its closing punctuation trimmed, as it
        /// may be at the end of a line, or null when it ends otherwise or a line may not break after it.
        /// </summary>
        private readonly Item? TrimLineEnd(int j)
        {
            var last = _items[j - 1];
            // A chunk ends where a line may break, so only its last character matters. As in Chrome, that is a closing
            // bracket or quote: dots and colons, closing only by where the font draws them, keep their space.
            if (last.Kind != ItemKind.Text || last.Length == 0 || last.TrimEnd > 0 || last.BreakAfter == BreakAfter.SoftHyphen) return null;
            int cp = LastCodePoint(last.Source, last.Start + last.Length);
            if (Text.TextSpacingCharacters.Classify(cp) is not (Text.TextSpacingClass.Close or Text.TextSpacingClass.CloseQuote)
                || HanKerning.Classify(last.Style.Font, cp) != Text.TextSpacingClass.Close) return null;
            float trim = TrimOf(last, last.Length - (cp > 0xFFFF ? 2 : 1));
            return trim > 0 ? last.Trimmed(last.TrimStart, trim) : null;
        }

        /// <summary>
        /// Adds an item of a chunk that breaks between characters: as much of a word as fits ends the line, and the rest
        /// goes on from the next line. Returns -1, or, when the first line ended inside the chunk, the index of the
        /// usual items the rest of the chunk starts at.
        /// </summary>
        private int AddSplittable(int k)
        {
            var whole = _items[k];
            var g = whole;
            while (true)
            {
                float room = _avail - _curW;
                if (g.Width <= room + Eps || g.Kind == ItemKind.Anchor)
                {
                    Add(g);
                    _curW += g.Width;
                    return -1;
                }
                if (g.IsText && g.Length > 1 && BreaksAnywhere(g.Style))
                    g = BreakWord(g, ClonesEdges ? room - CloneEdges(g, g, right: true) : room);
                else if (!_hasContent)
                {
                    Add(g); // overflows, nothing else we can do
                    _curW += g.Width;
                    return -1;
                }
                bool switching = _after != null;
                Finish(forced: false);
                if (switching) return Resume(k, whole.Length - g.Length);
            }
        }

        /// <summary>
        /// Ends the line with as many of the word's characters as fit in <paramref name="room"/> (at least one on an
        /// empty line) and returns the rest, or the whole word when none fit.
        /// </summary>
        private Item BreakWord(Item g, float room)
        {
            // Work on the item's slice of its source text: copying the rest of a long word for every line would make
            // breaking it quadratic.
            var font = g.Style.Font;
            float size = g.Style.TextSize, ls = g.Style.LetterSpacing;
            string src = g.Source;
            int start = g.Start, len = g.Length;
            int fit = 0;
            float fw = 0;
            while (fit < len)
            {
                float cw = g.Upright ? ItemCharAdvance(g, start + fit) : CharAdvance(font, src[start + fit], size, ls);
                if (fw + cw > room + Eps) break;
                fw += cw;
                fit++;
            }
            if (fit == 0 && !_hasContent) { fit = 1; fw = g.Upright ? ItemCharAdvance(g, start) : CharAdvance(font, src[start], size, ls); }
            if (fit == 0) return g;
            var head = g.Slice(0, fit, fw);
            head.BreakAfter = BreakAfter.None;
            Add(head);
            // The rest is measured exactly once it fits on a line; until then its width only needs to exceed the room,
            // so subtracting is enough.
            float restW = g.Width + g.TrimStart + g.TrimEnd - fw;
            int restLen = len - fit;
            if (restW <= _availW + Eps) restW = g.Upright ? ItemWidth(g, start + fit, restLen) : TextWidth(font, src, start + fit, restLen, size, ls);
            return g.Slice(fit, restLen, restW);
        }
    }
}

using System.Runtime.CompilerServices;
using PdfPecker.Css;

namespace PdfPecker.Layout;

/// <summary>
/// Line breaking: the items of a paragraph are taken in chunks between break opportunities, and a chunk that does not
/// fit starts the next line, unless the style lets it break between characters.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>
    /// Breaks items into lines. With <paramref name="after"/>, a list matching <paramref name="items"/> item for item,
    /// the lines after the first are made from that list instead (::first-line).
    /// </summary>
    private List<Line> BreakLines(List<Item> items, float availW, float firstIndent, FloatLines? fl = null, List<Item>? after = null)
    {
        var breaker = new LineBreaker(this, items, availW, firstIndent, fl, after);
        breaker.Run();
        if (breaker.ClonesEdges) AddCloneEdges(breaker.Lines);
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
    /// padding and border and the next line starts with its left ones, as spacers of the box. The line breaker has
    /// already kept room for them.
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

    private static float HyphenWidth(ComputedStyle s) => s.HyphenateCharacter is { } h
        ? TextWidth(s.Font, h, 0, h.Length, s.TextSize, s.LetterSpacing)
        : CharAdvance(s.Font, '-', s.TextSize, s.LetterSpacing);

    /// <summary>
    /// The state of breaking one paragraph: the line being filled, its room (which floats beside it reduce) and the
    /// finished lines. The current line's width and whether it holds anything but markers are kept up to date, so
    /// checking them costs nothing.
    /// </summary>
    private ref struct LineBreaker
    {
        private readonly LayoutEngine _engine;
        private readonly float _availW, _firstIndent;
        private readonly FloatLines? _fl;
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

        public LineBreaker(LayoutEngine engine, List<Item> items, float availW, float firstIndent, FloatLines? fl, List<Item>? after)
        {
            _engine = engine;
            _items = items;
            _availW = availW;
            _firstIndent = firstIndent;
            _fl = fl;
            _after = after;
            _count = items.Count;
            ClonesEdges = HasCloneEdges(items) || after != null && HasCloneEdges(after);
            _edgeAfter = null;
            Lines = engine.RentLineList();
            _cur = engine.RentLine();
            fl?.Begin(Lines);
            _avail = (fl?.Avail ?? availW) - firstIndent;
        }

        /// <summary>The room of the current line, after floats changed it: the first line also loses its indent.</summary>
        private void TakeFloatRoom() => _avail = _fl!.Avail - (Lines.Count == 0 ? _firstIndent : 0);

        private void Add(Item it)
        {
            if (_edgeAfter != null && !it.IsMarker && it.Kind != ItemKind.Float)
            {
                _curW += CloneEdges(_edgeAfter, it, right: false);
                _edgeAfter = null;
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
                li[^1] = shy.CloneWithText(shy.Text + HyphenText(shy.Style), shy.Width + HyphenWidth(shy.Style));
            float width = 0;
            foreach (var it in li) width += it.Width;
            _cur.Width = width;
            _cur.Forced = forced;
            _fl?.End(_cur);
            _edgeAfter = ClonesEdges && LastContent(li) is var last and >= 0 ? li[last] : null;
            Lines.Add(_cur);
            _cur = _engine.RentLine();
            _hasContent = false;
            _curW = 0;
            _fl?.Begin(Lines);
            _avail = _fl?.Avail ?? _availW;
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
                    var rest = it.Slice(cut, length, TextWidth(s.Font, it.Source, it.Start + cut, length, s.TextSize, s.LetterSpacing));
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
            _engine.ReturnLine(_cur); // the empty line Finish started
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
            // word-break: break-all and line-break: anywhere break the chunk at the end of the line instead of moving it to
            // the next.
            bool breakAll = splittable && _items[i].IsText && BreaksAtLineEnd(_items[i].Style);

            // A cloned box the line would break inside ends the line with its right edge.
            if (ClonesEdges && _hasContent && FirstContent(_items, j, skipSpaces: true) is var next and >= 0) need += CloneEdges(_items[j - 1], _items[next], right: true);
            if (_hasContent && _curW + need > _avail + Eps && !breakAll)
            {
                bool switching = _after != null;
                Finish(forced: false);
                if (switching)
                {
                    // The chunk moves to the second line, in its usual style, and is taken again from the usual items.
                    i = Resume(i);
                    j = ChunkEnd(i, out gw, out splittable);
                    if (j == i) return i;
                    gw += KernWithin(i, j);
                    need = Needed(i, j, gw);
                }
            }
            // Too narrow beside the floats: the line moves down past them until the chunk fits.
            if (_fl != null && !_hasContent && need > _avail + Eps)
                while (_fl.Lower())
                {
                    TakeFloatRoom();
                    if (need <= _avail + Eps) break;
                }
            if (splittable && _curW + gw > _avail + Eps)
            {
                // A chunk wider than the line that overflow-wrap or word-break lets break between characters.
                for (int k = i; k < j; k++)
                    if (AddSplittable(k) is var resume and >= 0) return resume;
            }
            else
            {
                // Fits, or may not break: a word too wide for the line overflows it, as in browsers.
                _curW += gw + KernWithLast(_items[i]);
                for (int k = i; k < j; k++) Add(_items[k]);
            }
            return j;
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
                float cw = CharAdvance(font, src[start + fit], size, ls);
                if (fw + cw > room + Eps) break;
                fw += cw;
                fit++;
            }
            if (fit == 0 && !_hasContent) { fit = 1; fw = CharAdvance(font, src[start], size, ls); }
            if (fit == 0) return g;
            var head = g.Slice(0, fit, fw);
            head.BreakAfter = BreakAfter.None;
            Add(head);
            // The rest is measured exactly once it fits on a line; until then its width only needs to exceed the room,
            // so subtracting is enough.
            float restW = g.Width - fw;
            int restLen = len - fit;
            if (restW <= _availW + Eps) restW = TextWidth(font, src, start + fit, restLen, size, ls);
            return g.Slice(fit, restLen, restW);
        }
    }
}

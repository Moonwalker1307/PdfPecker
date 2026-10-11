using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>Inline formatting: turning text and inline boxes into words, breaking them into lines and placing the lines.</summary>
internal sealed partial class LayoutEngine
{
    private readonly StringBuilder _runText = new(256);
    private float[] _xs = new float[64];
    private float[] _lineA = new float[32], _lineD = new float[32];

    // Items are pooled: a document creates one per word, and they are only needed while their paragraph is laid out.
    private readonly List<Item> _itemPool = new();
    private int _itemPoolUsed;
    private int _inlineDepth;

    private Item Rent(ItemKind kind, ComputedStyle style)
    {
        Item it;
        if (_itemPoolUsed < _itemPool.Count)
        {
            it = _itemPool[_itemPoolUsed];
            it.Reset();
        }
        else
        {
            it = new Item();
            _itemPool.Add(it);
        }
        _itemPoolUsed++;
        it.Kind = kind;
        it.Style = style;
        return it;
    }

    private enum ItemKind : byte { Text, Space, Break, Image, Box, Spacer, Anchor }

    private sealed class Item
    {
        public ItemKind Kind;
        public ComputedStyle Style = null!;
        // Text is a slice of Source, so words don't need their own strings until a line is written.
        public string Source = "";
        public int Start, Length;
        public float Width;
        public bool Breakable;
        public string? Href;
        public ComputedStyle? Deco;   // nearest inline ancestor that paints a background
        public PdfImage? Image;
        public float Height;          // image or inline-block height
        public float BoxBaseline;     // inline-block: distance from its top to its baseline
        public List<DrawOp>? BoxOps;
        public string? AnchorId;

        public void Reset()
        {
            Source = "";
            Start = Length = 0;
            Width = Height = BoxBaseline = 0;
            Breakable = false;
            Href = null;
            Deco = null;
            Image = null;
            BoxOps = null;
            AnchorId = null;
        }

        public string Text => Start == 0 && Length == Source.Length ? Source : Source.Substring(Start, Length);

        public Item CloneWithText(string text, float width)
        {
            var c = (Item)MemberwiseClone();
            c.Source = text;
            c.Start = 0;
            c.Length = text.Length;
            c.Width = width;
            return c;
        }
    }

    private sealed class Line
    {
        public readonly List<Item> Items = new();
        public float Width;
        public bool Forced;
    }

    // ------------------------------------------------------------------ collection

    private sealed class CollectState
    {
        public readonly List<Item> Items = new();
        public bool LastWasSpace = true;
        public int Column;
        public float AvailWidth;
    }

    private List<Item> CollectInline(List<Node> run, float availW)
    {
        var st = new CollectState { AvailWidth = availW };
        foreach (var node in run) CollectNode(st, node, null, null);
        return st.Items;
    }

    private void CollectNode(CollectState st, Node node, string? href, ComputedStyle? deco)
    {
        if (node is TextNode t)
        {
            AddText(st, t.Text, t.Parent!.Style, href, deco);
            return;
        }
        var e = (Element)node;
        var s = e.Style;
        if (s == null || s.Display == Display.None || e.Detached) return;

        if (e.Id != null) { var it_ = Rent(ItemKind.Anchor, s); it_.AnchorId = e.Id; st.Items.Add(it_); }
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) { var it_ = Rent(ItemKind.Anchor, s); it_.AnchorId = aname; st.Items.Add(it_); }

        switch (e.Name)
        {
            case "br":
                { var it_ = Rent(ItemKind.Break, s); st.Items.Add(it_); }
                st.LastWasSpace = true;
                st.Column = 0;
                return;
            case "svg":
            {
                var svg = SvgSize(e, st.AvailWidth, out float sw, out float sh);
                if (sw > 0 && sh > 0)
                {
                    float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0);
                    float pl = s.PaddingLeft.Resolve(0) + s.BorderLeft, pr = s.PaddingRight.Resolve(0) + s.BorderRight;
                    float ptop = s.PaddingTop.Resolve(0) + s.BorderTop, pbot = s.PaddingBottom.Resolve(0) + s.BorderBottom;
                    // Drawn as a small inline box so padding and borders work as for any replaced element.
                    var ops = new List<DrawOp>();
                    var group = new GroupOp();
                    ops.Add(group);
                    BoxPainter.Paint(group, s, 0, 0, sw + pl + pr, sh + ptop + pbot);
                    if (s.Visible && svg != null) ops.Add(new SvgOp(pl, ptop, sw, sh, svg));
                    if (ml > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = ml; it_.Href = href; st.Items.Add(it_); }
                    float boxH = sh + ptop + pbot;
                    st.Items.Add(new Item { Kind = ItemKind.Box, Style = s, Width = sw + pl + pr, Height = boxH, BoxBaseline = boxH, BoxOps = ops, Href = href ?? LinkOf(e) });
                    if (mr > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = mr; it_.Href = href; st.Items.Add(it_); }
                    st.LastWasSpace = false;
                }
                return;
            }
            case "img":
            {
                var img = ImageSize(e, st.AvailWidth, out float w, out float h);
                if (w > 0 && h > 0)
                {
                    float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0);
                    if (ml > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = ml; it_.Href = href; st.Items.Add(it_); }
                    { var it_ = Rent(ItemKind.Image, s); it_.Image = s.Visible ? img : null; it_.Width = w; it_.Height = h; it_.Href = href ?? LinkOf(e); st.Items.Add(it_); }
                    if (mr > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = mr; it_.Href = href; st.Items.Add(it_); }
                    st.LastWasSpace = false;
                }
                return;
            }
        }

        if (e.Name == "a" && e.GetAttribute("href") is { } link && link.Length > 0) href = link;

        if (IsAtomicInline(s.Display))
        {
            st.Items.Add(LayoutInlineBlock(e, st.AvailWidth, href));
            st.LastWasSpace = false;
            return;
        }

        if (CssColor.IsVisible(s.BackgroundColor)) deco = s;
        float left = s.PaddingLeft.Resolve(0) + s.BorderLeft + (s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0));
        float right = s.PaddingRight.Resolve(0) + s.BorderRight + (s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0));
        if (left > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = left; it_.Href = href; it_.Deco = deco; st.Items.Add(it_); }
        foreach (var c in e.Children) CollectNode(st, c, href, deco);
        if (right > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = right; it_.Href = href; it_.Deco = deco; st.Items.Add(it_); }
    }

    private static string? LinkOf(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "a" && p.GetAttribute("href") is { Length: > 0 } h) return h;
        return null;
    }

    private Item LayoutInlineBlock(Element e, float availW, string? href)
    {
        var savedOps = _ops;
        bool savedPaginate = _paginate;
        var savedMarker = _marker;
        float savedBaseline = _lastBaseline;
        _ops = new List<DrawOp>();
        _paginate = false;
        _marker = null;
        _lastBaseline = float.NaN;

        var s = e.Style;
        float mt = s.MarginTop.Resolve(availW), mb = s.MarginBottom.Resolve(availW);
        float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(availW), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(availW);
        _lastBoxRight = 0;
        float bottom = LayoutBlockBox(e, 0, mt, availW, shrinkToFit: true);
        float width = _lastBoxRight + mr;
        float height = bottom + mb;
        float baseline = float.IsNaN(_lastBaseline) ? height : _lastBaseline;
        var ops = _ops;

        _ops = savedOps;
        _paginate = savedPaginate;
        _marker = savedMarker;
        _lastBaseline = savedBaseline;
        _ = ml;
        return new Item { Kind = ItemKind.Box, Style = s, Width = width, Height = height, BoxBaseline = baseline, BoxOps = ops, Href = href };
    }

    private void AddText(CollectState st, string text, ComputedStyle style, string? href, ComputedStyle? deco)
    {
        var ws = style.WhiteSpace;
        bool collapse = ws is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;
        bool keepNewlines = ws is WhiteSpace.Pre or WhiteSpace.PreWrap or WhiteSpace.PreLine;
        bool breakable = ws is WhiteSpace.Normal or WhiteSpace.PreWrap or WhiteSpace.PreLine;
        var font = style.Font;
        float size = style.FontSize;
        float spaceW = font.CharWidth(' ', size) + style.LetterSpacing + style.WordSpacing;
        var items = st.Items;

        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '\r' || c == '\n')
            {
                if (keepNewlines)
                {
                    if (c == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
                    i++;
                    { var it_ = Rent(ItemKind.Break, style); it_.Href = href; it_.Deco = deco; items.Add(it_); }
                    st.LastWasSpace = true;
                    st.Column = 0;
                    continue;
                }
                c = ' ';
            }
            if (c == ' ' || c == '\t' || c == '\n' || c == '\f' || c == '\r')
            {
                if (collapse)
                {
                    if (!st.LastWasSpace)
                    {
                        { var it_ = Rent(ItemKind.Space, style); it_.Source = " "; it_.Length = 1; it_.Width = spaceW; it_.Breakable = breakable; it_.Href = href; it_.Deco = deco; items.Add(it_); }
                        st.LastWasSpace = true;
                    }
                    i++;
                    continue;
                }
                int count = c == '\t' ? 8 - st.Column % 8 : 1;
                for (int k = 0; k < count; k++)
                    { var it_ = Rent(ItemKind.Space, style); it_.Source = " "; it_.Length = 1; it_.Width = spaceW; it_.Breakable = breakable; it_.Href = href; it_.Deco = deco; items.Add(it_); }
                st.Column += count;
                st.LastWasSpace = true;
                i++;
                continue;
            }

            int start = i;
            while (i < n)
            {
                char d = text[i];
                if (d == ' ' || d == '\t' || d == '\n' || d == '\r' || d == '\f') break;
                i++;
            }
            string src = text;
            int wStart = start, wLen = i - start;
            var span = text.AsSpan(start, wLen);
            if (span.IndexOf('\u00AD') >= 0 || span.IndexOf('\u200B') >= 0 || style.TextTransform != TextTransform.None)
            {
                string word = span.ToString().Replace("\u00AD", "").Replace("\u200B", "");
                if (word.Length == 0) continue;
                switch (style.TextTransform)
                {
                    case TextTransform.Uppercase: word = word.ToUpperInvariant(); break;
                    case TextTransform.Lowercase: word = word.ToLowerInvariant(); break;
                    case TextTransform.Capitalize:
                        if (st.LastWasSpace && char.IsLower(word[0])) word = char.ToUpperInvariant(word[0]) + word.Substring(1);
                        break;
                }
                src = word;
                wStart = 0;
                wLen = word.Length;
            }
            float w = font.MeasureUnits(src, wStart, wLen) * size / 1000f + style.LetterSpacing * wLen;
            { var it_ = Rent(ItemKind.Text, style); it_.Source = src; it_.Start = wStart; it_.Length = wLen; it_.Width = w; it_.Href = href; it_.Deco = deco; items.Add(it_); }
            st.LastWasSpace = false;
            st.Column += wLen;
        }
    }

    private static bool HasRenderable(List<Item> items)
    {
        foreach (var it in items)
        {
            switch (it.Kind)
            {
                case ItemKind.Text:
                case ItemKind.Image:
                case ItemKind.Box:
                case ItemKind.Break:
                case ItemKind.Spacer:
                    return true;
                case ItemKind.Space:
                    if (!IsCollapsible(it.Style)) return true;
                    break;
            }
        }
        return false;
    }

    private static bool IsCollapsible(ComputedStyle s) => s.WhiteSpace is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;

    // ------------------------------------------------------------------ flushing a run of inline content

    private float FlushInline(List<Node> run, Element container, float x, float y, float w, bool firstInBlock, out bool produced)
    {
        _inlineDepth++;
        try
        {
            return FlushInlineCore(run, container, x, y, w, firstInBlock, out produced);
        }
        finally
        {
            if (--_inlineDepth == 0) _itemPoolUsed = 0;
        }
    }

    private float FlushInlineCore(List<Node> run, Element container, float x, float y, float w, bool firstInBlock, out bool produced)
    {
        var items = CollectInline(run, w);
        if (!HasRenderable(items))
        {
            foreach (var it in items)
                if (it.Kind == ItemKind.Anchor) SetAnchor(it.AnchorId!, y);
            produced = false;
            return y;
        }
        var cs = container.Style;
        float indent = firstInBlock ? cs.TextIndent : 0;
        var lines = BreakLines(items, w, indent);
        produced = true;
        return PlaceLines(lines, cs, x, y, w, indent);
    }

    private static List<Line> BreakLines(List<Item> items, float availW, float firstIndent)
    {
        var lines = new List<Line>();
        var cur = new Line();
        float curW = 0;
        float avail = availW - firstIndent;
        int n = items.Count;

        void Finish(bool forced)
        {
            var li = cur.Items;
            // Trailing collapsible/breakable spaces hang and are not counted.
            while (li.Count > 0 && li[li.Count - 1].Kind == ItemKind.Space && (li[li.Count - 1].Breakable || IsCollapsible(li[li.Count - 1].Style)))
                li.RemoveAt(li.Count - 1);
            float width = 0;
            foreach (var it in li) width += it.Width;
            cur.Width = width;
            cur.Forced = forced;
            lines.Add(cur);
            cur = new Line();
            curW = 0;
            avail = availW;
        }

        bool HasContent()
        {
            foreach (var it in cur.Items)
                if (it.Kind != ItemKind.Anchor) return true;
            return false;
        }

        int i = 0;
        while (i < n)
        {
            var it = items[i];
            if (it.Kind == ItemKind.Break)
            {
                cur.Items.Add(it);
                Finish(forced: true);
                i++;
                continue;
            }
            if (it.Kind == ItemKind.Space && it.Breakable)
            {
                if (!HasContent() && IsCollapsible(it.Style)) { i++; continue; }
                cur.Items.Add(it);
                curW += it.Width;
                i++;
                continue;
            }

            int j = i;
            float gw = 0;
            while (j < n)
            {
                var g = items[j];
                if (g.Kind == ItemKind.Break || g.Kind == ItemKind.Space && g.Breakable) break;
                gw += g.Width;
                j++;
            }

            if (HasContent() && curW + gw > avail + Eps)
            {
                Finish(forced: false);
            }
            if (!HasContent() && gw > avail + Eps)
            {
                // A single unbreakable chunk wider than the line: break it between characters.
                for (int k = i; k < j; k++)
                {
                    var g = items[k];
                    while (true)
                    {
                        float room = avail - curW;
                        if (g.Width <= room + Eps || g.Kind == ItemKind.Anchor)
                        {
                            cur.Items.Add(g);
                            curW += g.Width;
                            break;
                        }
                        if (g.Kind == ItemKind.Text && g.Text.Length > 1)
                        {
                            var font = g.Style.Font;
                            float size = g.Style.FontSize, ls = g.Style.LetterSpacing;
                            int fit = 0;
                            float fw = 0;
                            while (fit < g.Text.Length)
                            {
                                float cw = font.CharWidth(g.Text[fit], size) + ls;
                                if (fw + cw > room + Eps) break;
                                fw += cw;
                                fit++;
                            }
                            if (fit == 0 && !HasContent()) { fit = 1; fw = font.CharWidth(g.Text[0], size) + ls; }
                            if (fit > 0)
                            {
                                cur.Items.Add(g.CloneWithText(g.Text.Substring(0, fit), fw));
                                var rest = g.Text.Substring(fit);
                                g = g.CloneWithText(rest, font.MeasureUnits(rest, 0, rest.Length) * size / 1000f + ls * rest.Length);
                            }
                            Finish(forced: false);
                            continue;
                        }
                        if (HasContent()) { Finish(forced: false); continue; }
                        cur.Items.Add(g); // overflows, nothing else we can do
                        curW += g.Width;
                        break;
                    }
                }
            }
            else
            {
                for (int k = i; k < j; k++) cur.Items.Add(items[k]);
                curW += gw;
            }
            i = j;
        }
        if (cur.Items.Count > 0) Finish(forced: true);
        else if (lines.Count > 0) lines[lines.Count - 1].Forced = true;
        return lines;
    }

    // ------------------------------------------------------------------ placing lines

    private static void TextMetrics(ComputedStyle s, out float a, out float d)
    {
        var f = s.Font;
        float size = s.FontSize;
        float asc = f.Ascent * size, desc = -f.Descent * size;
        float half = (s.LineHeight - (asc + desc)) / 2;
        a = asc + half;
        d = desc + half;
        float shift = BaselineShift(s);
        a -= shift;
        d += shift;
    }

    /// <summary>Downward baseline shift for sub/superscripts.</summary>
    private static float BaselineShift(ComputedStyle s)
        => s.VerticalAlign switch
        {
            VerticalAlign.Super => -0.42f * s.FontSize,
            VerticalAlign.Sub => 0.25f * s.FontSize,
            _ => 0,
        };

    private float PlaceLines(List<Line> lines, ComputedStyle block, float x, float y, float w, float indent)
    {
        TextMetrics(block, out float strutA, out float strutD);
        float[] xs = _xs;

        int n = lines.Count;
        if (_lineA.Length < n) { _lineA = new float[n * 2]; _lineD = new float[n * 2]; }
        float[] lineA = _lineA, lineD = _lineD;
        for (int li = 0; li < n; li++)
        {
            float maxA = strutA, maxD = strutD;
            bool visible = false;
            foreach (var it in lines[li].Items)
            {
                float a, d;
                switch (it.Kind)
                {
                    case ItemKind.Anchor:
                        continue;
                    case ItemKind.Image:
                        ImageMetrics(it, block, out a, out d);
                        break;
                    case ItemKind.Box:
                        a = it.BoxBaseline;
                        d = it.Height - it.BoxBaseline;
                        break;
                    default:
                        TextMetrics(it.Style, out a, out d);
                        break;
                }
                visible = true;
                if (a > maxA) maxA = a;
                if (d > maxD) maxD = d;
            }
            lineA[li] = visible ? maxA : float.NaN;
            lineD[li] = maxD;
        }
        bool[]? breakBefore = _paginate && n > 1 ? PlanBreaks(n, lineA, lineD, y, block.Orphans, block.Widows) : null;

        for (int li = 0; li < n; li++)
        {
            var line = lines[li];
            var items = line.Items;
            if (float.IsNaN(lineA[li]))
            {
                foreach (var it in items) SetAnchor(it.AnchorId!, y);
                continue;
            }
            float maxA = lineA[li];
            int spaces = 0;
            foreach (var it in items)
                if (it.Kind == ItemKind.Space) spaces++;

            float lineH = maxA + lineD[li];
            y = FitOnPage(y, lineH);
            if (breakBefore != null && breakBefore[li]) y = NextPage(y);
            float baseline = y + maxA;
            float lineIndent = li == 0 ? indent : 0;
            float free = w - lineIndent - line.Width;
            float lx = x + lineIndent;
            float extra = 0;
            switch (block.TextAlign)
            {
                case TextAlign.Right: if (free > 0) lx += free; break;
                case TextAlign.Center: if (free > 0) lx += free / 2; break;
                case TextAlign.Justify:
                    if (!line.Forced && spaces > 0 && free > 0) extra = free / spaces;
                    break;
            }

            // Item positions.
            if (xs.Length < items.Count + 1) _xs = xs = new float[items.Count * 2];
            float cx = lx;
            for (int k = 0; k < items.Count; k++)
            {
                xs[k] = cx;
                cx += items[k].Width + (items[k].Kind == ItemKind.Space ? extra : 0);
            }
            xs[items.Count] = cx;

            PaintInlineBackgrounds(items, xs, baseline);
            EmitLinks(items, xs, y, lineH);

            int i = 0;
            while (i < items.Count)
            {
                var it = items[i];
                switch (it.Kind)
                {
                    case ItemKind.Text:
                    case ItemKind.Space:
                    {
                        var style = it.Style;
                        int start = i;
                        var sb = _runText.Clear();
                        while (i < items.Count)
                        {
                            var r = items[i];
                            if (r.Kind is not (ItemKind.Text or ItemKind.Space) || !ReferenceEquals(r.Style, style)) break;
                            sb.Append(r.Source, r.Start, r.Length);
                            i++;
                        }
                        float runX = xs[start], runEnd = xs[i];
                        if (style.Visible)
                        {
                            float bl = baseline + BaselineShift(style);
                            _ops.Add(new TextOp(runX, bl, y, style.Font, style.FontSize, style.Color, sb.ToString(),
                                style.WordSpacing + extra, style.LetterSpacing));
                            if (style.TextDecoration != TextDecoration.None) Decorate(style, runX, runEnd - runX, bl);
                        }
                        continue;
                    }
                    case ItemKind.Image:
                        if (it.Image != null)
                        {
                            ImageMetrics(it, block, out float ia, out _);
                            _ops.Add(new ImageOp(xs[i], baseline - ia, it.Width, it.Height, it.Image));
                        }
                        break;
                    case ItemKind.Box:
                    {
                        float dx = xs[i], dy = baseline - it.BoxBaseline;
                        foreach (var op in it.BoxOps!)
                        {
                            op.Offset(dx, dy);
                            _ops.Add(op);
                        }
                        break;
                    }
                    case ItemKind.Anchor:
                        SetAnchor(it.AnchorId!, y);
                        break;
                }
                i++;
            }

            _lastBaseline = baseline;
            if (_marker != null) EmitMarker(baseline, y);
            y += lineH;
        }
        return y;
    }

    /// <summary>
    /// Chooses the lines of a paragraph that start a new page so that at least <paramref name="orphans"/> lines stay at the
    /// bottom of a page and at least <paramref name="widows"/> lines go to the top of the next. Returns null when the
    /// natural breaks already satisfy both.
    /// </summary>
    private bool[]? PlanBreaks(int n, float[] lineA, float[] lineD, float top, int orphans, int widows)
    {
        if (orphans <= 1 && widows <= 1) return null;
        int visibleCount = 0;
        float total = 0;
        for (int li = 0; li < n; li++)
            if (!float.IsNaN(lineA[li])) { visibleCount++; total += lineA[li] + lineD[li]; }
        if (visibleCount < 2 || !CrossesPage(top, top + total)) return null;

        bool[]? forced = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float y = top;
            int chunkStart = -1, chunkLines = 0, seen = 0, chunkPage = 0;
            bool changed = false;
            for (int li = 0; li < n && !changed; li++)
            {
                if (float.IsNaN(lineA[li])) continue;
                float h = lineA[li] + lineD[li];
                float fy = FitOnPage(y, h);
                if (forced != null && forced[li]) fy = NextPage(fy);
                if (chunkStart >= 0 && PageOf(fy) > chunkPage)
                {
                    // A page break before line li: chunkLines lines stay behind, visibleCount - seen lines move on.
                    // Only the paragraph's first chunk can move as a whole: later chunks already start at a page top.
                    bool canMoveChunk = chunkLines == seen && !AtPageTop(top);
                    int after = visibleCount - seen;
                    if (chunkLines < orphans && canMoveChunk)
                    {
                        (forced ??= new bool[n])[chunkStart] = true;
                        changed = true;
                    }
                    else if (after < widows)
                    {
                        int need = widows - after, keep = chunkLines - need;
                        if (keep >= orphans || keep <= 0 && canMoveChunk)
                        {
                            int target = keep <= 0 ? chunkStart : NthVisible(lineA, n, chunkStart, keep);
                            if (target >= 0 && !(forced?[target] ?? false))
                            {
                                (forced ??= new bool[n])[target] = true;
                                changed = true;
                            }
                        }
                    }
                    chunkStart = -1;
                }
                if (chunkStart < 0) { chunkStart = li; chunkLines = 0; chunkPage = PageOf(fy); }
                chunkLines++;
                seen++;
                y = fy + h;
            }
            if (!changed) break;
        }
        return forced;
    }

    /// <summary>The index of the visible line <paramref name="k"/> lines after visible line <paramref name="from"/>.</summary>
    private static int NthVisible(float[] lineA, int n, int from, int k)
    {
        for (int li = from + 1; li < n; li++)
            if (!float.IsNaN(lineA[li]) && --k == 0) return li;
        return -1;
    }

    private static void ImageMetrics(Item it, ComputedStyle block, out float a, out float d)
    {
        switch (it.Style.VerticalAlign)
        {
            case VerticalAlign.Middle:
                a = it.Height / 2 + block.FontSize * 0.25f;
                d = it.Height - a;
                break;
            case VerticalAlign.Top:
            case VerticalAlign.TextTop:
                TextMetrics(block, out a, out _);
                d = it.Height - a;
                if (d < 0) { a = it.Height; d = 0; }
                break;
            default:
                a = it.Height;
                d = 0;
                break;
        }
    }

    private void PaintInlineBackgrounds(List<Item> items, float[] xs, float baseline)
    {
        int i = 0;
        while (i < items.Count)
        {
            var deco = items[i].Deco;
            if (deco == null) { i++; continue; }
            int start = i;
            while (i < items.Count && ReferenceEquals(items[i].Deco, deco)) i++;
            var f = deco.Font;
            float size = deco.FontSize;
            float top = baseline + BaselineShift(deco) - f.Ascent * size - deco.PaddingTop.Resolve(0);
            float h = (f.Ascent - f.Descent) * size + deco.PaddingTop.Resolve(0) + deco.PaddingBottom.Resolve(0);
            _ops.Add(new RectOp(xs[start], top, xs[i] - xs[start], h, deco.BackgroundColor));
        }
    }

    private void EmitLinks(List<Item> items, float[] xs, float lineTop, float lineH)
    {
        int i = 0;
        while (i < items.Count)
        {
            var href = items[i].Href;
            if (href == null) { i++; continue; }
            int start = i;
            while (i < items.Count && items[i].Href == href) i++;
            _ops.Add(new LinkOp(xs[start], lineTop, xs[i] - xs[start], lineH, href));
        }
    }

    private void Decorate(ComputedStyle s, float x, float w, float baseline)
    {
        float size = s.FontSize;
        float thickness = Math.Max(size * 0.055f, 0.5f);
        if ((s.TextDecoration & TextDecoration.Underline) != 0)
            _ops.Add(new RectOp(x, baseline + size * 0.1f, w, thickness, s.Color));
        if ((s.TextDecoration & TextDecoration.LineThrough) != 0)
            _ops.Add(new RectOp(x, baseline - size * 0.3f, w, thickness, s.Color));
        if ((s.TextDecoration & TextDecoration.Overline) != 0)
            _ops.Add(new RectOp(x, baseline - s.Font.Ascent * size, w, thickness, s.Color));
    }
}

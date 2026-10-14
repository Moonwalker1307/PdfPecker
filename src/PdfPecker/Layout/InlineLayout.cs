using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;
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
    private ContentSlot? _textTag; // the structure slot of the text node being collected, for a tagged PDF

    // A paragraph's collection state, lines and inline runs are reused once it has been placed or measured. Each is
    // handed back explicitly, so nested layout (an inline-block's own paragraphs) takes fresh ones from the pool.
    private readonly Stack<CollectState> _statePool = new();
    private readonly Stack<Line> _linePool = new();
    private readonly Stack<List<Line>> _lineListPool = new();
    private readonly Stack<List<Node>> _runPool = new();

    private List<Node> RentRun() => _runPool.Count > 0 ? _runPool.Pop() : new List<Node>();

    private void ReturnRun(List<Node>? run)
    {
        if (run == null) return;
        run.Clear();
        _runPool.Push(run);
    }

    private void ReturnState(CollectState st)
    {
        st.Items.Clear();
        _statePool.Push(st);
    }

    private Line RentLine()
    {
        if (!_linePool.TryPop(out var line)) return new Line();
        line.Width = 0;
        line.Forced = false;
        line.Top = float.NaN;
        line.Left = 0;
        line.Avail = float.NaN;
        return line;
    }

    private void ReturnLines(List<Line> lines)
    {
        foreach (var line in lines)
        {
            line.Items.Clear();
            _linePool.Push(line);
        }
        lines.Clear();
        _lineListPool.Push(lines);
    }

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
        it.Tag = _textTag;
        return it;
    }

    private enum ItemKind : byte { Text, Space, Break, Image, Box, Spacer, Anchor, Float, Absolute }

    private enum BreakAfter : byte { None, Break, SoftHyphen }

    private sealed class Item
    {
        public ItemKind Kind;
        public ComputedStyle Style = null!;
        // Text is a slice of Source, so words don't need their own strings until a line is written.
        public string Source = "";
        public int Start, Length;
        public float Width;
        public bool Breakable;
        public BreakAfter BreakAfter; // a line break opportunity right after this text (hyphen, soft hyphen, ...)
        public string? Href;
        public ComputedStyle? Deco;   // nearest inline ancestor that paints a background
        public PdfImage? Image;
        public float Height;          // image or inline-block height
        public float BoxBaseline;     // inline-block: distance from its top to its baseline
        public List<DrawOp>? BoxOps;
        public Captured? Capture;     // an inline-block laid out on its own, placed with its anchors and positioned boxes
        public string? AnchorId;
        public Element? Element;      // a float or an absolutely positioned box
        public float FloatMin;        // a float's min-content width, when measuring
        public float MinWidth = -1;   // an inline-block's min-content contribution, when measuring (-1: Width)
        public float RelX, RelY;      // offset of position: relative inline ancestors
        public ContentSlot? Tag;      // tagged PDF: the structure slot of the text or image

        public void Reset()
        {
            Source = "";
            Start = Length = 0;
            Width = Height = BoxBaseline = 0;
            Breakable = false;
            BreakAfter = BreakAfter.None;
            Href = null;
            Deco = null;
            Image = null;
            BoxOps = null;
            Capture = null;
            AnchorId = null;
            Element = null;
            FloatMin = RelX = RelY = 0;
            MinWidth = -1;
        }

        public string Text => Start == 0 && Length == Source.Length ? Source : Source.Substring(Start, Length);

        /// <summary>A copy showing <paramref name="length"/> characters of this item's text from <paramref name="offset"/>.</summary>
        public Item Slice(int offset, int length, float width)
        {
            var c = (Item)MemberwiseClone();
            c.Start = Start + offset;
            c.Length = length;
            c.Width = width;
            return c;
        }

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
        // Beside floats: the line's top and its room (left offset and width); NaN when the line simply follows the last.
        public float Top = float.NaN, Left, Avail = float.NaN;
    }

    // ------------------------------------------------------------------ collection

    private sealed class CollectState
    {
        public readonly List<Item> Items = new();
        public bool LastWasSpace = true;
        public int Column;
        public float AvailWidth;
    }

    /// <summary>The items of a run of inline content. Hand the state back with <see cref="ReturnState"/> when done.</summary>
    private CollectState CollectInline(List<Node> run, float availW)
    {
        if (_statePool.TryPop(out var st))
        {
            st.LastWasSpace = true;
            st.Column = 0;
        }
        else st = new CollectState();
        st.AvailWidth = availW;
        foreach (var node in run) CollectNode(st, node, null, null);
        return st;
    }

    /// <summary>
    /// Adds an SVG drawing (an inline svg element, or an SVG file in an img element) as a small inline box, so padding
    /// and borders work as for any replaced element.
    /// </summary>
    private void AddInlineSvg(CollectState st, Element e, ComputedStyle s, SvgImage? svg, float sw, float sh, string? href)
    {
        if (sw > 0 && sh > 0)
        {
            float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0);
            float pl = s.PaddingLeft.Resolve(0) + s.BorderLeft, pr = s.PaddingRight.Resolve(0) + s.BorderRight;
            float ptop = s.PaddingTop.Resolve(0) + s.BorderTop, pbot = s.PaddingBottom.Resolve(0) + s.BorderBottom;
            var ops = new List<DrawOp>();
            var group = new GroupOp();
            ops.Add(group);
            BoxPainter.Paint(group, s, 0, 0, sw + pl + pr, sh + ptop + pbot, _images);
            if (s.Visible && svg != null) ops.Add(new SvgOp(pl, ptop, sw, sh, svg) { Tag = Tags?.Of(e) });
            if (ml > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = ml; it_.Href = href; st.Items.Add(it_); }
            float boxH = sh + ptop + pbot;
            st.Items.Add(new Item { Kind = ItemKind.Box, Style = s, Width = sw + pl + pr, Height = boxH, BoxBaseline = boxH, BoxOps = ops, Href = href ?? LinkOf(e) });
            if (mr > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = mr; it_.Href = href; st.Items.Add(it_); }
            st.LastWasSpace = false;
        }
    }

    private void CollectNode(CollectState st, Node node, string? href, ComputedStyle? deco)
    {
        if (node is TextNode t)
        {
            if (Tags != null) _textTag = Tags.Of(t);
            AddText(st, t.Text, t.Parent!.Style, href, deco);
            _textTag = null;
            return;
        }
        var e = (Element)node;
        var s = e.Style;
        if (s == null || s.Display == Display.None || e.Detached) return;

        if (s.Floated || s.OutOfFlow)
        {
            var oof = Rent(s.Floated ? ItemKind.Float : ItemKind.Absolute, s);
            oof.Element = e;
            if (_measuring && s.Floated)
            {
                // Measuring: a float takes its width on the line, as it would beside the text.
                var (fmin, fmax) = Intrinsic(e);
                float margins = (s.MarginLeft.IsAuto || s.MarginLeft.IsPercent ? 0 : s.MarginLeft.Value)
                                + (s.MarginRight.IsAuto || s.MarginRight.IsPercent ? 0 : s.MarginRight.Value);
                oof.Width = fmax + margins;
                oof.FloatMin = fmin + margins;
            }
            st.Items.Add(oof);
            return;
        }

        if (e.Id != null) { var it_ = Rent(ItemKind.Anchor, s); it_.AnchorId = e.Id; st.Items.Add(it_); }
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) { var it_ = Rent(ItemKind.Anchor, s); it_.AnchorId = aname; st.Items.Add(it_); }

        if ((s.Layered || (s.BorderRadii != null || s.ObjectFit != ObjectFit.Fill || s.OutlineStyle != BorderStyle.None) && e.Name == "img") && e.Name is "img" or "svg")
        {
            // A transformed, translucent, relatively positioned, rounded or object-fit image is laid out as a box of its
            // own, which carries the layer or fits and clips the image.
            st.Items.Add(LayoutInlineBlock(e, st.AvailWidth, href ?? LinkOf(e)));
            st.LastWasSpace = false;
            return;
        }

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
                AddInlineSvg(st, e, s, svg, sw, sh, href);
                return;
            }
            case "img":
            {
                var img = ImageSize(e, st.AvailWidth, out float w, out float h, out var svgFile);
                if (svgFile != null)
                {
                    AddInlineSvg(st, e, s, svgFile, w, h, href);
                    return;
                }
                if (w > 0 && h > 0)
                {
                    float ml = s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0), mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0);
                    if (ml > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = ml; it_.Href = href; st.Items.Add(it_); }
                    { var it_ = Rent(ItemKind.Image, s); it_.Image = s.Visible ? img : null; it_.Width = w; it_.Height = h; it_.Href = href ?? LinkOf(e); it_.Tag = Tags?.Of(e); st.Items.Add(it_); }
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

        if (CssColor.IsVisible(s.BackgroundColor) || s.BoxShadows != null || s.OutlineStyle != BorderStyle.None) deco = s;
        float left = s.PaddingLeft.Resolve(0) + s.BorderLeft + (s.MarginLeft.IsAuto ? 0 : s.MarginLeft.Resolve(0));
        float right = s.PaddingRight.Resolve(0) + s.BorderRight + (s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(0));
        int first = st.Items.Count;
        if (left > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = left; it_.Href = href; it_.Deco = deco; st.Items.Add(it_); }
        foreach (var c in e.Children) CollectNode(st, c, href, deco);
        if (right > 0) { var it_ = Rent(ItemKind.Spacer, s); it_.Width = right; it_.Href = href; it_.Deco = deco; st.Items.Add(it_); }
        if (s.Position == Position.Relative)
        {
            // An inline box shifted by position: relative; percentages of the containing block's height are ignored.
            float dx = !s.Left.IsAuto ? (s.Left.IsPercent ? 0 : s.Left.Value) : !s.Right.IsAuto ? -(s.Right.IsPercent ? 0 : s.Right.Value) : 0;
            float dy = !s.Top.IsAuto ? (s.Top.IsPercent ? 0 : s.Top.Value) : !s.Bottom.IsAuto ? -(s.Bottom.IsPercent ? 0 : s.Bottom.Value) : 0;
            for (int k = first; k < st.Items.Count; k++) { st.Items[k].RelX += dx; st.Items[k].RelY += dy; }
        }
    }

    private static string? LinkOf(Element e)
    {
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "a" && p.GetAttribute("href") is { Length: > 0 } h) return h;
        return null;
    }

    private Item LayoutInlineBlock(Element e, float availW, string? href)
    {
        var c = CaptureBox(e, availW, shrinkToFit: true);
        float baseline = float.IsNaN(c.Baseline) ? c.Height : c.Baseline;
        var item = new Item { Kind = ItemKind.Box, Style = e.Style, Width = c.Width, Height = c.Height, BoxBaseline = baseline, Capture = c, Href = href };
        if (_measuring)
        {
            // An inline-block can wrap its own content, so its min-content contribution is its min-content width,
            // not the width it takes when laid out at the (very wide) measuring width.
            var s = e.Style;
            float margins = (s.MarginLeft.IsAuto || s.MarginLeft.IsPercent ? 0 : s.MarginLeft.Value)
                            + (s.MarginRight.IsAuto || s.MarginRight.IsPercent ? 0 : s.MarginRight.Value);
            item.MinWidth = Math.Min(c.Width, Intrinsic(e).Min + margins);
        }
        return item;
    }

    private const string TabSpaces = "        ";

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
                // A tab is one item covering its spaces up to the next tab stop, not one item per space.
                int count = c == '\t' ? 8 - st.Column % 8 : 1;
                { var it_ = Rent(ItemKind.Space, style); it_.Source = TabSpaces; it_.Length = count; it_.Width = spaceW * count; it_.Breakable = breakable; it_.Href = href; it_.Deco = deco; items.Add(it_); }
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
            var span = text.AsSpan(start, i - start);
            if (span.IndexOfAny(WordBreakChars) >= 0 && breakable) AddWordSegments(st, text, start, i - start, style, href, deco);
            else AddWord(st, text, start, i - start, style, href, deco, BreakAfter.None);
            st.Column += i - start;
        }
    }

    // Characters inside a word that give a line break opportunity (or, for U+200B and U+00AD, are not drawn).
    private static readonly System.Buffers.SearchValues<char> WordBreakChars = System.Buffers.SearchValues.Create("-?\u00AD\u200B\u2010\u2013\u2014");

    /// <summary>
    /// Splits a word at its break opportunities as Chrome does: after a hyphen followed by a letter, digit or hyphen,
    /// after U+2010 and en dashes, around em dashes, after "?" before a letter, at zero-width spaces and at soft hyphens
    /// (which show a hyphen only when the line breaks there).
    /// </summary>
    private void AddWordSegments(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco)
    {
        int end = start + length, seg = start;
        for (int k = start; k < end; k++)
        {
            char c = text[k];
            char next = k + 1 < end ? text[k + 1] : '\0';
            switch (c)
            {
                case '\u00AD':
                case '\u200B':
                    AddWord(st, text, seg, k - seg, style, href, deco, c == '\u00AD' ? BreakAfter.SoftHyphen : BreakAfter.Break);
                    seg = k + 1;
                    break;
                case '-':
                    if (char.IsLetterOrDigit(next) || next == '-') { AddWord(st, text, seg, k + 1 - seg, style, href, deco, BreakAfter.Break); seg = k + 1; }
                    break;
                case '?':
                    if (char.IsLetter(next)) { AddWord(st, text, seg, k + 1 - seg, style, href, deco, BreakAfter.Break); seg = k + 1; }
                    break;
                case '\u2010':
                case '\u2013':
                    if (next != '\0') { AddWord(st, text, seg, k + 1 - seg, style, href, deco, BreakAfter.Break); seg = k + 1; }
                    break;
                case '\u2014':
                    if (k > seg) AddWord(st, text, seg, k - seg, style, href, deco, BreakAfter.Break);
                    seg = k;
                    if (next != '\0') { AddWord(st, text, seg, 1, style, href, deco, BreakAfter.Break); seg = k + 1; }
                    break;
            }
        }
        if (seg < end) AddWord(st, text, seg, end - seg, style, href, deco, BreakAfter.None);
        else if (st.Items.Count > 0 && st.Items[^1] is { Kind: ItemKind.Text } last && last.BreakAfter == BreakAfter.SoftHyphen)
            last.BreakAfter = BreakAfter.None; // a soft hyphen at the end of a word is no break opportunity of its own
    }

    private void AddWord(CollectState st, string text, int start, int length, ComputedStyle style, string? href, ComputedStyle? deco, BreakAfter breakAfter)
    {
        string src = text;
        int wStart = start, wLen = length;
        var span = text.AsSpan(start, length);
        if (span.IndexOf('\u00AD') >= 0 || span.IndexOf('\u200B') >= 0 || style.TextTransform != TextTransform.None)
        {
            string word = span.ToString().Replace("\u00AD", "").Replace("\u200B", "");
            switch (style.TextTransform)
            {
                case TextTransform.Uppercase: word = word.ToUpperInvariant(); break;
                case TextTransform.Lowercase: word = word.ToLowerInvariant(); break;
                case TextTransform.Capitalize:
                    if (word.Length > 0 && st.LastWasSpace && char.IsLower(word[0])) word = char.ToUpperInvariant(word[0]) + word.Substring(1);
                    break;
            }
            src = word;
            wStart = 0;
            wLen = word.Length;
        }
        if (wLen == 0)
        {
            // An empty segment (two break characters in a row) still marks the opportunity on the text before it.
            if (breakAfter != BreakAfter.None && st.Items.Count > 0 && st.Items[^1] is { Kind: ItemKind.Text, BreakAfter: BreakAfter.None } prev) prev.BreakAfter = breakAfter;
            return;
        }
        var font = style.Font;
        float size = style.FontSize;
        float w = font.MeasureUnits(src, wStart, wLen) * size / 1000f + style.LetterSpacing * wLen;
        var it_ = Rent(ItemKind.Text, style);
        it_.Source = src; it_.Start = wStart; it_.Length = wLen; it_.Width = w; it_.Href = href; it_.Deco = deco; it_.BreakAfter = breakAfter;
        st.Items.Add(it_);
        st.LastWasSpace = false;
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
                case ItemKind.Float:
                case ItemKind.Absolute:
                    break;
                case ItemKind.Space:
                    if (!IsCollapsible(it.Style)) return true;
                    break;
            }
        }
        return false;
    }

    private static bool IsCollapsible(ComputedStyle s) => s.WhiteSpace is WhiteSpace.Normal or WhiteSpace.NoWrap or WhiteSpace.PreLine;

    // ------------------------------------------------------------------ flushing a run of inline content

    private float[] _kern = new float[16];

    /// <summary>
    /// The kerning between neighbouring words and spaces that are drawn as one run of text (same style), which the
    /// items' own widths leave out: kern[k] is added after item k. Pairs after the last word of the line are not
    /// counted, as nothing visible follows them. Returns the total.
    /// </summary>
    private static float KernBetweenItems(List<Item> items, float[] kern)
    {
        int lastText = -1;
        for (int k = 0; k < items.Count; k++)
        {
            kern[k] = 0;
            if (items[k].Kind == ItemKind.Text) lastText = k;
        }
        float total = 0;
        for (int k = 0; k < lastText; k++)
        {
            Item a = items[k], b = items[k + 1];
            if (a.Kind is not (ItemKind.Text or ItemKind.Space) || b.Kind is not (ItemKind.Text or ItemKind.Space)) continue;
            if (!ReferenceEquals(a.Style, b.Style) || a.Length == 0 || b.Length == 0) continue;
            var font = a.Style.Font;
            if (!font.HasKerning) continue;
            int units = font.KernUnits(a.Source[a.Start + a.Length - 1], b.Source[b.Start]);
            if (units == 0) continue;
            kern[k] = units * a.Style.FontSize / 1000f;
            total += kern[k];
        }
        return total;
    }

    private float FlushInline(List<Node> run, Element container, float x, float y, float w, bool firstInBlock, out bool produced)
    {
        Cancellation.ThrowIfCancellationRequested();
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
        var st = CollectInline(run, w);
        var items = st.Items;
        if (!HasRenderable(items))
        {
            foreach (var it in items)
            {
                if (it.Kind == ItemKind.Anchor) SetAnchor(it.AnchorId!, y);
                else if (it.Kind == ItemKind.Float) PlaceFloat(MeasureFloat(it.Element!, w), y, x, w);
                else if (it.Kind == ItemKind.Absolute) QueueAbsolute(it.Element!, x, y);
            }
            ReturnState(st);
            produced = false;
            return y;
        }
        var cs = container.Style;
        float indent = firstInBlock ? cs.TextIndent : 0;
        FloatLines? fl = null;
        if (FloatsBelow(y) || items.Exists(it => it.Kind == ItemKind.Float))
        {
            TextMetrics(cs, out float sa, out float sd);
            fl = new FloatLines { Engine = this, Block = cs, X = x, W = w, Y = y, StrutH = sa + sd };
        }
        var lines = BreakLines(items, w, indent, fl);
        fl?.Flush(lines);
        produced = true;
        float bottom = PlaceLines(lines, cs, x, y, w, indent, besideFloats: fl != null);
        ReturnLines(lines);
        ReturnState(st);
        return bottom;
    }

    private List<Line> BreakLines(List<Item> items, float availW, float firstIndent, FloatLines? fl = null)
    {
        var lines = _lineListPool.Count > 0 ? _lineListPool.Pop() : new List<Line>();
        var cur = RentLine();
        // Whether the current line holds anything but anchors, kept up to date so checking it costs nothing.
        bool hasContent = false;
        float curW = 0;
        fl?.Begin(lines);
        float avail = (fl?.Avail ?? availW) - firstIndent;
        int n = items.Count;

        void Finish(bool forced)
        {
            var li = cur.Items;
            // Trailing collapsible/breakable spaces hang and are not counted.
            while (li.Count > 0 && li[li.Count - 1].Kind == ItemKind.Space && (li[li.Count - 1].Breakable || IsCollapsible(li[li.Count - 1].Style)))
                li.RemoveAt(li.Count - 1);
            // A line broken at a soft hyphen shows a hyphen.
            if (!forced && li.Count > 0 && li[^1] is { Kind: ItemKind.Text, BreakAfter: BreakAfter.SoftHyphen } shy)
                li[^1] = shy.CloneWithText(shy.Text + "-", shy.Width + HyphenWidth(shy.Style));
            float width = 0;
            foreach (var it in li) width += it.Width;
            cur.Width = width;
            cur.Forced = forced;
            fl?.End(cur);
            lines.Add(cur);
            cur = RentLine();
            hasContent = false;
            curW = 0;
            fl?.Begin(lines);
            avail = fl?.Avail ?? availW;
        }

        bool HasContent() => hasContent;

        void Add(Item it)
        {
            cur.Items.Add(it);
            if (it.Kind is not (ItemKind.Anchor or ItemKind.Absolute)) hasContent = true;
        }

        int i = 0;
        while (i < n)
        {
            var it = items[i];
            if (it.Kind == ItemKind.Float)
            {
                if (fl != null)
                {
                    fl.Float(it, curW, HasContent());
                    avail = fl.Avail - (lines.Count == 0 ? firstIndent : 0);
                }
                i++;
                continue;
            }
            if (it.Kind == ItemKind.Break)
            {
                Add(it);
                Finish(forced: true);
                i++;
                continue;
            }
            if (it.Kind == ItemKind.Space && it.Breakable)
            {
                if (!HasContent() && IsCollapsible(it.Style)) { i++; continue; }
                Add(it);
                curW += it.Width;
                i++;
                continue;
            }

            int j = i;
            float gw = 0;
            bool splittable = false;
            while (j < n)
            {
                var g = items[j];
                if (g.Kind == ItemKind.Break || g.Kind == ItemKind.Space && g.Breakable) break;
                gw += g.Width;
                if (g.Kind == ItemKind.Text && BreaksAnywhere(g.Style)) splittable = true;
                j++;
                if (g.BreakAfter != BreakAfter.None) break;
            }
            // Room for the hyphen a soft hyphen shows if the line breaks after this chunk.
            float need = gw;
            if (j > i && items[j - 1].BreakAfter == BreakAfter.SoftHyphen) need += HyphenWidth(items[j - 1].Style);
            // word-break: break-all breaks the chunk at the end of the line instead of moving it to the next.
            bool breakAll = splittable && items[i].Kind == ItemKind.Text && items[i].Style.WordBreak == WordBreak.BreakAll;

            if (HasContent() && curW + need > avail + Eps && !breakAll)
            {
                Finish(forced: false);
            }
            // Too narrow beside the floats: the line moves down past them until the chunk fits.
            if (fl != null && !HasContent() && need > avail + Eps)
                while (fl.Lower())
                {
                    avail = fl.Avail - (lines.Count == 0 ? firstIndent : 0);
                    if (need <= avail + Eps) break;
                }
            if (splittable && curW + gw > avail + Eps)
            {
                // A chunk wider than the line that overflow-wrap or word-break lets break between characters.
                for (int k = i; k < j; k++)
                {
                    var g = items[k];
                    while (true)
                    {
                        float room = avail - curW;
                        if (g.Width <= room + Eps || g.Kind == ItemKind.Anchor)
                        {
                            Add(g);
                            curW += g.Width;
                            break;
                        }
                        if (g.Kind == ItemKind.Text && g.Length > 1 && BreaksAnywhere(g.Style))
                        {
                            // Work on the item's slice of its source text: copying the rest of a long word for every
                            // line would make breaking it quadratic.
                            var font = g.Style.Font;
                            float size = g.Style.FontSize, ls = g.Style.LetterSpacing;
                            string src = g.Source;
                            int start = g.Start, len = g.Length;
                            int fit = 0;
                            float fw = 0;
                            while (fit < len)
                            {
                                float cw = font.CharWidth(src[start + fit], size) + ls;
                                if (fw + cw > room + Eps) break;
                                fw += cw;
                                fit++;
                            }
                            if (fit == 0 && !HasContent()) { fit = 1; fw = font.CharWidth(src[start], size) + ls; }
                            if (fit > 0)
                            {
                                var head = g.Slice(0, fit, fw);
                                head.BreakAfter = BreakAfter.None;
                                Add(head);
                                // The rest is measured exactly once it fits on a line; until then its width only needs
                                // to exceed the room, so subtracting is enough.
                                float restW = g.Width - fw;
                                int restLen = len - fit;
                                if (restW <= availW + Eps) restW = font.MeasureUnits(src, start + fit, restLen) * size / 1000f + ls * restLen;
                                g = g.Slice(fit, restLen, restW);
                            }
                            Finish(forced: false);
                            continue;
                        }
                        if (HasContent()) { Finish(forced: false); continue; }
                        Add(g); // overflows, nothing else we can do
                        curW += g.Width;
                        break;
                    }
                }
            }
            else
            {
                // Fits, or may not break: a word too wide for the line overflows it, as in browsers.
                for (int k = i; k < j; k++) Add(items[k]);
                curW += gw;
            }
            i = j;
        }
        if (cur.Items.Count > 0) Finish(forced: true);
        else if (lines.Count > 0) lines[lines.Count - 1].Forced = true;
        cur.Items.Clear();
        _linePool.Push(cur); // the empty line Finish started
        return lines;
    }

    /// <summary>Whether overflow-wrap or word-break lets a word break between any two characters.</summary>
    private static bool BreaksAnywhere(ComputedStyle s) => s.OverflowWrap != OverflowWrap.Normal || s.WordBreak == WordBreak.BreakAll;

    private static float HyphenWidth(ComputedStyle s) => s.Font.CharWidth('-', s.FontSize) + s.LetterSpacing;

    // ------------------------------------------------------------------ placing lines

    private static void TextMetrics(ComputedStyle s, out float a, out float d)
    {
        s.Font.LineMetrics(s.FontSize, out float asc, out float desc);
        float lineH = s.LineHeight;
        // Chrome gives the top half of the leading whole CSS pixels and the rest to the bottom.
        float half = MathF.Floor((lineH - (asc + desc)) / 2 / 0.75f + 0.001f) * 0.75f;
        a = asc + half;
        d = lineH - a;
        float shift = BaselineShift(s);
        a -= shift;
        d += shift;
    }

    /// <summary>
    /// Downward baseline shift for sub/superscripts, as Chrome computes it from the parent's font size in whole pixels:
    /// up by a third plus one pixel, down by a fifth plus one pixel.
    /// </summary>
    private static float BaselineShift(ComputedStyle s)
    {
        if (s.VerticalAlign is not (VerticalAlign.Super or VerticalAlign.Sub)) return 0;
        int px = (int)MathF.Round(s.ParentFontSize / 0.75f);
        return s.VerticalAlign == VerticalAlign.Super ? -(px / 3 + 1) * 0.75f : (px / 5 + 1) * 0.75f;
    }

    private float PlaceLines(List<Line> lines, ComputedStyle block, float x, float y, float w, float indent, bool besideFloats = false)
    {
        float[] xs = _xs;

        int n = lines.Count;
        if (_lineA.Length < n) { _lineA = new float[n * 2]; _lineD = new float[n * 2]; }
        float[] lineA = _lineA, lineD = _lineD;
        for (int li = 0; li < n; li++) LineExtent(lines[li], block, out lineA[li], out lineD[li]);
        // Beside floats the lines' positions were fixed while breaking them.
        bool[]? breakBefore = _paginate && n > 1 && !besideFloats ? PlanBreaks(n, lineA, lineD, y, block.Orphans, block.Widows) : null;

        for (int li = 0; li < n; li++)
        {
            var line = lines[li];
            var items = line.Items;
            if (!float.IsNaN(line.Top) && line.Top > y) y = line.Top;
            float lineLeft = float.IsNaN(line.Avail) ? 0 : line.Left, lineW = float.IsNaN(line.Avail) ? w : line.Avail;
            if (float.IsNaN(lineA[li]))
            {
                foreach (var it in items)
                {
                    if (it.Kind == ItemKind.Absolute) QueueAbsolute(it.Element!, x + lineLeft, y);
                    else SetAnchor(it.AnchorId!, y);
                }
                continue;
            }
            float maxA = lineA[li];
            int spaces = 0;
            foreach (var it in items)
                if (it.Kind == ItemKind.Space) spaces++;

            float lineH = maxA + lineD[li];
            y = FitOnPage(y, lineH);
            CheckPageLimit(y);
            if (breakBefore != null && breakBefore[li]) y = NextPage(y);
            float baseline = y + maxA;
            float lineIndent = li == 0 ? indent : 0;
            if (_kern.Length < items.Count) _kern = new float[items.Count * 2];
            float lineKern = KernBetweenItems(items, _kern);
            float free = lineW - lineIndent - line.Width - lineKern;
            float lx = x + lineLeft + lineIndent;
            float extra = 0;
            switch (block.TextAlign)
            {
                case TextAlign.Right: if (free > 0) lx += free; break;
                case TextAlign.Center: if (free > 0) lx += free / 2; break;
                case TextAlign.Justify:
                    if (!line.Forced && spaces > 0 && free > 0) extra = free / spaces;
                    break;
            }

            if (block.TextOverflowEllipsis && block.ClipsX && !_measuring && line.Width + lineKern > lineW - lineIndent + Eps)
            {
                Ellipsize(line, block, lineW - lineIndent);
                spaces = 0;
                foreach (var it in items)
                    if (it.Kind == ItemKind.Space) spaces++;
                if (_kern.Length < items.Count) _kern = new float[items.Count * 2];
                lineKern = KernBetweenItems(items, _kern);
                free = lineW - lineIndent - line.Width - lineKern;
                lx = x + lineLeft + lineIndent;
                extra = 0;
                // The cut line fills the box, so alignment no longer moves it.
            }

            // Item positions.
            if (xs.Length < items.Count + 1) _xs = xs = new float[items.Count * 2];
            float cx = lx;
            for (int k = 0; k < items.Count; k++)
            {
                xs[k] = cx;
                cx += items[k].Width + _kern[k] + (items[k].Kind == ItemKind.Space ? extra : 0);
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
                            if (r.Kind is not (ItemKind.Text or ItemKind.Space) || !ReferenceEquals(r.Style, style) || r.Tag != it.Tag) break;
                            sb.Append(r.Source, r.Start, r.Length);
                            i++;
                        }
                        float rx = items[start].RelX;
                        float runX = xs[start] + rx, runEnd = xs[i] + rx;
                        if (style.Visible)
                        {
                            float bl = baseline + BaselineShift(style) + items[start].RelY;
                            if (style.TextShadows != null) TextShadows(style, runX, bl, y, sb.ToString(), style.WordSpacing + extra);
                            _ops.Add(new TextOp(runX, bl, y, style.Font, style.FontSize, CssColor.Fade(style.Color, style.InlineFade), sb.ToString(),
                                style.WordSpacing + extra, style.LetterSpacing) { Tag = it.Tag });
                            if (style.TextDecoration != TextDecoration.None) Decorate(style, runX, runEnd - runX, bl);
                        }
                        continue;
                    }
                    case ItemKind.Image:
                        if (it.Image != null)
                        {
                            ImageMetrics(it, block, out float ia, out _);
                            _ops.Add(new ImageOp(xs[i] + it.RelX, baseline - ia + it.RelY, it.Width, it.Height, it.Image) { Tag = it.Tag });
                        }
                        break;
                    case ItemKind.Box:
                    {
                        float dx = xs[i] + it.RelX, dy = baseline - it.BoxBaseline + it.RelY;
                        if (it.Capture != null) Place(it.Capture, dx, dy);
                        else
                            foreach (var op in it.BoxOps!)
                            {
                                op.Offset(dx, dy);
                                _ops.Add(op);
                            }
                        break;
                    }
                    case ItemKind.Absolute:
                        QueueAbsolute(it.Element!, xs[i], y);
                        break;
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
            deco.Font.LineMetrics(deco.FontSize, out float asc, out float desc);
            float top = baseline + BaselineShift(deco) - asc - deco.PaddingTop.Resolve(0) + items[start].RelY;
            float h = asc + desc + deco.PaddingTop.Resolve(0) + deco.PaddingBottom.Resolve(0);
            float rx = xs[start] + items[start].RelX;
            if (deco.BoxShadows != null)
            {
                var shadows = new GroupOp();
                BoxPainter.PaintShadows(shadows, deco, rx, top, xs[i] - xs[start], h);
                _ops.Add(shadows);
            }
            if (CssColor.IsVisible(deco.BackgroundColor)) _ops.Add(new RectOp(rx, top, xs[i] - xs[start], h, CssColor.Fade(deco.BackgroundColor, deco.InlineFade)));
            if (deco.OutlineStyle != BorderStyle.None && deco.Visible)
            {
                var outline = new GroupOp();
                BoxPainter.PaintOutline(outline, deco, rx, top, xs[i] - xs[start], h, null);
                _ops.Add(outline);
            }
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
            StructElem? link = null;
            while (i < items.Count && items[i].Href == href)
            {
                link ??= items[i].Tag?.Parent.Closest("Link");
                i++;
            }
            _ops.Add(new LinkOp(xs[start], lineTop, xs[i] - xs[start], lineH, href) { Link = link });
        }
    }

    /// <summary>
    /// Cuts a line that overflows its clipping box (text-overflow: ellipsis): characters and inline boxes at the end are
    /// dropped until "…", in the block's font, fits within <paramref name="room"/>.
    /// </summary>
    private void Ellipsize(Line line, ComputedStyle block, float room)
    {
        const string Ellipsis = "\u2026";
        var font = block.Font;
        float ew = font.MeasureUnits(Ellipsis, 0, 1) * block.FontSize / 1000f + block.LetterSpacing;
        float limit = room - ew, used = 0;
        var items = line.Items;
        var kept = new List<Item>(items.Count + 1);
        int k = 0;
        for (; k < items.Count; k++)
        {
            var it = items[k];
            if (used + it.Width <= limit + Eps) { kept.Add(it); used += it.Width; continue; }
            if (it.Kind == ItemKind.Text && it.Length > 0)
            {
                // As many of the word's characters as fit.
                var f = it.Style.Font;
                float size = it.Style.FontSize, ls = it.Style.LetterSpacing, fw = 0;
                int fit = 0;
                while (fit < it.Length)
                {
                    float cw = f.CharWidth(it.Source[it.Start + fit], size) + ls;
                    if (used + fw + cw > limit + Eps) break;
                    fw += cw;
                    fit++;
                }
                if (fit > 0) { kept.Add(it.Slice(0, fit, fw)); used += fw; }
            }
            break;
        }
        // Anchors and positioned boxes on the cut part still need their positions.
        for (; k < items.Count; k++)
            if (items[k].Kind is ItemKind.Anchor or ItemKind.Absolute) kept.Add(items[k]);
        while (kept.Count > 0 && kept[^1].Kind == ItemKind.Space) { used -= kept[^1].Width; kept.RemoveAt(kept.Count - 1); }
        var dots = Rent(ItemKind.Text, block);
        dots.Tag = kept.LastOrDefault(k => k.Tag != null)?.Tag;
        dots.Source = Ellipsis;
        dots.Length = 1;
        dots.Width = ew;
        // The ellipsis takes the place of the last visible item, after any anchors kept at the end.
        int at = kept.Count;
        while (at > 0 && kept[at - 1].Kind is ItemKind.Anchor or ItemKind.Absolute) at--;
        kept.Insert(at, dots);
        items.Clear();
        items.AddRange(kept);
        line.Width = used + ew;
    }

    /// <summary>
    /// Paints the text-shadows of a run under it, the first shadow on top. A blurred shadow is approximated by faint
    /// copies spread around its offset, which soften its edge.
    /// </summary>
    private void TextShadows(ComputedStyle style, float x, float baseline, float lineTop, string text, float wordSpacing)
    {
        var shadows = style.TextShadows!;
        for (int i = shadows.Length - 1; i >= 0; i--)
        {
            var sh = shadows[i];
            uint color = CssColor.Fade(sh.Color ?? style.Color, style.InlineFade);
            if (!CssColor.IsVisible(color)) continue;
            float sx = x + sh.X, sy = baseline + sh.Y;
            float blur = Math.Min(sh.Blur, 50);
            if (blur <= 0.375f)
            {
                _ops.Add(new TextOp(sx, sy, lineTop, style.Font, style.FontSize, color, text, wordSpacing, style.LetterSpacing));
                continue;
            }
            // Seventeen faint copies, at the offset and on two rings around it: about two thirds of the alpha where they
            // all overlap, fading toward the edges.
            uint faint = (uint)Math.Max(1, MathF.Round((color >> 24) * 0.06f)) << 24 | (color & 0xFFFFFF);
            _ops.Add(new TextOp(sx, sy, lineTop, style.Font, style.FontSize, faint, text, wordSpacing, style.LetterSpacing));
            for (int ring = 1; ring <= 2; ring++)
            {
                float r = blur * ring / 4;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * MathF.PI / 4 + ring * MathF.PI / 8;
                    _ops.Add(new TextOp(sx + r * MathF.Cos(a), sy + r * MathF.Sin(a), lineTop, style.Font, style.FontSize, faint, text,
                        wordSpacing, style.LetterSpacing));
                }
            }
        }
    }

    private void Decorate(ComputedStyle s, float x, float w, float baseline)
    {
        float size = s.FontSize;
        float thickness = Math.Max(size * 0.055f, 0.5f);
        if ((s.TextDecoration & TextDecoration.Underline) != 0)
            _ops.Add(new RectOp(x, baseline + size * 0.1f, w, thickness, CssColor.Fade(s.Color, s.InlineFade)) { IsDecoration = true });
        if ((s.TextDecoration & TextDecoration.LineThrough) != 0)
            _ops.Add(new RectOp(x, baseline - size * 0.3f, w, thickness, CssColor.Fade(s.Color, s.InlineFade)) { IsDecoration = true });
        if ((s.TextDecoration & TextDecoration.Overline) != 0)
            _ops.Add(new RectOp(x, baseline - s.Font.Ascent * size, w, thickness, CssColor.Fade(s.Color, s.InlineFade)) { IsDecoration = true });
    }
}

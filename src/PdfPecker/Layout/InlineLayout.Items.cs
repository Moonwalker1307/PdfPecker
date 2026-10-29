using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;

namespace PdfPecker.Layout;

/// <summary>The pieces inline content is collected into (items), the lines they are broken into, and their pools.</summary>
internal sealed partial class LayoutEngine
{
    /// <summary>What an inline item is: a word, a space, a forced break, a replaced box, or a marker that takes no room.</summary>
    private enum ItemKind : byte
    {
        Text,     // a word or a piece of one, a slice of Source
        Space,    // a run of spaces, or a tab, with a width
        Break,    // a forced line break (br, or a newline in preserved white space)
        Image,    // an img element
        Box,      // an inline-block, an inline svg or an image laid out as a box of its own
        Spacer,   // the horizontal padding, border and margin of an inline element, or an image's margin
        Anchor,   // an id or a footnote call: records the position it ends up at
        Float,    // a float met in the text, placed beside the line it is on
        Absolute, // an absolutely positioned box, placed where it would have been in the text
        Leader,   // leader() in generated content: a pattern that fills the free space of the line
        Bidi,     // bidi controls in Source, from unicode-bidi; nothing is drawn
    }

    /// <summary>A line break opportunity right after a text item: none, a plain one, or one that shows a hyphen.</summary>
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
        public InlineDeco? Deco;      // nearest inline ancestor that paints a background (or clones its edges)
        public PdfImage? Image;
        public float Height;          // image or inline-block height
        public float PageExcess;      // how much wider than the page an image would be (see _replacedExcess)
        public float BoxBaseline;     // inline-block: distance from its top to its baseline
        public List<DrawOp>? BoxOps;
        public Captured? Capture;     // an inline-block laid out on its own, placed with its anchors and positioned boxes
        public string? AnchorId;
        public Element? Element;      // a float or an absolutely positioned box
        public float FloatMin;        // a float's min-content width, when measuring
        public float MinWidth = -1;   // an inline-block's min-content contribution, when measuring (-1: Width)
        public float RelX, RelY;      // offset of position: relative inline ancestors
        public ContentSlot? Tag;      // tagged PDF: the structure slot of the text or image
        public byte Level, Para;      // bidi embedding level, and the level of its paragraph
        public byte Justify;          // where a justified line puts space around it (JustifyBefore, JustifyAfter, JustifyLetters)
        // text-spacing-trim: the blank half of fullwidth punctuation that the text's first character loses on its left
        // (the text is drawn that much further left) and its last on its right. Width leaves both out.
        public float TrimStart, TrimEnd;

        public void Reset()
        {
            Source = "";
            Start = Length = 0;
            Width = Height = BoxBaseline = PageExcess = 0;
            Breakable = false;
            BreakAfter = BreakAfter.None;
            Href = null;
            Deco = null;
            Image = null;
            BoxOps = null;
            Capture = null;
            AnchorId = null;
            Element = null;
            FloatMin = RelX = RelY = TrimStart = TrimEnd = 0;
            MinWidth = -1;
            Level = Para = Justify = 0;
        }

        public string Text => Start == 0 && Length == Source.Length ? Source : Source.Substring(Start, Length);

        /// <summary>Whether this is a word in a text that takes part in line breaking.</summary>
        public bool IsText => Kind == ItemKind.Text;

        /// <summary>Whether this item is only a marker (an anchor, a positioned box or bidi controls) that takes no room on the line.</summary>
        public bool IsMarker => Kind is ItemKind.Anchor or ItemKind.Absolute or ItemKind.Bidi;

        /// <summary>
        /// A copy showing <paramref name="length"/> characters of this item's text from <paramref name="offset"/>, which
        /// are <paramref name="width"/> wide untrimmed; it keeps the trims of the characters it still has.
        /// </summary>
        public Item Slice(int offset, int length, float width)
        {
            var c = (Item)MemberwiseClone();
            c.Start = Start + offset;
            c.Length = length;
            if (offset != 0) c.TrimStart = 0;
            if (offset + length != Length) c.TrimEnd = 0;
            c.Width = width - c.TrimStart - c.TrimEnd;
            return c;
        }

        /// <summary>A copy with other text, <paramref name="width"/> wide; the trim of its last character goes, as the character may.</summary>
        public Item CloneWithText(string text, float width)
        {
            var c = (Item)MemberwiseClone();
            c.Source = text;
            c.Start = 0;
            c.Length = text.Length;
            c.Width = width;
            c.TrimEnd = 0;
            return c;
        }

        /// <summary>A copy whose first and last characters are trimmed by <paramref name="start"/> and <paramref name="end"/> instead.</summary>
        public Item Trimmed(float start, float end)
        {
            var c = (Item)MemberwiseClone();
            c.Width = Width + TrimStart + TrimEnd - start - end;
            c.TrimStart = start;
            c.TrimEnd = end;
            return c;
        }
    }

    /// <summary>
    /// An inline element that paints a background, shadows, borders or an outline on its line fragments, that gives
    /// each fragment its own padding and borders (box-decoration-break: clone), or that is positioned and so may be the
    /// containing block of absolutely positioned boxes, with the nearest such element around it.
    /// The two collections of a paragraph with a ::first-line style each make their own, so the fragments of one
    /// element are matched by the element, not by the deco; nor by the style, which siblings styled alike share.
    /// </summary>
    private sealed class InlineDeco
    {
        public readonly Element Element;
        public readonly ComputedStyle Style;
        public readonly InlineDeco? Parent;
        /// <summary>The padding and border widths of the left and right edge, which clone repeats where the element breaks.</summary>
        public readonly float Left, Right;
        /// <summary>How many such elements it is inside.</summary>
        public readonly int Depth;
        /// <summary>
        /// The left and right edges that box-decoration-break: clone repeats for this element and those around it
        /// together, so that a break inside deeply nested boxes is measured without walking them.
        /// </summary>
        public readonly float CloneLeft, CloneRight;
        /// <summary>Whether its fragments paint or clone anything, rather than only placing positioned boxes.</summary>
        public bool Paints = true;
        /// <summary>Whether it is the containing block of an absolutely positioned box among the items.</summary>
        public bool HoldsAbsolutes;

        public InlineDeco(Element element, ComputedStyle style, InlineDeco? parent, float left, float right)
        {
            Element = element;
            Style = style;
            Parent = parent;
            Left = left;
            Right = right;
            Depth = parent == null ? 0 : parent.Depth + 1;
            bool clone = style.BoxDecorationClone;
            CloneLeft = (clone ? left : 0) + (parent?.CloneLeft ?? 0);
            CloneRight = (clone ? right : 0) + (parent?.CloneRight ?? 0);
        }
    }

    private sealed class Line
    {
        public readonly List<Item> Items = new();
        public float Width;
        public bool Forced;
        // Beside floats: the line's top and its room (left offset and width); NaN when the line simply follows the last.
        public float Top = float.NaN, Left, Avail = float.NaN;
        // The first line of a list item whose ::marker has a style of its own: the marker sits on this line's baseline,
        // so its font and line height can make the line taller. An outside marker only raises the line's top.
        public ComputedStyle? Marker;
        public bool MarkerOutside;
        public float MarkerImageH; // a marker image, which stands on the baseline: the line's ascent holds its height

        /// <summary>Whether the line was placed beside floats while it was broken, which fixed its room.</summary>
        public bool HasRoom => !float.IsNaN(Avail);
    }

    /// <summary>The state of collecting a run of inline content into items.</summary>
    private sealed class CollectState
    {
        public readonly List<Item> Items = new();
        public bool LastWasSpace = true;      // a collapsible space after this would be dropped
        public int Column;                    // the column in preserved white space, for tab stops
        public float AvailWidth;
        public FirstLineStyle? FirstLine;     // set while collecting the text of a block's first line

        /// <summary>The item added last, when there is one.</summary>
        public Item? Last => Items.Count > 0 ? Items[^1] : null;
    }

    // ------------------------------------------------------------------ pools

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

    private CollectState RentState()
    {
        if (!_statePool.TryPop(out var st)) return new CollectState();
        st.LastWasSpace = true;
        st.Column = 0;
        return st;
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
        line.Marker = null;
        line.MarkerOutside = false;
        line.MarkerImageH = 0;
        return line;
    }

    private void ReturnLine(Line line)
    {
        line.Items.Clear();
        _linePool.Push(line);
    }

    private List<Line> RentLineList() => _lineListPool.Count > 0 ? _lineListPool.Pop() : new List<Line>();

    private void ReturnLines(List<Line> lines)
    {
        foreach (var line in lines) ReturnLine(line);
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

    /// <summary>Adds a spacer of <paramref name="width"/>: the padding, border or margin of an inline element or image.</summary>
    private void AddSpacer(CollectState st, ComputedStyle style, float width, string? href, InlineDeco? deco = null)
    {
        var spacer = Rent(ItemKind.Spacer, style);
        spacer.Width = width;
        spacer.Href = href;
        spacer.Deco = deco;
        st.Items.Add(spacer);
    }

    /// <summary>Adds an anchor that records where <paramref name="id"/> ends up.</summary>
    private void AddAnchor(CollectState st, ComputedStyle style, string id)
    {
        var anchor = Rent(ItemKind.Anchor, style);
        anchor.AnchorId = id;
        st.Items.Add(anchor);
    }

    /// <summary>Adds a forced line break; the text after it starts a line, and its first column.</summary>
    private void AddBreak(CollectState st, ComputedStyle style, string? href = null, InlineDeco? deco = null)
    {
        var br = Rent(ItemKind.Break, style);
        br.Href = href;
        br.Deco = deco;
        st.Items.Add(br);
        st.LastWasSpace = true;
        st.Column = 0;
    }
}

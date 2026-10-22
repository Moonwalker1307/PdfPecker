using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Layout;

/// <summary>
/// Inline formatting: turning text and inline boxes into words, breaking them into lines and placing the lines. The
/// items and their pools, collecting, the words, line breaking, placing, orphans and widows, the ellipsis and the
/// decorations each have a partial file of their own; this one lays out a paragraph from start to end.
/// </summary>
internal sealed partial class LayoutEngine
{
    // A paragraph whose lines move between pages of different widths, or to keep orphans and widows, is broken again
    // until it settles, at most this many times.
    private const int MaxRebreaks = 8;

    private float FlushInline(List<Node> run, Element container, float x, float y, float w, bool firstInBlock, out bool produced)
    {
        Cancellation.ThrowIfCancellationRequested();
        var pages = _inlinePageBox;
        _inlinePageBox = null;
        _inlineDepth++;
        try
        {
            return FlushInlineCore(run, container, x, y, w, firstInBlock, pages, out produced);
        }
        finally
        {
            if (--_inlineDepth == 0) _itemPoolUsed = 0;
        }
    }

    private float FlushInlineCore(List<Node> run, Element container, float x, float y, float w, bool firstInBlock, PageBox? pages, out bool produced)
    {
        _boxLayouts++;
        // Percentage heights of the images and inline-blocks of the lines resolve as on the page the lines start on.
        NoteCbKind(y);
        var st = CollectInline(run, w);
        var items = st.Items;
        long chars = 0;
        foreach (var it in items)
            if (it.IsText) chars += it.Length;
        _boxLayouts += chars / CharactersPerLayout;
        if (!HasRenderable(items))
        {
            PlaceEmptyRun(items, x, y, w);
            ReturnState(st);
            produced = false;
            return y;
        }
        var cs = container.Style;
        ResolveBidi(items, cs);
        float indent = firstInBlock ? cs.TextIndent : 0;
        if (_marker is { Inside: true } inside)
        {
            // An inside marker starts the first line: the line moves over by the marker's width, and the marker is drawn
            // just before where its content now begins.
            float mw = InsideMarkerWidth(inside);
            inside.ContentX = x + indent + mw;
            inside.ContentRight = x + w - indent - mw;
            indent += mw;
        }
        bool besideFloats = FloatsBelow(y) || items.Exists(it => it.Kind == ItemKind.Float);
        // An initial letter starts the first line after its text-indent, and the lines beside it start where it ends.
        float letterIndent = 0;
        if (firstInBlock && indent != 0 && items.Exists(it => it.Kind == ItemKind.Float && IsInitialLetter(it.Element!)))
            (letterIndent, indent) = (indent, 0);
        // On pages of different widths each line is measured for the page it goes on, as lines beside floats are.
        if (_measuring || !_paginate) pages = null;
        StrutMetrics(cs, out float strutA, out float strutD);
        var paragraph = new Paragraph(this, items, cs, x, y, w, indent, strutA + strutD, pages) { LetterIndent = letterIndent };
        FloatLines? fl = besideFloats || pages != null ? paragraph.NewLines() : null;
        paragraph.First = FirstLineItems(run, container, firstInBlock, w, items, cs);
        int opsMark = _ops.Count;
        var lines = paragraph.Break(fl);
        if (pages != null)
        {
            // A line that went to another page than the one it was measured for, being taller than the paragraph's
            // line height, is measured again for that page; and lines that move to keep orphans and widows are
            // measured for the page they move to. Each change can move the lines after it, so the paragraph is broken
            // again until it settles.
            Dictionary<int, float>? hints = null;
            bool[]? plan = null;
            for (int attempt = 0; attempt < MaxRebreaks; attempt++)
            {
                if (fl!.Moved >= 0) (hints ??= new())[fl.Moved] = fl.MovedHeight;
                else
                {
                    if (besideFloats) break;
                    var next = PlanLineBreaks(lines, cs, y);
                    if (SamePlan(next, plan)) break;
                    plan = next;
                }
                ReturnLines(lines);
                Truncate(opsMark); // the floats the lines placed
                fl = paragraph.NewLines();
                fl.HeightHints = hints;
                fl.ForcedBefore = plan;
                lines = paragraph.Break(fl);
            }
        }
        produced = true;
        float bottom = PlaceLines(lines, cs, x, y, w, indent, besideFloats);
        // A br that clears floats at the end of the last line moves what follows the lines below the floats too.
        if (besideFloats && lines.Count > 0 && ClearingBreak(lines[^1]) is { } br) bottom = Cleared(br, bottom);
        if (besideFloats && _floats.ShapeIn(y, bottom)) LiftInlineContent(opsMark);
        ReturnLines(lines);
        if (paragraph.First != null) ReturnState(paragraph.First);
        ReturnState(st);
        return bottom;
    }

    /// <summary>A run with nothing to draw still places its anchors, floats and positioned boxes at <paramref name="y"/>.</summary>
    private void PlaceEmptyRun(List<Item> items, float x, float y, float w)
    {
        foreach (var it in items)
        {
            if (it.Kind == ItemKind.Anchor) SetAnchor(it.AnchorId!, y);
            else if (it.Kind == ItemKind.Float) PlaceFloat(MeasureFloat(it.Element!, w), y, x, w);
            else if (it.Kind == ItemKind.Absolute) QueueAbsolute(it.Element!, x, y);
        }
    }

    /// <summary>
    /// With a ::first-line style the items are collected a second time, styled for the first line, and the line breaker
    /// switches from that list to the usual one once the first line is full. Small capitals, bidi levels and
    /// hyphenation may cut the text into other items in each list, so the breaker finds its place in the usual list by
    /// the characters before it. Null when there is no such style.
    /// </summary>
    private CollectState? FirstLineItems(List<Node> run, Element container, bool firstInBlock, float w, List<Item> items, ComputedStyle cs)
    {
        if (!firstInBlock || container.FirstLine is not { } firstLine || _measuring) return null;
        var first = CollectInline(run, w, firstLine);
        ResolveBidi(first.Items, cs);
        // text-transform and small capitals change letters but not their number, so both lists hold as many
        // characters. Were that ever not so, the breaker could not find its place, and the first line keeps the usual
        // style rather than losing or repeating text.
        if (ContentLength(first.Items) == ContentLength(items)) return first;
        ReturnState(first);
        return null;
    }

    /// <summary>How much content an item holds: its characters if it is text, otherwise one.</summary>
    private static int ContentUnits(Item it) => it.IsText ? it.Length : 1;

    private static long ContentLength(List<Item> items)
    {
        long n = 0;
        foreach (var it in items) n += ContentUnits(it);
        return n;
    }

    /// <summary>A paragraph being laid out: what its line breaker needs, and the lines beside floats it may be broken with.</summary>
    private ref struct Paragraph
    {
        private readonly LayoutEngine _engine;
        private readonly List<Item> _items;
        private readonly ComputedStyle _block;
        private readonly float _x, _y, _w, _indent, _strutH;
        private readonly PageBox? _pages;
        public CollectState? First;
        public float LetterIndent;

        public Paragraph(LayoutEngine engine, List<Item> items, ComputedStyle block, float x, float y, float w, float indent, float strutH, PageBox? pages)
        {
            _engine = engine;
            _items = items;
            _block = block;
            _x = x;
            _y = y;
            _w = w;
            _indent = indent;
            _strutH = strutH;
            _pages = pages;
        }

        public readonly FloatLines NewLines() => new() { Engine = _engine, Block = _block, X = _x, W = _w, Y = _y, StrutH = _strutH, Pages = _pages, LetterIndent = LetterIndent };

        /// <summary>Breaks the paragraph into lines, from the ::first-line items when there are any, and flushes the floats.</summary>
        public List<Line> Break(FloatLines? fl)
        {
            var broken = First != null ? _engine.BreakLines(First.Items, _w, _indent, fl, after: _items) : _engine.BreakLines(_items, _w, _indent, fl, block: _block);
            fl?.Flush(broken);
            return broken;
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
}

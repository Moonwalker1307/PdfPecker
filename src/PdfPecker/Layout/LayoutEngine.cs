using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Pdf;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// Lays out the styled tree into a flat list of <see cref="DrawOp"/>s. Pagination happens during layout:
/// atomic pieces (lines, images, table rows) that would straddle a page boundary are pushed to the next page.
/// </summary>
internal sealed partial class LayoutEngine
{
    private readonly float _pageH;
    private readonly ImageLoader _images;
    private List<DrawOp> _ops = new(256);
    private bool _paginate = true;
    private ListMarker? _marker;
    private int _currentListIndex;
    private float _lastBaseline = float.NaN;
    private float _lastBoxRight; // right border edge (plus nothing else) of the last block box laid out
    private float _lastBoxX, _lastBoxTop, _lastBoxW, _lastBoxH; // border box of the last block box laid out
    private readonly Dictionary<Element, (float Min, float Max)> _intrinsicCache = new();

    public readonly Dictionary<string, float> Anchors = new(StringComparer.Ordinal);

    public LayoutEngine(float pageContentHeight, ImageLoader images)
    {
        _pageH = pageContentHeight;
        _images = images;
    }

    public List<DrawOp> Ops => _ops;

    /// <summary>The root element (html), whose border box positions the page canvas background.</summary>
    public Element? CanvasRoot { get; init; }

    /// <summary>The border box of <see cref="CanvasRoot"/> once laid out.</summary>
    public (float X, float Y, float W, float H)? CanvasRootBox { get; private set; }

    /// <summary>For a tagged PDF: the document's structure, whose slots the text, image and marker ops are tagged with.</summary>
    public StructureTree? Tags { get; init; }

    /// <summary>Stops the layout when cancelled.</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>The most pages the flow may fill; checked as lines are placed, before their ops pile up.</summary>
    public int MaxPages { get; init; } = int.MaxValue;

    /// <summary>Makes the content ops from <paramref name="start"/> on artifacts, for content drawn again (repeated table headers).</summary>
    private static void Untag(List<DrawOp> ops, int start = 0)
    {
        for (int i = start; i < ops.Count; i++)
        {
            ops[i].Tag = null;
            if (ops[i] is GroupOp g) Untag(g.Ops);
        }
    }

    private void CheckPageLimit(float y)
    {
        if (y >= (double)MaxPages * _pageH) throw new PdfLimitException($"The document would have more than {MaxPages:N0} pages (PdfOptions.MaxPages).");
    }

    private sealed class ListMarker
    {
        public string? Text;
        public ListStyleType Type;
        public ComputedStyle Style = null!;
        public float ContentX;
        public ContentSlot? Tag;
    }

    public float Layout(Element document, float width)
    {
        float bottom = LayoutFlow(document, 0, 0, width);
        if (_floats.Any && _floats.Bottom > bottom) bottom = _floats.Bottom; // the root contains its floats
        FinishRoot(width);
        return bottom;
    }

    /// <summary>Lays out a running header or footer on its own, unpaginated, at y = 0. Returns its height including margins.</summary>
    public float LayoutBand(Element band, float width)
    {
        _paginate = false;
        var s = band.Style;
        float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(width), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(width);
        float bottom = LayoutBlockBox(band, 0, mt, width, shrinkToFit: false) + mb;
        FinishRoot(width);
        return bottom;
    }

    // ------------------------------------------------------------------ pagination helpers

    private const float Eps = 0.01f;

    private int PageOf(float y) => (int)Math.Floor((y + Eps) / _pageH);

    private bool AtPageTop(float y) => y - PageOf(y) * _pageH < 0.5f;

    /// <summary>Returns the y at which a box of height h should start so that it does not straddle a page boundary.</summary>
    private float FitOnPage(float y, float h)
    {
        if (!_paginate || h > _pageH) return y;
        int p = PageOf(y);
        float pageEnd = (p + 1) * _pageH;
        return y + h > pageEnd + Eps ? pageEnd : y;
    }

    private float NextPage(float y)
    {
        if (!_paginate || AtPageTop(y)) return y;
        return (PageOf(y) + 1) * _pageH;
    }

    private bool CrossesPage(float top, float bottom) => _paginate && bottom - top > Eps && PageOf(top) != PageOf(bottom - 0.5f);

    private void Truncate(int count, bool keepFloats = false)
    {
        if (_ops.Count > count) _ops.RemoveRange(count, _ops.Count - count);
        if (!keepFloats) _floats.Truncate(count);
    }

    // ------------------------------------------------------------------ block formatting

    private static bool IsAtomicInline(Display d) => d is Display.InlineBlock or Display.InlineFlex or Display.InlineGrid;

    private static bool IsInlineLevel(Element e)
    {
        var d = e.Style.Display;
        if (IsAtomicInline(d)) return true;
        if (d != Display.Inline) return false;
        return !ContainsBlock(e);
    }

    private static bool ContainsBlock(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce || ce.Style == null || ce.Detached || ce.Style.Floated || ce.Style.OutOfFlow) continue;
            var d = ce.Style.Display;
            if (d == Display.None || IsAtomicInline(d)) continue;
            if (d != Display.Inline) return true;
            if (ContainsBlock(ce)) return true;
        }
        return false;
    }

    private static float CollapseMargins(float a, float b)
    {
        if (a >= 0 && b >= 0) return Math.Max(a, b);
        if (a < 0 && b < 0) return Math.Min(a, b);
        return a + b;
    }

    /// <summary>Where a block box of a flow started, so that layout can rewind to it (break-after/before: avoid).</summary>
    private struct FlowMark
    {
        public int Index, OpsCount, ListIndex, CurrentListIndex;
        public float Y, PendingMargin, Top;
        public bool AnyContent;
        public ListMarker? Marker;
        public int FirstPage; // page of the box's first painted content
    }

    /// <summary>Lays out the children of a block container. Returns the bottom of the content (including the last child's bottom margin).</summary>
    private float LayoutFlow(Element parent, float x, float y, float w)
    {
        float pendingMargin = 0;
        bool anyContent = false;
        // The parent has taken its first child's top margin as its own (they collapse): don't apply it again.
        bool absorbFirst = _absorbTop;
        _absorbTop = false;
        bool firstBlock = true;
        List<Node>? run = null;
        var children = parent.Children;

        int listIndex = 0;
        if (parent.Name == "ol" && int.TryParse(parent.GetAttribute("start"), out int start)) listIndex = start - 1;

        // Keep-with-next: the first box of a run of boxes that must not be separated from what follows them.
        FlowMark prev = default, chain = default;
        bool hasPrev = false, hasChain = false, prevAvoidAfter = false;
        int forcedAt = -1; // the box that was moved to a new page to keep its chain together

        int i;
        for (i = 0; i <= children.Count; i++)
        {
            Element? e = null;
            if (i < children.Count)
            {
                var child = children[i];
                if (child is TextNode)
                {
                    (run ??= RentRun()).Add(child);
                    continue;
                }
                e = (Element)child;
                var es = e.Style;
                if (es == null || es.Display == Display.None || e.Detached) continue;
                if (IsInlineLevel(e) || (es.Floated || es.OutOfFlow) && run is { Count: > 0 })
                {
                    // Floats and absolutely positioned boxes among inline content are placed as the lines reach them.
                    (run ??= RentRun()).Add(child);
                    continue;
                }
            }

            if (run is { Count: > 0 })
            {
                float before = y;
                int runOps = _ops.Count;
                y = FlushInline(run, parent, x, y + pendingMargin, w, !anyContent, out bool produced);
                run.Clear();
                if (produced)
                {
                    pendingMargin = 0;
                    anyContent = true;
                    if (hasChain && prevAvoidAfter && KeepTogetherFails(chain, runOps, before, forcedAt))
                    {
                        Rewind(chain);
                        forcedAt = chain.Index;
                        hasChain = hasPrev = prevAvoidAfter = false;
                        continue;
                    }
                    hasChain = hasPrev = prevAvoidAfter = false;
                }
                else y = before;
            }
            if (e == null) break;

            var s = e.Style;
            if (s.OutOfFlow)
            {
                // Its static position: where its top margin edge would be in the flow.
                float smt = s.MarginTop.Resolve(w);
                QueueAbsolute(e, x, y + CollapseMargins(pendingMargin, smt) - smt);
                continue;
            }
            if (s.Floated)
            {
                PlaceFloat(MeasureFloat(e, w), y + pendingMargin, x, w);
                continue;
            }
            var mark = new FlowMark
            {
                Index = i, OpsCount = _ops.Count, ListIndex = listIndex, CurrentListIndex = _currentListIndex,
                Y = y, PendingMargin = pendingMargin, AnyContent = anyContent, Marker = _marker,
            };
            // A forced break wins over avoid.
            bool joins = _paginate && hasPrev && (prevAvoidAfter || s.AvoidBreakBefore) && !s.PageBreakBefore;
            if (!joins) hasChain = false;
            if (joins && !hasChain) { chain = prev; hasChain = true; }

            if (s.PageBreakBefore || s.PageBreakAfter) _forcedBreaks++;
            if (s.Display == Display.ListItem)
            {
                listIndex++;
                if (int.TryParse(e.GetAttribute("value"), out int v)) listIndex = v;
                _currentListIndex = listIndex;
            }

            // A box's top margin collapses with those of its first children (CSS 2.1 §8.3.1).
            float mt = absorbFirst && firstBlock && !anyContent
                ? 0
                : CollapseMargins(s.MarginTop.Resolve(w), LeadingMargin(e, w));
            firstBlock = false;
            float top = y + CollapseMargins(pendingMargin, mt);
            if (s.ClearLeft || s.ClearRight) top = Cleared(s, top);
            if (s.PageBreakBefore || forcedAt == i) top = NextPage(top);

            int boxOps = _ops.Count;
            float bottom;
            if (FloatsBelow(top) && AvoidsFloats(e)) bottom = LayoutBesideFloats(e, x, ref top, w);
            else bottom = LayoutBlockBox(e, x, top, w, shrinkToFit: false);
            float trailing = _boxTrailing;
            if (joins && KeepTogetherFails(chain, boxOps, top, forcedAt))
            {
                Rewind(chain);
                forcedAt = chain.Index;
                hasChain = hasPrev = prevAvoidAfter = false;
                continue;
            }
            float mb = CollapseMargins(s.MarginBottom.Resolve(w), trailing);
            if (bottom - top < Eps && CollapsesThrough(e) && !s.PageBreakBefore && !s.PageBreakAfter)
            {
                // An empty box: its margins collapse with each other and with those around it.
                pendingMargin = CollapseMargins(CollapseMargins(pendingMargin, mt), mb);
                continue;
            }
            anyContent = true;
            pendingMargin = mb;
            y = bottom;
            if (s.PageBreakAfter)
            {
                y = NextPage(y + pendingMargin);
                pendingMargin = 0;
            }

            mark.Top = top;
            if (_paginate) mark.FirstPage = FirstContentPage(boxOps, top);
            prevAvoidAfter = _paginate && s.AvoidBreakAfter && !s.PageBreakAfter;
            if (!prevAvoidAfter) hasChain = false;
            prev = mark;
            hasPrev = !s.PageBreakAfter;
        }
        _flowTrailing = pendingMargin;
        ReturnRun(run);
        return y + pendingMargin;

        void Rewind(in FlowMark m)
        {
            Truncate(m.OpsCount);
            i = m.Index - 1;
            y = m.Y;
            pendingMargin = m.PendingMargin;
            anyContent = m.AnyContent;
            listIndex = m.ListIndex;
            _currentListIndex = m.CurrentListIndex;
            _marker = m.Marker;
            run?.Clear();
        }
    }

    /// <summary>
    /// Whether the content laid out from <paramref name="opsStart"/> begins on a later page than the chain that must stay
    /// with it, and moving the chain to a new page would help.
    /// </summary>
    private bool KeepTogetherFails(in FlowMark chain, int opsStart, float fallbackY, int forcedAt)
        => chain.Index != forcedAt && !AtPageTop(chain.Top)
           && FirstContentPage(opsStart, fallbackY) > chain.FirstPage;

    /// <summary>The page of the first text, image or drawing among the ops from <paramref name="from"/>.</summary>
    private int FirstContentPage(int from, float fallbackY)
    {
        for (int i = from; i < _ops.Count; i++)
            if (FirstContentY(_ops[i]) is { } fy) return PageOf(fy);
        return PageOf(fallbackY);
    }

    private static float? FirstContentY(DrawOp op)
    {
        switch (op)
        {
            case TextOp t: return t.LineTop;
            case ImageOp im: return im.Y;
            case SvgOp sv: return sv.Y;
            case GroupOp g:
                foreach (var o in g.Ops)
                    if (FirstContentY(o) is { } y) return y;
                return null;
            default: return null;
        }
    }

    // Margin collapsing between a box and its children: set by LayoutBlockBoxCore and LayoutFlow for their callers.
    private bool _absorbTop;      // the box being laid out has taken its first child's top margin
    private float _flowTrailing;  // the bottom margin of the last child of the flow just laid out
    private float _boxTrailing;   // the bottom margin of its last child that the box just laid out passes on

    /// <summary>A box in normal flow whose margins can collapse with its children's (not a flex, grid, table or column box).</summary>
    private static bool IsPlainFlow(Element e)
    {
        var s = e.Style;
        return s.Display is Display.Block or Display.ListItem && !s.IsMultiColumn && !s.Floated && !s.OutOfFlow && !s.FormattingRoot
               && e.Name is not ("img" or "svg" or "hr");
    }

    // The root element's margins do not collapse with its children's (CSS 2.1 §8.3.1).
    private static bool IsRoot(Element e) => e.Parent is { Name: "#document" };

    private static bool CollapsesTop(Element e)
        => IsPlainFlow(e) && !IsRoot(e) && e.Style.BorderTop == 0 && e.Style.PaddingTop.Resolve(0) == 0 && !e.Style.PaddingTop.IsPercent;

    private static bool CollapsesBottom(Element e)
    {
        var s = e.Style;
        return IsPlainFlow(e) && !IsRoot(e) && s.BorderBottom == 0 && s.PaddingBottom.Resolve(0) == 0 && !s.PaddingBottom.IsPercent
               && s.Height.IsAuto && (s.MinHeight.IsAuto || s.MinHeight.Value <= 0);
    }

    private static bool CollapsesThrough(Element e) => CollapsesTop(e) && CollapsesBottom(e);

    /// <summary>The first child of a box whose top margin collapses with the box's, or null.</summary>
    private static Element? FirstBlockChild(Element e)
    {
        if (!CollapsesTop(e)) return null;
        foreach (var child in e.Children)
        {
            if (child is TextNode t)
            {
                if (string.IsNullOrWhiteSpace(t.Text) && IsCollapsible(e.Style)) continue;
                return null;
            }
            var c = (Element)child;
            if (c.Style == null || c.Style.Display == Display.None || c.Detached || c.Style.OutOfFlow || c.Style.Floated) continue;
            if (IsInlineLevel(c) || c.Style.PageBreakBefore || c.Style.ClearLeft || c.Style.ClearRight) return null;
            return c;
        }
        return null;
    }

    /// <summary>The collapsed top margin of a box's first children, which the box takes as part of its own.</summary>
    private static float LeadingMargin(Element e, float w)
    {
        var c = FirstBlockChild(e);
        return c == null ? 0 : CollapseMargins(c.Style.MarginTop.Resolve(w), LeadingMargin(c, w));
    }

    /// <summary>Lays out a block-level box whose top border edge is at <paramref name="y"/>. Returns its bottom border edge.</summary>
    private float LayoutBlockBox(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        Cancellation.ThrowIfCancellationRequested();
        CheckPageLimit(y);
        var s = e.Style;
        bool table = s.Display == Display.Table;
        int opsStart = _ops.Count;
        var savedMarker = _marker;
        float bottom = table ? LayoutTable(e, x, y, availW) : LayoutBlockBoxCore(e, x, y, availW, shrinkToFit);
        if (s.AvoidBreakInside && CrossesPage(y, bottom) && bottom - y <= _pageH && !AtPageTop(y))
        {
            Truncate(opsStart);
            _marker = savedMarker;
            float ny = NextPage(y);
            bottom = table ? LayoutTable(e, x, ny, availW) : LayoutBlockBoxCore(e, x, ny, availW, shrinkToFit);
        }
        if (table) _boxTrailing = 0;
        if (s.Layered) FinishLayered(e, opsStart, availW);
        return bottom;
    }

    private float LayoutBlockBoxCore(Element e, float x, float y, float availW, bool shrinkToFit)
    {
        var s = e.Style;
        if (e.Id != null) SetAnchor(e.Id, y);
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) SetAnchor(aname, y);

        float ml = s.MarginLeft.Resolve(availW), mr = s.MarginRight.Resolve(availW);
        float bl = s.BorderLeft, br = s.BorderRight, bt = s.BorderTop, bb = s.BorderBottom;
        float pl = s.PaddingLeft.Resolve(availW), pr = s.PaddingRight.Resolve(availW);
        float pt = s.PaddingTop.Resolve(availW), pb = s.PaddingBottom.Resolve(availW);
        float frame = bl + br + pl + pr;
        float frameV = bt + bb + pt + pb;

        // Sizes imposed by a flex or grid container (border-box).
        bool hasWOverride = _widthOverride.TryGetValue(e, out float overrideW);
        bool hasHOverride = _heightOverride.TryGetValue(e, out float overrideH);

        bool isImage = e.Name == "img", isSvg = e.Name == "svg";
        float imgW = 0, imgH = 0;
        PdfImage? image = null;
        SvgImage? svg = null;
        float contentW;
        if (isImage || isSvg)
        {
            float room = hasWOverride ? overrideW - frame : availW - ml - mr - frame;
            if (isImage) image = ImageSize(e, room, out imgW, out imgH, out svg);
            else svg = SvgSize(e, room, out imgW, out imgH);
            if (hasWOverride && imgW > 0 && Math.Abs(room - imgW) > Eps)
            {
                if (s.Height.IsAuto) imgH *= Math.Max(0, room) / imgW;
                imgW = Math.Max(0, room);
            }
            contentW = imgW;
        }
        else if (hasWOverride)
        {
            contentW = overrideW - frame;
        }
        else if (!s.Width.IsAuto)
        {
            contentW = s.Width.Resolve(availW);
            if (s.BorderBox) contentW -= frame;
        }
        else if (s.AspectRatio > 0 && !s.Height.IsAuto && !s.Height.IsPercent)
        {
            // aspect-ratio with a fixed height and an auto width: the width follows from the height.
            float boxH = s.Height.Value + (s.BorderBox ? 0 : frameV);
            contentW = RatioOnBorderBox(s) ? boxH * s.AspectRatio - frame : (boxH - frameV) * s.AspectRatio;
        }
        else
        {
            contentW = availW - ml - mr - frame;
            if (shrinkToFit)
            {
                var (_, max) = Intrinsic(e);
                contentW = Math.Min(contentW, max - frame);
            }
        }
        if (!s.MaxWidth.IsAuto && !isImage && !isSvg && !hasWOverride)
        {
            float mw = s.MaxWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW > mw) contentW = mw;
        }
        if (!s.MinWidth.IsAuto && !isImage && !isSvg && !hasWOverride)
        {
            float mnw = s.MinWidth.Resolve(availW) - (s.BorderBox ? frame : 0);
            if (contentW < mnw) contentW = mnw;
        }
        if (contentW < 0) contentW = 0;

        float used = contentW + frame;
        if (s.MarginLeft.IsAuto && s.MarginRight.IsAuto) ml = Math.Max(0, (availW - used) / 2);
        else if (s.MarginLeft.IsAuto) ml = Math.Max(0, availW - used - mr);
        float bx = x + ml;

        int opsStart = _ops.Count;
        var group = new GroupOp();
        _ops.Add(group);

        // Positioned and transformed boxes are the containing block of their absolutely positioned descendants.
        CbFrame? cbFrame = null;
        if (s.Positioned || s.Transform != null) _frames.Add(cbFrame = new CbFrame { Transformed = s.Transform != null });
        // Boxes that start a block formatting context keep their floats inside (CSS 2.1 §9.4.1).
        bool formattingRoot = shrinkToFit || hasWOverride || hasHOverride || s.Floated || s.OutOfFlow || s.FormattingRoot
                              || s.IsFlex || s.IsGrid || s.IsMultiColumn || s.Display is Display.TableCell or Display.TableCaption
                              || e.Parent?.Style is { IsFlex: true } or { IsGrid: true };

        float cx = bx + bl + pl;
        float cy = y + bt + pt;

        if (s.Display == Display.ListItem && s.ListStyleType != ListStyleType.None)
            _marker = new ListMarker { Text = MarkerText(s.ListStyleType, _currentListIndex), Type = s.ListStyleType, Style = s, ContentX = cx, Tag = Tags?.MarkerOf(e) };

        float contentBottom;
        float trailing = 0;
        if (isImage || isSvg)
        {
            if (_paginate)
            {
                float fitted = FitOnPage(y, imgH + bt + bb + pt + pb);
                if (fitted != y)
                {
                    y = fitted;
                    cy = y + bt + pt;
                }
            }
            if (image != null && s.Visible)
            {
                // Rounded corners clip the image to the content box's curve.
                var radii = BoxPainter.InnerRadii(BoxPainter.Radii(s, imgW + bl + br + pl + pr, imgH + bt + bb + pt + pb),
                    bl + pl, bt + pt, br + pr, bb + pb);
                AddImage(s, image, cx, cy, imgW, imgH, radii, Tags?.Of(e));
            }
            if (svg != null && s.Visible) _ops.Add(new SvgOp(cx, cy, imgW, imgH, svg) { Tag = Tags?.Of(e) });
            contentBottom = cy + imgH;
        }
        else if (e.Name == "hr")
        {
            contentBottom = cy;
        }
        else
        {
            float? definiteH = null;
            if (hasHOverride) definiteH = Math.Max(0, overrideH - frameV);
            else if (!s.Height.IsAuto && !s.Height.IsPercent) definiteH = Math.Max(0, s.Height.Value - (s.BorderBox ? frameV : 0));
            float minH = !s.MinHeight.IsAuto && !s.MinHeight.IsPercent ? Math.Max(0, s.MinHeight.Value - (s.BorderBox ? frameV : 0)) : 0;
            if (RatioHeight(s, contentW, frame, frameV, hasHOverride) is float rh) minH = Math.Max(minH, rh);
            // A box that is not a formatting context root shares its top and bottom margins with its children's.
            bool joinsChildren = !shrinkToFit && !hasWOverride && !hasHOverride;
            _absorbTop = joinsChildren && FirstBlockChild(e) != null;
            var outerFloats = formattingRoot ? EnterFormattingContext() : null;
            contentBottom = LayoutContent(e, cx, cy, contentW, definiteH, minH);
            if (outerFloats != null) contentBottom = LeaveFormattingContext(outerFloats, contentBottom);
            _absorbTop = false;
            if (joinsChildren && CollapsesBottom(e) && !s.IsFlex && !s.IsGrid)
            {
                trailing = _flowTrailing;
                contentBottom -= trailing;
            }
        }

        if (_marker != null && _marker.Style == s)
        {
            // List item without any line of text (e.g. it only holds a nested list): put the marker at the top.
            EmitMarker(cy + s.Font.Ascent * s.FontSize, cy);
        }

        float contentH = contentBottom - cy;
        if (hasHOverride && !isImage && !isSvg)
        {
            contentH = Math.Max(0, overrideH - frameV);
        }
        else if (!s.Height.IsAuto && !s.Height.IsPercent)
        {
            float h = s.Height.Value - (s.BorderBox ? bt + bb + pt + pb : 0);
            contentH = Math.Max(0, h);
        }
        if (!s.MinHeight.IsAuto && !s.MinHeight.IsPercent)
        {
            float mh = s.MinHeight.Value - (s.BorderBox ? bt + bb + pt + pb : 0);
            if (contentH < mh) contentH = mh;
        }
        // aspect-ratio with an auto height: at least the ratio's height; taller content still makes it grow.
        if (!isImage && !isSvg && RatioHeight(s, contentW, frame, frameV, hasHOverride) is float ratioH && contentH < ratioH) contentH = ratioH;

        float bottom = cy + contentH + pb + bb;
        BoxPainter.Paint(group, s, bx, y, used, bottom - y, _images, availW);
        if (e.Control is { } control) FinishControl(e, control, opsStart + 1, bx, y, used, bottom - y, contentBottom - cy, contentH);
        if (e == CanvasRoot) CanvasRootBox = (bx, y, used, bottom - y);
        if (cbFrame != null)
        {
            _frames.Remove(cbFrame);
            LayoutAbsolutes(cbFrame, bx + bl, y + bt, used - bl - br, bottom - y - bt - bb);
        }
        if ((s.ClipsX || s.ClipsY) && !isImage && !isSvg && _ops.Count > opsStart + 1)
            WrapClip(opsStart + 1, s, bx, y, used, bottom - y);
        _lastBoxRight = bx + used;
        _lastBoxX = bx;
        _lastBoxTop = y;
        _lastBoxW = used;
        _lastBoxH = bottom - y;
        if (e.Name == "a" && e.GetAttribute("href") is { Length: > 0 } href) _ops.Add(new LinkOp(bx, y, used, bottom - y, href) { Link = Tags?.ElementOf(e) });
        if (Headings != null && HeadingLevel(e.Name) is int level and > 0) RecordHeading(e, level, y, opsStart);
        _boxTrailing = trailing;
        return bottom;
    }

    /// <summary>
    /// Moves the ops from <paramref name="start"/>, the content of the border box (x, y, w, h), into a clip to its
    /// padding box, rounded like its inner border edge.
    /// </summary>
    private void WrapClip(int start, ComputedStyle s, float x, float y, float w, float h)
    {
        float bl = s.BorderLeft, bt = s.BorderTop, br = s.BorderRight, bb = s.BorderBottom;
        float px = x + bl, py = y + bt, pw = Math.Max(0, w - bl - br), ph = Math.Max(0, h - bt - bb);
        // Rounded corners only clip when both axes do.
        var radii = s.ClipsX && s.ClipsY ? BoxPainter.InnerRadii(BoxPainter.Radii(s, w, h), bl, bt, br, bb) : null;
        if (!s.ClipsX) { px -= ClipOp.Far; pw += 2 * ClipOp.Far; }
        if (!s.ClipsY) { py -= ClipOp.Far; ph += 2 * ClipOp.Far; }
        var clip = new ClipOp(px, py, pw, ph, radii);
        for (int i = start; i < _ops.Count; i++) clip.Ops.Add(_ops[i]);
        Truncate(start, keepFloats: true);
        _ops.Add(clip);
        _floats.Fold(start);
    }

    /// <summary>Lays out the content box of a block container according to its display type.</summary>
    private float LayoutContent(Element e, float x, float y, float w, float? definiteH, float minH)
    {
        var s = e.Style;
        if (s.IsFlex) return LayoutFlex(e, x, y, w, definiteH, minH);
        if (s.IsGrid) return LayoutGrid(e, x, y, w, definiteH, minH);
        if (s.IsMultiColumn) return LayoutColumns(e, x, y, w);
        return LayoutFlow(e, x, y, w);
    }

    // ------------------------------------------------------------------ list markers

    private static string? MarkerText(ListStyleType type, int index)
        => type switch
        {
            ListStyleType.Decimal => index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".",
            ListStyleType.DecimalLeadingZero => index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".",
            ListStyleType.LowerAlpha => Counters.Alpha(index, 'a') + ".",
            ListStyleType.UpperAlpha => Counters.Alpha(index, 'A') + ".",
            ListStyleType.LowerRoman => Counters.Roman(index).ToLowerInvariant() + ".",
            ListStyleType.UpperRoman => Counters.Roman(index) + ".",
            _ => null,
        };

    private void EmitMarker(float baseline, float lineTop)
    {
        var m = _marker!;
        _marker = null;
        var s = m.Style;
        if (!s.Visible) return;
        float size = s.FontSize;
        if (m.Text != null)
        {
            var font = s.Font;
            float tw = font.Measure(m.Text, size);
            float gap = font.CharWidth(' ', size); // Chrome's marker text ends with a space: "1. "
            _ops.Add(new TextOp(m.ContentX - gap - tw, baseline, lineTop, font, size, s.Color, m.Text, 0, 0) { Tag = m.Tag });
            return;
        }
        // Chrome's symbol markers, in whole pixels from the font's rounded ascent: a square of side (2/3 ascent + 1) / 2,
        // 1px into a marker box that ends 1 + 7px (the marker padding) before the content.
        s.Font.LineMetrics(size, out float ascentPt, out _);
        int ascent = (int)MathF.Round(ascentPt / 0.75f), offset = ascent * 2 / 3, side = (offset + 1) / 2;
        float r = side * 0.75f / 2;
        float cxm = m.ContentX - (offset + 8 - 1) * 0.75f + r;
        float cym = baseline - (ascent - 3 * (ascent - offset) / 2) * 0.75f + r;
        switch (m.Type)
        {
            case ListStyleType.Disc:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: true));
                break;
            case ListStyleType.Circle:
                _ops.Add(new CircleOp(cxm, cym, r, s.Color, filled: false));
                break;
            case ListStyleType.Square:
                _ops.Add(new RectOp(cxm - r, cym - r, 2 * r, 2 * r, s.Color));
                break;
        }
    }

    // ------------------------------------------------------------------ images

    /// <summary>Computes the used size of an image in points. Returns the decoded image, or null when it could not be loaded.</summary>
    private PdfImage? ImageSize(Element e, float availW, out float w, out float h) => ImageSize(e, availW, out w, out h, out _);

    /// <summary>
    /// Computes the used size of an image in points. Returns the decoded raster image, or null with the drawing in
    /// <paramref name="svg"/> when the source is an SVG file, or null for both when it could not be loaded.
    /// </summary>
    private PdfImage? ImageSize(Element e, float availW, out float w, out float h, out SvgImage? svg)
    {
        var src = e.GetAttribute("src");
        var img = _images.Load(src);
        svg = img == null ? _images.LoadSvg(src) : null;
        float iw = img != null ? img.Width * 0.75f : 0, ih = img != null ? img.Height * 0.75f : 0;
        if (svg != null) (iw, ih) = SvgNaturalSize(svg, availW); // as for an inline svg element, as in Chrome
        ReplacedSize(e, iw, ih, availW, out w, out h);
        return img;
    }

    /// <summary>Computes the used size of an inline &lt;svg&gt; in points and returns its parsed drawing.</summary>
    private SvgImage? SvgSize(Element e, float availW, out float w, out float h)
    {
        if (!_images.Svgs.TryGetValue(e, out var svg)) _images.Svgs[e] = svg = SvgImage.Build(e, _images.SvgBudget);
        var (iw, ih) = SvgNaturalSize(svg, availW);
        ReplacedSize(e, iw, ih, availW, out w, out h);
        return svg;
    }

    /// <summary>The size of an SVG drawing before CSS sizes it, in points.</summary>
    private (float W, float H) SvgNaturalSize(SvgImage svg, float availW)
    {
        if (svg.Width is { } sw && svg.Height is { } sh) return (sw, sh);
        if (svg.HasViewBox && svg.ViewBoxWidth > 0 && svg.ViewBoxHeight > 0)
        {
            // Only a viewBox: as wide as the container (or a fixed width), with the viewBox's aspect ratio.
            float ratio = svg.ViewBoxHeight / svg.ViewBoxWidth;
            if (svg.Width is { } w1) return (w1, w1 * ratio);
            if (svg.Height is { } h1) return (h1 / ratio, h1);
            float iw = _measuring || availW <= 0 || availW > 50000 ? 225 : availW;
            return (iw, iw * ratio);
        }
        return (svg.Width ?? 225, svg.Height ?? 112.5f); // 300 x 150 px, as in browsers
    }

    /// <summary>Whether aspect-ratio sizes the border box (box-sizing: border-box without "auto") rather than the content box.</summary>
    private static bool RatioOnBorderBox(ComputedStyle s) => s.BorderBox && !s.AspectRatioAuto;

    /// <summary>The content height aspect-ratio gives a box with an auto height and the given content width, or null.</summary>
    private static float? RatioHeight(ComputedStyle s, float contentW, float frame, float frameV, bool hasHOverride)
    {
        // Percentage heights are treated as auto here, as elsewhere in block layout.
        if (s.AspectRatio <= 0 || hasHOverride || !s.Height.IsAuto && !s.Height.IsPercent) return null;
        float h = RatioOnBorderBox(s) ? (contentW + frame) / s.AspectRatio - frameV : contentW / s.AspectRatio;
        return Math.Max(0, h);
    }

    /// <summary>
    /// Draws an image into its content box (x, y, w, h), sized and placed by object-fit and object-position, and clipped
    /// to the box (and its rounded corners) where it reaches outside.
    /// </summary>
    private void AddImage(ComputedStyle s, PdfImage image, float x, float y, float w, float h, CornerRadii? radii, ContentSlot? tag)
    {
        if (s.ObjectFit == ObjectFit.Fill || image.Width <= 0 || image.Height <= 0 || w <= 0 || h <= 0)
        {
            _ops.Add(new ImageOp(x, y, w, h, image) { Radii = radii, Tag = tag });
            return;
        }
        float nw = image.Width * 0.75f, nh = image.Height * 0.75f;
        float contain = Math.Min(w / nw, h / nh);
        float k = s.ObjectFit switch
        {
            ObjectFit.Contain => contain,
            ObjectFit.Cover => Math.Max(w / nw, h / nh),
            ObjectFit.ScaleDown => Math.Min(1, contain),
            _ => 1, // none
        };
        float dw = nw * k, dh = nh * k;
        float dx = x + s.ObjectPositionX.Resolve(w - dw), dy = y + s.ObjectPositionY.Resolve(h - dh);
        var op = new ImageOp(dx, dy, dw, dh, image) { Tag = tag };
        if (radii == null && dx >= x - Eps && dy >= y - Eps && dx + dw <= x + w + Eps && dy + dh <= y + h + Eps)
        {
            _ops.Add(op);
            return;
        }
        var clip = new ClipOp(x, y, w, h, radii);
        clip.Ops.Add(op);
        _ops.Add(clip);
    }

    /// <summary>Used size of a replaced element from its intrinsic size and its CSS width, height and limits.</summary>
    private void ReplacedSize(Element e, float iw, float ih, float availW, out float w, out float h)
    {
        var s = e.Style;
        bool hasW = !s.Width.IsAuto && !(_measuring && s.Width.IsPercent), hasH = !s.Height.IsAuto && !s.Height.IsPercent;
        w = hasW ? s.Width.Resolve(Math.Max(availW, 0)) : 0;
        h = hasH ? s.Height.Value : 0;
        if (s.BorderBox)
        {
            if (hasW) w -= s.PaddingLeft.Resolve(availW) + s.PaddingRight.Resolve(availW) + s.BorderLeft + s.BorderRight;
            if (hasH) h -= s.PaddingTop.Resolve(availW) + s.PaddingBottom.Resolve(availW) + s.BorderTop + s.BorderBottom;
        }
        // aspect-ratio replaces the natural ratio, unless it says "auto" and the element has one.
        bool ratioOverride = s.AspectRatio > 0 && !(s.AspectRatioAuto && iw > 0 && ih > 0);
        float rw = ratioOverride ? s.AspectRatio : iw, rh = ratioOverride ? 1 : ih;
        if (hasW && !hasH) h = rw > 0 ? w * rh / rw : 0;
        else if (!hasW && hasH) w = rh > 0 ? h * rw / rh : 0;
        else if (!hasW && !hasH) { w = iw; h = ratioOverride ? iw / s.AspectRatio : ih; }

        if (!s.MaxWidth.IsAuto)
        {
            float mw = s.MaxWidth.Resolve(availW);
            if (w > mw && w > 0) { h *= mw / w; w = mw; }
        }
        if (!s.MaxHeight.IsAuto && !s.MaxHeight.IsPercent && h > s.MaxHeight.Value && h > 0)
        {
            w *= s.MaxHeight.Value / h;
            h = s.MaxHeight.Value;
        }
        // Never let an image run off the page.
        if (availW > 0 && w > availW) { h *= availW / w; w = availW; }
        if (_paginate && h > _pageH) { w *= _pageH / h; h = _pageH; }
        if (w < 0) w = 0;
        if (h < 0) h = 0;
    }

    // ------------------------------------------------------------------ anchors, captured layout, size overrides

    private List<string>? _captureAnchors;
    private int _forcedBreaks; // forced page breaks seen; captured content without any can be reused when it fits on a page
    private readonly Dictionary<Element, float> _widthOverride = new();
    private readonly Dictionary<Element, float> _heightOverride = new();

    /// <summary>Headings in document order for the PDF outline, or null when no outline is wanted. Each heading's
    /// position is kept in <see cref="Anchors"/> under its key, so it follows the heading when content is moved.</summary>
    public List<OutlineHeading>? Headings;
    private Dictionary<Element, string>? _headingKeys;

    private static int HeadingLevel(string name)
        => name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6' ? name[1] - '0' : 0;

    private void RecordHeading(Element e, int level, float top, int opsStart)
    {
        _headingKeys ??= new Dictionary<Element, string>();
        if (!_headingKeys.TryGetValue(e, out var key))
        {
            var title = CollapseWhitespace(e.InnerText());
            if (title.Length == 0) return;
            key = "\u0001h" + Headings!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _headingKeys[e] = key;
            Headings.Add(new OutlineHeading(title, level, key));
        }
        // Point at the first line of text, which may have moved to the next page.
        float y = top;
        if (_paginate)
            for (int i = opsStart; i < _ops.Count; i++)
                if (FirstContentY(_ops[i]) is { } fy) { if (PageOf(fy) > PageOf(top)) y = fy; break; }
        SetAnchor(key, y);
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void SetAnchor(string id, float y)
    {
        Anchors[id] = y;
        _captureAnchors?.Add(id);
    }

    /// <summary>Content laid out on its own (unpaginated, at the origin) so that it can be measured and then moved into place.</summary>
    private sealed class Captured
    {
        public List<DrawOp> Ops = null!;
        public List<string>? AnchorIds;
        public float Width, Height; // margin box
        public float Baseline = float.NaN;
        public bool ForcedBreaks;
        public float BoxX, BoxY, BoxW, BoxH; // border box of the outermost box laid out
        public List<(Element E, float X, float Y)>? Absolutes; // absolutely positioned boxes still waiting for their containing block
    }

    private Captured Capture(Func<float> layout)
    {
        var savedOps = _ops;
        bool savedPaginate = _paginate;
        var savedMarker = _marker;
        float savedBaseline = _lastBaseline;
        var savedAnchors = _captureAnchors;
        float savedRight = _lastBoxRight;
        var savedFloats = EnterFormattingContext();
        var boundary = _boundaryPool.TryPop(out var pooled) ? pooled : new CbFrame { Boundary = true };
        _frames.Add(boundary);
        _lastBoxX = _lastBoxTop = _lastBoxW = _lastBoxH = 0;
        _ops = new List<DrawOp>();
        _paginate = false;
        _marker = null;
        _lastBaseline = float.NaN;
        _captureAnchors = null;
        _lastBoxRight = 0;
        int breaks = _forcedBreaks;
        try
        {
            float bottom = layout();
            if (_floats.Any && _floats.Bottom > bottom) bottom = _floats.Bottom;
            var absolutes = boundary.Pending.Count > 0 ? boundary.TakePending() : null;
            return new Captured
            {
                Ops = _ops, AnchorIds = _captureAnchors, Height = bottom, Width = _lastBoxRight, Baseline = _lastBaseline,
                ForcedBreaks = _forcedBreaks != breaks,
                BoxX = _lastBoxX, BoxY = _lastBoxTop, BoxW = _lastBoxW, BoxH = _lastBoxH,
                Absolutes = absolutes,
            };
        }
        finally
        {
            _frames.Remove(boundary);
            boundary.TakePending();
            _boundaryPool.Push(boundary);
            LeaveFormattingContext(savedFloats, 0);
            _ops = savedOps;
            _paginate = savedPaginate;
            _marker = savedMarker;
            _lastBaseline = savedBaseline;
            _captureAnchors = savedAnchors;
            _lastBoxRight = savedRight;
        }
    }

    /// <summary>Lays out a block-level box in a box of <paramref name="outerW"/> (its margin box), optionally with a forced border-box size.</summary>
    private Captured CaptureBox(Element e, float outerW, bool shrinkToFit, float? borderW = null, float? borderH = null)
    {
        var s = e.Style;
        if (borderW.HasValue) _widthOverride[e] = borderW.Value;
        if (borderH.HasValue) _heightOverride[e] = borderH.Value;
        try
        {
            float mt = s.MarginTop.IsAuto ? 0 : s.MarginTop.Resolve(outerW), mb = s.MarginBottom.IsAuto ? 0 : s.MarginBottom.Resolve(outerW);
            float mr = s.MarginRight.IsAuto ? 0 : s.MarginRight.Resolve(outerW);
            var c = Capture(() => LayoutBlockBox(e, 0, mt, outerW, shrinkToFit) + mb);
            c.Width += mr;
            return c;
        }
        finally
        {
            if (borderW.HasValue) _widthOverride.Remove(e);
            if (borderH.HasValue) _heightOverride.Remove(e);
        }
    }

    /// <summary>Lays out a run of inline content (an anonymous block) at width <paramref name="w"/>.</summary>
    private Captured CaptureRun(List<Node> run, Element container, float w)
    {
        var c = Capture(() => FlushInline(run, container, 0, 0, w, true, out _));
        c.Width = w;
        return c;
    }

    /// <summary>
    /// Whether content captured without pagination can be used as is at <paramref name="top"/>: pagination only changes
    /// a layout when something crosses a page boundary or forces a break.
    /// </summary>
    private bool Reusable(Captured c, float top) => !_paginate || !c.ForcedBreaks && !CrossesPage(top, top + c.Height);

    /// <summary>Moves captured content to its final position and appends it to the output.</summary>
    private void Place(Captured c, float dx, float dy)
    {
        foreach (var op in c.Ops)
        {
            op.Offset(dx, dy);
            _ops.Add(op);
        }
        if (c.AnchorIds != null)
            foreach (var id in c.AnchorIds)
                if (Anchors.TryGetValue(id, out float ay)) SetAnchor(id, ay + dy);
        if (c.Absolutes != null)
            foreach (var (e, ax, ay) in c.Absolutes) QueueAbsolute(e, ax + dx, ay + dy);
    }
}

/// <summary>A heading that becomes a PDF bookmark: its text, level (1-6) and the key of its position in the anchors.</summary>
internal readonly record struct OutlineHeading(string Title, int Level, string Key);

using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Svg;

namespace PdfPecker.Layout;

/// <summary>
/// Collecting a run of inline content into items: text nodes become words and spaces, inline elements add their
/// padding and anchors around their children, and images, inline SVG and inline-blocks become boxes.
/// </summary>
internal sealed partial class LayoutEngine
{
    /// <summary>The items of a run of inline content. Hand the state back with <see cref="ReturnState"/> when done.</summary>
    private CollectState CollectInline(List<Node> run, float availW, FirstLineStyle? firstLine = null)
    {
        var st = RentState();
        st.AvailWidth = availW;
        st.FirstLine = firstLine;
        foreach (var node in run) CollectNode(st, node, null, null);
        st.FirstLine = null;
        TrimAdjacentPunctuation(st.Items);
        return st;
    }

    private void CollectNode(CollectState st, Node node, string? href, InlineDeco? deco)
    {
        if (node is TextNode t)
        {
            // Text from a display: contents link still links: the link is its parent, or a parent of that.
            if (href == null && t.Parent is { Style.Contents: true } owner) href = ContentsLink(owner);
            CollectTextNode(st, t, href, deco);
            return;
        }
        var e = (Element)node;
        var s = e.Style;
        if (s == null || s.Display == Display.None || e.Detached) return;

        if (s.Floated || s.OutOfFlow)
        {
            AddOutOfFlow(st, e, s, deco);
            return;
        }

        int before = st.Items.Count;
        href ??= e.ContentsHref;
        AddAnchors(st, e, s, ref href);

        if (IsImageLaidOutAsBox(e, s))
        {
            // A transformed, translucent, relatively positioned, rounded or object-fit image is laid out as a box of its
            // own, which carries the layer or fits and clips the image.
            st.Items.Add(LayoutInlineBlock(e, st.AvailWidth, href ?? LinkOf(e)));
            st.LastWasSpace = false;
            AddAtomicBreaks(st, e, before, st.Items.Count - 1);
            return;
        }

        switch (e.Name)
        {
            case "br":
                AddBreak(st, s);
                return;
            case "svg":
            {
                var svg = SvgSize(e, st.AvailWidth, out float sw, out float sh);
                int at = st.Items.Count;
                AddInlineSvg(st, e, s, svg, sw, sh, href);
                AddAtomicBreaks(st, e, before, at);
                return;
            }
            case "img" when e.AltText == null:
            {
                int at = st.Items.Count;
                AddInlineImage(st, e, s, href);
                AddAtomicBreaks(st, e, before, at);
                return;
            }
        }

        if (e.Name == "a" && e.GetAttribute("href") is { } link && link.Length > 0) href = link;

        if (s.IsAtomicInline)
        {
            st.Items.Add(LayoutInlineBlock(e, st.AvailWidth, href));
            st.LastWasSpace = false;
            AddAtomicBreaks(st, e, before, st.Items.Count - 1);
            return;
        }

        CollectInlineElement(st, e, s, href, deco);
    }

    /// <summary>
    /// The line break opportunities around an atomic inline (an image, an inline svg or an inline-block) whose items
    /// start at <paramref name="at"/>, after the element's anchors from <paramref name="before"/>. As CSS Text asks and
    /// Chrome does, a line may break before and after one even next to punctuation or a no-break space, so text that
    /// runs into it stays on its line when the box moves down. The anchors go with the box.
    /// </summary>
    private static void AddAtomicBreaks(CollectState st, Element e, int before, int at)
    {
        if (st.Items.Count <= at || e.Parent?.Style is not { } parent || !IsBreakable(parent)) return;
        if (before > 0 && st.Items[before - 1] is { Kind: not (ItemKind.Space or ItemKind.Break), BreakAfter: BreakAfter.None } prev)
            prev.BreakAfter = BreakAfter.Break;
        st.Items[^1].BreakAfter = BreakAfter.Break;
    }

    private void CollectTextNode(CollectState st, TextNode t, string? href, InlineDeco? deco)
    {
        if (Tags != null) _textTag = Tags.Of(t);
        var parent = t.Parent!;
        if (parent.Generated && t.Text.IndexOf(GeneratedContent.LeaderStart) >= 0) AddWithLeaders(st, t.Text, parent.Style, href, deco);
        else AddText(st, t.Text, st.FirstLine is { } fl ? fl.Map(parent.Style) : parent.Style, href, deco);
        _textTag = null;
    }

    /// <summary>A float or an absolutely positioned box met in the text: an item that places it when its line is placed.</summary>
    private void AddOutOfFlow(CollectState st, Element e, ComputedStyle s, InlineDeco? deco)
    {
        var oof = Rent(s.Floated ? ItemKind.Float : ItemKind.Absolute, s);
        oof.Element = e;
        // An absolutely positioned box inside a positioned inline element is placed against that element's fragments.
        if (!s.Floated && s.Position == Position.Absolute && PositionedDeco(deco) is { } cb)
        {
            oof.Deco = cb;
            cb.HoldsAbsolutes = true;
        }
        if (_measuring && s.Floated)
        {
            // Measuring: a float takes its width on the line, as it would beside the text.
            var (fmin, fmax) = Intrinsic(e);
            float margins = FixedHorizontalMargins(s);
            oof.Width = fmax + margins;
            oof.FloatMin = fmin + margins;
        }
        st.Items.Add(oof);
    }

    /// <summary>The anchors an element starts with: its id, name, layout key and, for a footnote call, the call's key.</summary>
    private void AddAnchors(CollectState st, Element e, ComputedStyle s, ref string? href)
    {
        if (e.Id != null) AddAnchor(st, s, e.Id);
        if (e.Name == "a" && e.GetAttribute("name") is { } aname) AddAnchor(st, s, aname);
        if (e.LayoutKey != null) AddAnchor(st, s, e.LayoutKey);
        if (e.FootnoteBody is { FootnoteIndex: >= 0 } footnote)
        {
            // A footnote call: its place decides the footnote's page, and it links to the footnote.
            AddAnchor(st, s, FootnoteSetup.CallKey(footnote.FootnoteIndex));
            href = "#" + FootnoteSetup.BodyKey(footnote.FootnoteIndex);
        }
    }

    /// <summary>
    /// Whether an image or inline SVG needs a box of its own: a layer or a vertical margin, or for an image a radius, object-fit, outline, or
    /// a box it paints or pads (borders, padding, a background or shadows), which a plain image item has no room for.
    /// </summary>
    private static bool IsImageLaidOutAsBox(Element e, ComputedStyle s) =>
        e.IsReplaced
        && (s.Layered || HasVerticalMargin(s) || e.IsPicture && (s.BorderRadii != null || s.ObjectFit != ObjectFit.Fill || s.OutlineStyle != BorderStyle.None
            || PaintsInlineBox(s) || HasPadding(s.PaddingTop) || HasPadding(s.PaddingRight) || HasPadding(s.PaddingBottom)
            || HasPadding(s.PaddingLeft)));

    private static bool HasPadding(Length padding) => padding.IsPercent || padding.Resolve(0) > 0;

    /// <summary>Whether a vertical margin is set, which a plain image or svg item has no room for.</summary>
    private static bool HasVerticalMargin(ComputedStyle s) => HasMargin(s.MarginTop) || HasMargin(s.MarginBottom);

    private static bool HasMargin(Length margin) => !margin.IsAuto && (margin.IsPercent || margin.Resolve(0) != 0);

    /// <summary>An inline element: its padding and borders as spacers around its children, with its bidi controls.</summary>
    private void CollectInlineElement(CollectState st, Element e, ComputedStyle s, string? href, InlineDeco? deco)
    {
        float boxLeft = s.PaddingLeft.Resolve(0) + s.BorderLeft, boxRight = s.PaddingRight.Resolve(0) + s.BorderRight;
        var outer = deco;
        bool paints = PaintsInlineBox(s) || s.BoxDecorationClone && (boxLeft > 0 || boxRight > 0);
        if (paints || s.Positioned) deco = new InlineDeco(e, s, outer, boxLeft, boxRight) { Paints = paints };
        float marginLeft = AutoAsZero(s.MarginLeft), marginRight = AutoAsZero(s.MarginRight);
        int first = st.Items.Count;
        var (bidiOpen, bidiClose) = BidiControls(s);
        if (bidiOpen != null) AddBidiControl(st, s, bidiOpen);
        AddInlineEdge(st, s, boxLeft, marginLeft, href, deco, outer, marginFirst: true);
        foreach (var c in e.Children) CollectNode(st, c, href, deco);
        AddInlineEdge(st, s, boxRight, marginRight, href, deco, outer, marginFirst: false);
        if (bidiClose != null) AddBidiControl(st, s, bidiClose);
        // A positioned element that paints nothing and holds no absolutely positioned box needs no fragments.
        if (deco is { Paints: false, HoldsAbsolutes: false } && !ReferenceEquals(deco, outer))
            for (int k = first; k < st.Items.Count; k++)
                if (ReferenceEquals(st.Items[k].Deco, deco)) st.Items[k].Deco = outer;
        if (s.Position == Position.Relative) ShiftRelative(st, first, s);
    }

    /// <summary>The nearest positioned inline element of a deco's chain, which is the containing block of absolutely positioned boxes in it.</summary>
    private static InlineDeco? PositionedDeco(InlineDeco? deco)
    {
        while (deco != null && !deco.Style.Positioned) deco = deco.Parent;
        return deco;
    }

    /// <summary>Whether an inline element paints anything of its own box: a background, shadows, borders or an outline.</summary>
    private static bool PaintsInlineBox(ComputedStyle s) =>
        CssColor.IsVisible(s.BackgroundColor) || s.BackgroundImages != null || s.BoxShadows != null || s.HasBorder
        || s.OutlineStyle != BorderStyle.None;

    /// <summary>
    /// The spacer of one side of an inline element: its padding and border, and its margin outside them. A margin of an
    /// element that paints its box is a spacer of its own that the element's background leaves out; a negative margin
    /// stays one spacer with the padding, so that the element still takes the room it did.
    /// </summary>
    private void AddInlineEdge(CollectState st, ComputedStyle s, float box, float margin, string? href, InlineDeco? deco, InlineDeco? outer,
        bool marginFirst)
    {
        if (!(box + margin > 0)) return;
        if (margin <= 0 || ReferenceEquals(deco, outer))
        {
            AddSpacer(st, s, box + margin, href, deco);
            return;
        }
        if (marginFirst) AddSpacer(st, s, margin, href, outer);
        if (box > 0) AddSpacer(st, s, box, href, deco);
        if (!marginFirst) AddSpacer(st, s, margin, href, outer);
    }

    /// <summary>
    /// Shifts the items from <paramref name="first"/> on by an inline box's position: relative; percentages of the
    /// containing block's height are ignored.
    /// </summary>
    private static void ShiftRelative(CollectState st, int first, ComputedStyle s)
    {
        float dx = RelativeOffset(s.Left, s.Right);
        float dy = RelativeOffset(s.Top, s.Bottom);
        for (int k = first; k < st.Items.Count; k++)
        {
            st.Items[k].RelX += dx;
            st.Items[k].RelY += dy;
        }
    }

    /// <summary>The offset position: relative gives along one axis: the start side when set, else the end side negated.</summary>
    private static float RelativeOffset(Length start, Length end) =>
        !start.IsAuto ? (start.IsPercent ? 0 : start.Value) : !end.IsAuto ? -(end.IsPercent ? 0 : end.Value) : 0;

    private static float AutoAsZero(Length margin) => margin.IsAuto ? 0 : margin.Resolve(0);

    /// <summary>The left and right margins that are neither auto nor percentages, which is all that counts when measuring.</summary>
    private static float FixedHorizontalMargins(ComputedStyle s) =>
        (s.MarginLeft.IsAuto || s.MarginLeft.IsPercent ? 0 : s.MarginLeft.Value)
        + (s.MarginRight.IsAuto || s.MarginRight.IsPercent ? 0 : s.MarginRight.Value);

    private static string? LinkOf(Element e)
    {
        if (e.ContentsHref != null) return e.ContentsHref;
        for (var p = e.Parent; p != null; p = p.Parent)
        {
            if (p.Name == "a" && p.GetAttribute("href") is { Length: > 0 } h) return h;
            if (p.ContentsHref != null) return p.ContentsHref;
        }
        return null;
    }

    /// <summary>The link around text in a display: contents element: the nearest such element that is a link.</summary>
    private static string? ContentsLink(Element owner)
    {
        for (Element? p = owner; p is { Style.Contents: true }; p = p.Parent)
        {
            if (p.Name == "a" && p.GetAttribute("href") is { Length: > 0 } h) return h;
            if (p.ContentsHref != null) return p.ContentsHref;
        }
        return null;
    }

    // ------------------------------------------------------------------ replaced boxes

    /// <summary>An img element, with its margins as spacers; an SVG file in it becomes an inline box.</summary>
    private void AddInlineImage(CollectState st, Element e, ComputedStyle s, string? href)
    {
        var img = ImageSize(e, st.AvailWidth, out float w, out float h, out var svgFile);
        if (svgFile != null)
        {
            AddInlineSvg(st, e, s, svgFile, w, h, href, file: true);
            return;
        }
        if (!(w > 0 && h > 0)) return;
        float ml = AutoAsZero(s.MarginLeft), mr = AutoAsZero(s.MarginRight);
        if (ml > 0) AddSpacer(st, s, ml, href);
        var image = Rent(ItemKind.Image, s);
        image.Image = s.Visible ? img : null;
        image.Width = w;
        image.Height = h;
        // A percentage width or max-width makes the image compressible (CSS Sizing 3 §5.2.2): as in Chrome, it has no
        // min-content width, so it does not hold a table column or a shrink-to-fit box open at its natural width.
        if (_measuring && IsCompressible(s)) image.MinWidth = 0;
        image.PageExcess = _replacedExcess;
        image.Href = href ?? LinkOf(e);
        image.Tag = Tags?.Of(e);
        st.Items.Add(image);
        if (mr > 0) AddSpacer(st, s, mr, href);
        st.LastWasSpace = false;
    }

    /// <summary>Whether a replaced element's width depends on its containing block, which leaves it no min-content width.</summary>
    private static bool IsCompressible(ComputedStyle s) => s.Width.IsPercent || s.MaxWidth.IsPercent;

    /// <summary>
    /// Adds an SVG drawing (an inline svg element, or an SVG file in an img element) as a small inline box, so padding
    /// and borders work as for any replaced element.
    /// </summary>
    private void AddInlineSvg(CollectState st, Element e, ComputedStyle s, SvgImage? svg, float sw, float sh, string? href, bool file = false)
    {
        if (!(sw > 0 && sh > 0)) return;
        float ml = AutoAsZero(s.MarginLeft), mr = AutoAsZero(s.MarginRight);
        float pl = s.PaddingLeft.Resolve(0) + s.BorderLeft, pr = s.PaddingRight.Resolve(0) + s.BorderRight;
        float ptop = s.PaddingTop.Resolve(0) + s.BorderTop, pbot = s.PaddingBottom.Resolve(0) + s.BorderBottom;
        float boxW = sw + pl + pr, boxH = sh + ptop + pbot;
        var ops = new List<DrawOp>();
        var group = new GroupOp { Reach = boxW + _replacedExcess };
        ops.Add(group);
        BoxPainter.Paint(group, s, 0, 0, boxW, boxH, _images);
        if (s.Visible && svg != null)
        {
            if (file)
            {
                // An SVG file in an img element: sized and placed by object-fit and object-position.
                var radii = BoxPainter.InnerRadii(BoxPainter.Radii(s, boxW, boxH), pl, ptop, pr, pbot);
                AddSvgImage(ops, s, svg, pl, ptop, sw, sh, radii, Tags?.Of(e));
            }
            else ops.Add(new SvgOp(pl, ptop, sw, sh, svg) { Tag = Tags?.Of(e) });
        }
        if (ml > 0) AddSpacer(st, s, ml, href);
        st.Items.Add(new Item
        {
            Kind = ItemKind.Box, Style = s, Width = boxW, Height = boxH, BoxBaseline = boxH, BoxOps = ops, Href = href ?? LinkOf(e),
            MinWidth = _measuring && IsCompressible(s) ? pl + pr : -1,
        });
        if (mr > 0) AddSpacer(st, s, mr, href);
        st.LastWasSpace = false;
    }

    /// <summary>
    /// Each inline-block laid out while measuring intrinsic widths, by element and available width: its size, and the
    /// anchors and positioned boxes it passes on to the content around it, without its drawing.
    /// </summary>
    private readonly Dictionary<(Element, float), Captured> _measuredInlineBlocks = new();

    /// <summary>An inline-block (or an image laid out as a box): laid out on its own and carried as one item.</summary>
    private Item LayoutInlineBlock(Element e, float availW, string? href)
    {
        Captured c;
        if (!_measuring) c = CaptureBox(e, availW, shrinkToFit: true);
        else if (_measuredInlineBlocks.TryGetValue((e, MeasuredWidthKey(e, availW)), out var measured)) c = measured;
        else
        {
            // Measuring an element's intrinsic widths lays out the inline-blocks in it, and laying out one of those
            // measures the inline-blocks inside it first and then lays them out again, so an inline-block nested n deep
            // was laid out once for every level above it, and its drawing was copied up through every level each time.
            // Measurement is unpaginated and its drawing is thrown away, so the same element in the same width always
            // gets the same size: it is laid out once, and later measurements reuse that without drawing anything.
            var laid = CaptureBox(e, availW, shrinkToFit: true);
            _measuredInlineBlocks[(e, MeasuredWidthKey(e, availW))] = new Captured
            {
                Ops = [], AnchorIds = laid.AnchorIds, Width = laid.Width, Height = laid.Height, Baseline = laid.Baseline,
                ForcedBreaks = laid.ForcedBreaks, BoxX = laid.BoxX, BoxY = laid.BoxY, BoxW = laid.BoxW, BoxH = laid.BoxH,
                Absolutes = laid.Absolutes,
            };
            c = laid;
        }
        float baseline = float.IsNaN(c.Baseline) ? c.Height : c.Baseline;
        // Chrome puts a check box's or radio button's baseline at the bottom of its border box, so its bottom margin
        // hangs below the baseline instead of raising the box and making the line taller.
        if (e.Control is { Kind: ControlKind.CheckBox or ControlKind.Radio } && float.IsNaN(c.Baseline))
            baseline -= AutoAsZero(e.Style.MarginBottom);
        var item = new Item { Kind = ItemKind.Box, Style = e.Style, Width = c.Width, Height = c.Height, BoxBaseline = baseline, Capture = c, Href = href };
        if (_measuring)
        {
            // An inline-block can wrap its own content, so its min-content contribution is its min-content width,
            // not the width it takes when laid out at the (very wide) measuring width.
            var (min, max) = Intrinsic(e);
            float margins = FixedHorizontalMargins(e.Style);
            // A percentage width has nothing to resolve against here, so the box counts with the width it would have
            // with an auto width, as in Chrome, not with a share of the very wide measuring width.
            if (e.Style.Width.IsPercent) item.Width = max + margins;
            item.MinWidth = Math.Min(item.Width, min + margins);
        }
        return item;
    }

    /// <summary>
    /// The available width a measured inline-block is kept under. The box shrinks to its max-content width wherever
    /// that fits, so when its own width, margins and padding do not depend on the available width either, every width
    /// with room for it gives the same layout, and they share one entry. Otherwise each level of nested inline-blocks
    /// wider than the measuring width offers the boxes inside a slightly narrower width, and they would be laid out
    /// again for every level. Only a box whose intrinsic widths are known is shared, so that measuring them still
    /// happens where the layout does it.
    /// </summary>
    private float MeasuredWidthKey(Element e, float availW)
    {
        var s = e.Style;
        if (e.IsReplaced || !_intrinsicCache.TryGetValue(e, out var intrinsic)) return availW;
        if (s.Width.IsPercent || s.MinWidth.IsPercent || s.MaxWidth.IsPercent
            || s.MarginLeft.IsAuto || s.MarginLeft.IsPercent || s.MarginRight.IsAuto || s.MarginRight.IsPercent
            || s.MarginTop.IsPercent || s.MarginBottom.IsPercent
            || s.PaddingLeft.IsPercent || s.PaddingRight.IsPercent || s.PaddingTop.IsPercent || s.PaddingBottom.IsPercent)
            return availW;
        return availW - s.MarginLeft.Value - s.MarginRight.Value >= intrinsic.Max || availW >= UnboundedMeasure
            ? float.PositiveInfinity : availW;
    }

    /// <summary>
    /// The available width from which a measured inline-block counts as unbounded. Intrinsic widths are measured at
    /// 100,000 points, and each level of nested inline-blocks offers the boxes inside a little less, so a box whose
    /// content is wider still wraps at a width that differs only by the frames around it. No page is that wide, so the
    /// difference never reaches the layout, but measuring it at each width laid out every box once for each level above
    /// it, and each of those layouts did the same inside, which grew with the cube of the depth.
    /// </summary>
    private const float UnboundedMeasure = 50_000;

    // ------------------------------------------------------------------ leaders

    /// <summary>Generated text with leaders (leader() in content): the text around them, and a leader item for each.</summary>
    private void AddWithLeaders(CollectState st, string text, ComputedStyle style, string? href, InlineDeco? deco)
    {
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf(GeneratedContent.LeaderStart, i);
            int end = start < 0 ? -1 : text.IndexOf(GeneratedContent.LeaderEnd, start + 1);
            if (start < 0 || end < 0)
            {
                AddText(st, text.Substring(i), style, href, deco);
                return;
            }
            if (start > i) AddText(st, text.Substring(i, start - i), style, href, deco);
            var leader = Rent(ItemKind.Leader, style);
            leader.Source = text.Substring(start + 1, end - start - 1);
            leader.Length = leader.Source.Length;
            leader.Href = href;
            leader.Deco = deco;
            st.Items.Add(leader);
            st.LastWasSpace = false;
            i = end + 1;
        }
    }
}

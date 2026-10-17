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
        return st;
    }

    private void CollectNode(CollectState st, Node node, string? href, ComputedStyle? deco)
    {
        if (node is TextNode t)
        {
            CollectTextNode(st, t, href, deco);
            return;
        }
        var e = (Element)node;
        var s = e.Style;
        if (s == null || s.Display == Display.None || e.Detached) return;

        if (s.Floated || s.OutOfFlow)
        {
            AddOutOfFlow(st, e, s);
            return;
        }

        AddAnchors(st, e, s, ref href);

        if (IsImageLaidOutAsBox(e, s))
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
                AddBreak(st, s);
                return;
            case "svg":
            {
                var svg = SvgSize(e, st.AvailWidth, out float sw, out float sh);
                AddInlineSvg(st, e, s, svg, sw, sh, href);
                return;
            }
            case "img":
                AddInlineImage(st, e, s, href);
                return;
        }

        if (e.Name == "a" && e.GetAttribute("href") is { } link && link.Length > 0) href = link;

        if (IsAtomicInline(s.Display))
        {
            st.Items.Add(LayoutInlineBlock(e, st.AvailWidth, href));
            st.LastWasSpace = false;
            return;
        }

        CollectInlineElement(st, e, s, href, deco);
    }

    private void CollectTextNode(CollectState st, TextNode t, string? href, ComputedStyle? deco)
    {
        if (Tags != null) _textTag = Tags.Of(t);
        var parent = t.Parent!;
        if (parent.Generated && t.Text.IndexOf(GeneratedContent.LeaderStart) >= 0) AddWithLeaders(st, t.Text, parent.Style, href, deco);
        else AddText(st, t.Text, st.FirstLine is { } fl ? fl.Map(parent.Style) : parent.Style, href, deco);
        _textTag = null;
    }

    /// <summary>A float or an absolutely positioned box met in the text: an item that places it when its line is placed.</summary>
    private void AddOutOfFlow(CollectState st, Element e, ComputedStyle s)
    {
        var oof = Rent(s.Floated ? ItemKind.Float : ItemKind.Absolute, s);
        oof.Element = e;
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

    /// <summary>Whether an image or inline SVG needs a box of its own: a layer, or for an image a radius, object-fit or outline.</summary>
    private static bool IsImageLaidOutAsBox(Element e, ComputedStyle s) =>
        e.Name is "img" or "svg"
        && (s.Layered || e.Name == "img" && (s.BorderRadii != null || s.ObjectFit != ObjectFit.Fill || s.OutlineStyle != BorderStyle.None));

    /// <summary>An inline element: its padding and borders as spacers around its children, with its bidi controls.</summary>
    private void CollectInlineElement(CollectState st, Element e, ComputedStyle s, string? href, ComputedStyle? deco)
    {
        if (CssColor.IsVisible(s.BackgroundColor) || s.BoxShadows != null || s.OutlineStyle != BorderStyle.None) deco = s;
        float left = s.PaddingLeft.Resolve(0) + s.BorderLeft + AutoAsZero(s.MarginLeft);
        float right = s.PaddingRight.Resolve(0) + s.BorderRight + AutoAsZero(s.MarginRight);
        int first = st.Items.Count;
        var (bidiOpen, bidiClose) = BidiControls(s);
        if (bidiOpen != null) AddBidiControl(st, s, bidiOpen);
        if (left > 0) AddSpacer(st, s, left, href, deco);
        foreach (var c in e.Children) CollectNode(st, c, href, deco);
        if (right > 0) AddSpacer(st, s, right, href, deco);
        if (bidiClose != null) AddBidiControl(st, s, bidiClose);
        if (s.Position == Position.Relative) ShiftRelative(st, first, s);
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
        for (var p = e.Parent; p != null; p = p.Parent)
            if (p.Name == "a" && p.GetAttribute("href") is { Length: > 0 } h) return h;
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
        image.Href = href ?? LinkOf(e);
        image.Tag = Tags?.Of(e);
        st.Items.Add(image);
        if (mr > 0) AddSpacer(st, s, mr, href);
        st.LastWasSpace = false;
    }

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
        var group = new GroupOp();
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
        st.Items.Add(new Item { Kind = ItemKind.Box, Style = s, Width = boxW, Height = boxH, BoxBaseline = boxH, BoxOps = ops, Href = href ?? LinkOf(e) });
        if (mr > 0) AddSpacer(st, s, mr, href);
        st.LastWasSpace = false;
    }

    /// <summary>An inline-block (or an image laid out as a box): laid out on its own and carried as one item.</summary>
    private Item LayoutInlineBlock(Element e, float availW, string? href)
    {
        var c = CaptureBox(e, availW, shrinkToFit: true);
        float baseline = float.IsNaN(c.Baseline) ? c.Height : c.Baseline;
        var item = new Item { Kind = ItemKind.Box, Style = e.Style, Width = c.Width, Height = c.Height, BoxBaseline = baseline, Capture = c, Href = href };
        if (_measuring)
        {
            // An inline-block can wrap its own content, so its min-content contribution is its min-content width,
            // not the width it takes when laid out at the (very wide) measuring width.
            item.MinWidth = Math.Min(c.Width, Intrinsic(e).Min + FixedHorizontalMargins(e.Style));
        }
        return item;
    }

    // ------------------------------------------------------------------ leaders

    /// <summary>Generated text with leaders (leader() in content): the text around them, and a leader item for each.</summary>
    private void AddWithLeaders(CollectState st, string text, ComputedStyle style, string? href, ComputedStyle? deco)
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

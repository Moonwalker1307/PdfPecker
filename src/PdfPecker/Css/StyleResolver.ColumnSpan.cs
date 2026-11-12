using System.Globalization;
using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// column-span: all deeper than a child of the multi-column box (CSS Multi-column 1 §6.1). A spanner may sit in blocks
/// of the multi-column box's own formatting context, such as a section around a heading. As in Chrome, those blocks
/// are split around it: the part before the spanner ends the columns, the spanner crosses them, and the part after it
/// starts the next set of columns, each part with its share of the block's borders, padding and margins. The layout
/// then finds every spanner among the children of the multi-column box.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>
    /// How many blocks deep a spanner is found. Every block around a spanner is split once for each spanner in it, so
    /// spanners at the bottom of hundreds of nested blocks would make that many copies of each block.
    /// </summary>
    private const int MaxSpannerNesting = 8;

    /// <summary>Lifts the spanners nested in blocks of <paramref name="multicol"/> up to its children, splitting the blocks.</summary>
    private static void LiftSpanners(Element multicol)
    {
        var s = multicol.Style;
        if (s is not { IsMultiColumn: true } || s.Display == Display.None || s.Contents) return;
        var children = multicol.Children;
        int first = -1;
        for (int i = 0; i < children.Count && first < 0; i++)
            if (children[i] is Element ce && Splittable(ce) && HoldsSpanner(ce, 1)) first = i;
        if (first < 0) return;
        // The next style pass puts the document's own children back (see Prepare).
        multicol.DomChildren ??= new List<Node>(children);
        var lifted = new List<Node>(children.Count + 2);
        for (int i = 0; i < children.Count; i++)
        {
            if (i >= first && children[i] is Element ce && Splittable(ce) && HoldsSpanner(ce, 1)) Split(ce, 1, lifted);
            else lifted.Add(children[i]);
        }
        children.Clear();
        foreach (var n in lifted)
        {
            n.Parent = multicol;
            children.Add(n);
        }
    }

    /// <summary>Whether a box takes the whole width of the columns around it.</summary>
    private static bool IsSpannerBox(Element e)
        => e.Style is { ColumnSpanAll: true, Floated: false, OutOfFlow: false, Contents: false } s && !e.Detached
           && s.Display is Display.Block or Display.ListItem or Display.Table or Display.Flex or Display.Grid;

    /// <summary>
    /// Whether a spanner inside <paramref name="e"/> still belongs to the columns around it: a block in normal flow that
    /// starts no formatting context of its own, so that its content flows in those columns.
    /// </summary>
    private static bool Splittable(Element e)
        => e.Style is { ColumnSpanAll: false, Floated: false, OutOfFlow: false, FormattingRoot: false, IsMultiColumn: false, Contents: false } s
           && s.Display is Display.Block or Display.ListItem && s.ContainerType == ContainerType.Normal
           && !e.Detached && !e.Anonymous && e.Control == null && e.Name != "fieldset";

    private static bool HoldsSpanner(Element e, int depth)
    {
        foreach (var c in e.Children)
            if (c is Element ce && (IsSpannerBox(ce) || depth < MaxSpannerNesting && Splittable(ce) && HoldsSpanner(ce, depth + 1))) return true;
        return false;
    }

    /// <summary>
    /// Adds to <paramref name="into"/> the parts of <paramref name="e"/> between its spanners and the spanners
    /// themselves, in document order. The first part is the element itself, so that its id, marker and bookmark stay
    /// with it; parts without content are left out.
    /// </summary>
    private static void Split(Element e, int depth, List<Node> into)
    {
        // The next style pass puts the element's own children back (see Prepare).
        e.DomChildren ??= new List<Node>(e.Children);
        var original = new List<Node>(e.Children);
        var parts = new List<(Element Part, List<Node> Content, bool AfterSpanner, bool BeforeSpanner)>();
        List<Node>? content = null;
        bool spanned = false;
        foreach (var child in original)
        {
            if (child is Element spanner && IsSpannerBox(spanner))
            {
                Lift(spanner);
                continue;
            }
            if (child is Element ce && depth < MaxSpannerNesting && Splittable(ce) && HoldsSpanner(ce, depth + 1))
            {
                var inner = new List<Node>();
                Split(ce, depth + 1, inner);
                foreach (var n in inner)
                {
                    if (n is Element lifted && IsSpannerBox(lifted)) Lift(lifted);
                    else (content ??= new List<Node>()).Add(n);
                }
                continue;
            }
            (content ??= new List<Node>()).Add(child);
        }
        Close(beforeSpanner: false);

        void Lift(Element spanner)
        {
            Close(beforeSpanner: true);
            into.Add(spanner);
            spanned = true;
        }

        void Close(bool beforeSpanner)
        {
            if (content == null) return;
            bool blank = true;
            foreach (var n in content)
                if (n is not TextNode t || !string.IsNullOrWhiteSpace(t.Text)) { blank = false; break; }
            if (!blank)
            {
                var part = parts.Count == 0 ? e : Continuation(e);
                parts.Add((part, content, spanned, beforeSpanner));
                into.Add(part);
            }
            content = null;
        }

        e.Children.Clear();
        var style = e.Style;
        // An ordered list goes on counting in its parts after the first: each starts at the number its first item has.
        bool ordered = e.Name == "ol", reversed = ordered && e.HasAttribute("reversed");
        int number = !ordered ? 0 : Invariant.TryParseInt(e.GetAttribute("start"), out int start) ? start
            : reversed ? original.Count(n => n is Element { Name: "li" }) : 1;
        foreach (var (part, nodes, afterSpanner, beforeSpanner) in parts)
        {
            if (ordered)
            {
                if (part != e) part.AddAttribute("start", number.ToString(CultureInfo.InvariantCulture));
                foreach (var n in nodes)
                    if (n is Element { Style.Display: Display.ListItem } item)
                        number = (Invariant.TryParseInt(item.GetAttribute("value"), out int value) ? value : number) + (reversed ? -1 : 1);
            }
            var st = style.Clone();
            if (afterSpanner)
            {
                // A part after a spanner continues the block: its top edge and first line were drawn before it.
                st.MarginTop = st.PaddingTop = Length.Zero;
                st.BorderTopWidth = 0;
                st.TextIndent = default;
                if (part != e && st.Display == Display.ListItem) st.Display = Display.Block;
            }
            if (beforeSpanner)
            {
                st.MarginBottom = st.PaddingBottom = Length.Zero;
                st.BorderBottomWidth = 0;
            }
            // A height is the whole block's, which its parts do not share out.
            st.Height = st.MaxHeight = Length.Auto;
            st.MinHeight = Length.Zero;
            foreach (var n in nodes)
            {
                n.Parent = part;
                part.Children.Add(n);
            }
            part.Style = st;
        }
    }

    /// <summary>A part of <paramref name="e"/> after one of its spanners: the same element without its id.</summary>
    private static Element Continuation(Element e)
    {
        var part = new Element(e.Name) { Style = e.Style, Classes = e.Classes };
        foreach (var a in e.Attributes)
            if (!a.Key.Equals("id", StringComparison.OrdinalIgnoreCase) && !(e.Name == "ol" && a.Key.Equals("start", StringComparison.OrdinalIgnoreCase)))
                part.AddAttribute(a.Key, a.Value);
        return part;
    }
}

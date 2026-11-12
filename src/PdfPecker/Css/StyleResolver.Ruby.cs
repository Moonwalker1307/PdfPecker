using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// Ruby (CSS Ruby 1, the ruby, rt and rp elements): each base and its annotation become an anonymous inline-block that
/// stacks the annotation, at half the font size, centred over the base. The base keeps its baseline, so the pair sits
/// in the line as the base text would, and the line grows only by what the annotation adds above the base's em box, as
/// Chrome's annotation overflow does when the line's leading cannot hold it. rp is not displayed.
/// </summary>
internal sealed partial class StyleResolver
{
    internal const string RubyPairName = "::ruby-pair", RubyBaseName = "::ruby-base";

    /// <summary>
    /// How deep rubies nest and still stack their annotations. Each pair adds two boxes to the layout's depth, so rubies
    /// nested as deep as the parser allows would take twice the stack of other boxes; real documents nest two at most.
    /// </summary>
    private const int MaxRubyNesting = 16;

    /// <summary>Wraps each base of a ruby element and the rt after it in a pair box, with the rt first so that it is drawn above.</summary>
    private static void WrapRubyPairs(Element ruby)
    {
        var s = ruby.Style;
        if (s == null || s.Display is Display.None || s.Contents) return;
        int nesting = 0;
        for (var p = ruby.Parent; p != null; p = p.Parent)
            if (p.Name == "ruby" && ++nesting >= MaxRubyNesting) return;
        var children = ruby.Children;
        int start = 0; // the first child of the base that the next rt annotates
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Element { Name: "rt", Style: { Display: not Display.None } } rt) continue;
            // The base: what lies between the last pair and this rt, less white space at its ends and hidden rp
            // elements, which belong to neither.
            int end = i - 1;
            while (start <= end && Neutral(children[start])) start++;
            while (end >= start && Neutral(children[end])) end--;
            bool under = rt.Style.RubyUnder;
            var pairStyle = s.CreateChild();
            pairStyle.Display = Display.InlineBlock;
            pairStyle.TextAlign = TextAlign.Center;
            pairStyle.TextIndent = default;
            if (under)
            {
                // The annotation stays first, where the layout looks for it, and a column flex box puts the base above
                // it; a column flex box takes its baseline from its first item, the base, as an inline-block over the
                // base takes it from its last line.
                pairStyle.Display = Display.InlineFlex;
                pairStyle.FlexDirection = FlexDirection.Column;
            }
            var pair = new Element(RubyPairName) { Anonymous = true, Style = pairStyle, Parent = ruby };
            // The annotation is placed over (or under) the base as ruby-align says, by default with its free space spread
            // around its characters.
            rt.Style = rt.Style.Clone();
            Align(rt.Style, rt.Style.RubyAlign);
            rt.Parent = pair;
            pair.Children.Add(rt);
            var baseStyle = pairStyle.CreateChild();
            baseStyle.Display = Display.Block;
            if (under) baseStyle.Order = -1;
            // The base's line is as tall as its em box, so the annotation sits right on the base's text and the pair
            // reaches above the line's own leading only by the annotation's height less that leading.
            baseStyle.LineHeightKind = LineHeightKind.Factor;
            baseStyle.LineHeightValue = 1;
            Align(baseStyle, s.RubyAlign);
            var rubyBase = new Element(RubyBaseName) { Anonymous = true, Style = baseStyle, Parent = pair };
            pair.Children.Add(rubyBase);
            // An rt right after another one annotates an empty base, which is still an em tall for the annotation to
            // sit on, as in Chrome (the layout puts the pair's baseline in it).
            if (start > end) baseStyle.MinHeight = Length.Pt(baseStyle.TextSize);
            for (int k = start; k <= end; k++)
            {
                var c = children[k];
                c.Parent = rubyBase;
                rubyBase.Children.Add(c);
                if (c is Element inner) TightenLines(inner, baseStyle);
            }
            int count = Math.Max(0, end - start + 1);
            children.RemoveAt(i); // the rt
            children.RemoveRange(start, count);
            children.Insert(start, pair);
            i = start;
            start = i + 1;
        }

        static void Align(ComputedStyle st, RubyAlign align)
        {
            st.TextAlign = align == RubyAlign.Start ? TextAlign.Start : TextAlign.Center;
            st.RubySpaceAround = align == RubyAlign.SpaceAround;
            st.RubySpaceBetween = align == RubyAlign.SpaceBetween;
        }

        static bool Neutral(Node n)
            => n is TextNode t ? string.IsNullOrWhiteSpace(t.Text) : n is Element { Style: null or { Display: Display.None } };
    }

    /// <summary>
    /// Gives an element in a ruby base, such as an rb, and what is inside it the base's line height. Each now counts
    /// the moved parent as its parent, since inline boxes measure their lines with their inline ancestors' metrics.
    /// </summary>
    private static void TightenLines(Element e, ComputedStyle parent)
    {
        // An inline-block, such as a ruby pair nested in the base, has lines of its own.
        if (e.Style is not { Display: Display.Inline } st) return;
        e.Style = st.Clone();
        e.Style.LineHeightKind = LineHeightKind.Factor;
        e.Style.LineHeightValue = 1;
        e.Style.ParentStyle = parent;
        foreach (var c in e.Children)
            if (c is Element inner) TightenLines(inner, e.Style);
    }

    /// <summary>Puts the base of a ruby pair back before its rt, where the document has it.</summary>
    private static void UnwrapRubyPair(Element pair)
    {
        if (pair.Children.Count == 2 && pair.Children[0] is Element { Name: "rt" } rt)
        {
            pair.Children.RemoveAt(0);
            pair.Children.Add(rt);
        }
    }
}

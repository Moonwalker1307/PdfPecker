using PdfPecker.Html;
using PdfPecker.Layout;

namespace PdfPecker.Css;

/// <summary>
/// margin-trim (CSS Box 4) on block containers, as Chrome does it: the margins that meet the container's block-start
/// or block-end edge are dropped, which are its first or last in-flow child's and those of the descendants whose
/// margins collapse with them, and the margins of empty boxes there that collapse through. Chrome trims nothing in flex
/// and grid containers and nothing on the inline axis, so neither does this.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>How deep the trim follows margins that collapse into children, so a deep tree cannot take the stack.</summary>
    private const int MaxMarginTrimDepth = 64;

    /// <summary>
    /// margin-trim: none, block and inline in either order, or any of block-start, inline-start, block-end and
    /// inline-end, each once.
    /// </summary>
    internal static MarginTrim? ParseMarginTrim(string value)
    {
        if (value == "none") return MarginTrim.None;
        var result = MarginTrim.None;
        bool axes = false, edges = false;
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (trim, axis) = token switch
            {
                "block" => (MarginTrim.BlockStart | MarginTrim.BlockEnd, true),
                "inline" => (MarginTrim.InlineStart | MarginTrim.InlineEnd, true),
                "block-start" => (MarginTrim.BlockStart, false),
                "block-end" => (MarginTrim.BlockEnd, false),
                "inline-start" => (MarginTrim.InlineStart, false),
                "inline-end" => (MarginTrim.InlineEnd, false),
                _ => (MarginTrim.None, false),
            };
            if (trim == MarginTrim.None || (result & trim) != 0) return null;
            if (axis) axes = true; else edges = true;
            result |= trim;
        }
        return result == MarginTrim.None || axes && edges ? null : result;
    }

    /// <summary>
    /// Trims the margins at a block container's block edges, once its children have their styles. Only horizontal
    /// writing modes are handled, where the block edges are the top and the bottom.
    /// </summary>
    private static void TrimMargins(Element container)
    {
        var s = container.Style;
        if (s.WritingMode != WritingMode.HorizontalTb || s.IsFlex || s.IsGrid || container.IsReplaced
            || s.Display is not (Display.Block or Display.ListItem or Display.InlineBlock or Display.TableCell or Display.TableCaption))
            return;
        if ((s.MarginTrim & MarginTrim.BlockStart) != 0) TrimStart(container, 0);
        if ((s.MarginTrim & MarginTrim.BlockEnd) != 0) TrimEnd(container, 0);
    }

    private static void TrimStart(Element box, int depth)
    {
        var children = box.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var c = InFlowBlock(box, children[i], out bool skip);
            if (skip) continue;
            if (c == null) return;
            var cs = c.Style = c.Style.Clone();
            cs.MarginTop = Length.Zero;
            if (CollapsesThroughEmpty(c))
            {
                cs.MarginBottom = Length.Zero;
                continue;
            }
            if (depth < MaxMarginTrimDepth && CollapsesWithChildren(c) && cs.BorderTop == 0 && IsZero(cs.PaddingTop)) TrimStart(c, depth + 1);
            return;
        }
    }

    private static void TrimEnd(Element box, int depth)
    {
        var children = box.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var c = InFlowBlock(box, children[i], out bool skip);
            if (skip) continue;
            if (c == null) return;
            var cs = c.Style = c.Style.Clone();
            cs.MarginBottom = Length.Zero;
            if (CollapsesThroughEmpty(c))
            {
                cs.MarginTop = Length.Zero;
                continue;
            }
            if (depth < MaxMarginTrimDepth && CollapsesWithChildren(c) && cs.BorderBottom == 0 && IsZero(cs.PaddingBottom)
                && cs.Height.IsAuto && (cs.MinHeight.IsAuto || cs.MinHeight.Value <= 0))
                TrimEnd(c, depth + 1);
            return;
        }
    }

    /// <summary>
    /// The child as a block in the container's flow, or null when it is text or an inline-level box, which leaves no
    /// margin at the edge to trim. <paramref name="skip"/> is set for what is not in the flow: white space that
    /// collapses, boxes not displayed, floats and positioned boxes.
    /// </summary>
    private static Element? InFlowBlock(Element box, Node child, out bool skip)
    {
        skip = false;
        if (child is TextNode t)
        {
            skip = string.IsNullOrWhiteSpace(t.Text) && InlineLayout.IsCollapsible(box.Style);
            return null;
        }
        if (child is not Element { Style: { } s } c || s.Display == Display.None || c.Detached || s.OutOfFlow || s.Floated)
        {
            skip = true;
            return null;
        }
        return s.Display is Display.Inline or Display.InlineBlock or Display.InlineFlex or Display.InlineGrid || s.InlineTable || c.IsReplaced
            ? null : c;
    }

    /// <summary>A block in normal flow that is no formatting context of its own, whose margins collapse with its children's.</summary>
    private static bool CollapsesWithChildren(Element c)
    {
        var s = c.Style;
        return s.Display is Display.Block or Display.ListItem && !s.FormattingRoot && !s.IsMultiColumn && c.Name is not ("hr" or "fieldset");
    }

    /// <summary>An empty block with no height, border or padding, whose top and bottom margins collapse together.</summary>
    private static bool CollapsesThroughEmpty(Element c)
    {
        var s = c.Style;
        if (s.Display != Display.Block || s.FormattingRoot || s.BorderTop != 0 || s.BorderBottom != 0 || !IsZero(s.PaddingTop)
            || !IsZero(s.PaddingBottom) || !(s.Height.IsAuto || s.Height is { Unit: LengthUnit.Pt, Value: 0 })
            || !(s.MinHeight.IsAuto || s.MinHeight.Value <= 0))
            return false;
        foreach (var child in c.Children)
        {
            if (child is TextNode t && string.IsNullOrWhiteSpace(t.Text) && InlineLayout.IsCollapsible(s)) continue;
            if (child is Element { Style: { } cs } e && (cs.Display == Display.None || cs.OutOfFlow || e.Detached)) continue;
            return false;
        }
        return true;
    }

    private static bool IsZero(Length l) => !l.IsPercent && l.Resolve(0) == 0;
}

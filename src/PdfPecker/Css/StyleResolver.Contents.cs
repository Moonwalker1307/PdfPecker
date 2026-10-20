using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// display: contents (CSS Display 3 §2.5): the element makes no box of its own, and its children, its ::before and
/// ::after boxes included, are laid out as if they were children of its parent. Its children still inherit from it.
/// </summary>
internal sealed partial class StyleResolver
{
    /// <summary>
    /// The style of an element with display: contents. Only its inherited properties reach anything, through its
    /// children, so the rest go back to their initial values; counters and string-set still apply, as they need no
    /// box. The root element becomes a block, and replaced elements and form controls, whose content is not made of
    /// children that could take their place, are not displayed (CSS Display 3 Appendix B).
    /// </summary>
    private ComputedStyle ContentsStyle(string name, ComputedStyle s, ComputedStyle parent)
    {
        _contentsUsed = true;
        if (name == "html" || NoContentsBox(name))
        {
            var replaced = s.Clone();
            replaced.Contents = false;
            replaced.Display = name == "html" ? Display.Block : Display.None;
            return replaced;
        }
        var c = s.CreateChild();
        c.Contents = true;
        c.ParentFontSize = s.ParentFontSize;
        c.CounterReset = s.CounterReset;
        c.CounterSet = s.CounterSet;
        c.CounterIncrement = s.CounterIncrement;
        c.StringSet = s.StringSet;
        // Its opacity is not applied, so it does not fade the inline content inside it either.
        c.InlineFade = parent.Display == Display.Inline ? parent.InlineFade : 1;
        // Text decorations propagate through boxes, and it has none: its own underline, such as a link's, is not drawn.
        c.TextDecoration = parent.TextDecoration;
        c.DecorationColor = parent.DecorationColor;
        c.DecorationStyle = parent.DecorationStyle;
        c.DecorationThickness = parent.DecorationThickness;
        c.UnderlineOffset = parent.UnderlineOffset;
        return c;
    }

    private static bool NoContentsBox(string name)
        => name is "img" or "svg" or "math" or "video" or "audio" or "canvas" or "iframe" or "object" or "embed" or "frame"
            or "frameset" or "input" or "select" or "textarea" or "progress" or "meter" or "br" or "wbr";

    private bool _contentsUsed;

    /// <summary>
    /// Puts the children of the display: contents children of <paramref name="parent"/> in their place, after all their
    /// styles are known. A wrapper of only inline content in ordinary flow stays as an inline element without a box,
    /// which keeps its link and anchors; any other is replaced by its children. Elements taken out get
    /// <paramref name="parent"/> as their parent, so that flex, grid and table layout see them as its items; text keeps
    /// its own parent, whose style it is drawn in.
    /// </summary>
    private static void SpliceContents(Element parent)
    {
        var children = parent.Children;
        List<Node>? boxes = null;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Element { Style.Contents: true } e || KeepsInline(e, parent.Style))
            {
                boxes?.Add(children[i]);
                continue;
            }
            if (boxes == null)
            {
                boxes = new List<Node>(children.Count + e.Children.Count);
                for (int k = 0; k < i; k++) boxes.Add(children[k]);
            }
            Flatten(e, parent, null, boxes);
        }
        if (boxes == null) return;
        parent.DomChildren = new List<Node>(children);
        children.Clear();
        children.AddRange(boxes);
    }

    private static void Flatten(Element e, Element parent, string? href, List<Node> boxes)
    {
        if (e.Name == "a" && e.GetAttribute("href") is { Length: > 0 } link) href = link;
        foreach (var c in e.Children)
        {
            if (c is Element ce)
            {
                if (ce.Style is { Contents: true } && !KeepsInline(ce, parent.Style))
                {
                    Flatten(ce, parent, href, boxes);
                    continue;
                }
                ce.Parent = parent;
                ce.ContentsHref = href;
            }
            boxes.Add(c);
        }
    }

    /// <summary>
    /// Whether a display: contents element can stay in the tree as an inline element without a box: its parent lays
    /// out its content in ordinary flow, not as flex, grid or table items, and everything in it is inline.
    /// </summary>
    private static bool KeepsInline(Element e, ComputedStyle parent)
        => !parent.IsFlex && !parent.IsGrid
           && parent.Display is not (Display.Table or Display.TableRow or Display.TableRowGroup or Display.TableHeaderGroup
               or Display.TableFooterGroup or Display.TableColumn or Display.TableColumnGroup)
           && InlineOnly(e);

    private static bool InlineOnly(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce || ce.Style is not { } s || s.Display == Display.None || ce.Detached || ce.FootnoteIndex >= 0 || s.Floated || s.OutOfFlow) continue;
            if (s.Contents ? !InlineOnly(ce) : s.IsBlockLevel) return false;
        }
        return true;
    }

    /// <summary>Gives <paramref name="parent"/> back the children the parser made, before a new style pass.</summary>
    private static void RestoreContents(Element parent)
    {
        if (parent.DomChildren is not { } dom) return;
        parent.Children.Clear();
        parent.Children.AddRange(dom);
        parent.DomChildren = null;
    }
}

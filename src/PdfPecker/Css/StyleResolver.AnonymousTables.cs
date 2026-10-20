using PdfPecker.Html;

namespace PdfPecker.Css;

/// <summary>
/// Anonymous table boxes (CSS 2.1 §17.2.1): table parts without the parent they need get one. Text straight in a
/// display: table box goes into an anonymous row and cell, cells straight in a table into a row, and cells or rows
/// in an ordinary block into a table of their own, so that they are laid out as a table, as in browsers.
/// </summary>
internal sealed partial class StyleResolver
{
    private const string AnonymousTableName = "::table", AnonymousRowName = "::table-row", AnonymousCellName = "::table-cell";

    /// <summary>Puts the children of the anonymous boxes in <paramref name="parent"/> back in their place.</summary>
    private static void UnwrapAnonymous(Element parent)
    {
        var children = parent.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Element { Anonymous: true } box) continue;
            children.RemoveAt(i);
            foreach (var c in box.Children) c.Parent = parent;
            children.InsertRange(i, box.Children);
            i--; // the first child may be an anonymous box too
        }
    }

    /// <summary>Wraps the children of <paramref name="parent"/> that need an anonymous table, row or cell box.</summary>
    private static void WrapTableParts(Element parent)
    {
        var s = parent.Style;
        // The table parts in a display: contents element are wrapped where they end up, in the box of its parent.
        if (s == null || s.Display == Display.None || s.Contents || s.IsFlex || s.IsGrid || parent.Children.Count == 0) return;
        switch (s.Display)
        {
            case Display.Table:
                // Everything that is no row, row group, caption or column goes into an anonymous row. Wrappers such as a
                // <form> around rows stay: the table looks into them for its rows.
                WrapRuns(parent, c => !IsTableChild(c.Style.Display) && !HasRows(c), Display.TableRow, AnonymousRowName);
                break;
            case Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup:
                WrapRuns(parent, c => c.Style.Display != Display.TableRow, Display.TableRow, AnonymousRowName);
                break;
            case Display.TableRow:
                WrapRuns(parent, c => c.Style.Display != Display.TableCell, Display.TableCell, AnonymousCellName);
                break;
            case Display.TableColumn or Display.TableColumnGroup:
                break;
            default:
                // Cells and rows outside a table: a run of them becomes one anonymous table, inline in inline content.
                WrapRuns(parent, c => IsTablePart(c.Style.Display), Display.Table, AnonymousTableName, inline: s.Display == Display.Inline, elementsOnly: true);
                break;
        }
    }

    /// <summary>
    /// A closed details element shows only its first summary: the rest of its content, text included, goes into
    /// anonymous boxes that are not displayed.
    /// </summary>
    private static void HideClosedDetails(Element details)
    {
        Element? summary = null;
        foreach (var c in details.Children)
            if (c is Element { Name: "summary" } s) { summary = s; break; }
        WrapRuns(details, c => c != summary, Display.None, "::details-content");
    }

    private static bool IsTableChild(Display d)
        => d is Display.TableRow or Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup or Display.TableCaption
            or Display.TableColumn or Display.TableColumnGroup;

    private static bool IsTablePart(Display d)
        => d is Display.TableRow or Display.TableCell or Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup;

    /// <summary>Whether a table finds rows inside <paramref name="e"/>, which it looks into as it does into a form.</summary>
    private static bool HasRows(Element e)
    {
        foreach (var c in e.Children)
        {
            if (c is not Element ce || ce.Style == null) continue;
            switch (ce.Style.Display)
            {
                case Display.TableRow or Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup: return true;
                case Display.Table or Display.TableCell or Display.TableCaption or Display.TableColumn or Display.TableColumnGroup or Display.None: continue;
                default:
                    if (HasRows(ce)) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Wraps each run of children that <paramref name="needsBox"/> picks, with the white space, comments and hidden
    /// elements between them, in an anonymous box of <paramref name="display"/>. Text that is not white space joins a
    /// run, or with <paramref name="elementsOnly"/> ends it. Running headers, footnotes and absolutely positioned boxes end a run
    /// and stay where they are.
    /// </summary>
    private static void WrapRuns(Element parent, Func<Element, bool> needsBox, Display display, string name, bool inline = false, bool elementsOnly = false)
    {
        var children = parent.Children;
        int start = -1, end = -1; // the run: its first and last child that needs the box
        for (int i = 0; i <= children.Count; i++)
        {
            int kind = i == children.Count ? 0 : Kind(children[i]);
            if (kind == 2)
            {
                if (start < 0) start = i;
                end = i;
                continue;
            }
            if (kind == 1) continue; // joins a run only between two of its children
            if (start >= 0)
            {
                var box = Wrap(parent, start, end, display, name, inline);
                i = start; // the box now stands where the run did
                start = end = -1;
                WrapTableParts(box);
            }
        }

        // 0: ends a run; 1: neutral; 2: needs the box.
        int Kind(Node n)
        {
            if (n is TextNode t) return string.IsNullOrWhiteSpace(t.Text) ? 1 : elementsOnly ? 0 : 2;
            if (n is not Element e || e.Style == null || e.Style.Display == Display.None) return 1;
            if (e.Detached || e.FootnoteIndex >= 0 || e.Style.OutOfFlow) return 0;
            return needsBox(e) ? 2 : 0;
        }
    }

    private static Element Wrap(Element parent, int start, int end, Display display, string name, bool inline)
    {
        var style = parent.Style.CreateChild();
        style.Display = display;
        style.InlineTable = inline;
        var box = new Element(name) { Anonymous = true, Style = style, Parent = parent };
        for (int k = start; k <= end; k++)
        {
            var c = parent.Children[k];
            // Text from a display: contents element keeps it as its parent, whose style it is drawn in.
            if (c is not TextNode { Parent.Style.Contents: true }) c.Parent = box;
            box.Children.Add(c);
        }
        parent.Children.RemoveRange(start, end - start + 1);
        parent.Children.Insert(start, box);
        return box;
    }
}

using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Layout;
using PdfPecker.Pdf;

namespace PdfPecker;

public static partial class HtmlToPdf
{
    /// <summary>A run of content laid out on one page setup: a named page's, or the document's.</summary>
    private sealed class PageRun
    {
        public string? Name;
        /// <summary>The run's top-level nodes and the elements that contain them; null for the whole document.</summary>
        public (HashSet<Node> Units, HashSet<Element> Ancestors)? Keep;
        public PageSetup Page = null!;
        /// <summary>The setups of the run's pages: <see cref="Page"/>, or another for pages with :first, :left or :right rules.</summary>
        public PageGrid Grid = null!;
        public float Width;
        public LayoutEngine Engine = null!;
        public float Height;
    }

    /// <summary>
    /// Splits the document where the page name (the page property) of its block-level boxes changes, as CSS Paged
    /// Media puts boxes with another page name on a new page. Content without a page of its own (whitespace, boxes
    /// out of the flow) stays with the run it is in.
    /// </summary>
    private static List<PageRun> SplitByPageName(Element doc)
    {
        var runs = new List<PageRun>();
        var units = new List<List<Node>>();
        var pending = new List<Node>();
        void Add(Node n, string? name, bool any)
        {
            if (any && runs.Count == 0) { pending.Add(n); return; }
            if (!any && (runs.Count == 0 || runs[^1].Name != name))
            {
                runs.Add(new PageRun { Name = name });
                units.Add(new List<Node>(pending));
                pending.Clear();
            }
            units[^1].Add(n);
        }
        void Walk(Element e)
        {
            foreach (var c in e.Children)
            {
                if (c is Element ce && InFlowBlock(ce))
                {
                    if (IsBlockContainer(ce.Style) && !Uniform(ce, ce.Style.PageName)) Walk(ce);
                    else Add(ce, ce.Style.PageName, false);
                }
                else Add(c, e.Style?.PageName, HasNoPage(c));
            }
        }
        Walk(doc);
        if (runs.Count <= 1) return new List<PageRun> { new() { Name = runs.Count == 1 ? runs[0].Name : null } };
        for (int i = 0; i < runs.Count; i++)
        {
            var ancestors = new HashSet<Element>();
            foreach (var n in units[i])
                for (var a = n.Parent; a != null && ancestors.Add(a); a = a.Parent) { }
            runs[i].Keep = (new HashSet<Node>(units[i]), ancestors);
        }
        return runs;

        static bool InFlowBlock(Element e) => e.Style is { } s && s.Display != Display.None && !e.Detached && s.IsBlockLevel
                                              && s.Position is not (Position.Absolute or Position.Fixed);
        static bool IsBlockContainer(ComputedStyle s) => s.Display is Display.Block or Display.ListItem;
        static bool Uniform(Element e, string? name)
        {
            foreach (var c in e.Children)
                if (c is Element ce && InFlowBlock(ce) && (ce.Style.PageName != name || IsBlockContainer(ce.Style) && !Uniform(ce, name))) return false;
            return true;
        }
        // Whitespace, hidden elements and boxes out of the flow go with whatever page they fall on.
        static bool HasNoPage(Node n) => n is TextNode t
            ? string.IsNullOrWhiteSpace(t.Text)
            : n is not Element { Detached: false, Style: { Display: not Display.None, Position: not (Position.Absolute or Position.Fixed) } };
    }

    /// <summary>Takes the nodes of other runs out of the tree; the returned action puts them back.</summary>
    private static Action KeepOnly((HashSet<Node> Units, HashSet<Element> Ancestors) keep)
    {
        var removed = new List<(Element Parent, Node[] Children)>();
        void Filter(Element e)
        {
            removed.Add((e, e.Children.ToArray()));
            e.Children.RemoveAll(c => !keep.Units.Contains(c) && !(c is Element ce && keep.Ancestors.Contains(ce)));
            foreach (var c in e.Children)
                if (c is Element ce && keep.Ancestors.Contains(ce)) Filter(ce);
        }
        foreach (var a in keep.Ancestors)
            if (a.Parent == null) Filter(a);
        return () =>
        {
            foreach (var (parent, children) in removed)
            {
                parent.Children.Clear();
                parent.Children.AddRange(children);
            }
        };
    }

    /// <summary>The page number (1-based) of each id's element in the laid-out runs, for target-counter().</summary>
    private static Dictionary<string, int> PagesOfAnchors(List<PageRun> runs)
    {
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        int first = 0;
        foreach (var run in runs)
        {
            var grid = run.Grid;
            foreach (var (key, y) in run.Engine.Anchors)
                if (key.Length > 0 && key[0] != '\u0001') pages.TryAdd(key, first + Math.Max(0, (int)grid.PageOf(y + 0.01)) + 1);
            first += grid.PagesFor(run.Height);
        }
        return pages;
    }

    /// <summary>The page (0-based, in the whole document) of each footnote's call in the laid-out runs.</summary>
    private static Dictionary<int, int> PagesOfFootnotes(List<PageRun> runs)
    {
        var pages = new Dictionary<int, int>();
        int first = 0;
        foreach (var run in runs)
        {
            foreach (var (index, page) in run.Engine.FootnoteCallPages) pages.TryAdd(index, first + page);
            first += run.Grid.PagesFor(run.Height);
        }
        return pages;
    }

    /// <summary>Whether an element paints a background colour or image, which can then become the canvas background.</summary>
    private static bool HasBackground(Element? e) =>
        e?.Style is { } s && (CssColor.IsVisible(s.BackgroundColor) || s.BackgroundImages?.Any(l => l != null) == true);
}

using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>The collapsing border model: resolved border widths and which border wins, compared with Chrome.</summary>
public class CollapsedBorderTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return engine.Ops;
    }

    private static IEnumerable<Element> Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) yield return c;
            foreach (var d in Find(c, name)) yield return d;
        }
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in Flatten(g.Ops)) yield return o;
        }
    }

    private static TextOp Text(List<DrawOp> ops, string word) => Flatten(ops).OfType<TextOp>().First(t => t.Text.Contains(word, StringComparison.Ordinal));

    private static List<RectOp> Rects(List<DrawOp> ops, uint color) => Flatten(ops).OfType<RectOp>().Where(r => r.Color == color).ToList();

    [Fact]
    public void AThickTableBorderWidensTheEdgeCells()
    {
        // The first cell's content starts inside the whole 3px table border, as in Chrome, not 1px further out.
        var ops = Layout("<style>body{margin:0} td{padding:0;border:1px solid red}</style>" +
                         "<table style='border-collapse:collapse;border:3px solid #000'><tr><td>a</td><td>b</td></tr></table>");
        Assert.Equal(2.25, Text(ops, "a").X, 2);
    }

    [Fact]
    public void AHiddenBorderHidesTheBorderItMeets()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-collapse:collapse'>" +
                         "<tr><td style='border:5px solid #00f'>top</td></tr><tr><td style='border:hidden'>below</td></tr></table>");
        // The blue cell keeps its top, left and right borders; its bottom meets the hidden border and goes.
        var blue = Rects(ops, 0xFF0000FF);
        Assert.Contains(blue, r => r.W > r.H);
        Assert.DoesNotContain(blue, r => r.W > r.H && r.Y > Text(ops, "top").Baseline);
    }

    [Fact]
    public void DoubleBeatsSolidOfTheSameWidth()
    {
        // On a tie of width the cell on the left would win; double beats solid first.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-collapse:collapse'><tr>" +
                         "<td style='border:6px solid #00f'>one</td><td style='border:6px double #f00'>two</td></tr></table>");
        var blue = Rects(ops, 0xFF0000FF).Where(r => r.H > r.W).ToList();
        // Only the left side of the blue cell is a vertical blue band: the shared edge is drawn double, in red.
        Assert.Single(blue);
        Assert.True(blue[0].X < Text(ops, "one").X);
    }

    [Fact]
    public void RowGroupBordersAreDrawnAndTakeRoom()
    {
        const string Table = "<style>body{margin:0} td{padding:0;border:1px solid #000}</style><table style='border-collapse:collapse'>" +
                             "<thead><tr><td>head</td></tr></thead><tbody{0}><tr><td>body</td></tr></tbody></table>";
        var plain = Layout(Table.Replace("{0}", "", StringComparison.Ordinal));
        var ops = Layout(Table.Replace("{0}", " style='border-top:6px solid #0c0'", StringComparison.Ordinal));
        var green = Assert.Single(Rects(ops, 0xFF00CC00));
        Assert.Equal(4.5, green.H, 2);
        Assert.True(Text(ops, "body").Baseline > Text(plain, "body").Baseline + 1);
    }

    [Fact]
    public void RowBordersAreDrawn()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-collapse:collapse'>" +
                         "<tr style='border-bottom:2px solid #0c0'><td>one</td><td>two</td></tr><tr><td>three</td><td>four</td></tr></table>");
        Assert.NotEmpty(Rects(ops, 0xFF00CC00));
    }

    [Fact]
    public void RightToLeftTablesHaveTheirLeftBorder()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0;border:1px solid #f00}</style>" +
                         "<table dir='rtl' style='border-collapse:collapse'><tr><td>one</td><td>two</td></tr></table>");
        var red = Rects(ops, 0xFFFF0000).Where(r => r.H > r.W).ToList();
        Assert.Equal(3, red.Count);
        Assert.Contains(red, r => r.X < 0.5f);
    }
}

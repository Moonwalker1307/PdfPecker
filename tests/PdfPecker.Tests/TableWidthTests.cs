using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>How the auto table layout sizes columns, compared with Chrome.</summary>
public class TableWidthTests
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

    private static RectOp Rect(List<DrawOp> ops, uint color) => Flatten(ops).OfType<RectOp>().First(r => r.Color == color);
    private static TextOp Text(List<DrawOp> ops, string word) => Flatten(ops).OfType<TextOp>().First(t => t.Text.Contains(word, StringComparison.Ordinal));

    [Fact]
    public void ColspanWidthIsSharedByTheColumnsContent()
    {
        // Chrome gives the columns under a wide colspan cell room in proportion to their own content, not equal shares.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='width:100%;border-spacing:0'>" +
                         "<tr><td colspan='3'>A colspan cell with quite a lot of text that sets the minimum for the columns below it</td></tr>" +
                         "<tr><td>a</td><td>bbbb</td><td>cccc cccc cccc cccc</td></tr></table>");
        float a = Text(ops, "a").X, b = Text(ops, "bbbb").X, c = Text(ops, "cccc").X;
        Assert.True(b - a < (c - b) / 2, $"columns at {a}, {b}, {c}");
    }

    [Fact]
    public void ColspanPercentageGoesToTheColumnsWithout()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='width:400px;border-spacing:0'>" +
                         "<tr><td colspan='2' style='width:50%'>x</td><td>y</td></tr><tr><td>p</td><td>q</td><td>r</td></tr></table>");
        Assert.Equal(150, Text(ops, "r").X, 1); // 400px is 300pt: p and q get 25% each
    }

    [Fact]
    public void PercentagesOverAHundredAreCutFromTheLastColumn()
    {
        // As in Chrome, 60% + 60% become 60% + 40%: the table keeps its width instead of overflowing.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='width:100%;border-spacing:0'><tr>" +
                         "<td style='width:60%;background:#f00'>one</td><td style='width:60%;background:#00f'>two</td></tr></table>");
        var one = Rect(ops, 0xFFFF0000);
        var two = Rect(ops, 0xFF0000FF);
        Assert.Equal(300, one.W, 1);
        Assert.Equal(200, two.W, 1);
        Assert.Equal(Width, two.X + two.W, 1);
    }

    [Fact]
    public void ALongWordWidensATableWithAWidth()
    {
        // Chrome widens a 250px table to the URL's min-content width rather than letting the URL overflow its cell.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='width:250px;border-spacing:0;background:#f00'><tr>" +
                         "<td>https://example.com/a/very/long/unbreakable/url/in/a/narrow/table/cell</td><td style='background:#00f'>b</td></tr></table>");
        var table = Rect(ops, 0xFFFF0000);
        var b = Rect(ops, 0xFF0000FF);
        Assert.True(table.W > 250 * 0.75f + 50, $"table {table.W}pt wide");
        Assert.Equal(table.X + table.W, b.X + b.W, 1);
        Assert.True(b.X > 250 * 0.75f, $"second cell at {b.X}pt");
    }
}

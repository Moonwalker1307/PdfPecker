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

    private static string LongTable(string style, int rows = 60) =>
        "<style>body{margin:0} td{padding:0;height:20pt}" + style + "</style><table style='border-collapse:collapse;width:300px'><tbody>"
        + string.Concat(Enumerable.Range(1, rows).Select(i => $"<tr><td>Row {i}</td><td>{i}</td></tr>"))
        + "</tbody><tfoot><tr><td>Total</td><td>60</td></tr></tfoot></table>";

    [Fact]
    public void ARowBackgroundLeavesTheBorderAboveItUncovered()
    {
        // The cell above draws the shared line before the row's background is painted: the background starts where the
        // line ends rather than covering its lower half, as Chrome paints the line over the background.
        var ops = Layout("<style>body{margin:0} td{padding:0;border:2px solid #000}</style><table style='border-collapse:collapse'>" +
                         "<tr><td>one</td></tr><tr style='background:#0c0'><td>two</td></tr></table>");
        var green = Assert.Single(Rects(ops, 0xFF00CC00));
        var line = Rects(ops, 0xFF000000).Single(r => r.W > r.H && r.Y > Text(ops, "one").Baseline && r.Y < Text(ops, "two").Baseline);
        Assert.Equal(line.Y + line.H, green.Y, 2);
    }

    [Fact]
    public void ARowBackgroundLeavesTheTableBorderUncovered()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-collapse:collapse;border:4px solid #000'>" +
                         "<tr style='background:#0c0'><td>one</td></tr></table>");
        var green = Assert.Single(Rects(ops, 0xFF00CC00));
        // The table's 3-point border is centred on the grid line at 1.5 points, so its inner half ends at 3.
        Assert.Equal(3, green.X, 2);
        Assert.Equal(3, green.Y, 2);
    }

    [Fact]
    public void AFooterRepeatedUnderABreakKeepsTheLineAboveIt()
    {
        // The footer's row background starts where the line drawn by the last row on the page ends.
        var ops = Layout(LongTable("td{border:1px solid #000} tfoot tr{background:#0c0}"));
        var greens = Rects(ops, 0xFF00CC00).OrderBy(r => r.Y).ToList();
        Assert.True(greens.Count >= 2);
        var first = greens[0];
        Assert.Contains(Rects(ops, 0xFF000000), r => r.W > r.H && r.X <= first.X && Math.Abs(r.Y + r.H - first.Y) < 0.01f);
    }

    [Fact]
    public void AFooterRepeatedUnderABreakDrawsTheLineTheLastBodyRowWins()
    {
        // Lines above each cell only, and a wide blue line under the table's last body row, which wins the line above
        // the footer. On the first page the row above the footer leaves its bottom line to the row that moved to the
        // next page, so the repeated footer draws the blue line itself, as Chrome does.
        var ops = Layout(LongTable("td{border-top:1px solid #000} tbody tr:last-child td{border-bottom:3px solid #00f}"));
        var footers = Flatten(ops).OfType<TextOp>().Where(t => t.Text == "Total").ToList();
        Assert.True(footers.Count >= 2);
        var blue = Rects(ops, 0xFF0000FF);
        Assert.Contains(blue, r => r.W > r.H && r.Y < footers[0].Baseline && r.Y > footers[0].Baseline - 30);
    }

    [Fact]
    public void ATableStopsUnderItsFooterRepeatedUnderABreak()
    {
        // As in Chrome, the table's own border closes under the footer repeated on each page, instead of its sides
        // running on to the bottom of the page with no line under the footer.
        var ops = Layout(LongTable("table{border:3px solid #f00} td{border:1px solid #000}"));
        var footer = Flatten(ops).OfType<TextOp>().First(t => t.Text == "Total");
        var red = Rects(ops, 0xFFFF0000);
        var left = red.Where(r => r.H > r.W).MinBy(r => r.Y)!;
        var under = red.Single(r => r.W > r.H && r.Y > footer.Baseline && r.Y < footer.Baseline + 20);
        Assert.Equal(under.Y + under.H, left.Y + left.H, 2);
        Assert.True(left.Y + left.H < PageHeight - 5);
    }

    [Fact]
    public void ATableWithoutAFooterStopsUnderTheLastRowOnAPage()
    {
        // Lines above each cell only: the row that moved to the next page draws the line above it there, and Chrome
        // draws it under the last row on the page too, where the table's sides stop.
        var ops = Layout("<style>body{margin:0} td{padding:0;height:23pt;border-top:1px solid #000} table{border:3px solid #f00}</style>"
                         + "<table style='border-collapse:collapse;width:300px'>"
                         + string.Concat(Enumerable.Range(1, 60).Select(i => $"<tr><td>Row {i}</td><td>{i}</td></tr>")) + "</table>");
        var left = Rects(ops, 0xFFFF0000).Where(r => r.H > r.W).MinBy(r => r.Y)!;
        float bottom = left.Y + left.H;
        Assert.True(bottom < PageHeight - 5);
        Assert.Contains(Rects(ops, 0xFF000000), r => r.W > r.H && Math.Abs(r.Y + r.H - bottom) < 0.01f);
    }

    [Fact]
    public void ARowspanCellCrossingABreakStopsAboveTheRepeatedFooter()
    {
        var html = "<style>body{margin:0} td{padding:0;height:20pt;border:1px solid #000} tr{break-inside:avoid}</style>"
                   + "<table style='border-collapse:collapse;width:300px'><tbody>"
                   + string.Concat(Enumerable.Range(1, 60).Select(i => i % 4 == 1
                       ? $"<tr><td rowspan='4' style='background:#0c0'>S{i}</td><td>Row {i}</td></tr>"
                       : $"<tr><td>Row {i}</td></tr>"))
                   + "</tbody><tfoot><tr><td colspan='2'>Total</td></tr></tfoot></table>";
        var ops = Layout(html);
        var footer = Flatten(ops).OfType<TextOp>().First(t => t.Text == "Total");
        // The spanning cell is painted page by page, its first part clipped where the footer starts.
        var clip = Flatten(ops).OfType<ClipOp>().First(c => Flatten(c.Ops).OfType<RectOp>().Any(r => r.Color == 0xFF00CC00));
        Assert.True(clip.Y + clip.H < footer.Baseline - 10);
        Assert.True(clip.Y + clip.H > footer.Baseline - 25);
    }
}


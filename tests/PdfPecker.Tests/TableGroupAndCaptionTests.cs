using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>Row group and column backgrounds, caption-side, min-width and max-width on cells, and empty-cells.</summary>
public class TableGroupAndCaptionTests
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

    private static List<RectOp> Rects(List<DrawOp> ops, uint color) => Flatten(ops).OfType<RectOp>().Where(r => r.Color == color).ToList();
    private static TextOp Text(List<DrawOp> ops, string word) => Flatten(ops).OfType<TextOp>().First(t => t.Text.Contains(word, StringComparison.Ordinal));

    [Fact]
    public void RowGroupBackgroundsArePaintedUnderTheirRows()
    {
        var ops = Layout("<style>body{margin:0}</style><table><thead style='background:#036'><tr><th>head</th></tr></thead>" +
                         "<tbody style='background:#00f'><tr><td>one</td></tr><tr><td>two</td></tr></tbody></table>");
        Assert.Single(Rects(ops, 0xFF003366));
        var body = Rects(ops, 0xFF0000FF);
        Assert.Equal(2, body.Count);
        Assert.True(body[0].Y < Text(ops, "one").Baseline && Text(ops, "two").Baseline < body[1].Y + body[1].H);
    }

    [Fact]
    public void ColumnBackgroundsArePaintedUnderTheirCells()
    {
        var ops = Layout("<style>body{margin:0} td{width:50px}</style><table><colgroup style='background:#f00'><col><col style='background:#00f'></colgroup>" +
                         "<tr><td>a</td><td>b</td></tr></table>");
        var group = Rects(ops, 0xFFFF0000);
        Assert.Equal(2, group.Count); // the group's colour under both columns
        var col = Assert.Single(Rects(ops, 0xFF0000FF));
        Assert.True(col.X > group.Min(r => r.X));
        // Bottom to top: the column group, then the column.
        var all = Flatten(ops).OfType<RectOp>().ToList();
        Assert.True(all.IndexOf(col) > all.IndexOf(group[1]));
    }

    [Fact]
    public void BottomCaptionGoesUnderTheTable()
    {
        var ops = Layout("<style>body{margin:0}</style><table style='caption-side:bottom'><caption>below</caption><tr><td>cell</td></tr></table>" +
                         "<p style='margin:0'>after</p>");
        Assert.True(Text(ops, "below").Baseline > Text(ops, "cell").Baseline);
        Assert.True(Text(ops, "after").Baseline > Text(ops, "below").Baseline);
    }

    [Fact]
    public void CaptionWidensANarrowTable()
    {
        var ops = Layout("<style>body{margin:0}</style><table style='border-collapse:collapse'><caption>Supercalifragilistic</caption>" +
                         "<tr><td style='background:#f00'>x</td></tr></table>");
        var cell = Assert.Single(Rects(ops, 0xFFFF0000));
        Assert.True(cell.W > 80, $"the cell is {cell.W}pt wide");
    }

    [Fact]
    public void MaxWidthOnACellCapsItsShareOfTheTable()
    {
        // As in Chrome: the extra room goes to the columns by their max-content widths, the capped one's at its max-width.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='width:400px;border-spacing:0'><tr>" +
                         "<td style='min-width:100px;background:#f00'>a</td><td style='max-width:50px;background:#00f'>bbb bbb bbb bbb bbb bbb bbb bbb</td></tr></table>");
        var a = Assert.Single(Rects(ops, 0xFFFF0000));
        var b = Assert.Single(Rects(ops, 0xFF0000FF));
        Assert.Equal(300, a.W + b.W, 1);
        Assert.Equal(200, a.W, 1); // 100px against 50px: two thirds
    }

    [Fact]
    public void MaxWidthOnANowrapCellNarrowsItsColumn()
    {
        // As in Chrome, max-width caps the cell's min-content width too: the nowrap text overflows the 60px cell.
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-spacing:0'><tr>" +
                         "<td style='max-width:60px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;background:#f00'>" +
                         "A caption much too long for its cell</td><td style='background:#00f'>next</td></tr></table>");
        var a = Assert.Single(Rects(ops, 0xFFFF0000));
        var b = Assert.Single(Rects(ops, 0xFF0000FF));
        Assert.Equal(45, a.W, 1); // 60px
        Assert.Equal(a.X + a.W, b.X, 1);
    }

    [Fact]
    public void MinWidthWinsOverMaxWidthAndWidthOnACell()
    {
        var ops = Layout("<style>body{margin:0} td{padding:0}</style><table style='border-spacing:0'><tr>" +
                         "<td style='width:200px;max-width:50px;background:#f00'>x</td>" +
                         "<td style='width:200px;max-width:50px;min-width:80px;background:#00f'>y</td></tr></table>");
        Assert.Equal(37.5, Assert.Single(Rects(ops, 0xFFFF0000)).W, 1); // 50px
        Assert.Equal(60, Assert.Single(Rects(ops, 0xFF0000FF)).W, 1); // 80px
    }

    [Fact]
    public void HiddenEmptyCellsPaintNothing()
    {
        var ops = Layout("<style>body{margin:0} td{background:#f00}</style><table style='empty-cells:hide'><tr><td>full</td><td> </td></tr></table>");
        Assert.Single(Rects(ops, 0xFFFF0000));
    }
}

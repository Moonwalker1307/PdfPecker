using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Anonymous table boxes (display: table on divs, inline-table, lone cells), details and summary, list markers inside
/// the content, and progress and meter elements, laid out as Chrome does.
/// </summary>
public class AnonymousTableTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string html, int stylePasses = 1)
    {
        var doc = HtmlParser.Parse(html);
        FormControls.Prepare(doc, false);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        for (int i = 0; i < stylePasses; i++)
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

    private static List<TextOp> Texts(List<DrawOp> ops) => Flatten(ops).OfType<TextOp>().ToList();
    private static TextOp Text(List<DrawOp> ops, string word) => Texts(ops).First(t => t.Text.Contains(word, StringComparison.Ordinal));
    private static string AllText(List<DrawOp> ops) => string.Concat(Texts(ops).Select(t => t.Text));
    private static RectOp Rect(List<DrawOp> ops, uint color) => Flatten(ops).OfType<RectOp>().First(r => r.Color == color);

    private static (float X, float W) Fill(List<DrawOp> ops, uint color)
    {
        foreach (var op in Flatten(ops))
        {
            if (op is RectOp r && r.Color == color) return (r.X, r.W);
            if (op is RoundRectOp rr && rr.Color == color) return (rr.X, rr.W);
        }
        throw new InvalidOperationException($"no fill {color:X8}");
    }

    [Fact]
    public void TableDivWithOnlyTextKeepsItsTextAndShrinks()
    {
        var ops = Layout("<style>body{margin:0}</style><div style='display:table;background:#00f;padding:5px'>only text</div>");
        Assert.Contains("only text", AllText(ops));
        var box = Rect(ops, 0xFF0000FF);
        Assert.True(box.W < 100, $"the table shrinks to its text, not {box.W}pt");
        Assert.True(Text(ops, "only").X > box.X);
    }

    [Fact]
    public void InlineTableSitsOnTheLine()
    {
        var ops = Layout("<style>body{margin:0}</style><div>before <div style='display:inline-table;border:1px solid'>inner</div> after</div>");
        var before = Text(ops, "before");
        var inner = Text(ops, "inner");
        var after = Text(ops, "after");
        Assert.Equal(before.Baseline, inner.Baseline, 1);
        Assert.Equal(before.Baseline, after.Baseline, 1);
        Assert.True(before.X < inner.X && inner.X < after.X);
    }

    [Fact]
    public void CellsWithoutARowShareOne()
    {
        var ops = Layout("<style>body{margin:0} .c{display:table-cell;padding:2px}</style>" +
                         "<div style='display:table'><div class=c>alpha</div><div class=c>beta</div></div>");
        var a = Text(ops, "alpha");
        var b = Text(ops, "beta");
        Assert.Equal(a.Baseline, b.Baseline, 1);
        Assert.True(b.X > a.X + 20);
    }

    [Fact]
    public void LoneCellsBecomeATableAndAlignVertically()
    {
        var ops = Layout("<style>body{margin:0}</style>" +
                         "<div style='display:table-cell;height:100px;vertical-align:middle;background:#00f'>middle</div>" +
                         "<div style='display:table-cell;vertical-align:bottom;background:#f00'>bottom</div>");
        var middle = Text(ops, "middle");
        var bottom = Text(ops, "bottom");
        Assert.True(middle.Baseline > 30 && middle.Baseline < 50, $"middle at {middle.Baseline}");
        Assert.True(bottom.Baseline > 65, $"bottom at {bottom.Baseline}");
        Assert.True(bottom.X > middle.X, "both cells are in one row");
        Assert.Equal(75, Rect(ops, 0xFFFF0000).H, 1); // the row's height
    }

    [Fact]
    public void TableDivsHaveNoBorderSpacing()
    {
        // border-spacing is 0 unless set: only the user agent style gives <table> its 2px.
        var ops = Layout("<style>body{margin:0} .c{display:table-cell;width:20px;height:10px}</style>" +
                         "<div style='display:table'><div class=c style='background:#f00'></div><div class=c style='background:#00f'></div></div>");
        Assert.Equal(0, Rect(ops, 0xFFFF0000).X, 1);
        Assert.Equal(15, Rect(ops, 0xFF0000FF).X, 1);
    }

    [Fact]
    public void AnonymousBoxesSurviveAnotherStylePass()
    {
        var ops = Layout("<style>body{margin:0} .c{display:table-cell}</style><div style='display:table'>t1<div class=c>c1</div></div>" +
                         "<div class=c>c2</div>", stylePasses: 3);
        Assert.Equal(1, Texts(ops).Count(t => t.Text.Contains("c1", StringComparison.Ordinal)));
        Assert.Equal(1, Texts(ops).Count(t => t.Text.Contains("c2", StringComparison.Ordinal)));
        Assert.Equal(1, Texts(ops).Count(t => t.Text.Contains("t1", StringComparison.Ordinal)));
    }

    [Fact]
    public void FormLeftInARowTakesNoCell()
    {
        var ops = Layout("<style>body{margin:0} td{width:100px}</style>" +
                         "<table><tr><td>a1</td><td>a2</td></tr><tr><form></form><td>b1</td><td>b2</td></tr></table>");
        Assert.Equal(Text(ops, "a1").X, Text(ops, "b1").X, 1);
        Assert.Equal(Text(ops, "a2").X, Text(ops, "b2").X, 1);
    }

    [Fact]
    public void ClosedDetailsShowOnlyTheSummary()
    {
        var ops = Layout("<style>body{margin:0}</style><details><summary>Label</summary>loose text<p>hidden paragraph</p></details>" +
                         "<details open><summary>Open</summary><p>shown paragraph</p></details>");
        var text = AllText(ops);
        Assert.Contains("Label", text);
        Assert.DoesNotContain("loose", text);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("shown paragraph", text);
        // Each summary starts with its triangle, inside the content box.
        var triangles = Flatten(ops).OfType<SvgOp>().ToList();
        Assert.Equal(2, triangles.Count);
        Assert.All(triangles, t => Assert.True(t.X >= 0));
        Assert.True(Text(ops, "Label").X > triangles[0].X + triangles[0].W);
    }

    [Fact]
    public void InsideMarkersStartTheFirstLine()
    {
        var ops = Layout("<style>body{margin:0;font:12pt Arial} ol{margin:0;padding:0;width:200px;list-style-position:inside}</style>" +
                         "<ol><li>first words of an item that wraps onto another line</li></ol>");
        var marker = Text(ops, "1.");
        Assert.Equal(0, marker.X, 1);
        var lines = Texts(ops).Where(t => t != marker).GroupBy(t => t.Baseline).OrderBy(g => g.Key).ToList();
        Assert.True(lines.Count >= 2);
        Assert.True(lines[0].Min(t => t.X) > marker.X + 5);
        Assert.Equal(0, lines[1].Min(t => t.X), 1); // the next line starts under the marker
    }

    [Fact]
    public void ProgressAndMeterDrawTheirBars()
    {
        var ops = Layout("<style>body{margin:0;font-size:16px}</style><progress value='70' max='100'>70%</progress> " +
                         "<meter value='0.2' low='0.3' optimum='0.8'>low</meter> <meter value='0.6'></meter>");
        Assert.DoesNotContain("70%", AllText(ops));
        var (_, track) = Fill(ops, 0xFFEFEFEF);
        Assert.Equal(120, track, 1); // 10em
        var (_, bar) = Fill(ops, 0xFF0075FF);
        Assert.Equal(0.7f * 118.5f + 0.75f, bar, 1);
        Assert.Equal(0.6f * 58.5f, Fill(ops, 0xFF107C10).W, 1); // in the optimum range: green
        Assert.Equal(0.2f * 58.5f, Fill(ops, 0xFFFFB900).W, 1); // below the optimum range: yellow
    }
}

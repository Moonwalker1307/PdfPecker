using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// display: contents: the element makes no box, and its children, ::before and ::after included, take its place in
/// flex, grid, table and ordinary layout, as in Chrome.
/// </summary>
public class DisplayContentsTests
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
    private static bool HasFill(List<DrawOp> ops, uint color) => Flatten(ops).Any(op => op is RectOp r && r.Color == color);

    private const string Items = "<style>body{margin:0} .flex{display:flex;gap:10px} .grid{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px}"
                                 + " .c{display:contents} .i{padding:4px}</style>";

    [Theory]
    [InlineData(1)]
    [InlineData(3)] // the next style passes give the wrapper its children back first
    public void ChildrenOfAWrapperAreFlexItems(int passes)
    {
        var ops = Layout(Items + "<div class=flex><div class=c><div class=i>alpha</div><div class=i>beta</div></div><div class=i>gamma</div></div>", passes);
        var (a, b, g) = (Text(ops, "alpha"), Text(ops, "beta"), Text(ops, "gamma"));
        Assert.Equal(a.Baseline, b.Baseline, 1);
        Assert.Equal(a.Baseline, g.Baseline, 1);
        Assert.True(a.X < b.X && b.X < g.X);
    }

    [Fact]
    public void ListItemsOfAWrapperListAreGridCells()
    {
        var ops = Layout(Items + "<div class=grid><ul class=c><li>one</li><li>two</li><li>three</li><li>four</li></ul><div>five</div></div>");
        var (one, two, three, four, five) = (Text(ops, "one"), Text(ops, "two"), Text(ops, "three"), Text(ops, "four"), Text(ops, "five"));
        Assert.Equal(one.Baseline, three.Baseline, 1);
        Assert.True(one.X < two.X && two.X < three.X);
        Assert.Equal(one.X, four.X, 1);
        Assert.Equal(four.Baseline, five.Baseline, 1);
        Assert.True(four.Baseline > one.Baseline);
    }

    [Fact]
    public void NestedWrappersAreAllLookedThrough()
    {
        var ops = Layout(Items + "<div class=grid><div class=c><div class=c><div>n1</div><div>n2</div></div><div>n3</div></div><div>n4</div></div>");
        Assert.Equal(Text(ops, "n1").Baseline, Text(ops, "n3").Baseline, 1);
        Assert.True(Text(ops, "n4").Baseline > Text(ops, "n1").Baseline);
        Assert.Equal(Text(ops, "n1").X, Text(ops, "n4").X, 1);
    }

    [Fact]
    public void BeforeAndAfterBoxesBecomeItemsToo()
    {
        var ops = Layout(Items + "<style>.c::before{content:'first'} .c::after{content:'last'}</style>"
                               + "<div class=flex><div class=c><div>middle</div></div></div>");
        var (first, middle, last) = (Text(ops, "first"), Text(ops, "middle"), Text(ops, "last"));
        Assert.Equal(first.Baseline, last.Baseline, 1);
        Assert.True(first.X < middle.X && middle.X < last.X);
    }

    [Fact]
    public void TheWrapperDrawsNoBoxButPassesOnItsInheritedStyle()
    {
        var ops = Layout("<style>body{margin:0}</style><div style='display:contents;background:#ff0;border:4px solid #f00;padding:20px;margin:30px;color:#00f'>"
                         + "<p style='margin:0'>para</p></div>");
        Assert.False(HasFill(ops, 0xFFFFFF00));
        Assert.DoesNotContain(Flatten(ops), op => op is LineOp or BorderRectOp);
        var para = Text(ops, "para");
        Assert.Equal(0xFF0000FFu, para.Color);
        Assert.True(para.X < 1, $"no padding or margin moves the text, at {para.X}");
    }

    [Fact]
    public void TextInAWrapperInAFlexBoxKeepsTheWrapperStyle()
    {
        var ops = Layout(Items + "<div class=flex><div class=c style='color:#0f0'>loose text</div><div>item</div></div>");
        var loose = Text(ops, "loose");
        Assert.Equal(0xFF00FF00u, loose.Color);
        Assert.Equal(loose.Baseline, Text(ops, "item").Baseline, 1);
    }

    [Fact]
    public void RowsAndCellsInAWrapperJoinTheTable()
    {
        var ops = Layout("<style>body{margin:0} .t{display:table} .r{display:table-row} .d{display:table-cell;padding:2px}</style>"
                         + "<div class=t><div class=c style='display:contents'><div class=r><div class=d>a1</div><div class=d>a2</div></div>"
                         + "<div class=r><div class=d>b1</div><div class=d>b2</div></div></div></div>"
                         + "<div class=t><div class=r><div style='display:contents'><div class=d>x1</div><div class=d>x2</div></div><div class=d>x3</div></div></div>");
        Assert.Equal(Text(ops, "a1").X, Text(ops, "b1").X, 1);
        Assert.Equal(Text(ops, "a2").X, Text(ops, "b2").X, 1);
        Assert.Equal(Text(ops, "a1").Baseline, Text(ops, "a2").Baseline, 1);
        Assert.Equal(Text(ops, "x1").Baseline, Text(ops, "x3").Baseline, 1);
        Assert.True(Text(ops, "x1").X < Text(ops, "x2").X && Text(ops, "x2").X < Text(ops, "x3").X);
    }

    [Fact]
    public void ATableRowGroupWithDisplayContentsKeepsItsRows()
    {
        var ops = Layout("<style>body{margin:0}</style><table><tbody style='display:contents'><tr><td>r1c1</td><td>r1c2</td></tr>"
                         + "<tr><td>r2c1</td><td>r2c2</td></tr></tbody></table>");
        Assert.Equal(Text(ops, "r1c1").X, Text(ops, "r2c1").X, 1);
        Assert.Equal(Text(ops, "r1c1").Baseline, Text(ops, "r1c2").Baseline, 1);
        Assert.True(Text(ops, "r2c1").Baseline > Text(ops, "r1c1").Baseline);
    }

    [Fact]
    public void AnInlineWrapperLosesItsBoxButKeepsItsText()
    {
        var ops = Layout("<style>body{margin:0}</style><p>before <span style='display:contents;background:#f00;padding:10px;border:2px solid'>inside</span> after</p>");
        Assert.Contains("before inside after", AllText(ops));
        Assert.False(HasFill(ops, 0xFFFF0000));
        Assert.Equal(Text(ops, "before").Baseline, Text(ops, "inside").Baseline, 1);
    }

    [Fact]
    public void LinksWithDisplayContentsStillLink()
    {
        var ops = Layout(Items + "<div class=grid><a class=c href='https://example.com/one'><div>card one</div></a>"
                               + "<a class=c href='https://example.com/two'><div>card two</div></a></div>"
                               + "<p>inline <a class=c href='https://example.com/three'>three</a></p>"
                               + "<div class=flex><a class=c href='https://example.com/four'>four</a></div>");
        var targets = Flatten(ops).OfType<LinkOp>().Select(l => l.Target).ToList();
        Assert.Contains("https://example.com/one", targets);
        Assert.Contains("https://example.com/two", targets);
        Assert.Contains("https://example.com/three", targets);
        Assert.Contains("https://example.com/four", targets);
    }

    [Fact]
    public void ALinkWithDisplayContentsIsNotUnderlined()
    {
        // Chrome draws a link's underline on the link's box; without one, its text is not underlined.
        var plain = Layout("<style>body{margin:0}</style><p>text</p>");
        var ops = Layout("<style>body{margin:0}</style><p><a style='display:contents' href='https://example.com'>text</a></p>");
        Assert.Equal(Flatten(plain).Count(op => op is not TextOp), Flatten(ops).Count(op => op is not (TextOp or LinkOp)));
    }

    [Fact]
    public void ReplacedElementsAndControlsWithDisplayContentsAreNotShown()
    {
        var ops = Layout("<p>[<img style='display:contents' src='data:image/gif;base64,R0lGODlhAQABAIAAAP8AAAAAACwAAAAAAQABAAACAkQBADs=' width=20 height=20>]"
                         + "[<input style='display:contents' value='field'>]</p>");
        Assert.DoesNotContain(Flatten(ops), op => op is ImageOp);
        Assert.DoesNotContain("field", AllText(ops));
    }

    [Fact]
    public void SelectorsStillSeeTheDocumentTree()
    {
        // Before a second style pass the wrapper gets its children back, so child and :first-child selectors match as
        // in the document, not as in the boxes.
        var ops = Layout(Items + "<style>.c > div:first-child{color:#f00} .flex > div:first-child{color:#00f}</style>"
                               + "<div class=flex><div class=c><div>one</div><div>two</div></div><div>three</div></div>", stylePasses: 2);
        Assert.Equal(0xFFFF0000u, Text(ops, "one").Color);
        Assert.NotEqual(0xFFFF0000u, Text(ops, "two").Color);
        Assert.NotEqual(0xFF0000FFu, Text(ops, "one").Color);
    }

    [Fact]
    public void CountersStillCountInAWrapper()
    {
        var ops = Layout("<style>body{margin:0;counter-reset:n} .c{display:contents;counter-increment:n} .c::before{content:counter(n) '. '}</style>"
                         + "<div class=c>a</div><div class=c>b</div>");
        var text = AllText(ops);
        Assert.True(text.IndexOf("1.", StringComparison.Ordinal) is >= 0 and var one && text.IndexOf("2.", StringComparison.Ordinal) > one, text);
    }
}

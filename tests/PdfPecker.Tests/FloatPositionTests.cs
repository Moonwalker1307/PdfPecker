using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Floats, clear, positioning, z-index, transforms, ::before/::after and counters.</summary>
public class FloatPositionTests
{
    private const float Width = 500, PageHeight = 800;

    private static (List<DrawOp> Ops, List<DrawOp> Fixed) Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return (engine.Ops, engine.FixedOps);
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
    private static List<TextOp> Texts(List<DrawOp> ops) => Flatten(ops).OfType<TextOp>().ToList();
    private static string AllText(List<DrawOp> ops) => string.Concat(Texts(ops).Select(t => t.Text));

    [Fact]
    public void LeftFloatPushesTextRight()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:left;width:100px;height:60px;background:#f00}</style>" +
                              "<div class=f></div><p style='margin:0'>Hello beside</p>");
        var f = Rect(ops, 0xFFFF0000);
        Assert.Equal(0, f.X, 1);
        Assert.Equal(75, f.W, 1); // 100px
        var t = Texts(ops).First();
        Assert.True(t.X >= 75 - 0.01f, $"text at {t.X}");
        Assert.True(t.LineTop < 45);
    }

    [Fact]
    public void RightFloatSitsAtRightEdge()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:right;width:100px;height:20px;background:#00f}</style><div class=f></div>");
        var f = Rect(ops, 0xFF0000FF);
        Assert.Equal(Width - 75, f.X, 1);
    }

    [Fact]
    public void ClearMovesBelowFloat()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:left;width:100px;height:60px;background:#f00}" +
                              ".c{clear:both;height:10px;background:#0f0}</style><div class=f></div><div class=c></div>");
        Assert.Equal(45, Rect(ops, 0xFF00FF00).Y, 1);
    }

    [Fact]
    public void ClearfixContainsFloats()
    {
        var (ops, _) = Layout("<style>body{margin:0} .w{background:#00f} .w::after{content:'';display:table;clear:both}" +
                              ".f{float:left;width:50px;height:80px}</style><div class=w><div class=f></div></div><div style='height:5px;background:#0f0'></div>");
        Assert.Equal(60, Rect(ops, 0xFF0000FF).H, 1);
        Assert.Equal(60, Rect(ops, 0xFF00FF00).Y, 1);
    }

    [Fact]
    public void EmptyClearingDivMovesNextBlockBelowFloat()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:left;width:100px;height:60px}</style>" +
                              "<div class=f></div><div style='clear:both'></div><div style='height:10px;background:#0f0'></div>");
        Assert.Equal(45, Rect(ops, 0xFF00FF00).Y, 1);
    }

    [Fact]
    public void EmptyClearingDivContainsFloats()
    {
        var (ops, _) = Layout("<style>body{margin:0} .w{background:#00f} .f{float:left;width:50px;height:80px}</style>" +
                              "<div class=w><div class=f></div><div style='clear:left'></div></div><div style='height:5px;background:#0f0'></div>");
        Assert.Equal(60, Rect(ops, 0xFF0000FF).H, 1);
        Assert.Equal(60, Rect(ops, 0xFF00FF00).Y, 1);
    }

    [Fact]
    public void BrClearAllMovesWhatFollowsBelowFloat()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:left;width:100px;height:60px}</style>" +
                              "<div><div class=f></div>a<br clear=all></div><div style='height:10px;background:#0f0'></div>");
        Assert.Equal(45, Rect(ops, 0xFF00FF00).Y, 1);
    }

    [Fact]
    public void BrClearClearsOnlyItsSide()
    {
        var (ops, _) = Layout("<style>body{margin:0} .f{float:right;width:100px;height:60px}</style>" +
                              "<div><div class=f></div>a<br clear=left></div><div style='height:10px;background:#0f0'></div>");
        Assert.True(Rect(ops, 0xFF00FF00).Y < 30);
    }

    [Fact]
    public void FlowRootContainsFloats()
    {
        var (ops, _) = Layout("<style>body{margin:0} .w{display:flow-root;background:#00f} .f{float:left;width:50px;height:40px}</style>" +
                              "<div class=w><div class=f></div></div>");
        Assert.Equal(30, Rect(ops, 0xFF0000FF).H, 1);
    }

    [Fact]
    public void AbsoluteUsesPositionedAncestor()
    {
        var (ops, _) = Layout("<style>body{margin:0} .cb{position:relative;margin:40px;height:100px}" +
                              ".a{position:absolute;right:10px;bottom:10px;width:20px;height:20px;background:#f00}</style>" +
                              "<div class=cb><div class=a></div></div>");
        var a = Rect(ops, 0xFFFF0000);
        Assert.Equal(Width - 30 - 7.5f - 15, a.X, 1);
        Assert.Equal(30 + 75 - 7.5f - 15, a.Y, 1);
    }

    [Fact]
    public void RelativeOffsetsShiftBox()
    {
        var (ops, _) = Layout("<style>body{margin:0} .r{position:relative;left:20px;top:10px;height:10px;background:#f00}</style><div class=r></div>");
        var r = Rect(ops, 0xFFFF0000);
        Assert.Equal(15, r.X, 1);
        Assert.Equal(7.5f, r.Y, 1);
    }

    [Fact]
    public void ZIndexOrdersPainting()
    {
        var (ops, _) = Layout("<style>body{margin:0} div{position:absolute;width:10px;height:10px}</style>" +
                              "<div style='z-index:2;background:#f00'></div><div style='z-index:1;background:#00f'></div>");
        var rects = Flatten(ops).OfType<RectOp>().ToList();
        Assert.True(rects.FindIndex(r => r.Color == 0xFF0000FF) < rects.FindIndex(r => r.Color == 0xFFFF0000));
    }

    [Fact]
    public void FixedBoxGoesToEveryPage()
    {
        var (ops, fixedOps) = Layout("<style>body{margin:0} .x{position:fixed;bottom:0;left:0;width:10px;height:10px;background:#f00}</style>" +
                                     "<div class=x></div><div style='height:3000px'></div>");
        Assert.DoesNotContain(Flatten(ops).OfType<RectOp>(), r => r.Color == 0xFFFF0000);
        // Chrome floors the page area to whole px: 800pt is 1066.67px, so 1066px = 799.5pt.
        Assert.Equal(799.5f - 7.5f, Rect(fixedOps, 0xFFFF0000).Y, 1);

        var pdf = ConvertUncompressed("<div style='position:fixed;top:0'>FIXEDMARK</div><div style='height:3000px'></div>");
        Assert.Equal(PageCount(pdf), CountOf(AllContent(pdf), "FIXEDMARK"));
    }

    private static int CountOf(string s, string word)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(word, i, StringComparison.Ordinal)) >= 0) { n++; i += word.Length; }
        return n;
    }

    [Fact]
    public void TransformBecomesLayerMatrix()
    {
        var (ops, _) = Layout("<style>body{margin:0} .t{transform:rotate(90deg) scale(2);width:100px;height:50px;background:#f00}</style><div class=t></div>");
        var layer = Flatten(ops).OfType<LayerOp>().First(l => l.Transform != null);
        Assert.Equal(37.5f, layer.OriginX, 1);
        Assert.Equal(18.75f, layer.OriginY, 1);
        var pdf = ConvertUncompressed("<div style='transform:translate(10px,5px) rotate(45deg)'>Rotated</div>");
        AssertStructurallyValid(pdf);
        Assert.Contains(" cm", AllContent(pdf));
    }

    [Fact]
    public void InvalidTransformIsIgnored()
    {
        Assert.False(CssTransform.TryParse("rotate3d(1,1,45deg)", 16, 16, 5, 5, out _));
        Assert.False(CssTransform.TryParse("translate3d(1px,2px,10%)", 16, 16, 5, 5, out _));
        Assert.False(CssTransform.TryParse(string.Concat(Enumerable.Repeat("scale(1) ", 40)), 16, 16, 5, 5, out _));
        Assert.True(CssTransform.TryParse("translatex(10%) skew(10deg, 5deg) matrix(1,0,0,1,4,4)", 16, 16, 5, 5, out var fns));
        Assert.Equal(3, fns.Length);
    }

    [Fact]
    public void BeforeAndAfterGenerateText()
    {
        var (ops, _) = Layout("<style>p::before{content:'[' attr(data-x) ']'} p:after{content:\"\\201C end\"}</style><p data-x=7>mid</p>");
        var text = AllText(ops);
        Assert.Contains("[7]", text);
        Assert.Contains("“end", text.Replace(" ", ""));
        Assert.True(text.IndexOf("[7]") < text.IndexOf("mid"));
    }

    [Fact]
    public void CountersNumberSections()
    {
        var (ops, _) = Layout("<style>body{counter-reset:s} h2{counter-increment:s} h2::before{content:counter(s, upper-roman) '. '}" +
                              "ol{counter-reset:i} li{display:block;counter-increment:i} li::before{content:counters(i,'.') ' '}</style>" +
                              "<h2>A</h2><h2>B</h2><ol><li>x<ol><li>y</li></ol></li></ol>");
        var text = AllText(ops).Replace(" ", "");
        Assert.Contains("I.A", text);
        Assert.Contains("II.B", text);
        Assert.Contains("1.1y", text);
    }

    [Fact]
    public void CountersWithAnEmptySeparatorAreIgnored()
    {
        // Used to throw IndexOutOfRangeException and fail the whole conversion.
        var (ops, _) = Layout("<style>ol{counter-reset:i} li{display:block;counter-increment:i} li::before{content:counters(i, )}</style>" +
                              "<ol><li>x</li></ol>");
        Assert.Contains("x", AllText(ops));
    }

    [Fact]
    public void QuotesNest()
    {
        var (ops, _) = Layout("<p><q>a <q>b</q></q></p>");
        Assert.Equal("“a‘b’”", AllText(ops).Replace(" ", ""));
    }

}

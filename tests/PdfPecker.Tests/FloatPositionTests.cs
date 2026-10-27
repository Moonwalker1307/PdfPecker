using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData("flex;flex-direction:column")]
    [InlineData("flex")]
    [InlineData("grid")]
    public void AbsoluteChildOfFlexOrGridTakesNoRoom(string display)
    {
        // Out of flow, the badge is no item: it neither moves the content nor stretches, and its offsets apply.
        var (ops, _) = Layout("<style>body{margin:0} .c{display:" + display + ";position:relative;width:200px;height:100px}" +
                              ".a{position:absolute;top:10px;right:10px;height:20px;padding:0 5px;background:#f00}" +
                              ".b{height:10px;width:40px;background:#00f}</style>" +
                              "<div class=c><span class=a><i style='display:inline-block;width:30px'></i></span><div class=b></div></div>");
        var b = Rect(ops, 0xFF0000FF);
        Assert.Equal(0, b.X, 1);
        Assert.Equal(0, b.Y, 1);
        var a = Rect(ops, 0xFFFF0000);
        Assert.Equal(30, a.W, 1); // shrink-to-fit: 30px of content and 10px of padding
        Assert.Equal(150 - 7.5f - 30, a.X, 1);
        Assert.Equal(7.5f, a.Y, 1);
    }

    [Theory]
    [InlineData("display:grid;grid-template-columns:100px;grid-auto-rows:100px")]
    [InlineData("display:flex;height:100px")]
    public void StretchedItemWithoutBackgroundPlacesAbsoluteChildrenAtItsFullHeight(string container)
    {
        // A label in a grid row or flex line: the item has no background or border, but it is stretched to 100px, and
        // its code sits 10px above that bottom edge, not above the bottom of its one line of text.
        var (ops, _) = Layout("<style>body{margin:0} .c{" + container + "} .i{position:relative;width:100px}" +
                              ".a{position:absolute;right:0;bottom:10px;width:20px;height:10px;background:#f00}</style>" +
                              "<div class=c><div class=i>Name<div class=a></div></div></div>");
        var a = Rect(ops, 0xFFFF0000);
        Assert.Equal(80 * 0.75f, a.Y, 1);
    }

    [Fact]
    public void RelativeInlineContainsAbsoluteChildren()
    {
        // A tooltip below a word and a badge on its corner, as Chrome places them: against the inline box, not the page.
        var (ops, _) = Layout("<style>body{margin:0} p{margin:40px;font:16px/2 sans-serif} .r{position:relative;background:#00f}" +
                              ".a{position:absolute;width:20px;height:10px}</style>" +
                              "<p>Hover <span class=r>this word<b class=a style='top:100%;left:0;background:#f00'></b>" +
                              "<b class=a style='top:-5px;right:-5px;background:#0f0'></b></span> to see.</p>");
        var span = Rect(ops, 0xFF0000FF);
        var below = Rect(ops, 0xFFFF0000);
        Assert.Equal(span.X, below.X, 1);
        Assert.Equal(span.Y + span.H, below.Y, 1);
        var badge = Rect(ops, 0xFF00FF00);
        Assert.Equal(span.X + span.W + 3.75f - 15, badge.X, 1);
        Assert.Equal(span.Y - 3.75f, badge.Y, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InlineContainingBlockSpansFromFirstFragmentToLast(bool rtl)
    {
        // Split over two lines, the containing block starts where the first fragment starts and ends where the last one
        // ends, inside the borders; when the last fragment ends before the first starts, it has no width.
        var (ops, _) = Layout("<style>body{margin:0} p{width:200px;margin:0;font:16px/2 sans-serif} .r{position:relative;border:2px solid #00f}" +
                              ".a{position:absolute;top:0;bottom:0;left:0;right:0;min-width:4px;background:#f00}</style>" +
                              "<p" + (rtl ? " dir=rtl" : "") + ">aaaa bbbb cccc dddd <span class=r>eeee ffff gggg hhhh<b class=a></b></span> iiii.</p>");
        var borders = Flatten(ops).OfType<BorderRectOp>().Where(b => b.Color == 0xFF0000FF).OrderBy(b => b.Y).ToList();
        Assert.Equal(2, borders.Count);
        var (first, last) = (borders[0], borders[1]);
        var a = Rect(ops, 0xFFFF0000);
        Assert.Equal(first.Y + 1.5f, a.Y, 1);
        Assert.Equal(last.Y + last.H - 1.5f, a.Y + a.H, 1);
        // Right to left, the first fragment's start is its right edge and the last one's end its left edge.
        float start = rtl ? first.X + first.W - 1.5f : first.X + 1.5f, end = rtl ? last.X + 1.5f : last.X + last.W - 1.5f;
        float w = Math.Max(3, rtl ? start - end : end - start);
        Assert.Equal(rtl ? start - w : start, a.X, 1);
        Assert.Equal(w, a.W, 1);
    }

    [Fact]
    public void EmptyRelativeInlineContainsAbsoluteChildren()
    {
        // With nothing else in it, the inline element still has a fragment on its line, of no width.
        var (ops, _) = Layout("<style>body{margin:0} p{margin:0;font:16px/2 sans-serif}" +
                              ".a{position:absolute;top:0;bottom:0;left:0;right:0;min-width:10px;background:#f00}</style>" +
                              "<div style='height:100px'></div><p>Text <span style='position:relative'><b class=a></b></span></p>");
        var a = Rect(ops, 0xFFFF0000);
        Assert.InRange(a.Y, 75, 75 + 24);
        Assert.InRange(a.H, 9, 18);
        Assert.InRange(a.X, 10, 60);
    }

    [Fact]
    public void RelativeTableCellContainsAbsoluteChildren()
    {
        var (ops, _) = Layout("<style>body{margin:0} table{border-collapse:collapse;width:200px} td{padding:0;height:100px}" +
                              ".r{position:relative} .a{position:absolute;right:0;bottom:0;width:20px;height:20px;background:#f00}</style>" +
                              "<table><tr><td>x</td><td class=r>y<div class=a></div></td></tr></table><div style='height:500px'></div>");
        var a = Rect(ops, 0xFFFF0000);
        Assert.Equal(150 - 15, a.X, 1);
        Assert.Equal(75 - 15, a.Y, 1);
    }

    [Fact]
    public void OutlineOnCollapsedTableCellIsDrawn()
    {
        var (ops, _) = Layout("<style>body{margin:0} table{border-collapse:collapse} td{border:1px solid #999;width:50px;height:20px}</style>" +
                              "<table><tr><td style='outline:2px solid #f00'>a</td><td>b</td></tr></table>");
        var layer = Flatten(ops).OfType<LayerOp>().Single(l => l.Order == LayerOp.OutlineOrder);
        Assert.Contains(Flatten(layer.Ops).OfType<BorderRectOp>(), r => r.Color == 0xFFFF0000);
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

    [Theory]
    // An absolutely positioned box is laid out when its containing block ends, after the relative box that follows
    // it, but paints in document order with it, as in Chrome: the relative box on top.
    [InlineData("<div class=a style='background:#f00'></div><div class=r style='background:#00f'></div>")]
    [InlineData("<div class=a style='background:#f00;z-index:1'></div><div class=r style='background:#00f;z-index:1'></div>")]
    [InlineData("<div class=a style='background:#f00'></div><div style='opacity:.5'><div style='height:10px;background:#00f'></div></div>")]
    [InlineData("<div class=r style='background:#f00'></div><div class=a style='background:#00f'></div>")]
    public void PositionedBoxesPaintInDocumentOrder(string boxes)
    {
        var (ops, _) = Layout("<style>body{margin:0} .a{position:absolute;top:0;width:10px;height:10px} .r{position:relative;height:10px}</style>" +
                              "<div style='position:relative'>" + boxes + "</div>");
        var rects = Flatten(ops).OfType<RectOp>().ToList();
        Assert.True(rects.FindIndex(r => r.Color == 0xFFFF0000) < rects.FindIndex(r => (r.Color & 0xFFFFFF) == 0x0000FF));
    }

    [Theory]
    // Without offsets, an absolutely positioned child of a flex container goes where it would as the container's only
    // item (css-flexbox §4.1), as in Chrome: here in a 200px by 100px content box, the box is 20px square.
    [InlineData("justify-content:center;align-items:center", 67.5f, 30)]
    [InlineData("justify-content:flex-end;align-items:flex-end", 135, 60)]
    [InlineData("flex-direction:column;justify-content:flex-end;align-items:center", 67.5f, 60)]
    [InlineData("flex-direction:row-reverse", 135, 0)]
    [InlineData("justify-content:center;align-items:center;direction:rtl", 67.5f, 30)]
    [InlineData("justify-content:flex-start", 0, 0)]
    public void AbsoluteFlexChildWithoutOffsetsIsAligned(string container, float x, float y)
    {
        var (ops, _) = Layout("<style>body{margin:0}</style><div style='position:relative;display:flex;width:200px;height:100px;" + container + "'>" +
                              "<div style='position:absolute;width:20px;height:20px;background:#f00'></div></div>");
        var r = Rect(ops, 0xFFFF0000);
        Assert.Equal(x, r.X, 1);
        Assert.Equal(y, r.Y, 1);
    }

    [Fact]
    public void AbsoluteGridChildIsPositionedInItsGridArea()
    {
        // With the grid container as its containing block, grid-column and grid-row give the box its grid area, whose
        // edges its offsets are measured from (CSS Grid §9.1); an auto line is the container's padding edge.
        var (ops, _) = Layout("<style>body{margin:0} i{display:block}</style>" +
                              "<div style='position:relative;display:grid;grid-template-columns:100px 100px 100px;grid-template-rows:30px 30px;width:400px'>" +
                              "<i></i><i></i><i></i>" +
                              "<div style='position:absolute;grid-column:2/4;grid-row:2;right:0;bottom:0;width:20px;height:10px;background:#f00'></div>" +
                              "<div style='position:absolute;grid-column:2;grid-row:1;left:10px;top:5px;width:20px;height:10px;background:#00f'></div>" +
                              "<div style='position:absolute;grid-row:2;left:0;right:0;top:0;height:10px;background:#0f0'></div></div>");
        var red = Rect(ops, 0xFFFF0000);
        Assert.Equal(280 * 0.75f, red.X, 1);
        Assert.Equal(50 * 0.75f, red.Y, 1);
        var blue = Rect(ops, 0xFF0000FF);
        Assert.Equal(110 * 0.75f, blue.X, 1);
        Assert.Equal(5 * 0.75f, blue.Y, 1);
        var green = Rect(ops, 0xFF00FF00);
        Assert.Equal(400 * 0.75f, green.W, 1);
        Assert.Equal(30 * 0.75f, green.Y, 1);
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

    [Theory]
    [InlineData("")]
    [InlineData("html{overflow:hidden}")] // as in Acid2, whose root clip holds the root's layers
    public void FixedBoxPaintsBelowHigherZIndex(string rootStyle)
    {
        // Chrome paints a fixed box by its z-index among the root's layers on every page: over the flow, under z-index 2.
        var pdf = ConvertUncompressed("<style>" + rootStyle + "</style><div style='position:fixed;top:0'>FIXEDMARK</div>" +
                                      "<div style='position:relative;z-index:2'>ABOVEMARK</div><p>FLOWMARK</p><div style='height:3000px'></div>");
        var marks = Regex.Matches(AllContent(pdf), "FIXEDMARK|ABOVEMARK|FLOWMARK").Select(m => m.Value);
        Assert.Equal(new[] { "FLOWMARK", "FIXEDMARK", "ABOVEMARK" }.Concat(Enumerable.Repeat("FIXEDMARK", PageCount(pdf) - 1)), marks);
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

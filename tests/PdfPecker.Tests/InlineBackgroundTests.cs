using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Backgrounds of inline boxes: images and gradients, their clip and origin, rounded corners, opacity, text clipping,
/// and box-decoration-break across line fragments.
/// </summary>
public class InlineBackgroundTests
{
    private const float Width = 500, PageHeight = 800;

    private static readonly string Square = DataUri(SolidPng(20, 20, 255, 0, 0));

    /// <summary>The draw ops of <paramref name="html"/> laid out in a <paramref name="width"/> wide page, groups kept.</summary>
    private static List<DrawOp> Layout(string html, float width = Width)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
        return engine.Ops.ToList();
    }

    private static List<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        var list = new List<DrawOp>();
        foreach (var o in ops)
        {
            list.Add(o);
            if (o is GroupOp g) list.AddRange(Flatten(g.Ops));
        }
        return list;
    }

    private static List<T> All<T>(string html, float width = Width) => Flatten(Layout(html, width)).OfType<T>().ToList();

    // Lengths in the styles are CSS pixels, three quarters of a point.
    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}");

    private static TextOp Text(List<TextOp> texts, string start) => texts.First(t => t.Text.StartsWith(start, StringComparison.Ordinal));

    [Fact]
    public void GradientOnSpanIsPaintedBehindItsText()
    {
        var ops = Flatten(Layout("<p style='margin:0'>a <span style='background:linear-gradient(red, blue)'>badge</span> b</p>"));
        var bg = Assert.Single(ops.OfType<BackgroundOp>());
        Assert.NotNull(bg.Gradient);
        var text = ops.OfType<TextOp>().First(t => t.Text == "badge");
        Near(text.X, bg.ClipX);
        Near(bg.ClipW, bg.TileW);
        Assert.True(ops.IndexOf(bg) < ops.IndexOf(text), "The background is painted before the text");
    }

    [Theory]
    [InlineData("radial-gradient(circle, red, blue)")]
    [InlineData("conic-gradient(red, blue, red)")]
    [InlineData("repeating-linear-gradient(45deg, red 0 4px, blue 4px 8px)")]
    public void EveryGradientKindIsPainted(string gradient)
    {
        var bg = Assert.Single(All<BackgroundOp>($"<p style='margin:0'><span style='background:{gradient}'>badge</span></p>"));
        Assert.NotNull(bg.Gradient);
    }

    [Fact]
    public void UrlImageIsPositionedInThePaddingBox()
    {
        var bg = Assert.Single(All<BackgroundOp>(
            $"<p style='margin:0'><span style='background:url({Square}) no-repeat 2px center / 12px 12px; padding-left:18px'>ready</span></p>"));
        Assert.NotNull(bg.Image);
        Assert.False(bg.RepeatX);
        // 12px = 9pt, 2px = 1.5pt.
        Near(9, bg.TileW);
        Near(1.5f, bg.TileX - bg.ClipX);
        Near(bg.ClipY + (bg.ClipH - 9) / 2, bg.TileY);
    }

    [Fact]
    public void MarginIsLeftOutOfTheBackground()
    {
        var ops = Flatten(Layout("<p style='margin:0'>a<span style='background:red; margin:0 10px; padding:0 4px'>x</span>b</p>"));
        var rect = Assert.Single(ops.OfType<RectOp>(), r => r.Color == 0xFFFF0000);
        var x = ops.OfType<TextOp>().First(t => t.Text == "x");
        Near(x.X - 3, rect.X); // 4px of padding, the 10px margin left out
    }

    [Fact]
    public void SliceLaysTheBackgroundOutAcrossTheFragments()
    {
        var bgs = All<BackgroundOp>(
            "<p style='margin:0; width:150px'>Some text <span style='background:linear-gradient(90deg, red, blue); padding:0 3px'>with a highlighted run that wraps</span></p>", 200);
        Assert.True(bgs.Count >= 2, $"Expected a fragment on each line, got {bgs.Count}");
        float total = bgs.Sum(b => b.ClipW);
        float before = 0;
        foreach (var bg in bgs)
        {
            // One gradient as wide as all fragments, of which each shows its own part.
            Near(total, bg.TileW, 0.1f);
            Near(bg.ClipX - before, bg.TileX, 0.1f);
            before += bg.ClipW;
        }
    }

    [Fact]
    public void CloneGivesEachFragmentItsOwnBackgroundAndPadding()
    {
        const string html = "<p style='margin:0; width:150px'>Some text <span style='background:linear-gradient(90deg, red, blue); padding:0 6px; "
            + "box-decoration-break:clone'>with a highlighted run that wraps</span></p>";
        var ops = Flatten(Layout(html, 200));
        var bgs = ops.OfType<BackgroundOp>().ToList();
        Assert.True(bgs.Count >= 2, $"Expected a fragment on each line, got {bgs.Count}");
        foreach (var bg in bgs)
        {
            Near(bg.ClipW, bg.TileW);
            Near(bg.ClipX, bg.TileX);
        }
        // The second line starts with the box's left padding, and the first ends with its right padding.
        var texts = ops.OfType<TextOp>().ToList();
        var second = texts.Where(t => t.LineTop > texts[0].LineTop).OrderBy(t => t.X).First();
        Near(0, bgs[1].ClipX);
        Near(4.5f, second.X); // 6px
        Assert.True(bgs[0].ClipX + bgs[0].ClipW <= 150 + 0.05f, "The first line keeps room for the right padding");
    }

    [Fact]
    public void WebkitPrefixedCloneIsUnderstood()
    {
        var doc = HtmlParser.Parse("<span style='-webkit-box-decoration-break:clone'>x</span><b style='box-decoration-break:slice'>y</b>");
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        Assert.True(doc.FindFirst("span")!.Style!.BoxDecorationClone);
        Assert.False(doc.FindFirst("b")!.Style!.BoxDecorationClone);
        Assert.True(CssSupports.Evaluate("(box-decoration-break: clone)"));
    }

    [Fact]
    public void RoundedCornersFollowTheFragments()
    {
        var single = Assert.Single(All<RoundRectOp>("<p style='margin:0'><span style='background:red; border-radius:999px; padding:0 8px'>pill</span></p>"));
        Assert.True(single.R.TLx > 0 && single.R.TRx > 0);

        var parts = All<RoundRectOp>(
            "<p style='margin:0; width:150px'>Some text <span style='background:red; border-radius:6px'>with a highlighted run that wraps</span></p>", 200);
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].R.TLx > 0 && parts[0].R.TRx == 0, "The first fragment is rounded only at the box's start");
        Assert.True(parts[1].R.TLx == 0 && parts[1].R.TRx > 0, "The last fragment is rounded only at the box's end");
    }

    [Fact]
    public void OpacityFadesTheLayers()
    {
        var ops = Flatten(Layout("<p style='margin:0'><span style='opacity:.5; background:linear-gradient(red, blue)'>x</span></p>"));
        var layer = Assert.Single(ops.OfType<LayerOp>(), l => l.Opacity < 1);
        Near(0.5f, layer.Opacity);
        Assert.Contains(layer.Ops, o => o is BackgroundOp);
    }

    [Fact]
    public void BackgroundClipTextClipsToTheSpansText()
    {
        var ops = Flatten(Layout(
            "<p style='margin:0'>plain <span style='background:linear-gradient(90deg, red, blue); background-clip:text; color:transparent'>gradient</span> after</p>"));
        var clip = Assert.Single(ops.OfType<LayerOp>(), l => l.TextClip);
        var copies = clip.Ops.OfType<TextOp>().Select(t => t.Text).ToList();
        Assert.Equal(new[] { "gradient" }, copies);
        Assert.Contains(clip.Ops, o => o is BackgroundOp);
    }

    [Fact]
    public void OuterBackgroundRunsUnderANestedBox()
    {
        var bgs = All<BackgroundOp>(
            "<p style='margin:0'><span style='background:linear-gradient(red, blue)'>outer <span style='background:yellow'>inner</span> outer</span></p>");
        Assert.Single(bgs);
    }

    [Fact]
    public void RendersTheGradientIntoTheTaggedArchivalPdf()
    {
        const string html = "<p>Text <span style='background:linear-gradient(90deg, #f00, #00f); padding:0 4px; border-radius:4px'>badge</span></p>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions
        {
            Conformance = PdfConformance.PdfA2b, Accessibility = PdfAccessibility.Tagged, Margins = new PageMargins(0),
            CreationDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        });
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        // Somewhere along the badge the gradient is red at its start and blue at its end.
        bool red = false, blue = false;
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 200; x++)
            {
                int i = (y * img.Width + x) * 4;
                byte r = img.Rgba[i], b = img.Rgba[i + 2];
                if (r > 200 && b < 80) red = true;
                if (b > 200 && r < 80) blue = true;
            }
        Assert.True(red && blue, "The gradient is drawn");
    }
}

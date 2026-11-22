using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// Which line of a box others align with: the first line of a flex item, unless the container asks for last baseline,
/// and the last line of an inline-block, unless baseline-source says first, as in Chrome.
/// </summary>
public class BaselineAlignmentTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<TextOp> Texts(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).OfType<TextOp>().ToList();
    }

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static float BaselineOf(List<TextOp> texts, string word) => texts.First(t => t.Text.Contains(word)).Baseline;

    private static void Near(float expected, float actual)
        => Assert.True(Math.Abs(expected - actual) <= 0.05f, $"Expected {expected} but got {actual}");

    private const string TwoLines = "<div style='font-size: 18pt'>firstx<br>lastx</div>";

    [Fact]
    public void A_flex_item_aligns_by_its_first_line()
    {
        var texts = Texts($"<div style='display: flex; align-items: baseline'>{TwoLines}<div>besidex</div></div>");
        Near(BaselineOf(texts, "firstx"), BaselineOf(texts, "besidex"));
    }

    [Fact]
    public void Last_baseline_aligns_a_flex_item_by_its_last_line()
    {
        var texts = Texts($"<div style='display: flex; align-items: last baseline'>{TwoLines}<div>besidex</div></div>");
        Near(BaselineOf(texts, "lastx"), BaselineOf(texts, "besidex"));
    }

    [Fact]
    public void A_flex_item_starting_with_a_column_flex_box_aligns_by_its_first_line()
    {
        var texts = Texts("<div style='display: flex; align-items: baseline'><div style='display: flex; flex-direction: column; font-size: 18pt'>"
                          + "<span>firstx</span><span>lastx</span></div><div>besidex</div></div>");
        Near(BaselineOf(texts, "firstx"), BaselineOf(texts, "besidex"));
    }

    [Fact]
    public void Baseline_source_puts_an_inline_block_on_its_first_line()
    {
        var texts = Texts("<p>outsidex <span style='display: inline-block; baseline-source: first'>firstx<br>lastx</span> "
                          + "<span style='display: inline-block'>defaultfirstx<br>defaultlastx</span></p>");
        Near(BaselineOf(texts, "outsidex"), BaselineOf(texts, "firstx"));
        Near(BaselineOf(texts, "outsidex"), BaselineOf(texts, "defaultlastx"));
    }
}

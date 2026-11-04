using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// box-decoration-break: clone on block boxes split across pages and columns: each fragment is a whole box, and the
/// repeated borders and padding take room in each fragment, as Chrome prints them.
/// </summary>
public class BoxDecorationBreakTests
{
    private const float Width = 400, PageHeight = 200;

    // A 4.5pt border (6px, as border widths are rounded to whole pixels) and 5.5pt padding: each fragment of the box
    // keeps 10pt at its top and 10pt at its bottom. Lines are 18pt (24px) tall.
    private const string Style = "<style>body{margin:0} p{margin:0; font: 9pt/18pt Helvetica}"
                                 + " .b{border:6px solid #00f; padding:5.5pt; background:#f00} .c{box-decoration-break:clone}</style>";

    private static readonly PdfOptions Fixed = new() { CreationDate = DateTimeOffset.UnixEpoch };

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).ToList();
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
        foreach (var o in ops)
        {
            if (o is GroupOp g) { foreach (var inner in Flatten(g.Ops)) yield return inner; }
            else yield return o;
        }
    }

    private static string Lines(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"<p>line{i}</p>"));

    private static float LineTop(List<DrawOp> ops, int line) => ops.OfType<TextOp>().Single(t => t.Text == "line" + line).LineTop;

    // The backgrounds of the boxes painted in red, top to bottom, as (top, height).
    private static List<(float Top, float H)> Backgrounds(List<DrawOp> ops, uint color = 0xFFFF0000)
        => ops.OfType<RectOp>().Where(r => r.Color == color).Select(r => (r.Y, r.H)).ToList();

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 0.05f, expected + 0.05f);

    [Fact]
    public void The_content_leaves_room_for_the_edges_on_each_page()
    {
        var ops = Layout(Style + $"<div class='b c'>{Lines(12)}</div>");
        // Ten lines fit between the top edge at 10pt and the bottom edge kept at 190pt; the eleventh starts the next
        // page below the repeated top edge.
        Near(172, LineTop(ops, 10));
        Near(210, LineTop(ops, 11));
        Near(228, LineTop(ops, 12));
    }

    [Fact]
    public void Each_fragment_is_painted_as_a_whole_box()
    {
        var ops = Layout(Style + $"<div class='b c'>{Lines(12)}</div>");
        // The first fragment reaches the end of its page, the second ends under the last line with the bottom edge.
        var backgrounds = Backgrounds(ops);
        Assert.Equal(2, backgrounds.Count);
        Near(0, backgrounds[0].Top);
        Near(200, backgrounds[0].H);
        Near(200, backgrounds[1].Top);
        Near(56, backgrounds[1].H);
        var borders = ops.OfType<BorderRectOp>().ToList();
        Assert.Equal(2, borders.Count);
        Assert.All(borders, b => Near(4.5f, b.Width));
        Near(200, borders[0].H);
        Near(56, borders[1].H);
    }

    [Fact]
    public void Slice_paints_one_box_across_the_break()
    {
        var ops = Layout(Style + $"<div class='b'>{Lines(12)}</div>");
        Near(172, LineTop(ops, 10));
        Near(200, LineTop(ops, 11));
        var background = Assert.Single(Backgrounds(ops));
        Near(246, background.H);
        Assert.Single(ops.OfType<BorderRectOp>());
    }

    [Fact]
    public void A_box_that_fits_on_its_page_is_drawn_as_with_slice()
    {
        const string html = "<div style='border: 2pt solid black; border-radius: 4pt; padding: 4pt{0}'><p>Short text</p></div><p>After</p>";
        Assert.Equal(HtmlToPdf.Convert(string.Format(html, ""), Fixed), HtmlToPdf.Convert(string.Format(html, "; box-decoration-break: clone"), Fixed));
    }

    [Fact]
    public void A_height_goes_on_past_the_room_at_each_break()
    {
        // As in Chrome, the height is the content's: 180pt of it on the first page and the other 120pt on the next,
        // each with the box's edges around it.
        var ops = Layout(Style + "<div class='b c' style='height:300pt'></div><p>after</p>");
        var backgrounds = Backgrounds(ops);
        Assert.Equal(2, backgrounds.Count);
        Near(200, backgrounds[0].H);
        Near(200, backgrounds[1].Top);
        Near(140, backgrounds[1].H);
        Near(340, ops.OfType<TextOp>().Single(t => t.Text == "after").LineTop);
    }

    [Fact]
    public void Nested_cloned_boxes_each_keep_their_room()
    {
        var ops = Layout(Style + $"<div class='b c' style='background:#0f0'><div class='b c'>{Lines(12)}</div></div>");
        // The inner content keeps 20pt at each end of a page; the inner box's second fragment starts below the outer
        // box's repeated top edge.
        Near(146, LineTop(ops, 8));
        Near(220, LineTop(ops, 9));
        var inner = Backgrounds(ops);
        Assert.Equal(2, inner.Count);
        Near(10, inner[0].Top);
        Near(180, inner[0].H);
        Near(210, inner[1].Top);
        var outer = Backgrounds(ops, 0xFF00FF00);
        Assert.Equal(2, outer.Count);
        Near(200, outer[0].H);
        Near(200, outer[1].Top);
    }

    [Fact]
    public void A_sliced_box_inside_a_cloned_one_leaves_its_edges_free()
    {
        string html = Style + $"<div class='b c' style='background:#0f0'><div style='background:#f00'>{Lines(12)}</div></div>";
        Near(210, LineTop(Layout(html), 11));
        // The inner box's background stops where the content does at the end of the page, and starts again below the
        // repeated top edge, as its fragments do in Chrome.
        var parts = LayoutWithGroups(html).OfType<ClipOp>().Where(c => c.Ops.OfType<RectOp>().Any(r => r.Color == 0xFFFF0000)).ToList();
        Assert.Equal(2, parts.Count);
        Near(190, parts[0].Y + parts[0].H);
        Near(210, parts[1].Y);
    }

    // All the ops, groups too, in paint order.
    private static List<DrawOp> LayoutWithGroups(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var all = new List<DrawOp>();
        void Walk(IEnumerable<DrawOp> list)
        {
            foreach (var o in list)
            {
                all.Add(o);
                if (o is GroupOp g) Walk(g.Ops);
            }
        }
        Walk(engine.Ops);
        return all;
    }

    [Fact]
    public void Table_rows_leave_room_for_the_cloned_table_edges()
    {
        string rows = string.Concat(Enumerable.Range(1, 12).Select(i => $"<tr><td style='padding:0'><p>line{i}</p></td></tr>"));
        var ops = Layout(Style + $"<table class='b c' style='border-spacing:0'>{rows}</table>");
        Near(172, LineTop(ops, 10));
        Near(210, LineTop(ops, 11));
        var backgrounds = Backgrounds(ops);
        Assert.Equal(2, backgrounds.Count);
        Near(200, backgrounds[0].H);
        Near(200, backgrounds[1].Top);
    }

    [Fact]
    public void Edges_too_tall_to_repeat_keep_no_room()
    {
        // Edges of 170pt would leave the content 30pt of a 200pt page: as with a table header too tall to repeat, the
        // content goes on as it would without them, and each fragment is still painted whole.
        var ops = Layout(Style + $"<div class='b c' style='padding:80.5pt'>{Lines(12)}</div>");
        Assert.Equal(12, ops.OfType<TextOp>().Count());
        Near(LineTop(ops, 1) + 18, LineTop(ops, 2));
        Assert.True(Backgrounds(ops).Count > 1);
    }

    [Fact]
    public void A_cloned_box_taller_than_the_page_limit_stops_with_the_limit()
    {
        var options = new PdfOptions { MaxPages = 50 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<PdfLimitException>(() => HtmlToPdf.Convert(
            "<div style='box-decoration-break: clone; border: 5pt solid; padding: 5pt; height: 1e30px'>x</div>", options));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Columns_clone_the_box_in_each_column()
    {
        var ops = Layout(Style + $"<div style='columns:2; column-gap:0; height:100pt; column-fill:auto'><div class='b c'>{Lines(6)}</div></div>");
        // Four lines fit in a 100pt column between the edges; the rest go on in the second column below its top edge.
        var backgrounds = Backgrounds(ops);
        Assert.Equal(2, backgrounds.Count);
        Near(100, backgrounds[0].H);
        Near(0, backgrounds[1].Top);
        Near(56, backgrounds[1].H);
        Assert.True(ops.OfType<TextOp>().Single(t => t.Text == "line5").X > Width / 2 - 1);
    }
}

using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>
/// MathML Core: fractions, scripts, radicals, limits, stretchy fences, tables and operator spacing, laid out by the
/// math layout and checked by where the text and rules land.
/// </summary>
public class MathMLTests
{
    private const float Width = 500, PageHeight = 800;

    private static List<DrawOp> Layout(string body, out Element doc)
    {
        doc = HtmlParser.Parse("<!doctype html><body style='margin:0;font:20px serif'>" + body);
        MathElements.Prepare(doc);
        new StyleResolver(new Stylesheet(), Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        return Flatten(engine.Ops).ToList();
    }

    private static List<DrawOp> Layout(string body) => Layout(body, out _);

    private static IEnumerable<DrawOp> Flatten(IEnumerable<DrawOp> ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            if (op is GroupOp g) foreach (var o in Flatten(g.Ops)) yield return o;
        }
    }

    private static TextOp Text(List<DrawOp> ops, string text) => ops.OfType<TextOp>().First(t => t.Text == text);

    private static IEnumerable<Element> All(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    [Fact]
    public void A_fraction_puts_its_numerator_over_its_denominator_with_a_bar_between()
    {
        var ops = Layout("<math><mfrac><mi>a</mi><mi>b</mi></mfrac></math>");
        var a = Text(ops, "a");
        var b = Text(ops, "b");
        var bar = ops.OfType<RectOp>().Single();
        Assert.True(a.Baseline < bar.Y && bar.Y + bar.H < b.Baseline - 5, $"a {a.Baseline}, bar {bar.Y}, b {b.Baseline}");
        // Both are centred over the bar, which is a little wider than the wider of them.
        Assert.InRange(a.X - bar.X, 0, 3);
        Assert.InRange(b.X - bar.X, 0, 3);
        Assert.InRange(bar.H, 0.5f, 1.2f);
    }

    [Fact]
    public void A_fraction_in_text_has_smaller_parts_and_one_in_a_display_formula_does_not()
    {
        var inline = Layout("<math><mfrac><mn>1</mn><mn>2</mn></mfrac></math>");
        var display = Layout("<math display=block><mfrac><mn>1</mn><mn>2</mn></mfrac></math>");
        Assert.Equal(15 * 0.71f, Text(inline, "1").Size, 1);
        Assert.Equal(15, Text(display, "1").Size, 1);
    }

    [Fact]
    public void The_bar_of_a_fraction_in_a_line_sits_on_the_math_axis_above_the_baseline()
    {
        var ops = Layout("<p>x <math><mfrac><mi>a</mi><mi>b</mi></mfrac></math> y</p>");
        var x = Text(ops, "x ");
        var bar = ops.OfType<RectOp>().Single();
        // The axis is a quarter of an em (15pt) above the baseline of the text.
        Assert.Equal(x.Baseline - 15 * 0.25f, bar.Y + bar.H / 2, 0.5f);
    }

    [Fact]
    public void Scripts_are_smaller_and_raised_or_lowered()
    {
        var ops = Layout("<math><msup><mi>x</mi><mn>2</mn></msup><msub><mi>y</mi><mi>i</mi></msub></math>");
        var x = Text(ops, "x");
        var two = Text(ops, "2");
        var i = Text(ops, "i");
        Assert.Equal(x.Size * 0.71f, two.Size, 1);
        Assert.True(two.Baseline < x.Baseline - 4, "The superscript is raised.");
        Assert.True(i.Baseline > x.Baseline + 2, "The subscript is lowered.");
        Assert.True(two.X > x.X);
    }

    [Fact]
    public void A_single_letter_identifier_is_italic_and_a_name_is_not()
    {
        Layout("<math><mi>x</mi><mi>sin</mi><mi mathvariant=normal>y</mi><mn>2</mn></math>", out var doc);
        var mi = All(doc).Where(e => e.Name is "mi" or "mn").ToList();
        Assert.True(mi[0].Style.Italic);
        Assert.False(mi[1].Style.Italic);
        Assert.False(mi[2].Style.Italic);
        Assert.False(mi[3].Style.Italic);
    }

    [Fact]
    public void Operators_take_their_spacing_from_the_dictionary()
    {
        // A minus at the start of a row is a prefix operator without spaces, a relation has thick spaces (5/18 em) on
        // each side and a binary operator medium ones (4/18 em); an operator's hyphen-minus is a minus sign.
        var ops = Layout("<math><mo>-</mo><mi>a</mi><mo>=</mo><mi>b</mi><mo>+</mo><mi>c</mi></math>");
        float em = 15;
        float Right(string text) { var t = Text(ops, text); return t.X + t.Font.Measure(text, t.Size); }
        Assert.Equal(Right("\u2212"), Text(ops, "a").X, 0.01f);
        Assert.Equal(5 / 18f * em, Text(ops, "=").X - Right("a"), 0.01f);
        Assert.Equal(5 / 18f * em, Text(ops, "b").X - Right("="), 0.01f);
        Assert.Equal(4 / 18f * em, Text(ops, "+").X - Right("b"), 0.01f);
        Assert.Equal(4 / 18f * em, Text(ops, "c").X - Right("+"), 0.01f);
    }

    [Fact]
    public void A_display_formula_is_centred_on_its_line()
    {
        var ops = Layout("<math display=block><mi>x</mi></math>");
        var x = Text(ops, "x");
        float w = x.Font.Measure("x", x.Size);
        Assert.Equal(Width / 2, x.X + w / 2, 0.5f);
    }

    [Fact]
    public void Math_in_a_line_shares_the_baseline_of_the_text()
    {
        var ops = Layout("<p>Then <math><mi>x</mi><mo>=</mo><mn>1</mn></math> holds.</p>");
        Assert.Equal(Text(ops, "Then ").Baseline, Text(ops, "x").Baseline, 0.1f);
        Assert.Equal(Text(ops, " holds.").Baseline, Text(ops, "1").Baseline, 0.1f);
    }

    [Fact]
    public void Limits_go_over_and_under_a_sum_in_display_style_and_beside_it_in_text()
    {
        const string sum = "<munderover><mo>∑</mo><mi>k</mi><mi>n</mi></munderover>";
        var display = Layout("<math display=block>" + sum + "</math>");
        var sigma = Text(display, "∑");
        var k = Text(display, "k");
        var n = Text(display, "n");
        Assert.True(k.Baseline > sigma.Baseline && n.Baseline < sigma.Baseline - 15, "The limits are under and over the sum.");
        Assert.True(sigma.Size > 15 * 1.3f, "A sum in display style is larger.");
        var inline = Layout("<math>" + sum + "</math>");
        var s2 = Text(inline, "∑");
        Assert.True(Text(inline, "k").X > s2.X + 5 && Text(inline, "n").X > s2.X + 5, "In text the limits are scripts.");
        Assert.Equal(15, s2.Size, 0.1f);
    }

    [Fact]
    public void A_square_root_draws_its_sign_and_a_bar_over_the_base()
    {
        var ops = Layout("<math><msqrt><mi>x</mi></msqrt></math>");
        var x = Text(ops, "x");
        var sign = ops.OfType<PathOp>().First();
        Assert.True(sign.Y0 < x.Baseline - 8, "The bar is over the base.");
        Assert.True(sign.X1 > x.X + 5, "The bar runs over the base.");
        Assert.True(sign.X0 < x.X, "The sign is before the base.");
    }

    [Fact]
    public void Fences_stretch_around_a_fraction_unless_they_are_not_stretchy()
    {
        var ops = Layout("<math><mrow><mo>(</mo><mfrac><mi>a</mi><mi>b</mi></mfrac><mo>)</mo></mrow></math>");
        Assert.Equal(2, ops.OfType<PathOp>().Count());
        Assert.DoesNotContain(ops.OfType<TextOp>(), t => t.Text == "(");
        var paren = ops.OfType<PathOp>().First();
        Assert.True(paren.Y1 - paren.Y0 > 18, $"The fence is {paren.Y1 - paren.Y0}pt tall.");

        var fixedOps = Layout("<math><mrow><mo stretchy=false>(</mo><mfrac><mi>a</mi><mi>b</mi></mfrac><mo stretchy=false>)</mo></mrow></math>");
        Assert.Empty(fixedOps.OfType<PathOp>());
        Assert.Contains(fixedOps.OfType<TextOp>(), t => t.Text == "(");
    }

    [Fact]
    public void A_fence_around_a_letter_stays_the_glyph()
    {
        var ops = Layout("<math><mo>(</mo><mi>x</mi><mo>)</mo></math>");
        Assert.Empty(ops.OfType<PathOp>());
    }

    [Fact]
    public void A_table_lays_its_cells_out_in_rows_and_columns()
    {
        var ops = Layout("<math><mtable><mtr><mtd><mn>1</mn></mtd><mtd><mn>22</mn></mtd></mtr><mtr><mtd><mn>3</mn></mtd><mtd><mn>4</mn></mtd></mtr></mtable></math>");
        var one = Text(ops, "1");
        var two = Text(ops, "22");
        var three = Text(ops, "3");
        var four = Text(ops, "4");
        Assert.Equal(one.Baseline, two.Baseline, 0.1f);
        Assert.Equal(three.Baseline, four.Baseline, 0.1f);
        Assert.True(three.Baseline > one.Baseline + 10);
        Assert.Equal(one.X, three.X, 0.1f);
        // The narrower cell is centred in the column of the wider one.
        Assert.Equal(two.X + two.Font.Measure("22", two.Size) / 2, four.X + four.Font.Measure("4", four.Size) / 2, 0.1f);
    }

    [Fact]
    public void A_space_and_a_phantom_take_room_but_draw_nothing()
    {
        var ops = Layout("<math><mi>a</mi><mspace width=2em /><mphantom><mi>q</mi></mphantom><mi>b</mi></math>");
        var a = Text(ops, "a");
        var b = Text(ops, "b");
        Assert.DoesNotContain(ops.OfType<TextOp>(), t => t.Text == "q");
        Assert.True(b.X - a.X > 2 * 15 + 10, $"{b.X - a.X}");
    }

    [Fact]
    public void An_accent_sits_over_its_base_and_a_bar_stretches_across_it()
    {
        var ops = Layout("<math><mover><mi>x</mi><mo>^</mo></mover><mover><mrow><mi>a</mi><mi>b</mi><mi>c</mi></mrow><mo>¯</mo></mover></math>");
        var x = Text(ops, "x");
        var hat = Text(ops, "^");
        Assert.Equal(x.Size, hat.Size, 0.1f); // an accent is not a script
        Assert.True(hat.Baseline < x.Baseline - 1);
        var bar = ops.OfType<RectOp>().Single();
        var a = Text(ops, "a");
        Assert.True(bar.W > 15, $"The bar is {bar.W}pt wide.");
        Assert.True(bar.Y < a.Baseline - 8);
    }

    [Fact]
    public void A_formula_in_a_table_cell_keeps_its_width()
    {
        var ops = Layout("<table><tr><td><math><mi>a</mi><mo>+</mo><mi>b</mi><mo>+</mo><mi>c</mi></math></td><td>x</td></tr></table>");
        var c = Text(ops, "c");
        var x = Text(ops, "x");
        Assert.Equal(Text(ops, "a").Baseline, c.Baseline, 0.1f);
        Assert.True(x.X > c.X + c.Font.Measure("c", c.Size));
    }

    [Fact]
    public void Mathcolor_and_mathsize_are_style_hints()
    {
        var ops = Layout("<math><mi mathcolor=red mathsize=30px>x</mi></math>");
        var x = Text(ops, "x");
        Assert.Equal(0xFFFF0000, x.Color);
        Assert.Equal(22.5f, x.Size, 0.1f);
    }

    [Fact]
    public void Text_extraction_reads_a_formula()
    {
        var pdf = HtmlToPdf.Convert("<p>Area <math><mi>π</mi><msup><mi>r</mi><mn>2</mn></msup></math>.</p>");
        string text = PdfText.Extract(pdf);
        Assert.Contains("π", text, StringComparison.Ordinal);
        Assert.Contains("r", text, StringComparison.Ordinal);
    }
}

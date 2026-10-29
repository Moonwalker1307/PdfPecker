using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Counter styles (the predefined ones, @counter-style and symbols()), list-style-image and the relative colour syntax.
/// The expected texts and colours are Chrome's.
/// </summary>
public class CounterStyleTests
{
    private static List<DrawOp> Layout(string html, float width = 500, float pageHeight = 800)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, pageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(pageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, width);
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

    private static List<string> Texts(List<DrawOp> ops) => ops.OfType<TextOp>().Select(t => t.Text).ToList();

    private static void Near(float expected, float actual, float tolerance = 0.05f)
        => Assert.True(Math.Abs(expected - actual) <= tolerance, $"Expected {expected} but got {actual}");

    private static string Marker(string style, int value) => CounterStyles.Predefined.Get(style).MarkerText(value);

    private static string Marker(string css, string style, int value)
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(css, sheet);
        return CounterStyles.From(sheet.CounterStyles).Get(style).MarkerText(value);
    }

    // ---- relative colours ----

    [Theory]
    [InlineData("rgb(from red r g b / 50%)", 0x80FF0000)]
    [InlineData("hsl(from red calc(h + 120) s l)", 0xFF00FF00)]
    [InlineData("rgb(from #336699 b g r)", 0xFF996633)]
    [InlineData("rgb(from rgb(10 20 30 / 0.5) r g b)", 0x800A141E)]
    [InlineData("oklch(from #0af calc(l * 0.8) c h)", 0xFF007DCF)]
    [InlineData("lab(from #0af calc(l - 20) a b)", 0xFF0076C6)]
    [InlineData("hwb(from red h calc(w + 50) b)", 0xFFFF8080)]
    [InlineData("lch(from blue l c calc(h + 180))", 0xFF005900)]
    [InlineData("color(from red srgb r calc(g + 0.5) b)", 0xFFFF8000)]
    [InlineData("color(from #0af display-p3 r g b)", 0xFF00AAFF)]
    [InlineData("color(from #0af rec2020 r g b)", 0xFF00AAFF)]
    [InlineData("color(from #0af prophoto-rgb r g b)", 0xFF00AAFF)]
    [InlineData("color(from red a98-rgb r g b)", 0xFFFF0000)]
    [InlineData("color(from red xyz-d65 x y z)", 0xFFFF0000)]
    [InlineData("rgb(from hsl(120 100% 50%) calc(r + 100) g calc(b + 50) / calc(alpha / 2))", 0x8064FF32)]
    [InlineData("oklab(from #808080 l a b / 0.25)", 0x40808080)]
    [InlineData("rgb(from red r g b / alpha)", 0xFFFF0000)]
    [InlineData("hsl(from #808080 h s l)", 0xFF808080)]
    [InlineData("rgba(from blue r g b / 0.3)", 0x4D0000FF)]
    [InlineData("rgb(from currentColor r g b / 50%)", 0x80000000)]
    [InlineData("rgb(from red calc(r - 300) 10 10)", 0xFF000A0A)]
    [InlineData("hsl(from blue calc(h - 360) s calc(l + 10))", 0xFF3333FF)]
    [InlineData("rgb(from color-mix(in srgb, red, blue) r g b)", 0xFF800080)]
    [InlineData("rgb(from rgb(from red r g b / 0.5) g r b / calc(alpha * 2))", 0xFF00FF00)]
    public void Relative_colours_parse_as_chrome(string css, uint expected)
    {
        Assert.True(CssColor.TryParse(css, CssColor.Black, out uint c), css);
        for (int shift = 0; shift <= 24; shift += 8)
            Assert.True(Math.Abs((int)(expected >> shift & 0xFF) - (int)(c >> shift & 0xFF)) <= 1, $"{css}: expected {expected:X8}, got {c:X8}");
    }

    [Fact]
    public void Relative_colours_work_with_custom_properties()
    {
        var doc = HtmlParser.Parse("<style>:root { --brand: #336699 } p { color: rgb(from var(--brand) r g b / 50%) }</style><p id=p>x</p>");
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var p = Find(doc, "p").Single();
        Assert.Equal(0x80336699u, p.Style.Color);
    }

    // ---- predefined counter styles ----

    [Theory]
    [InlineData("decimal", 7, "7. ")]
    [InlineData("decimal-leading-zero", 0, "00. ")]
    [InlineData("decimal-leading-zero", -3, "-3. ")]
    [InlineData("lower-greek", 49, "βα. ")]
    [InlineData("lower-greek", 0, "0. ")]
    [InlineData("upper-latin", 49, "AW. ")]
    [InlineData("lower-roman", 2024, "mmxxiv. ")]
    [InlineData("upper-roman", 4000, "4000. ")]
    [InlineData("armenian", 2024, "ՍԻԴ. ")]
    [InlineData("lower-armenian", 19, "ժթ. ")]
    [InlineData("upper-armenian", 20304, "Բ̂ՅԴ. ")]
    [InlineData("georgian", 19999, "ჵჰშჟთ. ")]
    [InlineData("georgian", 20000, "20000. ")]
    [InlineData("hebrew", 15, "טו. ")]
    [InlineData("hebrew", 16, "טז. ")]
    [InlineData("hebrew", 2024, "ב׳כד. ")]
    [InlineData("hebrew", 0, "אפס. ")]
    [InlineData("cjk-decimal", 2024, "二〇二四、")]
    [InlineData("cjk-decimal", -3, "-3、")]
    [InlineData("cjk-earthly-branch", 12, "亥、")]
    [InlineData("cjk-earthly-branch", 13, "一三、")]
    [InlineData("cjk-heavenly-stem", 10, "癸、")]
    [InlineData("hiragana", 49, "ああ、")]
    [InlineData("katakana", 48, "ン、")]
    [InlineData("hiragana-iroha", 47, "す、")]
    [InlineData("katakana-iroha", 48, "イイ、")]
    [InlineData("japanese-informal", 2024, "二千二十四、")]
    [InlineData("japanese-informal", 10000, "一〇〇〇〇、")]
    [InlineData("japanese-formal", 1010, "壱阡壱拾、")]
    [InlineData("japanese-informal", -3, "マイナス三、")]
    [InlineData("korean-hangul-formal", 19999, "일만 구천구백구십구, ")]
    [InlineData("korean-hanja-informal", 10001, "萬 一, ")]
    [InlineData("korean-hanja-formal", 115, "壹百壹拾五, ")]
    [InlineData("korean-hangul-formal", -3, "마이너스 삼, ")]
    [InlineData("simp-chinese-informal", 15, "十五、")]
    [InlineData("simp-chinese-informal", 1010, "一千零一十、")]
    [InlineData("simp-chinese-informal", 100000, "十万、")]
    [InlineData("simp-chinese-informal", 20304, "二万零三百零四、")]
    [InlineData("simp-chinese-informal", 100010000, "一亿零一万、")]
    [InlineData("simp-chinese-formal", 2024, "贰仟零贰拾肆、")]
    [InlineData("trad-chinese-informal", -10000, "負一萬、")]
    [InlineData("trad-chinese-formal", 100000, "壹拾萬、")]
    [InlineData("cjk-ideographic", 0, "零、")]
    [InlineData("arabic-indic", 2024, "٢٠٢٤. ")]
    [InlineData("persian", -3, "-۳. ")]
    [InlineData("devanagari", 19, "१९. ")]
    [InlineData("bengali", 10, "১০. ")]
    [InlineData("gurmukhi", 5, "੫. ")]
    [InlineData("gujarati", 5, "૫. ")]
    [InlineData("oriya", 5, "୫. ")]
    [InlineData("tamil", 5, "௫. ")]
    [InlineData("telugu", 5, "౫. ")]
    [InlineData("kannada", 5, "೫. ")]
    [InlineData("malayalam", 5, "൫. ")]
    [InlineData("thai", 49, "๔๙. ")]
    [InlineData("lao", 5, "໕. ")]
    [InlineData("tibetan", 5, "༥. ")]
    [InlineData("myanmar", 5, "၅. ")]
    [InlineData("khmer", 5, "៥. ")]
    [InlineData("cambodian", 5, "៥. ")]
    [InlineData("mongolian", 5, "᠕. ")]
    [InlineData("ethiopic-numeric", 1001, "፲፻፩/ ")]
    [InlineData("ethiopic-numeric", 1000000, "፻፼/ ")]
    [InlineData("disclosure-closed", 3, "▸ ")]
    [InlineData("square", 3, "■ ")]
    [InlineData("LOWER-GREEK", 2, "β. ")]
    [InlineData("no-such-style", 3, "3. ")]
    public void Predefined_counter_styles_match_chrome(string style, int value, string expected)
        => Assert.Equal(expected, Marker(style, value));

    [Fact]
    public void Extreme_values_fall_back_without_failing()
    {
        foreach (var name in new[] { "lower-alpha", "lower-greek", "simp-chinese-formal", "korean-hanja-informal", "ethiopic-numeric", "hebrew", "armenian", "decimal" })
        {
            Assert.NotEmpty(Marker(name, int.MinValue));
            Assert.NotEmpty(Marker(name, int.MaxValue));
        }
        Assert.Equal("二十一亿四千七百四十八万三千六百四十七、", Marker("simp-chinese-informal", int.MaxValue));
        Assert.Equal("-2147483648. ", Marker("decimal", int.MinValue));
    }

    // ---- @counter-style ----

    [Theory]
    [InlineData("@counter-style paren { system: extends decimal; prefix: '('; suffix: ') ' }", "paren", 1, "(1) ")]
    [InlineData("@counter-style paren { system: extends decimal; prefix: '('; suffix: ') ' }", "paren", -4, "(-4) ")]
    [InlineData("@counter-style abc { system: alphabetic; symbols: a b c }", "abc", 4, "aa. ")]
    [InlineData("@counter-style cyc { system: cyclic; symbols: '*' '+'; suffix: '' }", "cyc", 3, "*")]
    [InlineData("@counter-style pad3 { system: extends decimal; pad: 3 '0'; negative: '(' ')' }", "pad3", -4, "(4). ")]
    [InlineData("@counter-style pad3 { system: extends decimal; pad: 3 '0' }", "pad3", 7, "007. ")]
    [InlineData("@counter-style add { system: additive; additive-symbols: 5 V, 1 I; range: 1 7; fallback: lower-greek }", "add", 8, "θ. ")]
    [InlineData("@counter-style add { system: additive; additive-symbols: 5 V, 1 I; range: 1 7 }", "add", 7, "VII. ")]
    [InlineData("@counter-style add2 { system: additive; additive-symbols: 1 I; range: 1 3; suffix: ') '; fallback: lower-greek }", "add2", 5, "ε) ")]
    [InlineData("@counter-style fx { system: fixed 2; symbols: A B }", "fx", 3, "B. ")]
    [InlineData("@counter-style fx { system: fixed 2; symbols: A B }", "fx", 4, "4. ")]
    [InlineData("@counter-style sym { system: symbolic; symbols: '*' '+' }", "sym", 5, "***. ")]
    [InlineData("@counter-style num { system: numeric; symbols: '0' '1'; }", "num", 5, "101. ")]
    [InlineData("@counter-style rng { system: extends decimal; range: 2 3, 6 infinite; fallback: upper-alpha }", "rng", 1, "A. ")]
    [InlineData("@counter-style rng { system: extends decimal; range: 2 3, 6 infinite; fallback: upper-alpha }", "rng", 5, "E. ")]
    [InlineData("@counter-style lower-roman { system: extends upper-alpha; suffix: '] ' }", "lower-roman", 3, "C] ")]
    [InlineData("@counter-style UPPER-ROMAN { system: cyclic; symbols: U }", "upper-roman", 4, "U. ")]
    [InlineData("@counter-style a { system: extends b; prefix: 'A' } @counter-style b { system: extends a; suffix: '!' }", "a", 3, "A3. ")]
    [InlineData("@counter-style Foo { system: cyclic; symbols: F }", "Foo", 1, "F. ")]
    [InlineData("@counter-style Foo { system: cyclic; symbols: F }", "foo", 1, "1. ")]
    [InlineData("@counter-style bad { system: alphabetic; symbols: X }", "bad", 1, "1. ")]
    [InlineData("@counter-style bad { system: additive; additive-symbols: 1 a, 5 b }", "bad", 1, "1. ")]
    [InlineData("@counter-style bad { system: extends lower-greek; symbols: a b }", "bad", 1, "1. ")]
    [InlineData("@counter-style decimal { system: cyclic; symbols: x }", "decimal", 1, "1. ")]
    [InlineData("@counter-style esc { system: cyclic; symbols: '\\2605' \\2606; suffix: ' ' }", "esc", 2, "☆ ")]
    [InlineData("@counter-style g { system: extends lower-greek; } @counter-style g2 { system: extends g; suffix: '' }", "g2", 2, "β")]
    public void Counter_style_rules(string css, string name, int value, string expected) => Assert.Equal(expected, Marker(css, name, value));

    [Fact]
    public void Counter_styles_in_list_markers_and_generated_content()
    {
        var ops = Layout(@"<style>
            @counter-style thumbs { system: cyclic; symbols: '>'; suffix: ' ' }
            .g { list-style-type: lower-greek }
            .t { list-style: thumbs inside }
            .s { list-style-type: symbols(cyclic '*' '+') }
            .c { counter-reset: n 4 }
            .c p::before { content: counter(n, upper-roman) '|' counter(n, thumbs) '|' counter(n, none) '|'; counter-increment: n }
            .nest { counter-reset: item }
            .nest li { counter-increment: item }
            .nest li::before { content: counters(item, '.', lower-alpha) ' ' }
            </style>
            <ol class=g><li>a</li><li>b</li></ol><ul class=t><li>c</li></ul><ul class=s><li>d</li><li>e</li></ul>
            <div class=c><p>p</p></div>
            <ol class=nest style='list-style: none'><li>x<ol class=nest style='list-style: none'><li>y</li></ol></li></ol>");
        var texts = Texts(ops);
        Assert.Contains("α.", texts);
        Assert.Contains("β.", texts);
        Assert.Contains(">", texts);
        Assert.Contains("*", texts);
        Assert.Contains("+", texts);
        Assert.Contains(texts, t => t.StartsWith("V|>||", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("a.a", StringComparison.Ordinal));
    }

    [Fact]
    public void A_counter_style_marker_ends_its_suffix_space_before_the_content()
    {
        var ops = Layout("<ol style='list-style-type: lower-greek'><li>item</li></ol><ol><li>item</li></ol>");
        var greek = ops.OfType<TextOp>().Single(t => t.Text == "α.");
        var decimalMarker = ops.OfType<TextOp>().Single(t => t.Text == "1.");
        var items = ops.OfType<TextOp>().Where(t => t.Text.StartsWith("item", StringComparison.Ordinal)).ToList();
        float space = greek.Font.CharWidth(' ', greek.Size);
        Near(items[0].X - space, greek.X + greek.Font.Measure("α.", greek.Size), 0.1f);
        Near(items[1].X - space, decimalMarker.X + decimalMarker.Font.Measure("1.", decimalMarker.Size), 0.1f);
    }

    [Fact]
    public void Counter_styles_reach_page_margin_boxes()
    {
        var pdf = ConvertUncompressed(@"<style>
            @counter-style pg { system: extends upper-alpha; prefix: '<'; suffix: '>' }
            @page { @bottom-center { content: counter(page, pg) counter(page, pg) } }
          </style><p>one</p><p style='break-before: page'>two</p>");
        var content = AllContent(pdf);
        Assert.Contains("(AA)", content);
        Assert.Contains("(BB)", content);
    }

    [Fact]
    public void Disclosure_markers_are_triangles_on_the_baseline()
    {
        var ops = Layout("<ul style='font-size: 15pt'><li style='list-style-type: disclosure-closed; color: red'>item</li></ul>");
        var tri = Assert.Single(ops.OfType<SvgOp>());
        var item = ops.OfType<TextOp>().Single(t => t.Text.StartsWith("item", StringComparison.Ordinal));
        // 20px * 0.66 = 13px, 8px before the content, as Chrome draws it.
        Near(13 * 0.75f, tri.W);
        Near(item.X - 21 * 0.75f, tri.X);
        Near(item.Baseline, tri.Y + tri.H);
        Assert.Equal(0xFFFF0000u, tri.Image.Shapes.Single().Fill);
    }

    // ---- list-style-image ----

    [Fact]
    public void List_style_image_draws_the_image_on_the_baseline_before_the_content()
    {
        var png = DataUri(SolidPng(10, 10, 255, 0, 0));
        var ops = Layout($"<ul style=\"list-style-image: url('{png}')\"><li>item</li></ul>");
        var img = Assert.Single(ops.OfType<ImageOp>());
        var item = ops.OfType<TextOp>().Single(t => t.Text.StartsWith("item", StringComparison.Ordinal));
        Near(7.5f, img.W);
        Near(7.5f, img.H);
        Near(item.X - 5.25f, img.X + img.W);
        Near(item.Baseline, img.Y + img.H);
        Assert.Empty(ops.OfType<CircleOp>());
    }

    [Fact]
    public void List_style_shorthand_takes_the_image_and_svg_images_size_as_in_chrome()
    {
        var sized = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns='http://www.w3.org/2000/svg' width='20' height='8'><rect width='20' height='8' fill='red'/></svg>");
        var ratio = "data:image/svg+xml," + Uri.EscapeDataString("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 2 1'><rect width='2' height='1' fill='red'/></svg>");
        var ops = Layout($"<ul style=\"list-style: square url('{sized}') outside\"><li>a</li></ul><ul style=\"list-style: url('{ratio}')\"><li>b</li></ul>");
        var svgs = ops.OfType<SvgOp>().ToList();
        Assert.Equal(2, svgs.Count);
        Near(15, svgs[0].W);
        Near(6, svgs[0].H);
        // Without a size: half the font's ascent wide, with the viewBox's aspect ratio.
        Assert.True(svgs[1].W > 3 && svgs[1].W < 8, svgs[1].W.ToString());
        Near(svgs[1].W / 2, svgs[1].H);
        Assert.DoesNotContain(ops.OfType<RectOp>(), r => r.W < 10);
    }

    [Fact]
    public void A_list_style_image_that_does_not_load_falls_back_to_the_type()
    {
        var ops = Layout("<ul style=\"list-style: url('missing.png') lower-roman\"><li>a</li><li>b</li></ul>"
                         + "<ul style=\"list-style: url('missing.png') none\"><li>c</li></ul>"
                         + "<ul style=\"list-style-image: url('missing.png')\"><li>d</li></ul>");
        var texts = Texts(ops);
        Assert.Contains("i.", texts);
        Assert.Contains("ii.", texts);
        Assert.Single(ops.OfType<CircleOp>()); // only the third list keeps its disc
        Assert.Empty(ops.OfType<ImageOp>());
    }

    [Fact]
    public void Marker_content_wins_over_list_style_image()
    {
        var png = DataUri(SolidPng(4, 4, 0, 0, 255));
        var ops = Layout($"<style>li::marker {{ content: '-> ' }}</style><ul style=\"list-style-image: url('{png}')\"><li>a</li></ul>");
        Assert.Empty(ops.OfType<ImageOp>());
        Assert.Contains(Texts(ops), t => t.StartsWith("->", StringComparison.Ordinal));
    }

    [Fact]
    public void Marker_direction_orders_its_text_on_the_items_side()
    {
        // The item's direction puts the marker before its content and the marker's own direction orders its text,
        // with the suffix's space at the marker's end: ".1" right against the content, or "1." right after its end.
        var ops = Layout("<ol style='direction:ltr;padding-left:40pt'><li style='font:10pt Helvetica'>a</li></ol>" +
                         "<style>.r li::marker { direction: rtl } .l li::marker { direction: ltr }</style>" +
                         "<ol class=r style='padding-left:40pt'><li style='font:10pt Helvetica'>b</li></ol>" +
                         "<ol class=l dir=rtl style='padding-right:40pt'><li style='font:10pt Helvetica'>c</li></ol>");
        var texts = ops.OfType<TextOp>().ToList();
        TextOp Of(string t) => texts.First(o => o.Text == t);
        var b = texts.First(o => o.Text.Contains('b'));
        var c = texts.First(o => o.Text.Contains('c'));
        Assert.Contains(texts, o => o.Text == "1.");
        Assert.Equal(".1", Of(".1").Text);
        Assert.Equal(b.X, Of(".1").X + Of(".1").Font.Measure(".1", 10), 1);
        var ltrMarker = texts.Last(o => o.Text == "1.");
        Assert.Equal(c.X + c.Font.Measure("c", 10), ltrMarker.X, 1);
    }

    [Fact]
    public void Counter_list_item_numbers_marker_and_before_content()
    {
        // Each list counts its own items, from an ol's start, down in a reversed list, and from a value attribute,
        // as the default markers do; a counter-reset on the list and a counter-increment on an item take part.
        var ops = Layout("<style>li::marker { content: counter(list-item) ') ' } .b li::before { content: '[' counter(list-item) ']' }</style>" +
                         "<ol><li>a</li><li value=7>b</li><li>c<ol><li>d</li></ol></li></ol>" +
                         "<ol start=4 reversed><li>e</li><li>f</li></ol><ol class=b style='counter-reset: list-item 10'><li>g</li></ol>" +
                         "<ol><li style='counter-increment: list-item 5'>h</li><li>i</li></ol>");
        var texts = Texts(ops).Select(t => t.Trim()).ToList();
        foreach (var marker in new[] { "1)", "7)", "8)", "4)", "3)", "11)", "5)", "6)" }) Assert.Contains(marker, texts);
        Assert.Equal(2, texts.Count(t => t == "1)")); // the nested list starts again
        Assert.Contains(texts, t => t.StartsWith("[11]", StringComparison.Ordinal));
        Assert.DoesNotContain("0)", texts);
    }

    [Fact]
    public void List_style_shorthand_resets_the_longhands()
    {
        var ops = Layout("<ol style='list-style: inside'><li>a</li></ol><ol style='list-style: none'><li>b</li></ol>");
        // "inside" alone resets the type to disc; "none" removes the marker.
        Assert.Single(ops.OfType<CircleOp>());
        Assert.DoesNotContain("1.", Texts(ops));
    }

    [Fact]
    public void Unusual_counter_styles_render_to_a_valid_pdf()
    {
        var html = "<style>@counter-style x { system: additive; additive-symbols: 1000000 '#', 1 '.'; } li::after { content: counter(list-item, x) }</style>"
                   + string.Concat(new[] { "hebrew", "georgian", "trad-chinese-formal", "korean-hangul-formal", "tibetan", "mongolian", "ethiopic-numeric", "x", "disclosure-open" }
                       .Select(s => $"<ol style='list-style-type: {s}'><li value=2147483647>a</li><li value=-5>b</li><li>c</li></ol>"));
        var pdf = ConvertUncompressed(html);
        AssertStructurallyValid(pdf);
    }
}

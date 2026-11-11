using PdfPecker.Css;
using PdfPecker.Fonts;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Tests;

/// <summary>
/// Upright text in vertical writing modes: text-orientation stands Chinese and Japanese characters (and with upright
/// every character) back up in lines that are laid out horizontally and turned, text-combine-upright sets digits across
/// the line in one em, and pictures stand upright, as Chrome draws them.
/// </summary>
public class TextOrientationTests
{
    // A subset of Noto Sans SC (SIL Open Font License, see Fixtures/fonts), which has the vert feature. Its ideographs
    // and fullwidth punctuation are one em wide.
    private static readonly FontCollection Cjk = new FontCollection().Add(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansSC-Subset.woff2")), "CJK");

    private const string Pixel = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";

    private static List<DrawOp> Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var fonts = FontResolver.Create([], Cjk, _ => null);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block, Fonts = fonts });
        var engine = new LayoutEngine(800, new ImageLoader(null, null, false));
        engine.Layout(doc, 500);
        return engine.Ops;
    }

    private static string Vertical(string content, string style = "")
        => $"<div style='writing-mode: vertical-rl; height: 200pt; font: 10pt/1.5 CJK; margin: 0; {style}'>{content}</div>";

    /// <summary>Each op with the transform that takes it to the page: the layers' transforms about their origins.</summary>
    private static List<(DrawOp Op, Matrix ToPage)> Placed(IEnumerable<DrawOp> ops) => Placed(ops, Matrix.Identity);

    private static List<(DrawOp Op, Matrix ToPage)> Placed(IEnumerable<DrawOp> ops, Matrix outer)
    {
        var placed = new List<(DrawOp, Matrix)>();
        foreach (var op in ops)
        {
            placed.Add((op, outer));
            if (op is not GroupOp g) continue;
            var m = outer;
            if (g is LayerOp { Transform: { } t } layer)
                m = Matrix.Translate(-layer.OriginX, -layer.OriginY).Then(t).Then(Matrix.Translate(layer.OriginX, layer.OriginY)).Then(outer);
            placed.AddRange(Placed(g.Ops, m));
        }
        return placed;
    }

    private static List<(TextOp Op, Matrix ToPage)> Texts(List<DrawOp> ops)
        => Placed(ops).Where(p => p.Op is TextOp).Select(p => ((TextOp)p.Op, p.ToPage)).ToList();

    private static bool Upright(Matrix m) => Math.Abs(m.A - 1) < 1e-4 && Math.Abs(m.B) < 1e-4 && Math.Abs(m.C) < 1e-4;

    private static bool TurnedClockwise(Matrix m) => Math.Abs(m.A) < 1e-4 && Math.Abs(m.B - 1) < 1e-4;

    private static (float X, float Y) OnPage(TextOp t, Matrix m)
    {
        float x = t.X, y = t.Baseline;
        m.Apply(ref x, ref y);
        return (x, y);
    }

    [Fact]
    public void Ideographs_in_vertical_text_stand_upright_one_em_apart()
    {
        var texts = Texts(Layout(Vertical("中文本")));
        Assert.Equal(["中", "文", "本"], texts.Select(t => t.Op.Text));
        Assert.All(texts, t => Assert.True(Upright(t.ToPage), $"{t.Op.Text} is turned"));
        var at = texts.Select(t => OnPage(t.Op, t.ToPage)).ToList();
        // One under the other, an em apart, in one column.
        Assert.Equal(at[0].X, at[1].X, 2);
        Assert.Equal(at[0].X, at[2].X, 2);
        Assert.Equal(10, at[1].Y - at[0].Y, 2);
        Assert.Equal(10, at[2].Y - at[1].Y, 2);
    }

    [Fact]
    public void Latin_words_in_mixed_vertical_text_stay_turned()
    {
        var texts = Texts(Layout(Vertical("中文ABC本")));
        var latin = Assert.Single(texts, t => t.Op.Text == "ABC");
        Assert.True(TurnedClockwise(latin.ToPage));
        Assert.All(texts.Where(t => t.Op.Text != "ABC"), t => Assert.True(Upright(t.ToPage)));
        // The ideograph after the word starts where the word ends.
        var abc = OnPage(latin.Op, latin.ToPage);
        var after = texts.Single(t => t.Op.Text == "本");
        float width = latin.Op.Font.Measure("ABC", 10);
        Assert.True(OnPage(after.Op, after.ToPage).Y > abc.Y + width, "本 overlaps the word before it");
    }

    [Fact]
    public void Sideways_turns_every_character()
    {
        var texts = Texts(Layout(Vertical("中文ABC", "text-orientation: sideways")));
        var text = Assert.Single(texts);
        Assert.Equal("中文ABC", text.Op.Text);
        Assert.True(TurnedClockwise(text.ToPage));
    }

    [Fact]
    public void Upright_stands_latin_letters_up_one_em_apart()
    {
        var texts = Texts(Layout(Vertical("AB", "text-orientation: upright")));
        Assert.Equal(["A", "B"], texts.Select(t => t.Op.Text));
        Assert.All(texts, t => Assert.True(Upright(t.ToPage)));
        Assert.Equal(10, OnPage(texts[1].Op, texts[1].ToPage).Y - OnPage(texts[0].Op, texts[0].ToPage).Y, 2);
    }

    [Fact]
    public void Upright_punctuation_takes_the_fonts_vertical_forms()
    {
        var texts = Texts(Layout(Vertical("中、文。")));
        var comma = Assert.Single(texts, t => t.Op.Text == "、");
        var plain = ((FontChain)Layout("<p style='font: 10pt CJK'>、</p>").OfType<TextOp>().Single().Font).Pick('、');
        var drawn = ((FontChain)comma.Op.Font).Pick('、');
        Assert.NotEqual(((EmbeddedFont)plain).Program.GlyphId('、'), ((EmbeddedFont)drawn).Program.GlyphId('、'));
    }

    [Fact]
    public void Horizontal_text_is_left_alone()
    {
        var ops = Layout("<p style='font: 10pt CJK; text-orientation: upright; text-combine-upright: all'>中文本</p>");
        var text = Assert.Single(ops.OfType<TextOp>());
        Assert.Equal("中文本", text.Text);
        Assert.DoesNotContain(Placed(ops), p => p.Op is LayerOp { Transform: not null });
    }

    [Fact]
    public void Combined_digits_take_one_em_and_stand_across_the_line()
    {
        var texts = Texts(Layout(Vertical("平成<span style='text-combine-upright: all'>31</span>年")));
        var digits = Assert.Single(texts, t => t.Op.Text == "31");
        // Two digits of this font are a little wider than an em, so they are squeezed along their length.
        var m = digits.ToPage;
        Assert.True(Math.Abs(m.B) < 1e-4 && Math.Abs(m.C) < 1e-4 && Math.Abs(m.D - 1) < 1e-4 && m.A is > 0.8f and <= 1, "the digits are turned");
        float y(string s) => texts.Single(t => t.Op.Text == s) is var t ? OnPage(t.Op, t.ToPage).Y : 0;
        Assert.Equal(20, y("年") - y("成"), 2);
        // Centred across the column of the ideographs.
        var cheng = texts.Single(t => t.Op.Text == "成");
        float digitsCentre = OnPage(digits.Op, m).X + digits.Op.Font.Measure("31", 10) * m.A / 2;
        Assert.Equal(OnPage(cheng.Op, cheng.ToPage).X + 5, digitsCentre, 0);
    }

    [Fact]
    public void Combined_text_wider_than_an_em_is_squeezed()
    {
        var texts = Texts(Layout(Vertical("<span style='-webkit-text-combine: horizontal'>2024</span>年")));
        var digits = Assert.Single(texts, t => t.Op.Text == "2024");
        Assert.True(digits.ToPage.A is > 0.3f and < 0.99f, $"scale {digits.ToPage.A}");
        Assert.Equal(0, digits.ToPage.B, 3);
    }

    [Fact]
    public void Images_in_vertical_text_stand_upright_in_their_box()
    {
        var ops = Layout(Vertical($"中<img src='{Pixel}' style='width: 20pt; height: 8pt'>文"));
        var (op, toPage) = Assert.Single(Placed(ops), p => p.Op is ImageOp);
        var image = (ImageOp)op;
        Assert.True(Upright(toPage), "the image is turned");
        // Drawn 20pt wide and 8pt tall on the page.
        Assert.Equal(20, image.W, 1);
        Assert.Equal(8, image.H, 1);
    }

    [Fact]
    public void An_image_with_one_side_given_keeps_its_ratio_upright()
    {
        // A 2 by 1 pixel image 20pt wide is 10pt tall on the page, as in Chrome.
        const string wide = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAIAAAB7QOjdAAAADUlEQVR4nGP4zwAE/wEHAAH/4iOeWQAAAABJRU5ErkJggg==";
        var ops = Layout(Vertical($"<img src='{wide}' style='width: 20pt'>"));
        var (op, toPage) = Assert.Single(Placed(ops), p => p.Op is ImageOp);
        var image = (ImageOp)op;
        Assert.True(Upright(toPage));
        Assert.Equal(20, image.W, 1);
        Assert.Equal(10, image.H, 1);
    }

    [Theory]
    [InlineData("text-orientation: upright", "Upright", false)]
    [InlineData("-webkit-text-orientation: sideways-right", "Sideways", false)]
    [InlineData("text-orientation: sideways; text-orientation: bogus", "Sideways", false)]
    [InlineData("text-combine-upright: all", "Mixed", true)]
    [InlineData("-webkit-text-combine: horizontal", "Mixed", true)]
    public void The_properties_are_read_and_inherited(string css, string orientation, bool combine)
    {
        var doc = HtmlParser.Parse($"<div style='{css}'><span>x</span></div>");
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var span = Find(doc, "span");
        Assert.Equal(orientation, span.Style!.TextOrientation.ToString());
        Assert.Equal(combine, span.Style.TextCombineUpright);
    }

    [Fact]
    public void Vertical_japanese_converts_to_pdf()
    {
        var fonts = new FontCollection().Add(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansSC-Subset.woff2")), "CJK");
        var pdf = HtmlToPdf.Convert(Vertical("这是「中文」，<span style='text-combine-upright: all'>12</span>页。"), new PdfOptions { Fonts = fonts });
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        var text = PdfText.Extract(pdf);
        Assert.Contains("中文", text);
    }

    private static Element Find(Element e, string name)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Name == name) return c;
            if (Find(c, name) is { } d) return d;
        }
        return null!;
    }
}

namespace PdfPecker.Fluent.Tests;

/// <summary>The calls that close the gap to QuestPDF: transforms, layers, inlined and multi-column content, and the rest.</summary>
public sealed class ExtrasTests
{
    private static string Css(Action<IContainer> content) => Html.Css(Html.Page(c => c.Column(col => content(col.Item()))));

    [Fact]
    public void Rotate_and_scale_turn_around_the_top_left_corner()
    {
        Assert.Contains("{transform:rotate(30deg);transform-origin:0 0}", Css(c => c.Rotate(30).Text("x")));
        Assert.Contains("{transform:scale(1.5,1.5);transform-origin:0 0}", Css(c => c.Scale(1.5f).Text("x")));
        Assert.Contains("{transform:scale(2,1);transform-origin:0 0}", Css(c => c.ScaleHorizontal(2).Text("x")));
    }

    [Fact]
    public void Flips_and_translations_stay_in_place()
    {
        Assert.Contains("{transform:scale(-1,1)}", Css(c => c.FlipHorizontal().Text("x")));
        Assert.Contains("{transform:scale(-1,-1)}", Css(c => c.FlipOver().Text("x")));
        Assert.Contains("{transform:translate(0,-5pt)}", Css(c => c.TranslateY(-5).Text("x")));
    }

    [Fact]
    public void Two_transforms_nest_so_they_compose()
        => Assert.Equal("<div class=\"c1\"><p class=\"c2\">x</p></div>", Html.Item(c => c.TranslateX(10).Rotate(45).Text("x")));

    [Fact]
    public void A_transform_after_a_background_leaves_the_background_alone()
        => Assert.Equal("<div class=\"c1\"><p class=\"c2\">x</p></div>", Html.Item(c => c.Background(Colors.Red.Medium).Rotate(10).Text("x")));

    [Fact]
    public void Quarter_turns_turn_the_layout_with_writing_mode()
    {
        Assert.Contains("{writing-mode:vertical-rl}", Css(c => c.RotateRight().Text("x")));
        Assert.Contains("{writing-mode:vertical-rl;transform:rotate(180deg)}", Css(c => c.RotateLeft().Text("x")));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(5000f)]
    public void Bad_scale_factors_throw(float factor)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Scale(factor).Text("x")));

    [Fact]
    public void Layers_stack_in_document_order()
    {
        var document = Html.Page(c => c.Layers(l =>
        {
            l.Layer().Text("under");
            l.PrimaryLayer().Text("main");
            l.Layer().Text("over");
        }));
        string css = Html.Css(document);
        Assert.Contains("{position:relative;z-index:0}", css);
        Assert.Contains("{position:absolute;top:0;right:0;bottom:0;left:0}", css);
        Assert.Contains("{position:relative}", css);
        Assert.DoesNotContain("z-index:1", css);
        string html = document.GenerateHtml();
        Assert.True(html.IndexOf(">under<", StringComparison.Ordinal) < html.IndexOf(">main<", StringComparison.Ordinal));
        Assert.True(html.IndexOf(">main<", StringComparison.Ordinal) < html.IndexOf(">over<", StringComparison.Ordinal));
    }

    [Fact]
    public void Layers_need_exactly_one_primary_layer()
    {
        Assert.Throws<InvalidOperationException>(() => Html.Of(c => c.Layers(l => l.Layer().Text("x"))));
        Assert.Throws<InvalidOperationException>(() => Html.Of(c => c.Layers(l =>
        {
            l.PrimaryLayer().Text("a");
            l.PrimaryLayer().Text("b");
        })));
    }

    [Fact]
    public void Inlined_items_wrap_and_keep_their_words_whole()
    {
        string css = Html.Css(Html.Page(c => c.Inlined(i =>
        {
            i.HorizontalSpacing(6);
            i.VerticalSpacing(3);
            i.AlignCenter();
            i.BaselineBottom();
            i.Item().Text("a");
        })));
        Assert.Contains("{display:flex;flex-wrap:wrap;row-gap:3pt;column-gap:6pt;justify-content:center;align-items:flex-end}", css);
        Assert.Contains("{flex:0 0 auto;max-width:100%}", css);
    }

    [Fact]
    public void Multi_column_content_flows_through_css_columns()
    {
        string css = Html.Css(Html.Page(c => c.MultiColumn(m =>
        {
            m.Columns(3);
            m.Spacing(10);
            m.BalanceHeight(false);
            m.Content().Text("x");
        })));
        Assert.Contains("{column-count:3;column-gap:10pt;column-fill:auto}", css);
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.MultiColumn(m => m.Columns(0))));
    }

    [Fact]
    public void Ensure_space_moves_the_content_to_a_new_page_when_too_little_is_left()
    {
        static byte[] Pdf(float space) => Document.Create(d => d.Page(p =>
        {
            p.Size(300, 300);
            p.Margin(20);
            p.Content().Column(col =>
            {
                col.Item().Height(200).Background(Colors.Grey.Lighten3);
                col.Item().EnsureSpace(space).Text("heading");
            });
        })).GeneratePdf();

        Assert.Equal(1, PdfToImage.GetPageCount(Pdf(30)));
        Assert.Equal(2, PdfToImage.GetPageCount(Pdf(80)));
    }

    [Fact]
    public void Ensure_space_writes_an_unbreakable_spacer_before_the_content()
    {
        Assert.Equal("<div><div class=\"c1\"></div><p>x</p></div>", Html.Item(c => c.EnsureSpace(100).Text("x")));
        Assert.Contains(".c1{break-inside:avoid;height:100pt;margin-bottom:-100pt}", Css(c => c.EnsureSpace(100).Text("x")));
    }

    [Fact]
    public void Unconstrained_content_takes_no_space_and_its_natural_width()
    {
        Assert.Equal("<div class=\"c1\"><p class=\"c2\">x</p></div>", Html.Item(c => c.Unconstrained().Text("x")));
        string css = Css(c => c.Unconstrained().Text("x"));
        Assert.Contains("{width:0;height:0;min-height:0}", css);
        Assert.Contains("{width:max-content}", css);
    }

    [Fact]
    public void Z_index_positions_the_slot()
        => Assert.Contains("{position:relative;z-index:3}", Css(c => c.ZIndex(3).Text("x")));

    [Fact]
    public void Aspect_ratio_options()
    {
        Assert.Contains("{width:100%;aspect-ratio:2}", Css(c => c.AspectRatio(2).Text("x")));
        Assert.Contains("{height:100%;width:auto;aspect-ratio:0.5}", Css(c => c.AspectRatio(0.5f, AspectRatioOption.FitHeight).Text("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.AspectRatio(0)));
    }

    [Fact]
    public void Shadow_and_single_corners()
    {
        Assert.Contains("{box-shadow:1pt 2pt 3pt 0pt #ff0000}",
            Css(c => c.Shadow(new BoxShadowStyle { OffsetX = 1, OffsetY = 2, Blur = 3, Color = "#ff0000" }).Text("x")));
        Assert.Contains("{border-top-left-radius:4pt;overflow:hidden}", Css(c => c.CornerRadiusTopLeft(4).Text("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Shadow(new BoxShadowStyle { Blur = -1 })));
    }

    [Fact]
    public void Clamped_lines_and_first_line_indent()
    {
        string css = Css(c => c.Text("x").ClampLines(2).ParagraphFirstLineIndentation(12));
        Assert.Contains("text-indent:12pt;display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden", css);
        Assert.Throws<ArgumentOutOfRangeException>(() => Html.Of(c => c.Text("x").ClampLines(0)));
    }

    [Fact]
    public void Decoration_styles_and_word_spacing()
    {
        string css = Css(c => c.Text(t =>
        {
            t.Span("a").Underline().DecorationWavy().DecorationColor("#ff0000").DecorationThickness(2);
            t.Span("b").WordSpacing(0.5f);
        }));
        Assert.Contains("{text-decoration-line:underline;text-decoration-style:wavy;text-decoration-color:#ff0000;text-decoration-thickness:0.14em}", css);
        Assert.Contains("{word-spacing:0.5em}", css);
    }

    [Fact]
    public void Dashed_and_dotted_lines()
    {
        Assert.Contains("border-top-style:dashed", Css(c => c.LineHorizontal(1).LineDashPattern([4, 2])));
        Assert.Contains("border-top-style:dotted", Css(c => c.LineHorizontal(2).LineDashPattern([1, 1])));
        Assert.Throws<ArgumentException>(() => Html.Of(c => c.LineHorizontal(1).LineDashPattern([])));
    }

    [Fact]
    public void Placeholder_debug_area_and_lazy_render_content()
    {
        Assert.Contains(">Logo<", Html.Of(c => c.Placeholder("Logo")));
        Assert.Contains(">area<", Html.Of(c => c.DebugArea("area").Text("inside")));
        Assert.Contains(">inside<", Html.Of(c => c.DebugArea("area").Text("inside")));
        Assert.Contains(">lazy<", Html.Of(c => c.Lazy(x => x.Text("lazy"))));
    }
}

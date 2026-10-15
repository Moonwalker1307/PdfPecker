using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;

namespace PdfPecker.Tests;

/// <summary>CSS Color 4/5, math functions, cascade layers, nesting, @supports, @property, media queries and selectors 4.</summary>
public class Css3Tests
{
    private const float Width = 500, PageHeight = 800;

    private static Element Styled(string html, float width = Width)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;

        static IEnumerable<Element> Find(Element e, string name)
        {
            foreach (var c in e.Children.OfType<Element>())
            {
                if (c.Name == name) yield return c;
                foreach (var d in Find(c, name)) yield return d;
            }
        }
    }

    private static Element ById(Element root, string id)
    {
        if (root.Id == id) return root;
        foreach (var c in root.Children)
            if (c is Element e && ById(e, id) is { } found) return found;
        return null!;
    }

    private static uint ColorOf(Element doc, string id) => ById(doc, id).Style.Color;

    private const uint Red = 0xFFFF0000, Blue = 0xFF0000FF, Black = 0xFF000000;

    private static uint Parse(string css)
    {
        Assert.True(CssColor.TryParse(css, Black, out uint c), css);
        return c;
    }

    private static void Near(uint expected, uint actual, int tolerance = 1)
    {
        for (int shift = 0; shift <= 24; shift += 8)
        {
            int e = (int)(expected >> shift & 0xFF), a = (int)(actual >> shift & 0xFF);
            Assert.True(Math.Abs(e - a) <= tolerance, $"expected {expected:X8}, got {actual:X8}");
        }
    }

    // ---- colours ----

    [Theory]
    [InlineData("rgb(255 0 0)", 0xFFFF0000)]
    [InlineData("rgb(255 0 0 / 50%)", 0x80FF0000)]
    [InlineData("rgba(0, 0, 255, .5)", 0x800000FF)]
    [InlineData("rgb(100% 50% 0%)", 0xFFFF8000)]
    [InlineData("hsl(120deg 100% 25%)", 0xFF008000)]
    [InlineData("hsl(0.5turn 100% 50%)", 0xFF00FFFF)]
    [InlineData("hsl(240, 100%, 50%, 0.25)", 0x400000FF)]
    [InlineData("hwb(0 0% 0%)", 0xFFFF0000)]
    [InlineData("hwb(120 20% 20%)", 0xFF33CC33)]
    [InlineData("lab(54.29 80.82 69.88)", 0xFFFF0000)]
    [InlineData("lch(54.29 106.84 40.85)", 0xFFFF0000)]
    [InlineData("oklab(0.628 0.2249 0.1258)", 0xFFFF0000)]
    [InlineData("oklch(62.8% 0.2577 29.23)", 0xFFFF0000)]
    [InlineData("oklch(0.452 0.313 264.05)", 0xFF0000FF)]
    [InlineData("color(srgb 1 0.5 0)", 0xFFFF8000)]
    [InlineData("color(display-p3 1 0 0)", 0xFFFF0000)]
    [InlineData("color(srgb-linear 0.2140 0.2140 0.2140)", 0xFF808080)]
    [InlineData("color(xyz-d65 0.9505 1 1.089)", 0xFFFFFFFF)]
    [InlineData("color-mix(in srgb, red, blue)", 0xFF800080)]
    [InlineData("color-mix(in srgb, red 25%, blue)", 0xFF4000BF)]
    [InlineData("color-mix(in srgb, red 40%, transparent)", 0x66FF0000)]
    [InlineData("color-mix(in srgb, red 30%, blue 30%)", 0x99800080)]
    [InlineData("color-mix(in oklab, white, black)", 0xFF636363)]
    [InlineData("light-dark(red, blue)", 0xFFFF0000)]
    [InlineData("Canvas", 0xFFFFFFFF)]
    [InlineData("rgb(calc(255 / 2) 0 0)", 0xFF800000)]
    public void Modern_colour_syntax_parses(string css, uint expected) => Near(expected, Parse(css));

    [Fact]
    public void Colour_mix_in_oklch_takes_the_shorter_hue_arc()
    {
        // Red (hue 29) and blue (hue 264) mix through magenta, not green.
        uint c = Parse("color-mix(in oklch, red, blue)");
        uint r = c >> 16 & 0xFF, g = c >> 8 & 0xFF, b = c & 0xFF;
        Assert.True(r > 150 && b > 150 && g < 80, $"{c:X8}");
    }

    [Theory]
    [InlineData("rgb(from red r g b)")]
    [InlineData("oklch(50% 0.1)")]
    [InlineData("color(unknown 1 0 0)")]
    [InlineData("color-mix(red, blue)")]
    public void Unsupported_or_invalid_colours_are_rejected(string css) => Assert.False(CssColor.TryParse(css, Black, out _));

    // ---- math functions ----

    [Theory]
    [InlineData("round(7.4)", 7)]
    [InlineData("round(up, 7.1, 2)", 8)]
    [InlineData("round(down, -7.1, 2)", -8)]
    [InlineData("round(to-zero, -7.9, 1)", -7)]
    [InlineData("round(2.5)", 3)]
    [InlineData("mod(-7, 3)", 2)]
    [InlineData("rem(-7, 3)", -1)]
    [InlineData("abs(-4)", 4)]
    [InlineData("sign(-0.5)", -1)]
    [InlineData("sin(90deg)", 1)]
    [InlineData("cos(0.5turn)", -1)]
    [InlineData("tan(45deg)", 1)]
    [InlineData("pow(2, 10)", 1024)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("hypot(3, 4)", 5)]
    [InlineData("log(e)", 1)]
    [InlineData("log(8, 2)", 3)]
    [InlineData("exp(0)", 1)]
    [InlineData("calc(2 * pi / pi)", 2)]
    public void Math_functions_on_numbers(string css, float expected)
    {
        Assert.True(CssMath.TryParseNumber(css, out float n), css);
        Assert.Equal(expected, n, 3);
    }

    [Theory]
    [InlineData("calc(45deg * 2)", 90)]
    [InlineData("atan2(1, 1)", 45)]
    [InlineData("acos(0)", 90)]
    [InlineData("calc(0.25turn + 10deg)", 100)]
    [InlineData("round(100deg, 45deg)", 90)]
    public void Math_functions_on_angles(string css, float expected)
    {
        Assert.True(CssMath.TryParseAngle(css, out float deg), css);
        Assert.Equal(expected, deg, 3);
    }

    [Fact]
    public void Stepped_lengths_resolve_against_percentages()
    {
        // round(50%, 7px) of 100px is 49px (in points).
        Assert.True(CssMath.TryParseLength("round(50%, 7px)", 12, 12, 0, 0, out var len));
        Assert.Equal(49 * 0.75f, len.Resolve(75), 3);
        Assert.True(CssMath.TryParseLength("mod(25px, 10px)", 12, 12, 0, 0, out var m));
        Assert.Equal(5 * 0.75f, m.Resolve(0), 3);
        Assert.True(CssMath.TryParseLength("calc(10px * sin(30deg))", 12, 12, 0, 0, out var s));
        Assert.Equal(5 * 0.75f, s.Resolve(0), 3);
    }

    [Fact]
    public void Angle_math_works_in_transforms_and_gradients()
    {
        var doc = Styled(@"<style>
            #a { transform: rotate(calc(0.125turn * 2)) }
            #b { width: round(down, 105px, 10px); opacity: calc(sin(30deg)) }
          </style><div id=a>a</div><div id=b>b</div>");
        Assert.NotNull(ById(doc, "a").Style.Transform);
        Assert.Equal(100 * 0.75f, ById(doc, "b").Style.Width.Value, 3);
        Assert.Equal(0.5f, ById(doc, "b").Style.Opacity, 3);
    }

    // ---- cascade layers ----

    [Fact]
    public void Unlayered_rules_beat_layered_rules_and_later_layers_beat_earlier()
    {
        var doc = Styled(@"<style>
            @layer base, theme;
            @layer theme { #a { color: blue } p { color: blue } }
            @layer base { #a { color: red } #b { color: red } }
            p { color: red }
            @layer theme { #c { color: blue } }
          </style><span id=a>a</span><span id=b>b</span><div id=c>c</div><p id=d>d</p>");
        // #a: theme (declared second) wins over base despite base coming later in the source.
        Assert.Equal(Blue, ColorOf(doc, "a"));
        // The unlayered "p" rule has lower specificity than theme's, but an unlayered rule beats any layer.
        Assert.Equal(Red, ColorOf(doc, "d"));
        Assert.Equal(Red, ColorOf(doc, "b"));
        Assert.Equal(Blue, ColorOf(doc, "c"));
    }

    [Fact]
    public void Unlayered_rule_beats_a_more_specific_layered_rule()
    {
        var doc = Styled(@"<style>
            @layer components { #x.y { color: blue } }
            p { color: red }
          </style><p id=x class=y>x</p>");
        Assert.Equal(Red, ColorOf(doc, "x"));
    }

    [Fact]
    public void Important_reverses_layer_order()
    {
        var doc = Styled(@"<style>
            @layer a { p { color: red !important } }
            @layer b { p { color: blue !important } }
            p { color: black !important }
          </style><p id=p>p</p>");
        Assert.Equal(Red, ColorOf(doc, "p"));
    }

    [Fact]
    public void Sub_layers_come_before_their_parent_and_anonymous_layers_count()
    {
        var doc = Styled(@"<style>
            @layer outer { p { color: blue } @layer inner { p { color: red } } }
            @layer { #q { color: red } }
            @layer outer.inner { #r { color: red } }
            @layer outer { #r { color: blue } }
          </style><p id=p>p</p><p id=q>q</p><p id=r>r</p>");
        Assert.Equal(Blue, ColorOf(doc, "p"));
        Assert.Equal(Red, ColorOf(doc, "q"));
        Assert.Equal(Blue, ColorOf(doc, "r"));
    }

    // ---- nesting ----

    [Fact]
    public void Nested_rules_resolve_against_their_parent()
    {
        var doc = Styled(@"<style>
            .card {
              color: red;
              .title { color: blue }
              & > em { color: blue }
              &.on { color: blue }
              > b { color: blue }
              .x & { color: blue }
              @media print { & span { color: blue } }
              @media screen { & i { color: blue } }
            }
            .a, .b { & u { color: blue } }
          </style>
          <div class=card id=c1><p class=title id=t>t</p><em id=e>e</em><span id=s>s</span><i id=i>i</i><b id=bb>b</b></div>
          <div class='card on' id=c2>x</div>
          <div class=x><div class=card id=c3>y</div></div>
          <div class=b><u id=u>u</u></div>");
        Assert.Equal(Red, ColorOf(doc, "c1"));
        Assert.Equal(Blue, ColorOf(doc, "t"));
        Assert.Equal(Blue, ColorOf(doc, "e"));
        Assert.Equal(Blue, ColorOf(doc, "c2"));
        Assert.Equal(Blue, ColorOf(doc, "bb"));
        Assert.Equal(Blue, ColorOf(doc, "c3"));
        Assert.Equal(Blue, ColorOf(doc, "s"));
        Assert.Equal(Red, ColorOf(doc, "i"));
        Assert.Equal(Blue, ColorOf(doc, "u"));
    }

    [Fact]
    public void Declarations_after_a_nested_rule_still_apply()
    {
        var doc = Styled(@"<style>
            div { color: red; p { color: blue } font-size: 20px }
          </style><div id=d><p id=p>p</p></div>");
        Assert.Equal(20 * 0.75f, ById(doc, "d").Style.FontSize, 3);
        Assert.Equal(Blue, ColorOf(doc, "p"));
    }

    // ---- @supports ----

    [Theory]
    [InlineData("(display: grid)", true)]
    [InlineData("(display: grid) and (gap: 1rem)", true)]
    [InlineData("(display: frobnicate)", false)]
    [InlineData("(made-up-property: 1)", false)]
    [InlineData("not (made-up-property: 1)", true)]
    [InlineData("(made-up: 1) or (color: oklch(50% 0.1 20))", true)]
    [InlineData("(color: rgb(from red r g b))", false)]
    [InlineData("selector(:has(a))", true)]
    [InlineData("selector(:frobnicate)", false)]
    [InlineData("((-webkit-hyphens:none)) and (not (margin-trim:inline))", false)]
    [InlineData("(--custom: anything)", true)]
    public void Supports_conditions(string condition, bool expected) => Assert.Equal(expected, CssSupports.Evaluate(condition));

    [Fact]
    public void Supports_blocks_apply_only_when_true()
    {
        var doc = Styled(@"<style>
            @supports (display: grid) { #a { color: red } }
            @supports (display: nonsense) { #b { color: red } }
            @supports not (display: nonsense) { #c { color: blue } }
          </style><p id=a>a</p><p id=b>b</p><p id=c>c</p>");
        Assert.Equal(Red, ColorOf(doc, "a"));
        Assert.Equal(Black, ColorOf(doc, "b"));
        Assert.Equal(Blue, ColorOf(doc, "c"));
    }

    // ---- @property ----

    [Fact]
    public void Registered_properties_have_initial_values_and_may_not_inherit()
    {
        var doc = Styled(@"<style>
            @property --accent { syntax: '<color>'; inherits: false; initial-value: red; }
            @property --ink { syntax: '<color>'; inherits: true; initial-value: blue; }
            @property --bad { syntax: '<color>'; initial-value: red; }
            #a { color: var(--accent) }
            #outer { --accent: blue; --ink: red }
            #b { color: var(--accent) }
            #c { color: var(--ink) }
            #d { color: var(--bad, blue) }
          </style><p id=a>a</p><div id=outer><p id=b>b</p><p id=c>c</p></div><p id=d>d</p>");
        Assert.Equal(Red, ColorOf(doc, "a"));
        Assert.Equal(Red, ColorOf(doc, "b")); // --accent does not inherit: b sees the initial red, not outer's blue
        Assert.Equal(Red, ColorOf(doc, "c")); // --ink inherits outer's red
        Assert.Equal(Blue, ColorOf(doc, "d")); // no "inherits": not registered
    }

    // ---- media queries ----

    [Theory]
    [InlineData("print", true)]
    [InlineData("screen", false)]
    [InlineData("not screen", true)]
    [InlineData("all and (min-width: 500px)", true)]
    [InlineData("(min-width: 800px)", false)]
    [InlineData("(max-width: 800px)", true)]
    [InlineData("(width >= 600px)", true)]
    [InlineData("(width < 600px)", false)]
    [InlineData("(600px <= width <= 700px)", true)]
    [InlineData("(400px < width < 600px)", false)]
    [InlineData("(orientation: portrait)", true)]
    [InlineData("(min-aspect-ratio: 1/1)", false)]
    [InlineData("(prefers-color-scheme: dark)", false)]
    [InlineData("(hover: hover)", false)]
    [InlineData("(hover: none)", true)]
    [InlineData("screen, print and (color)", true)]
    [InlineData("(min-width: 30em) and (max-width: 50em)", true)]
    [InlineData("(min-width: 1200px) or (orientation: portrait)", true)]
    [InlineData("not (min-width: 1200px)", true)]
    [InlineData("print and (min-resolution: 2dppx)", false)]
    [InlineData("(frobnicate: 1)", false)]
    public void Media_queries_evaluate_against_the_page_area(string query, bool expected)
    {
        // A4 with 1in margins: 523pt x 770pt, about 698 x 1026 CSS px.
        var context = new MediaContext { Width = 595.28f - 72, Height = 841.89f - 72 };
        Assert.Equal(expected, MediaQuery.Matches(query, context));
    }

    [Fact]
    public void Media_rules_follow_the_page_size_from_page_rules()
    {
        var html = @"<style>@page { size: A4 landscape; margin: 0 }
            @media (orientation: landscape) { p { color: red } }</style><p>x</p>";
        var pdf = PdfTestHelpers.ConvertUncompressed(html);
        Assert.Contains("1 0 0 rg", PdfTestHelpers.AllContent(pdf));
    }

    // ---- selectors 4 ----

    [Fact]
    public void Has_matches_relative_selectors()
    {
        var doc = Styled(@"<style>
            div:has(img) { color: red }
            section:has(> h2) { color: blue }
            h3:has(+ p) { color: red }
            h4:has(~ ul) { color: red }
            li:not(:has(a)) { color: blue }
            article:has(.note b) { color: red }
            .wrap:has(+ .next em) { color: red }
          </style>
          <div id=d1><span><img></span></div><div id=d2>no</div>
          <section id=s1><h2>t</h2></section><section id=s2><div><h2>deep</h2></div></section>
          <h3 id=h3a>x</h3><p>p</p><h3 id=h3b>y</h3><div>d</div>
          <h4 id=h4>z</h4><p>p</p><ul></ul>
          <ul><li id=l1><a>a</a></li><li id=l2>b</li></ul>
          <article id=art><div class=note><i><b>b</b></i></div></article>
          <div class=wrap id=w>w</div><div class=next><p><em>e</em></p></div>");
        Assert.Equal(Red, ColorOf(doc, "d1"));
        Assert.Equal(Black, ColorOf(doc, "d2"));
        Assert.Equal(Blue, ColorOf(doc, "s1"));
        Assert.Equal(Black, ColorOf(doc, "s2"));
        Assert.Equal(Red, ColorOf(doc, "h3a"));
        Assert.Equal(Black, ColorOf(doc, "h3b"));
        Assert.Equal(Red, ColorOf(doc, "h4"));
        Assert.Equal(Black, ColorOf(doc, "l1"));
        Assert.Equal(Blue, ColorOf(doc, "l2"));
        Assert.Equal(Red, ColorOf(doc, "art"));
        Assert.Equal(Red, ColorOf(doc, "w"));
    }

    [Fact]
    public void Has_cannot_nest_and_counts_like_is_for_specificity()
    {
        Assert.Null(Selector.Parse("a:has(b:has(c))"));
        Assert.Equal(Selector.Parse("a:is(#x)")!.Specificity, Selector.Parse("a:has(#x)")!.Specificity);
    }

    [Fact]
    public void Nth_child_of_selector_counts_matching_siblings()
    {
        var doc = Styled(@"<style>
            li:nth-child(2 of .on) { color: red }
            li:nth-last-child(1 of .on) { color: blue }
          </style>
          <ul><li id=a class=on>a</li><li id=b>b</li><li id=c class=on>c</li><li id=d class=on>d</li><li id=e>e</li></ul>");
        Assert.Equal(Black, ColorOf(doc, "a"));
        Assert.Equal(Black, ColorOf(doc, "b"));
        Assert.Equal(Red, ColorOf(doc, "c"));
        Assert.Equal(Blue, ColorOf(doc, "d"));
    }

    [Fact]
    public void Lang_and_form_state_pseudo_classes()
    {
        var doc = Styled(@"<style>
            :lang(da) { color: red }
            :lang(en) { color: blue }
            input[type]:checked { color: red }
            input:disabled { color: blue }
            input:enabled:required { color: red }
            #o:optional { color: blue }
            input:read-only { font-size: 20px }
            input:placeholder-shown { font-size: 10px }
            option:checked { color: red }
            [data-kind='Note' i] { color: red }
          </style>
          <div lang=da-DK><p id=da>x</p></div><p lang=en-GB id=en>y</p>
          <input id=c type=checkbox checked><input id=d disabled><input id=r required>
          <input id=o><input id=ro readonly value=x><input id=ph placeholder=type>
          <fieldset disabled><legend><input id=leg></legend><input id=fs></fieldset>
          <select><option id=opt selected>a</option></select>
          <p id=ci data-kind=NOTE>z</p>");
        Assert.Equal(Red, ColorOf(doc, "da"));
        Assert.Equal(Blue, ColorOf(doc, "en"));
        Assert.Equal(Red, ColorOf(doc, "c"));
        Assert.Equal(Blue, ColorOf(doc, "d"));
        Assert.Equal(Red, ColorOf(doc, "r"));
        Assert.Equal(Blue, ColorOf(doc, "o"));
        Assert.Equal(15f, ById(doc, "ro").Style.FontSize, 3);
        Assert.Equal(7.5f, ById(doc, "ph").Style.FontSize, 3);
        Assert.Equal(Blue, ColorOf(doc, "fs"));
        Assert.NotEqual(Blue, ColorOf(doc, "leg"));
        Assert.Equal(Red, ColorOf(doc, "opt"));
        Assert.Equal(Red, ColorOf(doc, "ci"));
    }
}

using System.Text;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Type-position and list pseudo-classes, object-fit, text-overflow, outline, text-shadow and aspect-ratio.</summary>
public class SelectorAndBoxEffectTests
{
    private const float Width = 500, PageHeight = 800;

    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Find(doc, "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
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

    private static List<DrawOp> Ops(string html)
    {
        var doc = Styled(html);
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false));
        engine.Layout(doc, Width);
        var list = new List<DrawOp>();
        void Add(IEnumerable<DrawOp> ops)
        {
            foreach (var o in ops)
            {
                list.Add(o);
                if (o is GroupOp g) Add(g.Ops);
            }
        }
        Add(engine.Ops);
        return list;
    }

    private static string PngUri(int size) => "data:image/png;base64," + Convert.ToBase64String(RgbaPng(size));

    // ---- selectors ----

    [Fact]
    public void Of_type_pseudo_classes_count_siblings_with_the_same_name()
    {
        var doc = Styled(@"<style>
            p:first-of-type { color: red }
            p:last-of-type { color: blue }
            span:only-of-type { color: red }
            i:nth-of-type(2n) { color: red }
            i:nth-last-of-type(1) { color: blue }
          </style>
          <div><h2 id=h>h</h2><p id=p1>1</p><span id=s>s</span><p id=p2>2</p><p id=p3>3</p>
          <i id=i1>a</i><b>x</b><i id=i2>b</i><i id=i3>c</i></div>");
        Assert.Equal(Red, ColorOf(doc, "p1"));
        Assert.Equal(Black, ColorOf(doc, "p2"));
        Assert.Equal(Blue, ColorOf(doc, "p3"));
        Assert.Equal(Red, ColorOf(doc, "s"));
        Assert.Equal(Black, ColorOf(doc, "h"));
        Assert.Equal(Black, ColorOf(doc, "i1"));
        Assert.Equal(Red, ColorOf(doc, "i2"));
        Assert.Equal(Blue, ColorOf(doc, "i3"));
    }

    [Fact]
    public void Is_matches_any_selector_in_its_list()
    {
        var doc = Styled(@"<style>:is(h1, h2, .lead) > b { color: red } article :is(header, footer) i { color: blue }</style>
          <h2><b id=a>a</b></h2><p class=lead><b id=b>b</b></p><p><b id=c>c</b></p>
          <article><footer><span><i id=d>d</i></span></footer></article><footer><i id=e>e</i></footer>");
        Assert.Equal(Red, ColorOf(doc, "a"));
        Assert.Equal(Red, ColorOf(doc, "b"));
        Assert.Equal(Black, ColorOf(doc, "c"));
        Assert.Equal(Blue, ColorOf(doc, "d"));
        Assert.Equal(Black, ColorOf(doc, "e"));
    }

    [Fact]
    public void Is_takes_its_most_specific_argument_and_where_counts_nothing()
    {
        var doc = Styled(@"<style>
            :is(#x, p) { color: red }
            p.t { color: blue }
            :where(#y) .u { color: red }
            .u { color: blue }
          </style><p id=x class=t>a</p><div id=y><span id=u class=u>b</span></div>");
        // :is(#x, p) has an id's specificity even when it matched through p: it beats p.t.
        Assert.Equal(Red, ColorOf(doc, "x"));
        // :where() adds nothing, so the later .u wins.
        Assert.Equal(Blue, ColorOf(doc, "u"));
    }

    [Fact]
    public void Is_and_where_forgive_invalid_selectors_but_not_does_not()
    {
        var doc = Styled(@"<style>
            :is(p, ::before, :frobnicate) { color: red }
            :where(:frobnicate, i) { color: blue }
            b:not(.a, :frobnicate) { color: red }
          </style><p id=p>a</p><i id=i>b</i><b id=b>c</b>");
        Assert.Equal(Red, ColorOf(doc, "p"));
        Assert.Equal(Blue, ColorOf(doc, "i"));
        Assert.Equal(Black, ColorOf(doc, "b"));
    }

    [Fact]
    public void Not_takes_a_list_of_complex_selectors()
    {
        var doc = Styled(@"<style>li:not(.a, .b) { color: red } span:not(nav span) { color: blue }</style>
          <ul><li id=a class=a>a</li><li id=b class=b>b</li><li id=c>c</li></ul><nav><span id=s1>x</span></nav><span id=s2>y</span>");
        Assert.Equal(Black, ColorOf(doc, "a"));
        Assert.Equal(Black, ColorOf(doc, "b"));
        Assert.Equal(Red, ColorOf(doc, "c"));
        Assert.Equal(Black, ColorOf(doc, "s1"));
        Assert.Equal(Blue, ColorOf(doc, "s2"));
    }

    [Fact]
    public async Task Nested_is_selectors_over_deep_markup_stay_fast()
    {
        var sel = new StringBuilder();
        for (int i = 0; i < 60; i++) sel.Append(":is(div div div div div div div div, div div span) ");
        sel.Append("p");
        var html = "<style>" + sel + "{color:red}</style>" + string.Concat(Enumerable.Repeat("<div>", 250)) + "<p>x</p>"
                   + string.Concat(Enumerable.Repeat("<p>y</p>", 200));
        var task = Task.Run(() => HtmlToPdf.Convert(html));
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(finished == task, "Did not finish in time");
        AssertStructurallyValid(await task);
    }

    [Fact]
    public void Nested_lists_take_full_complex_selectors()
    {
        // Nine compounds inside :is(), and complex selectors two and three lists deep.
        var doc = Styled(@"<style>
            :is(.a .b .c .d .e .f .g .h .i) em { color: red }
            p:not(:is(section > div .x, aside .y)) { color: blue }
            :where(:is(main :not(nav .q) > b, .z)) i { color: red }
            li:has(> span :is(.k ~ .m b)) { color: blue }
          </style>
          <div class=a><div class=b><div class=c><div class=d><div class=e><div class=f><div class=g><div class=h><div class=i>
            <em id=e1>x</em></div></div></div></div></div></div></div></div></div>
          <section><div><p id=p1 class=x>a</p></div></section><aside><p id=p2 class=y>b</p></aside><p id=p3 class=x>c</p>
          <main><div><b><i id=i1>x</i></b></div><nav><div class=q><b><i id=i2>y</i></b></div></nav></main>
          <ul><li id=l1><span><s class=k></s><s class=m><b>z</b></s></span></li><li id=l2><span><s class=m><b>z</b></s></span></li></ul>");
        Assert.Equal(Red, ColorOf(doc, "e1"));
        Assert.Equal(Black, ColorOf(doc, "p1"));
        Assert.Equal(Black, ColorOf(doc, "p2"));
        Assert.Equal(Blue, ColorOf(doc, "p3"));
        Assert.Equal(Red, ColorOf(doc, "i1"));
        Assert.Equal(Black, ColorOf(doc, "i2"));
        Assert.Equal(Blue, ColorOf(doc, "l1"));
        Assert.Equal(Black, ColorOf(doc, "l2"));
    }

    [Fact]
    public void Escapes_in_selectors_are_resolved()
    {
        // Acid2 matches class="second two" with [class=second\ two]; \31 is a hex escape for "1", ended by one space.
        var doc = Styled(@"<style>
            [class=second\ two][class=""second two""] { color: red }
            .\31 0 { color: blue }
            #a\:b { color: red }
          </style>
          <p id=p1 class='second two'>x</p><p id=p2 class=10>y</p><p id=a:b>z</p>");
        Assert.Equal(Red, ColorOf(doc, "p1"));
        Assert.Equal(Blue, ColorOf(doc, "p2"));
        Assert.Equal(Red, ColorOf(doc, "a:b"));
    }

    [Fact]
    public void Nested_lists_keep_parse_limits()
    {
        Assert.NotNull(Selector.Parse(":is(" + string.Join(" ", Enumerable.Repeat("a", 20)) + ")"));
        Assert.NotNull(Selector.Parse(":is(a :not(b :is(c d > e)))"));
        Assert.NotNull(Selector.Parse(":is(a, :has(> b c ~ d e))"));
        // Very deep nesting is dropped rather than recursed into.
        Assert.NotNull(Selector.Parse(string.Concat(Enumerable.Repeat(":not(a ", 15)) + "b" + new string(')', 15)));
        Assert.Null(Selector.Parse(string.Concat(Enumerable.Repeat(":not(a ", 40)) + "b" + new string(')', 40)));
    }

    [Fact]
    public async Task Deeply_nested_complex_lists_over_deep_markup_stay_fast()
    {
        // Each level holds a complex selector and the next level: matched naively, every level multiplies the work by the
        // depth of the markup.
        var sel = "div div p";
        for (int i = 0; i < 12; i++) sel = $":is(div {sel}, div div div span) div";
        var html = "<style>" + sel + " p, :not(" + sel + ") i {color:red} div:has(div div :is(" + sel + ")) {color:blue}</style>"
                   + string.Concat(Enumerable.Repeat("<div>", 200)) + string.Concat(Enumerable.Repeat("<p>x</p><i>y</i>", 200));
        var task = Task.Run(() => HtmlToPdf.Convert(html));
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.True(finished == task, "Did not finish in time");
        AssertStructurallyValid(await task);
    }

    // ---- properties ----

    [Fact]
    public void Box_effect_properties_parse()
    {
        var doc = Styled(@"<div id=a style='outline: 2px dashed #f00; outline-offset: 3px; text-shadow: 1px 2px 3px blue, 0 0 red;
            object-fit: cover; object-position: right 10px top; text-overflow: ellipsis; aspect-ratio: 16 / 9'>
            <p id=b style='aspect-ratio: auto 2'>t</p></div>");
        var s = ById(doc, "a").Style;
        Assert.Equal(BorderStyle.Dashed, s.OutlineStyle);
        Assert.Equal(1.5f, s.OutlineWidth);
        Assert.Equal(2.25f, s.OutlineOffset);
        Assert.Equal(0xFFFF0000u, s.OutlineColor);
        Assert.Equal(2, s.TextShadows!.Length);
        Assert.Equal(Blue, s.TextShadows[0].Color);
        Assert.Equal(ObjectFit.Cover, s.ObjectFit);
        Assert.True(s.ObjectPositionX.FromEnd);
        Assert.True(s.TextOverflowEllipsis);
        Assert.Equal(16f / 9, s.AspectRatio, 0.001f);
        Assert.False(s.AspectRatioAuto);
        // text-shadow is inherited; the others are not.
        var b = ById(doc, "b").Style;
        Assert.NotNull(b.TextShadows);
        Assert.Equal(BorderStyle.None, b.OutlineStyle);
        Assert.Equal(2f, b.AspectRatio);
        Assert.True(b.AspectRatioAuto);
    }

    [Fact]
    public void Object_fit_cover_fills_the_box_and_clips_the_rest()
    {
        // A 40 x 40 px image in a 100 x 50 pt box: scaled to 100 x 100 pt and centred, so 25 pt hang over each edge.
        var ops = Ops($"<img src='{PngUri(40)}' style='display:block; width:100pt; height:50pt; object-fit:cover'>");
        var clip = Assert.Single(ops.OfType<ClipOp>());
        Assert.Equal(100f, clip.W, 0.01f);
        Assert.Equal(50f, clip.H, 0.01f);
        var img = Assert.Single(clip.Ops.OfType<ImageOp>());
        Assert.Equal(100f, img.W, 0.01f);
        Assert.Equal(100f, img.H, 0.01f);
        Assert.Equal(clip.Y - 25, img.Y, 0.01f);
    }

    [Fact]
    public void Object_fit_contain_and_position_place_the_image_inside()
    {
        var ops = Ops($"<p><img src='{PngUri(40)}' style='width:100pt; height:50pt; object-fit:contain; object-position:left'></p>");
        Assert.Empty(ops.OfType<ClipOp>());
        var img = Assert.Single(ops.OfType<ImageOp>());
        Assert.Equal(50f, img.W, 0.01f);
        Assert.Equal(50f, img.H, 0.01f);
        Assert.Equal(0f, img.X, 0.01f);
    }

    [Fact]
    public void Text_overflow_ellipsis_cuts_the_line_to_fit()
    {
        var ops = Ops("<div style='width:80pt; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; font: 10pt Helvetica'>" +
                      "A rather long line of text that does not fit</div>");
        var text = string.Concat(ops.OfType<TextOp>().Select(t => t.Text));
        Assert.EndsWith("…", text);
        Assert.StartsWith("A rather", text);
        Assert.True(text.Length < 30, text);
        var last = ops.OfType<TextOp>().Last();
        Assert.True(last.X + last.Font.Measure(last.Text, last.Size) <= 80.01f);
    }

    [Fact]
    public void Text_overflow_needs_a_clipping_box()
    {
        var ops = Ops("<div style='width:80pt; white-space:nowrap; text-overflow:ellipsis'>A rather long line of text that does not fit</div>");
        Assert.DoesNotContain("…", string.Concat(ops.OfType<TextOp>().Select(t => t.Text)));
    }

    [Fact]
    public void Outline_is_drawn_outside_the_border_box_without_moving_it()
    {
        var ops = Ops("<div style='margin:20pt; width:100pt; height:40pt; outline: 3pt solid #00f; outline-offset: 3pt'></div>");
        var ring = Assert.Single(ops.OfType<BorderRectOp>());
        Assert.Equal(Blue, ring.Color);
        Assert.Equal(20f - 6, ring.X, 0.1f);
        Assert.Equal(112f, ring.W, 0.1f);
        Assert.Equal(3f, ring.Width, 0.1f);
    }

    [Fact]
    public void Outline_follows_rounded_corners()
    {
        var ops = Ops("<div style='width:100pt; height:40pt; border-radius:6pt; outline: 3pt solid red'></div>");
        var ring = Assert.Single(ops.OfType<RoundRectOp>());
        Assert.True(ring.HasHole);
        Assert.Equal(9f, ring.R.TLx, 0.1f);
        Assert.Equal(-3f, ring.X, 0.1f);
    }

    [Fact]
    public void Dashed_outline_sides_start_and_end_on_a_dash_like_chrome()
    {
        // 6pt (8px) dashes are two widths long with gaps of about one, spread so that each side ends on a dash.
        var ops = Ops("<div style='margin:20pt; width:75pt; height:30pt; outline: 6pt dashed #00f'></div>");
        var lines = ops.OfType<LineOp>().ToList();
        Assert.Equal(4, lines.Count);
        var top = lines[0];
        Assert.Equal(12f, top.DashLength, 0.01f);
        float length = top.X2 - top.X1, dashes = MathF.Round((length + top.DashGap) / (top.DashLength + top.DashGap));
        Assert.Equal(length, dashes * top.DashLength + (dashes - 1) * top.DashGap, 0.01f);
        Assert.InRange(top.DashGap, 4.5f, 9f);
    }

    [Fact]
    public void Dotted_outline_dots_sit_in_the_corners()
    {
        var ops = Ops("<div style='margin:20pt; width:75pt; height:30pt; outline: 6pt dotted #00f'></div>");
        var top = ops.OfType<LineOp>().First();
        // From the centre of the first corner's dot to the centre of the last one, a whole number of steps.
        Assert.Equal(20f - 6 + 3, top.X1, 0.01f);
        Assert.Equal(20f + 75 + 6 - 3, top.X2, 0.01f);
        float steps = (top.X2 - top.X1) / top.DashGap;
        Assert.Equal(MathF.Round(steps), steps, 0.01f);
    }

    [Fact]
    public void Translucent_dashed_outline_paints_each_corner_once()
    {
        var ops = Ops("<div style='margin:20pt; width:75pt; height:30pt; outline: 6pt dashed rgba(0,0,255,.5)'></div>");
        var lines = ops.OfType<LineOp>().ToList();
        var left = Assert.Single(lines, l => l.X1 == l.X2 && l.X1 < 20);
        // The vertical sides leave the corner squares to the horizontal ones and keep their pattern where it was.
        Assert.Equal(20f - 6 + 6, left.Y1 - 0, 0.01f);
        Assert.Equal(6f, left.DashPhase, 0.01f);
        Assert.All(lines.Where(l => l.Y1 == l.Y2), l => Assert.Equal(0f, l.DashPhase));
    }

    [Fact]
    public void Outline_in_a_3d_style_has_mitred_shaded_sides()
    {
        var ops = Ops("<div style='width:100pt; height:40pt; outline: 6pt inset #4a90d9'></div>");
        var sides = ops.OfType<RoundRectOp>().ToList();
        Assert.Equal(4, sides.Count);
        Assert.All(sides, s => Assert.NotNull(s.Clip));
        Assert.Equal(2, sides.Select(s => s.Color).Distinct().Count());
    }

    [Fact]
    public void Rounded_outline_in_a_3d_style_keeps_its_shades()
    {
        var ops = Ops("<div style='width:100pt; height:40pt; border-radius:12pt; outline: 6pt groove #4a90d9'></div>");
        var rings = ops.OfType<RoundRectOp>().ToList();
        Assert.Equal(8, rings.Count); // two halves of each side
        Assert.True(rings.Select(s => s.Color).Distinct().Count() >= 2);
    }

    [Fact]
    public void Outline_style_auto_is_a_thin_focus_ring_whatever_its_width()
    {
        foreach (var width in new[] { "1px", "8px" })
        {
            var ops = Ops($"<div style='margin:20pt; width:100pt; height:40pt; outline: {width} auto #00f'></div>");
            var ring = Assert.Single(ops.OfType<RoundRectOp>());
            // Two pixels wide, centred on the border edge, with rounded corners.
            Assert.Equal(20f - 0.75f, ring.X, 0.01f);
            Assert.Equal(20f + 0.75f, ring.HoleX, 0.01f);
            Assert.Equal(3f, ring.R.TLx, 0.01f);
        }
        var solid = Ops("<div style='height:20pt; outline: 8px auto #00f; outline-style: dashed'></div>");
        Assert.Equal(4, solid.OfType<LineOp>().Count());
    }

    [Fact]
    public void Wrapped_inline_outline_is_one_shape_around_all_its_lines()
    {
        var ops = Ops("<p style='font:12pt Arial; line-height:1.2'><span style='outline:3pt solid #00f'>Alpha<br>Be<br>Gamma ray</span></p>");
        var path = Assert.Single(ops.OfType<PathOp>());
        Assert.Empty(ops.OfType<BorderRectOp>());
        Assert.Equal(3f, path.StrokeWidth);
        // The lines touch once grown by the outline, so their union has one edge: in at the short line and out again.
        var loop = Assert.Single(path.Loops);
        Assert.Equal(8, loop.Length / 2);
        Assert.Null(path.Radii);
    }

    [Fact]
    public void Wrapped_inline_outline_stays_in_pieces_when_its_lines_are_apart()
    {
        var ops = Ops("<p style='font:12pt Arial; line-height:3'><span style='outline:3pt solid #00f; border-radius:4pt'>Alpha<br>Be<br>Gamma ray</span></p>");
        var paths = ops.OfType<PathOp>().ToList();
        Assert.Equal(3, paths.Count);
        Assert.All(paths, p => Assert.Equal(4, Assert.Single(p.Loops).Length / 2));
        // Rounded like the box, along the middle of the outline.
        Assert.All(paths, p => Assert.Equal(4f + 1.5f, p.Radii![0][0], 0.01f));
    }

    [Fact]
    public void Wrapped_inline_outline_in_a_3d_style_shades_each_edge_by_the_side_it_faces()
    {
        var ops = Ops("<p style='font:12pt Arial; line-height:1.2'><span style='outline:6pt inset #4a90d9'>Alpha<br>Be<br>Gamma ray</span></p>");
        var edges = ops.OfType<PathOp>().ToList();
        Assert.Equal(8, edges.Count);
        Assert.All(edges, e => Assert.NotNull(e.Clip));
        Assert.Equal(2, edges.Select(e => e.Color).Distinct().Count());
    }

    [Fact]
    public void Wrapped_inline_dashed_outline_fits_dashes_to_each_edge()
    {
        var ops = Ops("<p style='font:12pt Arial; line-height:1.2'><span style='outline:3pt dashed #00f'>Alpha<br>Be<br>Gamma ray</span></p>");
        Assert.Empty(ops.OfType<PathOp>());
        Assert.Equal(8, ops.OfType<LineOp>().Count());
    }

    [Fact]
    public void Text_shadow_paints_a_copy_under_the_text()
    {
        var ops = Ops("<p style='color:#000; text-shadow: 2pt 3pt #f00'>Shadow</p>");
        var texts = ops.OfType<TextOp>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(Red, texts[0].Color);
        Assert.Equal(texts[1].X + 2, texts[0].X, 0.01f);
        Assert.Equal(texts[1].Baseline + 3, texts[0].Baseline, 0.01f);
    }

    /// <summary>The ops without the contents of text-shadow layers, which are drawn only as the shadows' pixels.</summary>
    private static List<DrawOp> Unshadowed(List<DrawOp> ops)
    {
        var inside = ops.OfType<LayerOp>().Where(l => l.TextShadow).SelectMany(l => l.Ops).ToHashSet();
        return ops.Where(o => !inside.Contains(o)).ToList();
    }

    [Fact]
    public void Blurred_text_shadows_are_one_layer_of_pixels_per_run()
    {
        // Two blurred shadows and a sharp one in between: the sharp one stays text, each blurred one is a layer whose
        // filter blurs the text's alpha by half the blur radius.
        var ops = Unshadowed(Ops("<p style='color:#000; text-shadow: 1pt 1pt 4pt red, 2pt 2pt blue, 0 0 10pt rgba(0,0,0,0.5)'>Glow</p>"));
        var layers = ops.OfType<LayerOp>().ToList();
        Assert.Equal(2, layers.Count);
        Assert.All(layers, l => Assert.True(l.TextShadow && l.RasterReplaces && l.Raster!.TextShadows));
        Assert.Equal(5f, layers[0].Raster!.Primitives[0].Sx, 0.01f); // the last shadow is painted first
        Assert.Equal(2f, layers[1].Raster!.Primitives[0].Sx, 0.01f);
        Assert.Equal(Red, layers[1].Raster!.Primitives[2].Color);
        // The layer's text is opaque, so a transparent text still casts its shadow.
        Assert.Equal(Black, Assert.IsType<TextOp>(Assert.Single(layers[0].Ops)).Color);
        var texts = ops.OfType<TextOp>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal(Blue, texts[0].Color);
        Assert.True(ops.IndexOf(layers[0]) < ops.IndexOf(texts[0]) && ops.IndexOf(texts[0]) < ops.IndexOf(layers[1]));
    }

    [Fact]
    public void Text_shadows_include_the_decorations()
    {
        var ops = Unshadowed(Ops("<p style='color:#000; text-decoration: underline line-through; text-shadow: 2pt 3pt #f00, 0 0 4pt blue'>Shadow</p>"));
        var rects = ops.OfType<RectOp>().Where(r => r.IsDecoration).ToList();
        // The sharp shadow's two lines in red, offset, then the text's own two lines in black.
        Assert.Equal(4, rects.Count);
        Assert.Equal(Red, rects[0].Color);
        Assert.Equal(Black, rects[2].Color);
        Assert.Equal(rects[2].X + 2, rects[0].X, 0.01f);
        Assert.Equal(rects[2].Y + 3, rects[0].Y, 0.01f);
        // The blurred shadow's layer holds the run and both lines.
        var layer = Assert.Single(ops.OfType<LayerOp>());
        Assert.Equal(2, layer.Ops.OfType<RectOp>().Count());
    }

    [Fact]
    public void Aspect_ratio_sizes_the_height_from_the_width()
    {
        var ops = Ops("<div style='width:200pt; aspect-ratio: 2 / 1; background:#f00'></div>" +
                      "<div style='width:100pt; aspect-ratio: 1; padding:10pt; box-sizing:border-box; background:#00f'></div>" +
                      "<div style='height:30pt; aspect-ratio: 3; background:#0f0'></div>");
        var rects = ops.OfType<RectOp>().ToList();
        Assert.Equal(100f, rects[0].H, 0.1f);
        Assert.Equal(100f, rects[1].H, 0.1f);
        Assert.Equal(90f, rects[2].W, 0.1f);
    }

    [Fact]
    public void Aspect_ratio_lets_content_grow_the_box()
    {
        var ops = Ops("<div style='width:100pt; aspect-ratio: 10; background:#f00'><div style='height:50pt'></div></div>");
        Assert.Equal(50f, ops.OfType<RectOp>().First().H, 0.1f);
    }

    [Fact]
    public void Aspect_ratio_overrides_an_image_ratio()
    {
        var ops = Ops($"<p><img src='{PngUri(40)}' style='width:90pt; aspect-ratio: 3 / 1'></p>");
        var img = Assert.Single(ops.OfType<ImageOp>());
        Assert.Equal(30f, img.H, 0.01f);
    }

    [Fact]
    public void Effects_render_to_a_valid_pdf()
    {
        var pdf = ConvertUncompressed($@"<style>li:nth-of-type(odd) {{ background: #eee }} :is(h1, h2) {{ text-shadow: 1px 1px 2px #999 }}</style>
            <h1>Title</h1><ul><li>a</li><li>b</li><li>c</li></ul>
            <img src='{PngUri(40)}' style='width:80pt; height:40pt; object-fit:cover; border-radius:6pt; outline:1px dotted #333'>
            <div style='width:60pt; overflow:hidden; white-space:nowrap; text-overflow:ellipsis'>Truncated text here</div>");
        AssertStructurallyValid(pdf);
    }
}

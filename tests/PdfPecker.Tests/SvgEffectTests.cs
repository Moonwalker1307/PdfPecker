using System.Text.RegularExpressions;
using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Svg;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>SVG markers, patterns, masks and filters; clipping in table cells; double borders on rounded boxes.</summary>
public class SvgEffectTests
{
    private static Element Styled(string html)
    {
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc;
    }

    private static Element ById(Element root, string id) => Descendants(root).First(e => e.Id == id);

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static PdfOptions NoMargins => new() { Margins = new PageMargins(0), Compress = false };

    /// <summary>The RGB of a pixel of the first page rendered at 72 dpi (1 px = 1 pt).</summary>
    private static (int R, int G, int B) Pixel(byte[] pdf, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static bool IsRed((int R, int G, int B) p) => p.R > 220 && p.G < 40 && p.B < 40;
    private static bool IsBlue((int R, int G, int B) p) => p.R < 40 && p.G < 40 && p.B > 220;
    private static bool IsWhite((int R, int G, int B) p) => p.R > 235 && p.G > 235 && p.B > 235;

    // An SVG of 400 × 400 px drawn in 300 × 300 pt: user units are 0.75 pt.
    private static byte[] Svg(string content)
        => HtmlToPdf.Convert($"<body style='margin:0'><svg width='400' height='400' style='display:block'>{content}</svg></body>", NoMargins);

    private static (int, int, int) At(byte[] pdf, float x, float y) => Pixel(pdf, (int)(x * 0.75f), (int)(y * 0.75f));

    // ---- markers ----

    [Fact]
    public void Markers_are_drawn_at_the_vertices_of_a_polyline()
    {
        var doc = Styled("""
            <svg id="s" width="200" height="200">
              <marker id="m" markerWidth="4" markerHeight="4" refX="2" refY="2" markerUnits="userSpaceOnUse">
                <rect width="4" height="4" fill="red"/>
              </marker>
              <polyline points="10,10 50,10 50,50" fill="none" stroke="black" marker-start="url(#m)" marker-mid="url(#m)" marker-end="url(#m)"/>
            </svg>
            """);
        var shapes = SvgImage.Build(ById(doc, "s")).Shapes;
        var markers = shapes.Where(s => s.Kind == SvgShape.KindPath && s.Fill == 0xFFFF0000u).ToList();
        Assert.Equal(3, markers.Count);
        float[][] centres = { new[] { 10f, 10f }, new[] { 50f, 10f }, new[] { 50f, 50f } };
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(centres[i][0], (markers[i].MinX + markers[i].MaxX) / 2, 3);
            Assert.Equal(centres[i][1], (markers[i].MinY + markers[i].MaxY) / 2, 3);
        }
        // Each marker is clipped to its viewport.
        Assert.Equal(3, shapes.Count(s => s.Kind == SvgShape.KindClip));
    }

    [Fact]
    public void Auto_oriented_markers_follow_the_path_and_arcs_are_one_command()
    {
        var doc = Styled("""
            <svg id="s" width="200" height="200">
              <marker id="m" markerWidth="10" markerHeight="2" refX="0" refY="1" orient="auto" markerUnits="userSpaceOnUse" overflow="visible">
                <rect width="10" height="2" fill="red"/>
              </marker>
              <path d="M10 100 L10 50 A 20 20 0 0 1 50 50" fill="none" stroke="black" marker-start="url(#m)" marker-mid="url(#m)" marker-end="url(#m)"/>
            </svg>
            """);
        var shapes = SvgImage.Build(ById(doc, "s")).Shapes;
        Assert.DoesNotContain(shapes, s => s.Kind == SvgShape.KindClip); // overflow="visible"
        var markers = shapes.Where(s => s.Fill == 0xFFFF0000u).ToList();
        Assert.Equal(3, markers.Count); // start, the joint, and the end: the arc's curves add no vertices
        // The start marker points up (the first segment goes up): it is 10 long along -y.
        Assert.Equal(2, markers[0].MaxX - markers[0].MinX, 2);
        Assert.Equal(10, markers[0].MaxY - markers[0].MinY, 2);
        Assert.Equal(90, markers[0].MinY, 2);
        // The end marker points down, where the arc arrives.
        Assert.Equal(50, markers[2].MinY, 2);
        Assert.Equal(60, markers[2].MaxY, 2);
    }

    [Fact]
    public void Markers_scale_with_the_stroke_width_and_reverse_at_the_start()
    {
        var doc = Styled("""
            <svg id="s" width="200" height="200">
              <marker id="m" viewBox="0 0 10 10" markerWidth="3" markerHeight="3" refX="0" refY="5" orient="auto-start-reverse">
                <rect width="10" height="10" fill="red"/>
              </marker>
              <line x1="50" y1="50" x2="150" y2="50" stroke="black" stroke-width="4" marker-start="url(#m)" marker-end="url(#m)"/>
            </svg>
            """);
        var markers = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Fill == 0xFFFF0000u).ToList();
        Assert.Equal(2, markers.Count);
        // 3 × stroke width 4 = 12 units; the end marker reaches forward from the end, the start marker backward.
        Assert.Equal(150, markers[1].MinX, 2);
        Assert.Equal(162, markers[1].MaxX, 2);
        Assert.Equal(38, markers[0].MinX, 2);
        Assert.Equal(50, markers[0].MaxX, 2);
    }

    // ---- patterns ----

    [Fact]
    public void Pattern_fills_are_tiled()
    {
        var pdf = Svg("""
            <pattern id="p" width="40" height="40" patternUnits="userSpaceOnUse">
              <rect width="20" height="20" fill="red"/><rect x="20" y="20" width="20" height="20" fill="blue"/>
            </pattern>
            <rect width="400" height="400" fill="url(#p)"/>
            """);
        Assert.Contains("/PatternType 1", Latin1(pdf));
        Assert.True(IsRed(At(pdf, 10, 10)), "first tile, red");
        Assert.True(IsBlue(At(pdf, 30, 30)), "first tile, blue");
        Assert.True(IsWhite(At(pdf, 30, 10)), "first tile, empty");
        Assert.True(IsRed(At(pdf, 210, 290)), "a later tile");
        Assert.True(IsBlue(At(pdf, 350, 390)), "a later tile");
    }

    [Fact]
    public void Pattern_in_bounding_box_units_starts_at_the_shape()
    {
        var pdf = Svg("""
            <pattern id="p" width=".5" height=".5" patternContentUnits="objectBoundingBox">
              <rect width=".25" height=".25" fill="red"/>
            </pattern>
            <rect x="100" y="100" width="200" height="200" fill="url(#p)" stroke="none"/>
            """);
        Assert.True(IsRed(At(pdf, 120, 120)));
        Assert.True(IsWhite(At(pdf, 180, 120)));
        Assert.True(IsRed(At(pdf, 220, 220)));
        Assert.True(IsWhite(At(pdf, 90, 90)));
    }

    [Fact]
    public void Pattern_strokes_and_text_in_tiles_are_written()
    {
        var pdf = Svg("""
            <pattern id="p" width="20" height="20" patternUnits="userSpaceOnUse"><rect width="20" height="20" fill="red"/>
              <text x="2" y="15" font-size="12" font-family="Courier">x</text></pattern>
            <rect x="20" y="20" width="200" height="200" fill="none" stroke="url(#p)" stroke-width="20"/>
            """);
        AssertStructurallyValid(pdf);
        Assert.Contains("/Pattern CS /T", Latin1(pdf));
        Assert.Contains("Courier", Latin1(pdf)); // the tile's font is in the resources
        Assert.True(IsRed(At(pdf, 120, 25)));
        Assert.True(IsWhite(At(pdf, 120, 120)));
    }

    [Fact]
    public void Self_referencing_patterns_stop()
    {
        var pdf = Svg("""
            <pattern id="p" width="20" height="20" patternUnits="userSpaceOnUse"><rect width="20" height="20" fill="url(#p)"/></pattern>
            <rect width="100" height="100" fill="url(#p)"/>
            """);
        AssertStructurallyValid(pdf);
    }

    // ---- masks ----

    [Fact]
    public void Luminance_mask_shows_content_where_the_mask_is_white()
    {
        var pdf = Svg("""
            <mask id="m"><rect width="200" height="400" fill="white"/><rect x="200" width="200" height="400" fill="black"/></mask>
            <rect width="400" height="400" fill="red" mask="url(#m)"/>
            """);
        Assert.Contains("/S /Luminosity", Latin1(pdf));
        Assert.True(IsRed(At(pdf, 100, 200)));
        Assert.True(IsWhite(At(pdf, 300, 200)));
    }

    [Fact]
    public void Alpha_mask_uses_the_mask_content_alpha()
    {
        var pdf = Svg("""
            <mask id="m" mask-type="alpha"><rect width="200" height="400" fill="black"/></mask>
            <rect width="400" height="400" fill="red" mask="url(#m)"/>
            """);
        Assert.Contains("/S /Alpha", Latin1(pdf));
        Assert.True(IsRed(At(pdf, 100, 200)));
        Assert.True(IsWhite(At(pdf, 300, 200)));
    }

    [Fact]
    public void Mask_region_defaults_to_the_bounding_box_with_ten_percent_margins()
    {
        // The mask content covers everything, but the region is the shape's box grown by 10%.
        var pdf = Svg("""
            <mask id="m"><rect width="400" height="400" fill="white"/></mask>
            <g mask="url(#m)"><rect x="100" y="100" width="100" height="100" fill="red"/>
              <rect x="0" y="0" width="400" height="20" fill="blue"/></g>
            """);
        // The group's box spans y 0..200 and x 0..400, so the region is y -20..220: everything here shows.
        Assert.True(IsRed(At(pdf, 150, 150)));
        Assert.True(IsBlue(At(pdf, 200, 10)));
        var clipped = Svg("""
            <mask id="m"><rect width="400" height="400" fill="white"/></mask>
            <rect x="100" y="100" width="100" height="100" fill="red" mask="url(#m)"/>
            <rect x="0" y="0" width="1" height="1" fill="blue"/>
            """);
        Assert.True(IsRed(At(clipped, 150, 150)));
    }

    [Fact]
    public void Mask_with_bounding_box_content_units_and_gradient()
    {
        var pdf = Svg("""
            <linearGradient id="g"><stop offset="0" stop-color="white"/><stop offset="1" stop-color="black"/></linearGradient>
            <mask id="m" maskContentUnits="objectBoundingBox"><rect width="1" height="1" fill="url(#g)"/></mask>
            <rect x="0" y="0" width="400" height="400" fill="red" mask="url(#m)"/>
            """);
        var left = At(pdf, 20, 200);
        var mid = At(pdf, 200, 200);
        var right = At(pdf, 385, 200);
        Assert.True(left.Item2 < 60, $"left {left}");
        Assert.True(mid.Item2 > 80 && mid.Item2 < 200, $"middle {mid}");
        Assert.True(right.Item2 > 220, $"right {right}");
    }

    [Fact]
    public void Empty_mask_hides_the_content()
    {
        var pdf = Svg("""<mask id="m"></mask><rect width="400" height="400" fill="red" mask="url(#m)"/>""");
        Assert.True(IsWhite(At(pdf, 200, 200)));
    }

    // ---- filters ----

    [Fact]
    public void Gaussian_blur_softens_edges_and_is_drawn_as_an_image()
    {
        var pdf = Svg("""
            <filter id="f"><feGaussianBlur stdDeviation="10"/></filter>
            <rect x="100" y="100" width="200" height="200" fill="#0000ff" filter="url(#f)"/>
            """);
        var text = Latin1(pdf);
        Assert.Contains("/Xr1 Do", text);
        Assert.Contains("/SMask", text);
        Assert.True(IsBlue(At(pdf, 200, 200)), "the middle stays solid");
        var edge = At(pdf, 100, 200);
        Assert.True(edge.Item1 > 80 && edge.Item1 < 200, $"the edge is half covered: {edge}");
        var outside = At(pdf, 85, 200);
        Assert.True(outside.Item1 < 250, $"the blur reaches outside: {outside}");
        Assert.True(IsWhite(At(pdf, 50, 200)), "beyond the filter region nothing is drawn");
    }

    [Fact]
    public void Drop_shadow_is_offset_under_the_shape()
    {
        var pdf = Svg("""
            <filter id="f" x="0" y="0" width="200%" height="200%"><feDropShadow dx="40" dy="40" stdDeviation="0" flood-color="#00f"/></filter>
            <rect x="50" y="50" width="100" height="100" fill="red" filter="url(#f)"/>
            """);
        Assert.True(IsRed(At(pdf, 100, 100)));
        Assert.True(IsBlue(At(pdf, 170, 170)));
        Assert.True(IsWhite(At(pdf, 170, 70)));
    }

    [Fact]
    public void Offset_flood_composite_and_merge_build_a_shadow()
    {
        var pdf = Svg("""
            <filter id="f" x="0" y="0" width="2" height="2">
              <feOffset in="SourceAlpha" dx="50" dy="0" result="o"/>
              <feFlood flood-color="#0000ff"/><feComposite in2="o" operator="in"/>
              <feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge>
            </filter>
            <rect x="50" y="50" width="100" height="100" fill="red" filter="url(#f)"/>
            """);
        Assert.True(IsRed(At(pdf, 100, 100)));
        Assert.True(IsBlue(At(pdf, 175, 100)));
        Assert.True(IsWhite(At(pdf, 100, 175)));
    }

    [Fact]
    public void Color_matrix_and_component_transfer_change_colours()
    {
        var pdf = Svg("""
            <filter id="gray"><feColorMatrix type="saturate" values="0"/></filter>
            <filter id="invert" color-interpolation-filters="sRGB"><feComponentTransfer>
              <feFuncR type="table" tableValues="1 0"/><feFuncG type="table" tableValues="1 0"/><feFuncB type="table" tableValues="1 0"/>
            </feComponentTransfer></filter>
            <rect x="0" y="0" width="100" height="100" fill="red" filter="url(#gray)"/>
            <rect x="200" y="0" width="100" height="100" fill="red" filter="url(#invert)"/>
            """);
        var gray = At(pdf, 50, 50);
        Assert.True(Math.Abs(gray.Item1 - gray.Item2) < 4 && Math.Abs(gray.Item2 - gray.Item3) < 4 && gray.Item1 < 200, $"gray {gray}");
        var cyan = At(pdf, 250, 50);
        Assert.True(cyan.Item1 < 30 && cyan.Item2 > 225 && cyan.Item3 > 225, $"cyan {cyan}");
    }

    [Fact]
    public void Filter_without_primitives_hides_the_element_and_a_missing_filter_is_ignored()
    {
        var pdf = Svg("""
            <filter id="f"></filter>
            <rect width="100" height="100" fill="red" filter="url(#f)"/>
            <rect x="200" width="100" height="100" fill="red" filter="url(#none)"/>
            """);
        Assert.True(IsWhite(At(pdf, 50, 50)));
        Assert.True(IsRed(At(pdf, 250, 50)));
    }

    [Fact]
    public void Filters_on_text_and_in_pdfa_are_valid()
    {
        var html = """
            <body style='margin:0'><svg width='400' height='100'>
              <filter id="f"><feGaussianBlur stdDeviation="2"/></filter>
              <mask id="m"><rect width="200" height="100" fill="white"/></mask>
              <text x="10" y="50" font-size="30" filter="url(#f)" mask="url(#m)">Blurred</text>
            </svg></body>
            """;
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Margins = new PageMargins(0), Conformance = PdfConformance.PdfA2b });
        AssertStructurallyValid(pdf);
    }

    // ---- SVG paint ----

    [Fact]
    public void Translucent_svg_fills_overlap_with_real_transparency()
    {
        var pdf = Svg("""
            <rect width="200" height="200" fill="#0000ff"/>
            <rect x="100" width="200" height="200" fill="#ff0000" fill-opacity="0.5"/>
            """);
        Assert.Contains("/a128 gs", Latin1(pdf));
        var over = At(pdf, 150, 100);
        Assert.True(over.Item1 > 100 && over.Item1 < 160 && over.Item3 > 100 && over.Item3 < 160, $"red over blue {over}");
    }

    [Fact]
    public void Gradient_stops_with_opacity_are_transparent_over_other_content()
    {
        // Red fading out over blue: at the transparent end the blue shows through, where it used to be blended onto white.
        var pdf = Svg("""
            <linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="red" stop-opacity="0"/></linearGradient>
            <rect width="400" height="200" fill="#0000ff"/>
            <rect width="400" height="200" fill="url(#g)"/>
            """);
        AssertStructurallyValid(pdf);
        Assert.Contains("/S /Luminosity", Latin1(pdf));
        Assert.True(IsRed(At(pdf, 4, 100)), $"{At(pdf, 4, 100)}");
        Assert.True(IsBlue(At(pdf, 396, 100)), $"{At(pdf, 396, 100)}");
        var middle = At(pdf, 200, 100);
        Assert.True(middle.Item1 > 100 && middle.Item1 < 160 && middle.Item2 < 30 && middle.Item3 > 100 && middle.Item3 < 160, $"half red over blue {middle}");
    }

    [Fact]
    public void Translucent_gradient_stops_keep_spread_method_and_gradient_transform()
    {
        // A vertical repeating gradient (rotated by 90 degrees), opaque red to transparent in each 40-unit period.
        var pdf = Svg("""
            <linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2="40" y2="0" spreadMethod="repeat" gradientTransform="rotate(90)">
              <stop offset="0" stop-color="red"/><stop offset="0.5" stop-color="red"/><stop offset="0.5" stop-color="red" stop-opacity="0"/><stop offset="1" stop-color="red" stop-opacity="0"/>
            </linearGradient>
            <rect width="400" height="400" fill="#0000ff"/>
            <rect width="400" height="400" fill="url(#g)" stroke="url(#g)" stroke-width="10"/>
            """);
        AssertStructurallyValid(pdf);
        Assert.True(IsRed(At(pdf, 200, 10)), $"{At(pdf, 200, 10)}");
        Assert.True(IsBlue(At(pdf, 200, 30)), $"{At(pdf, 200, 30)}");
        Assert.True(IsRed(At(pdf, 200, 330)), $"{At(pdf, 200, 330)}");
        Assert.True(IsBlue(At(pdf, 200, 350)), $"{At(pdf, 200, 350)}");
        // The stroke, painted through a soft mask of its own.
        Assert.True(IsRed(At(pdf, 2, 10)), $"{At(pdf, 2, 10)}");
        Assert.True(IsBlue(At(pdf, 2, 30)), $"{At(pdf, 2, 30)}");
    }

    [Fact]
    public void Fill_opacity_on_a_gradient_is_transparent()
    {
        var pdf = Svg("""
            <linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="red"/></linearGradient>
            <rect width="400" height="200" fill="#0000ff"/>
            <rect width="400" height="200" fill="url(#g)" fill-opacity="0.5"/>
            <text x="10" y="300" font-size="60" fill="url(#g)" fill-opacity="0.5">Text</text>
            """);
        var p = At(pdf, 200, 100);
        Assert.True(p.Item1 > 100 && p.Item1 < 160 && p.Item3 > 100 && p.Item3 < 160, $"half red over blue {p}");
        Assert.Single(Regex.Matches(AllContent(pdf), @"/s\d+ gs\n[^Q]*0 Tr 0 Tw"));
    }

    [Fact]
    public void Opaque_svg_gradients_need_no_soft_mask()
    {
        var pdf = Svg("""<linearGradient id="g"><stop offset="0" stop-color="red"/><stop offset="1" stop-color="blue"/></linearGradient><rect width="400" height="200" fill="url(#g)"/>""");
        Assert.DoesNotContain("/SMask", Latin1(pdf));
    }

    [Fact]
    public void Colours_after_a_clip_group_are_written_again()
    {
        var pdf = Svg("""
            <clipPath id="c"><rect width="400" height="100"/></clipPath>
            <rect width="10" height="10" fill="blue"/>
            <rect y="50" width="400" height="100" fill="red" clip-path="url(#c)"/>
            <rect y="200" width="400" height="100" fill="red"/>
            """);
        Assert.True(IsRed(At(pdf, 200, 250)));
    }

    // ---- stroke dashes ----

    private static float[]? DashOf(string content, string id = "a", string svg = "width='200' height='100'")
    {
        var doc = Styled($"<svg id='s' {svg}>{content}</svg>");
        return SvgImage.Build(ById(doc, "s")).Shapes.Single(s => s.Stroke == ById(doc, id).Style!.SvgStroke).Dash;
    }

    [Theory]
    [InlineData("10 5", new[] { 10f, 5f })]
    [InlineData("10,5", new[] { 10f, 5f })]
    [InlineData(" 4 , 2 1 ", new[] { 4f, 2f, 1f, 4f, 2f, 1f })] // an odd-length list repeats
    [InlineData("3", new[] { 3f, 3f })]
    [InlineData("0.75pt 0.5in", new[] { 1f, 48f })]
    [InlineData("10%", new[] { 10f, 10f })] // of the viewport's normalised diagonal, sqrt((100² + 100²) / 2) = 100
    public void Stroke_dasharray_becomes_the_dash_pattern(string value, float[] expected)
    {
        var dash = DashOf($"<line id='a' x2='100' stroke='red' stroke-dasharray='{value}'/>", svg: "width='200' height='100' viewBox='0 0 100 100'");
        Assert.NotNull(dash);
        Assert.Equal(expected, dash!.Select(v => MathF.Round(v, 3)));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("10 -5")]   // a negative value makes the list invalid
    [InlineData("0 0")]     // a pattern of length zero
    [InlineData("10,,5")]
    [InlineData("10 x")]
    public void Invalid_or_empty_dash_arrays_give_a_solid_stroke(string value)
        => Assert.Null(DashOf($"<line id='a' x2='100' stroke='red' stroke-dasharray='{value}'/>"));

    [Fact]
    public void Dash_arrays_inherit_and_come_from_css()
    {
        Assert.Equal(new[] { 2f, 6f }, DashOf("<g stroke-dasharray='2 6'><line id='a' x2='100' stroke='red'/></g>"));
        Assert.Null(DashOf("<g stroke-dasharray='2 6'><line id='a' x2='100' stroke='red' stroke-dasharray='none'/></g>"));
        Assert.Equal(new[] { 6f, 2f }, DashOf("<line id='a' style='stroke-dasharray:6px,2;stroke-dashoffset:-1' x2='100' stroke='red'/>"));
        // The user units of a transformed shape are scaled into the root's.
        Assert.Equal(new[] { 4f, 2f }, DashOf("<g transform='scale(2)'><line id='a' x2='100' stroke='red' stroke-dasharray='2 1'/></g>"));
    }

    [Fact]
    public void Dash_offset_becomes_the_phase_within_the_pattern()
    {
        static float Phase(string content)
        {
            var doc = Styled($"<svg id='s' width='200' height='100'>{content}</svg>");
            return SvgImage.Build(ById(doc, "s")).Shapes.Single().DashPhase;
        }
        Assert.Equal(3, Phase("<line x2='100' stroke='red' stroke-dasharray='8 4' stroke-dashoffset='3'/>"));
        Assert.Equal(7, Phase("<line style='stroke-dasharray:6px,2;stroke-dashoffset:-1' x2='100' stroke='red'/>")); // -1 within a pattern of 8
        Assert.Equal(1, Phase("<line x2='100' stroke='red' stroke-dasharray='8 4' stroke-dashoffset='25'/>"));
    }

    [Fact]
    public void Path_length_rescales_dashes()
    {
        // A line 200 units long measured as 100: each dash unit is two user units.
        Assert.Equal(new[] { 60f, 140f }, DashOf("<line id='a' x2='200' stroke='red' pathLength='100' stroke-dasharray='30 70'/>"));
        var donut = DashOf("<circle id='a' cx='50' cy='50' r='40' fill='none' stroke='red' pathLength='100' stroke-dasharray='25 75'/>")!;
        Assert.Equal(2 * MathF.PI * 40 / 4, donut[0], 0.2f);
    }

    [Fact]
    public void Dashed_svg_strokes_are_drawn_dashed()
    {
        var pdf = Svg("""
            <line x1="0" y1="50" x2="400" y2="50" stroke="red" stroke-width="20" stroke-dasharray="40 40"/>
            <line x1="0" y1="150" x2="400" y2="150" stroke="red" stroke-width="20"/>
            """);
        var content = AllContent(pdf);
        Assert.Contains("[40 40] 0 d", content);
        // The dash is put back to solid for the next stroke.
        Assert.Matches(@"\[40 40\] 0 d\n[^\[]*S\n\[\] 0 d\n", content);
        Assert.True(IsRed(At(pdf, 20, 50)), $"{At(pdf, 20, 50)}");
        Assert.True(IsWhite(At(pdf, 60, 50)), $"{At(pdf, 60, 50)}");
        Assert.True(IsRed(At(pdf, 100, 50)), $"{At(pdf, 100, 50)}");
        Assert.True(IsRed(At(pdf, 60, 150)), $"{At(pdf, 60, 150)}");
    }

    [Fact]
    public void Solid_svg_strokes_write_no_dash_pattern()
        => Assert.DoesNotContain(" d\n", AllContent(Svg("<rect x='10' y='10' width='100' height='100' fill='none' stroke='red'/>")));

    // ---- text on a path ----

    // The text matrices of the glyphs drawn, in the drawing's user space (y down), and how many glyphs were shown.
    private static List<float[]> GlyphMatrices(byte[] pdf)
        => Regex.Matches(AllContent(pdf), @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm (?:<[0-9A-F]+>|\(.*?\)) Tj")
            .Select(m => Enumerable.Range(1, 6).Select(i => float.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
            .ToList();

    private static float Width(string text, float size) => PdfPecker.Fonts.StandardFont.All[0].Measure(text, size);

    [Fact]
    public void Text_on_a_path_puts_each_glyph_on_it_turned_with_the_path()
    {
        // A path straight down: each glyph is turned a quarter turn clockwise and the next one sits below it.
        var pdf = Svg("""<path id="p" d="M100 20 L100 380"/><text font-family="Helvetica" font-size="20"><textPath href="#p">AB</textPath></text>""");
        var tm = GlyphMatrices(pdf);
        Assert.Equal(2, tm.Count);
        Assert.Equal(new[] { 0f, 1f, 1f, 0f, 100f, 20f }, tm[0].Select(v => MathF.Round(v, 3)));
        Assert.Equal(20 + Width("A", 20), tm[1][5], 0.01f);
        Assert.Equal(100, tm[1][4], 0.01f);
    }

    [Fact]
    public void Text_on_a_path_follows_curves()
    {
        // Around the top of a circle of radius 100, drawn as an arc: each glyph is turned with the tangent at its middle.
        var pdf = Svg("""<path id="p" d="M100 200 A100 100 0 0 1 300 200"/><text font-family="Helvetica" font-size="20"><textPath href="#p" startOffset="50%" text-anchor="middle">MMM</textPath></text>""");
        var tm = GlyphMatrices(pdf);
        Assert.Equal(3, tm.Count);
        // The middle glyph is at the top of the arc, upright; the first leans left and the last right, by the same angle.
        Assert.Equal(1, tm[1][0], 0.01f);
        Assert.Equal(200 - Width("M", 20) / 2, tm[1][4], 0.3f);
        Assert.Equal(100, tm[1][5], 0.3f);
        Assert.True(tm[0][1] < -0.1f && tm[2][1] > 0.1f, $"{tm[0][1]} {tm[2][1]}");
        Assert.Equal(-tm[0][1], tm[2][1], 0.01f);
    }

    [Fact]
    public void Glyphs_off_either_end_of_the_path_are_not_drawn()
    {
        // A glyph is drawn when its middle is on the path: of six M (16.7 wide) on a path 30 long, two.
        var pdf = Svg("""<path id="p" d="M10 50 L40 50"/><text font-family="Helvetica" font-size="20"><textPath href="#p">MMMMMM</textPath></text>""");
        Assert.Equal(2, GlyphMatrices(pdf).Count);
        // Starting before the path, the first glyphs are left out.
        pdf = Svg("""<path id="p" d="M10 50 L390 50"/><text font-family="Helvetica" font-size="20"><textPath href="#p" startOffset="-20">MMM</textPath></text>""");
        var tm = GlyphMatrices(pdf);
        Assert.Equal(2, tm.Count);
        Assert.Equal(10 - 20 + Width("M", 20), tm[0][4], 0.01f);
    }

    [Fact]
    public void Start_offset_takes_percentages_lengths_and_path_length_units()
    {
        static float FirstX(string offset, string pathLength = "")
            => GlyphMatrices(Svg($"""<path id="p" d="M0 50 L400 50" {pathLength}/><text font-family="Helvetica" font-size="20"><textPath href="#p" startOffset="{offset}">A</textPath></text>"""))[0][4];
        Assert.Equal(100, FirstX("25%"), 0.01f);
        Assert.Equal(30, FirstX("30"), 0.01f);
        Assert.Equal(40, FirstX("30pt"), 0.01f);
        Assert.Equal(200, FirstX("5", "pathLength='10'"), 0.01f);
    }

    [Fact]
    public void Text_on_a_path_takes_dy_styling_letter_spacing_and_the_paths_transform()
    {
        var doc = Styled("""
            <svg id="s" width="400" height="200" font-family="Helvetica">
              <path id="p" d="M10 0 L390 0" transform="translate(0 100)"/>
              <text font-size="20" letter-spacing="5"><textPath href="#p">A<tspan dy="-10" fill="#ff0000">B</tspan>C</textPath></text>
            </svg>
            """);
        var texts = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Kind == SvgShape.KindText).ToList();
        Assert.Equal(new[] { "A", "B", "C" }, texts.Select(t => t.Text));
        Assert.All(texts, t => Assert.NotNull(t.TextPath));
        Assert.Equal(100, texts[0].TextPath!.MinY);
        Assert.Equal(0xFFFF0000u, texts[1].Fill);
        // dy shifts the text off the path, and the shift stays for the text after it.
        Assert.Equal(new[] { 0f, -10f, -10f }, texts.Select(t => t.PathShift));
        Assert.Equal(Width("A", 20) + 5, texts[1].PathStart, 0.01f);
        Assert.Equal(Width("AB", 20) + 10, texts[2].PathStart, 0.01f);
    }

    [Fact]
    public void Text_on_a_path_is_written_as_selectable_text()
    {
        var content = AllContent(Svg("""<path id="p" d="M10 100 Q200 0 390 100"/><text font-family="Helvetica" font-size="20"><textPath href="#p">Hi there</textPath></text>"""));
        // One text object with a text matrix and a shown glyph for each character, filled (not drawn as paths).
        Assert.Single(Regex.Matches(content, "BT "));
        Assert.Contains(" 0 Tr ", content);
        Assert.Equal("Hi there", string.Concat(Regex.Matches(content, @"Tm \((.)\) Tj").Select(m => m.Groups[1].Value)));
    }

    [Theory]
    [InlineData("#missing")]
    [InlineData("#c")]   // Chromium lays text only along path elements
    [InlineData("#t")]   // the text element itself
    [InlineData("#tp")]  // the textPath itself
    [InlineData("")]
    public void Text_on_a_path_that_is_not_there_is_not_drawn(string href)
    {
        var doc = Styled($"""
            <svg id="s" width="400" height="200" font-family="Helvetica">
              <circle id="c" cx="100" cy="100" r="50"/>
              <text id="t" x="10" y="50" font-size="20">Before <textPath id="tp" href="{href}">on</textPath></text>
            </svg>
            """);
        var texts = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Kind == SvgShape.KindText).ToList();
        Assert.Equal("Before", Assert.Single(texts).Text);
    }

    [Fact]
    public void Text_after_a_text_path_continues_from_the_paths_end()
    {
        var doc = Styled("""
            <svg id="s" width="400" height="200" font-family="Helvetica">
              <path id="p" d="M10 100 Q100 20 220 60"/>
              <text x="5" y="15" font-size="14">Before <textPath href="#p">on the path</textPath> after</text>
            </svg>
            """);
        var texts = SvgImage.Build(ById(doc, "s")).Shapes.Where(s => s.Kind == SvgShape.KindText).ToList();
        var after = texts.Single(t => t.Text == " after");
        Assert.Null(after.TextPath);
        Assert.Equal(220, after.TextMatrix.E, 0.01f);
        Assert.Equal(60, after.TextMatrix.F, 0.01f);
    }

    [Fact]
    public void Shaped_text_on_a_path_keeps_its_joined_glyphs()
    {
        // Arabic is shaped as a whole and its glyphs placed one by one: the same glyphs as the straight text, joined forms
        // included, each with its own text matrix.
        var arabic = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansArabic-Subset.ttf")));
        string Glyphs(string text) => string.Join(" ", Regex.Matches(AllContent(HtmlToPdf.Convert(
            "<style>@font-face{font-family:Ar;src:url(data:font/ttf;base64," + arabic + ")}</style><body style='margin:0'>"
            + "<svg width='400' height='200' font-family='Ar' font-size='20' direction='rtl'><path id='p' d='M10 150 Q200 0 390 150'/>"
            + text + "</svg></body>", NoMargins)), @"<([0-9A-F]+)>")
            .SelectMany(m => m.Groups[1].Value.Chunk(4).Select(c => new string(c))).Order(StringComparer.Ordinal));
        string straight = Glyphs("<text x='300' y='100'>مرحبا</text>");
        Assert.Equal(straight, Glyphs("<text><textPath href='#p' startOffset='90%'>مرحبا</textPath></text>"));
        Assert.True(straight.Split(' ').Length >= 4, straight);
    }

    // ---- table cells and borders ----

    [Fact]
    public void Table_cells_clip_their_overflow()
    {
        var html = """
            <body style='margin:0'><table style='border-collapse:collapse;table-layout:fixed;width:200px'><tr>
              <td style='width:100px;padding:0;overflow:hidden'><div style='width:300px;height:40px;background:#ff0000'></div></td>
              <td style='width:100px;padding:0'></td></tr></table>
            <table style='table-layout:fixed;width:200px;border-spacing:0'><tr>
              <td style='width:100px;padding:0'><div style='width:300px;height:40px;background:#0000ff'></div></td>
              <td style='width:100px;padding:0'></td></tr></table></body>
            """;
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        Assert.Contains("W n", Latin1(pdf));
        Assert.True(IsRed(At(pdf, 50, 20)));
        Assert.True(IsWhite(At(pdf, 150, 20)), "clipped at the cell's edge");
        Assert.True(IsBlue(At(pdf, 150, 60)), "a cell with visible overflow does not clip");
    }

    [Fact]
    public void Double_borders_on_rounded_boxes_leave_the_middle_third_open()
    {
        var html = """
            <body style='margin:0'><div style='margin:20px;width:200px;height:100px;border:12px double #ff0000;border-radius:20px'></div>
            <div style='margin:20px;width:200px;height:100px;border-style:double;border-width:12px;border-color:#ff0000 #0000ff;border-radius:20px'></div></body>
            """;
        var pdf = HtmlToPdf.Convert(html, NoMargins);
        // The left side of the first box spans x 20..32: outer third 20..24, gap 24..28, inner third 28..32.
        Assert.True(IsRed(At(pdf, 22, 90)));
        Assert.True(IsWhite(At(pdf, 26, 90)));
        Assert.True(IsRed(At(pdf, 30, 90)));
        // The second box starts at y 164 (the margins between the boxes collapse): top side red, left side blue.
        float top = 20 + 124 + 20;
        Assert.True(IsRed(At(pdf, 130, top + 2)));
        Assert.True(IsWhite(At(pdf, 130, top + 6)));
        Assert.True(IsBlue(At(pdf, 22, top + 60)));
        Assert.True(IsWhite(At(pdf, 26, top + 60)));
    }
}

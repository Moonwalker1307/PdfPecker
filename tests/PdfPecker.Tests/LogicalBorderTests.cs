using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Tests;

/// <summary>
/// The logical border properties (border-inline-start, border-block-width, border-start-start-radius, ...), which land
/// on the side that the element's writing mode and direction give them.
/// </summary>
public class LogicalBorderTests
{
    private static ComputedStyle StyleOf(string attributes, string style)
    {
        var doc = HtmlParser.Parse($"<div {attributes} style='{style}'></div>");
        new StyleResolver(new Stylesheet(), 600, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return doc.FindFirst("div")!.Style!;
    }

    private static string Sides(ComputedStyle s)
        => $"{Side(s.BorderTopStyle, s.BorderTopWidth, s.BorderTopColor)} {Side(s.BorderRightStyle, s.BorderRightWidth, s.BorderRightColor)} "
           + $"{Side(s.BorderBottomStyle, s.BorderBottomWidth, s.BorderBottomColor)} {Side(s.BorderLeftStyle, s.BorderLeftWidth, s.BorderLeftColor)}";

    // A side as style:width:colour, with "-" where it has none; widths are in CSS pixels.
    private static string Side(BorderStyle style, float width, uint? color)
        => style == BorderStyle.None ? "-" : $"{style}:{Math.Round(width / 0.75f, 2)}:{(color is { } c ? (c & 0xFFFFFF).ToString("x6") : "currentColor")}";

    [Fact]
    public void Inline_start_is_the_left_side_in_ltr_and_the_right_side_in_rtl()
    {
        Assert.Equal("- - - Solid:4:27ae60", Sides(StyleOf("", "border-inline-start: 4px solid #27ae60")));
        Assert.Equal("- Solid:4:27ae60 - -", Sides(StyleOf("dir=rtl", "border-inline-start: 4px solid #27ae60")));
        Assert.Equal("- Solid:4:27ae60 - -", Sides(StyleOf("", "border-inline-end: 4px solid #27ae60")));
    }

    [Fact]
    public void Block_sides_are_the_top_and_bottom_of_horizontal_text()
    {
        Assert.Equal("Dotted:2:c0392b - - -", Sides(StyleOf("", "border-block-start: 2px dotted #c0392b")));
        Assert.Equal("- - Dotted:2:c0392b -", Sides(StyleOf("dir=rtl", "border-block-end: 2px dotted #c0392b")));
    }

    [Theory]
    // Block start is the right side in vertical-rl and sideways-rl, and the left side in vertical-lr and sideways-lr;
    // inline start is the top, except in sideways-lr, where lines run upwards.
    [InlineData("vertical-rl", "", "- Solid:1:000000 - -", "Solid:1:000000 - - -")]
    [InlineData("vertical-lr", "", "- - - Solid:1:000000", "Solid:1:000000 - - -")]
    [InlineData("sideways-rl", "", "- Solid:1:000000 - -", "Solid:1:000000 - - -")]
    [InlineData("sideways-lr", "", "- - - Solid:1:000000", "- - Solid:1:000000 -")]
    [InlineData("vertical-rl", "dir=rtl", "- Solid:1:000000 - -", "- - Solid:1:000000 -")]
    [InlineData("sideways-lr", "dir=rtl", "- - - Solid:1:000000", "Solid:1:000000 - - -")]
    public void Writing_mode_and_direction_pick_the_physical_side(string mode, string attributes, string blockStart, string inlineStart)
    {
        Assert.Equal(blockStart, Sides(StyleOf(attributes, $"writing-mode: {mode}; border-block-start: 1px solid #000")));
        Assert.Equal(inlineStart, Sides(StyleOf(attributes, $"writing-mode: {mode}; border-inline-start: 1px solid #000")));
    }

    [Fact]
    public void Writing_mode_is_known_before_logical_properties_that_come_earlier()
    {
        Assert.Equal("- Solid:1:000000 - -", Sides(StyleOf("", "border-block-start: 1px solid #000; writing-mode: vertical-rl")));
    }

    [Fact]
    public void Axis_shorthands_set_both_sides()
    {
        Assert.Equal("- Solid:4:ff0000 - Solid:4:ff0000", Sides(StyleOf("", "border-inline: 4px solid #f00")));
        Assert.Equal("Dashed:2:0000ff - Dashed:2:0000ff -", Sides(StyleOf("", "border-block: 2px dashed #00f")));
    }

    [Fact]
    public void Width_style_and_color_shorthands_take_start_and_end()
    {
        var s = StyleOf("", "border-inline-style: solid dashed; border-inline-width: 2px 4px; border-inline-color: #f00 #00f");
        Assert.Equal("- Dashed:4:0000ff - Solid:2:ff0000", Sides(s));
        var one = StyleOf("", "border-block: 1px solid; border-block-color: #0f0");
        Assert.Equal("Solid:1:00ff00 - Solid:1:00ff00 -", Sides(one));
    }

    [Fact]
    public void Longhands_set_one_property_of_one_side()
    {
        var s = StyleOf("dir=rtl", "border-inline-start-style: solid; border-inline-start-width: 8px; border-inline-start-color: #123456");
        Assert.Equal("- Solid:8:123456 - -", Sides(s));
        var t = StyleOf("", "border-block-end-style: double; border-block-end-width: 6px");
        Assert.Equal("- - Double:6:currentColor -", Sides(t));
    }

    [Fact]
    public void The_later_declaration_wins_whichever_way_the_side_is_named()
    {
        Assert.Equal("- - - Solid:4:0000ff", Sides(StyleOf("", "border-left: 4px solid #f00; border-inline-start: 4px solid #00f")));
        Assert.Equal("- - - Solid:4:ff0000", Sides(StyleOf("", "border-inline-start: 4px solid #00f; border-left: 4px solid #f00")));
    }

    [Fact]
    public void Css_wide_keywords_reach_the_physical_side()
    {
        var s = StyleOf("", "border: 2px solid #f00; border-inline-start: initial");
        Assert.Equal("Solid:2:ff0000 Solid:2:ff0000 Solid:2:ff0000 -", Sides(s));
    }

    [Fact]
    public void Invalid_values_are_ignored()
    {
        Assert.Equal("- - - -", Sides(StyleOf("", "border-inline-width: 1px 2px 3px; border-inline-style: 5")));
    }

    [Theory]
    [InlineData("", "border-start-start-radius", 0)]
    [InlineData("", "border-start-end-radius", 1)]
    [InlineData("", "border-end-end-radius", 2)]
    [InlineData("", "border-end-start-radius", 3)]
    [InlineData("dir=rtl", "border-start-start-radius", 1)]
    [InlineData("dir=rtl", "border-end-start-radius", 2)]
    public void Logical_radii_land_on_the_corner_of_their_sides(string attributes, string property, int corner)
    {
        var s = StyleOf(attributes, $"{property}: 8px");
        Assert.NotNull(s.BorderRadii);
        for (int k = 0; k < 4; k++) Assert.Equal(k == corner ? 6f : 0f, s.BorderRadii![k * 2].Value, 0.001);
    }

    [Fact]
    public void Logical_radii_follow_vertical_writing_modes()
    {
        // vertical-rl: block start is the right side and inline start the top, so start-start is the top right corner.
        var s = StyleOf("", "writing-mode: vertical-rl; border-start-start-radius: 8px");
        Assert.Equal(6f, s.BorderRadii![2].Value, 0.001);
    }

    [Fact]
    public void The_logical_properties_are_supported_in_queries()
    {
        Assert.True(CssSupports.Evaluate("(border-inline-start: 1px solid red) and (border-block-width: 1px) and (border-end-start-radius: 2px)"));
    }

    [Fact]
    public void Logical_padding_and_margin_follow_vertical_writing_modes_too()
    {
        var s = StyleOf("", "writing-mode: vertical-rl; padding-block-start: 8px; margin-inline-end: 4px");
        Assert.Equal(6f, s.PaddingRight.Value, 0.001);
        Assert.Equal(3f, s.MarginBottom.Value, 0.001);
    }
}

using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>The if() function (CSS Values 5) with media(), supports() and style() conditions, as Chrome ships it.</summary>
public class CssIfTests
{
    private static readonly MediaContext Print = new() { Width = 400, Height = 600 };

    private static CssVarScope Vars(params (string Name, string Value)[] vars)
    {
        var scope = new CssVarScope(null);
        foreach (var (name, value) in vars) scope.Set(name, value);
        return scope;
    }

    [Theory]
    [InlineData("if(style(--a: 1): red; else: blue)", "red")]
    [InlineData("if(style(--a: 2): red; else: blue)", "blue")]
    [InlineData("if(style(--a): red; else: blue)", "red")]
    [InlineData("if(style(--missing): red; else: blue)", "blue")]
    [InlineData("if(style(--b:  x   y ): yes; else: no)", "yes")]
    [InlineData("if(not style(--a: 2): yes; else: no)", "yes")]
    [InlineData("if(style(--a: 1) and style(--b: x y): yes; else: no)", "yes")]
    [InlineData("if(style(--a: 2) or (style(--a: 1)): yes; else: no)", "yes")]
    [InlineData("if(media(print): 1cm; else: 2cm)", "1cm")]
    [InlineData("if(media(screen): 1cm; else: 2cm)", "2cm")]
    [InlineData("if(media(width > 500pt): wide; media(width >= 100px): narrow)", "narrow")]
    [InlineData("if(supports(display: grid): grid; else: block)", "grid")]
    [InlineData("if(supports(display: nonsense): grid; else: block)", "block")]
    [InlineData("1px if(style(--a: 1): solid; else: dashed) red", "1px solid red")]
    [InlineData("if(style(--a: 1): if(style(--b: x y): inner; else: no); else: outer)", "inner")]
    [InlineData("'if(style(--a: 1): x)'", "'if(style(--a: 1): x)'")]
    [InlineData("if(style(--a: 1): a; style(--a: 1): b)", "a")]
    [InlineData("calc(if(style(--a: 1): 2; else: 3) * 1px)", "calc(2 * 1px)")]
    public void Branches_are_chosen(string value, string expected)
        => Assert.Equal(expected, CssIf.Resolve(value, Vars(("--a", "1"), ("--b", "x y")), Print));

    [Theory]
    [InlineData("if(style(--a: 2): red)")]          // no branch holds
    [InlineData("if(red)")]                         // no condition
    [InlineData("if(foo(x): red; else: blue)")]     // unknown test
    [InlineData("if(style(color: red): a; else: b)")] // only custom properties
    [InlineData("if(style(--a: 1) and style(--a: 1) or style(--a: 1): x)")] // and with or
    public void Invalid_calls_make_the_value_invalid(string value)
        => Assert.Null(CssIf.Resolve(value, Vars(("--a", "1")), Print));

    [Fact]
    public void Deep_nesting_is_bounded()
    {
        var value = string.Concat(Enumerable.Repeat("if(else: ", 100)) + "x" + new string(')', 100);
        Assert.Null(CssIf.Resolve(value, null, Print));
    }

    [Fact]
    public void Elements_choose_from_their_own_custom_properties()
    {
        var pdf = ConvertUncompressed("<style>p { --s: a; color: if(style(--s: b): #ff0000; else: #0000ff) } p.b { --s: b }</style><p>one</p><p class=b>two</p>");
        var content = AllContent(pdf);
        Assert.Contains("0 0 1 rg", content);
        Assert.Contains("1 0 0 rg", content);
    }

    [Fact]
    public void A_shorthand_with_if_is_expanded_after_the_choice()
    {
        var pdf = ConvertUncompressed("<style>div { border: if(media(print): 3pt solid #00ff00; else: none); height: 10pt }</style><div></div>");
        Assert.Contains("0 1 0 RG", AllContent(pdf));
    }

    [Fact]
    public void A_custom_property_holding_if_is_resolved_where_it_is_used()
    {
        var pdf = ConvertUncompressed("<style>:root { --c: if(style(--dark: 1): #ffffff; else: #ff0000) } p.d { --dark: 1; background: #000 } p { color: var(--c) }</style><p>light</p><p class=d>dark</p>");
        var content = AllContent(pdf);
        Assert.Contains("1 0 0 rg", content);
        Assert.Contains("1 1 1 rg", content);
    }

    [Fact]
    public void Words_ending_in_if_are_not_calls()
    {
        Assert.False(CssIf.Has("url(a.gif(1))"));
        Assert.False(CssIf.Has("motif(x)"));
        Assert.True(CssIf.Has("1px IF(else: 2px)"));
    }
}

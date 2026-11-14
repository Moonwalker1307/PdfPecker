using PdfPecker.Css;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>Custom functions defined with @function (CSS Functions and Mixins), as Chrome ships them from version 139.</summary>
public class CssFunctionTests
{
    private static readonly MediaContext Print = new() { Width = 400, Height = 600 };

    private const string Functions = """
        @function --double(--x) { result: calc(var(--x) * 2); }
        @function --space(--n: 1) { result: calc(var(--n) * var(--unit)); }
        @function --tint(--c, --pct: 30%) { --mixed: color-mix(in srgb, var(--c) var(--pct), white); result: var(--mixed); }
        @function --order() { result: var(--late); --late: early; --late: late; }
        @function --twice(--x) { result: --double(--double(var(--x))); }
        @function --kind(--k) { result: if(style(--k: warn): orange; else: green); }
        @function --list(--l) { result: linear-gradient(var(--l)); }
        @function --where() { result: screen; @media print { result: print; } @media (width > 10000px) { result: huge; } }
        @function --feature() { result: no; @supports (display: grid) { result: grid; } }
        @function --container() { result: plain; @container (width > 0px) { result: contained; } }
        @function --outer() { result: var(--unit); }
        @function --none() { --x: 1; }
        @function --loop(--x) { result: --loop(var(--x)); }
        """;

    private static string? Resolve(string value)
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet(Functions, sheet);
        var vars = new CssVarScope(null);
        vars.Set("--unit", "4px");
        return CssFunctions.Resolve(value, sheet.Functions, vars, Print);
    }

    [Theory]
    [InlineData("--double(3px)", "calc(3px * 2)")]
    [InlineData("--space()", "calc(1 * 4px)")]
    [InlineData("--space(5)", "calc(5 * 4px)")]
    [InlineData("1px solid --tint(red)", "1px solid color-mix(in srgb, red 30%, white)")]
    [InlineData("--tint(red, 60%)", "color-mix(in srgb, red 60%, white)")]
    [InlineData("--order()", "late")]
    [InlineData("--twice(1px)", "calc(calc(1px * 2) * 2)")]
    [InlineData("--kind(warn) --kind(ok)", "orange green")]
    [InlineData("--list({red, blue})", "linear-gradient(red, blue)")]
    [InlineData("--where()", "print")]
    [InlineData("--feature()", "grid")]
    [InlineData("--container()", "plain")]
    [InlineData("--outer()", "4px")]
    [InlineData("'--double(1px)'", "'--double(1px)'")]
    [InlineData("--unknown(1px)", "--unknown(1px)")]
    [InlineData("var(--double)", "var(--double)")]
    public void Calls_are_replaced_by_their_results(string value, string expected) => Assert.Equal(expected, Resolve(value));

    [Theory]
    [InlineData("--double()")]          // a missing argument without a default
    [InlineData("--double(1px, 2px)")] // too many arguments
    [InlineData("--none()")]           // no result
    [InlineData("--loop(1px)")]        // endless recursion
    [InlineData("--double(1px")]       // unclosed
    public void Invalid_calls_make_the_value_invalid(string value) => Assert.Null(Resolve(value));

    [Fact]
    public void The_last_rule_of_a_name_wins_and_bad_preludes_are_ignored()
    {
        var sheet = new Stylesheet();
        CssParser.ParseStylesheet("@function --f() { result: a } @function --f() { result: b } @function f() { result: c } @function --g(x) { result: d }", sheet);
        Assert.Equal("b", CssFunctions.Resolve("--f()", sheet.Functions, null, Print));
        Assert.Single(sheet.Functions!);
    }

    [Fact]
    public void Functions_size_boxes_and_shorthands_as_in_Chrome()
    {
        var pdf = ConvertUncompressed("""
            <style>
            :root { --unit: 5pt }
            @function --space(--n: 1) { result: calc(var(--n) * var(--unit)); }
            @function --brand(--a: 1) { result: rgb(0 255 0 / var(--a)); }
            div { width: --space(20); height: --space(2); background: --brand(); border: --space() solid #ff0000 }
            </style><div></div>
            """);
        var content = AllContent(pdf);
        Assert.Contains("0 1 0 rg", content);
        Assert.Contains("1 0 0 RG", content);
        // 100pt by 10pt inside a border that is snapped to whole device pixels, with the background under it.
        Assert.Contains("108.75 18.75 re", content);
    }

    [Fact]
    public void A_custom_property_holding_a_call_is_resolved_where_it_is_used()
    {
        var pdf = ConvertUncompressed("<style>@function --pick(--c) { result: var(--c) } :root { --fg: --pick(#ff0000) } p { color: var(--fg) }</style><p>red</p>");
        Assert.Contains("1 0 0 rg", AllContent(pdf));
    }

    [Fact]
    public void A_brace_in_a_call_stays_in_its_declaration()
    {
        // Before, the brace of {red, blue} started a nested rule, and the declarations after it were lost.
        var pdf = ConvertUncompressed("<style>@function --first(--l) { result: var(--l) } p { --x: --first({#0000ff}); color: #ff0000; background: #fff }</style><p>red</p>");
        Assert.Contains("1 0 0 rg", AllContent(pdf));
    }
}

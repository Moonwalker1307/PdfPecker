using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Tests;

/// <summary>:in-range and :out-of-range match the elements Chrome matches in a page nobody has touched.</summary>
public class RangeSelectorTests
{
    private static readonly Selector InRange = Selector.Parse(":in-range")!, OutOfRange = Selector.Parse(":out-of-range")!;

    private static IEnumerable<Element> WithIds(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Id != null) yield return c;
            foreach (var d in WithIds(c)) yield return d;
        }
    }

    // Every case of this page, with what Chrome 153 matches: I for :in-range, O for :out-of-range, - for neither.
    // The r cases are inputs of every type with and without min, max and value, the n cases are elements that have no
    // range, and the e cases are inputs without min and max, which Chrome matches as in range only while they are empty.
    private const string Page = @"
<input id=r0 type=number min=5 value=3>
<input id=r1 type=number min=5 value=7>
<input id=r2 type=number max=5 value=7>
<input id=r3 type=number max=5 value=3>
<input id=r4 type=number min=1 max=10 value=5>
<input id=r5 type=number min=1 max=10>
<input id=r6 type=number min=1 max=10 value="""">
<input id=r7 type=number min=1 max=10 value=abc>
<input id=r8 type=number>
<input id=r9 type=number value=3>
<input id=r10 type=number min=abc value=3>
<input id=r11 type=number max=abc value=3>
<input id=r12 type=number min=5 max=1 value=3>
<input id=r13 type=number min=5 max=1 value=7>
<input id=r14 type=number min=5 max=1 value=5>
<input id=r15 type=number min=5 max=1>
<input id=r16 type=number min=5 max=5 value=5>
<input id=r17 type=number min=5 max=5 value=6>
<input id=r18 type=number min=1 max=10 value=11 step=any>
<input id=r19 type=number min=1 max=10 value=1.5 step=2>
<input id=r20 type=number min="""" max="""" value=3>
<input id=r21 type=number min="" 5"" value=3>
<input id=r22 type=number min=5 value="" 3"">
<input id=r23 type=number min=1e1 value=9>
<input id=r24 type=number min=.5 value=.4>
<input id=r25 type=number min=+5 value=3>
<input id=r26 type=number min=1 max=10 value=10>
<input id=r27 type=number min=1 max=10 value=1>
<input id=r28 type=number min=1 max=10 value=0.999999999>
<input id=r29 type=number min=5 value=3 disabled>
<input id=r30 type=number min=5 value=3 readonly>
<input id=r31 type=number min=5 value=7 disabled>
<input id=r32 type=number max=5 value=7 readonly>
<input id=r33 type=range min=0 max=10 value=20>
<input id=r34 type=range min=0 max=10 value=-5>
<input id=r35 type=range>
<input id=r36 type=range min=5 max=1 value=3>
<input id=r37 type=range min=5 max=1>
<input id=r38 type=range min=0 max=10 step=3 value=10>
<input id=r39 type=range min=0 max=10 disabled>
<input id=r40 type=date min=2020-01-01 value=2019-05-05>
<input id=r41 type=date min=2020-01-01 value=2021-05-05>
<input id=r42 type=date max=2020-01-01 value=2021-05-05>
<input id=r43 type=date min=2020-01-01 max=2020-12-31 value=2020-06-06>
<input id=r44 type=date min=2020-01-01 max=2020-12-31>
<input id=r45 type=date min=2020-01-01 value="""">
<input id=r46 type=date min=2020-01-01 value=garbage>
<input id=r47 type=date min=bad max=2020-12-31 value=2021-01-01>
<input id=r48 type=date min=2020-12-31 max=2020-01-01 value=2020-06-06>
<input id=r49 type=date min=2020-01-01 max=2020-12-31 value=2020-02-30>
<input id=r50 type=date min=2020-01-01 value=2019-5-5>
<input id=r51 type=date min=2020-01-01 value=0001-01-01>
<input id=r52 type=date min=2020-01-01 max=2020-12-31 value=2020-12-31>
<input id=r53 type=date min=2020-01-01 value=2020-01-01>
<input id=r54 type=date min=2020-01-01T10:00 value=2019-01-01>
<input id=r55 type=date min=2020-01-01 step=7 value=2020-01-02>
<input id=r56 type=date>
<input id=r57 type=month min=2020-05 value=2020-03>
<input id=r58 type=month min=2020-05 value=2020-07>
<input id=r59 type=month max=2020-05 value=2020-07>
<input id=r60 type=month min=2020-01 max=2020-12 value=2020-06>
<input id=r61 type=month min=2020-05 value=junk>
<input id=r62 type=month min=2020-05 value=2020-13>
<input id=r63 type=month min=2020-12 max=2020-01 value=2020-06>
<input id=r64 type=month min=2020-05 value="""">
<input id=r65 type=month min=2020-05>
<input id=r66 type=month min=2020-5 value=2020-03>
<input id=r67 type=month min=2020-05-01 value=2020-03>
<input id=r68 type=week min=2020-W10 value=2020-W05>
<input id=r69 type=week min=2020-W10 value=2020-W15>
<input id=r70 type=week max=2020-W10 value=2020-W15>
<input id=r71 type=week min=2020-W01 max=2020-W52 value=2020-W30>
<input id=r72 type=week min=2020-W10 value=2020-W54>
<input id=r73 type=week min=2020-W10 value=2021-W53>
<input id=r74 type=week min=2020-W53 value=2020-W20>
<input id=r75 type=week min=2020-W20 max=2020-W10 value=2020-W15>
<input id=r76 type=week min=2020-W10 value=junk>
<input id=r77 type=week min=2020-W10>
<input id=r78 type=time min=10:00 value=09:00>
<input id=r79 type=time min=10:00 value=11:00>
<input id=r80 type=time max=10:00 value=11:00>
<input id=r81 type=time min=10:00 max=12:00 value=11:00>
<input id=r82 type=time min=10:00 max=12:00 value=13:00>
<input id=r83 type=time min=10:00 max=12:00 value=09:59>
<input id=r84 type=time min=22:00 max=02:00 value=23:00>
<input id=r85 type=time min=22:00 max=02:00 value=01:00>
<input id=r86 type=time min=22:00 max=02:00 value=12:00>
<input id=r87 type=time min=22:00 max=02:00 value=22:00>
<input id=r88 type=time min=22:00 max=02:00 value=02:00>
<input id=r89 type=time min=22:00 max=02:00 value=02:01>
<input id=r90 type=time min=22:00 max=02:00 value=21:59>
<input id=r91 type=time min=22:00 max=02:00>
<input id=r92 type=time min=22:00 max=02:00 value="""">
<input id=r93 type=time min=10:00 value=09:00:30>
<input id=r94 type=time min=10:00:30 value=10:00>
<input id=r95 type=time min=10:00 value=10:00:00.500>
<input id=r96 type=time min=10:00 max=10:00 value=10:00>
<input id=r97 type=time min=10:00 max=10:00 value=11:00>
<input id=r98 type=time min=10:00:00.5 value=10:00>
<input id=r99 type=time min=10:00 value=9:00>
<input id=r100 type=time min=10:00 value=24:00>
<input id=r101 type=time min=10 value=09:00>
<input id=r102 type=time value=09:00>
<input id=r103 type=datetime-local min=2020-01-01T10:00 value=2019-01-01T10:00>
<input id=r104 type=datetime-local min=2020-01-01T10:00 value=2021-01-01T10:00>
<input id=r105 type=datetime-local max=2020-01-01T10:00 value=2021-01-01T10:00>
<input id=r106 type=datetime-local min=2020-01-01T10:00 max=2020-12-31T10:00 value=2020-06-01T10:00>
<input id=r107 type=datetime-local min=2020-01-01T10:00 value="""">
<input id=r108 type=datetime-local min=2020-01-01T10:00 value=""2019-01-01 10:00"">
<input id=r109 type=datetime-local min=2020-01-01T10:00 value=junk>
<input id=r110 type=datetime-local min=2020-01-01 value=2019-01-01T10:00>
<input id=r111 type=datetime-local min=2020-12-31T10:00 max=2020-01-01T10:00 value=2020-06-01T10:00>
<input id=r112 type=datetime-local min=""2020-01-01 10:00"" value=2019-01-01T10:00>
<input id=r113 type=datetime-local min=2020-01-01T10:00 value=2020-01-01T10:00>
<input id=r114 type=datetime-local min=2020-01-01T10:00 value=2020-01-01T09:59:59>
<input id=r115 type=text min=5 max=1 value=3>
<input id=r116 type=text>
<input id=r117 type=email>
<input id=r118 type=url>
<input id=r119 type=tel>
<input id=r120 type=search>
<input id=r121 type=password>
<input id=r122 type=checkbox>
<input id=r123 type=radio>
<input id=r124 type=color>
<input id=r125 type=file>
<input id=r126 type=hidden>
<input id=r127 type=submit>
<input id=r128 type=button>
<input id=r129 type=reset>
<input id=r130 type=image>
<input id=r131 type=bogus min=5 value=1>
<div id=n0>x</div>
<select id=n1><option>a</option></select>
<textarea id=n2></textarea>
<button id=n3 min=5>b</button>
<progress id=n4 value=5 max=10></progress>
<meter id=n5 value=50 min=0 max=10></meter>
<form id=n7><input type=number min=5 value=3></form>
<datalist><input id=n11 type=number min=5 value=3></datalist>
<input id=e0 type=number value="""">
<input id=e1 type=number>
<input id=e2 type=number value=3>
<input id=e3 type=date>
<input id=e4 type=date value=2020-01-01>
<input id=e5 type=time>
<input id=e6 type=week>
<input id=e7 type=number required value=3>
<input id=e8 type=number min=1 required>
";

    private const string Chrome = "r0=O r1=I r2=O r3=I r4=I r5=I r6=I r7=I r8=I r9=- r10=- r11=- r12=O r13=O r14=O r15=I r16=I r17=O r18=O r19=I r20=- r21=- r22=I r23=O r24=O r25=- r26=I r27=I r28=O r29=- r30=- r31=- r32=- r33=I r34=I r35=I r36=I r37=I r38=I r39=- r40=O r41=I r42=O r43=I r44=I r45=I r46=I r47=O r48=O r49=I r50=I r51=O r52=I r53=I r54=- r55=I r56=I r57=O r58=I r59=O r60=I r61=I r62=I r63=O r64=I r65=I r66=- r67=- r68=O r69=I r70=O r71=I r72=I r73=I r74=O r75=O r76=I r77=I r78=O r79=I r80=O r81=I r82=O r83=O r84=I r85=I r86=O r87=I r88=I r89=O r90=O r91=I r92=I r93=O r94=O r95=I r96=I r97=O r98=O r99=I r100=I r101=- r102=- r103=O r104=I r105=O r106=I r107=I r108=O r109=I r110=- r111=O r112=O r113=I r114=O r115=- r116=- r117=- r118=- r119=- r120=- r121=- r122=- r123=- r124=- r125=- r126=- r127=- r128=- r129=- r130=- r131=- n0=- n1=- n2=- n3=- n4=- n5=- n7=- n11=- e0=I e1=I e2=- e3=I e4=- e5=I e6=I e7=- e8=I";

    private static string States(string html)
    {
        // Styling fills in the ids and sibling indexes that selectors read.
        var doc = HtmlParser.Parse(html);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        return string.Join(" ", WithIds(doc).Select(e => e.Id + "=" + (InRange.Matches(e) ? "I" : OutOfRange.Matches(e) ? "O" : "-")));
    }

    [Fact]
    public void Every_case_matches_as_in_Chrome() => Assert.Equal(Chrome, States(Page));

    [Fact]
    public void Disabled_and_read_only_inputs_match_neither()
        => Assert.Equal("a=- b=- c=- d=- e=- f=- g=-", States(
            "<input id=a type=number min=5 value=3 disabled><input id=b type=date min=2020-01-01 value=2019-01-01 readonly>"
            + "<input id=c type=range readonly><fieldset disabled><input id=d type=number min=5 value=3></fieldset><input id=e type=range disabled>"
            + "<input id=f type=range readonly max=3 value=9><input id=g type=' number' value=11 max=10>"));

    [Fact]
    public void Rules_with_the_selectors_apply()
    {
        var html = "<style>input{color:black} input:in-range{color:green} input:out-of-range{color:red}</style><input type=number min=1 max=3 value=2><input type=number min=1 max=3 value=9>";
        PdfTestHelpers.AssertStructurallyValid(HtmlToPdf.Convert(html));
        Assert.NotNull(Selector.Parse("input:not(:in-range)"));
        Assert.NotNull(Selector.Parse("input:is(:out-of-range, :disabled)"));
    }

    [Fact]
    public void Supports_reports_the_selectors()
        => Assert.True(CssSupports.Evaluate("selector(:in-range) and selector(:out-of-range)"));
}

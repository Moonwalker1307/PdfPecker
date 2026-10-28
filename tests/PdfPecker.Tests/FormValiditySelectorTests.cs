using PdfPecker.Css;
using PdfPecker.Html;

namespace PdfPecker.Tests;

/// <summary>:valid, :invalid and :open match the elements Chrome matches in a page nobody has typed into.</summary>
public class FormValiditySelectorTests
{
    private static readonly Selector Valid = Selector.Parse(":valid")!, Invalid = Selector.Parse(":invalid")!, Open = Selector.Parse(":open")!;

    private static IEnumerable<Element> WithIds(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            if (c.Id != null) yield return c;
            foreach (var d in WithIds(c)) yield return d;
        }
    }

    // Every case of this page, with what Chrome 153 matches: V for :valid, I for :invalid, O for :open, - for none.
    private const string Page = @"<form id=f1>
<input id=t1 value=x>
<input id=t2 required>
<input id=t3 required value=a>
<input id=t4 required disabled>
<input id=t5 required readonly>
<input id=t6 type=hidden required>
<input id=t7 type=email value=""a@b"">
<input id=t8 type=email value=""nope"">
<input id=t9 type=email multiple value=""a@b.c, x"">
<input id=t10 type=url value=""example.com"">
<input id=t11 type=url value=""http://x"">
<input id=t12 pattern=""[0-9]+"" value=""12a"">
<input id=t13 pattern=""[0-9]+"" value=""12"">
<input id=t14 pattern=""[0-9]+"" value="""">
<input id=t15 type=number min=5 value=3>
<input id=t16 type=number max=5 value=7>
<input id=t17 type=number step=2 value=3>
<input id=t18 type=number value=abc required>
<input id=t19 type=range min=0 max=10 value=20>
<input id=t20 type=date min=""2020-01-01"" value=""2019-05-05"">
<input id=t21 type=checkbox required>
<input id=t22 type=checkbox required checked>
<input id=t23 type=radio name=r required><input id=t24 type=radio name=r checked>
<input id=t25 type=radio name=q required><input id=t26 type=radio name=q>
<input id=t27 maxlength=2 value=abcd>
<input id=t28 minlength=5 value=ab>
<input id=t29 type=file required>
<input id=t30 type=color required>
<input id=t31 type=submit>
<input id=t32 type=button>
<input id=t33 type=reset>
<button id=b1>b</button><button id=b2 type=button>b</button><button id=b3 type=reset>b</button>
<textarea id=a1 required></textarea><textarea id=a2 required>x</textarea>
<select id=s1 required><option value="""">Pick</option><option>A</option></select>
<select id=s2 required><option>A</option></select>
<select id=s3 required><option value="""">Pick</option><option selected>A</option></select>
<select id=s4 required multiple><option>A</option></select>
<select id=s5 required size=3><option value="""">x</option></select>
<select id=s6 required><optgroup><option value="""">Pick</option></optgroup><option>A</option></select>
<fieldset id=fs1><input required></fieldset>
<fieldset id=fs2><input></fieldset>
<output id=o1>x</output>
<input id=t34 type=number min=1 max=10 value=5 step=any>
<input id=t35 type=time min=""10:00"" value=""09:00"">
<input id=t36 pattern=""a|b"" value=""ab"">
<input id=t37 type=text required value=""  "">
<input id=t38 type=email required value="""">
<input id=t39 type=number step=0.5 min=0.25 value=0.75>
<input id=t40 type=number step=2 value=3 min=1>
<object id=ob1></object>
<input id=t41 type=search pattern=""\d+"" value=""5"">
<input id=t42 required list=x value="""">
</form>
<form id=f2><input value=x></form>
<details id=d1 open><summary>s</summary>x</details><details id=d2><summary>s</summary>x</details>
<dialog id=g1 open>d</dialog><dialog id=g2>d</dialog>
<select id=sx><option>a</option></select>
<input id=ix type=date>
";

    private const string Chrome = "f1=I t1=V t2=I t3=V t4=- t5=- t6=- t7=V t8=I t9=I t10=I t11=V t12=I t13=V t14=V t15=I t16=I t17=V t18=I t19=V t20=I t21=I t22=V t23=V t24=V t25=I t26=I t27=V t28=V t29=I t30=V t31=V t32=- t33=- b1=V b2=- b3=- a1=I a2=V s1=I s2=V s3=V s4=I s5=I s6=V fs1=I fs2=V o1=- t34=V t35=I t36=I t37=V t38=I t39=V t40=V ob1=- t41=V t42=I f2=V d1=O d2=- g1=O g2=- sx=V ix=V";

    [Fact]
    public void Every_case_matches_as_in_Chrome()
    {
        // Styling fills in the ids and sibling indexes that selectors read.
        var doc = HtmlParser.Parse(Page);
        new StyleResolver(new Stylesheet(), 500, 800).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var expected = Chrome.Split(' ').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        var actual = new List<string>();
        foreach (var e in WithIds(doc))
        {
            string state = Open.Matches(e) ? "O" : Valid.Matches(e) ? "V" : Invalid.Matches(e) ? "I" : "-";
            actual.Add(e.Id + "=" + state);
        }
        Assert.Equal(string.Join(" ", expected.Select(p => p.Key + "=" + p.Value)), string.Join(" ", actual));
    }
}

/// <summary>Forms that make :valid and :invalid expensive: many radio groups, deep fieldsets, slow patterns.</summary>
[Collection(HostileInputCollection.Name)]
public class FormValidityHostileInputTests
{
    [Fact]
    public void Large_forms_and_slow_patterns_stay_bounded()
    {
        var html = "<style>:valid{color:green} :invalid{color:red} form :invalid ~ :valid{color:blue}</style><form>"
            + string.Concat(Enumerable.Range(0, 5000).Select(i => $"<input type=radio name=r{i} required>"))
            + string.Concat(Enumerable.Repeat("<fieldset>", 2000)) + "<input required>" + string.Concat(Enumerable.Repeat("</fieldset>", 2000))
            + string.Concat(Enumerable.Range(0, 200).Select(i => $"<input pattern='(a+)+\\1b{i}' value='{new string('a', 40)}'>"))
            + string.Concat(Enumerable.Range(0, 200).Select(i => $"<input pattern='(a|aa){{1,{1000 + i}}}(((' value='a'><input pattern='{new string('(', 3000)}' value=x>"))
            + "<input type=number min=1e308 max=-1e308 step=1e-308 value=1e308><input type=week value=2026-W53 min=0000-W01>"
            + "<input type=datetime-local value='2026-02-29T25:61' required><input type=time min=23:00 max=01:00 value=12:00:00.1234>"
            + "</form>";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PdfTestHelpers.AssertStructurallyValid(HtmlToPdf.Convert(html, null, cts.Token));
    }
}

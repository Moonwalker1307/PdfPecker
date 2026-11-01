using System.Xml.Linq;
using PdfPecker.Fluent.Html;

namespace PdfPecker.Fluent.Tests;

/// <summary>Charts, which the fluent layer draws as SVG images.</summary>
public sealed class ChartTests
{
    private static string Svg(Action<ChartDescriptor> build)
    {
        var chart = new ChartDescriptor();
        build(chart);
        return chart.ToSvg();
    }

    [Fact]
    public void A_chart_is_an_svg_image_with_a_text_alternative()
    {
        string html = Html.Item(c => c.Chart(chart =>
        {
            chart.Categories("Q1", "Q2");
            chart.Series("2025", new[] { 1, 2 });
            chart.Series("2026", new[] { 3, 4 });
        }));
        Assert.StartsWith("<img class=\"c1\" src=\"fluent:img/0\"", html, StringComparison.Ordinal);
        Assert.EndsWith("alt=\"Column chart: 2025, 2026\">", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"Sales\"", Html.Item(c => c.Chart(chart => chart.Type(ChartType.Pie).Series("x", new[] { 1.0 })).AlternativeText("Sales")));
    }

    [Fact]
    public void Each_type_draws_its_labels_and_values_into_the_pdf()
    {
        foreach (var type in Enum.GetValues<ChartType>())
        {
            string text = Html.Text(Html.Page(c => c.Chart(chart =>
            {
                chart.Type(type).DataLabels();
                chart.Categories("North", "South");
                chart.Series("Sales", new[] { 30.5, 69.5 });
            })).GeneratePdf());
            Assert.Contains("North", text);
            Assert.Contains("South", text);
            Assert.Contains(type == ChartType.Pie ? "70%" : "69.5", text);
        }
    }

    [Fact]
    public void Columns_and_bars_grow_from_zero_and_negative_values_go_the_other_way()
    {
        var doc = XDocument.Parse(Svg(chart => chart.Size(200, 100).Legend(false).Series("s", new[] { 10.0, -5.0 })));
        XNamespace ns = "http://www.w3.org/2000/svg";
        var bars = doc.Descendants(ns + "rect").ToList();
        Assert.Equal(2, bars.Count);
        float Y(XElement e) => float.Parse(e.Attribute("y")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        float H(XElement e) => float.Parse(e.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        // The positive bar ends where the negative one starts: on the zero line, which is the axis colour.
        Assert.Equal(Y(bars[0]) + H(bars[0]), Y(bars[1]), 2);
        Assert.Equal(2 * H(bars[1]), H(bars[0]), 1);
        Assert.Contains(doc.Descendants(ns + "line"), l => l.Attribute("stroke")!.Value == "#9e9e9e" && Math.Abs(float.Parse(l.Attribute("y1")!.Value, System.Globalization.CultureInfo.InvariantCulture) - Y(bars[1])) < 0.01);
    }

    [Theory]
    [InlineData(0, 22, 0, 25, 5)]
    [InlineData(-20, 143, -50, 150, 50)]
    [InlineData(0, 0, 0, 1, 0.2)]
    [InlineData(0, 0.7, 0, 0.8, 0.2)]
    [InlineData(0, 1900, 0, 2000, 500)]
    public void The_value_axis_has_round_steps(double min, double max, double lo, double hi, double step)
    {
        var scale = ChartSvg.NiceScale(min, max);
        Assert.Equal(lo, scale.Lo, 6);
        Assert.Equal(hi, scale.Hi, 6);
        Assert.Equal(step, scale.Step, 6);
    }

    [Fact]
    public void Caller_text_stays_text_in_the_drawing()
    {
        const string evil = "</text><script>x</script>&\"\u0001\uD800";
        string svg = Svg(chart =>
        {
            chart.Categories(evil, evil);
            chart.Series(evil, new[] { 1.0, 2.0 });
            chart.DataLabels();
            chart.Legend();
        });
        var doc = XDocument.Parse(svg);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "script");
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "text" && e.Value.StartsWith("</text>", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_and_odd_charts_still_draw()
    {
        foreach (var type in Enum.GetValues<ChartType>())
        {
            XDocument.Parse(Svg(chart => chart.Type(type)));
            XDocument.Parse(Svg(chart => chart.Type(type).Series("a", Array.Empty<double>())));
            XDocument.Parse(Svg(chart => chart.Type(type).DataLabels().Series("a", new[] { 0.0, -1.0 })));
            XDocument.Parse(Svg(chart => chart.Type(type).Size(50, 50).FontSize(200).Categories("a", "b").Series("a", new[] { 1e300, -1e300 })));
            XDocument.Parse(Svg(chart => chart.Type(type).Series("a", new[] { 1e-300, 2e-300 })));
        }
    }

    [Fact]
    public void Bad_chart_arguments_throw_argument_exceptions()
    {
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.Series("a", new[] { double.NaN })));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.Series("a", new double[1001])));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.Categories(new string[1001])));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => { for (int i = 0; i < 101; i++) c.Series("a", new[] { 1.0 }); }));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.ValueFormat("")));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.FontFamily("a\"b")));
        Assert.ThrowsAny<ArgumentException>(() => Svg(c => c.FontFamily()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Svg(c => c.Size(10, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Svg(c => c.FontSize(float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Svg(c => c.Type((ChartType)7)));
    }
}

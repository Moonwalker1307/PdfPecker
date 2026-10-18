using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Pdf;

namespace PdfPecker.Tests;

/// <summary>
/// Fixed dates and content-derived file identifiers. The tests change <c>SOURCE_DATE_EPOCH</c>, which the whole process
/// sees, so they run in a collection of their own after the parallel tests.
/// </summary>
[Collection(ReproducibleOutputCollection.Name)]
public class ReproducibleOutputTests
{
    private const string Html = "<html><head><title>Report</title></head><body><h1>Totals</h1><p>Sum: 42</p></body></html>";
    private static readonly DateTimeOffset Fixed = new(2024, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2));

    private static string Text(byte[] pdf) => PdfTestHelpers.Latin1(pdf);

    private static string Id(byte[] pdf) => Regex.Match(Text(pdf), @"/ID \[<([0-9A-Fa-f]+)><([0-9A-Fa-f]+)>\]").Groups[1].Value;

    private static string CreationDate(byte[] pdf) => Regex.Match(Text(pdf), @"/CreationDate \(([^)]*)\)").Groups[1].Value;

    /// <summary>Runs <paramref name="action"/> with the environment variable set (or removed, for null), then restores it.</summary>
    private static void WithEpoch(string? value, Action action)
    {
        var saved = Environment.GetEnvironmentVariable(ReproducibleDate.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, value);
        try { action(); }
        finally { Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, saved); }
    }

    [Fact]
    public void Same_creation_date_gives_byte_identical_output() => WithEpoch(null, () =>
    {
        var a = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        var b = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        Assert.Equal(a, b);
        // UTC, to the second.
        Assert.Equal("D:20240102010405Z", CreationDate(a));
        Assert.Equal(32, Id(a).Length);

        // The identifier follows the content.
        var other = HtmlToPdf.Convert(Html.Replace("42", "43", StringComparison.Ordinal), new PdfOptions { CreationDate = Fixed });
        Assert.NotEqual(Id(a), Id(other));
    });

    [Fact]
    public void Fixed_date_reaches_xmp_metadata_and_attachments() => WithEpoch(null, () =>
    {
        PdfOptions Options() => new()
        {
            CreationDate = Fixed,
            Compress = false,
            Conformance = PdfConformance.PdfA3b,
            Attachments = { new PdfAttachment("data.csv", "a,b\n1,2\n"u8.ToArray()) },
        };
        var a = HtmlToPdf.Convert(Html, Options());
        var b = HtmlToPdf.Convert(Html, Options());
        Assert.Equal(a, b);
        var text = Text(a);
        Assert.Contains("<xmp:CreateDate>2024-01-02T01:04:05Z</xmp:CreateDate>", text, StringComparison.Ordinal);
        Assert.Contains("<xmp:ModifyDate>2024-01-02T01:04:05Z</xmp:ModifyDate>", text, StringComparison.Ordinal);
        // The attachment has no date of its own, so it gets the document's.
        Assert.Matches(@"/Params << /Size \d+ /ModDate \(D:20240102010405", text);
    });

    [Fact]
    public void Markdown_with_a_fixed_date_is_reproducible() => WithEpoch(null, () =>
    {
        var a = MarkdownToPdf.Convert("# Title\n\nSome *text*.", new PdfOptions { CreationDate = Fixed });
        var b = MarkdownToPdf.Convert("# Title\n\nSome *text*.", new PdfOptions { CreationDate = Fixed });
        Assert.Equal(a, b);
    });

    [Fact]
    public void Source_date_epoch_fixes_the_date_and_identifier() => WithEpoch("1700000000", () =>
    {
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.Equal(a, b);
        Assert.Equal("D:20231114221320Z", CreationDate(a));

        // The option takes precedence over the environment.
        var c = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        Assert.Equal("D:20240102010405Z", CreationDate(c));
    });

    [Fact]
    public void Invalid_source_date_epoch_is_ignored() => WithEpoch("yesterday", () =>
    {
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.NotEqual(Id(a), Id(b));
    });

    [Fact]
    public void Default_output_keeps_current_date_and_random_identifier() => WithEpoch(null, () =>
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.NotEqual(Id(a), Id(b));
        var date = DateTime.ParseExact(CreationDate(a), "'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.InRange(date, before, DateTime.UtcNow.AddSeconds(2));
    });

    [Fact]
    public void Fixed_date_is_written_the_same_under_any_culture() => WithEpoch(null, () =>
    {
        var invariant = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(invariant, HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed }));
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Assert.Equal(invariant, HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed }));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    });

    [Theory]
    [InlineData("0", "1970-01-01T00:00:00")]
    [InlineData(" 1700000000 ", "2023-11-14T22:13:20")]
    [InlineData("253402300799", "9999-12-31T23:59:59")]
    [InlineData("253402300800", null)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_source_date_epoch(string? value, string? expected)
    {
        var parsed = ReproducibleDate.ParseEpoch(value);
        Assert.Equal(expected, parsed?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        if (parsed != null) Assert.Equal(DateTimeKind.Utc, parsed.Value.Kind);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReproducibleOutputCollection
{
    public const string Name = "Reproducible output";
}

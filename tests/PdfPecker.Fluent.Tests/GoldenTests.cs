using System.Globalization;
using System.Runtime.CompilerServices;
using FluentExamples;

namespace PdfPecker.Fluent.Tests;

/// <summary>
/// Each example's HTML and PDF against a checked-in baseline, so any change to the generated markup or to the PDF
/// PdfPecker makes of it shows up in review. Set PDFPECKER_UPDATE_GOLDEN=1 to rewrite the baselines, and say in the
/// commit which ones changed and why.
/// </summary>
public sealed class GoldenTests
{
    public static TheoryData<string> Names => [.. Examples.All.Keys];

    private static string GoldenDirectory([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Golden");

    private static bool Update => Environment.GetEnvironmentVariable("PDFPECKER_UPDATE_GOLDEN") == "1";

    [Theory]
    [MemberData(nameof(Names))]
    public void Html_matches_the_baseline(string name)
    {
        string html = DocumentGenerator.Html(Examples.All[name](), dataUris: false, out _);
        string path = Path.Combine(GoldenDirectory(), name + ".expected.html");
        if (Update) File.WriteAllText(path, html);
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), html);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Pdf_matches_the_baseline(string name)
    {
        byte[] pdf = Examples.All[name]().GeneratePdf();
        string path = Path.Combine(GoldenDirectory(), name + ".expected.pdf");
        if (Update) File.WriteAllBytes(path, pdf);
        Assert.Null(PdfCompare.FirstDifference(File.ReadAllBytes(path), pdf));
    }

    /// <summary>
    /// Arabic and Persian cultures use other minus signs and digits, and Danish a decimal comma: every number the
    /// package writes must come out the same under them.
    /// </summary>
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("da-DK")]
    public void Output_does_not_depend_on_the_culture(string culture)
    {
        var expected = Examples.All.ToDictionary(e => e.Key, e => (Html: DocumentGenerator.Html(e.Value(), false, out _), Pdf: e.Value().GeneratePdf()));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            foreach (var (name, create) in Examples.All)
            {
                Assert.Equal(expected[name].Html, DocumentGenerator.Html(create(), false, out _));
                Assert.Null(PdfCompare.FirstDifference(expected[name].Pdf, create().GeneratePdf()));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = previous;
        }
    }
}

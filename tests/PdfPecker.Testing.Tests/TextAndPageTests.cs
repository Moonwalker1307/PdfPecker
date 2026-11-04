using PdfPecker.Testing;

namespace PdfPecker.Testing.Tests;

public sealed class TextAndPageTests
{
    private static readonly byte[] TwoPages = Pdfs.Make("""
        <h1>Invoice 2026-104</h1>
        <p>Total
           due: 1,500.00</p>
        <p style="break-before: page">Payment terms: 30 days</p>
        """);

    [Fact]
    public void Text_is_found_with_white_space_runs_as_one_space()
    {
        PdfAssert.ContainsText(TwoPages, "Invoice 2026-104");
        PdfAssert.ContainsText(TwoPages, "Total due:   1,500.00");
        PdfAssert.ContainsText(TwoPages, "Payment terms", 2);
    }

    [Fact]
    public void Missing_text_fails_with_the_start_of_the_text()
    {
        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.ContainsText(TwoPages, "Total due: 1,900.00"));
        Assert.Contains("Invoice 2026-104", e.Message, StringComparison.Ordinal);
        Assert.Throws<PdfAssertException>(() => PdfAssert.ContainsText(TwoPages, "Payment terms", 1));
        var noPage = Assert.Throws<PdfAssertException>(() => PdfAssert.ContainsText(TwoPages, "Payment terms", 3));
        Assert.Contains("2 pages", noPage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_must_not_be_there()
    {
        PdfAssert.DoesNotContainText(TwoPages, "{{customer}}");
        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.DoesNotContainText(TwoPages, "1,500.00"));
        Assert.Contains("due: 1,500.00", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_count()
    {
        PdfAssert.HasPageCount(TwoPages, 2);
        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.HasPageCount(TwoPages, 1));
        Assert.Equal("The PDF has 2 pages, not 1.", e.Message);
    }

    [Fact]
    public void Unreadable_input_fails_as_an_assertion()
    {
        var garbage = "not a pdf"u8.ToArray();
        Assert.Throws<PdfAssertException>(() => PdfAssert.HasPageCount(garbage, 1));
        Assert.Throws<PdfAssertException>(() => PdfAssert.ContainsText(garbage, "x"));
        Assert.Throws<PdfAssertException>(() => PdfAssert.ContainsText(garbage, "x", 1));
        Assert.Throws<PdfAssertException>(() => PdfAssert.DoesNotContainText(garbage, "x"));
    }

    [Fact]
    public void Bad_arguments_throw_argument_exceptions()
    {
        Assert.Throws<ArgumentNullException>(() => PdfAssert.ContainsText(null!, "x"));
        Assert.Throws<ArgumentException>(() => PdfAssert.ContainsText(TwoPages, ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfAssert.ContainsText(TwoPages, "x", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfAssert.HasPageCount(TwoPages, -1));
    }
}

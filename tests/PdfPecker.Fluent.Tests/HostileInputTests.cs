using FluentExamples;

using System.Diagnostics;

namespace PdfPecker.Fluent.Tests;

/// <summary>Timed tests run alone, so a slow machine running other tests at the same time cannot fail them.</summary>
[CollectionDefinition(nameof(HostileInputCollection), DisableParallelization = true)]
public sealed class HostileInputCollection;

/// <summary>
/// Caller text is data, never markup or CSS: whatever goes into a text, a font name, a section name, an alternative
/// text or the metadata comes out as the same text, and the document around it stays intact.
/// </summary>
[Collection(nameof(HostileInputCollection))]
public sealed class HostileInputTests
{
    public static TheoryData<string> Hostile =>
    [
        "<x-evil>tag</x-evil>",
        "</p></main><x-evil>",
        "\"><x-evil a='",
        "'><x-evil>",
        "</style><x-evil>",
        "&lt;x-evil&gt; &amp;",
        "<!-- <x-evil> -->",
        "<![CDATA[<x-evil>]]>",
        "<script>alert(1)</script><x-evil>",
        "\0\u0001\u001f\u007f\u0085 controls <x-evil>",
        "\ud800 lone \udfff surrogates <x-evil>",
        "{page} {total-pages} {pages} {{page}} <x-evil>",
        "\r\n\r crlf <x-evil>",
        "} ; } a{color:red} <x-evil>",
        "\\\"\\'\\<x-evil>",
    ];

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Text_never_becomes_markup(string value)
    {
        var document = Document.Create(d => d.Page(p =>
        {
            p.Header().Text(value);
            p.Content().Column(c =>
            {
                c.Item().Text(value).FontFamily(value);
                c.Item().Text(t =>
                {
                    t.Span(value);
                    t.Hyperlink(value, "https://example.com/?q=" + Uri.EscapeDataString(value));
                    t.SectionLink(value, value);
                    t.BeginPageNumberOfSection(value);
                });
                c.Item().List(l =>
                {
                    l.Marker(value.Length > 20 ? value[..20] : value);
                    l.Item().SemanticImage(value).Image(Placeholders.Image(4, 4));
                });
                c.Item().Decoration(d => { d.Before().Text(value); d.Content().Text(value); });
                c.Item().Section(value).Text("target");
                c.Item().Image(File.ReadAllBytes(Examples.AssetPath("logo.png"))).AlternativeText(value);
            });
            p.Footer().Text(t => { t.Span(value); t.CurrentPageNumber(); });
        })).WithMetadata(new DocumentMetadata { Title = value, Language = value, Author = value });

        string html = DocumentGenerator.Html(document, false, out _).Single();
        Assert.DoesNotContain("<x-evil", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Equal(1, html.Split("</style>").Length - 1);
        Assert.Equal(1, html.Split("</main>").Length - 1);
        Assert.DoesNotContain('\0', html);

        byte[] pdf = document.GeneratePdf();
        Assert.Contains("x-evil", Html.Text(pdf));
    }

    [Fact]
    public void Braces_in_header_text_stay_literal()
    {
        byte[] pdf = Document.Create(d => d.Page(p =>
        {
            p.Header().Text("literal {page} and {total-pages}");
            p.Content().Text("body");
            p.Footer().Text(t => { t.Span("{"); t.Span("page}"); t.Span(" real "); t.CurrentPageNumber(); });
        })).GeneratePdf();
        string text = Html.Text(pdf);
        Assert.Contains("literal {page} and {total-pages}", text);
        Assert.Contains("{page} real 1", text);
    }

    [Fact]
    public void Deep_nesting_fails_with_a_documented_exception()
    {
        var document = Html.Page(c =>
        {
            for (int i = 0; i < 1000; i++) c = c.Padding(1);
            c.Text("deep");
        });
        Assert.Throws<InvalidOperationException>(() => document.GeneratePdf());
    }

    [Fact]
    public void Deeply_nested_columns_fail_with_a_documented_exception()
    {
        static void Nest(IContainer container, int depth)
        {
            if (depth == 0) container.Text("x");
            else container.Column(c => Nest(c.Item(), depth - 1));
        }
        Assert.Throws<InvalidOperationException>(() => Html.Page(c => Nest(c, 500)).GenerateHtml());
    }

    [Fact]
    public void A_huge_span_fails_fast_instead_of_allocating()
    {
        var watch = Stopwatch.StartNew();
        var document = Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => { for (int i = 0; i < 1000; i++) d.RelativeColumn(); });
            t.Cell().Row(100_000).RowSpan(100_000).ColumnSpan(1000).Text("x");
        }));
        Assert.Throws<InvalidOperationException>(() => document.GenerateHtml());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public void A_span_wider_than_the_table_fails()
        => Assert.Throws<InvalidOperationException>(() => Html.Page(c => c.Table(t =>
        {
            t.ColumnsDefinition(d => d.RelativeColumn());
            t.Cell().ColumnSpan(2).Text("x");
        })).GenerateHtml());

    [Fact]
    public void Raw_html_is_the_one_call_that_is_not_escaped()
        => Assert.Contains("<b>bold</b>", Html.Of(c => c.Html("<b>bold</b>")));

    [Fact]
    public void Svg_is_drawn_as_an_image_not_inserted_as_markup()
    {
        string html = Html.Of(c => c.Svg("<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><x-evil/></svg>"));
        Assert.DoesNotContain("<svg", html);
        Assert.StartsWith("<main><img", html);
    }

    [Fact]
    public void A_damaged_image_does_not_fail_the_document()
    {
        byte[] damaged = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4, 5, 6, 7, 8];
        var pdf = Document.Create(d => d.Page(p => p.Content().Column(c =>
        {
            c.Item().Image(damaged);
            c.Item().Image(new byte[1000]);
            c.Item().Text("after");
        }))).GeneratePdf();
        Assert.True(pdf.Length > 0);
    }
}

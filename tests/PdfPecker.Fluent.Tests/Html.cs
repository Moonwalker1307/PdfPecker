namespace PdfPecker.Fluent.Tests;

/// <summary>Helpers that build a one-page document and return the body of the HTML it makes.</summary>
internal static class Html
{
    public static Document Page(Action<IContainer> content) => Document.Create(d => d.Page(p => content(p.Content())));

    /// <summary>The &lt;main&gt; element, the page content slot.</summary>
    public static string Of(Action<IContainer> content) => Body(DocumentGenerator.Html(Page(content), false, out _).Single());

    /// <summary>
    /// The HTML of one column item, where no page slot is around to take the first styles: the column's own element
    /// gets class c0, so the item's classes start at c1.
    /// </summary>
    public static string Item(Action<IContainer> content)
    {
        string main = Of(c => c.Column(col => content(col.Item())));
        const string open = "<main><div class=\"c0\">", close = "</div></main>";
        return main[open.Length..^close.Length];
    }

    public static string Body(string html)
    {
        int start = html.IndexOf("<main", StringComparison.Ordinal);
        int end = html.LastIndexOf("</main>", StringComparison.Ordinal) + 7;
        return html[start..end];
    }

    /// <summary>The CSS rules, so a test can check what a class holds.</summary>
    public static string Css(IDocument document)
    {
        string html = DocumentGenerator.Html(document, false, out _).Single();
        int start = html.IndexOf("<style>", StringComparison.Ordinal);
        return html[(start + 7)..html.IndexOf("</style>", StringComparison.Ordinal)];
    }

    public static string Text(byte[] pdf) => PdfText.Extract(pdf);
}

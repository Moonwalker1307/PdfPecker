namespace PdfPecker;

/// <summary>
/// Converts Markdown to PDF: <see cref="MarkdownToHtml"/> followed by <see cref="HtmlToPdf"/>, all in this library.
/// </summary>
/// <remarks>All methods are static and thread-safe.</remarks>
public static class MarkdownToPdf
{
    /// <summary>Converts a Markdown string to a PDF document.</summary>
    public static byte[] Convert(string markdown, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
    {
        var html = MarkdownToHtml.ConvertDocument(markdown, markdownOptions);
        return HtmlToPdf.Convert(html, pdfOptions);
    }

    /// <summary>Converts a Markdown string and writes the PDF to <paramref name="output"/>.</summary>
    public static void Convert(string markdown, Stream output, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        var bytes = Convert(markdown, pdfOptions, markdownOptions);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Converts a Markdown file to a PDF file. Relative image paths resolve against the Markdown file's folder.</summary>
    public static void ConvertFile(string markdownPath, string pdfPath, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
    {
        var html = MarkdownToHtml.ConvertDocument(File.ReadAllText(markdownPath), markdownOptions);
        pdfOptions ??= new PdfOptions();
        var baseDir = pdfOptions.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(markdownPath));
        File.WriteAllBytes(pdfPath, HtmlToPdf.Render(html, pdfOptions, baseDir));
    }

    /// <summary>
    /// Converts a Markdown string to a PDF asynchronously on the thread pool. Cancelling the token also stops a conversion
    /// that has already started, and signing, as in <see cref="HtmlToPdf.ConvertAsync"/>.
    /// </summary>
    public static Task<byte[]> ConvertAsync(string markdown, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null,
        CancellationToken cancellationToken = default)
        => HtmlToPdf.RenderAsync(() => MarkdownToHtml.ConvertDocument(markdown, markdownOptions), pdfOptions ?? new PdfOptions(), cancellationToken);
}

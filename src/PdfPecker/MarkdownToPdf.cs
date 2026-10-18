namespace PdfPecker;

/// <summary>
/// Converts Markdown to PDF: <see cref="MarkdownToHtml"/> followed by <see cref="HtmlToPdf"/>, all in this library.
/// </summary>
/// <remarks>All methods are static and thread-safe.</remarks>
public static class MarkdownToPdf
{
    /// <summary>Converts a Markdown string to a PDF document.</summary>
    public static byte[] Convert(string markdown, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
        => Convert(markdown, pdfOptions, markdownOptions, CancellationToken.None);

    /// <summary>
    /// Converts a Markdown string to a PDF document, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <param name="pdfOptions">Page, font and output settings; null for the defaults.</param>
    /// <param name="markdownOptions">Markdown extensions and styling; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    public static byte[] Convert(string markdown, PdfOptions? pdfOptions, MarkdownOptions? markdownOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var html = MarkdownToHtml.ConvertDocument(markdown, markdownOptions);
        return HtmlToPdf.Convert(html, pdfOptions, cancellationToken);
    }

    /// <summary>Converts a Markdown string and writes the PDF to <paramref name="output"/>.</summary>
    public static void Convert(string markdown, Stream output, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
        => Convert(markdown, output, pdfOptions, markdownOptions, CancellationToken.None);

    /// <summary>
    /// Converts a Markdown string and writes the PDF to <paramref name="output"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled. Nothing is written
    /// to <paramref name="output"/> unless the conversion completes.
    /// </summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <param name="output">Where the PDF is written; it is not flushed or closed.</param>
    /// <param name="pdfOptions">Page, font and output settings; null for the defaults.</param>
    /// <param name="markdownOptions">Markdown extensions and styling; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    public static void Convert(string markdown, Stream output, PdfOptions? pdfOptions, MarkdownOptions? markdownOptions, CancellationToken cancellationToken)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        var bytes = Convert(markdown, pdfOptions, markdownOptions, cancellationToken);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Converts a Markdown file to a PDF file. Relative image paths resolve against the Markdown file's folder.</summary>
    public static void ConvertFile(string markdownPath, string pdfPath, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null)
        => ConvertFile(markdownPath, pdfPath, pdfOptions, markdownOptions, CancellationToken.None);

    /// <summary>
    /// Converts a Markdown file to a PDF file, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled; the output file is then left as it was. Relative image paths
    /// resolve against the Markdown file's folder.
    /// </summary>
    /// <param name="markdownPath">The Markdown file.</param>
    /// <param name="pdfPath">The PDF file to write; it appears only once the conversion has succeeded.</param>
    /// <param name="pdfOptions">Page, font and output settings; null for the defaults.</param>
    /// <param name="markdownOptions">Markdown extensions and styling; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    public static void ConvertFile(string markdownPath, string pdfPath, PdfOptions? pdfOptions, MarkdownOptions? markdownOptions,
        CancellationToken cancellationToken)
    {
        var markdown = File.ReadAllText(markdownPath);
        cancellationToken.ThrowIfCancellationRequested();
        var html = MarkdownToHtml.ConvertDocument(markdown, markdownOptions);
        pdfOptions ??= new PdfOptions();
        var baseDir = pdfOptions.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(markdownPath));
        AtomicFile.WriteAllBytes(pdfPath, HtmlToPdf.Render(html, pdfOptions, baseDir, cancellationToken));
    }

    /// <summary>
    /// Converts a Markdown string to a PDF asynchronously on the thread pool. Cancelling the token also stops a conversion
    /// that has already started, and signing, as in <see cref="HtmlToPdf.ConvertAsync(string, PdfOptions?, CancellationToken)"/>.
    /// </summary>
    public static Task<byte[]> ConvertAsync(string markdown, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null,
        CancellationToken cancellationToken = default)
        => HtmlToPdf.RenderAsync(() => MarkdownToHtml.ConvertDocument(markdown, markdownOptions), pdfOptions ?? new PdfOptions(), cancellationToken);

    /// <summary>
    /// Converts a Markdown string on the thread pool and writes the PDF to <paramref name="output"/> asynchronously, as
    /// ASP.NET Core requires of a response body. Nothing is written unless the conversion completes.
    /// </summary>
    /// <param name="markdown">The Markdown text.</param>
    /// <param name="output">Where the PDF is written; it is not flushed or closed.</param>
    /// <param name="pdfOptions">Page, font and output settings; null for the defaults.</param>
    /// <param name="markdownOptions">Markdown extensions and styling; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the conversion, signing and the write.</param>
    public static async Task ConvertAsync(string markdown, Stream output, PdfOptions? pdfOptions = null, MarkdownOptions? markdownOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        var bytes = await ConvertAsync(markdown, pdfOptions, markdownOptions, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}

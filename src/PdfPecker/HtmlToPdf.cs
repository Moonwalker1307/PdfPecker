using PdfPecker.Pdf;
using PdfPecker.Signing;

namespace PdfPecker;

/// <summary>
/// Converts HTML with CSS to PDF. Everything (HTML parsing, CSS cascade, layout, PDF writing) is implemented in
/// this library; it has no dependencies beyond the .NET base class library.
/// </summary>
/// <remarks>Instances are not needed: all methods are static and thread-safe.</remarks>
public static partial class HtmlToPdf
{
    /// <summary>Converts an HTML string to a PDF document.</summary>
    public static byte[] Convert(string html, PdfOptions? options = null)
    {
        if (html == null) throw new ArgumentNullException(nameof(html));
        return Render(html, options ?? new PdfOptions(), options?.BaseDirectory);
    }

    /// <summary>
    /// Converts an HTML string to a PDF document, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled. Use it with a timeout when the HTML comes from untrusted users.
    /// </summary>
    public static byte[] Convert(string html, PdfOptions? options, CancellationToken cancellationToken)
    {
        if (html == null) throw new ArgumentNullException(nameof(html));
        return Render(html, options ?? new PdfOptions(), options?.BaseDirectory, cancellationToken);
    }

    /// <summary>Converts an HTML string and writes the PDF to <paramref name="output"/>.</summary>
    public static void Convert(string html, Stream output, PdfOptions? options = null)
    {
        if (output == null) throw new ArgumentNullException(nameof(output));
        var bytes = Convert(html, options);
        output.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Converts an HTML file to a PDF file. Relative image and stylesheet paths resolve against the HTML file's folder.</summary>
    public static void ConvertFile(string htmlPath, string pdfPath, PdfOptions? options = null)
    {
        var html = File.ReadAllText(htmlPath);
        options ??= new PdfOptions();
        var baseDir = options.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(htmlPath));
        AtomicFile.WriteAllBytes(pdfPath, Render(html, options, baseDir));
    }

    /// <summary>Converts an HTML string to a PDF asynchronously on the thread pool.</summary>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> stops a conversion that is already running. With
    /// <see cref="PdfOptions.Signature"/> the external signer and the timestamp server are awaited, as in
    /// <see cref="PdfSigner.SignAsync"/>, and cancelling stops them too.
    /// </remarks>
    public static Task<byte[]> ConvertAsync(string html, PdfOptions? options = null, CancellationToken cancellationToken = default)
        => RenderAsync(() => html ?? throw new ArgumentNullException(nameof(html)), options ?? new PdfOptions(), cancellationToken);

    internal static byte[] Render(string html, PdfOptions options, string? baseDirectory, CancellationToken cancellationToken = default)
    {
        var pdf = new Conversion(html, options, ValidateOptions(options), baseDirectory, cancellationToken).Run();
        return options.Signature == null ? pdf : SignatureWriter.Sign(pdf, options.Signature, SigningPassword(options), cancellationToken);
    }

    /// <summary>
    /// Converts on the thread pool, then signs without holding a thread: the external signer and the timestamp request
    /// are awaited. <paramref name="html"/> is read on the pool too, so that producing it (Markdown) does not run on the
    /// caller's thread and its failures end up in the task.
    /// </summary>
    internal static async Task<byte[]> RenderAsync(Func<string> html, PdfOptions options, CancellationToken cancellationToken)
    {
        var pdf = await Task.Run(() => new Conversion(html(), options, ValidateOptions(options), options.BaseDirectory, cancellationToken).Run(),
            cancellationToken).ConfigureAwait(false);
        if (options.Signature == null) return pdf;
        return await SignatureWriter.SignAsync(pdf, options.Signature, SigningPassword(options), async: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The password that opens the file just written, which signing needs when it is encrypted.</summary>
    private static string? SigningPassword(PdfOptions options) => options.Security?.OwnerPassword ?? options.Security?.UserPassword;

    /// <summary>What the checked options settle for the output file, beyond the options themselves.</summary>
    /// <param name="Conformance">The PDF/A level: the options' own, or PDF/A-3b for an e-invoice that names none.</param>
    /// <param name="Attachments">The attachments to embed, the e-invoice's XML among them.</param>
    /// <param name="PdfUAPart">The PDF/UA part (1 or 2), or 0 for none.</param>
    private readonly record struct ConversionSettings(PdfConformance Conformance, List<PdfAttachment> Attachments, int PdfUAPart)
    {
        public bool IsPdfUA => PdfUAPart > 0;
    }

    /// <summary>
    /// Checks the caller's options before any work is done, so that a wrong combination fails at once with a message
    /// that says how to fix it. The checks run in a fixed order, which decides the message when several are wrong.
    /// </summary>
    private static ConversionSettings ValidateOptions(PdfOptions options)
    {
        if (!(options.Scale >= 0.1f && options.Scale <= 2f))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "PdfOptions.Scale must be between 0.1 and 2.");
        var conformance = options.Conformance;
        if (!Enum.IsDefined(conformance))
            throw new ArgumentOutOfRangeException(nameof(options), conformance, "Unknown PdfOptions.Conformance.");
        if (!Enum.IsDefined(options.Accessibility))
            throw new ArgumentOutOfRangeException(nameof(options), options.Accessibility, "Unknown PdfOptions.Accessibility.");
        if (options.EInvoice != null)
        {
            // Factur-X / ZUGFeRD files are PDF/A-3, the part that allows attachments; PDF/A-4f allows them too.
            if (conformance == PdfConformance.None) conformance = PdfConformance.PdfA3b;
            else if (conformance is not (PdfConformance.PdfA3b or PdfConformance.PdfA3u or PdfConformance.PdfA4f))
                throw new ArgumentException("An e-invoice needs a PDF/A level that allows attachments: set PdfOptions.Conformance to "
                    + "PdfA3b, PdfA3u or PdfA4f, or leave it at None for PdfA3b.", nameof(options));
        }
        var attachments = EmbeddedFiles.Prepare(options.Attachments, options.EInvoice);
        if (conformance == PdfConformance.PdfA4f && attachments.Count == 0)
            throw new ArgumentException("PDF/A-4f is for files with attachments: use it with PdfOptions.Attachments or EInvoice, or use PdfA4.", nameof(options));
        if (conformance is PdfConformance.PdfA2b or PdfConformance.PdfA2u or PdfConformance.PdfA4 && attachments.Count > 0)
            throw new ArgumentException($"PDF/A-{conformance.ToString()[4..].ToLowerInvariant()} allows attaching only PDF/A files, which PdfPecker does not check: "
                + "set PdfOptions.Conformance to PdfA3b, PdfA3u or PdfA4f, which allow any attachment, or to None for a plain PDF.", nameof(options));
        // PDF/UA-1 and PDF/A-2 and -3 are based on PDF 1.7, PDF/UA-2 and PDF/A-4 on PDF 2.0: a file is one or the other.
        if (options.Accessibility == PdfAccessibility.PdfUA2 && conformance != PdfConformance.None && !PdfA.IsPdf20(conformance))
            throw new ArgumentException($"PDF/UA-2 is based on PDF 2.0 and {conformance} on PDF 1.7: combine PdfUA2 with PdfA4 or PdfA4f, "
                + "or use PdfUA1.", nameof(options));
        if (options.Accessibility == PdfAccessibility.PdfUA1 && PdfA.IsPdf20(conformance))
            throw new ArgumentException($"PDF/UA-1 is based on PDF 1.7 and {conformance} on PDF 2.0: combine PdfA4 with PdfUA2, "
                + "or use PDF/A-2 or PDF/A-3 with PdfUA1.", nameof(options));
        if (conformance != PdfConformance.None && options.Security != null)
            throw new ArgumentException(options.EInvoice != null
                ? "An e-invoice is a PDF/A file, which does not allow encryption: remove PdfOptions.Security."
                : "PDF/A does not allow encryption: set PdfOptions.Conformance or PdfOptions.Security, not both.", nameof(options));
        int uaPart = options.Accessibility switch { PdfAccessibility.PdfUA1 => 1, PdfAccessibility.PdfUA2 => 2, _ => 0 };
        if (uaPart > 0 && options.Security is { } security && (security.Permissions & PdfPermissions.ExtractForAccessibility) == 0)
            throw new ArgumentException("PDF/UA needs PdfPermissions.ExtractForAccessibility, so screen readers can read an encrypted file.", nameof(options));
        if (options.Watermark is { } wm && string.IsNullOrEmpty(wm.Text) && wm.Image == null)
            throw new ArgumentException("The watermark needs a Text or an Image.", nameof(options));
        // Only the caller's own options can be wrong in a way worth an exception; @page rules come from the document,
        // which may be untrusted, so their margins are fitted to the page instead (FitMargins).
        if ((options.PageSize.Width - options.Margins.Left - options.Margins.Right) / options.Scale < MinContentSize
            || (options.PageSize.Height - options.Margins.Top - options.Margins.Bottom) / options.Scale < MinContentSize)
            throw new ArgumentException("The page margins leave no room for content.", nameof(options));
        return new ConversionSettings(conformance, attachments, uaPart);
    }
}

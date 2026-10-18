using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>Where a stamp sits on the page, as the page is displayed.</summary>
public enum PdfStampPosition
{
    /// <summary>Centred on the page.</summary>
    Center,
    /// <summary>Centred along the top edge.</summary>
    Top,
    /// <summary>Centred along the bottom edge.</summary>
    Bottom,
    /// <summary>Centred along the left edge.</summary>
    Left,
    /// <summary>Centred along the right edge.</summary>
    Right,
    /// <summary>In the top left corner.</summary>
    TopLeft,
    /// <summary>In the top right corner.</summary>
    TopRight,
    /// <summary>In the bottom left corner.</summary>
    BottomLeft,
    /// <summary>In the bottom right corner.</summary>
    BottomRight,
}

/// <summary>Options for <see cref="PdfStamper"/>.</summary>
public sealed class PdfStampOptions
{
    /// <summary>
    /// Draw the stamp underneath the page content, like letterhead paper, instead of on top of it. Content that paints an
    /// opaque background over the whole page hides an underlay. Default: false (on top).
    /// </summary>
    public bool Underlay { get; set; }

    /// <summary>The page of the stamp PDF to use, 0-based. Ignored when <see cref="MatchPages"/> is set. Default: 0.</summary>
    public int StampPage { get; set; }

    /// <summary>
    /// Put stamp page i on page i, and the stamp's last page on every page after that: with a two-page letterhead, the
    /// first page gets the first letterhead page and the following pages get the second. Default: false.
    /// </summary>
    public bool MatchPages { get; set; }

    /// <summary>
    /// Which pages to stamp, by 0-based page index, for example <c>i =&gt; i == 0</c> for the first page only. Default:
    /// null, every page.
    /// </summary>
    public Func<int, bool>? Pages { get; set; }

    /// <summary>Opacity of the stamp, from 0 (invisible) to 1 (as drawn). Default: 1.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>
    /// Size factor for the stamp page: 1 draws it at its own size. Default: 0, as large as fits on the page, keeping
    /// its aspect ratio (a stamp the size of the page covers it exactly).
    /// </summary>
    public float Scale { get; set; }

    /// <summary>Where the stamp sits on the page. Default: centred.</summary>
    public PdfStampPosition Position { get; set; } = PdfStampPosition.Center;

    /// <summary>Moves the stamp to the right, in points (negative: to the left). Default: 0.</summary>
    public float OffsetX { get; set; }

    /// <summary>Moves the stamp down, in points (negative: up). Default: 0.</summary>
    public float OffsetY { get; set; }

    /// <summary>The user or owner password of the PDF being stamped, when it is encrypted.</summary>
    public string? Password { get; set; }

    /// <summary>The password of the stamp PDF, when it is encrypted.</summary>
    public string? StampPassword { get; set; }

    /// <summary>
    /// Encrypts the result with these passwords and permissions. Default: null, the result is not encrypted (also when
    /// the input was).
    /// </summary>
    public PdfSecurity? Security { get; set; }

    /// <summary>
    /// Stamp a digitally signed PDF anyway. The signatures are kept but no longer validate, since the signed bytes
    /// change. Default: false, a signed PDF is refused with <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool InvalidateSignatures { get; set; }
}

/// <summary>
/// Puts a page of one PDF on top of (a "PAID" or "COPY" stamp) or underneath (letterhead) the pages of another. Any
/// PDF can be stamped, not only those this library creates.
/// </summary>
/// <remarks>
/// The stamp page is copied in once, as a form XObject, and drawn on each chosen page; text in it stays text and
/// vector graphics stay sharp. It is placed as the page is displayed, so it is upright on rotated pages, and it lies
/// within the crop box. The page content is wrapped so its graphics state cannot leak onto an overlay. Everything else
/// in the document is kept: annotations (which are always drawn above page content, so above an overlay too), form
/// fields, bookmarks, links, attachments, metadata, and the structure tree of a tagged PDF, in which the stamp is
/// marked as an artifact. The stamp page's own annotations, form fields and tags are not copied. The result is a new
/// file, written in full, not an incremental update: unreachable objects are left out, and the result is not encrypted
/// unless <see cref="PdfStampOptions.Security"/> is set. A document that claims PDF/A keeps its claim; whether it still
/// conforms depends on the stamp (embedded fonts; no transparency for PDF/A-1). Both inputs are treated as untrusted.
/// </remarks>
public static class PdfStamper
{
    /// <summary>Stamps the pages of <paramref name="pdf"/> with a page of <paramref name="stampPdf"/> and returns the result.</summary>
    /// <param name="pdf">The PDF to stamp.</param>
    /// <param name="stampPdf">The PDF holding the stamp, for example a one-page PDF with a "PAID" stamp or a letterhead.</param>
    /// <param name="options">Placement, page selection and passwords; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="PdfPasswordException">A PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="InvalidDataException">A PDF is not readable, has no pages, or decodes to more data than allowed.</exception>
    /// <exception cref="InvalidOperationException">The PDF is digitally signed and <see cref="PdfStampOptions.InvalidateSignatures"/> is not set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range, such as a <see cref="PdfStampOptions.StampPage"/> the stamp PDF does not have.</exception>
    /// <exception cref="NotSupportedException">A PDF uses an encryption handler other than the standard one.</exception>
    public static byte[] Stamp(byte[] pdf, byte[] stampPdf, PdfStampOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (stampPdf == null) throw new ArgumentNullException(nameof(stampPdf));
        options ??= new PdfStampOptions();
        var writer = new PdfStampWriter(pdf, options, cancellationToken);
        var stamp = PdfStampWriter.OpenStamp(stampPdf, options.StampPassword, cancellationToken);
        return writer.Write(_ => stamp);
    }

    /// <summary>Stamps a PDF read from a stream and writes the result to <paramref name="output"/>.</summary>
    /// <remarks>Both inputs are read from their current position to their end; the streams are not disposed.</remarks>
    /// <inheritdoc cref="Stamp(byte[], byte[], PdfStampOptions?, CancellationToken)"/>
    public static void Stamp(Stream pdf, Stream stampPdf, Stream output, PdfStampOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (stampPdf == null) throw new ArgumentNullException(nameof(stampPdf));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var result = Stamp(PdfMerger.ReadAll(pdf), PdfMerger.ReadAll(stampPdf), options, cancellationToken);
        output.Write(result);
        output.Flush();
    }

    /// <summary>Stamps a PDF file and writes the result to <paramref name="outputPath"/> (which may be the same path).</summary>
    public static void StampFile(string inputPath, string stampPath, string outputPath, PdfStampOptions? options = null)
        => File.WriteAllBytes(outputPath, Stamp(File.ReadAllBytes(inputPath), File.ReadAllBytes(stampPath), options));

    /// <summary>
    /// Stamps the pages of <paramref name="pdf"/> with HTML, rendered at the size of each page (as displayed) with no
    /// margins, so it can place text anywhere with CSS. With <see cref="PdfStampOptions.MatchPages"/>, page i of the
    /// rendered HTML goes on page i.
    /// </summary>
    /// <param name="pdf">The PDF to stamp.</param>
    /// <param name="html">The stamp as an HTML document; its page background stays transparent unless it sets one.</param>
    /// <param name="options">Placement, page selection and passwords; null for the defaults.</param>
    /// <param name="htmlOptions">
    /// Options for rendering the HTML, such as fonts; null for the defaults with no margins. Its page size is replaced by
    /// each page's size, and CSS <c>@page</c> size rules are ignored. Its footer, header, watermark, attachments,
    /// bookmarks, tagging, signature, security, conformance and e-invoice are not used, so the options of the main
    /// conversion can be passed as they are.
    /// </param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <inheritdoc cref="Stamp(byte[], byte[], PdfStampOptions?, CancellationToken)"/>
    public static byte[] StampHtml(byte[] pdf, string html, PdfStampOptions? options = null, PdfOptions? htmlOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (html == null) throw new ArgumentNullException(nameof(html));
        options ??= new PdfStampOptions();
        var writer = new PdfStampWriter(pdf, options, cancellationToken);
        var rendered = new Dictionary<(long, long), PdfFile>();
        return writer.Write(i =>
        {
            var (w, h) = writer.DisplaySize(i);
            var key = ((long)Math.Round(w * 100), (long)Math.Round(h * 100));
            if (!rendered.TryGetValue(key, out var file))
            {
                var o = htmlOptions?.Clone() ?? new PdfOptions { Margins = new PageMargins(0) };
                o.PageSize = new PageSize((float)w, (float)h);
                o.IgnoreCssPageRules = true;
                o.Signature = null;
                o.Security = null;
                o.Conformance = PdfConformance.None;
                o.EInvoice = null;
                // Options shared with the main conversion would otherwise put a second footer, header and watermark on
                // every page; the stamp's attachments, bookmarks and tags would be dropped anyway, as it is drawn as an
                // artifact.
                o.FooterText = null;
                o.HeaderText = null;
                o.Watermark = null;
                o.Attachments = new List<PdfAttachment>();
                o.Bookmarks = false;
                o.Accessibility = PdfAccessibility.None;
                if (writer.NeedsEmbeddedFonts) o.EmbedStandardFonts = true;
                rendered[key] = file = PdfStampWriter.OpenStamp(HtmlToPdf.Convert(html, o, cancellationToken), null, cancellationToken);
            }
            return file;
        });
    }
}

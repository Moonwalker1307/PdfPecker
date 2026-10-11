using PdfPecker.Pdf;

namespace PdfPecker;

/// <summary>Options for <see cref="PdfMerger"/>.</summary>
public sealed class PdfMergeOptions
{
    /// <summary>
    /// Passwords for encrypted inputs, by input position (null for an input that needs none). An encrypted input opens
    /// with its user or owner password, or with no password when it only restricts permissions; otherwise merging throws
    /// <see cref="PdfPasswordException"/>.
    /// </summary>
    public IList<string?>? Passwords { get; set; }

    /// <summary>Keep each input's bookmarks, one after the other. Default: true.</summary>
    public bool Bookmarks { get; set; } = true;

    /// <summary>The most pages the merged document may have; more throws <see cref="PdfLimitException"/>. Default: 100,000.</summary>
    public int MaxPages { get; set; } = 100_000;

    /// <summary>Document title. Default: the first input's title.</summary>
    public string? Title { get; set; }

    /// <summary>Document author. Default: the first input's author.</summary>
    public string? Author { get; set; }

    /// <summary>Document subject. Default: the first input's subject.</summary>
    public string? Subject { get; set; }

    /// <summary>Document keywords. Default: the first input's keywords.</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the original document. Default: the first input's creator.</summary>
    public string? Creator { get; set; }

    /// <summary>The application that produced the PDF. Default: "PdfPecker".</summary>
    public string? Producer { get; set; }

    /// <summary>
    /// Custom document properties. They are added to the first input's custom properties, replacing those with the same
    /// name. Names cannot be the standard ones (see <see cref="PdfOptions.CustomProperties"/>).
    /// </summary>
    public IDictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Combines PDFs into one: the pages of every input in order, with their bookmarks and links. Any PDF can be merged,
/// not only those this library creates.
/// </summary>
/// <remarks>
/// The result is a new, unencrypted PDF. Digital signatures are not carried over (merging changes the document, which
/// would break them anyway), nor are form fields' interactivity, PDF/A conformance, attachments or the logical
/// structure (tags). Inputs are treated as untrusted: damaged files are repaired where possible, and decoding work is
/// limited per input.
/// </remarks>
public static class PdfMerger
{
    /// <summary>Merges PDFs, in order.</summary>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened without a password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF.</exception>
    public static byte[] Merge(params byte[][] pdfs) => Merge(pdfs, null);

    /// <summary>Merges PDFs, in order.</summary>
    /// <param name="pdfs">The PDFs to merge.</param>
    /// <param name="options">Passwords, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the merge.</param>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF, or decodes to more data than allowed.</exception>
    /// <exception cref="PdfLimitException">The merged document has more than <see cref="PdfMergeOptions.MaxPages"/> pages.</exception>
    /// <exception cref="NotSupportedException">An input uses an encryption handler other than the standard one.</exception>
    public static byte[] Merge(IEnumerable<byte[]> pdfs, PdfMergeOptions? options, CancellationToken cancellationToken = default)
    {
        if (pdfs == null) throw new ArgumentNullException(nameof(pdfs));
        var list = pdfs.ToList();
        if (list.Count == 0) throw new ArgumentException("Give at least one PDF to merge.", nameof(pdfs));
        for (int i = 0; i < list.Count; i++)
            if (list[i] == null) throw new ArgumentException($"PDF {i + 1} is null.", nameof(pdfs));
        options ??= new PdfMergeOptions();
        return new PdfMergeWriter(options, cancellationToken).Merge(list);
    }

    /// <summary>Merges PDF files, in order, and writes the result to <paramref name="outputPath"/>.</summary>
    public static void MergeFiles(string outputPath, IEnumerable<string> inputPaths, PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
        if (inputPaths == null) throw new ArgumentNullException(nameof(inputPaths));
        var data = inputPaths.Select(File.ReadAllBytes).ToList();
        File.WriteAllBytes(outputPath, Merge(data, options, cancellationToken));
    }
}

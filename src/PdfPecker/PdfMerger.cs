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
/// Combines PDFs into one: the pages of every input in order, with their bookmarks, links and form fields. Any PDF can
/// be merged, not only those this library creates.
/// </summary>
/// <remarks>
/// The result is a new, unencrypted PDF. Digital signatures are not carried over (merging changes the document, which
/// would break them anyway), nor are PDF/A conformance, attachments, page labels or the logical structure (tags). A form
/// field named like one in an earlier input is renamed "name_2", "name_3", ..., so each copy keeps its value. Streams
/// that are identical in several inputs (a logo, an embedded font) are written once. Inputs are treated as untrusted:
/// damaged files are repaired where possible, and decoding work is limited per input.
/// </remarks>
public static class PdfMerger
{
    /// <summary>Merges PDFs, in order.</summary>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened without a password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF, or none of them has any pages.</exception>
    public static byte[] Merge(params byte[][] pdfs) => Merge(pdfs, null);

    /// <summary>Merges PDFs, in order.</summary>
    /// <param name="pdfs">The PDFs to merge.</param>
    /// <param name="options">Passwords, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the merge.</param>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF or decodes to more data than allowed, or none of them has any pages.</exception>
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

    /// <summary>
    /// Merges PDFs read from streams, in order, and writes the result to <paramref name="output"/> as it is made.
    /// </summary>
    /// <remarks>
    /// Inputs are read one at a time, when their turn comes, and let go once their pages are written, so memory use
    /// follows the largest input rather than all of them together; <paramref name="inputs"/> can be a lazy sequence that
    /// opens each stream on demand. Each input is read from its current position to its end. The streams are not
    /// disposed. If merging fails, <paramref name="output"/> holds an incomplete document.
    /// </remarks>
    /// <param name="inputs">The PDFs to merge.</param>
    /// <param name="output">Where the merged PDF is written.</param>
    /// <param name="options">Passwords, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the merge.</param>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF or decodes to more data than allowed, or none of them has any pages.</exception>
    /// <exception cref="PdfLimitException">The merged document has more than <see cref="PdfMergeOptions.MaxPages"/> pages.</exception>
    /// <exception cref="NotSupportedException">An input uses an encryption handler other than the standard one.</exception>
    public static void Merge(IEnumerable<Stream> inputs, Stream output, PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckArguments(inputs, output);
        var writer = new PdfMergeWriter(options ?? new PdfMergeOptions(), cancellationToken, output);
        foreach (var input in inputs)
        {
            if (input == null) throw new ArgumentException($"PDF {writer.InputCount + 1} is null.", nameof(inputs));
            writer.Add(ReadAll(input));
        }
        if (writer.InputCount == 0) throw new ArgumentException("Give at least one PDF to merge.", nameof(inputs));
        writer.Finish();
        writer.Flush(output);
        output.Flush();
    }

    /// <summary>
    /// Merges PDFs read from streams, in order, and writes the result to <paramref name="output"/> as it is made, reading
    /// and writing the streams asynchronously (as ASP.NET Core requires of request and response bodies).
    /// </summary>
    /// <remarks>
    /// As <see cref="Merge(IEnumerable{Stream}, Stream, PdfMergeOptions?, CancellationToken)"/>; parsing and writing
    /// each input is done synchronously between reads.
    /// </remarks>
    /// <inheritdoc cref="Merge(IEnumerable{Stream}, Stream, PdfMergeOptions?, CancellationToken)"/>
    public static async Task MergeAsync(IEnumerable<Stream> inputs, Stream output, PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckArguments(inputs, output);
        var writer = new PdfMergeWriter(options ?? new PdfMergeOptions(), cancellationToken);
        foreach (var input in inputs)
        {
            if (input == null) throw new ArgumentException($"PDF {writer.InputCount + 1} is null.", nameof(inputs));
            writer.Add(await ReadAllAsync(input, cancellationToken).ConfigureAwait(false));
            await writer.FlushAsync(output, cancellationToken).ConfigureAwait(false);
        }
        if (writer.InputCount == 0) throw new ArgumentException("Give at least one PDF to merge.", nameof(inputs));
        writer.Finish();
        await writer.FlushAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Merges PDF files, in order, and writes the result to <paramref name="outputPath"/>.</summary>
    /// <remarks>
    /// The files are read one at a time and the result is written as it is made, so memory use follows the largest
    /// input. The output file appears only once the merge has succeeded, and may be one of the inputs.
    /// </remarks>
    public static void MergeFiles(string outputPath, IEnumerable<string> inputPaths, PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
        if (inputPaths == null) throw new ArgumentNullException(nameof(inputPaths));
        var full = Path.GetFullPath(outputPath);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                Merge(OpenFiles(inputPaths), output, options, cancellationToken);
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static IEnumerable<Stream> OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path ?? throw new ArgumentException("An input path is null.", nameof(paths)));
            yield return stream;
        }
    }

    private static void CheckArguments(IEnumerable<Stream> inputs, Stream output)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
    }

    private static byte[] ReadAll(Stream input)
    {
        if (input.CanSeek)
        {
            var data = new byte[CheckedLength(input.Length - input.Position)];
            input.ReadExactly(data);
            return data;
        }
        using var copy = new MemoryStream();
        input.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream input, CancellationToken cancellationToken)
    {
        if (input.CanSeek)
        {
            var data = new byte[CheckedLength(input.Length - input.Position)];
            await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return data;
        }
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static int CheckedLength(long length) =>
        length <= Array.MaxLength ? (int)Math.Max(0, length) : throw new InvalidDataException("An input is larger than 2 GB.");
}

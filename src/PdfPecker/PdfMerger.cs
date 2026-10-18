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
/// would break them anyway), nor is PDF/A conformance; tagged inputs stay tagged, and page labels are kept. Attachments
/// (embedded files) are kept, an attachment named like one in an earlier input becoming "name (2).ext". A form field
/// named like one in an earlier input is renamed "name_2", "name_3", ..., so each copy keeps its value. To take only
/// some pages of an input, give it a <see cref="PdfMergeInput"/> with a <see cref="PdfPageRange"/>. Streams
/// that are identical in several inputs (a logo, an embedded font) are written once. Inputs are treated as untrusted:
/// damaged files are repaired where possible, and decoding work is limited per input. The byte[] overloads hold every
/// input and the result in memory; the Stream overloads, <see cref="MergeAsync(IAsyncEnumerable{byte[]}, Stream, PdfMergeOptions?, CancellationToken)"/>
/// and <see cref="StreamingPdfMerger"/> write each input to the output as it is read.
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
    /// Merges the chosen pages of PDFs, in order: for example <c>Merge(new[] { new PdfMergeInput(a, "1-3"), new
    /// PdfMergeInput(b, "2,1") })</c>. Pages can be reordered and repeated; what only the pages left out use (their
    /// bookmarks, links to them, their form fields and tags) is left out too (see <see cref="PdfPages"/>).
    /// </summary>
    /// <param name="inputs">The PDFs to merge, each with the pages to take (null for all).</param>
    /// <param name="options">Passwords, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the merge.</param>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page of its PDF.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF or decodes to more data than allowed, or none of them has any pages.</exception>
    /// <exception cref="PdfLimitException">The merged document has more than <see cref="PdfMergeOptions.MaxPages"/> pages.</exception>
    public static byte[] Merge(IEnumerable<PdfMergeInput> inputs, PdfMergeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        var list = inputs.ToList();
        if (list.Count == 0) throw new ArgumentException("Give at least one PDF to merge.", nameof(inputs));
        for (int i = 0; i < list.Count; i++)
            if (list[i] == null) throw new ArgumentException($"PDF {i + 1} is null.", nameof(inputs));
        var writer = new PdfMergeWriter(options ?? new PdfMergeOptions(), cancellationToken);
        foreach (var input in list) writer.Add(input.Pdf, Spec(input.Pages));
        writer.Finish();
        return writer.ToArray();
    }

    internal static PdfMergeWriter.PageSpec? Spec(PdfPageRange? pages) => pages == null ? null : new PdfMergeWriter.PageSpec { Pages = pages };

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

    /// <summary>
    /// Merges PDFs, in order, into <paramref name="output"/> as they arrive, so only the PDF being added is held in memory
    /// (see <see cref="StreamingPdfMerger"/>).
    /// </summary>
    /// <param name="pdfs">The PDFs to merge, for example downloaded one by one from storage.</param>
    /// <param name="output">Where to write the merged PDF, for example an HTTP response body or a file.</param>
    /// <param name="options">Passwords, limits and document properties; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the merge.</param>
    /// <returns>The number of pages written.</returns>
    /// <exception cref="ArgumentException"><paramref name="pdfs"/> is empty or has a null PDF.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF or decodes to more data than allowed, or none of them has any pages.</exception>
    /// <exception cref="PdfLimitException">The merged document has more than <see cref="PdfMergeOptions.MaxPages"/> pages.</exception>
    public static async Task<int> MergeAsync(IAsyncEnumerable<byte[]> pdfs, Stream output, PdfMergeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (pdfs == null) throw new ArgumentNullException(nameof(pdfs));
        var merger = new StreamingPdfMerger(output, options);
        await foreach (var pdf in pdfs.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (pdf == null) throw new ArgumentException($"PDF {merger.InputCount + 1} is null.", nameof(pdfs));
            await merger.AddAsync(pdf, cancellationToken).ConfigureAwait(false);
        }
        if (merger.InputCount == 0) throw new ArgumentException("Give at least one PDF to merge.", nameof(pdfs));
        await merger.CompleteAsync(cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return merger.PageCount;
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
        AtomicFile.Write(outputPath, output => Merge(OpenFiles(inputPaths), output, options, cancellationToken));
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

    internal static byte[] ReadAll(Stream input)
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

/// <summary>
/// Merges PDFs into a stream one at a time: each PDF's pages are written to the output when it is added, and the PDF is
/// then let go, so memory stays at about one input however many there are. Use it to merge many documents straight into
/// an HTTP response or a file when they arrive one by one, deciding between inputs what to do next. The result is the
/// same as <see cref="PdfMerger"/>'s.
/// </summary>
/// <remarks>
/// Add the PDFs in order, then call <see cref="Complete"/> or <see cref="CompleteAsync"/>, which writes the page tree,
/// the document information, the bookmark root, the form and the cross-reference table; until then the output is not a
/// valid PDF. If adding or completing throws, what was written cannot be used, and every later call throws
/// <see cref="InvalidOperationException"/>. The output only needs to be writable, not seekable, and is never flushed or
/// closed. <see cref="PdfMergeOptions.Passwords"/> is indexed by the order PDFs are added, and
/// <see cref="PdfMergeOptions.MaxPages"/> counts every input. Not thread-safe.
/// </remarks>
public sealed class StreamingPdfMerger
{
    private readonly Stream _output;
    private readonly PdfMergeWriter _writer;
    private bool _completed, _failed;

    /// <param name="output">Where to write the merged PDF.</param>
    /// <param name="options">Passwords (by the order PDFs are added), limits and document properties; null for the defaults.</param>
    public StreamingPdfMerger(Stream output, PdfMergeOptions? options = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        _writer = new PdfMergeWriter(options ?? new PdfMergeOptions(), CancellationToken.None);
    }

    /// <summary>The number of PDFs added.</summary>
    public int InputCount => _writer.InputCount;

    /// <summary>The number of pages written so far.</summary>
    public int PageCount => _writer.PageCount;

    /// <summary>Writes the pages of the next PDF, and everything they use, to the output.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pdf"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The merge is complete, or an earlier call failed.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and cannot be opened with the given password.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable or decodes to more data than allowed.</exception>
    /// <exception cref="PdfLimitException">The merged document gets more than <see cref="PdfMergeOptions.MaxPages"/> pages.</exception>
    public void Add(byte[] pdf, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        Run(() => { Write(pdf, null, cancellationToken); _writer.Flush(_output); });
    }

    /// <inheritdoc cref="Add(byte[], CancellationToken)"/>
    public Task AddAsync(byte[] pdf, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return RunAsync(() => Write(pdf, null, cancellationToken), cancellationToken);
    }

    /// <summary>Writes the chosen pages of the next PDF, in the given order, and everything they use, to the output.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="pages">The pages to take (see <see cref="PdfPageRange"/>); null for every page.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentOutOfRangeException">A page is past the last page.</exception>
    /// <inheritdoc cref="Add(byte[], CancellationToken)"/>
    public void Add(byte[] pdf, PdfPageRange? pages, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        Run(() => { Write(pdf, pages, cancellationToken); _writer.Flush(_output); });
    }

    /// <inheritdoc cref="Add(byte[], PdfPageRange?, CancellationToken)"/>
    public Task AddAsync(byte[] pdf, PdfPageRange? pages, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return RunAsync(() => Write(pdf, pages, cancellationToken), cancellationToken);
    }

    /// <summary>Writes the end of the merged PDF. The output is not flushed or closed.</summary>
    /// <exception cref="InvalidOperationException">The merge is already complete, or an earlier call failed.</exception>
    /// <exception cref="InvalidDataException">None of the PDFs has any pages.</exception>
    public void Complete(CancellationToken cancellationToken = default) =>
        Run(() => { Finish(cancellationToken); _writer.Flush(_output); });

    /// <inheritdoc cref="Complete"/>
    public Task CompleteAsync(CancellationToken cancellationToken = default) =>
        RunAsync(() => Finish(cancellationToken), cancellationToken);

    private void Write(byte[] pdf, PdfPageRange? pages, CancellationToken cancellationToken)
    {
        _writer.Cancellation = cancellationToken;
        _writer.Add(pdf, PdfMerger.Spec(pages));
    }

    private void Finish(CancellationToken cancellationToken)
    {
        _completed = true;
        _writer.Cancellation = cancellationToken;
        _writer.Finish();
    }

    private void Run(Action step)
    {
        CheckState();
        try
        {
            step();
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private async Task RunAsync(Action step, CancellationToken cancellationToken)
    {
        Run(step);
        try
        {
            await _writer.FlushAsync(_output, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void CheckState()
    {
        if (_failed) throw new InvalidOperationException("An earlier PDF could not be merged, so the output cannot be used.");
        if (_completed) throw new InvalidOperationException("The merge is already complete.");
    }
}

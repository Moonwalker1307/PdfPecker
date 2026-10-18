using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>How an attached file relates to the document: the file specification's /AFRelationship (PDF/A-3, PDF 2.0).</summary>
public enum PdfAttachmentRelationship
{
    /// <summary>No relationship is known, or it is none of the others. The default.</summary>
    Unspecified,
    /// <summary>The file is the original source the document was made from (a spreadsheet, a word-processor file).</summary>
    Source,
    /// <summary>The file holds data the visible content is drawn from, such as a table's values as CSV.</summary>
    Data,
    /// <summary>The file is an alternative representation of the content, such as a machine-readable invoice.</summary>
    Alternative,
    /// <summary>The file adds to the content, such as an appendix or supporting material.</summary>
    Supplement,
}

/// <summary>
/// A file embedded in a PDF (an attachment), listed in the viewer's attachments panel. Add them with
/// <see cref="PdfOptions.Attachments"/> when converting, or with <see cref="PdfAttachments.Add(byte[], PdfAttachment[])"/>
/// to an existing PDF; <see cref="PdfAttachments.List(byte[], string?)"/> reads them back.
/// </summary>
public sealed class PdfAttachment
{
    /// <summary>An attachment with the given file name and content.</summary>
    /// <exception cref="ArgumentException">The file name is empty or has a path separator or control character.</exception>
    public PdfAttachment(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);
        if (fileName.Length == 0 || fileName.Any(ch => ch < ' ' || ch == '/' || ch == '\\'))
            throw new ArgumentException($"\"{fileName}\" is not a file name: it must be non-empty, without a folder or control characters.", nameof(fileName));
        FileName = fileName;
        Data = data;
    }

    /// <summary>An attachment from a file on disk: its name, content and last-write time.</summary>
    public static PdfAttachment FromFile(string path)
        => new(Path.GetFileName(path), File.ReadAllBytes(path)) { ModDate = new DateTimeOffset(File.GetLastWriteTimeUtc(path)) };

    /// <summary>The file name shown in the viewer and used when the file is saved. File names in one PDF are unique.</summary>
    public string FileName { get; }

    /// <summary>The file's content, embedded unchanged.</summary>
    public byte[] Data { get; }

    /// <summary>
    /// The file's MIME type, such as "text/csv". Default: null, guessed from the file name's extension
    /// (application/octet-stream when unknown).
    /// </summary>
    public string? MimeType { get; set; }

    /// <summary>A description shown in the attachments panel. Default: null, the file name (PDF/UA needs one).</summary>
    public string? Description { get; set; }

    /// <summary>How the file relates to the document (PDF/A-3's AFRelationship). Default: Unspecified.</summary>
    public PdfAttachmentRelationship Relationship { get; set; }

    /// <summary>When the file was created. Default: null, not written.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>When the file was last changed. Default: null, the time the PDF is written.</summary>
    public DateTimeOffset? ModDate { get; set; }

    /// <summary>Leave out the MD5 /CheckSum, so the e-invoice attachment keeps its earlier output.</summary>
    internal bool OmitChecksum { get; init; }

    /// <summary>The MIME type written: <see cref="MimeType"/> or one guessed from the extension.</summary>
    internal string MimeTypeOrGuess => string.IsNullOrWhiteSpace(MimeType) ? EmbeddedFiles.GuessMimeType(FileName) : MimeType!.Trim();

    /// <summary>The file name and size, for debugging and logs.</summary>
    public override string ToString() => $"{FileName} ({Data.Length} bytes)";
}

/// <summary>Reads the attachments (embedded files) of a PDF, and adds attachments to an existing PDF.</summary>
public static class PdfAttachments
{
    /// <summary>
    /// The files attached to a PDF: those in the document's embedded-files list and its associated files (/AF), in the
    /// list's order. Files attached to page annotations are not included.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static IReadOnlyList<PdfAttachment> List(byte[] pdf, string? password = null) => List(pdf, password, CancellationToken.None);

    /// <summary>
    /// The files attached to a PDF, as <see cref="List(byte[], string?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <param name="cancellationToken">Stops the work before each attachment and while one is decoded.</param>
    public static IReadOnlyList<PdfAttachment> List(byte[] pdf, string? password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = Open(pdf, password);
        // One decode budget for all attachments, which are all held in memory at once.
        using (PdfFile.BeginRender(cancellationToken))
        {
            var result = new List<PdfAttachment>();
            foreach (var found in EmbeddedFiles.Read(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(found.ToAttachment(file));
            }
            return result;
        }
    }

    /// <summary>
    /// The files attached to a PDF read from <paramref name="pdf"/> (from its current position to its end). The stream is
    /// not disposed. See <see cref="List(byte[], string?)"/>.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <param name="cancellationToken">Stops the work before each attachment and while one is decoded.</param>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static IReadOnlyList<PdfAttachment> List(Stream pdf, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return List(PdfMerger.ReadAll(pdf), password, cancellationToken);
    }

    /// <summary>The content of the attached file with the given name (compared exactly), or null when there is none.</summary>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static byte[]? Extract(byte[] pdf, string fileName, string? password = null) => Extract(pdf, fileName, password, CancellationToken.None);

    /// <summary>
    /// The content of the attached file with the given name, as <see cref="Extract(byte[], string, string?)"/>, stopping
    /// with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="fileName">The attachment's file name, compared exactly.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <param name="cancellationToken">Stops the work, also while the attachment is decoded.</param>
    public static byte[]? Extract(byte[] pdf, string fileName, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        cancellationToken.ThrowIfCancellationRequested();
        var file = Open(pdf, password);
        foreach (var f in EmbeddedFiles.Read(file))
            if (f.FileName == fileName)
            {
                // Only the one attachment is decoded, so its budget is the same as Decode's own.
                using (PdfFile.BeginRender(cancellationToken)) return EmbeddedFiles.Decode(file, f.Stream);
            }
        return null;
    }

    /// <summary>
    /// The content of the attached file with the given name in a PDF read from <paramref name="pdf"/> (from its current
    /// position to its end), or null when there is none. The stream is not disposed.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="fileName">The attachment's file name, compared exactly.</param>
    /// <param name="password">The user or owner password of an encrypted PDF.</param>
    /// <param name="cancellationToken">Stops the work, also while the attachment is decoded.</param>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static byte[]? Extract(Stream pdf, string fileName, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(fileName);
        return Extract(PdfMerger.ReadAll(pdf), fileName, password, cancellationToken);
    }

    /// <summary>
    /// Attaches files to an existing PDF as an incremental update: the original bytes are kept unchanged, so earlier
    /// digital signatures stay valid. In a PDF/A-3 file the attachments become associated files.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A file name is used twice, or is already attached; or the PDF is PDF/A-1 or PDF/A-2, which do not allow attaching
    /// arbitrary files.
    /// </exception>
    public static byte[] Add(byte[] pdf, params PdfAttachment[] attachments) => Add(pdf, attachments, password: null);

    /// <summary>
    /// Attaches files to an existing, possibly encrypted PDF as an incremental update. An encrypted PDF stays encrypted
    /// with the same key, and the attachments are encrypted with it.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="attachments">The files to attach.</param>
    /// <param name="password">The user or owner password of an encrypted PDF; null for an unencrypted one.</param>
    /// <exception cref="ArgumentException">
    /// A file name is used twice, or is already attached; or the PDF is PDF/A-1 or PDF/A-2, which do not allow attaching
    /// arbitrary files.
    /// </exception>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static byte[] Add(byte[] pdf, IEnumerable<PdfAttachment> attachments, string? password)
        => Add(pdf, attachments, password, CancellationToken.None);

    /// <summary>
    /// Attaches files to an existing, possibly encrypted PDF as an incremental update, as
    /// <see cref="Add(byte[], IEnumerable{PdfAttachment}, string?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="attachments">The files to attach.</param>
    /// <param name="password">The user or owner password of an encrypted PDF; null for an unencrypted one.</param>
    /// <param name="cancellationToken">Stops the work before the PDF is read and before the update is written.</param>
    public static byte[] Add(byte[] pdf, IEnumerable<PdfAttachment> attachments, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(attachments);
        cancellationToken.ThrowIfCancellationRequested();
        var list = EmbeddedFiles.Prepare(attachments, null);
        if (list.Count == 0) return pdf;
        var file = Open(pdf, password);
        cancellationToken.ThrowIfCancellationRequested();
        return EmbeddedFiles.Append(file, pdf, list);
    }

    /// <summary>
    /// Attaches files to the PDF read from <paramref name="input"/> (from its current position to its end) as an
    /// incremental update and writes the result to <paramref name="output"/>. See
    /// <see cref="Add(byte[], IEnumerable{PdfAttachment}, string?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The streams are not disposed. Nothing is written when the files cannot be attached or the work is cancelled.</remarks>
    /// <param name="input">The PDF.</param>
    /// <param name="output">Where the PDF with the attachments is written.</param>
    /// <param name="attachments">The files to attach.</param>
    /// <param name="password">The user or owner password of an encrypted PDF; null for an unencrypted one.</param>
    /// <param name="cancellationToken">Stops the work before the PDF is read and before the update is written.</param>
    /// <exception cref="ArgumentException">
    /// A file name is used twice, or is already attached; or the PDF is PDF/A-1 or PDF/A-2, which do not allow attaching
    /// arbitrary files.
    /// </exception>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static void Add(Stream input, Stream output, IEnumerable<PdfAttachment> attachments, string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        var result = Add(PdfMerger.ReadAll(input), attachments, password, cancellationToken);
        output.Write(result, 0, result.Length);
    }

    internal static PdfFile Open(byte[] pdf, string? password)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        try
        {
            return PdfFile.Open(pdf, password);
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }
}

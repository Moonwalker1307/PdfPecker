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
/// to an existing PDF; <see cref="PdfAttachments.List"/> reads them back.
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
    public static IReadOnlyList<PdfAttachment> List(byte[] pdf, string? password = null)
    {
        var file = Open(pdf, password);
        return EmbeddedFiles.Read(file).Select(f => f.ToAttachment(file)).ToList();
    }

    /// <summary>The content of the attached file with the given name (compared exactly), or null when there is none.</summary>
    /// <exception cref="InvalidDataException">The PDF cannot be read.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password is missing or wrong.</exception>
    public static byte[]? Extract(byte[] pdf, string fileName, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var file = Open(pdf, password);
        foreach (var f in EmbeddedFiles.Read(file))
            if (f.FileName == fileName) return EmbeddedFiles.Decode(file, f.Stream);
        return null;
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
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(attachments);
        var list = EmbeddedFiles.Prepare(attachments, null);
        if (list.Count == 0) return pdf;
        return EmbeddedFiles.Append(Open(pdf, password), pdf, list);
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

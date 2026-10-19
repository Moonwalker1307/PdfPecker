using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Pdf;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker;

/// <summary>
/// Adds, changes or removes password protection on an existing PDF: any PDF, not only those this library creates. To
/// protect a PDF while converting, set <see cref="PdfOptions.Security"/> instead.
/// </summary>
/// <remarks>
/// Encrypted inputs are read with the standard security handler (RC4 40 to 128 bit, AES-128 and AES-256); the result of
/// <see cref="Encrypt(byte[], PdfSecurity, string?)"/> is always AES-256. Either the user or the owner password opens an
/// input (or no password, when it only restricts permissions), as in other PDF tools, so whoever can open a document can
/// also remove or change its protection and permissions: permissions are a request to well-behaved readers, not access
/// control. The document is written out again as a whole: pages, bookmarks, links, forms and their values, the logical
/// structure (tags), metadata, page labels, attachments and every other object reachable from the catalog are kept;
/// earlier revisions kept by incremental updates are not. Digitally signed PDFs are refused, because rewriting them breaks
/// the signatures, and so is encrypting PDF/A files, which must not be encrypted.
/// </remarks>
public static class PdfProtector
{
    /// <summary>
    /// Encrypts <paramref name="pdf"/> with AES-256, or changes the passwords and permissions of an already encrypted PDF.
    /// </summary>
    /// <param name="pdf">The PDF to protect.</param>
    /// <param name="security">The new passwords and permissions.</param>
    /// <param name="password">The user or owner password of <paramref name="pdf"/>, when it is already encrypted.</param>
    /// <returns>The encrypted PDF.</returns>
    /// <exception cref="PdfPasswordException"><paramref name="pdf"/> is encrypted and <paramref name="password"/> does not open it.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is digitally signed, or claims PDF/A conformance; or it claims
    /// PDF/UA and <paramref name="security"/> does not allow <see cref="PdfPermissions.ExtractForAccessibility"/>.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException"><paramref name="pdf"/> uses an encryption handler other than the standard one.</exception>
    public static byte[] Encrypt(byte[] pdf, PdfSecurity security, string? password = null)
        => Encrypt(pdf, security, password, CancellationToken.None);

    /// <summary>
    /// Encrypts <paramref name="pdf"/> like <see cref="Encrypt(byte[], PdfSecurity, string?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The PDF to protect.</param>
    /// <param name="security">The new passwords and permissions.</param>
    /// <param name="password">The user or owner password of <paramref name="pdf"/>, when it is already encrypted.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The encrypted PDF.</returns>
    public static byte[] Encrypt(byte[] pdf, PdfSecurity security, string? password, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (security == null) throw new ArgumentNullException(nameof(security));
        return EncryptCore(pdf, security, password, cancellationToken).Span.ToArray();
    }

    /// <summary>Encrypts the PDF read from <paramref name="input"/> (from its current position) and writes the result to <paramref name="output"/>.</summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be encrypted.</remarks>
    /// <inheritdoc cref="Encrypt(byte[], PdfSecurity, string?)"/>
    public static void Encrypt(Stream input, Stream output, PdfSecurity security, string? password = null)
        => Encrypt(input, output, security, password, CancellationToken.None);

    /// <summary>
    /// Encrypts the PDF read from <paramref name="input"/> (from its current position) and writes the result to
    /// <paramref name="output"/>, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be encrypted or the work is cancelled.</remarks>
    /// <param name="input">The PDF to protect.</param>
    /// <param name="output">Where the encrypted PDF is written.</param>
    /// <param name="security">The new passwords and permissions.</param>
    /// <param name="password">The user or owner password of the input, when it is already encrypted.</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    public static void Encrypt(Stream input, Stream output, PdfSecurity security, string? password, CancellationToken cancellationToken)
    {
        CheckStreams(input, output);
        if (security == null) throw new ArgumentNullException(nameof(security));
        var result = EncryptCore(PdfMerger.ReadAll(input), security, password, cancellationToken);
        output.Write(result.RawBuffer, 0, result.Length);
    }

    /// <summary>
    /// Removes the encryption from <paramref name="pdf"/>, and with it any permission restrictions. A PDF that is not
    /// encrypted is returned unchanged.
    /// </summary>
    /// <param name="pdf">The encrypted PDF.</param>
    /// <param name="password">Its user or owner password; null or empty when it opens without one (only restricts permissions).</param>
    /// <returns>The unencrypted PDF.</returns>
    /// <exception cref="PdfPasswordException"><paramref name="password"/> does not open <paramref name="pdf"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="pdf"/> is digitally signed.</exception>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    /// <exception cref="NotSupportedException"><paramref name="pdf"/> uses an encryption handler other than the standard one.</exception>
    public static byte[] Decrypt(byte[] pdf, string? password) => Decrypt(pdf, password, CancellationToken.None);

    /// <summary>
    /// Removes the encryption from <paramref name="pdf"/> like <see cref="Decrypt(byte[], string?)"/>, stopping with
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="pdf">The encrypted PDF.</param>
    /// <param name="password">Its user or owner password; null or empty when it opens without one (only restricts permissions).</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    /// <returns>The unencrypted PDF.</returns>
    public static byte[] Decrypt(byte[] pdf, string? password, CancellationToken cancellationToken)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        var result = DecryptCore(pdf, password, cancellationToken);
        return result == null ? (byte[])pdf.Clone() : result.Span.ToArray();
    }

    /// <summary>Decrypts the PDF read from <paramref name="input"/> (from its current position) and writes the result to <paramref name="output"/>.</summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be decrypted.</remarks>
    /// <inheritdoc cref="Decrypt(byte[], string?)"/>
    public static void Decrypt(Stream input, Stream output, string? password) => Decrypt(input, output, password, CancellationToken.None);

    /// <summary>
    /// Decrypts the PDF read from <paramref name="input"/> (from its current position) and writes the result to
    /// <paramref name="output"/>, stopping with <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>The streams are not closed. Nothing is written when the input cannot be decrypted or the work is cancelled.</remarks>
    /// <param name="input">The encrypted PDF.</param>
    /// <param name="output">Where the unencrypted PDF is written.</param>
    /// <param name="password">Its user or owner password; null or empty when it opens without one (only restricts permissions).</param>
    /// <param name="cancellationToken">Stops the work between objects.</param>
    public static void Decrypt(Stream input, Stream output, string? password, CancellationToken cancellationToken)
    {
        CheckStreams(input, output);
        var pdf = PdfMerger.ReadAll(input);
        var result = DecryptCore(pdf, password, cancellationToken);
        if (result == null) output.Write(pdf);
        else output.Write(result.RawBuffer, 0, result.Length);
    }

    /// <summary>Whether <paramref name="pdf"/> is encrypted (password protected, or only permission restricted).</summary>
    /// <exception cref="InvalidDataException"><paramref name="pdf"/> is not a readable PDF.</exception>
    public static bool IsEncrypted(byte[] pdf)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        try
        {
            return Open(pdf, null).Decryptor != null;
        }
        catch (Exception ex) when (ex is PdfPasswordException or NotSupportedException)
        {
            return true; // needs a password, or uses a security handler other than the standard one
        }
    }

    private static ByteBuffer EncryptCore(byte[] pdf, PdfSecurity security, string? password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (PdfFile.BeginRender(cancellationToken))
        {
            var file = Open(pdf, password);
            CheckNotSigned(file);
            var xmp = Xmp(file);
            if (xmp != null && Regex.IsMatch(xmp, @"pdfaid:part\s*(>\s*|=\s*[""'])\d"))
                throw new ArgumentException("The PDF claims PDF/A conformance, which does not allow encryption.", nameof(pdf));
            if (xmp != null && xmp.Contains("http://www.aiim.org/pdfua/ns/id/", StringComparison.Ordinal)
                && (security.Permissions & PdfPermissions.ExtractForAccessibility) == 0)
                throw new ArgumentException("The PDF claims PDF/UA, which needs PdfPermissions.ExtractForAccessibility so screen readers can read an encrypted file.", nameof(security));
            using var crypt = new PdfEncryptor(security);
            return PdfRewriter.Write(file, crypt);
        }
    }

    private static ByteBuffer? DecryptCore(byte[] pdf, string? password, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (PdfFile.BeginRender(cancellationToken))
        {
            var file = Open(pdf, password);
            if (file.Decryptor == null) return null;
            CheckNotSigned(file);
            return PdfRewriter.Write(file, null);
        }
    }

    private static PdfFile Open(byte[] pdf, string? password)
    {
        try
        {
            var file = PdfFile.Open(pdf, password);
            if (file.Catalog == null) throw new InvalidDataException("The PDF is not readable: it has no catalog.");
            return file;
        }
        catch (PdfFormatException ex)
        {
            throw new InvalidDataException("The PDF is not readable: " + ex.Message, ex);
        }
    }

    /// <summary>Refuses a PDF with a signed signature field or a certification (DocMDP) or usage rights signature.</summary>
    internal static void CheckNotSigned(PdfFile file)
    {
        var catalog = file.Catalog;
        if (file.GetDict(catalog, "Perms") is { Entries.Count: > 0 } || HasSignedField(file, file.GetArray(file.GetDict(catalog, "AcroForm"), "Fields")))
            throw new ArgumentException("The PDF is digitally signed: rewriting it would break the signatures.", "pdf");
    }

    private static bool HasSignedField(PdfFile file, PdfArray? fields)
    {
        if (fields == null) return false;
        var seen = new HashSet<PdfDict>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject Field, string? InheritedType, int Depth)>();
        foreach (var f in fields.Items) stack.Push((f, null, 0));
        while (stack.Count > 0)
        {
            var (o, inherited, depth) = stack.Pop();
            if (depth > 64 || file.Resolve(o) is not PdfDict field || !seen.Add(field)) continue;
            var type = file.GetName(field, "FT") ?? inherited;
            if (type == "Sig" && file.Resolve(field["V"]) is PdfDict) return true;
            if (file.GetArray(field, "Kids") is { } kids)
                foreach (var k in kids.Items) stack.Push((k, type, depth + 1));
        }
        return false;
    }

    private static string? Xmp(PdfFile file)
    {
        if (file.Resolve(file.Catalog!["Metadata"]) is not PdfStream meta) return null;
        try
        {
            return Encoding.UTF8.GetString(file.DecodeStream(meta));
        }
        catch (Exception ex) when (ex is PdfFormatException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static void CheckStreams(Stream input, Stream output)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
    }
}

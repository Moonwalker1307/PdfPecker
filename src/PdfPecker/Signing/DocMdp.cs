using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Signing;

/// <summary>
/// Certification signatures (ISO 32000-1 12.8.2.2): the DocMDP transform a certifying signature declares, the catalog's
/// /Perms entry that names it, and the rules for signing a document that is already certified.
/// </summary>
internal static class DocMdp
{
    /// <summary>
    /// The DocMDP level, 1 to 3, a signature dictionary declares in its /Reference, or 0 when it is an approval signature.
    /// A missing /P means 2, as the standard says; a value outside 1 to 3 is read as 1, the strictest, so a damaged
    /// certification never allows more than it might have meant.
    /// </summary>
    public static int Level(PdfFile file, PdfDict sig)
    {
        if (file.GetArray(sig, "Reference") is not { } references) return 0;
        for (int i = 0; i < references.Count && i < 16; i++)
        {
            if (file.Resolve(references[i]) is not PdfDict reference || file.GetName(reference, "TransformMethod") != "DocMDP") continue;
            int p = file.GetInt(file.GetDict(reference, "TransformParams"), "P", 2);
            return p is >= 1 and <= 3 ? p : 1;
        }
        return 0;
    }

    /// <summary>The signature dictionary the catalog's /Perms /DocMDP names, or null when the document is not certified.</summary>
    public static PdfDict? Certification(PdfFile file) => file.GetDict(file.GetDict(file.Catalog, "Perms"), "DocMDP");

    /// <summary>
    /// Whether the signature whose value is <paramref name="contents"/> certified the revision that ends at
    /// <paramref name="revisionEnd"/>: that revision's catalog names it in /Perms. An update may write the catalog again
    /// without /Perms, so a certification is recognised by the revision it signed, not by the newest catalog.
    /// </summary>
    public static bool CertifiedRevision(PdfFile file, long revisionEnd, byte[] contents, string? password)
    {
        if (revisionEnd <= 0 || revisionEnd >= file.Data.Length) return false;
        try
        {
            var revision = PdfFile.Open(file.Data.AsSpan(0, (int)revisionEnd).ToArray(), password);
            return revision.Resolve(Certification(revision)?["Contents"]) is PdfString c && c.Bytes.AsSpan().SequenceEqual(contents);
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfPasswordException or InvalidDataException or ArgumentException
            or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws when a signature with <paramref name="level"/> cannot be added to <paramref name="file"/>: a certification
    /// must be the document's first signature, and a document certified with no changes allowed takes no more signatures.
    /// </summary>
    public static void CheckCanSign(PdfFile file, PdfCertificationLevel level)
    {
        if (level is < PdfCertificationLevel.None or > PdfCertificationLevel.FormFillingAndAnnotations)
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown certification level.");
        var certification = Certification(file);
        if (level != PdfCertificationLevel.None)
        {
            if (certification != null || SignatureScanner.Find(file).Count > 0)
                throw new PdfSignatureException("The PDF is already signed. Only the first signature of a document can certify it.");
        }
        else if (certification != null && Level(file, certification) == 1)
            throw new PdfSignatureException("The PDF is certified with no changes allowed, so another signature would break the certification.");
    }

    /// <summary>The words that say what <paramref name="level"/> allows, for the reports of disallowed changes.</summary>
    public static string Allows(int level) => level switch
    {
        1 => "which allows no changes",
        2 => "which allows only form filling and signing",
        _ => "which allows only form filling, signing and annotations",
    };
}

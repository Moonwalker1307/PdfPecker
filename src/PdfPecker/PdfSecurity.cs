namespace PdfPecker;

/// <summary>What a reader may do with a password-protected PDF when it is opened with the user password.</summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing beyond viewing.</summary>
    None = 0,
    /// <summary>Print the document (possibly at low resolution unless <see cref="PrintHighQuality"/> is also set).</summary>
    Print = 1 << 2,
    /// <summary>Change the document's contents.</summary>
    Modify = 1 << 3,
    /// <summary>Copy or extract text and graphics.</summary>
    Copy = 1 << 4,
    /// <summary>Add or change annotations and fill in form fields.</summary>
    Annotate = 1 << 5,
    /// <summary>Fill in existing form fields.</summary>
    FillForms = 1 << 8,
    /// <summary>Extract text and graphics for accessibility (screen readers).</summary>
    ExtractForAccessibility = 1 << 9,
    /// <summary>Insert, rotate or delete pages and create bookmarks.</summary>
    Assemble = 1 << 10,
    /// <summary>Print at full quality.</summary>
    PrintHighQuality = 1 << 11,
    /// <summary>Everything.</summary>
    All = Print | Modify | Copy | Annotate | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality,
}

/// <summary>
/// Password protection for the generated PDF: AES-256 encryption (PDF 2.0 / Acrobat X standard security handler, revision 6).
/// </summary>
public sealed class PdfSecurity
{
    /// <summary>
    /// The password needed to open the document. Null or empty: anyone can open it, but the <see cref="Permissions"/>
    /// still apply in readers that honour them.
    /// </summary>
    public string? UserPassword { get; set; }

    /// <summary>
    /// The password that gives full access regardless of <see cref="Permissions"/>. When not set, a random owner password
    /// is used, so the permissions cannot be lifted.
    /// </summary>
    public string? OwnerPassword { get; set; }

    /// <summary>What a reader may do when the document is opened with the user password. Default: print and copy for accessibility only.</summary>
    public PdfPermissions Permissions { get; set; } = PdfPermissions.Print | PdfPermissions.PrintHighQuality | PdfPermissions.ExtractForAccessibility;
}

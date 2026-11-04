namespace PdfPecker.Fluent;

public sealed partial class DocumentOperation
{
    /// <summary>Which pages of another PDF go over or under which pages of the result, for <see cref="OverlayFile"/> and <see cref="UnderlayFile"/>.</summary>
    /// <remarks>
    /// The pages named by <see cref="TargetPages"/> get, in order, the pages named by <see cref="SourcePages"/>; once those
    /// run out, the pages of <see cref="RepeatSourcePages"/> follow round and round, and without them the remaining target
    /// pages get nothing. A source page larger than its target page is shrunk to fit; any other is drawn at its own size.
    /// Either way it is centred, as in QuestPDF.
    /// </remarks>
    public sealed class LayerConfiguration
    {
        /// <summary>
        /// The PDF file whose pages are drawn. Relative paths are looked for in the current directory and then in the
        /// application's directory.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>The PDF whose pages are drawn, held in memory, instead of <see cref="FilePath"/>; QuestPDF does not have it.</summary>
        public byte[]? Pdf { get; set; }

        /// <summary>
        /// The pages of the result that get a layer, in the syntax of <see cref="TakePages"/>, such as <c>1</c> for the
        /// first page or <c>2-z</c> for every page after it. Default: null, every page.
        /// </summary>
        public string? TargetPages { get; set; }

        /// <summary>
        /// The pages of the layer PDF to use first, in order, in the syntax of <see cref="TakePages"/>. Default: null,
        /// all of them; an empty string uses none, so only <see cref="RepeatSourcePages"/> are drawn.
        /// </summary>
        public string? SourcePages { get; set; }

        /// <summary>
        /// The pages of the layer PDF to use again and again once <see cref="SourcePages"/> have run out, such as
        /// <c>z</c> to repeat the last page. Default: null, none.
        /// </summary>
        public string? RepeatSourcePages { get; set; }
    }

    /// <summary>How an attached file relates to the document (PDF/A-3's AFRelationship).</summary>
    public enum DocumentAttachmentRelationship
    {
        /// <summary>Data the visible content is drawn from, such as a table's values.</summary>
        Data,
        /// <summary>The original source the document was made from.</summary>
        Source,
        /// <summary>An alternative representation of the content, such as a machine-readable invoice.</summary>
        Alternative,
        /// <summary>A file that adds to the content.</summary>
        Supplement,
        /// <summary>No particular relationship.</summary>
        Unspecified,
    }

    /// <summary>A file to attach with <see cref="AddAttachment"/>.</summary>
    public sealed class DocumentAttachment
    {
        /// <summary>
        /// Kept so QuestPDF code compiles. PdfPecker names an attachment by its file name only, so the key is used only as
        /// the name when <see cref="AttachmentName"/> is not set and the data comes from <see cref="Data"/>.
        /// </summary>
        public string? Key { get; set; }

        /// <summary>
        /// The file to attach. Relative paths are looked for in the current directory and then in the application's
        /// directory.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>The file's content, held in memory, instead of <see cref="FilePath"/>; it needs <see cref="AttachmentName"/>. QuestPDF does not have it.</summary>
        public byte[]? Data { get; set; }

        /// <summary>The name shown in the viewer and used when the file is saved. Default: the file's name without its folder.</summary>
        public string? AttachmentName { get; set; }

        /// <summary>When the file was created. Default: null, not written.</summary>
        public DateTime? CreationDate { get; set; }

        /// <summary>
        /// When the file was last changed. Default: the file's last write time, or for <see cref="Data"/> the time the PDF
        /// is written (or its fixed date for reproducible output).
        /// </summary>
        public DateTime? ModificationDate { get; set; }

        /// <summary>The MIME type, such as <c>text/csv</c>. Default: guessed from the name's extension.</summary>
        public string? MimeType { get; set; }

        /// <summary>A description shown in the attachments panel.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Replace an attachment of the same name added earlier in the same operation. Default: true. An attachment the
        /// loaded PDF already has is never replaced.
        /// </summary>
        public bool Replace { get; set; } = true;

        /// <summary>How the file relates to the document. Default: null, unspecified.</summary>
        public DocumentAttachmentRelationship? Relationship { get; set; }

        internal PdfAttachment ToPdfAttachment()
        {
            if ((FilePath == null) == (Data == null))
                throw new ArgumentException("An attachment needs either a FilePath or Data.", "attachment");
            string? name = AttachmentName;
            byte[] data;
            DateTime? modified = ModificationDate;
            if (FilePath != null)
            {
                data = ReadFile(FilePath, nameof(FilePath));
                name ??= Path.GetFileName(FilePath);
                modified ??= ModifiedTime(FilePath);
            }
            else
            {
                data = Data!;
                name ??= Key ?? throw new ArgumentException("An attachment from Data needs an AttachmentName.", "attachment");
            }
            return new PdfAttachment(name, data)
            {
                MimeType = MimeType,
                Description = Description,
                CreationDate = CreationDate is { } created ? new DateTimeOffset(created.ToUniversalTime()) : null,
                ModDate = modified is { } changed ? new DateTimeOffset(changed.ToUniversalTime()) : null,
                Relationship = Relationship switch
                {
                    DocumentAttachmentRelationship.Data => PdfAttachmentRelationship.Data,
                    DocumentAttachmentRelationship.Source => PdfAttachmentRelationship.Source,
                    DocumentAttachmentRelationship.Alternative => PdfAttachmentRelationship.Alternative,
                    DocumentAttachmentRelationship.Supplement => PdfAttachmentRelationship.Supplement,
                    null or DocumentAttachmentRelationship.Unspecified => PdfAttachmentRelationship.Unspecified,
                    _ => throw new ArgumentOutOfRangeException("attachment", Relationship, "Not a DocumentAttachmentRelationship value."),
                },
            };
        }

        private static DateTime ModifiedTime(string path)
        {
            string found = !Path.IsPathRooted(path) && !File.Exists(path) ? Path.Combine(AppContext.BaseDirectory, path) : path;
            return File.GetLastWriteTimeUtc(found);
        }
    }

    /// <summary>The passwords every <c>Encrypt</c> call takes.</summary>
    public abstract class EncryptionBase
    {
        private protected EncryptionBase() { }

        /// <summary>
        /// The password needed to open the PDF. Null or empty: anyone can open it, but the permissions still apply in
        /// readers that honour them.
        /// </summary>
        public string? UserPassword { get; set; }

        /// <summary>
        /// The password that opens the PDF with every permission. Null or empty: a random one, so the permissions cannot
        /// be lifted.
        /// </summary>
        public string? OwnerPassword { get; set; }

        internal abstract PdfPermissions Permissions { get; }

        internal PdfSecurity ToSecurity() => new()
        {
            UserPassword = UserPassword,
            OwnerPassword = string.IsNullOrEmpty(OwnerPassword) ? null : OwnerPassword,
            // Screen readers may always extract the text, as PDF 2.0 asks and PDF/UA needs.
            Permissions = Permissions | PdfPermissions.ExtractForAccessibility,
        };

        private protected static PdfPermissions If(bool allowed, PdfPermissions permissions) => allowed ? permissions : PdfPermissions.None;
    }

    /// <summary>
    /// QuestPDF's 40-bit RC4 settings. PdfPecker encrypts with AES-256 instead, since RC4 is broken; the permissions are
    /// those of 40-bit encryption, where changing the document includes assembling it and annotating includes filling in
    /// forms.
    /// </summary>
    public sealed class Encryption40Bit : EncryptionBase
    {
        /// <summary>Whether annotations can be added and forms filled in. Default: true.</summary>
        public bool AllowAnnotation { get; set; } = true;

        /// <summary>Whether text and graphics can be copied. Default: true.</summary>
        public bool AllowContentExtraction { get; set; } = true;

        /// <summary>Whether the document and its pages can be changed. Default: true.</summary>
        public bool AllowModification { get; set; } = true;

        /// <summary>Whether the document can be printed. Default: true.</summary>
        public bool AllowPrinting { get; set; } = true;

        internal override PdfPermissions Permissions =>
            If(AllowAnnotation, PdfPermissions.Annotate | PdfPermissions.FillForms) | If(AllowContentExtraction, PdfPermissions.Copy)
            | If(AllowModification, PdfPermissions.Modify | PdfPermissions.Assemble) | If(AllowPrinting, PdfPermissions.Print | PdfPermissions.PrintHighQuality);
    }

    /// <summary>QuestPDF's 128-bit settings. PdfPecker encrypts with AES-256 instead, with the same permissions.</summary>
    public sealed class Encryption128Bit : EncryptionBase
    {
        /// <summary>Whether annotations can be added. Default: true.</summary>
        public bool AllowAnnotation { get; set; } = true;

        /// <summary>Whether pages can be inserted, rotated or deleted. Default: true.</summary>
        public bool AllowAssembly { get; set; } = true;

        /// <summary>Whether text and graphics can be copied. Default: true.</summary>
        public bool AllowContentExtraction { get; set; } = true;

        /// <summary>Whether form fields can be filled in. Default: true.</summary>
        public bool AllowFillingForms { get; set; } = true;

        /// <summary>Whether the document's content can be changed. Default: true.</summary>
        public bool AllowModification { get; set; } = true;

        /// <summary>Whether the document can be printed. Default: true.</summary>
        public bool AllowPrinting { get; set; } = true;

        /// <summary>Kept so QuestPDF code compiles: PdfPecker always encrypts the metadata. Default: true.</summary>
        public bool EncryptMetadata { get; set; } = true;

        internal override PdfPermissions Permissions =>
            Full(AllowAnnotation, AllowAssembly, AllowContentExtraction, AllowFillingForms, AllowModification, AllowPrinting);

        internal static PdfPermissions Full(bool annotate, bool assemble, bool extract, bool forms, bool modify, bool print) =>
            If(annotate, PdfPermissions.Annotate) | If(assemble, PdfPermissions.Assemble) | If(extract, PdfPermissions.Copy)
            | If(forms, PdfPermissions.FillForms) | If(modify, PdfPermissions.Modify) | If(print, PdfPermissions.Print | PdfPermissions.PrintHighQuality);
    }

    /// <summary>QuestPDF's 256-bit settings: AES-256, as PdfPecker always uses.</summary>
    public sealed class Encryption256Bit : EncryptionBase
    {
        /// <summary>Whether annotations can be added. Default: true.</summary>
        public bool AllowAnnotation { get; set; } = true;

        /// <summary>Whether pages can be inserted, rotated or deleted. Default: true.</summary>
        public bool AllowAssembly { get; set; } = true;

        /// <summary>Whether text and graphics can be copied. Default: true.</summary>
        public bool AllowContentExtraction { get; set; } = true;

        /// <summary>Whether form fields can be filled in. Default: true.</summary>
        public bool AllowFillingForms { get; set; } = true;

        /// <summary>Whether the document's content can be changed. Default: true.</summary>
        public bool AllowModification { get; set; } = true;

        /// <summary>Whether the document can be printed. Default: true.</summary>
        public bool AllowPrinting { get; set; } = true;

        /// <summary>Kept so QuestPDF code compiles: PdfPecker always encrypts the metadata. Default: true.</summary>
        public bool EncryptMetadata { get; set; } = true;

        internal override PdfPermissions Permissions =>
            Encryption128Bit.Full(AllowAnnotation, AllowAssembly, AllowContentExtraction, AllowFillingForms, AllowModification, AllowPrinting);
    }
}

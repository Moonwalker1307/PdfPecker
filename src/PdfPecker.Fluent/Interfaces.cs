using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>
/// A slot for one piece of content. Styling calls (<c>Padding</c>, <c>Background</c>, <c>AlignRight</c>, ...) return
/// the slot inside them; a content call (<c>Text</c>, <c>Column</c>, <c>Row</c>, <c>Table</c>, <c>Image</c>, ...) fills
/// it. A slot takes one content call; use a column or row for several.
/// </summary>
public interface IContainer;

/// <summary>A table cell's slot, which can also span rows and columns or be placed at a given row and column.</summary>
public interface ITableCellContainer : IContainer;

/// <summary>The document being composed: add pages to it with <c>Page</c>.</summary>
public interface IDocumentContainer;

/// <summary>A reusable piece of layout, composed into a slot with <c>container.Component(...)</c>.</summary>
public interface IComponent
{
    /// <summary>Fills the slot.</summary>
    void Compose(IContainer container);
}

/// <summary>
/// A document defined as a class, the alternative to <see cref="Document.Create"/>. Generate it with the
/// <see cref="GenerateExtensions"/> methods, such as <c>new InvoiceDocument(model).GeneratePdf()</c>.
/// </summary>
public interface IDocument
{
    /// <summary>Adds the pages.</summary>
    void Compose(IDocumentContainer container);

    /// <summary>The title, author and other information stored in the PDF.</summary>
    DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    /// <summary>PDF/A, PDF/UA and compression.</summary>
    DocumentSettings GetSettings() => DocumentSettings.Default;
}

internal sealed class DocumentContainer : IDocumentContainer
{
    public List<PageSet> Pages { get; } = [];
    public List<string> Css { get; } = [];

    public static DocumentContainer Of(IDocumentContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container as DocumentContainer
            ?? throw new ArgumentException("Only document containers that PdfPecker.Fluent creates can be used.", nameof(container));
    }
}

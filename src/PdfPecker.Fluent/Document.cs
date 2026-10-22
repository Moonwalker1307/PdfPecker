namespace PdfPecker.Fluent;

/// <summary>Information stored in the PDF. Properties left null are taken from <see cref="PdfOptions"/>, if set there.</summary>
public sealed class DocumentMetadata
{
    /// <summary>A new, empty set of metadata.</summary>
    public static DocumentMetadata Default => new();

    /// <summary>The title, shown by PDF viewers instead of the file name.</summary>
    public string? Title { get; set; }

    /// <summary>The author.</summary>
    public string? Author { get; set; }

    /// <summary>The subject.</summary>
    public string? Subject { get; set; }

    /// <summary>Keywords.</summary>
    public string? Keywords { get; set; }

    /// <summary>The application that created the document.</summary>
    public string? Creator { get; set; }

    /// <summary>The application that wrote the PDF.</summary>
    public string? Producer { get; set; }

    /// <summary>The document language, such as "en" or "da-DK"; needed for PDF/UA.</summary>
    public string? Language { get; set; }

    /// <summary>The creation date. A fixed date makes the output byte-identical from run to run.</summary>
    public DateTimeOffset? CreationDate { get; set; }
}

/// <summary>Standards and compression. <see cref="PdfOptions"/> has every other PdfPecker setting.</summary>
public sealed class DocumentSettings
{
    /// <summary>The default settings.</summary>
    public static DocumentSettings Default => new();

    /// <summary>Writes PDF/A-3b for archiving, unless <see cref="PdfOptions.Conformance"/> asks for another part.</summary>
    public bool PdfA { get; set; }

    /// <summary>Writes tagged PDF/UA-1 for accessibility, unless <see cref="PdfOptions.Accessibility"/> is set.</summary>
    public bool PdfUA { get; set; }

    /// <summary>Compresses the PDF's streams (on by default).</summary>
    public bool CompressDocument { get; set; } = true;
}

/// <summary>
/// A document composed with a lambda: <c>Document.Create(container =&gt; container.Page(page =&gt; ...))</c>.
/// Generate it with <c>GeneratePdf()</c>, <c>GenerateHtml()</c> or the other <see cref="GenerateExtensions"/>.
/// </summary>
public sealed class Document : IDocument
{
    private readonly Action<IDocumentContainer> _compose;
    private DocumentMetadata _metadata = DocumentMetadata.Default;
    private DocumentSettings _settings = DocumentSettings.Default;

    private Document(Action<IDocumentContainer> compose) => _compose = compose;

    /// <summary>A document whose pages <paramref name="compose"/> adds. It runs again each time the document is generated.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="compose"/> is null.</exception>
    public static Document Create(Action<IDocumentContainer> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);
        return new Document(compose);
    }

    /// <summary>Sets the title, author and other information stored in the PDF.</summary>
    public Document WithMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        return this;
    }

    /// <summary>Sets PDF/A, PDF/UA and compression.</summary>
    public Document WithSettings(DocumentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        return this;
    }

    /// <inheritdoc />
    public void Compose(IDocumentContainer container) => _compose(container);

    /// <inheritdoc />
    public DocumentMetadata GetMetadata() => _metadata;

    /// <inheritdoc />
    public DocumentSettings GetSettings() => _settings;
}

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

    /// <summary>
    /// One document made of several, one after the other, such as a batch of invoices in one PDF. Each document keeps
    /// its own page sets, and each starts on a new page. Page numbers count within each document unless
    /// <see cref="MergedDocument.UseContinuousPageNumbers"/> is called.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> or one of them is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="documents"/> is empty.</exception>
    public static MergedDocument Merge(params IDocument[] documents) => new(documents);

    /// <inheritdoc cref="Merge(IDocument[])" />
    public static MergedDocument Merge(IEnumerable<IDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return new MergedDocument([.. documents]);
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

/// <summary>
/// Several documents generated as one, which <see cref="Document.Merge(IDocument[])"/> makes. It is generated with the
/// same <see cref="GenerateExtensions"/> as any document, into one PDF or one HTML document.
/// </summary>
public sealed class MergedDocument : IDocument
{
    private readonly IDocument[] _documents;
    private bool _continuous;
    private DocumentMetadata? _metadata;
    private DocumentSettings? _settings;

    internal MergedDocument(IDocument[] documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Length == 0) throw new ArgumentException("Merge needs at least one document.", nameof(documents));
        foreach (var document in documents)
            if (document == null) throw new ArgumentNullException(nameof(documents), "A document to merge is null.");
        _documents = (IDocument[])documents.Clone();
    }

    /// <summary>
    /// Counts the pages of each document on its own, as if it were generated alone (the default): page numbers start
    /// at 1 in each document, the total is the document's page count, and section names belong to the document they
    /// are in, so the same name can be used in each. Section page numbers, such as
    /// <c>BeginPageNumberOfSection</c>, still count the pages of the whole PDF.
    /// </summary>
    public MergedDocument UseOriginalPageNumbers()
    {
        _continuous = false;
        return this;
    }

    /// <summary>
    /// Counts the pages through all the documents as through one: page numbers run on, the total is the page count of
    /// the whole PDF, and section names are shared, so a link in one document can lead to a section of another.
    /// </summary>
    public MergedDocument UseContinuousPageNumbers()
    {
        _continuous = true;
        return this;
    }

    /// <summary>Sets the information stored in the PDF; without it, the first document's is used.</summary>
    public MergedDocument WithMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata = metadata;
        return this;
    }

    /// <summary>Sets PDF/A, PDF/UA and compression; without it, the first document's settings are used.</summary>
    public MergedDocument WithSettings(DocumentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        return this;
    }

    /// <inheritdoc />
    public void Compose(IDocumentContainer container)
    {
        var target = DocumentContainer.Of(container);
        int outer = target.Document;
        bool outerContinuous = target.Continuous;
        // A merge inside a merge with continuous numbers numbers on with it, whatever it was set to.
        target.Continuous |= _continuous;
        foreach (var document in _documents)
        {
            // With original numbers each document gets a number of its own, which the writer turns into an element
            // whose pages its page numbers count; with continuous numbers they all share the number they start with.
            if (!target.Continuous) target.StartDocument();
            document.Compose(container);
        }
        target.Continuous = outerContinuous;
        target.Document = outer;
    }

    /// <inheritdoc />
    public DocumentMetadata GetMetadata() => _metadata ?? _documents[0].GetMetadata();

    /// <inheritdoc />
    public DocumentSettings GetSettings() => _settings ?? _documents[0].GetSettings();
}

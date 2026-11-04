using PdfPecker.Fluent.Operations;

namespace PdfPecker.Fluent;

/// <summary>
/// Works on an existing PDF, in the style of QuestPDF's <c>DocumentOperation</c>: takes or reorders its pages, appends
/// pages of other PDFs, puts the pages of another PDF over or under its pages, attaches files, and encrypts, decrypts or
/// linearizes the result. Load a PDF with <see cref="LoadFile"/> or <see cref="Load"/>, chain the operations and call
/// <see cref="Save(string)"/> or <see cref="GeneratePdf"/>.
/// </summary>
/// <remarks>
/// <para>
/// The operations run in a fixed order whatever the order of the calls, as in QuestPDF: first the pages are chosen and
/// merged, then the underlays and overlays are drawn on the result's pages, then the files are attached, and last the
/// result is encrypted or decrypted and linearized. The work is done by PdfPecker's <see cref="PdfMerger"/>,
/// <see cref="PdfStamper"/>, <see cref="PdfAttachments"/>, <see cref="PdfProtector"/> and <see cref="PdfLinearizer"/>, so
/// any PDF can be loaded, not only those PdfPecker writes, and the inputs are treated as untrusted.
/// </para>
/// <para>
/// Files named by a relative path are looked for in the current directory first and then in the application's
/// directory, as in QuestPDF. Every file is read when the call that names it is made, so a missing file fails early,
/// and the result can be saved over the loaded file.
/// </para>
/// </remarks>
public sealed partial class DocumentOperation
{
    private readonly byte[] _pdf;
    private readonly string? _password;
    private List<(byte[]? Pdf, string Selector)>? _pages; // null Pdf: the loaded document
    private readonly List<(LayerConfiguration Layer, byte[] Pdf)> _underlays = [];
    private readonly List<(LayerConfiguration Layer, byte[] Pdf)> _overlays = [];
    private readonly List<PdfAttachment> _attachments = [];
    private PdfSecurity? _encryption;
    private bool _decrypt, _linearize;

    private DocumentOperation(byte[] pdf, string? password)
    {
        _pdf = pdf;
        _password = password;
    }

    /// <summary>
    /// Loads the PDF file to work on. Relative paths are looked for in the current directory and then in the
    /// application's directory.
    /// </summary>
    /// <param name="filePath">The PDF file.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
    /// <exception cref="FileNotFoundException">The file is in none of the places looked at.</exception>
    public static DocumentOperation LoadFile(string filePath, string? password = null)
        => new(ReadFile(filePath, nameof(filePath)), password);

    /// <summary>Loads the PDF to work on from memory, which QuestPDF does not have.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    public static DocumentOperation Load(byte[] pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new(pdf, password);
    }

    /// <summary>
    /// Takes the pages named by <paramref name="pageSelector"/> from the loaded PDF, in the order named; the pages not
    /// named are left out. Called again, it appends more pages of the loaded PDF.
    /// </summary>
    /// <param name="pageSelector">
    /// Pages in qpdf's syntax, as in QuestPDF: <c>1,6,4</c> (in that order), <c>3-7</c>, <c>7-3</c> (backwards), <c>z</c>
    /// (the last page), <c>r3-r1</c> (the last three pages), <c>1-10,x3-4</c> (1 to 10 except 3 and 4), and
    /// <c>1-z:even</c> (the pages in even positions of the list before the colon).
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pageSelector"/> is not a valid page selection.</exception>
    public DocumentOperation TakePages(string pageSelector)
    {
        PageSelector.Validate(pageSelector, nameof(pageSelector));
        (_pages ??= []).Add((null, pageSelector));
        return this;
    }

    /// <summary>
    /// Appends the pages named by <paramref name="pageSelector"/> (all of them when null) of another PDF file. When
    /// <see cref="TakePages"/> has not been called first, every page of the loaded PDF comes before them.
    /// </summary>
    /// <param name="filePath">The PDF file to append; relative paths are looked for as with <see cref="LoadFile"/>.</param>
    /// <param name="pageSelector">The pages to take, in the syntax of <see cref="TakePages"/>; null for all.</param>
    /// <exception cref="ArgumentException">A path or selector is empty or not valid.</exception>
    /// <exception cref="FileNotFoundException">The file is in none of the places looked at.</exception>
    public DocumentOperation MergeFile(string filePath, string? pageSelector = null)
    {
        if (pageSelector != null) PageSelector.Validate(pageSelector, nameof(pageSelector));
        return Merge(ReadFile(filePath, nameof(filePath)), pageSelector);
    }

    /// <summary>Appends pages of a PDF held in memory, like <see cref="MergeFile"/>; QuestPDF does not have it.</summary>
    /// <param name="pdf">The PDF to append. It cannot be encrypted with a user password.</param>
    /// <param name="pageSelector">The pages to take, in the syntax of <see cref="TakePages"/>; null for all.</param>
    /// <exception cref="ArgumentException"><paramref name="pageSelector"/> is not a valid page selection.</exception>
    public DocumentOperation Merge(byte[] pdf, string? pageSelector = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pageSelector != null) PageSelector.Validate(pageSelector, nameof(pageSelector));
        if (_pages == null) TakePages("1-z");
        _pages!.Add((pdf, pageSelector ?? "1-z"));
        return this;
    }

    /// <summary>
    /// Draws pages of another PDF underneath the content of the result's pages, like letterhead paper. Content that
    /// paints an opaque background over the whole page hides an underlay.
    /// </summary>
    /// <exception cref="ArgumentException">The configuration names no file, or a page selector is not valid.</exception>
    /// <exception cref="FileNotFoundException">The file is in none of the places looked at.</exception>
    public DocumentOperation UnderlayFile(LayerConfiguration configuration)
    {
        _underlays.Add(Layer(configuration));
        return this;
    }

    /// <summary>Draws pages of another PDF on top of the content of the result's pages, such as a "COPY" stamp.</summary>
    /// <exception cref="ArgumentException">The configuration names no file, or a page selector is not valid.</exception>
    /// <exception cref="FileNotFoundException">The file is in none of the places looked at.</exception>
    public DocumentOperation OverlayFile(LayerConfiguration configuration)
    {
        _overlays.Add(Layer(configuration));
        return this;
    }

    /// <summary>
    /// Attaches a file, listed in the viewer's attachments panel. An attachment of the same name added earlier in this
    /// operation is replaced when <see cref="DocumentAttachment.Replace"/> is set; one the loaded PDF already has is not,
    /// and fails with <see cref="ArgumentException"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The attachment names neither or both of a file and data, its name is not a file name, or an attachment of that
    /// name was added before and <see cref="DocumentAttachment.Replace"/> is not set.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file is in none of the places looked at.</exception>
    public DocumentOperation AddAttachment(DocumentAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var file = attachment.ToPdfAttachment();
        int existing = _attachments.FindIndex(a => string.Equals(a.FileName, file.FileName, StringComparison.Ordinal));
        if (existing >= 0 && !attachment.Replace)
            throw new ArgumentException($"An attachment named \"{file.FileName}\" was added before, and Replace is not set.", nameof(attachment));
        if (existing >= 0) _attachments[existing] = file;
        else _attachments.Add(file);
        return this;
    }

    /// <summary>
    /// Saves the result without encryption. A loaded PDF that is encrypted needs this or one of the <c>Encrypt</c> calls
    /// when its pages change or it is linearized, so its protection is never dropped by accident.
    /// </summary>
    public DocumentOperation Decrypt()
    {
        _decrypt = true;
        return this;
    }

    /// <summary>Encrypts the result. PdfPecker always uses AES-256, the strongest encryption PDF has, for every <c>Encrypt</c> call.</summary>
    /// <exception cref="InvalidOperationException">An <c>Encrypt</c> call was made before.</exception>
    public DocumentOperation Encrypt(Encryption40Bit encryption) => SetEncryption(encryption);

    /// <inheritdoc cref="Encrypt(Encryption40Bit)"/>
    public DocumentOperation Encrypt(Encryption128Bit encryption) => SetEncryption(encryption);

    /// <inheritdoc cref="Encrypt(Encryption40Bit)"/>
    public DocumentOperation Encrypt(Encryption256Bit encryption) => SetEncryption(encryption);

    /// <summary>
    /// Linearizes the result for Fast Web View: a viewer that loads it over the web shows the first page before the rest
    /// has arrived. It combines with encryption.
    /// </summary>
    public DocumentOperation Linearize()
    {
        _linearize = true;
        return this;
    }

    /// <summary>Runs the operations and writes the result to a file, replacing it if it exists.</summary>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty, or an operation does not fit the
    /// PDF: a page selector names a page it does not have, or a PDF cannot take what is asked, such as encryption of a
    /// PDF/A file or an attachment it already has.</exception>
    /// <exception cref="InvalidOperationException">The loaded PDF is encrypted and the result would not be, but neither
    /// <see cref="Decrypt"/> nor an <c>Encrypt</c> call was made.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF.</exception>
    /// <exception cref="PdfLimitException">The result would have more pages than PdfPecker allows.</exception>
    public void Save(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        File.WriteAllBytes(filePath, GeneratePdf());
    }

    /// <summary>Runs the operations and writes the result to a stream; QuestPDF does not have it.</summary>
    /// <inheritdoc cref="GeneratePdf" path="/exception"/>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(GeneratePdf());
    }

    /// <summary>Runs the operations and returns the resulting PDF; QuestPDF does not have it.</summary>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException">An operation does not fit the PDF: a page selector names a page it does not
    /// have, or a PDF cannot take what is asked, such as encryption of a PDF/A file or an attachment it already has.</exception>
    /// <exception cref="InvalidOperationException">The loaded PDF is encrypted and the result would not be, but neither
    /// <see cref="Decrypt"/> nor an <c>Encrypt</c> call was made.</exception>
    /// <exception cref="PdfPasswordException">An input is encrypted and its password is missing or wrong.</exception>
    /// <exception cref="InvalidDataException">An input is not a readable PDF.</exception>
    /// <exception cref="PdfLimitException">The result would have more pages than PdfPecker allows.</exception>
    public byte[] GeneratePdf(CancellationToken cancellationToken = default)
    {
        byte[] pdf = _pdf;
        string? password = _password;
        bool rewritten = _pages != null || _underlays.Count > 0 || _overlays.Count > 0 || _linearize;
        if (rewritten && !_decrypt && _encryption == null && PdfProtector.IsEncrypted(pdf))
            throw new InvalidOperationException("The loaded PDF is encrypted and these operations write it out unencrypted: call Decrypt() to "
                + "save it so, or Encrypt(...) to protect the result.");

        if (_pages != null)
        {
            var inputs = new List<PdfMergeInput>(_pages.Count);
            var passwords = new List<string?>(_pages.Count);
            var counts = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
            foreach (var (source, selector) in _pages)
            {
                byte[] input = source ?? pdf;
                string? inputPassword = source == null ? password : null;
                if (!counts.TryGetValue(input, out int count)) counts[input] = count = PdfPages.Count(input, inputPassword);
                var pages = PageSelector.Resolve(selector, count, "pageSelector");
                if (pages.Count == 0) continue;
                inputs.Add(new PdfMergeInput(input, PdfPageRange.FromPages(pages)));
                passwords.Add(inputPassword);
            }
            if (inputs.Count == 0) throw new ArgumentException("The page selectors choose no pages.", "pageSelector");
            pdf = PdfMerger.Merge(inputs, new PdfMergeOptions { Passwords = passwords }, cancellationToken);
            password = null;
        }

        // QuestPDF draws the first underlay lowest; each stamp goes under all that is on the page, so they go in reverse.
        for (int i = _underlays.Count - 1; i >= 0; i--)
            pdf = Layers.Apply(pdf, ref password, _underlays[i].Layer, _underlays[i].Pdf, underlay: true, cancellationToken);
        foreach (var (layer, source) in _overlays)
            pdf = Layers.Apply(pdf, ref password, layer, source, underlay: false, cancellationToken);

        if (_attachments.Count > 0) pdf = PdfAttachments.Add(pdf, _attachments, password, cancellationToken);

        if (_linearize) return PdfLinearizer.Linearize(pdf, _encryption, password, cancellationToken);
        if (_encryption != null) return PdfProtector.Encrypt(pdf, _encryption, password, cancellationToken);
        if (_decrypt && PdfProtector.IsEncrypted(pdf)) return PdfProtector.Decrypt(pdf, password, cancellationToken);
        return pdf;
    }

    private DocumentOperation SetEncryption(EncryptionBase encryption)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        if (_encryption != null) throw new InvalidOperationException("Encryption can be set only once.");
        _encryption = encryption.ToSecurity();
        return this;
    }

    private static (LayerConfiguration, byte[]) Layer(LayerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.TargetPages != null) PageSelector.Validate(configuration.TargetPages, nameof(configuration.TargetPages));
        if (!string.IsNullOrWhiteSpace(configuration.SourcePages)) PageSelector.Validate(configuration.SourcePages, nameof(configuration.SourcePages));
        if (!string.IsNullOrWhiteSpace(configuration.RepeatSourcePages))
            PageSelector.Validate(configuration.RepeatSourcePages, nameof(configuration.RepeatSourcePages));
        if (configuration.Pdf != null && configuration.FilePath != null)
            throw new ArgumentException("Give the layer's FilePath or its Pdf, not both.", nameof(configuration));
        byte[] pdf = configuration.Pdf ?? ReadFile(configuration.FilePath ?? throw new ArgumentException("The layer names no FilePath or Pdf.", nameof(configuration)),
            nameof(configuration.FilePath));
        // A copy, so later changes to the caller's object do not change this operation.
        var copy = new LayerConfiguration
        {
            TargetPages = configuration.TargetPages,
            SourcePages = configuration.SourcePages,
            RepeatSourcePages = configuration.RepeatSourcePages,
        };
        return (copy, pdf);
    }

    /// <summary>Reads a file, looking for a relative path in the current directory and then in the application's.</summary>
    internal static byte[] ReadFile(string path, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, parameter);
        if (!Path.IsPathRooted(path) && !File.Exists(path))
        {
            string beside = Path.Combine(AppContext.BaseDirectory, path);
            if (File.Exists(beside)) return File.ReadAllBytes(beside);
        }
        if (!File.Exists(path))
            throw new FileNotFoundException($"\"{path}\" was found neither in the current directory nor in the application's directory.", path);
        return File.ReadAllBytes(path);
    }
}

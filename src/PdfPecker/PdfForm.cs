using PdfPecker.Pdf;

namespace PdfPecker;

/// <summary>The kind of a form field in an existing PDF.</summary>
public enum PdfFormFieldType
{
    /// <summary>A text field, on one line or several.</summary>
    Text,
    /// <summary>A check box.</summary>
    CheckBox,
    /// <summary>A group of radio buttons, of which one at most is on.</summary>
    RadioButton,
    /// <summary>A drop-down list, possibly editable.</summary>
    ComboBox,
    /// <summary>A list box, possibly with several choices.</summary>
    ListBox,
    /// <summary>A push button: it has no value.</summary>
    PushButton,
    /// <summary>A signature field: <see cref="PdfSigner"/> signs it, it cannot be filled.</summary>
    Signature,
}

/// <summary>A choice of a combo box, list box, radio button group or check box.</summary>
public sealed class PdfFormFieldOption
{
    internal PdfFormFieldOption(string value, string text)
    {
        Value = value;
        Text = text;
    }

    /// <summary>The value the field takes when this is chosen (its export value or, for buttons, its state name).</summary>
    public string Value { get; }

    /// <summary>The text shown for it; the same as <see cref="Value"/> unless the PDF gives a separate text.</summary>
    public string Text { get; }

    /// <inheritdoc/>
    public override string ToString() => Value == Text ? Value : Value + " (" + Text + ")";
}

/// <summary>A form field of an existing PDF, as <see cref="PdfForm.GetFields(byte[], string?)"/> reads it.</summary>
public sealed class PdfFormField
{
    internal PdfFormField(string name, PdfFormFieldType type, IReadOnlyList<string> values, IReadOnlyList<PdfFormFieldOption> options,
        bool readOnly, bool required, bool multiline, bool password, bool multiSelect, bool editable, int maxLength, string? toolTip, int page)
    {
        Name = name;
        Type = type;
        Values = values;
        Options = options;
        ReadOnly = readOnly;
        Required = required;
        Multiline = multiline;
        Password = password;
        MultiSelect = multiSelect;
        Editable = editable;
        MaxLength = maxLength;
        ToolTip = toolTip;
        Page = page;
    }

    /// <summary>The fully qualified name: the names of the field and its ancestors joined with dots, such as "address.city".</summary>
    public string Name { get; }

    /// <summary>The kind of field.</summary>
    public PdfFormFieldType Type { get; }

    /// <summary>
    /// The current value: the text of a text field, the chosen value of a combo box or list box, the state name of the
    /// button that is on (or "Off"), or null when the field has none.
    /// </summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>All current values; more than one only for a list box with several choices.</summary>
    public IReadOnlyList<string> Values { get; }

    /// <summary>
    /// The choices of a combo box or list box, the buttons of a radio button group, and the on state of a check box;
    /// empty for other fields.
    /// </summary>
    public IReadOnlyList<PdfFormFieldOption> Options { get; }

    /// <summary>Whether the user may not change the field.</summary>
    public bool ReadOnly { get; }

    /// <summary>Whether the field must have a value when the form is submitted.</summary>
    public bool Required { get; }

    /// <summary>Whether a text field takes several lines.</summary>
    public bool Multiline { get; }

    /// <summary>Whether a text field hides what is typed.</summary>
    public bool Password { get; }

    /// <summary>Whether several choices of a list box can be chosen.</summary>
    public bool MultiSelect { get; }

    /// <summary>Whether a combo box takes text other than its choices.</summary>
    public bool Editable { get; }

    /// <summary>The most characters a text field takes, or 0 for no limit.</summary>
    public int MaxLength { get; }

    /// <summary>The field's tooltip (its alternate name), or null.</summary>
    public string? ToolTip { get; }

    /// <summary>The page of the field's first widget, numbered from 1, or 0 when the field has no widget on a page.</summary>
    public int Page { get; }

    /// <inheritdoc/>
    public override string ToString() => Name + " = " + (Value ?? "(none)");
}

/// <summary>Options for <see cref="PdfForm.Fill(byte[], IDictionary{string, string}, PdfFormFillOptions?, CancellationToken)"/>.</summary>
public sealed class PdfFormFillOptions
{
    /// <summary>
    /// The password of an encrypted PDF: its user or owner password. Not needed for a PDF that only restricts permissions.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// After filling, draw every form field into its page and remove the fields, so the values can no longer be changed
    /// (other annotations, such as links and comments, stay). Default: false.
    /// </summary>
    public bool Flatten { get; set; }

    /// <summary>
    /// A TrueType or OpenType font (file data) for values a field's own font cannot show. Default: null, the bundled
    /// Liberation Sans, which covers Latin, Greek and Cyrillic.
    /// </summary>
    public byte[]? Font { get; set; }

    /// <summary>
    /// Ignore names in the values that are no field of the PDF, instead of throwing <see cref="ArgumentException"/>.
    /// Default: false.
    /// </summary>
    public bool IgnoreUnknownFields { get; set; }
}

/// <summary>
/// Reads, fills and flattens the form fields (AcroForm) of existing PDFs: any PDFs, not only those this library makes.
/// </summary>
/// <remarks>
/// <para>
/// Fields are named by their fully qualified names, the names of a field and its ancestors joined with dots, as
/// <see cref="GetFields(byte[], string?)"/> lists them. A text field takes any text; a check box takes its on state
/// (see <see cref="PdfFormField.Options"/>), or "true", "yes", "on" or "1" for it, and "Off", "false", "no", "0" or ""
/// to clear it; a radio button group takes the state name of one of its buttons, or "Off"; a combo box and a list box
/// take a choice's value or its text (an editable combo box takes any text), and a list box with several choices takes
/// them separated by line feeds.
/// </para>
/// <para>
/// Every filled field gets a new appearance, drawn with the font, size, colour and alignment of its default appearance
/// (/DA and /Q): automatic size, several lines and comb fields are laid out, and boxes and backgrounds of the old
/// appearance are kept. When the field's own font cannot show the value, a subset of <see cref="PdfFormFillOptions.Font"/>
/// is embedded instead. Text is not shaped, so scripts that need shaping, such as Arabic or Devanagari, do not show
/// correctly; their values are still stored. XFA form data, which would override the filled fields, is removed.
/// </para>
/// <para>
/// A digitally signed or encrypted PDF is filled with an incremental update: the original bytes are kept, so existing
/// signatures stay valid (as long as their permissions allow form filling) and the encryption stays as it is. Other PDFs
/// are written out again as a whole, without the old values in earlier revisions. Flattening changes pages, so it is
/// refused for signed PDFs.
/// </para>
/// </remarks>
public static class PdfForm
{
    /// <summary>The form fields of a PDF, in the order of the form's field tree; empty when it has no form.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static IReadOnlyList<PdfFormField> GetFields(byte[] pdf, string? password = null)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        return PdfFormEditor.GetFields(pdf, password);
    }

    /// <summary>Reads a PDF from <paramref name="pdf"/> (from its current position to the end) and returns its form fields. The stream is not disposed.</summary>
    /// <inheritdoc cref="GetFields(byte[], string?)"/>
    public static IReadOnlyList<PdfFormField> GetFields(Stream pdf, string? password = null)
        => GetFields(PdfMerger.ReadAll(pdf ?? throw new ArgumentNullException(nameof(pdf))), password);

    /// <summary>Fills form fields of a PDF and returns the filled PDF; with <see cref="PdfFormFillOptions.Flatten"/>, also flattens it.</summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="values">The values by fully qualified field name (see the remarks on <see cref="PdfForm"/>).</param>
    /// <param name="options">Password, flattening and the fallback font; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="ArgumentException">A name is not a field (unless <see cref="PdfFormFillOptions.IgnoreUnknownFields"/>), a
    /// value does not suit its field, such as a choice the field does not have or text longer than its maximum length,
    /// or a field is a push button or signature field.</exception>
    /// <exception cref="InvalidOperationException">Flattening was asked for a digitally signed PDF.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static byte[] Fill(byte[] pdf, IDictionary<string, string> values, PdfFormFillOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pdf == null) throw new ArgumentNullException(nameof(pdf));
        if (values == null) throw new ArgumentNullException(nameof(values));
        return PdfFormEditor.Fill(pdf, values, options ?? new PdfFormFillOptions(), cancellationToken);
    }

    /// <summary>
    /// Reads a PDF from <paramref name="input"/> (from its current position), fills its fields and writes the result to
    /// <paramref name="output"/>. The streams are not disposed; nothing is written when the PDF cannot be filled.
    /// </summary>
    /// <inheritdoc cref="Fill(byte[], IDictionary{string, string}, PdfFormFillOptions?, CancellationToken)"/>
    public static void Fill(Stream input, Stream output, IDictionary<string, string> values, PdfFormFillOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if (!output.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(output));
        output.Write(Fill(PdfMerger.ReadAll(input), values, options, cancellationToken));
    }

    /// <summary>
    /// Fills the fields of the PDF file <paramref name="inputPath"/> and writes the result to <paramref name="outputPath"/>.
    /// The output file appears only once it is complete, and may be the input.
    /// </summary>
    /// <inheritdoc cref="Fill(byte[], IDictionary{string, string}, PdfFormFillOptions?, CancellationToken)"/>
    public static void FillFile(string inputPath, string outputPath, IDictionary<string, string> values, PdfFormFillOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
        var result = Fill(File.ReadAllBytes(inputPath), values, options, cancellationToken);
        AtomicFile.Write(outputPath, output => output.Write(result));
    }

    /// <summary>
    /// Draws every form field into its page with its current value and removes the fields; other annotations stay. Fields
    /// whose appearance a viewer was asked to draw (/NeedAppearances) get one first.
    /// </summary>
    /// <param name="pdf">The PDF.</param>
    /// <param name="password">The user or owner password, when the PDF is encrypted.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="InvalidOperationException">The PDF is digitally signed.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and the password does not open it.</exception>
    /// <exception cref="InvalidDataException">The PDF is not readable.</exception>
    public static byte[] Flatten(byte[] pdf, string? password = null, CancellationToken cancellationToken = default)
        => Fill(pdf, new Dictionary<string, string>(), new PdfFormFillOptions { Password = password, Flatten = true }, cancellationToken);
}

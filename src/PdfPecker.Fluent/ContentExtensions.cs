using PdfPecker.Fluent.Model;

namespace PdfPecker.Fluent;

/// <summary>Content for a slot: text, layouts, images, barcodes, lines and breaks.</summary>
public static partial class ContentExtensions
{
    /// <summary>A paragraph of text. Line breaks (<c>\n</c>) and spaces are kept; long lines wrap.</summary>
    public static TextBlockDescriptor Text(this IContainer container, string? text)
    {
        var element = Container.Of(container).Set(new TextElement());
        var run = new TextRun { Text = text ?? "" };
        element.Runs.Add(run);
        return new TextBlockDescriptor(element, run);
    }

    /// <summary>A paragraph built from spans with their own styles, links and page numbers.</summary>
    public static void Text(this IContainer container, Action<TextDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new TextElement());
        content(new TextDescriptor(element));
    }

    /// <summary>Items stacked from top to bottom, flowing over pages.</summary>
    public static void Column(this IContainer container, Action<ColumnDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new ColumnElement());
        content(new ColumnDescriptor(element));
    }

    /// <summary>Items side by side, stretched to the tallest.</summary>
    public static void Row(this IContainer container, Action<RowDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new RowElement());
        content(new RowDescriptor(element));
    }

    /// <summary>Content drawn on top of each other, such as a watermark under or a stamp over the main content.</summary>
    /// <exception cref="InvalidOperationException">The layers have no primary layer.</exception>
    public static void Layers(this IContainer container, Action<LayersDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new LayersElement());
        content(new LayersDescriptor(element));
        foreach (var (primary, _) in element.Layers)
            if (primary) return;
        throw new InvalidOperationException("Layers need a PrimaryLayer, whose content sets their size.");
    }

    /// <summary>
    /// Content with a part before and after it that repeat on every page the content spans, such as a section title
    /// with "(continued)" or a running subtotal line.
    /// </summary>
    public static void Decoration(this IContainer container, Action<DecorationDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new DecorationElement());
        content(new DecorationDescriptor(element));
    }

    /// <summary>A bulleted or numbered list, tagged as a list in PDF/UA.</summary>
    public static void List(this IContainer container, Action<ListDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new ListElement());
        content(new ListDescriptor(element));
    }

    /// <summary>Items laid out like words: side by side, wrapping onto new lines, such as tags or badges.</summary>
    public static void Inlined(this IContainer container, Action<InlinedDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new InlinedElement());
        content(new InlinedDescriptor(element));
    }

    /// <summary>Content that flows through several columns, like a newspaper.</summary>
    public static void MultiColumn(this IContainer container, Action<MultiColumnDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new MultiColumnElement());
        content(new MultiColumnDescriptor(element));
    }

    /// <summary>A grey box with an optional label, standing in for content not built yet.</summary>
    public static void Placeholder(this IContainer container, string? text = null)
        => container.MinHeight(24).Background(Colors.Grey.Lighten2).Padding(5).AlignCenter().AlignMiddle()
            .Text(text ?? "").FontSize(9).FontColor(Colors.Grey.Darken2);

    /// <summary>A table whose header and footer rows repeat on every page it spans.</summary>
    public static void Table(this IContainer container, Action<TableDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var element = Container.Of(container).Set(new TableElement());
        content(new TableDescriptor(element));
    }

    /// <summary>A PNG, JPEG, GIF, WebP, BMP, TIFF or SVG image, scaled to the slot's width unless told otherwise.</summary>
    public static ImageDescriptor Image(this IContainer container, byte[] imageData)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        return new ImageDescriptor(Container.Of(container).Set(new ImageElement(imageData)));
    }

    /// <summary>An image read from a stream.</summary>
    public static ImageDescriptor Image(this IContainer container, Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        using var copy = new MemoryStream();
        imageStream.CopyTo(copy);
        return Image(container, copy.ToArray());
    }

    /// <summary>An image read from a file now, by your code; the conversion itself never reads files.</summary>
    public static ImageDescriptor Image(this IContainer container, string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return Image(container, File.ReadAllBytes(filePath));
    }

    /// <summary>
    /// An SVG drawing, given as markup. It is drawn as an image, so it cannot change the document around it, and it
    /// loads nothing from outside except data URIs.
    /// </summary>
    public static ImageDescriptor Svg(this IContainer container, string svg)
    {
        ArgumentNullException.ThrowIfNull(svg);
        return Image(container, System.Text.Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>A QR code for the text, as a vector drawing scaled to fit the slot (<c>FitArea</c>).</summary>
    public static ImageDescriptor QrCode(this IContainer container, string text, BarcodeOptions? options = null)
        => Svg(container, Barcode.QrCode(text, options)).FitArea();

    /// <summary>An EPC payment QR code (GiroCode) that banking apps read to fill in a SEPA transfer.</summary>
    public static ImageDescriptor EpcQrCode(this IContainer container, EpcPayment payment, BarcodeOptions? options = null)
        => Svg(container, Barcode.EpcQrCode(payment, options)).FitArea();

    /// <summary>A Code 128 barcode for the text.</summary>
    public static ImageDescriptor Code128(this IContainer container, string text, BarcodeOptions? options = null)
        => Svg(container, Barcode.Code128(text, options)).FitArea();

    /// <summary>An EAN-13 barcode for 12 or 13 digits.</summary>
    public static ImageDescriptor Ean13(this IContainer container, string digits, BarcodeOptions? options = null)
        => Svg(container, Barcode.Ean13(digits, options)).FitArea();

    /// <summary>A horizontal line across the slot.</summary>
    public static LineDescriptor LineHorizontal(this IContainer container, float thickness, Unit unit = Unit.Point)
        => new(Container.Of(container).Set(new LineElement { Thickness = Length.Points(thickness, unit, nameof(thickness)) }));

    /// <summary>A vertical line down the slot, such as a <c>ConstantItem</c> in a row.</summary>
    public static LineDescriptor LineVertical(this IContainer container, float thickness, Unit unit = Unit.Point)
        => new(Container.Of(container).Set(new LineElement { Vertical = true, Thickness = Length.Points(thickness, unit, nameof(thickness)) }));

    /// <summary>Starts a new page here.</summary>
    public static void PageBreak(this IContainer container) => Container.Of(container).Set(new PageBreakElement());

    /// <summary>
    /// HTML written into the document as given, for what the fluent API does not cover. Only pass markup you wrote
    /// yourself: unlike every other call, it is not escaped.
    /// </summary>
    public static void Html(this IContainer container, string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Container.Of(container).Set(new HtmlElement(html));
    }
}

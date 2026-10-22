using System.Globalization;
using PdfPecker;
using PdfPecker.Fluent;

namespace FluentExamples;

/// <summary>
/// The example documents. The tests keep a golden copy of each one's HTML and PDF, the sample writes them out, and the
/// benchmarks time them, so the README's examples are always code that runs.
/// </summary>
public static class Examples
{
    /// <summary>A fixed date, so the output is byte-identical from run to run.</summary>
    public static readonly DateTimeOffset Date = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static IReadOnlyDictionary<string, Func<IDocument>> All { get; } = new Dictionary<string, Func<IDocument>>
    {
        ["invoice"] = () => Invoice(InvoiceModel.Sample(12)),
        ["report"] = () => Report(30),
        ["catalogue"] = Catalogue,
        ["layout"] = Layout,
        ["text"] = Text,
        ["extras"] = Extras,
    };

    public static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "assets", name);

    private static byte[]? s_logo, s_photo;
    public static byte[] Logo => s_logo ??= File.ReadAllBytes(AssetPath("logo.png"));
    public static byte[] Photo => s_photo ??= File.ReadAllBytes(AssetPath("photo.jpg"));

    // Invoice

    public sealed record InvoiceLine(string Description, int Quantity, decimal UnitPrice)
    {
        public decimal Amount => Quantity * UnitPrice;
    }

    public sealed record InvoiceModel(string Number, DateTime Date, DateTime DueDate, string Seller, string Customer, string CustomerAddress, IReadOnlyList<InvoiceLine> Lines)
    {
        public decimal Total => Lines.Sum(l => l.Amount);

        public static InvoiceModel Sample(int lines) => new(
            "2026-104", new DateTime(2026, 10, 21), new DateTime(2026, 11, 20),
            "MyApp Ltd", "Example Customer A/S", "Harbour Street 12\n8000 Aarhus C\nDenmark",
            [.. Enumerable.Range(1, lines).Select(i => new InvoiceLine($"Consulting, work package {i}", i % 4 + 1, 95m + i * 12.5m))]);
    }

    public static IDocument Invoice(InvoiceModel invoice) => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(18, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Colors.Grey.Darken4));

            page.Header().PaddingBottom(14).Row(row =>
            {
                row.RelativeItem().Column(column =>
                {
                    column.Item().Heading(1).Text("Invoice").FontSize(22).Bold().FontColor(Colors.Blue.Darken3);
                    column.Item().Text($"No. {invoice.Number} · {invoice.Date.ToString("yyyy-MM-dd", Culture)}").FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(32, Unit.Millimetre).Image(Logo);
            });

            page.Content().Column(column =>
            {
                column.Spacing(14);
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text("Bill to").FontSize(8).FontColor(Colors.Grey.Darken1);
                        c.Item().Text(invoice.Customer).Bold();
                        c.Item().Text(invoice.CustomerAddress);
                    });
                    row.RelativeItem().AlignRight().Column(c =>
                    {
                        c.Item().Text("From").FontSize(8).FontColor(Colors.Grey.Darken1);
                        c.Item().Text(invoice.Seller).Bold();
                        c.Item().Text("VAT DK12345678");
                    });
                });

                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(5);
                        c.ConstantColumn(40);
                        c.ConstantColumn(70);
                        c.ConstantColumn(80);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Description");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Price");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                    });

                    foreach (var line in invoice.Lines)
                    {
                        table.Cell().Element(BodyCell).Text(line.Description);
                        table.Cell().Element(BodyCell).AlignRight().Text(line.Quantity.ToString(Culture));
                        table.Cell().Element(BodyCell).AlignRight().Text(line.UnitPrice.ToString("N2", Culture));
                        table.Cell().Element(BodyCell).AlignRight().Text(line.Amount.ToString("N2", Culture));
                    }

                    table.Footer(footer =>
                    {
                        footer.Cell().ColumnSpan(3).PaddingTop(6).AlignRight().Text("Total (EUR)").Bold();
                        footer.Cell().PaddingTop(6).AlignRight().Text(invoice.Total.ToString("N2", Culture)).Bold();
                    });
                });

                column.Item().PreventPageBreak().Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span("Please pay by ");
                        text.Span(invoice.DueDate.ToString("yyyy-MM-dd", Culture)).Bold();
                        text.Line(" to");
                        text.Span("DK50 0040 0440 1162 43").FontFamily("monospace");
                    });
                    row.ConstantItem(64).EpcQrCode(new EpcPayment
                    {
                        Name = invoice.Seller,
                        Iban = "DK5000400440116243",
                        Amount = invoice.Total,
                        Text = "Invoice " + invoice.Number,
                    });
                });
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = $"Invoice {invoice.Number}", Author = invoice.Seller, Language = "en", CreationDate = Date });

    private static IContainer HeaderCell(IContainer container)
        => container.BorderBottom(1).BorderColor(Colors.Grey.Darken2).PaddingVertical(4).DefaultTextStyle(x => x.SemiBold());

    private static IContainer BodyCell(IContainer container)
        => container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);

    // Report

    public static IDocument Report(int chapters) => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(10).LineHeight(1.4f));

            page.Header().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingBottom(4).Row(row =>
            {
                row.RelativeItem().Text("MyApp Ltd · Quarterly report").FontSize(8).FontColor(Colors.Grey.Darken1);
                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });

            page.Content().PaddingTop(10).Column(column =>
            {
                column.Spacing(8);
                column.Item().Heading(1).Text("Quarterly report, Q3 2026").FontSize(24).Bold();
                column.Item().Text("Contents").FontSize(12).SemiBold();
                for (int i = 1; i <= Math.Min(chapters, 8); i++)
                    column.Item().Text(text => text.SectionLink($"{i}. {ChapterTitle(i)}", $"chapter-{i}").FontColor(Colors.Blue.Darken2));

                for (int i = 1; i <= chapters; i++)
                {
                    if (i % 3 == 1) column.Item().PageBreak();
                    column.Item().Section($"chapter-{i}").Heading(2).PaddingTop(6).Text($"{i}. {ChapterTitle(i)}").FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                    column.Item().Text(Paragraph(i)).Justify();
                    column.Item().Row(row =>
                    {
                        row.Spacing(10);
                        foreach (var (label, value) in Figures(i))
                        {
                            row.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).CornerRadius(4).Padding(8).Column(c =>
                            {
                                c.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                                c.Item().Text(value).FontSize(16).SemiBold();
                            });
                        }
                    });
                    column.Item().Text(Paragraph(i + 7)).Justify();
                }
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = "Quarterly report", Language = "en", CreationDate = Date });

    private static string ChapterTitle(int i) => (i % 5) switch
    {
        0 => "Outlook",
        1 => "Revenue and orders",
        2 => "Costs",
        3 => "Customers",
        _ => "People and hiring",
    };

    private static (string, string)[] Figures(int i) =>
    [
        ("Revenue", (1200 + i * 37).ToString("N0", Culture) + " k"),
        ("Margin", (18 + i % 7).ToString(Culture) + " %"),
        ("Orders", (340 + i * 11).ToString("N0", Culture)),
    ];

    private static string Paragraph(int seed)
    {
        string[] sentences =
        [
            "Revenue grew in every region this quarter, led by renewals from existing customers.",
            "The new pricing model was introduced in August and now covers two thirds of all contracts.",
            "Delivery times fell by a day on average after the warehouse moved to two shifts.",
            "We expect the fourth quarter to follow the usual seasonal pattern, with December as the strongest month.",
            "Support tickets per customer dropped for the third quarter in a row.",
            "Hiring stayed below plan because two senior positions are still open.",
            "Energy costs were stable, and travel spending was lower than budgeted.",
        ];
        return string.Join(" ", Enumerable.Range(0, 6).Select(k => sentences[(seed + k * 3) % sentences.Length]));
    }

    // Catalogue: images, barcodes, spans, alignment

    public static IDocument Catalogue() => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape);
            page.Margin(14, Unit.Millimetre);
            page.PageColor(Colors.Grey.Lighten5);

            page.Header().Row(row =>
            {
                row.ConstantItem(36).Image(Logo).AlternativeText("PdfPecker logo");
                row.RelativeItem().PaddingLeft(10).AlignMiddle().Text("Product catalogue").FontSize(20).Bold();
                row.AutoItem().AlignMiddle().Text("Autumn 2026").FontColor(Colors.Grey.Darken1);
            });

            page.Content().PaddingTop(12).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                });

                for (int i = 1; i <= 6; i++)
                {
                    // Background and border first, so they fill the whole cell; table cells stretch to the tallest in the row.
                    table.Cell().Background(Colors.White).Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(c =>
                    {
                        c.Spacing(4);
                        c.Item().Height(70).Image(Photo).FitArea();
                        c.Item().Text($"Product {i}").Bold();
                        c.Item().Text(text =>
                        {
                            text.Span("EUR ").FontSize(8);
                            text.Span((19.95m * i).ToString("N2", Culture)).FontSize(13).SemiBold().FontColor(Colors.Green.Darken3);
                        });
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Height(28).Ean13($"5901234{i:D5}");
                            r.ConstantItem(8);
                            r.ConstantItem(28).QrCode($"https://example.com/product/{i}");
                        });
                    });
                }

                table.Cell().ColumnSpan(2).Background(Colors.Blue.Darken3).Padding(12).AlignMiddle().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontColor(Colors.White));
                    text.Line("Spanning two columns").FontSize(14).Bold();
                    text.Hyperlink("pdfpecker.net", "https://pdfpecker.net").Underline();
                });
                table.Cell().Padding(6).AlignCenter().AlignMiddle().Width(140).Height(50).Code128("PECK-2026");
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = "Product catalogue", Language = "en", CreationDate = Date });

    // Layout: decorator order, alignment, borders, lines

    public static IDocument Layout() => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(12, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8));

            page.Content().Column(column =>
            {
                column.Spacing(8);
                column.Item().Text("Padding, then background: the colour sits inside the padding").Bold();
                column.Item().Border(0.5f).Padding(8).Background(Colors.Amber.Lighten3).Text("inside");
                column.Item().Text("Background, then padding: the colour fills the padding too").Bold();
                column.Item().Border(0.5f).Background(Colors.Amber.Lighten3).Padding(8).Text("inside");

                column.Item().Text("Nine alignments in a fixed height").Bold();
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                    foreach (var vertical in new[] { "top", "middle", "bottom" })
                    {
                        foreach (var horizontal in new[] { "left", "center", "right" })
                        {
                            var cell = table.Cell().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Height(36).Padding(3);
                            cell = vertical == "top" ? cell.AlignTop() : vertical == "middle" ? cell.AlignMiddle() : cell.AlignBottom();
                            cell = horizontal == "left" ? cell.AlignLeft() : horizontal == "center" ? cell.AlignCenter() : cell.AlignRight();
                            cell.Background(Colors.LightBlue.Lighten4).Padding(2).Text($"{vertical} {horizontal}");
                        }
                    }
                });

                column.Item().Text("Row items: relative 1 and 2, constant 60 pt, auto, with a vertical line").Bold();
                column.Item().Height(30).Row(row =>
                {
                    row.Spacing(4);
                    row.RelativeItem().Background(Colors.Green.Lighten4).AlignMiddle().AlignCenter().Text("1");
                    row.RelativeItem(2).Background(Colors.Green.Lighten3).AlignMiddle().AlignCenter().Text("2");
                    row.ConstantItem(1).LineVertical(1).LineColor(Colors.Grey.Darken1);
                    row.ConstantItem(60).Background(Colors.Green.Lighten2).AlignMiddle().AlignCenter().Text("60 pt");
                    row.AutoItem().Background(Colors.Green.Lighten1).Padding(4).Text("auto width");
                });

                column.Item().Text("Borders per side and rounded corners").Bold();
                column.Item().Row(row =>
                {
                    row.Spacing(6);
                    row.RelativeItem().BorderLeft(3).BorderColor(Colors.Red.Medium).Padding(6).Text("left");
                    row.RelativeItem().BorderBottom(3).BorderColor(Colors.Blue.Medium).Padding(6).Text("bottom");
                    row.RelativeItem().Border(1).BorderColor(Colors.Purple.Medium).CornerRadius(8).Background(Colors.Purple.Lighten5).Padding(6).Text("rounded");
                    row.RelativeItem().Width(40).Height(24).Background(Colors.Orange.Lighten2);
                });

                column.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);

                column.Item().Text("Right to left").Bold();
                column.Item().ContentFromRightToLeft().Row(row =>
                {
                    row.ConstantItem(40).Background(Colors.Teal.Lighten3).Padding(4).Text("first");
                    row.RelativeItem().Background(Colors.Teal.Lighten5).Padding(4).Text("second, laid out from the right");
                });

                column.Item().ShowIf(false).Text("This text is hidden.");
                column.Item().Hyperlink("https://pdfpecker.net").Background(Colors.Blue.Lighten5).Padding(6).Text("The whole box is a link");
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = "Layout", CreationDate = Date });

    // Text

    public static IDocument Text() => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(12, Unit.Millimetre);

            page.Content().Column(column =>
            {
                column.Spacing(6);
                column.Item().Text(text =>
                {
                    text.Span("Thin ").Thin();
                    text.Span("light ").Light();
                    text.Span("normal ");
                    text.Span("semibold ").SemiBold();
                    text.Span("bold ").Bold();
                    text.Span("black").Black();
                });
                column.Item().Text(text =>
                {
                    text.Span("italic ").Italic();
                    text.Span("underline ").Underline();
                    text.Span("strikethrough ").Strikethrough();
                    text.Span("highlighted").BackgroundColor(Colors.Yellow.Lighten2);
                });
                column.Item().Text(text =>
                {
                    text.Span("E = mc");
                    text.Span("2").Superscript();
                    text.Span(" and H");
                    text.Span("2").Subscript();
                    text.Span("O, letter spacing ");
                    text.Span("wide").LetterSpacing(0.3f);
                });
                column.Item().Text("Serif, monospace and sizes").FontSize(14).SemiBold();
                column.Item().Text("A serif paragraph in Times.").FontFamily("serif");
                column.Item().Text("A monospace line: 0123456789").FontFamily("monospace");
                column.Item().Text("Centred text").AlignCenter();
                column.Item().Text("Right-aligned text").AlignRight();
                column.Item().Text("Line breaks\nare kept,   and so are   spaces.");
                column.Item().Text("Escaped, not markup: <b>bold?</b> & {page} & \"quotes\"").FontColor(Colors.Red.Darken2);
                column.Item().Text("Justified text spreads its lines to both edges. " + string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 5))).Justify();
                column.Item().Text(text =>
                {
                    text.Span("Links: ");
                    text.Hyperlink("pdfpecker.net", "https://pdfpecker.net").FontColor(Colors.Blue.Darken2).Underline();
                    text.Span(" and ");
                    text.Hyperlink("mail", "mailto:hello@example.com").FontColor(Colors.Blue.Darken2).Underline();
                });
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = "Text", CreationDate = Date });

    // Extras: the calls that came after the first version, from transforms to multi-column text

    public static IDocument Extras() => Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(12, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(8));

            page.Content().Column(column =>
            {
                column.Spacing(8);

                column.Item().Text("Transforms: rotate, scale, flip and translate draw differently without moving anything else").Bold();
                column.Item().Height(44).Row(row =>
                {
                    row.Spacing(10);
                    row.ConstantItem(60).PaddingLeft(8).Rotate(12).Background(Colors.Blue.Lighten4).Padding(3).Text("Rotate(12)");
                    row.ConstantItem(60).Scale(1.4f).Background(Colors.Blue.Lighten4).Padding(3).Text("Scale(1.4)");
                    row.ConstantItem(60).FlipHorizontal().Background(Colors.Blue.Lighten4).Padding(3).Text("Flipped");
                    row.ConstantItem(60).TranslateY(14).Background(Colors.Blue.Lighten4).Padding(3).Text("TranslateY(14)");
                    row.ConstantItem(18).RotateRight().Background(Colors.Orange.Lighten4).Padding(2).Text("RotateRight");
                    row.ConstantItem(18).RotateLeft().Background(Colors.Orange.Lighten4).Padding(2).Text("RotateLeft");
                });

                column.Item().Text("Layers: a watermark under the content and a stamp over it").Bold();
                column.Item().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Layers(layers =>
                {
                    layers.Layer().AlignCenter().AlignMiddle().Text("DRAFT").FontSize(30).Bold().FontColor(Colors.Grey.Lighten3);
                    layers.PrimaryLayer().Padding(8).Text("The primary layer sets the size. The layer before it is drawn underneath, "
                        + "the one after it on top, and both cover the same area.");
                    layers.Layer().AlignRight().AlignBottom().Padding(4).Rotate(-8).Border(1).BorderColor(Colors.Red.Medium)
                        .PaddingHorizontal(4).Text("APPROVED").FontColor(Colors.Red.Medium).Bold();
                });

                column.Item().Text("Inlined: items that wrap like words").Bold();
                column.Item().Inlined(inlined =>
                {
                    inlined.Spacing(4);
                    inlined.BaselineMiddle();
                    foreach (var tag in new[] { "invoice", "report", "catalogue", "net8.0", "net9.0", "net10.0", "trimmable", "native AOT", "MIT" })
                        inlined.Item().Background(Colors.Teal.Lighten4).CornerRadius(6).PaddingHorizontal(6).PaddingVertical(2).Text(tag);
                    inlined.Item().Placeholder("Placeholder");
                });

                column.Item().Text("MultiColumn: text flowing through three columns").Bold();
                column.Item().MultiColumn(multi =>
                {
                    multi.Columns(3);
                    multi.Spacing(12);
                    multi.Content().Text("PdfPecker.Fluent writes HTML and CSS and lets PdfPecker lay it out, so multi-column text, "
                        + "transforms and layers are the CSS features PdfPecker already renders. Nothing here needs a browser, a native "
                        + "library or a second package. The columns share the text equally, and the gap between them is twelve points.")
                        .Justify();
                });

                column.Item().Text("Text: clamped lines, first-line indent, decoration styles and word spacing").Bold();
                column.Item().Width(220).Text("ClampLines(2) keeps a long product description to two lines and ends it with an ellipsis "
                    + "when it is cut, which keeps cards in a grid the same height.").ClampLines(2);
                column.Item().Text("A paragraph whose first line is indented by sixteen points, as in a book, while the lines after it "
                    + "start at the margin again.").ParagraphFirstLineIndentation(16);
                column.Item().Text(text =>
                {
                    text.Span("wavy").Underline().DecorationWavy().DecorationColor(Colors.Red.Medium);
                    text.Span("   ");
                    text.Span("dashed").Underline().DecorationDashed();
                    text.Span("   ");
                    text.Span("dotted").Underline().DecorationDotted();
                    text.Span("   ");
                    text.Span("double").Strikethrough().DecorationDouble();
                    text.Span("   ");
                    text.Span("thick").Underline().DecorationThickness(3).DecorationColor(Colors.Blue.Medium);
                    text.Span("   ");
                    text.Span("word spacing").WordSpacing(1);
                });

                column.Item().Text("Shadow, single rounded corners, dashed lines and an aspect ratio").Bold();
                column.Item().Row(row =>
                {
                    row.Spacing(12);
                    row.RelativeItem().Shadow(new BoxShadowStyle { OffsetX = 2, OffsetY = 2, Blur = 4, Color = "#00000055" })
                        .Background(Colors.White).Padding(6).Text("Shadow");
                    row.RelativeItem().CornerRadiusTopLeft(10).CornerRadiusBottomRight(10).Background(Colors.Purple.Lighten4).Padding(6).Text("two corners");
                    row.RelativeItem().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Darken1).LineDashPattern([4, 2]);
                    row.ConstantItem(48).AspectRatio(16 / 9f).Background(Colors.Green.Lighten3).AlignCenter().AlignMiddle().Text("16:9");
                });

                column.Item().Text("ZIndex, Unconstrained and DebugArea").Bold();
                column.Item().Row(row =>
                {
                    row.Spacing(12);
                    row.RelativeItem().DebugArea("DebugArea").Padding(8).Text("An outlined, labelled slot while building a layout.");
                    row.RelativeItem().Height(30).Column(c =>
                    {
                        c.Item().Unconstrained().TranslateX(24).TranslateY(-4).ZIndex(1).Background(Colors.Yellow.Lighten2).Padding(3)
                            .Text("Unconstrained badge on top");
                        c.Item().Background(Colors.Grey.Lighten3).Padding(6).Text("Content under the badge");
                    });
                });

                column.Item().EnsureSpace(160).Text("EnsureSpace(160): this heading starts a new page, because less than 160 pt is left on this one.").Bold();
                column.Item().Text("The paragraph after it follows it onto the new page.");
            });
        });
    }).WithMetadata(new DocumentMetadata { Title = "Extras", Language = "en", CreationDate = Date });
}

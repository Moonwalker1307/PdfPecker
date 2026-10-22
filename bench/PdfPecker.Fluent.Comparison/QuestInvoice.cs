using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using static FluentExamples.Examples;

namespace Comparison;

// The invoice from examples/Examples.cs written for QuestPDF, call for call where QuestPDF has the same call. The EPC
// QR code is PdfPecker's SVG, because QuestPDF has no barcode generator of its own.
internal static class QuestInvoice
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static IDocument Create(InvoiceModel invoice)
    {
        string qr = PdfPecker.Barcode.EpcQrCode(new PdfPecker.EpcPayment
        {
            Name = invoice.Seller,
            Iban = "DK5000400440116243",
            Amount = invoice.Total,
            Text = "Invoice " + invoice.Number,
        });

        return Document.Create(container =>
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
                        column.Item().Text("Invoice").FontSize(22).Bold().FontColor(Colors.Blue.Darken3);
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

                    column.Item().ShowEntire().Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
                    {
                        row.RelativeItem().Text(text =>
                        {
                            text.Span("Please pay by ");
                            text.Span(invoice.DueDate.ToString("yyyy-MM-dd", Culture)).Bold();
                            text.Line(" to");
                            text.Span("DK50 0040 0440 1162 43"); // Only Lato ships with QuestPDF, so no monospace font.
                        });
                        row.ConstantItem(64).Svg(qr);
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
        }).WithMetadata(new DocumentMetadata { Title = $"Invoice {invoice.Number}", Author = invoice.Seller, CreationDate = Date });
    }

    private static IContainer HeaderCell(IContainer container)
        => container.BorderBottom(1).BorderColor(Colors.Grey.Darken2).PaddingVertical(4).DefaultTextStyle(x => x.SemiBold());

    private static IContainer BodyCell(IContainer container)
        => container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4);
}

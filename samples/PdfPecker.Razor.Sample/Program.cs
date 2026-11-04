using PdfPecker.Razor;
using PdfPecker.Razor.Sample;
using PdfPecker.Razor.Sample.Components;

// Writes the invoice template as PDF and as the HTML it renders to: dotnet run -- <output folder>
string output = args.Length > 0 ? args[0] : "output";
Directory.CreateDirectory(output);

var invoice = new InvoiceModel("2026-104", new DateOnly(2026, 11, 4), "Northwind Traders", "12 Harbour Street, Springfield",
[
    new LineItem("Consulting, October", 12, 95m),
    new LineItem("Template design <draft & final>", 1, 640m),
    new LineItem("Hosting, 12 months", 12, 18.5m),
]);
var parameters = new Dictionary<string, object?> { [nameof(Invoice.Data)] = invoice };

File.WriteAllText(Path.Combine(output, "invoice.html"), await RazorToPdf.RenderHtmlAsync<Invoice>(parameters));
File.WriteAllBytes(Path.Combine(output, "invoice.pdf"), await RazorToPdf.ConvertAsync<Invoice>(parameters));
Console.WriteLine($"Wrote invoice.pdf and invoice.html to {Path.GetFullPath(output)}");

using PdfPecker.Fluent;
using FluentExamples;

// Writes every example as a PDF and as the HTML it is made from. Usage: PdfPecker.Fluent.Sample [output directory]
string output = args.Length > 0 ? args[0] : "output";
Directory.CreateDirectory(output);
foreach (var (name, create) in Examples.All)
{
    var document = create();
    File.WriteAllBytes(Path.Combine(output, name + ".pdf"), document.GeneratePdf());
    File.WriteAllText(Path.Combine(output, name + ".html"), document.GenerateHtml());
    Console.WriteLine($"{name}.pdf");
}

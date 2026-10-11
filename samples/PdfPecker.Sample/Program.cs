using System.Diagnostics;
using PdfPecker;

// Converts every .html and .md file in the html folder next to the executable (or the folder given as the first argument)
// into a PDF in the output folder (second argument, default "output").
var inputDir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "html");
var outputDir = args.Length > 1 ? args[1] : "output";
Directory.CreateDirectory(outputDir);

var options = new PdfOptions { FooterText = "Page {page} of {pages}" };
foreach (var file in Directory.GetFiles(inputDir, "*.html").Concat(Directory.GetFiles(inputDir, "*.md")))
{
    var target = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".pdf");
    var sw = Stopwatch.StartNew();
    if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) MarkdownToPdf.ConvertFile(file, target, options);
    else HtmlToPdf.ConvertFile(file, target, options);
    sw.Stop();
    Console.WriteLine($"{Path.GetFileName(file),-20} -> {target} ({new FileInfo(target).Length / 1024.0:F1} KB, {sw.Elapsed.TotalMilliseconds:F1} ms)");
}

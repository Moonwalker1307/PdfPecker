using System.Globalization;
using System.Text;

namespace PdfPecker.Cli;

/// <summary>Commands that read a PDF and write something else: to-image, to-html, text and info.</summary>
internal static class ExportCommands
{
    public static int ToImage(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "to-image <input.pdf> [output-directory] [--format png|jpeg|webp|tiff] [--dpi 150] [--pages 1-3]";
        var a = Common.WithPassword(new OptionSet())
            .Value("format").Value("dpi").Value("quality").Value("width").Value("height").Value("pages").Flag("transparent").Value("prefix")
            .Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var inputArg = positionals[0];
        var dir = a.Get("output") ?? (positionals.Count > 1 ? positionals[1] : (inputArg == "-" ? "." : Path.GetDirectoryName(Path.GetFullPath(inputArg)) ?? "."));

        var format = (a.Get("format") ?? "png").ToLowerInvariant() switch
        {
            "png" => PdfImageFormat.Png,
            "jpg" or "jpeg" => PdfImageFormat.Jpeg,
            "webp" => PdfImageFormat.WebP,
            "tif" or "tiff" => PdfImageFormat.Tiff,
            var other => throw new UsageException($"--format must be png, jpeg, webp or tiff, not '{other}'."),
        };
        var password = Common.Password(a);
        var options = new ImageOptions { Format = format, Password = password, Transparent = a.Has("transparent") };
        if (a.GetFloat("dpi") is { } dpi) options.Dpi = dpi;
        if (a.GetInt("quality") is { } quality) options.Quality = quality;
        if (a.GetInt("width") is { } w) options.Width = w;
        if (a.GetInt("height") is { } h) options.Height = h;

        var pdf = Common.ReadPdf(io, inputArg);
        var prefix = a.Get("prefix") ?? (inputArg == "-" ? "page" : Path.GetFileNameWithoutExtension(inputArg));
        var extension = RenderedImage.GetFileExtension(format);

        if (format == PdfImageFormat.Tiff)
        {
            if (a.Get("pages") is { } tiffPages)
            {
                // The TIFF holds every page it is given, so the chosen pages are taken out first; the copy is not
                // encrypted.
                pdf = PdfPages.Extract(pdf, PdfPageRange.Parse(tiffPages), new PdfPagesOptions { Password = password }, ct);
                options.Password = null;
            }

            var tiff = PdfToImage.ConvertToTiff(pdf, options, ct);
            var tiffName = Path.Combine(dir, prefix + extension);
            io.WriteOutput(tiffName, tiff);
            Common.Report(io, tiffName, tiff);
            return 0;
        }

        int count = PdfPages.Count(pdf, password);
        var pages = a.Get("pages") is { } range ? PdfPageRange.Parse(range).ToPageNumbers(count) : Enumerable.Range(1, count).ToList();
        int width = Math.Max(3, count.ToString(CultureInfo.InvariantCulture).Length);
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var image = PdfToImage.ConvertPage(pdf, page - 1, options, ct);
            var name = Path.Combine(dir, $"{prefix}-{page.ToString("D" + width, CultureInfo.InvariantCulture)}{extension}");
            io.WriteOutput(name, image);
            Common.Report(io, name, image);
        }

        return 0;
    }

    public static int ToHtml(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "to-html <input.pdf> [output.html] [--mode visual|structured] [--pages 1-3]";
        var a = Common.WithPassword(new OptionSet()).Value("mode").Value("pages").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = a.Get("output") ?? (positionals.Count > 1 ? positionals[1] : (positionals[0] == "-" ? "-" : Path.ChangeExtension(positionals[0], ".html")));

        var options = new PdfHtmlOptions { Password = Common.Password(a) };
        if (a.Get("mode") is { } mode)
            options.Mode = mode.ToLowerInvariant() switch
            {
                "visual" => PdfHtmlMode.Visual,
                "structured" => PdfHtmlMode.Structured,
                _ => throw new UsageException($"--mode must be visual or structured, not '{mode}'."),
            };
        if (a.Get("pages") is { } pages) options.Pages = PdfPageRange.Parse(pages);

        var html = PdfToHtml.Convert(Common.ReadPdf(io, positionals[0]), options, ct);
        io.WriteOutput(output, html);
        if (output != "-") io.Info($"Wrote {output} ({Common.FormatSize(Encoding.UTF8.GetByteCount(html))})");
        return 0;
    }

    public static int Text(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "text <input.pdf> [output.txt] [--pages 1-3]";
        var a = Common.WithPassword(new OptionSet()).Value("pages").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = a.Get("output") ?? (positionals.Count > 1 ? positionals[1] : "-");

        var options = new PdfTextOptions { Password = Common.Password(a) };
        if (a.Get("pages") is { } pages) options.Pages = PdfPageRange.Parse(pages);

        var text = PdfText.Extract(Common.ReadPdf(io, positionals[0]), options, ct);
        // A terminal and a pipe both expect the last line to end.
        if (output == "-" && text.Length > 0 && !text.EndsWith('\n')) text += "\n";
        io.WriteOutput(output, text);
        if (output != "-") io.Info($"Wrote {output}");
        return 0;
    }

    public static int Info(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "info <input.pdf>";
        var a = Common.WithPassword(new OptionSet()).Parse(args);
        var input = Common.Positionals(a, 1, 1, usage)[0];
        var pdf = Common.ReadPdf(io, input);

        bool encrypted = PdfProtector.IsEncrypted(pdf);
        var password = Common.Password(a);
        var sb = new StringBuilder();
        sb.Append("size:      ").Append(Common.FormatSize(pdf.Length)).Append('\n');
        sb.Append("encrypted: ").Append(encrypted ? "yes" : "no").Append('\n');
        if (!encrypted || password != null)
        {
            sb.Append("pages:     ").Append(Common.Number(PdfPages.Count(pdf, password))).Append('\n');
            sb.Append("signatures: ").Append(Common.Number(PdfSignatureVerifier.Verify(pdf, null, password, ct).Count)).Append('\n');
            sb.Append("active content: ").Append(PdfSanitizer.HasActiveContent(pdf, null, password) ? "yes" : "no").Append('\n');
        }

        io.Output.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        io.Output.Flush();
        return 0;
    }
}

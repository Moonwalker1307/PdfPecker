namespace PdfPecker.Cli;

/// <summary>Commands that rewrite a whole PDF: compact, linearize and sanitize.</summary>
internal static class FileCommands
{
    public static int Compact(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "compact <input.pdf> <output.pdf> [--image-quality 1-100] [--max-dpi N] [--remove-metadata]";
        var a = new OptionSet().Value("image-quality").Value("max-dpi").Flag("remove-metadata").Flag("quiet|q").Value("output|o").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);

        var pdf = Common.ReadPdf(io, positionals[0]);
        var options = new PdfCompactOptions
        {
            ImageQuality = a.GetInt("image-quality"),
            MaxImageDpi = a.GetInt("max-dpi"),
            RemoveMetadata = a.Has("remove-metadata"),
        };
        var result = PdfCompactor.Compact(pdf, options, ct);
        io.WriteOutput(output, result);
        if (output != "-")
            io.Info($"Wrote {output} ({Common.FormatSize(result.Length)}, was {Common.FormatSize(pdf.Length)})");
        return 0;
    }

    public static int Linearize(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "linearize <input.pdf> <output.pdf>";
        var a = new OptionSet().Flag("quiet|q").Value("output|o").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        var result = PdfLinearizer.Linearize(Common.ReadPdf(io, positionals[0]), ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result);
        return 0;
    }

    public static int Sanitize(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "sanitize <input.pdf> <output.pdf>  |  sanitize <input.pdf> --check";
        var a = Common.WithPassword(new OptionSet()).Flag("check").Parse(args);
        io.Quiet = a.Has("quiet");

        if (a.Has("check"))
        {
            var only = Common.Positionals(a, 1, 1, usage)[0];
            bool active = PdfSanitizer.HasActiveContent(Common.ReadPdf(io, only), null, Common.Password(a));
            io.Error.WriteLine(active ? "The PDF has active content (scripts or actions)." : "The PDF has no active content.");
            return active ? 1 : 0;
        }

        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        var result = PdfSanitizer.RemoveActiveContent(Common.ReadPdf(io, positionals[0]), null, ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result);
        return 0;
    }
}

namespace PdfPecker.Cli;

/// <summary>Commands that choose, join, turn or overlay pages: merge, split, extract, remove, rotate and stamp.</summary>
internal static class PageCommands
{
    public static int Merge(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "merge <a.pdf> <b.pdf>[:pages]... -o <output.pdf>";
        var a = Common.WithPassword(new OptionSet())
            .Value("title").Value("author").Value("subject").Value("keywords").Flag("no-bookmarks").Parse(args);
        io.Quiet = a.Has("quiet");
        var output = a.Get("output") ?? throw new UsageException($"Usage: pdfpecker {usage}");
        if (a.Positionals.Count == 0) throw new UsageException($"Usage: pdfpecker {usage}");

        var password = Common.Password(a);
        var inputs = new List<PdfMergeInput>();
        foreach (var arg in a.Positionals)
        {
            ct.ThrowIfCancellationRequested();
            var (path, pages) = SplitPages(arg);
            if (output != "-" && path != "-" && string.Equals(Path.GetFullPath(path), Path.GetFullPath(output), StringComparison.Ordinal))
                throw new UsageException($"The output '{output}' is also an input; choose another output file.");
            var pdf = Common.ReadPdf(io, path);
            inputs.Add(pages == null ? new PdfMergeInput(pdf) : new PdfMergeInput(pdf, pages));
        }

        var options = new PdfMergeOptions
        {
            Title = a.Get("title"),
            Author = a.Get("author"),
            Subject = a.Get("subject"),
            Keywords = a.Get("keywords"),
            Bookmarks = !a.Has("no-bookmarks"),
        };
        if (password != null) options.Passwords = Enumerable.Repeat<string?>(password, inputs.Count).ToList();

        var merged = PdfMerger.Merge(inputs, options, ct);
        io.WriteOutput(output, merged);
        Common.Report(io, output, merged);
        return 0;
    }

    /// <summary>
    /// "file.pdf:1-3,5" or "file.pdf:2-last" names pages of a file, in any form <see cref="PdfPageRange"/> reads; a colon
    /// that is part of a path (C:\x.pdf) is left alone.
    /// </summary>
    internal static (string Path, string? Pages) SplitPages(string arg)
    {
        int colon = arg.LastIndexOf(':');
        if (colon > 1 && colon < arg.Length - 1)
        {
            var pages = arg.Substring(colon + 1);
            if (PdfPageRange.TryParse(pages, out _)) return (arg.Substring(0, colon), pages);
        }

        return (arg, null);
    }

    public static int Split(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "split <input.pdf> [--every N | --range 1-3 --range 4-6] [--out-dir DIR] [--prefix NAME]";
        var a = Common.WithPassword(new OptionSet()).Value("every").Value("range").Value("out-dir").Value("prefix").Parse(args);
        io.Quiet = a.Has("quiet");
        var inputArg = Common.Positionals(a, 1, 1, usage)[0];
        var pdf = Common.ReadPdf(io, inputArg);
        var options = new PdfPagesOptions { Password = Common.Password(a) };

        byte[][] parts;
        var ranges = a.GetAll("range").ToList();
        if (ranges.Count > 0)
        {
            if (a.Has("every")) throw new UsageException("Use --every or --range, not both.");
            parts = PdfPages.Split(pdf, ranges.Select(PdfPageRange.Parse), options, ct);
        }
        else
        {
            int every = a.GetInt("every") ?? 1;
            if (every < 1) throw new UsageException("--every must be 1 or more.");
            parts = PdfPages.Split(pdf, every, options, ct);
        }

        var dir = a.Get("out-dir") ?? (inputArg == "-" ? "." : Path.GetDirectoryName(Path.GetFullPath(inputArg)) ?? ".");
        var prefix = a.Get("prefix") ?? (inputArg == "-" ? "part" : Path.GetFileNameWithoutExtension(inputArg));
        int width = Math.Max(3, parts.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        for (int i = 0; i < parts.Length; i++)
        {
            var name = Path.Combine(dir, $"{prefix}-{(i + 1).ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture)}.pdf");
            io.WriteOutput(name, parts[i]);
            Common.Report(io, name, parts[i]);
        }

        return 0;
    }

    public static int ExtractOrRemove(CliIo io, IReadOnlyList<string> args, bool remove, CancellationToken ct)
    {
        var verb = remove ? "remove" : "extract";
        var usage = $"{verb} <input.pdf> <output.pdf> --pages 1-3,5";
        var a = Common.WithPassword(new OptionSet()).Value("pages").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        var pages = a.Get("pages") ?? throw new UsageException($"Usage: pdfpecker {usage}");

        var pdf = Common.ReadPdf(io, positionals[0]);
        var options = new PdfPagesOptions { Password = Common.Password(a) };
        var result = remove ? PdfPages.Remove(pdf, pages, options, ct) : PdfPages.Extract(pdf, pages, options, ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result);
        return 0;
    }

    public static int Rotate(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "rotate <input.pdf> <output.pdf> --degrees 90 [--pages 1-3]";
        var a = Common.WithPassword(new OptionSet()).Value("degrees").Value("pages").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 1, 2, usage);
        var output = Common.Output(a, positionals, 1, usage, positionals[0]);
        int degrees = a.GetInt("degrees") ?? throw new UsageException($"Usage: pdfpecker {usage}");

        var pdf = Common.ReadPdf(io, positionals[0]);
        var result = PdfPages.Rotate(pdf, degrees, a.Get("pages"), new PdfPagesOptions { Password = Common.Password(a) }, ct);
        io.WriteOutput(output, result);
        Common.Report(io, output, result);
        return 0;
    }

    public static int Stamp(CliIo io, IReadOnlyList<string> args, CancellationToken ct)
    {
        const string usage = "stamp <input.pdf> <stamp.pdf|stamp.html> <output.pdf> [--underlay] [--opacity 0.5] [--position center] [--pages 1,3]";
        var a = Common.WithPassword(new OptionSet())
            .Flag("underlay").Value("opacity").Value("scale").Value("position").Value("offset-x").Value("offset-y")
            .Value("pages").Value("stamp-page").Value("stamp-password").Value("stamp-password-env").Parse(args);
        io.Quiet = a.Has("quiet");
        var positionals = Common.Positionals(a, 2, 3, usage);
        var output = Common.Output(a, positionals, 2, usage, positionals[0]);

        var password = Common.Password(a);
        var pdf = Common.ReadPdf(io, positionals[0]);
        var options = new PdfStampOptions
        {
            Underlay = a.Has("underlay"),
            Password = password,
            StampPassword = Common.Password(a, "stamp-password", "stamp-password-env"),
        };
        if (a.GetFloat("opacity") is { } opacity) options.Opacity = opacity;
        if (a.GetFloat("scale") is { } scale) options.Scale = scale;
        if (a.GetFloat("offset-x") is { } ox) options.OffsetX = ox;
        if (a.GetFloat("offset-y") is { } oy) options.OffsetY = oy;
        if (a.GetInt("stamp-page") is { } sp) options.StampPage = sp;
        if (a.Get("position") is { } pos) options.Position = ParsePosition(pos);
        if (a.Get("pages") is { } pages)
        {
            var wanted = PdfPageRange.Parse(pages).ToPageNumbers(PdfPages.Count(pdf, password)).ToHashSet();
            options.Pages = index => wanted.Contains(index + 1);
        }

        var stampPath = positionals[1];
        byte[] result;
        if (stampPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || stampPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
        {
            var htmlOptions = new PdfOptions
            {
                Margins = new PageMargins(0),
                BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(stampPath)),
            };
            result = PdfStamper.StampHtml(pdf, File.ReadAllText(stampPath), options, htmlOptions, ct);
        }
        else
        {
            result = PdfStamper.Stamp(pdf, File.ReadAllBytes(stampPath), options, ct);
        }

        io.WriteOutput(output, result);
        Common.Report(io, output, result);
        return 0;
    }

    private static PdfStampPosition ParsePosition(string s) => s.ToLowerInvariant().Replace("-", "") switch
    {
        "center" or "centre" => PdfStampPosition.Center,
        "top" => PdfStampPosition.Top,
        "bottom" => PdfStampPosition.Bottom,
        "left" => PdfStampPosition.Left,
        "right" => PdfStampPosition.Right,
        "topleft" => PdfStampPosition.TopLeft,
        "topright" => PdfStampPosition.TopRight,
        "bottomleft" => PdfStampPosition.BottomLeft,
        "bottomright" => PdfStampPosition.BottomRight,
        _ => throw new UsageException($"--position must be center, top, bottom, left, right, top-left, top-right, bottom-left or bottom-right, not '{s}'."),
    };
}

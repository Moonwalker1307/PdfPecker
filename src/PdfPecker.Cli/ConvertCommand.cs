using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace PdfPecker.Cli;

/// <summary>HTML or Markdown to PDF: <c>convert</c>, <c>watch</c> and the wkhtmltopdf-style call without a command word.</summary>
internal static class ConvertCommand
{
    private sealed class Job
    {
        public required List<string> Inputs { get; init; }
        public required string Output { get; init; }
        public required ParsedArgs Args { get; init; }
        public required bool WkDefaults { get; init; }
    }

    public static async Task<int> RunAsync(CliIo io, IReadOnlyList<string> args, bool wkDefaults, bool watch, CancellationToken ct)
    {
        var a = ConvertOptions.Create().Parse(args);
        io.Quiet = a.Has("quiet");
        ConvertOptions.WarnAboutIgnored(io, a);
        var job = ResolveJob(a, wkDefaults, watch);

        if (!watch)
        {
            Render(io, job, ct);
            return 0;
        }

        return await WatchAsync(io, job, ct).ConfigureAwait(false);
    }

    private static Job ResolveJob(ParsedArgs a, bool wkDefaults, bool watch)
    {
        var positionals = a.Positionals;
        List<string> inputs;
        string output;
        if (a.Get("output") is { } o)
        {
            inputs = positionals.ToList();
            output = o;
        }
        else if (positionals.Count == 1)
        {
            inputs = positionals.ToList();
            if (positionals[0] == "-") throw new UsageException("Reading from standard input needs an output: pdfpecker convert - out.pdf, or - to write to standard output.");
            if (IsUrl(positionals[0])) throw new UsageException(UrlMessage(positionals[0]));
            output = Path.ChangeExtension(StripFileUrl(positionals[0]), ".pdf");
        }
        else if (positionals.Count > 1)
        {
            inputs = positionals.Take(positionals.Count - 1).ToList();
            output = positionals[positionals.Count - 1];
            // The last argument is the output, so a mistyped call must not overwrite a source document with a PDF.
            if (output != "-" && !output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new UsageException($"The last argument is the output file and must end in .pdf (or be - for standard output), not '{output}'. Name the output with -o if it is something else.");
        }
        else
        {
            throw new UsageException("Give the HTML or Markdown file to convert: pdfpecker convert input.html [output.pdf]");
        }

        if (inputs.Count == 0) throw new UsageException("Give the HTML or Markdown file to convert: pdfpecker convert input.html [output.pdf]");
        foreach (var input in inputs)
        {
            if (IsUrl(input)) throw new UsageException(UrlMessage(input));
            if (output != "-" && input != "-" && string.Equals(Path.GetFullPath(StripFileUrl(input)), Path.GetFullPath(output), StringComparison.Ordinal))
                throw new UsageException($"The output '{output}' is also an input; choose another output file.");
        }

        if (inputs.Count > 1) ConvertOptions.CheckSeveralInputs(a);
        if (watch && inputs.Any(i => i == "-")) throw new UsageException("watch needs files; it cannot watch standard input.");
        if (watch && output == "-") throw new UsageException("watch needs an output file to rewrite, not standard output.");
        return new Job { Inputs = inputs, Output = output, Args = a, WkDefaults = wkDefaults };
    }

    private static bool IsUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string UrlMessage(string url)
        => $"'{url}' is a web address. PdfPecker converts files and never fetches anything over the network; download the page first, or save it to a file.";

    private static string StripFileUrl(string s)
    {
        if (!s.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return s;
        return Uri.UnescapeDataString(new Uri(s).LocalPath);
    }

    private static bool IsMarkdown(ParsedArgs a, string input)
    {
        if (a.Has("markdown")) return true;
        if (a.Has("html")) return false;
        var ext = Path.GetExtension(input);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An input read once, so that standard input can be converted a second time when the page numbers need it.</summary>
    private sealed record Source(string Raw, string Path, string Text, string? BaseDirectory);

    /// <summary>Converts every input and writes the output once.</summary>
    private static void Render(CliIo io, Job job, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var now = DateTimeOffset.Now;
        var pdf = job.Inputs.Count == 1
            ? RenderOne(job, Load(io, job.Inputs[0]), null, BandPages.Alone, now, alone: true, ct)
            : RenderSeveral(io, job, now, ct);
        io.WriteOutput(job.Output, pdf);
        if (job.Output != "-")
        {
            int pages = PdfPages.Count(pdf, Common.Password(job.Args, "user-password", "user-password-env"));
            io.Info($"Wrote {job.Output} ({pages.ToString(CultureInfo.InvariantCulture)} {(pages == 1 ? "page" : "pages")}, {FormatSize(pdf.Length)}) in {watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
        }
    }

    /// <summary>
    /// Converts each input on its own and merges them. What belongs to the whole file (PDF/A, encryption, linearization,
    /// the dates and the file ID) is applied to the merged result, since merging writes a new, plain PDF; and the
    /// wkhtmltopdf page numbers [page] and [topage] count the whole output, which takes a second conversion of the inputs
    /// once their page counts are known.
    /// </summary>
    private static byte[] RenderSeveral(CliIo io, Job job, DateTimeOffset now, CancellationToken ct)
    {
        var a = job.Args;
        var whole = ConvertOptions.Build(a, job.WkDefaults);
        var sources = new List<Source>();
        foreach (var raw in job.Inputs) sources.Add(Load(io, raw));
        var documentTitle = whole.Title ?? FindTitle(sources[0].Text);

        var pdfs = new byte[sources.Count][];
        var counts = new int[sources.Count];
        bool pageToken = ConvertOptions.BandsUse(a, "[page]");
        bool totalToken = ConvertOptions.BandsUse(a, "[topage]");
        for (int i = 0; i < sources.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            pdfs[i] = RenderOne(job, sources[i], documentTitle, BandPages.Alone, now, alone: false, ct);
            if (pageToken || totalToken) counts[i] = PdfPages.Count(pdfs[i]);
        }

        if (pageToken || totalToken)
        {
            int total = counts.Sum();
            int offset = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                // The headers and footers sit in the page margins, so they do not change where the pages break.
                if (totalToken || offset > 0)
                    pdfs[i] = RenderOne(job, sources[i], documentTitle, new BandPages(offset, counts[i], totalToken ? total : null), now, alone: false, ct);
                offset += counts[i];
            }
        }

        var merged = PdfMerger.Merge(pdfs, new PdfMergeOptions
        {
            Title = whole.Title,
            Author = whole.Author,
            Subject = whole.Subject,
            Keywords = whole.Keywords,
            CreationDate = whole.CreationDate,
        }, ct);

        if (whole.Conformance != PdfConformance.None)
        {
            var result = PdfToPdfA.Convert(merged, new PdfAConversionOptions { Conformance = whole.Conformance }, ct);
            foreach (var problem in result.Problems) io.Warn("PDF/A: " + problem);
            merged = result.Pdf;
        }

        if (whole.Linearize) return PdfLinearizer.Linearize(merged, whole.Security, null, ct);
        return whole.Security != null ? PdfProtector.Encrypt(merged, whole.Security, null, ct) : merged;
    }

    private static Source Load(CliIo io, string raw)
    {
        if (raw == "-")
        {
            // Text piped in has no folder of its own. The current folder is wherever the tool runs, often a server's
            // working directory, so a document from standard input reads local files only from a --base-dir it is given.
            return new Source(raw, "", io.ReadAllInputText(), null);
        }

        string path = StripFileUrl(raw);
        return new Source(raw, path, File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory());
    }

    /// <summary>
    /// Converts one input. <paramref name="alone"/> is false for one of several inputs, which leaves out what
    /// <see cref="RenderSeveral"/> applies to the merged file.
    /// </summary>
    private static byte[] RenderOne(Job job, Source source, string? documentTitle, BandPages pages, DateTimeOffset now, bool alone, CancellationToken ct)
    {
        var a = job.Args;
        var options = ConvertOptions.Build(a, job.WkDefaults);
        options.BaseDirectory ??= source.BaseDirectory;
        if (!alone)
        {
            options.Security = null;
            options.Linearize = false;
        }

        var sheet = new System.Text.StringBuilder();
        if (a.Get("user-style-sheet") is { } cssPath) sheet.Append(File.ReadAllText(cssPath)).Append('\n');
        var title = options.Title ?? FindTitle(source.Text);
        var bands = ConvertOptions.HeaderFooterStyleSheet(a, title, documentTitle ?? title, source.Raw == "-" ? "" : Path.GetFileName(source.Path), now, pages);
        if (bands != null) sheet.Append(bands);
        if (sheet.Length > 0) options.UserStyleSheet = sheet.ToString();

        return IsMarkdown(a, source.Raw)
            ? MarkdownToPdf.Convert(source.Text, options, new MarkdownOptions { Title = options.Title }, ct)
            : HtmlToPdf.Convert(source.Text, options, ct);
    }

    /// <summary>The text of the document's title element, for the [title] token; null when there is none.</summary>
    private static string? FindTitle(string html)
    {
        int open = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int start = html.IndexOf('>', open);
        if (start < 0) return null;
        int end = html.IndexOf("</title", start, StringComparison.OrdinalIgnoreCase);
        // The title is text with character references (Q&amp;A), as a browser shows it.
        return end < 0 ? null : WebUtility.HtmlDecode(html.Substring(start + 1, end - start - 1)).Trim();
    }

    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";

    private static async Task<int> WatchAsync(CliIo io, Job job, CancellationToken ct)
    {
        var signal = new SemaphoreSlim(0);
        var outputFull = Path.GetFullPath(job.Output);
        var outputTemp = CliIo.TemporaryPath(outputFull);

        void OnChange(object? sender, FileSystemEventArgs e)
        {
            // Writing the PDF (and its temporary file) must not start another render. The paths are compared whole, so
            // an input whose name only starts with the output's, report.html beside -o report, still renders again.
            if (string.Equals(e.FullPath, outputFull, StringComparison.Ordinal) || string.Equals(e.FullPath, outputTemp, StringComparison.Ordinal)) return;
            try { signal.Release(); } catch (ObjectDisposedException) { }
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in job.Inputs)
            directories.Add(Path.GetDirectoryName(Path.GetFullPath(StripFileUrl(input))) ?? Directory.GetCurrentDirectory());
        if (job.Args.Get("base-dir") is { } b) directories.Add(Path.GetFullPath(b));
        if (job.Args.Get("user-style-sheet") is { } css) directories.Add(Path.GetDirectoryName(Path.GetFullPath(css)) ?? Directory.GetCurrentDirectory());

        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var dir in directories)
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                w.Changed += OnChange;
                w.Created += OnChange;
                w.Renamed += OnChange;
                w.Deleted += OnChange;
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }

            io.Info($"Watching {string.Join(", ", job.Inputs)} and writing {job.Output}. Press Ctrl+C to stop.");
            RenderQuietly(io, job, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await signal.WaitAsync(ct).ConfigureAwait(false);
                    // Editors write a file in several steps; wait until the saves have settled.
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (signal.Wait(0)) { }
                RenderQuietly(io, job, ct);
            }
        }
        finally
        {
            foreach (var w in watchers) w.Dispose();
            signal.Dispose();
        }

        return 0;
    }

    /// <summary>A failed render is shown and watching goes on, because the next save is usually the fix.</summary>
    private static void RenderQuietly(CliIo io, Job job, CancellationToken ct)
    {
        try
        {
            Render(io, job, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (CliApp.IsExpectedFailure(e))
        {
            io.Error.WriteLine("error: " + e.Message);
        }
    }
}

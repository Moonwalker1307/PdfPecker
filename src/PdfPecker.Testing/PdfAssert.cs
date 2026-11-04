using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PdfPecker.Testing;

/// <summary>
/// Checks for PDFs in tests: snapshot comparisons, text and page counts. A failed check throws
/// <see cref="PdfAssertException"/>, so they work with any test framework.
/// </summary>
public static class PdfAssert
{
    /// <summary>The environment variable that turns every snapshot check into an update of its snapshot.</summary>
    public const string UpdateSnapshotsVariable = "PDFPECKER_UPDATE_SNAPSHOTS";

    /// <summary>
    /// Checks that a PDF matches the snapshot <c>name.pdf</c>, by default in a <c>Snapshots</c> folder next to the
    /// calling test's source file. Dates, file IDs, XMP document IDs and stream compression are left out of the
    /// comparison, so a PDF made again from the same input matches without a fixed creation date.
    /// </summary>
    /// <remarks>
    /// When the snapshot is missing or differs, the check writes the PDF as <c>name.received.pdf</c> next to the
    /// snapshot, and for each page that looks different an <c>name.page-N.expected.png</c>, <c>.actual.png</c> and
    /// <c>.diff.png</c>, in which the changed pixels are magenta. Replace the snapshot with the received file to accept
    /// the change, or run the tests with the <c>PDFPECKER_UPDATE_SNAPSHOTS</c> environment variable set to <c>1</c>.
    /// A check that passes deletes the files an earlier failure of the same snapshot left.
    /// </remarks>
    /// <param name="pdf">The PDF under test.</param>
    /// <param name="name">The snapshot's name: a file name without extension, unique in its folder.</param>
    /// <param name="options">Where the snapshots are and what has to match; null for the defaults.</param>
    /// <param name="sourceFilePath">Filled in by the compiler with the caller's source file; don't pass it.</param>
    /// <exception cref="PdfAssertException">The snapshot is missing, differs, or either file can't be read.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or not a portable file name.</exception>
    /// <exception cref="InvalidOperationException">No snapshot folder is set and the caller's source folder doesn't exist.</exception>
    public static void MatchesSnapshot(byte[] pdf, string name, PdfSnapshotOptions? options = null,
        [CallerFilePath] string sourceFilePath = "")
    {
        ArgumentNullException.ThrowIfNull(pdf);
        CheckName(name);
        options ??= new PdfSnapshotOptions();
        var directory = SnapshotDirectory(options, sourceFilePath);
        var snapshotPath = Path.Combine(directory, name + ".pdf");
        var receivedPath = Path.Combine(directory, name + ".received.pdf");
        DeleteEarlierFailure(directory, name);
        bool update = options.UpdateSnapshots || UpdateRequestedByEnvironment();

        if (!File.Exists(snapshotPath))
        {
            if (update)
            {
                Write(snapshotPath, pdf);
                return;
            }
            Write(receivedPath, pdf);
            throw new PdfAssertException(
                $"There is no snapshot {snapshotPath} yet. The PDF was written to {receivedPath}: check it, and rename it " +
                $"to {name}.pdf to make it the snapshot, or run the tests with {UpdateSnapshotsVariable}=1.",
                [receivedPath]);
        }

        var expected = File.ReadAllBytes(snapshotPath);
        string? contentDifference = null;
        if (options.Comparison == PdfSnapshotComparison.Content)
        {
            contentDifference = PdfNormalizer.FirstDifference(PdfNormalizer.Normalize(expected), PdfNormalizer.Normalize(pdf));
            if (contentDifference is null) return;
            if (update)
            {
                Write(snapshotPath, pdf);
                return;
            }
        }
        else if (expected.AsSpan().SequenceEqual(pdf)) return;

        var pages = ComparePages(expected, pdf, options);
        long allowed = options.Comparison == PdfSnapshotComparison.Appearance ? options.MaxDifferentPixels : 0;
        var differing = pages.Where(p => p.Difference.DifferentPixels > allowed).ToList();
        if (options.Comparison == PdfSnapshotComparison.Appearance && differing.Count == 0) return;

        if (update)
        {
            Write(snapshotPath, pdf);
            return;
        }

        var files = new List<string>();
        Write(receivedPath, pdf);
        files.Add(receivedPath);
        if (options.WriteDiffImages)
        {
            foreach (var page in differing)
                files.AddRange(WritePageImages(directory, name, page));
        }

        var message = new StringBuilder();
        message.Append(CultureInfo.InvariantCulture, $"The PDF doesn't match the snapshot {snapshotPath}");
        message.Append(contentDifference is null ? ". " : $": {contentDifference}. ");
        if (differing.Count == 0)
            message.Append("The pages look the same, so the change is in what the file holds rather than in how it looks. ");
        else
        {
            message.Append(differing.Count == 1 ? "Page " : "Pages ");
            message.AppendJoin(", ", differing.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{p.Number} ({p.Difference.DifferentPixels:N0} pixels{(p.Note is null ? "" : ", " + p.Note)})")));
            message.Append(differing.Count == 1 ? " looks different" : " look different");
            message.Append(options.WriteDiffImages ? $"; the diff images are next to the snapshot. " : ". ");
        }
        message.Append(CultureInfo.InvariantCulture,
            $"The PDF was written to {receivedPath}: replace the snapshot with it to accept the change, or run the tests with {UpdateSnapshotsVariable}=1.");
        throw new PdfAssertException(message.ToString(), files);
    }

    /// <summary>Checks that the PDF's text contains <paramref name="text"/>, with runs of white space treated as one space.</summary>
    /// <exception cref="PdfAssertException">The text isn't there, or the PDF can't be read.</exception>
    public static void ContainsText(byte[] pdf, string text)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(text);
        var all = Read(pdf, static p => PdfText.Extract(p));
        if (!Collapse(all).Contains(Collapse(text), StringComparison.Ordinal))
            throw new PdfAssertException($"The PDF doesn't contain \"{text}\". Its text starts: {Excerpt(all)}");
    }

    /// <summary>
    /// Checks that the text of page <paramref name="pageNumber"/>, counted from 1, contains <paramref name="text"/>,
    /// with runs of white space treated as one space.
    /// </summary>
    /// <exception cref="PdfAssertException">The text isn't on the page, the page doesn't exist, or the PDF can't be read.</exception>
    public static void ContainsText(byte[] pdf, string text, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        int count = Read(pdf, static p => PdfPages.Count(p));
        if (pageNumber > count)
            throw new PdfAssertException(string.Create(CultureInfo.InvariantCulture,
                $"The PDF has {count} page{(count == 1 ? "" : "s")}, so it has no page {pageNumber}."));
        var page = Read(pdf, p => PdfText.ExtractPage(p, pageNumber).Text);
        if (!Collapse(page).Contains(Collapse(text), StringComparison.Ordinal))
            throw new PdfAssertException(string.Create(CultureInfo.InvariantCulture,
                $"Page {pageNumber} doesn't contain \"{text}\". Its text starts: {Excerpt(page)}"));
    }

    /// <summary>Checks that the PDF's text doesn't contain <paramref name="text"/>, with runs of white space treated as one space.</summary>
    /// <exception cref="PdfAssertException">The text is there, or the PDF can't be read.</exception>
    public static void DoesNotContainText(byte[] pdf, string text)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentException.ThrowIfNullOrEmpty(text);
        var all = Collapse(Read(pdf, static p => PdfText.Extract(p)));
        int at = all.IndexOf(Collapse(text), StringComparison.Ordinal);
        if (at >= 0)
        {
            int from = Math.Max(0, at - 40);
            throw new PdfAssertException($"The PDF contains \"{text}\": …{all.AsSpan(from, Math.Min(all.Length - from, text.Length + 80))}…");
        }
    }

    /// <summary>Checks that the PDF has <paramref name="expected"/> pages.</summary>
    /// <exception cref="PdfAssertException">The page count differs, or the PDF can't be read.</exception>
    public static void HasPageCount(byte[] pdf, int expected)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfNegative(expected);
        int count = Read(pdf, static p => PdfPages.Count(p));
        if (count != expected)
            throw new PdfAssertException(string.Create(CultureInfo.InvariantCulture,
                $"The PDF has {count} page{(count == 1 ? "" : "s")}, not {expected}."));
    }

    // ---- snapshot files ----

    private static readonly char[] UnportableNameChars = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static void CheckName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(UnportableNameChars) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException($"\"{name}\" isn't a file name that works on every system; use letters, digits, '-', '_' and '.'.", nameof(name));
    }

    private static string SnapshotDirectory(PdfSnapshotOptions options, string sourceFilePath)
    {
        if (!string.IsNullOrEmpty(options.Directory)) return Path.GetFullPath(options.Directory);
        var sourceDirectory = string.IsNullOrEmpty(sourceFilePath) ? null : Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrEmpty(sourceDirectory) || !System.IO.Directory.Exists(sourceDirectory))
            throw new InvalidOperationException(
                $"The snapshots belong next to the test's source file, but its folder ({(string.IsNullOrEmpty(sourceDirectory) ? "unknown" : sourceDirectory)}) " +
                "doesn't exist on this machine, as in builds with ContinuousIntegrationBuild or deterministic source paths. Set PdfSnapshotOptions.Directory.");
        return Path.Combine(sourceDirectory, "Snapshots");
    }

    private static bool UpdateRequestedByEnvironment() =>
        Environment.GetEnvironmentVariable(UpdateSnapshotsVariable) is { } value
        && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static void DeleteEarlierFailure(string directory, string name)
    {
        if (!System.IO.Directory.Exists(directory)) return;
        File.Delete(Path.Combine(directory, name + ".received.pdf"));
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, name + ".page-*.png"))
        {
            // The pattern also matches longer snapshot names that start with this one followed by ".page-".
            var rest = Path.GetFileName(file.AsSpan())[(name.Length + ".page-".Length)..];
            int dot = rest.IndexOf('.');
            if (dot > 0 && int.TryParse(rest[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && rest[dot..] is ".expected.png" or ".actual.png" or ".diff.png")
                File.Delete(file);
        }
    }

    private static void Write(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    // ---- pages ----

    private sealed record ComparedPage(int Number, PageDifference Difference, RenderedImage? Expected, RenderedImage? Actual, string? Note);

    private static List<ComparedPage> ComparePages(byte[] expected, byte[] actual, PdfSnapshotOptions options)
    {
        var expectedRenderer = Open(expected, options.Password, "snapshot");
        var actualRenderer = Open(actual, options.Password, "PDF under test");
        var imageOptions = new ImageOptions { Dpi = options.Dpi, Password = options.Password };
        int count = Math.Max(expectedRenderer.PageCount, actualRenderer.PageCount);
        var pages = new List<ComparedPage>(count);
        for (int i = 0; i < count; i++)
        {
            var e = i < expectedRenderer.PageCount ? Render(expectedRenderer, i, imageOptions, "snapshot") : null;
            var a = i < actualRenderer.PageCount ? Render(actualRenderer, i, imageOptions, "PDF under test") : null;
            var difference = PixelDiff.Compare(e?.Rgba, e?.Width ?? 0, e?.Height ?? 0, a?.Rgba, a?.Width ?? 0, a?.Height ?? 0,
                options.ColorTolerance);
            string? note = e is null ? "only in the PDF" : a is null ? "only in the snapshot"
                : e.Width != a.Width || e.Height != a.Height ? "a different size" : null;
            pages.Add(new ComparedPage(i + 1, difference, e, a, note));
        }
        return pages;
    }

    private static List<string> WritePageImages(string directory, string name, ComparedPage page)
    {
        var files = new List<string>(3);
        var prefix = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{name}.page-{page.Number}"));
        if (page.Expected is not null)
        {
            Write(prefix + ".expected.png", page.Expected.ToPng());
            files.Add(prefix + ".expected.png");
        }
        if (page.Actual is not null)
        {
            Write(prefix + ".actual.png", page.Actual.ToPng());
            files.Add(prefix + ".actual.png");
        }
        Write(prefix + ".diff.png", PngWriter.Encode(page.Difference.Rgb, page.Difference.Width, page.Difference.Height));
        files.Add(prefix + ".diff.png");
        return files;
    }

    private static PdfRenderer Open(byte[] pdf, string? password, string which)
    {
        try
        {
            return PdfRenderer.Open(pdf, password);
        }
        catch (Exception e) when (e is PdfPeckerException or InvalidDataException)
        {
            throw new PdfAssertException($"The {which} can't be read as a PDF: {e.Message}", e);
        }
    }

    private static RenderedImage Render(PdfRenderer renderer, int index, ImageOptions options, string which)
    {
        try
        {
            return renderer.RenderPage(index, options);
        }
        catch (Exception e) when (e is PdfPeckerException or InvalidDataException)
        {
            throw new PdfAssertException(string.Create(CultureInfo.InvariantCulture,
                $"Page {index + 1} of the {which} can't be rendered: {e.Message}"), e);
        }
    }

    // ---- text ----

    private static T Read<T>(byte[] pdf, Func<byte[], T> read)
    {
        try
        {
            return read(pdf);
        }
        catch (Exception e) when (e is PdfPeckerException or InvalidDataException)
        {
            throw new PdfAssertException($"The PDF can't be read: {e.Message}", e);
        }
    }

    /// <summary>The text with every run of white space as one space, so line breaks in the extracted text don't matter.</summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Excerpt(string text)
    {
        var collapsed = Collapse(text);
        return collapsed.Length == 0 ? "(the PDF has no text)" : collapsed.Length <= 200 ? $"\"{collapsed}\"" : $"\"{collapsed[..200]}…\"";
    }
}

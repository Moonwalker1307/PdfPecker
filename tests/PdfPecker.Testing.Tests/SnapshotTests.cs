using PdfPecker.Testing;

namespace PdfPecker.Testing.Tests;

/// <summary>A folder of its own for each test, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdfpecker-testing-" + Guid.NewGuid().ToString("N"));

    public PdfSnapshotOptions Options(Action<PdfSnapshotOptions>? configure = null)
    {
        var options = new PdfSnapshotOptions { Directory = Path };
        configure?.Invoke(options);
        return options;
    }

    public string[] Files() => Directory.Exists(Path)
        ? Directory.GetFiles(Path).Select(f => System.IO.Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray()
        : [];

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal static class Pdfs
{
    public const string Invoice = """
        <html><head><title>Invoice</title><style>
          body { font-family: sans-serif; margin: 0 }
          h1 { color: #1a4b8c } td { padding: 4pt 8pt }
        </style></head><body>
        <h1>Invoice 2026-104</h1>
        <table><tr><td>Consulting</td><td>1,200.00</td></tr><tr><td>Total due</td><td>1,500.00</td></tr></table>
        </body></html>
        """;

    /// <summary>A PDF made now, with the current time as its creation date.</summary>
    public static byte[] Make(string html = Invoice, Action<PdfOptions>? configure = null)
    {
        var options = new PdfOptions { PageSize = PageSize.A5 };
        configure?.Invoke(options);
        return HtmlToPdf.Convert(html, options);
    }
}

public sealed class SnapshotTests
{
    [Fact]
    public void A_missing_snapshot_fails_and_writes_the_received_file()
    {
        using var folder = new TempFolder();
        var pdf = Pdfs.Make();
        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(pdf, "invoice", folder.Options()));
        Assert.Contains("no snapshot", e.Message, StringComparison.Ordinal);
        Assert.Equal(["invoice.received.pdf"], folder.Files());
        Assert.Equal([System.IO.Path.Combine(folder.Path, "invoice.received.pdf")], e.Files);
        Assert.Equal(pdf, File.ReadAllBytes(e.Files[0]));
    }

    [Fact]
    public void Update_writes_a_missing_snapshot_and_passes()
    {
        using var folder = new TempFolder();
        var pdf = Pdfs.Make();
        PdfAssert.MatchesSnapshot(pdf, "invoice", folder.Options(o => o.UpdateSnapshots = true));
        Assert.Equal(["invoice.pdf"], folder.Files());
        PdfAssert.MatchesSnapshot(pdf, "invoice", folder.Options());
    }

    [Fact]
    public void The_same_document_made_again_matches_although_its_dates_and_id_differ()
    {
        using var folder = new TempFolder();
        var first = Pdfs.Make(configure: o => o.CreationDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var second = Pdfs.Make(configure: o => o.CreationDate = new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.FromHours(1)));
        Assert.NotEqual(first, second);
        PdfAssert.MatchesSnapshot(first, "invoice", folder.Options(o => o.UpdateSnapshots = true));
        PdfAssert.MatchesSnapshot(second, "invoice", folder.Options());
    }

    [Fact]
    public void The_word_stream_in_a_title_or_link_is_not_taken_for_a_stream()
    {
        // The words once made the object a stream that swallowed the rest of the file with its random /ID.
        using var folder = new TempFolder();
        const string html = """
            <html><head><title>Upstream report</title></head>
            <body><p><a href="https://example.com/livestream">Watch the livestream</a></p></body></html>
            """;
        var first = Pdfs.Make(html, o => o.CreationDate = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var second = Pdfs.Make(html, o => o.CreationDate = new DateTimeOffset(2026, 11, 4, 20, 0, 0, TimeSpan.Zero));
        PdfAssert.MatchesSnapshot(first, "upstream", folder.Options(o => o.UpdateSnapshots = true));
        PdfAssert.MatchesSnapshot(second, "upstream", folder.Options());
    }

    [Fact]
    public void PDF_A_metadata_dates_are_left_out()
    {
        using var folder = new TempFolder();
        static byte[] Make(int year) => Pdfs.Make(configure: o =>
        {
            o.Conformance = PdfConformance.PdfA2b;
            o.CreationDate = new DateTimeOffset(year, 5, 6, 7, 8, 9, TimeSpan.Zero);
        });
        PdfAssert.MatchesSnapshot(Make(2025), "pdfa", folder.Options(o => o.UpdateSnapshots = true));
        PdfAssert.MatchesSnapshot(Make(2026), "pdfa", folder.Options());
    }

    [Fact]
    public void A_changed_text_fails_with_the_page_images_and_the_changed_pixels_in_magenta()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var changed = Pdfs.Make(Pdfs.Invoice.Replace("1,500.00", "1,900.00", StringComparison.Ordinal));

        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options()));
        Assert.Contains("stream of object", e.Message, StringComparison.Ordinal);
        Assert.Contains("Page 1 (", e.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["invoice.page-1.actual.png", "invoice.page-1.diff.png", "invoice.page-1.expected.png", "invoice.pdf", "invoice.received.pdf"],
            folder.Files());

        var diff = PdfRenderer.Open(ImageToPdf.Convert([File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "invoice.page-1.diff.png"))]))
            .RenderPage(0, new ImageOptions { Dpi = 72 });
        Assert.True(CountMagenta(diff.Rgba) > 0);
    }

    [Fact]
    public void A_pass_deletes_the_files_of_an_earlier_failure()
    {
        using var folder = new TempFolder();
        var pdf = Pdfs.Make();
        PdfAssert.MatchesSnapshot(pdf, "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var changed = Pdfs.Make(Pdfs.Invoice.Replace("Consulting", "Design", StringComparison.Ordinal));
        Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options()));
        // Another snapshot whose name starts with this one keeps its files.
        Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(changed, "invoice.page-x", folder.Options()));
        Assert.Equal(6, folder.Files().Length);

        PdfAssert.MatchesSnapshot(pdf, "invoice", folder.Options());
        Assert.Equal(["invoice.page-x.received.pdf", "invoice.pdf"], folder.Files());
    }

    [Fact]
    public void Update_replaces_a_snapshot_that_differs()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var changed = Pdfs.Make(Pdfs.Invoice.Replace("Consulting", "Design", StringComparison.Ordinal));
        PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options(o => o.UpdateSnapshots = true));
        Assert.Equal(changed, File.ReadAllBytes(System.IO.Path.Combine(folder.Path, "invoice.pdf")));
        Assert.Equal(["invoice.pdf"], folder.Files());
    }

    [Fact]
    public void A_change_that_does_not_show_fails_the_content_comparison_without_images()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var retitled = Pdfs.Make(Pdfs.Invoice.Replace("<title>Invoice</title>", "<title>Bill</title>", StringComparison.Ordinal));

        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(retitled, "invoice", folder.Options()));
        Assert.Contains("look the same", e.Message, StringComparison.Ordinal);
        Assert.Equal(["invoice.pdf", "invoice.received.pdf"], folder.Files());

        // The appearance comparison only looks at the pages.
        PdfAssert.MatchesSnapshot(retitled, "invoice", folder.Options(o => o.Comparison = PdfSnapshotComparison.Appearance));
        Assert.Equal(["invoice.pdf"], folder.Files());
    }

    [Fact]
    public void Appearance_comparison_allows_the_set_number_of_pixels()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var changed = Pdfs.Make(Pdfs.Invoice.Replace("1,500.00", "1,500.08", StringComparison.Ordinal));

        var e = Assert.Throws<PdfAssertException>(() =>
            PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options(o => o.Comparison = PdfSnapshotComparison.Appearance)));
        Assert.Contains("Page 1 (", e.Message, StringComparison.Ordinal);
        Assert.Equal(4, e.Files.Count);

        PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options(o =>
        {
            o.Comparison = PdfSnapshotComparison.Appearance;
            o.MaxDifferentPixels = 10_000;
        }));
    }

    [Fact]
    public void Colour_tolerance_lets_slightly_different_colours_match()
    {
        using var folder = new TempFolder();
        static byte[] Box(string colour) => Pdfs.Make($"<div style='background:{colour};height:100pt'></div>");
        PdfAssert.MatchesSnapshot(Box("#808080"), "box", folder.Options(o => o.UpdateSnapshots = true));

        Assert.Throws<PdfAssertException>(() =>
            PdfAssert.MatchesSnapshot(Box("#828282"), "box", folder.Options(o => o.Comparison = PdfSnapshotComparison.Appearance)));
        PdfAssert.MatchesSnapshot(Box("#828282"), "box", folder.Options(o =>
        {
            o.Comparison = PdfSnapshotComparison.Appearance;
            o.ColorTolerance = 3;
        }));
    }

    [Fact]
    public void An_extra_page_is_reported_and_drawn_as_a_diff()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var longer = Pdfs.Make(Pdfs.Invoice.Replace("</body>", "<p style='break-before:page'>Terms</p></body>", StringComparison.Ordinal));

        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(longer, "invoice", folder.Options()));
        Assert.Contains("only in the PDF", e.Message, StringComparison.Ordinal);
        Assert.Contains("invoice.page-2.diff.png", folder.Files());
        Assert.Contains("invoice.page-2.actual.png", folder.Files());
        Assert.DoesNotContain("invoice.page-2.expected.png", folder.Files());
    }

    [Fact]
    public void Diff_images_can_be_turned_off()
    {
        using var folder = new TempFolder();
        PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options(o => o.UpdateSnapshots = true));
        var changed = Pdfs.Make(Pdfs.Invoice.Replace("Consulting", "Design", StringComparison.Ordinal));
        Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(changed, "invoice", folder.Options(o => o.WriteDiffImages = false)));
        Assert.Equal(["invoice.pdf", "invoice.received.pdf"], folder.Files());
    }

    [Fact]
    public void A_damaged_snapshot_fails_as_an_assertion()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Path);
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path, "broken.pdf"), "%PDF-1.7\n1 0 obj\n<< /Length 5 /Filter /FlateDecode >>\nstream\nxxxxx\nendstream\n"u8.ToArray());
        var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(Pdfs.Make(), "broken", folder.Options()));
        Assert.Contains("snapshot", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("name.")]
    [InlineData("what?")]
    public void Names_must_be_portable_file_names(string name)
    {
        using var folder = new TempFolder();
        Assert.ThrowsAny<ArgumentException>(() => PdfAssert.MatchesSnapshot(Pdfs.Make(), name, folder.Options()));
    }

    [Fact]
    public void Without_a_folder_the_snapshots_are_next_to_the_test_source()
    {
        var name = "default-folder-" + Guid.NewGuid().ToString("N");
        var expected = System.IO.Path.Combine(SourceDirectory(), "Snapshots", name + ".received.pdf");
        try
        {
            var e = Assert.Throws<PdfAssertException>(() => PdfAssert.MatchesSnapshot(Pdfs.Make(), name));
            Assert.Equal([expected], e.Files);
            Assert.True(File.Exists(expected));
        }
        finally
        {
            File.Delete(expected);
        }
    }

    [Fact]
    public void Options_refuse_values_out_of_range()
    {
        var options = new PdfSnapshotOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Dpi = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.ColorTolerance = 256);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxDifferentPixels = -1);
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        System.IO.Path.GetDirectoryName(path)!;

    private static int CountMagenta(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i] > 200 && rgba[i + 1] < 60 && rgba[i + 2] > 200) count++;
        return count;
    }
}

public sealed class NormalizerTests
{
    private static byte[] File(string title, System.IO.Compression.CompressionLevel level)
    {
        var content = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(content, level, leaveOpen: true))
            z.Write(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 12 Tf (Hello) Tj ET\n", 200))));
        var pdf = new MemoryStream();
        void Write(string s) => pdf.Write(System.Text.Encoding.Latin1.GetBytes(s));
        Write($"%PDF-1.7\n1 0 obj\n<< /Title ({title} \\(stream\\)) /Keywords <73747265616d> % stream\n/URI /livestream >>\nendobj\n");
        Write($"2 0 obj\n<< /Length {content.Length} /Filter /FlateDecode >>\nstream\n");
        pdf.Write(content.ToArray());
        Write("\nendstream\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return pdf.ToArray();
    }

    [Theory]
    [InlineData("Quarterly report")]
    [InlineData("Upstream report")]
    [InlineData("Mainstream endobj")]
    public void Words_in_strings_names_and_comments_are_not_keywords(string title)
    {
        var fast = PdfNormalizer.Normalize(File(title, System.IO.Compression.CompressionLevel.Fastest));
        var small = PdfNormalizer.Normalize(File(title, System.IO.Compression.CompressionLevel.SmallestSize));
        Assert.Equal(["1", "2"], fast.Select(o => o.Number));
        Assert.Equal("", fast[0].Stream);
        Assert.StartsWith("BT /F1 12 Tf", fast[1].Stream, StringComparison.Ordinal);
        // The streams were compressed differently, and their inflated content is what is compared.
        Assert.Null(PdfNormalizer.FirstDifference(fast, small));
    }

    [Fact]
    public void An_unbalanced_string_falls_back_to_the_plain_search()
    {
        var objects = PdfNormalizer.Normalize("%PDF-1.7\n1 0 obj\n(open\nendobj\n2 0 obj\n<< /A 1 >>\nendobj\n"u8.ToArray());
        Assert.Equal(["1", "2"], objects.Select(o => o.Number));
    }
}

/// <summary>The environment variable is process-wide, so these tests don't run alongside the others.</summary>
[CollectionDefinition(nameof(EnvironmentCollection), DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection(nameof(EnvironmentCollection))]
public sealed class UpdateVariableTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void The_environment_variable_updates_snapshots(string value)
    {
        using var folder = new TempFolder();
        Environment.SetEnvironmentVariable(PdfAssert.UpdateSnapshotsVariable, value);
        try
        {
            PdfAssert.MatchesSnapshot(Pdfs.Make(), "invoice", folder.Options());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PdfAssert.UpdateSnapshotsVariable, null);
        }
        Assert.Equal(["invoice.pdf"], folder.Files());
    }
}

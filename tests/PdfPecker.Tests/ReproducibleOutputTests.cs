using System.Globalization;
using System.Text.RegularExpressions;
using PdfPecker.Pdf;

namespace PdfPecker.Tests;

/// <summary>
/// Fixed dates and content-derived file identifiers. The tests change <c>SOURCE_DATE_EPOCH</c>, which the whole process
/// sees, so they run in a collection of their own after the parallel tests.
/// </summary>
[Collection(ReproducibleOutputCollection.Name)]
public class ReproducibleOutputTests
{
    private const string Html = "<html><head><title>Report</title></head><body><h1>Totals</h1><p>Sum: 42</p></body></html>";
    private static readonly DateTimeOffset Fixed = new(2024, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(2));

    private static string Text(byte[] pdf) => PdfTestHelpers.Latin1(pdf);

    private static string Id(byte[] pdf) => Regex.Match(Text(pdf), @"/ID \[<([0-9A-Fa-f]+)><([0-9A-Fa-f]+)>\]").Groups[1].Value;

    private static string CreationDate(byte[] pdf) => Regex.Match(Text(pdf), @"/CreationDate \(([^)]*)\)").Groups[1].Value;

    /// <summary>Runs <paramref name="action"/> with the environment variable set (or removed, for null), then restores it.</summary>
    private static void WithEpoch(string? value, Action action)
    {
        var saved = Environment.GetEnvironmentVariable(ReproducibleDate.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, value);
        try { action(); }
        finally { Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, saved); }
    }

    [Fact]
    public void Same_creation_date_gives_byte_identical_output() => WithEpoch(null, () =>
    {
        var a = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        var b = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        Assert.Equal(a, b);
        // UTC, to the second.
        Assert.Equal("D:20240102010405Z", CreationDate(a));
        Assert.Equal(32, Id(a).Length);

        // The identifier follows the content.
        var other = HtmlToPdf.Convert(Html.Replace("42", "43", StringComparison.Ordinal), new PdfOptions { CreationDate = Fixed });
        Assert.NotEqual(Id(a), Id(other));
    });

    [Fact]
    public void Fixed_date_reaches_xmp_metadata_and_attachments() => WithEpoch(null, () =>
    {
        PdfOptions Options() => new()
        {
            CreationDate = Fixed,
            Compress = false,
            Conformance = PdfConformance.PdfA3b,
            Attachments = { new PdfAttachment("data.csv", "a,b\n1,2\n"u8.ToArray()) },
        };
        var a = HtmlToPdf.Convert(Html, Options());
        var b = HtmlToPdf.Convert(Html, Options());
        Assert.Equal(a, b);
        var text = Text(a);
        Assert.Contains("<xmp:CreateDate>2024-01-02T01:04:05Z</xmp:CreateDate>", text, StringComparison.Ordinal);
        Assert.Contains("<xmp:ModifyDate>2024-01-02T01:04:05Z</xmp:ModifyDate>", text, StringComparison.Ordinal);
        // The attachment has no date of its own, so it gets the document's.
        Assert.Matches(@"/Params << /Size \d+ /ModDate \(D:20240102010405", text);
    });

    [Fact]
    public void Markdown_with_a_fixed_date_is_reproducible() => WithEpoch(null, () =>
    {
        var a = MarkdownToPdf.Convert("# Title\n\nSome *text*.", new PdfOptions { CreationDate = Fixed });
        var b = MarkdownToPdf.Convert("# Title\n\nSome *text*.", new PdfOptions { CreationDate = Fixed });
        Assert.Equal(a, b);
    });

    [Fact]
    public void Source_date_epoch_fixes_the_date_and_identifier() => WithEpoch("1700000000", () =>
    {
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.Equal(a, b);
        Assert.Equal("D:20231114221320Z", CreationDate(a));

        // The option takes precedence over the environment.
        var c = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        Assert.Equal("D:20240102010405Z", CreationDate(c));
    });

    [Fact]
    public void Invalid_source_date_epoch_is_ignored() => WithEpoch("yesterday", () =>
    {
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.NotEqual(Id(a), Id(b));
    });

    [Fact]
    public void Default_output_keeps_current_date_and_random_identifier() => WithEpoch(null, () =>
    {
        var before = DateTime.UtcNow.AddSeconds(-2);
        var a = HtmlToPdf.Convert(Html);
        var b = HtmlToPdf.Convert(Html);
        Assert.NotEqual(Id(a), Id(b));
        var date = DateTime.ParseExact(CreationDate(a), "'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.InRange(date, before, DateTime.UtcNow.AddSeconds(2));
    });

    [Fact]
    public void Fixed_date_is_written_the_same_under_any_culture() => WithEpoch(null, () =>
    {
        var invariant = HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed });
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal(invariant, HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed }));
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            Assert.Equal(invariant, HtmlToPdf.Convert(Html, new PdfOptions { CreationDate = Fixed }));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    });

    // ------------------------------------------------------------------ merging, page tools, stamping, updates

    private static readonly Regex IdPattern = new(@"/ID ?\[ ?<([0-9A-Fa-f]+)> ?<([0-9A-Fa-f]+)> ?\]");

    /// <summary>The first part of the last file identifier, the one of the latest revision.</summary>
    private static string FirstId(byte[] pdf) => IdPattern.Matches(Text(pdf)).Last().Groups[1].Value;

    /// <summary>The second part of the last file identifier, which marks the latest version.</summary>
    private static string SecondId(byte[] pdf) => IdPattern.Matches(Text(pdf)).Last().Groups[2].Value;

    private static string ModDate(byte[] pdf) => Regex.Match(Text(pdf), @"/ModDate \(([^)]*)\)").Groups[1].Value;

    private static byte[] Source(string text, int paragraphs = 3) => HtmlToPdf.Convert(
        "<h1>" + text + "</h1>" + string.Concat(Enumerable.Range(0, paragraphs).Select(i => $"<p>{text} paragraph {i}</p>")),
        new PdfOptions { CreationDate = new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.Zero), Compress = paragraphs < 100 });

    [Fact]
    public void Merge_with_a_fixed_date_is_reproducible() => WithEpoch(null, () =>
    {
        byte[] Merge(string second) => PdfMerger.Merge(new[] { Source("one"), Source(second) }, new PdfMergeOptions { CreationDate = Fixed });
        var a = Merge("two");
        Assert.Equal(a, Merge("two"));
        Assert.Equal("D:20240102010405Z", CreationDate(a));
        Assert.Equal("D:20240102010405Z", ModDate(a));
        Assert.Equal(FirstId(a), SecondId(a));
        Assert.NotEqual(Id(a), Id(Merge("three")));

        // Without a date the identifier stays random.
        Assert.NotEqual(Id(PdfMerger.Merge(Source("one"), Source("two"))), Id(PdfMerger.Merge(Source("one"), Source("two"))));
    });

    [Fact]
    public Task Streamed_merge_hashes_what_it_flushes() => WithEpochAsync(null, async () =>
    {
        // Large enough that the writer hands the output on several times before the end.
        var inputs = new[] { Source("one", 16000), Source("two", 16000) };
        var options = new PdfMergeOptions { CreationDate = Fixed };
        var inMemory = PdfMerger.Merge(inputs, options);
        Assert.True(inMemory.Length > 3 << 19, $"only {inMemory.Length} bytes");

        var streamed = new MemoryStream();
        PdfMerger.Merge(inputs.Select(i => (Stream)new MemoryStream(i)), streamed, options);
        Assert.Equal(inMemory, streamed.ToArray());

        var incremental = new MemoryStream();
        var merger = new StreamingPdfMerger(incremental, options);
        foreach (var input in inputs) merger.Add(input);
        merger.Complete();
        Assert.Equal(inMemory, incremental.ToArray());

        var asynchronous = new MemoryStream();
        await PdfMerger.MergeAsync(inputs.Select(i => (Stream)new MemoryStream(i)), asynchronous, options);
        Assert.Equal(inMemory, asynchronous.ToArray());

        // The identifier is the hash of everything before the trailer, as for a conversion.
        int trailer = Text(inMemory).LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexString(ReproducibleDate.FileId(inMemory.AsSpan(0, trailer))), Id(inMemory), ignoreCase: true);
    });

    [Fact]
    public void Page_tools_with_a_fixed_date_are_reproducible() => WithEpoch(null, () =>
    {
        var pdf = PdfMerger.Merge(Source("one"), Source("two"), Source("three"));
        var options = new PdfPagesOptions { CreationDate = Fixed };
        var extracted = PdfPages.Extract(pdf, "1,3", options);
        Assert.Equal(extracted, PdfPages.Extract(pdf, "1,3", options));
        Assert.Equal("D:20240102010405Z", ModDate(extracted));
        Assert.NotEqual(Id(extracted), Id(PdfPages.Extract(pdf, "2,3", options)));
        Assert.Equal(PdfPages.Remove(pdf, "2", options), PdfPages.Remove(pdf, "2", options));
        Assert.Equal(PdfPages.Rotate(pdf, 90, "1", options), PdfPages.Rotate(pdf, 90, "1", options));
        var parts = PdfPages.Split(pdf, 1, options);
        Assert.Equal(parts, PdfPages.Split(pdf, 1, options));
        Assert.Equal(3, parts.Select(Id).Distinct().Count());

        var streamed = new MemoryStream();
        PdfPages.Extract(new MemoryStream(pdf), streamed, PdfPageRange.Parse("1,3"), options);
        Assert.Equal(extracted, streamed.ToArray());

        Assert.NotEqual(Id(PdfPages.Extract(pdf, "1,3")), Id(PdfPages.Extract(pdf, "1,3")));
    });

    [Fact]
    public void Stamp_with_a_fixed_date_is_reproducible() => WithEpoch(null, () =>
    {
        var pdf = Source("document");
        var options = new PdfStampOptions { CreationDate = Fixed };
        var a = PdfStamper.Stamp(pdf, Source("PAID"), options);
        Assert.Equal(a, PdfStamper.Stamp(pdf, Source("PAID"), options));
        // The document keeps its permanent identifier; the version's follows the content.
        Assert.Equal(FirstId(pdf), FirstId(a), ignoreCase: true);
        Assert.NotEqual(SecondId(pdf).ToUpperInvariant(), SecondId(a).ToUpperInvariant());
        Assert.NotEqual(SecondId(a), SecondId(PdfStamper.Stamp(pdf, Source("COPY"), options)));

        Assert.NotEqual(SecondId(PdfStamper.Stamp(pdf, Source("PAID"))), SecondId(PdfStamper.Stamp(pdf, Source("PAID"))));
        // Encryption keys are random, so an encrypted result is never reproducible.
        var secured = new PdfStampOptions { CreationDate = Fixed, Security = new PdfSecurity { OwnerPassword = "owner" } };
        Assert.NotEqual(PdfStamper.Stamp(pdf, Source("PAID"), secured), PdfStamper.Stamp(pdf, Source("PAID"), secured));
    });

    [Fact]
    public void Source_date_epoch_reaches_merge_stamp_and_rewrite() => WithEpoch("1700000000", () =>
    {
        var merged = PdfMerger.Merge(Source("one"), Source("two"));
        Assert.Equal(merged, PdfMerger.Merge(Source("one"), Source("two")));
        Assert.Equal("D:20231114221320Z", CreationDate(merged));
        // The option takes precedence over the environment.
        Assert.Equal("D:20240102010405Z", CreationDate(PdfMerger.Merge(new[] { Source("one") }, new PdfMergeOptions { CreationDate = Fixed })));

        Assert.Equal(PdfStamper.Stamp(merged, Source("PAID")), PdfStamper.Stamp(merged, Source("PAID")));
        Assert.Equal(PdfPages.Extract(merged, "2"), PdfPages.Extract(merged, "2"));

        // Decrypting writes the file out again with a new version identifier.
        var encrypted = PdfProtector.Encrypt(merged, new PdfSecurity { UserPassword = "user" });
        var decrypted = PdfProtector.Decrypt(encrypted, "user");
        Assert.Equal(decrypted, PdfProtector.Decrypt(encrypted, "user"));
    });

    [Fact]
    public void Form_fill_with_a_fixed_date_is_reproducible() => WithEpoch(null, () =>
    {
        var pdf = HtmlToPdf.Convert("<p><input type=\"text\" name=\"name\" style=\"width:200px\"></p>", new PdfOptions { CreationDate = Fixed });
        byte[] Fill(string value, DateTimeOffset? date)
            => PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = value }, new PdfFormFillOptions { CreationDate = date });
        var a = Fill("Jane", Fixed);
        Assert.Equal(a, Fill("Jane", Fixed));
        Assert.NotEqual(SecondId(a), SecondId(Fill("John", Fixed)));
        Assert.NotEqual(SecondId(Fill("Jane", null)), SecondId(Fill("Jane", null)));
    });

    [Fact]
    public void Update_of_a_file_without_identifier_is_reproducible()
    {
        // A minimal file with no /ID: the incremental update has to add one.
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        var pdf = System.Text.Encoding.Latin1.GetBytes(sb.ToString());

        byte[] Attach(string content) => PdfAttachments.Add(pdf, new PdfAttachment("a.txt", System.Text.Encoding.ASCII.GetBytes(content)));
        WithEpoch("1700000000", () =>
        {
            var a = Attach("hello");
            Assert.Equal(a, Attach("hello"));
            // The attachment's modification date, D:20231114221320Z, as the hex string the update writes.
            Assert.Contains("/ModDate <443A32303233313131343232313332305A>", Text(a), StringComparison.Ordinal);
            Assert.Equal(FirstId(a), SecondId(a));
            Assert.NotEqual(FirstId(a), FirstId(Attach("other")));
        });
        WithEpoch(null, () => Assert.NotEqual(FirstId(Attach("hello")), FirstId(Attach("hello"))));

        // The writer itself, with the date given directly.
        WithEpoch(null, () =>
        {
            byte[] Update()
            {
                var file = PdfPecker.Rendering.Parsing.PdfFile.Open(pdf, null);
                var update = new PdfUpdateWriter(file, pdf, 0, Fixed);
                update.Finish(new PdfPecker.Rendering.Parsing.PdfRef(1, 0), 4);
                return update.Output.Span.ToArray();
            }
            Assert.Equal(Update(), Update());
            Assert.Equal(Convert.ToHexString(ReproducibleDate.FileId(pdf)), FirstId(Update()), ignoreCase: true);
        });
    }

    /// <summary>Runs <paramref name="action"/> with the environment variable set (or removed, for null), then restores it.</summary>
    private static async Task WithEpochAsync(string? value, Func<Task> action)
    {
        var saved = Environment.GetEnvironmentVariable(ReproducibleDate.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, value);
        try { await action(); }
        finally { Environment.SetEnvironmentVariable(ReproducibleDate.EnvironmentVariable, saved); }
    }

    [Theory]
    [InlineData("0", "1970-01-01T00:00:00")]
    [InlineData(" 1700000000 ", "2023-11-14T22:13:20")]
    [InlineData("253402300799", "9999-12-31T23:59:59")]
    [InlineData("253402300800", null)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_source_date_epoch(string? value, string? expected)
    {
        var parsed = ReproducibleDate.ParseEpoch(value);
        Assert.Equal(expected, parsed?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        if (parsed != null) Assert.Equal(DateTimeKind.Utc, parsed.Value.Kind);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReproducibleOutputCollection
{
    public const string Name = "Reproducible output";
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PdfPecker.Tests;

/// <summary>
/// Runs well-formed documents through the library with <see cref="SwallowedErrors"/> listening, and fails when any of
/// the catch blocks meant for damaged input had to swallow an <see cref="IndexOutOfRangeException"/>, a
/// <see cref="NullReferenceException"/> or an <see cref="InvalidCastException"/>. On damaged input those catches keep
/// the renderer going; on these inputs they could only be hiding a bug.
/// </summary>
/// <remarks>
/// The sink is an <see cref="AsyncLocal{T}"/>, so these tests run in parallel with the rest of the suite and see only
/// their own work. Hostile-input tests never set it. A font or image that another test parsed first may already sit in a
/// process-wide cache, so this is a net with some holes rather than a guarantee.
/// </remarks>
public class SwallowedErrorTests
{
    private static readonly string Fixtures = Path.Combine(SourceDirectory(), "Fixtures");

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    // Low enough to keep the suite quick; every operator still runs.
    private const float Dpi = 36;

    public static TheoryData<string> GoldenSamples() => GoldenPdfTests.Samples();

    [Theory]
    [MemberData(nameof(GoldenSamples))]
    public void Golden_samples_convert_and_render_without_swallowed_errors(string sample)
    {
        AssertNoneSwallowed(() =>
        {
            var pdf = GoldenPdfTests.Render(Path.Combine(GoldenPdfTests.Root, sample));
            ReadBack(pdf);
        });
    }

    public static TheoryData<string> WellFormedPdfs()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(GoldenPdfTests.Root, "*.expected.pdf", SearchOption.AllDirectories)
                     .Select(p => Path.GetRelativePath(GoldenPdfTests.Root, p).Replace('\\', '/'))
                     .Select(p => "Golden/" + p)
                     .Concat(new[] { "Fixtures/features.pdf", "Fixtures/objstm-cairo.pdf" })
                     .Concat(Directory.GetFiles(Path.Combine(Fixtures, "third-party"), "*.pdf")
                         .Select(p => "Fixtures/third-party/" + Path.GetFileName(p)))
                     .OrderBy(p => p, StringComparer.Ordinal))
            data.Add(file);
        return data;
    }

    private static readonly HashSet<string> EncryptedPdfs = new(StringComparer.Ordinal)
    {
        "Golden/operations/encrypt.expected.pdf",
        "Golden/operations/protect.expected.pdf",
        "Fixtures/third-party/libreoffice-aes256.pdf",
        "Fixtures/third-party/libreoffice-rc4-128.pdf",
    };

    [Theory]
    [MemberData(nameof(WellFormedPdfs))]
    public void Well_formed_pdfs_render_without_swallowed_errors(string file)
    {
        var pdf = File.ReadAllBytes(Path.Combine(SourceDirectory(), file));
        // The encrypted files are opened with the user password that their generator and the golden tests set.
        string? password = EncryptedPdfs.Contains(file) ? "user" : null;
        AssertNoneSwallowed(() => ReadBack(pdf, password));
    }

    public static TheoryData<string> WellFormedFonts()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures, "fonts"))
                     .Select(Path.GetFileName)
                     .Where(name => name!.EndsWith(".ttf", StringComparison.Ordinal) || name.EndsWith(".otf", StringComparison.Ordinal)
                                    || name.EndsWith(".woff", StringComparison.Ordinal) || name.EndsWith(".woff2", StringComparison.Ordinal)
                                    || name.EndsWith(".ttc", StringComparison.Ordinal))
                     .Where(name => !name!.Contains("Hostile", StringComparison.Ordinal)) // damaged on purpose
                     .OrderBy(name => name, StringComparer.Ordinal))
            data.Add(file!);
        return data;
    }

    [Theory]
    [MemberData(nameof(WellFormedFonts))]
    public void Fixture_fonts_lay_out_embed_and_render_without_swallowed_errors(string font)
    {
        // Latin with kerning and ligatures, the scripts of the Noto subsets, and emoji for the colour fonts, at two
        // weights so that variable fonts are instanced.
        const string Text = "Office affine AVAWAY 0123 &#x645;&#x631;&#x62D;&#x628;&#x627; &#x5E9;&#x5DC;&#x5D5;&#x5DD; "
                            + "&#x928;&#x92E;&#x938;&#x94D;&#x924;&#x947; &#x0E2A;&#x0E27;&#x0E31;&#x0E2A;&#x0E14;&#x0E35; "
                            + "&#x4F60;&#x597D; &#x1F600;&#x1F44D;&#x1F3FD;";
        var html = $"<style>@font-face {{ font-family: F; src: url('{font}'); }} body {{ font-family: F; }}</style>"
                   + $"<p>{Text}</p><p style='font-weight: 700; font-variation-settings: \"wdth\" 80'>{Text}</p>";
        var options = new PdfOptions { AllowFileAccess = true, BaseDirectory = Path.Combine(Fixtures, "fonts"), EmbedStandardFonts = true };
        AssertNoneSwallowed(() => ReadBack(HtmlToPdf.Convert(html, options)));
    }

    public static TheoryData<string> WellFormedImages()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures, "images"))
                     .Select(Path.GetFileName)
                     .Where(name => !name!.EndsWith(".gz", StringComparison.Ordinal))
                     .Concat(new[] { "../cmyk.jpg", "../rgb.jpg", "../codecs/jpx-alpha.jp2", "../codecs/jpx-lossless.jp2", "../codecs/jpx-lossy.j2k" })
                     .OrderBy(name => name, StringComparer.Ordinal))
            data.Add(file!);
        return data;
    }

    [Theory]
    [MemberData(nameof(WellFormedImages))]
    public void Fixture_images_embed_and_render_without_swallowed_errors(string image)
    {
        var html = $"<img src='{image}' style='width: 200px'>";
        var options = new PdfOptions { AllowFileAccess = true, BaseDirectory = Path.Combine(Fixtures, "images"), EmbedStandardFonts = true };
        AssertNoneSwallowed(() => ReadBack(HtmlToPdf.Convert(html, options)));
    }

    [Fact]
    public async Task The_sink_sees_only_the_bug_types_and_only_in_its_own_context()
    {
        var seen = new ConcurrentQueue<Exception>();
        SwallowedErrors.Record(new IndexOutOfRangeException()); // no sink: nothing happens
        SwallowedErrors.Sink = seen.Enqueue;
        try
        {
            SwallowedErrors.Record(new FormatException());
            SwallowedErrors.Record(new IndexOutOfRangeException());
            await Task.Run(() => SwallowedErrors.Record(new InvalidCastException())); // inherits the sink
            var other = new Thread(() => { SwallowedErrors.Sink = null; SwallowedErrors.Record(new NullReferenceException()); });
            other.Start();
            other.Join();
        }
        finally
        {
            SwallowedErrors.Sink = null;
        }
        Assert.Equal(new[] { typeof(IndexOutOfRangeException), typeof(InvalidCastException) }, seen.Select(e => e.GetType()));
    }

    // Renders every page to an image and reads the text, the images and the HTML back, which exercises the PDF parser,
    // the renderer, its font programs and the image decoders on the PDF the library wrote.
    private static void ReadBack(byte[] pdf, string? password = null)
    {
        var options = new ImageOptions { Dpi = Dpi, MaxDegreeOfParallelism = 1, Password = password };
        PdfToImage.Convert(pdf, options);
        PdfText.Extract(pdf, new PdfTextOptions { Password = password });
        PdfToHtml.Convert(pdf, new PdfHtmlOptions { Password = password });
        PdfImages.Extract(pdf, new PdfImagesOptions { Password = password });
    }

    private static void AssertNoneSwallowed(Action work)
    {
        var seen = new ConcurrentQueue<Exception>();
        SwallowedErrors.Sink = seen.Enqueue;
        try
        {
            work();
        }
        finally
        {
            SwallowedErrors.Sink = null;
        }
        if (seen.IsEmpty) return;
        var report = string.Join("\n\n", seen.Take(5).Select(e => e.GetType().FullName + ": " + e.Message + "\n" + e.StackTrace));
        Assert.Fail($"{seen.Count} swallowed exception(s) on well-formed input; the first:\n{report}");
    }
}

namespace PdfPecker.Tests;

public class FileOutputTests
{
    private const string TwoPages = "<p>one</p><p style='page-break-before:always'>two</p>";

    [Fact]
    public void PdfPeckerException_catches_each_library_exception()
    {
        var plain = HtmlToPdf.Convert("<p>x</p>");
        var encrypted = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "open" } });

        Assert.IsType<PdfLimitException>(Caught(() => HtmlToPdf.Convert(TwoPages, new PdfOptions { MaxPages = 1 })));
        Assert.IsType<PdfPasswordException>(Caught(() => PdfForm.GetFields(encrypted)));
        Assert.IsType<PdfSignatureException>(Caught(() => PdfSigner.AddValidationData(plain)));
    }

    private static PdfPeckerException? Caught(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (PdfPeckerException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void A_failed_write_keeps_the_old_file_and_leaves_no_temporary_file()
    {
        WithFolder(dir =>
        {
            var output = Path.Combine(dir.FullName, "out.pdf");
            File.WriteAllText(output, "old");
            Assert.Throws<IOException>(() => AtomicFile.Write(output, stream =>
            {
                stream.Write(new byte[1000]);
                throw new IOException("Disk full.");
            }));
            Assert.Equal("old", File.ReadAllText(output));
            Assert.Equal(new[] { "out.pdf" }, Names(dir));
        });
    }

    [Fact]
    public void File_methods_that_fail_keep_the_old_output_file()
    {
        WithFolder(dir =>
        {
            var html = Path.Combine(dir.FullName, "in.html");
            var input = Path.Combine(dir.FullName, "in.pdf");
            var bad = Path.Combine(dir.FullName, "bad.pdf");
            var output = Path.Combine(dir.FullName, "out.pdf");
            File.WriteAllText(html, TwoPages);
            File.WriteAllBytes(input, HtmlToPdf.Convert(TwoPages));
            File.WriteAllText(bad, "not a pdf");
            File.WriteAllText(output, "old");

            Assert.Throws<PdfLimitException>(() => HtmlToPdf.ConvertFile(html, output, new PdfOptions { MaxPages = 1 }));
            Assert.Throws<InvalidDataException>(() => PdfStamper.StampFile(input, bad, output));
            Assert.ThrowsAny<OperationCanceledException>(() => PdfMerger.MergeFiles(output, new[] { input, input }, cancellationToken: new CancellationToken(true)));
            Assert.ThrowsAny<OperationCanceledException>(() => PdfPages.ExtractFile(input, output, "1", cancellationToken: new CancellationToken(true)));

            Assert.Equal("old", File.ReadAllText(output));
            Assert.Equal(new[] { "bad.pdf", "in.html", "in.pdf", "out.pdf" }, Names(dir));
        });
    }

    [Fact]
    public void File_methods_replace_an_existing_output_file()
    {
        WithFolder(dir =>
        {
            var html = Path.Combine(dir.FullName, "in.html");
            var pdf = Path.Combine(dir.FullName, "out.pdf");
            var tiff = Path.Combine(dir.FullName, "out.tif");
            File.WriteAllText(html, TwoPages);
            File.WriteAllText(pdf, "old");
            File.WriteAllText(tiff, "old");

            HtmlToPdf.ConvertFile(html, pdf);
            PdfToImage.ConvertFileToTiff(pdf, tiff, new ImageOptions { Dpi = 18 });

            Assert.Equal(2, PdfRenderer.OpenFile(pdf).PageCount);
            Assert.Equal((byte)'I', File.ReadAllBytes(tiff)[0]);
            Assert.Equal(new[] { "in.html", "out.pdf", "out.tif" }, Names(dir));
        });
    }

    private static string[] Names(DirectoryInfo dir) => dir.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();

    private static void WithFolder(Action<DirectoryInfo> test)
    {
        var dir = Directory.CreateTempSubdirectory("pdfpecker-files");
        try
        {
            test(dir);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

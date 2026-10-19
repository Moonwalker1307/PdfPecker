using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Making existing PDFs smaller with <see cref="PdfCompactor"/>.</summary>
public class CompactorTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private const string Html = "<html lang=\"en\"><head><title>Compact sample</title></head><body style=\"font-family:sans-serif\">"
        + "<h1>Chapter one</h1><p>First page text.</p><p>Name <input type=\"text\" name=\"name\" value=\"Jane Doe\"></p>"
        + "<h1 style=\"page-break-before:always\">Chapter two</h1><p>Second page with a <a href=\"https://example.com\">link</a>.</p></body></html>";

    private static readonly Lazy<byte[]> Source = new(() => HtmlToPdf.Convert(Html, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 }));

    /// <summary>A photo-like gradient with noise, which neither Flate nor a predictor shrinks much.</summary>
    private static byte[] Photo(int size, bool gray = false)
    {
        var random = new Random(7);
        return PdfTestHelpers.PixelPng(size, size, (x, y) =>
        {
            int noise = random.Next(24);
            byte r = (byte)Math.Min(255, x * 255 / size + noise), g = (byte)Math.Min(255, y * 255 / size + noise), b = (byte)(128 + noise);
            return gray ? (r, r, r) : (r, g, b);
        });
    }

    private static uint[][] Render(byte[] pdf, int dpi = 36)
    {
        var r = PdfRenderer.Open(pdf);
        return Enumerable.Range(0, r.PageCount).Select(i => r.RenderPage(i, new ImageOptions { Dpi = dpi }).Premultiplied).ToArray();
    }

    private static void AssertSameRendering(byte[] expected, byte[] actual)
    {
        var a = Render(expected);
        var b = Render(actual);
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(a[i].AsSpan().SequenceEqual(b[i]), $"page {i + 1} renders differently");
    }

    /// <summary>The image XObjects of every page, as parsed.</summary>
    private static List<PdfStream> Images(byte[] pdf)
    {
        var file = PdfFile.Open(pdf, null);
        var images = new List<PdfStream>();
        var stack = new Stack<PdfDict>();
        stack.Push(file.GetDict(file.Catalog, "Pages")!);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (file.GetArray(node, "Kids") is { } kids)
            {
                foreach (var k in kids.Items) stack.Push((PdfDict)file.Resolve(k)!);
                continue;
            }
            if (file.GetDict(file.GetDict(node, "Resources"), "XObject") is { } xobjects)
                foreach (var x in xobjects.Entries.Values)
                    if (file.Resolve(x) is PdfStream s && file.GetName(s, "Subtype") == "Image") images.Add(s);
        }
        return images;
    }

    [Fact]
    public void Keeps_pages_bookmarks_fields_tags_and_metadata_and_renders_the_same()
    {
        var source = Source.Value;
        var compact = PdfCompactor.Compact(source);
        Assert.True(compact.Length < source.Length, $"{compact.Length} >= {source.Length}");
        Assert.StartsWith("%PDF-1.7\n", PdfTestHelpers.Latin1(compact));
        AssertSameRendering(source, compact);

        var file = PdfFile.Open(compact, null);
        var catalog = file.Catalog!;
        Assert.Equal(2, file.GetInt(file.GetDict(catalog, "Pages"), "Count", 0));
        var titles = new List<string>();
        for (var item = file.GetDict(file.GetDict(catalog, "Outlines"), "First"); item != null && titles.Count < 10; item = file.GetDict(item, "Next"))
            titles.Add(((PdfString)file.Resolve(item["Title"])!).Text);
        Assert.Equal(new[] { "Chapter one", "Chapter two" }, titles);
        Assert.Equal("Jane Doe", PdfForm.GetFields(compact).Single(f => f.Name == "name").Value);
        Assert.NotNull(file.GetDict(catalog, "StructTreeRoot"));
        Assert.Equal("Compact sample", ((PdfString)file.Resolve(file.GetDict(file.Trailer, "Info")!["Title"])!).Text);
        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", Encoding.UTF8.GetString(file.DecodeStream((PdfStream)file.Resolve(catalog["Metadata"])!)));
        Assert.Equal(PdfText.Extract(source), PdfText.Extract(compact));

        // Objects live in compressed object streams, listed by a cross-reference stream.
        var text = PdfTestHelpers.Latin1(compact);
        Assert.Contains("/Type/ObjStm", text);
        Assert.Contains("/Type/XRef", text);
        Assert.DoesNotContain("\ntrailer", text);
    }

    [Fact]
    public void Is_reproducible_and_does_not_grow_a_compacted_file()
    {
        var once = PdfCompactor.Compact(Source.Value);
        Assert.Equal(once, PdfCompactor.Compact(Source.Value));
        var twice = PdfCompactor.Compact(once);
        Assert.True(twice.Length <= once.Length);
        AssertSameRendering(once, twice);
    }

    [Fact]
    public void Writes_identical_fonts_and_images_once()
    {
        var logo = PdfTestHelpers.DataUri(Photo(64));
        var page = HtmlToPdf.Convert($"<p style=\"font-family:serif\">Invoice</p><img src=\"{logo}\" width=\"64\">");
        var merged = PdfMerger.Merge(Enumerable.Repeat(page, 20).ToArray());
        var compact = PdfCompactor.Compact(merged);
        // The merger already writes identical streams once; the font and image dictionaries, resources and object syntax
        // are what is left, so 20 copies come out not much larger than one.
        int single = PdfCompactor.Compact(page).Length;
        Assert.True(compact.Length < single * 3 / 2, $"{compact.Length} for 20 copies of {single}");
        AssertSameRendering(merged, compact);

        var file = PdfFile.Open(compact, null);
        var images = Images(compact);
        Assert.Equal(20, images.Count);
        Assert.Single(images.Distinct(ReferenceEqualityComparer.Instance));
        Assert.Equal(20, PdfPages.Count(compact));
    }

    [Fact]
    public void Drops_earlier_revisions_and_unused_objects()
    {
        // Filling a form appends an incremental update, which keeps the old values in the file.
        var filled = PdfForm.Fill(Source.Value, new Dictionary<string, string> { ["name"] = "A much longer replacement name" });
        Assert.True(filled.Length > Source.Value.Length);
        var compact = PdfCompactor.Compact(filled);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(PdfTestHelpers.Latin1(compact), "%%EOF"));
        Assert.Equal("A much longer replacement name", PdfForm.GetFields(compact).Single(f => f.Name == "name").Value);
        AssertSameRendering(filled, compact);
    }

    [Fact]
    public void Recompresses_uncompressed_streams()
    {
        var uncompressed = PdfTestHelpers.ConvertUncompressed("<h1>Plain</h1>" + string.Concat(Enumerable.Repeat("<p>Some repeated paragraph text.</p>", 200)));
        var compact = PdfCompactor.Compact(uncompressed);
        Assert.True(compact.Length * 4 < uncompressed.Length, $"{compact.Length} from {uncompressed.Length}");
        AssertSameRendering(uncompressed, compact);
    }

    [Fact]
    public void Keeps_pdfa_metadata_and_writes_a_classic_table_without_object_streams()
    {
        var pdfa = HtmlToPdf.Convert("<p>Archive</p>", new PdfOptions { Conformance = PdfConformance.PdfA2b, Title = "Archive" });
        var compact = PdfCompactor.Compact(pdfa, new PdfCompactOptions { RemoveMetadata = true });
        var file = PdfFile.Open(compact, null);
        var meta = (PdfStream)file.Resolve(file.Catalog!["Metadata"])!;
        Assert.Null(meta["Filter"]); // XMP stays readable without decoding
        Assert.Contains("pdfaid:part", Encoding.UTF8.GetString(meta.RawData));
        Assert.NotNull(file.GetDict(file.Trailer, "Info"));
        Assert.NotNull(file.Catalog!["OutputIntents"]);
        AssertSameRendering(pdfa, compact);

        var classic = PdfCompactor.Compact(pdfa, new PdfCompactOptions { UseObjectStreams = false });
        PdfTestHelpers.AssertStructurallyValid(classic);
        Assert.DoesNotContain("/ObjStm", PdfTestHelpers.Latin1(classic));
        AssertSameRendering(pdfa, classic);
    }

    [Fact]
    public void Removes_metadata_when_asked()
    {
        var pdf = HtmlToPdf.Convert("<html><head><title>Secret title</title></head><body><p>Text</p></body></html>");
        var compact = PdfCompactor.Compact(pdf, new PdfCompactOptions { RemoveMetadata = true });
        var file = PdfFile.Open(compact, null);
        Assert.Null(file.Trailer["Info"]);
        Assert.Null(file.Catalog!["Metadata"]);
        Assert.DoesNotContain("Secret title", PdfTestHelpers.Latin1(compact));
        AssertSameRendering(pdf, compact);
    }

    [Fact]
    public void Downsamples_images_drawn_above_the_resolution()
    {
        // 600 pixels drawn 2 inches wide is 300 pixels per inch.
        var pdf = HtmlToPdf.Convert($"<img src=\"{PdfTestHelpers.DataUri(Photo(600))}\" style=\"width:2in;height:2in\">");
        var lossless = PdfCompactor.Compact(pdf);
        Assert.Equal(600, PdfFile.Open(lossless, null).GetInt(Images(lossless)[0], "Width", 0));

        var compact = PdfCompactor.Compact(pdf, new PdfCompactOptions { MaxImageDpi = 100 });
        var image = Images(compact)[0];
        var file = PdfFile.Open(compact, null);
        Assert.Equal(200, file.GetInt(image, "Width", 0));
        Assert.Equal(200, file.GetInt(image, "Height", 0));
        Assert.Equal("FlateDecode", file.GetName(image, "Filter")); // a lossless image stays lossless
        Assert.True(compact.Length * 4 < lossless.Length, $"{compact.Length} from {lossless.Length}");
        AssertImagesClose(pdf, compact);
    }

    [Fact]
    public void Recompresses_images_as_jpeg_at_the_quality()
    {
        // A greyscale JPEG is embedded as it is, with DeviceGray; the RGB image is a lossless PNG.
        var random = new Random(3);
        var grayPixels = Enumerable.Range(0, 300 * 300).Select(i => 0xFF000000u | (uint)(i % 300 * 200 / 300 + random.Next(40)) * 0x010101u).ToArray();
        var grayJpeg = PdfPecker.Rendering.JpegEncoder.EncodeGray(grayPixels, 300, 300, 98);
        var pdf = HtmlToPdf.Convert($"<img src=\"{PdfTestHelpers.DataUri(Photo(300))}\" width=\"300\">"
            + $"<img src=\"data:image/jpeg;base64,{Convert.ToBase64String(grayJpeg)}\" width=\"300\">");
        var compact = PdfCompactor.Compact(pdf, new PdfCompactOptions { ImageQuality = 70 });
        Assert.True(compact.Length * 2 < pdf.Length, $"{compact.Length} from {pdf.Length}");
        var file = PdfFile.Open(compact, null);
        var images = Images(compact);
        Assert.All(images, i => Assert.Equal("DCTDecode", file.GetName(i, "Filter")));
        // The grey image keeps its one-component colour space, with a one-component JPEG.
        var gray = images.Single(i => file.GetName(i, "ColorSpace") == "DeviceGray");
        Assert.Equal(1, gray.RawData[Array.IndexOf(gray.RawData, (byte)0xC0, 2) + 8]);
        Assert.True(gray.RawData.Length < grayJpeg.Length);
        AssertImagesClose(pdf, compact);
    }

    [Fact]
    public void Keeps_jpegs_and_masks_exact_without_image_settings()
    {
        var jpeg = "data:image/jpeg;base64," + Convert.ToBase64String(Fixture("rgb.jpg"));
        var pdf = HtmlToPdf.Convert($"<img src=\"{jpeg}\"><img src=\"{PdfTestHelpers.DataUri(PdfTestHelpers.RgbaPng(32))}\">");
        var compact = PdfCompactor.Compact(pdf);
        var original = PdfFile.Open(pdf, null);
        var file = PdfFile.Open(compact, null);
        var before = Images(pdf).Single(i => original.GetName(i, "Filter") == "DCTDecode");
        // The JPEG data is the same, though it may now be wrapped in Flate when that is smaller.
        var after = Images(compact).Single(i => file.GetInt(i, "Width", 0) == original.GetInt(before, "Width", 0));
        Assert.Equal(before.RawData, file.DecodeStream(after, stopAtImageFilter: true, out var codec, out _));
        Assert.Equal("DCTDecode", codec);
        Assert.Contains(Images(compact), i => i["SMask"] != null);
        AssertSameRendering(pdf, compact);
    }

    /// <summary>Lossy results render within a small average difference of the original.</summary>
    private static void AssertImagesClose(byte[] expected, byte[] actual)
    {
        var a = Render(expected, 72);
        var b = Render(actual, 72);
        for (int p = 0; p < a.Length; p++)
        {
            Assert.Equal(a[p].Length, b[p].Length);
            long total = 0;
            for (int i = 0; i < a[p].Length; i++)
                for (int shift = 0; shift < 24; shift += 8)
                    total += Math.Abs((int)((a[p][i] >> shift) & 0xFF) - (int)((b[p][i] >> shift) & 0xFF));
            double mean = total / (3.0 * a[p].Length);
            Assert.True(mean < 6, $"page {p + 1} differs by {mean:0.00} on average");
        }
    }

    [Fact]
    public void Refuses_signed_and_encrypted_pdfs_and_bad_options()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var signed = PdfSigner.Sign(Source.Value, new PdfSignatureOptions(cert));
        Assert.Contains("signed", Assert.Throws<ArgumentException>(() => PdfCompactor.Compact(signed)).Message);

        var encrypted = PdfProtector.Encrypt(Source.Value, new PdfSecurity { UserPassword = "open", OwnerPassword = "admin" });
        Assert.Contains("encrypted", Assert.Throws<ArgumentException>(() => PdfCompactor.Compact(encrypted)).Message);
        var restricted = PdfProtector.Encrypt(Source.Value, new PdfSecurity { OwnerPassword = "admin", Permissions = PdfPermissions.Print | PdfPermissions.ExtractForAccessibility });
        Assert.Contains("encrypted", Assert.Throws<ArgumentException>(() => PdfCompactor.Compact(restricted)).Message);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCompactor.Compact(Source.Value, new PdfCompactOptions { ImageQuality = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCompactor.Compact(Source.Value, new PdfCompactOptions { MaxImageDpi = 0 }));
        Assert.Throws<InvalidDataException>(() => PdfCompactor.Compact(Encoding.ASCII.GetBytes("not a pdf at all")));
        Assert.Throws<ArgumentNullException>(() => PdfCompactor.Compact((byte[])null!));
    }

    [Fact]
    public void Compacts_third_party_files_and_streams()
    {
        foreach (var name in new[] { "features.pdf", "objstm-cairo.pdf" })
        {
            var pdf = Fixture(name);
            var compact = PdfCompactor.Compact(pdf);
            Assert.True(compact.Length <= pdf.Length);
            AssertSameRendering(pdf, compact);
        }

        using var input = new MemoryStream(Source.Value);
        using var output = new MemoryStream();
        PdfCompactor.Compact(input, output);
        Assert.Equal(PdfCompactor.Compact(Source.Value), output.ToArray());
    }

    [Fact]
    public void Stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PdfCompactor.Compact(Source.Value, null, cts.Token));
    }
}

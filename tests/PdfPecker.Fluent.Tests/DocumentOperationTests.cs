using PdfPecker.Fluent.Operations;
using static PdfPecker.Fluent.DocumentOperation;

namespace PdfPecker.Fluent.Tests;

/// <summary>QuestPDF's DocumentOperation: page selection, merging, layers, attachments, encryption and linearization.</summary>
public sealed class DocumentOperationTests
{
    private static readonly DateTimeOffset Fixed = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>A PDF whose pages say "{prefix}1", "{prefix}2", ...</summary>
    private static byte[] Pages(string prefix, int count, PageSize? size = null, float top = 0) => Document.Create(d =>
    {
        for (int i = 1; i <= count; i++)
        {
            string text = prefix + i;
            d.Page(p =>
            {
                p.Size(size ?? PageSizes.A4);
                p.Content().PaddingTop(top).Text(text);
            });
        }
    }).GeneratePdf(new PdfOptions { CreationDate = Fixed });

    /// <summary>A layer whose text sits lower on the page than the document's, so text extraction keeps them apart.</summary>
    private static byte[] Layer(string prefix, int count, PageSize? size = null) => Pages(prefix, count, size, 200);

    /// <summary>A one-page PDF with a square of <paramref name="color"/> in the top left corner of the content area.</summary>
    private static byte[] Square(string color, float size) => Document.Create(d => d.Page(p =>
    {
        p.Size(PageSizes.A4);
        p.Margin(0);
        p.Content().Width(size).Height(size).Background(color);
    })).GeneratePdf(new PdfOptions { CreationDate = Fixed });

    /// <summary>The RGB colour at a point of the first page, from an uncompressed TIFF rendering at 72 dpi.</summary>
    private static int Pixel(byte[] pdf, int x, int y)
    {
        var tiff = PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 72, Format = PdfImageFormat.Tiff, TiffCompression = TiffCompression.None });
        // Little-endian TIFF: the first directory's width, samples, rows per strip and strip offsets.
        int U16(int at) => tiff[at] | tiff[at + 1] << 8;
        int U32(int at) => U16(at) | U16(at + 2) << 16;
        int ifd = U32(4), width = 0, samples = 3, rowsPerStrip = int.MaxValue, offsets = 0, type = 0;
        for (int i = 0, n = U16(ifd); i < n; i++)
        {
            int e = ifd + 2 + 12 * i, tag = U16(e);
            int value = U16(e + 2) == 3 ? U16(e + 8) : U32(e + 8);
            if (tag == 256) width = value;
            else if (tag == 277) samples = value;
            else if (tag == 278) rowsPerStrip = value;
            else if (tag == 273) (offsets, type) = (U32(e + 4) == 1 ? e + 8 : value, U16(e + 2));
        }
        int strip = y / rowsPerStrip;
        int start = type == 3 ? U16(offsets + 2 * strip) : U32(offsets + 4 * strip);
        int at = start + ((y % rowsPerStrip) * width + x) * samples;
        return tiff[at] << 16 | tiff[at + 1] << 8 | tiff[at + 2];
    }

    private static List<string> Texts(byte[] pdf, string? password = null)
        => PdfText.ExtractPages(pdf, new PdfTextOptions { Password = password }).Select(p => string.Join(" ", p.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToList();

    [Theory]
    [InlineData("1,6,4", 15, "1,6,4")]
    [InlineData("3-7", 15, "3,4,5,6,7")]
    [InlineData("7-3", 15, "7,6,5,4,3")]
    [InlineData("1-z", 3, "1,2,3")]
    [InlineData("z-1", 3, "3,2,1")]
    [InlineData("r3-r1", 15, "13,14,15")]
    [InlineData("r1-r3", 15, "15,14,13")]
    [InlineData("1-20:even", 20, "2,4,6,8,10,12,14,16,18,20")]
    [InlineData("5,7-9,12", 15, "5,7,8,9,12")]
    [InlineData("5,7-9,12:odd", 15, "5,8,12")]
    [InlineData("5,7-9,12:even", 15, "7,9")]
    [InlineData("1-10,x3-4", 15, "1,2,5,6,7,8,9,10")]
    [InlineData("4-10,x7-9,12-8,xr5", 15, "4,5,6,10,12,10,9,8")]
    [InlineData(" 2 - 3 , z ", 4, "2,3,4")]
    [InlineData("1-3,x2,x3", 4, "1")]
    public void Page_selectors_follow_qpdf(string selector, int pages, string expected)
        => Assert.Equal(expected, string.Join(",", PageSelector.Resolve(selector, pages, "s")));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("1,,2")]
    [InlineData("x1")]
    [InlineData("1-")]
    [InlineData("a")]
    [InlineData("r")]
    [InlineData("1:all")]
    [InlineData("1-2-3")]
    [InlineData("99999999999")]
    public void Invalid_page_selectors_throw_at_the_call(string selector)
    {
        var operation = Load(Pages("P", 1));
        Assert.Throws<ArgumentException>(() => operation.TakePages(selector));
        Assert.Throws<ArgumentException>(() => operation.OverlayFile(new LayerConfiguration { Pdf = Pages("L", 1), TargetPages = selector }));
    }

    [Fact]
    public void Pages_past_the_end_throw_when_saving()
    {
        var operation = Load(Pages("P", 3)).TakePages("2-4");
        Assert.Throws<ArgumentOutOfRangeException>(() => operation.GeneratePdf());
        Assert.Throws<ArgumentOutOfRangeException>(() => Load(Pages("P", 3)).TakePages("r4").GeneratePdf());
        Assert.Throws<ArgumentException>(() => PageSelector.Resolve(string.Join(",", Enumerable.Repeat("1-z", 30)), 5000, "s"));
    }

    [Fact]
    public void Takes_and_merges_pages_in_order()
    {
        var a = Pages("A", 4);
        var b = Pages("B", 3);
        Assert.Equal(["A3", "A1", "B3", "B2", "B1", "A4"], Texts(Load(a).TakePages("3,1").Merge(b, "z-1").TakePages("z").GeneratePdf()));
        // Without TakePages, every page of the loaded PDF comes first.
        Assert.Equal(["A1", "A2", "A3", "A4", "B2"], Texts(Load(a).Merge(b, "2").GeneratePdf()));
        Assert.Equal(["A2", "A4"], Texts(Load(a).TakePages("1-z:even").GeneratePdf()));
    }

    [Fact]
    public void Layers_pair_target_pages_with_source_pages_then_repeat()
    {
        var document = Pages("P", 5);
        var layer = Layer("L", 2);
        var result = Load(document).OverlayFile(new LayerConfiguration { Pdf = layer, TargetPages = "2-z", SourcePages = "1", RepeatSourcePages = "2" }).GeneratePdf();
        Assert.Equal(["P1", "P2 L1", "P3 L2", "P4 L2", "P5 L2"], Texts(result));

        // By default the k-th page gets the k-th layer page, and pages past the layer's end get nothing.
        Assert.Equal(["P1 L1", "P2 L2", "P3", "P4", "P5"], Texts(Load(document).OverlayFile(new LayerConfiguration { Pdf = layer }).GeneratePdf()));

        // An empty SourcePages draws only the repeated pages.
        Assert.Equal(["P1 L2", "P2 L2", "P3 L2", "P4 L2", "P5 L2"],
            Texts(Load(document).OverlayFile(new LayerConfiguration { Pdf = layer, SourcePages = "", RepeatSourcePages = "z" }).GeneratePdf()));
    }

    [Fact]
    public void Many_different_layer_pages_go_on_in_one_stamp()
    {
        var document = Pages("P", 6);
        var result = Load(document).UnderlayFile(new LayerConfiguration { Pdf = Layer("U", 6) }).GeneratePdf();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"P{i} U{i}"), Texts(result));
    }

    [Fact]
    public void Underlays_go_below_the_page_and_overlays_above_in_call_order()
    {
        // Each square is drawn over the one before it, so at a point inside every square the last one drawn shows.
        var page = Square("#ffffff00", 10); // transparent, so the underlays show through
        var result = Load(page)
            .OverlayFile(new LayerConfiguration { Pdf = Square("#00ff00", 100) })
            .UnderlayFile(new LayerConfiguration { Pdf = Square("#ff0000", 200) })
            .UnderlayFile(new LayerConfiguration { Pdf = Square("#0000ff", 150) })
            .OverlayFile(new LayerConfiguration { Pdf = Square("#ffff00", 50) })
            .GeneratePdf();
        Assert.Equal(0xFFFF00, Pixel(result, 20, 20));  // the last overlay on top
        Assert.Equal(0x00FF00, Pixel(result, 75, 75));  // the first overlay below it
        Assert.Equal(0x0000FF, Pixel(result, 125, 125)); // the second underlay over the first
        Assert.Equal(0xFF0000, Pixel(result, 175, 175)); // the first underlay lowest
        Assert.Equal(0xFFFFFF, Pixel(result, 300, 300));
    }

    [Fact]
    public void A_larger_layer_page_is_shrunk_and_a_smaller_one_kept_at_its_size()
    {
        var document = Pages("P", 2, PageSizes.A5);
        var result = Load(document).OverlayFile(new LayerConfiguration { Pdf = Layer("L", 1, PageSizes.A4), RepeatSourcePages = "1" }).GeneratePdf();
        Assert.Equal(["P1 L1", "P2 L1"], Texts(result));
        var words = PdfText.ExtractPages(result)[0].Lines.SelectMany(l => l.Words).Where(w => w.Text == "L1").ToList();
        Assert.Single(words);
        // The A4 page is shrunk to the A5 page, so its text is smaller than the page's own.
        var own = PdfText.ExtractPages(result)[0].Lines.SelectMany(l => l.Words).Single(w => w.Text == "P1");
        Assert.True(words[0].Height < own.Height * 0.8, $"{words[0].Height} vs {own.Height}");

        var small = Load(Pages("P", 1)).OverlayFile(new LayerConfiguration { Pdf = Layer("S", 1, PageSizes.A5) }).GeneratePdf();
        var page = PdfText.ExtractPages(small)[0].Lines.SelectMany(l => l.Words).ToList();
        Assert.Equal(page.Single(w => w.Text == "P1").Height, page.Single(w => w.Text == "S1").Height, 0.5);
    }

    [Fact]
    public void Attachments_from_files_and_memory()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string csv = Path.Combine(dir, "data.csv");
            File.WriteAllText(csv, "a,b\n1,2\n");
            var result = Load(Pages("P", 1))
                .AddAttachment(new DocumentAttachment { FilePath = csv, Description = "The figures", Relationship = DocumentAttachmentRelationship.Data })
                .AddAttachment(new DocumentAttachment { Data = [1, 2, 3], AttachmentName = "x.bin" })
                .AddAttachment(new DocumentAttachment { Data = [4, 5], AttachmentName = "x.bin" })
                .GeneratePdf();
            var list = PdfAttachments.List(result).OrderBy(a => a.FileName, StringComparer.Ordinal).ToList();
            Assert.Equal(["data.csv", "x.bin"], list.Select(a => a.FileName));
            Assert.Equal("text/csv", list[0].MimeType);
            Assert.Equal("The figures", list[0].Description);
            Assert.Equal(PdfAttachmentRelationship.Data, list[0].Relationship);
            Assert.Equal(new byte[] { 4, 5 }, list[1].Data);

            Assert.Throws<ArgumentException>(() => Load(Pages("P", 1))
                .AddAttachment(new DocumentAttachment { Data = [1], AttachmentName = "x.bin" })
                .AddAttachment(new DocumentAttachment { Data = [2], AttachmentName = "x.bin", Replace = false }));
            Assert.Throws<ArgumentException>(() => Load(result).AddAttachment(new DocumentAttachment { Data = [1], AttachmentName = "x.bin" }).GeneratePdf());
            Assert.Throws<ArgumentException>(() => Load(result).AddAttachment(new DocumentAttachment()));
            Assert.Throws<ArgumentException>(() => Load(result).AddAttachment(new DocumentAttachment { Data = [1] }));
            Assert.Throws<FileNotFoundException>(() => Load(result).AddAttachment(new DocumentAttachment { FilePath = Path.Combine(dir, "missing.csv") }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Encrypts_with_aes_256_and_the_permissions_asked_for()
    {
        var result = Load(Pages("P", 2)).Encrypt(new Encryption128Bit
        {
            UserPassword = "open",
            OwnerPassword = "admin",
            AllowContentExtraction = false,
            AllowModification = false,
        }).GeneratePdf();
        Assert.True(PdfProtector.IsEncrypted(result));
        Assert.Throws<PdfPasswordException>(() => PdfText.Extract(result));
        Assert.Equal(["P1", "P2"], Texts(result, "open"));
        var properties = PdfDocumentProperties.Read(result, "open");
        Assert.True(properties.IsEncrypted);
        Assert.Contains("/R 6", PdfLatin1(result), StringComparison.Ordinal);

        Assert.Equal(PdfPermissions.Annotate | PdfPermissions.Assemble | PdfPermissions.FillForms | PdfPermissions.Print | PdfPermissions.PrintHighQuality,
            new Encryption256Bit { AllowContentExtraction = false, AllowModification = false }.Permissions);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.PrintHighQuality | PdfPermissions.Copy,
            new Encryption40Bit { AllowAnnotation = false, AllowModification = false }.Permissions);
        Assert.Throws<InvalidOperationException>(() => Load(Pages("P", 1)).Encrypt(new Encryption256Bit()).Encrypt(new Encryption40Bit()));
    }

    [Fact]
    public void Linearizes_with_or_without_encryption()
    {
        var plain = Load(Pages("P", 3)).Linearize().GeneratePdf();
        Assert.StartsWith("%PDF", PdfLatin1(plain));
        Assert.Contains("/Linearized 1", PdfLatin1(plain)[..200], StringComparison.Ordinal);

        var both = Load(Pages("P", 3)).Merge(Pages("Q", 1)).Linearize().Encrypt(new Encryption256Bit { UserPassword = "open" }).GeneratePdf();
        Assert.Contains("/Linearized 1", PdfLatin1(both)[..200], StringComparison.Ordinal);
        Assert.True(PdfProtector.IsEncrypted(both));
        Assert.Equal(["P1", "P2", "P3", "Q1"], Texts(both, "open"));
    }

    [Fact]
    public void An_encrypted_pdf_keeps_its_protection_unless_told_otherwise()
    {
        var encrypted = PdfProtector.Encrypt(Pages("P", 2), new PdfSecurity { UserPassword = "open" });
        Assert.Throws<InvalidOperationException>(() => Load(encrypted, "open").TakePages("2").GeneratePdf());
        Assert.Throws<InvalidOperationException>(() => Load(encrypted, "open").Linearize().GeneratePdf());

        var decrypted = Load(encrypted, "open").TakePages("2").Decrypt().GeneratePdf();
        Assert.False(PdfProtector.IsEncrypted(decrypted));
        Assert.Equal(["P2"], Texts(decrypted));
        Assert.False(PdfProtector.IsEncrypted(Load(encrypted, "open").Decrypt().GeneratePdf()));

        // Attaching a file is an incremental update, which keeps the encryption.
        var attached = Load(encrypted, "open").AddAttachment(new DocumentAttachment { Data = [1], AttachmentName = "a.txt" }).GeneratePdf();
        Assert.True(PdfProtector.IsEncrypted(attached));
        Assert.Single(PdfAttachments.List(attached, "open"));

        var reprotected = Load(encrypted, "open").TakePages("1").Encrypt(new Encryption256Bit { UserPassword = "new" }).GeneratePdf();
        Assert.Equal(["P1"], Texts(reprotected, "new"));
        Assert.Throws<PdfPasswordException>(() => Load(encrypted, "wrong").TakePages("1").Decrypt().GeneratePdf());
    }

    [Fact]
    public void Files_are_found_beside_the_application_and_saved()
    {
        string name = "operation-" + Guid.NewGuid().ToString("N") + ".pdf";
        string beside = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllBytes(beside, Pages("P", 2));
        try
        {
            // The same file is loaded, merged and overwritten.
            LoadFile(name).MergeFile(name, "1").Save(beside);
            Assert.Equal(["P1", "P2", "P1"], Texts(File.ReadAllBytes(beside)));
            using var stream = new MemoryStream();
            LoadFile(beside).TakePages("z").Save(stream);
            Assert.Equal(["P1"], Texts(stream.ToArray()));
        }
        finally
        {
            File.Delete(beside);
        }
        Assert.Throws<FileNotFoundException>(() => LoadFile("missing-" + name));
        Assert.Throws<ArgumentException>(() => LoadFile(""));
        Assert.Throws<ArgumentException>(() => Load(Pages("P", 1)).OverlayFile(new LayerConfiguration()));
        Assert.Throws<ArgumentException>(() => Load(Pages("P", 1)).OverlayFile(new LayerConfiguration { FilePath = "x.pdf", Pdf = [1] }));
    }

    [Fact]
    public void Output_is_reproducible_without_encryption()
    {
        byte[] Run() => Load(Pages("P", 3)).TakePages("3,1").OverlayFile(new LayerConfiguration { Pdf = Layer("L", 1), RepeatSourcePages = "1" })
            .AddAttachment(new DocumentAttachment { Data = [1], AttachmentName = "a.txt", ModificationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) })
            .Linearize().GeneratePdf();
        Environment.SetEnvironmentVariable("SOURCE_DATE_EPOCH", "1767225600");
        try
        {
            Assert.Equal(Run(), Run());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOURCE_DATE_EPOCH", null);
        }
    }

    private static string PdfLatin1(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);
}

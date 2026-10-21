using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>Stamping an existing PDF with a page of another, over or under its content.</summary>
public class StampTests
{
    private static readonly PdfOptions NoMargins = new() { Margins = new PageMargins(0), PageSize = PageSize.Letter };

    /// <summary>A Letter page painted one colour, with a black box at its top left.</summary>
    private static byte[] Background(string color, int pages = 1)
        => HtmlToPdf.Convert(
            "<style>@page{size:612pt 792pt;margin:0} body{margin:0} div.p{width:612pt;height:792pt;background:" + color + ";position:relative;page-break-after:always}"
            + " div.p:last-child{page-break-after:auto} b{position:absolute;left:36pt;top:36pt;width:72pt;height:36pt;background:#000}</style>"
            + string.Concat(Enumerable.Repeat("<div class=p><b></b></div>", pages)),
            NoMargins);

    /// <summary>A Letter page that is transparent except for a box of one colour at its bottom right.</summary>
    private static byte[] CornerStamp(params string[] colors)
        => HtmlToPdf.Convert(
            "<style>@page{size:612pt 792pt;margin:0} body{margin:0} div.p{width:612pt;height:792pt;position:relative;page-break-after:always}"
            + " div.p:last-child{page-break-after:auto} i{position:absolute;right:0;bottom:0;width:144pt;height:72pt}</style>"
            + string.Concat(colors.Select(c => "<div class=p><i style=\"background:" + c + "\"></i></div>")),
            NoMargins);

    private static (byte R, byte G, byte B) Pixel(byte[] pdf, int page, int x, int y)
    {
        var img = PdfRenderer.Open(pdf).RenderPage(page, new ImageOptions { Dpi = 72 });
        int i = (y * img.Width + x) * 4;
        return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]);
    }

    private static void AssertColor((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) < 24 && Math.Abs(expected.G - actual.G) < 24 && Math.Abs(expected.B - actual.B) < 24,
            $"expected {expected}, got {actual}");
    }

    private static readonly (byte, byte, byte) Red = (255, 0, 0), Green = (0, 128, 0), Blue = (0, 0, 255), Black = (0, 0, 0), White = (255, 255, 255);

    [Fact]
    public void An_overlay_is_drawn_on_top_of_the_page()
    {
        var result = PdfStamper.Stamp(Background("green"), CornerStamp("red"));
        PdfTestHelpers.AssertStructurallyValid(result);
        AssertColor(Red, Pixel(result, 0, 600, 780)); // the stamp's box
        AssertColor(Green, Pixel(result, 0, 300, 400)); // the stamp is transparent elsewhere
        AssertColor(Black, Pixel(result, 0, 50, 50)); // the page's own content
    }

    [Fact]
    public void An_underlay_is_drawn_under_the_page()
    {
        // Under an opaque page it is hidden; under a page without a background it shows.
        var hidden = PdfStamper.Stamp(Background("green"), CornerStamp("red"), new PdfStampOptions { Underlay = true });
        AssertColor(Green, Pixel(hidden, 0, 600, 780));
        var plain = HtmlToPdf.Convert("<p style=\"margin:0;font-size:40pt\">Letter text</p>", NoMargins);
        var shown = PdfStamper.Stamp(plain, Background("blue"), new PdfStampOptions { Underlay = true });
        AssertColor(Blue, Pixel(shown, 0, 300, 400));
        AssertColor(Black, Pixel(shown, 0, 50, 50));
        var content = Content(shown, 0);
        Assert.StartsWith("q\n", content);
        Assert.Contains(" Do\nQ\n", content);
    }

    [Fact]
    public void Only_the_chosen_pages_are_stamped()
    {
        var result = PdfStamper.Stamp(Background("green", 3), CornerStamp("red"), new PdfStampOptions { Pages = i => i == 1 });
        Assert.Equal(3, PdfToImage.GetPageCount(result));
        AssertColor(Green, Pixel(result, 0, 600, 780));
        AssertColor(Red, Pixel(result, 1, 600, 780));
        AssertColor(Green, Pixel(result, 2, 600, 780));
    }

    [Fact]
    public void Matching_pages_repeat_the_last_stamp_page()
    {
        var stamp = CornerStamp("red", "blue");
        var result = PdfStamper.Stamp(Background("green", 3), stamp, new PdfStampOptions { MatchPages = true });
        AssertColor(Red, Pixel(result, 0, 600, 780));
        AssertColor(Blue, Pixel(result, 1, 600, 780));
        AssertColor(Blue, Pixel(result, 2, 600, 780));
        // Each stamp page is copied once, however many pages use it.
        var file = PdfFile.Open(result, null);
        Assert.Equal(2, Enumerable.Range(0, 3).Select(i => StampRef(file, i).Num).Distinct().Count());

        var second = PdfStamper.Stamp(Background("green"), stamp, new PdfStampOptions { StampPage = 1 });
        AssertColor(Blue, Pixel(second, 0, 600, 780));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamper.Stamp(Background("green"), stamp, new PdfStampOptions { StampPage = 2 }));
    }

    [Fact]
    public void Position_scale_offset_and_opacity_are_applied()
    {
        var stamp = HtmlToPdf.Convert("<div style=\"width:100pt;height:50pt;background:red\"></div>",
            new PdfOptions { PageSize = new PageSize(100, 50), Margins = new PageMargins(0) });
        var result = PdfStamper.Stamp(Background("white"), stamp, new PdfStampOptions
        {
            Position = PdfStampPosition.TopRight, Scale = 1, OffsetX = -10, OffsetY = 20, Opacity = 0.5f,
        });
        // The box spans x 502..602, y 20..70 (from the top).
        AssertColor((255, 128, 128), Pixel(result, 0, 550, 45));
        AssertColor(White, Pixel(result, 0, 550, 15));
        AssertColor(White, Pixel(result, 0, 606, 45));
        AssertColor(White, Pixel(result, 0, 498, 45));
        Assert.Contains("/CA 0.5 /ca 0.5", PdfTestHelpers.Latin1(result));

        // Fitted (the default), the 2:1 box fills the page width, centred.
        var fitted = PdfStamper.Stamp(Background("white"), stamp);
        AssertColor(Red, Pixel(fitted, 0, 5, 396));
        AssertColor(White, Pixel(fitted, 0, 300, 50));
    }

    [Fact]
    public void A_rotated_page_with_an_offset_box_gets_the_stamp_upright_where_it_is_displayed()
    {
        var stamp = HtmlToPdf.Convert("<div style=\"width:100pt;height:50pt;background:red\"></div>",
            new PdfOptions { PageSize = new PageSize(100, 50), Margins = new PageMargins(0) });
        foreach (int rotate in new[] { 0, 90, 180, 270 })
        {
            // A 300 x 400 visible area at (100, 200), white, with the page turned.
            var page = RawPdf("/MediaBox [50 100 500 700] /CropBox [100 200 400 600] /Rotate " + rotate, "1 1 1 rg 0 0 1000 1000 re f");
            var result = PdfStamper.Stamp(page, stamp, new PdfStampOptions { Position = PdfStampPosition.TopLeft, Scale = 1 });
            PdfTestHelpers.AssertStructurallyValid(result);
            var img = PdfRenderer.Open(result).RenderPage(0, new ImageOptions { Dpi = 72 });
            Assert.Equal(rotate is 90 or 270 ? 400 : 300, img.Width);
            (byte, byte, byte) At(int x, int y) { int i = (y * img.Width + x) * 4; return (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]); }
            AssertColor(Red, At(5, 5));
            AssertColor(Red, At(95, 45)); // the box is 100 wide and 50 high as displayed: upright
            AssertColor(White, At(105, 5));
            AssertColor(White, At(5, 55));
        }
    }

    [Fact]
    public void A_rotated_stamp_page_is_placed_as_it_is_displayed()
    {
        // A 50 x 100 page turned 90 degrees clockwise displays 100 wide and 50 high, its bottom half on the left.
        var stamp = RawPdf("/MediaBox [0 0 50 100] /Rotate 90", "1 0 0 rg 0 0 50 50 re f");
        var result = PdfStamper.Stamp(Background("white"), stamp, new PdfStampOptions { Position = PdfStampPosition.BottomLeft, Scale = 1 });
        AssertColor(Red, Pixel(result, 0, 5, 788));
        AssertColor(Red, Pixel(result, 0, 45, 745));
        AssertColor(White, Pixel(result, 0, 55, 770));
        AssertColor(White, Pixel(result, 0, 25, 735));
    }

    [Fact]
    public void Page_content_that_leaves_its_graphics_state_changed_does_not_move_the_stamp()
    {
        var stamp = HtmlToPdf.Convert("<div style=\"width:100pt;height:50pt;background:red\"></div>",
            new PdfOptions { PageSize = new PageSize(100, 50), Margins = new PageMargins(0) });
        foreach (var content in new[] { "q 0.1 0 0 0.1 0 0 cm q 0 0 1 rg", "Q Q 0.1 0 0 0.1 0 0 cm 0 0 1 rg", "q Q Q q 3 0 0 3 0 0 cm" })
        {
            var result = PdfStamper.Stamp(RawPdf("/MediaBox [0 0 300 300]", content), stamp,
                new PdfStampOptions { Position = PdfStampPosition.TopLeft, Scale = 1 });
            AssertColor(Red, Pixel(result, 0, 95, 45));
            AssertColor(White, Pixel(result, 0, 105, 45));
        }
    }

    [Fact]
    public void Links_bookmarks_form_fields_and_properties_are_kept()
    {
        var html = "<h1>Chapter</h1><p><a href=\"https://example.com\">link</a></p><form><input name=\"name\" value=\"Ann\"></form>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Title = "The title", Author = "Someone" });
        var result = PdfStamper.Stamp(pdf, CornerStamp("red"));
        PdfTestHelpers.AssertStructurallyValid(result);
        var file = PdfFile.Open(result, null);
        var catalog = file.Catalog!;
        Assert.NotNull(file.GetDict(file.GetDict(catalog, "Outlines"), "First"));
        Assert.Single(file.GetArray(file.GetDict(catalog, "AcroForm"), "Fields")!.Items);
        var info = file.Resolve(file.Trailer["Info"]) as PdfDict;
        Assert.Equal("The title", ((PdfString)file.Resolve(info!["Title"])!).Text);
        var annots = file.GetArray(Page(file, 0), "Annots")!.Items.Select(a => file.GetName((PdfDict)file.Resolve(a)!, "Subtype")).ToList();
        Assert.Contains("Link", annots);
        Assert.Contains("Widget", annots);
        // The original identifier is kept.
        Assert.Equal(((PdfString)file.Resolve(((PdfArray)file.Resolve(PdfFile.Open(pdf, null).Trailer["ID"])!)[0])!).Bytes,
            ((PdfString)file.Resolve(((PdfArray)file.Resolve(file.Trailer["ID"])!)[0])!).Bytes);
    }

    [Fact]
    public void A_tagged_document_stays_tagged_and_the_stamp_is_an_artifact()
    {
        var html = "<html lang=\"en\"><head><title>Report</title></head><body><h1>Title</h1><p>Text.</p></body></html>";
        var pdf = HtmlToPdf.Convert(html, new PdfOptions { Accessibility = PdfAccessibility.PdfUA1 });
        var stamp = HtmlToPdf.Convert(html.Replace("Title", "PAID"), new PdfOptions { Accessibility = PdfAccessibility.Tagged });
        var result = PdfStamper.Stamp(pdf, stamp);
        PdfTestHelpers.AssertStructurallyValid(result);
        var before = PdfFile.Open(pdf, null);
        var file = PdfFile.Open(result, null);
        var root = file.GetDict(file.Catalog, "StructTreeRoot")!;
        Assert.Equal(before.GetInt(before.GetDict(before.Catalog, "StructTreeRoot"), "ParentTreeNextKey", -1), file.GetInt(root, "ParentTreeNextKey", -2));
        Assert.Equal(before.GetInt(Page(before, 0), "StructParents", -1), file.GetInt(Page(file, 0), "StructParents", -2));
        Assert.True(file.Resolve(file.GetDict(file.Catalog, "MarkInfo")!["Marked"]) is PdfBool { Value: true });
        Assert.NotNull(file.Resolve(file.Catalog!["Metadata"]));
        var content = Content(result, 0);
        Assert.Contains("/Artifact BMC\nq\n", content);
        // The stamp's own marked-content ids are gone, since its structure tree is not copied.
        var form = (PdfStream)file.Resolve(StampRef(file, 0))!;
        var formContent = Encoding.Latin1.GetString(file.DecodeStream(form));
        Assert.DoesNotContain("MCID", formContent);
        Assert.Contains("BMC", formContent);
    }

    [Fact]
    public void A_signed_document_is_refused_unless_signatures_may_break()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Stamp Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx, "pw"), "pw");
        var signed = PdfSigner.Sign(Background("white"), new PdfSignatureOptions(cert));
        var ex = Assert.Throws<InvalidOperationException>(() => PdfStamper.Stamp(signed, CornerStamp("red")));
        Assert.Contains("signed", ex.Message);
        var result = PdfStamper.Stamp(signed, CornerStamp("red"), new PdfStampOptions { InvalidateSignatures = true });
        AssertColor(Red, Pixel(result, 0, 600, 780));
    }

    [Fact]
    public void Encrypted_input_needs_its_password_and_the_result_can_be_encrypted_again()
    {
        var encrypted = HtmlToPdf.Convert("<p>secret</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "open" } });
        Assert.Throws<PdfPasswordException>(() => PdfStamper.Stamp(encrypted, CornerStamp("red")));
        var plain = PdfStamper.Stamp(encrypted, CornerStamp("red"), new PdfStampOptions { Password = "open" });
        Assert.DoesNotContain("/Encrypt", PdfTestHelpers.Latin1(plain));
        AssertColor(Red, Pixel(plain, 0, 560, 780));

        var again = PdfStamper.Stamp(encrypted, CornerStamp("red"), new PdfStampOptions { Password = "open", Security = new PdfSecurity { UserPassword = "new" } });
        Assert.Throws<PdfPasswordException>(() => PdfRenderer.Open(again));
        var img = PdfRenderer.Open(again, "new").RenderPage(0, new ImageOptions { Dpi = 72 });
        int i = (780 * img.Width + 560) * 4;
        AssertColor(Red, (img.Rgba[i], img.Rgba[i + 1], img.Rgba[i + 2]));

        var encryptedStamp = HtmlToPdf.Convert("<p>x</p>", new PdfOptions { Security = new PdfSecurity { UserPassword = "s" } });
        Assert.Throws<PdfPasswordException>(() => PdfStamper.Stamp(Background("white"), encryptedStamp));
        PdfStamper.Stamp(Background("white"), encryptedStamp, new PdfStampOptions { StampPassword = "s" });
    }

    [Fact]
    public void Html_stamps_are_rendered_at_each_page_size()
    {
        var target = PdfMerger.Merge(Background("white"), HtmlToPdf.Convert("<p>a</p>", new PdfOptions { PageSize = PageSize.A5.Landscape }));
        var html = "<style>body{margin:0} i{position:absolute;right:0;bottom:0;width:72pt;height:36pt;background:red}</style><i></i>";
        var result = PdfStamper.StampHtml(target, html);
        AssertColor(Red, Pixel(result, 0, 600, 780));
        AssertColor(Red, Pixel(result, 1, 590, 410));
        AssertColor(White, Pixel(result, 1, 300, 200));
        // The caller's rendering options are not changed.
        var htmlOptions = new PdfOptions { Margins = new PageMargins(0) };
        PdfStamper.StampHtml(target, html, null, htmlOptions);
        Assert.Equal(PageSize.A4.Width, htmlOptions.PageSize.Width);
    }

    [Fact]
    public void Html_stamps_ignore_the_footer_header_and_watermark_of_shared_options()
    {
        // The main conversion's options are the natural ones to pass for fonts and base directory; their footer, header
        // and watermark were drawn a second time by the stamp.
        var shared = new PdfOptions { FooterText = "Footer {page}", HeaderText = "Header", Watermark = new PdfWatermark { Text = "DRAFT" } };
        var text = PdfText.Extract(PdfStamper.StampHtml(HtmlToPdf.Convert("<p>a</p>"), "<p style='margin: 0'>PAID</p>", null, shared));
        Assert.Contains("PAID", text);
        Assert.DoesNotContain("Footer", text);
        Assert.DoesNotContain("Header", text);
        Assert.DoesNotContain("DRAFT", text);
    }

    [Fact]
    public void The_stream_overload_writes_the_same_document()
    {
        using var input = new MemoryStream(Background("green"));
        using var stamp = new MemoryStream(CornerStamp("red"));
        using var output = new MemoryStream();
        PdfStamper.Stamp(input, stamp, output);
        AssertColor(Red, Pixel(output.ToArray(), 0, 600, 780));
    }

    [Fact]
    public void Hostile_stamps_fail_cleanly()
    {
        var target = Background("white");
        Assert.Throws<InvalidDataException>(() => PdfStamper.Stamp(target, Encoding.ASCII.GetBytes("not a pdf at all")));
        Assert.Throws<InvalidDataException>(() => PdfStamper.Stamp(target, Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => PdfStamper.Stamp(Encoding.ASCII.GetBytes("%PDF-1.7\ngarbage"), CornerStamp("red")));
        var good = CornerStamp("red");
        // Damaged files are repaired where possible, and otherwise refused as unreadable.
        var rng = new Random(7);
        for (int i = 1; i <= 24; i++)
        {
            var damaged = (byte[])good.Clone();
            if (i % 2 == 0) damaged = damaged[..(damaged.Length * i / 25)];
            else for (int k = 0; k < 20; k++) damaged[rng.Next(damaged.Length)] = (byte)rng.Next(256);
            foreach (var (pdf, stampPdf) in new[] { (target, damaged), (damaged, good) })
            {
                try
                {
                    var stamped = PdfStamper.Stamp(pdf, stampPdf);
                    PdfTestHelpers.AssertStructurallyValid(stamped);
                    PdfRenderer.Open(stamped).RenderPage(0, new ImageOptions { Dpi = 10 });
                }
                catch (InvalidDataException)
                {
                }
            }
        }
        // A page tree that loops, resources that point at themselves, content that is not content.
        var loop = RawPdf("/MediaBox [0 0 100 100] /Resources 5 0 R", "q q q BT ( unclosed", extra: "5 0 obj\n<< /XObject << /Self 5 0 R >> /Font 5 0 R >>\nendobj\n",
            kidsExtra: " 2 0 R");
        var result = PdfStamper.Stamp(target, loop);
        PdfTestHelpers.AssertStructurallyValid(result);
        PdfRenderer.Open(result).RenderPage(0);
        // A content stream that inflates to far more than allowed.
        var bomb = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(bomb, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            for (int i = 0; i < 80; i++) z.Write(new byte[1 << 20]);
        var bombPdf = RawPdf("/MediaBox [0 0 100 100]", null, streamData: bomb.ToArray());
        Assert.Throws<InvalidDataException>(() => PdfStamper.Stamp(target, bombPdf));
        // Bad options.
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamper.Stamp(target, good, new PdfStampOptions { Opacity = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamper.Stamp(target, good, new PdfStampOptions { Scale = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamper.Stamp(target, good, new PdfStampOptions { StampPage = -1 }));
    }

    // ------------------------------------------------------------------ helpers

    private static PdfRef StampRef(PdfFile file, int page)
        => (PdfRef)file.GetDict(file.GetDict(Page(file, page), "Resources"), "XObject")!["Stamp"]!;

    private static PdfDict Page(PdfFile file, int index)
        => (PdfDict)file.Resolve(file.GetArray(file.GetDict(file.Catalog, "Pages"), "Kids")!.Items[index])!;

    /// <summary>A page's content streams, decoded and joined.</summary>
    private static string Content(byte[] pdf, int page)
    {
        var file = PdfFile.Open(pdf, null);
        var contents = file.Resolve(Page(file, page)["Contents"]);
        var items = contents is PdfArray a ? a.Items : new List<PdfObject> { Page(file, page)["Contents"]! };
        return string.Concat(items.Select(i => Encoding.Latin1.GetString(file.DecodeStream((PdfStream)file.Resolve(i)!))));
    }

    /// <summary>A one-page PDF with the given page entries and content (or compressed content data).</summary>
    private static byte[] RawPdf(string pageEntries, string? content, string extra = "", string kidsExtra = "", byte[]? streamData = null)
    {
        static byte[] L(string t) => Encoding.Latin1.GetBytes(t);
        var objects = new List<byte[]>
        {
            L("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"),
            L("2 0 obj\n<< /Type /Pages /Kids [3 0 R" + kidsExtra + "] /Count 1 >>\nendobj\n"),
            L("3 0 obj\n<< /Type /Page /Parent 2 0 R " + pageEntries + " /Contents 4 0 R >>\nendobj\n"),
            streamData != null
                ? L("4 0 obj\n<< /Length " + streamData.Length + " /Filter /FlateDecode >>\nstream\n").Concat(streamData).Concat(L("\nendstream\nendobj\n")).ToArray()
                : L("4 0 obj\n<< /Length " + content!.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n"),
        };
        if (extra.Length > 0) objects.Add(L(extra));
        var ms = new MemoryStream();
        ms.Write(L("%PDF-1.7\n"));
        var offsets = new List<long>();
        foreach (var o in objects) { offsets.Add(ms.Position); ms.Write(o); }
        long xref = ms.Position;
        var sb = new StringBuilder("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        ms.Write(L(sb.ToString()));
        return ms.ToArray();
    }
}

using System.Text;
using PdfPecker.Barcodes;
using PdfPecker.Barcodes.Reading;

namespace PdfPecker.Tests;

public class BarcodeReaderTests
{
    /// <summary>
    /// Draws a symbol's modules as grey pixels, turned by <paramref name="degrees"/> around the image centre, with a quiet
    /// zone of four modules. Each pixel takes the module under its centre, as a scan of a crisp print does.
    /// </summary>
    internal static GrayImage Draw(bool[,] modules, double moduleSize = 4, double degrees = 0, bool inverted = false, int quiet = 4)
    {
        int rows = modules.GetLength(0), cols = modules.GetLength(1);
        double w = (cols + 2 * quiet) * moduleSize, h = (rows + 2 * quiet) * moduleSize;
        double diagonal = Math.Sqrt(w * w + h * h);
        int size = (int)Math.Ceiling(degrees % 90 == 0 ? Math.Max(w, h) : diagonal) + 4;
        double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        var pixels = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - size / 2.0, dy = y + 0.5 - size / 2.0;
                double u = (cos * dx + sin * dy + w / 2) / moduleSize - quiet, v = (-sin * dx + cos * dy + h / 2) / moduleSize - quiet;
                int mx = (int)Math.Floor(u), my = (int)Math.Floor(v);
                bool dark = mx >= 0 && my >= 0 && mx < cols && my < rows && modules[my, mx];
                pixels[y * size + x] = dark != inverted ? (byte)20 : (byte)235;
            }
        }
        return new GrayImage(size, size, pixels);
    }

    /// <summary>A linear barcode as one row of modules, drawn with the given bar height.</summary>
    private static bool[,] Bars(bool[] modules, int height = 30)
    {
        var grid = new bool[height, modules.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < modules.Length; x++)
                grid[y, x] = modules[x];
        return grid;
    }

    /// <summary>The barcode SVG sized to the width of the element around it.</summary>
    private static string Fill(string svg) => svg.Replace("<svg ", "<svg style='display:block;width:100%;height:auto' ", StringComparison.Ordinal);

    private static List<BarcodeResult> Read(GrayImage image, BarcodeReaderOptions? options = null) =>
        Barcode.Read(image, options, 0, 1, 1, CancellationToken.None);

    [Theory]
    [InlineData("https://pdfpecker.net/docs", 0)]
    [InlineData("https://pdfpecker.net/docs", 90)]
    [InlineData("https://pdfpecker.net/docs", 180)]
    [InlineData("https://pdfpecker.net/docs", 27)]
    [InlineData("0123456789012345678901234567890123456789", 0)]
    [InlineData("HELLO WORLD $%*+-./:", 0)]
    [InlineData("Grüße, æøå and 日本語", 0)]
    public void Qr_codes_read_back_at_any_angle(string text, double degrees)
    {
        var found = Read(Draw(QrEncoder.Encode(text, QrErrorCorrection.Medium), 4, degrees));
        var code = Assert.Single(found);
        Assert.Equal(BarcodeFormat.QrCode, code.Format);
        Assert.Equal(text, code.Text);
        Assert.Equal(text, Encoding.UTF8.GetString(code.Bytes));
    }

    [Fact]
    public void Large_qr_codes_read_back()
    {
        var text = string.Concat(Enumerable.Repeat("Hello World 0123456789 ", 60));
        var modules = QrEncoder.Encode(text, QrErrorCorrection.Low);
        Assert.True(modules.GetLength(0) > 80);
        Assert.Equal(text, Assert.Single(Read(Draw(modules, 3, 8))).Text);
    }

    [Fact]
    public void Qr_error_correction_repairs_damaged_modules()
    {
        var modules = QrEncoder.Encode("Damaged but readable", QrErrorCorrection.High);
        // Flip a block of data modules away from the finder patterns; level H corrects up to 30 % of the codewords.
        for (int y = 10; y < 14; y++)
            for (int x = 10; x < 14; x++)
                modules[y, x] = !modules[y, x];
        Assert.Equal("Damaged but readable", Assert.Single(Read(Draw(modules))).Text);
    }

    [Fact]
    public void Light_codes_on_a_dark_ground_read_unless_turned_off()
    {
        var image = Draw(QrEncoder.Encode("inverted", QrErrorCorrection.Medium), inverted: true);
        Assert.Equal("inverted", Assert.Single(Read(image)).Text);
        Assert.Empty(Read(image, new BarcodeReaderOptions { TryInverted = false }));
    }

    [Fact]
    public void Mirrored_qr_codes_read()
    {
        var modules = QrEncoder.Encode("mirror image", QrErrorCorrection.Medium);
        int n = modules.GetLength(0);
        var mirrored = new bool[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                mirrored[y, x] = modules[y, n - 1 - x];
        Assert.Equal("mirror image", Assert.Single(Read(Draw(mirrored))).Text);
    }

    [Theory]
    [InlineData("Hello Data Matrix", 0)]
    [InlineData("Hello Data Matrix", 90)]
    [InlineData("Hello Data Matrix", 33)]
    [InlineData("1234567890123456789012345678901234567890", 0)]
    [InlineData("Grüße æøå 日本", 0)]
    public void Data_matrix_codes_read_back_at_any_angle(string text, double degrees)
    {
        var code = Assert.Single(Read(Draw(DataMatrixEncoder.Encode(text), 5, degrees)));
        Assert.Equal(BarcodeFormat.DataMatrix, code.Format);
        Assert.Equal(text, code.Text);
        Assert.False(code.IsGs1);
    }

    [Fact]
    public void Large_data_matrix_codes_with_several_regions_read_back()
    {
        var text = string.Concat(Enumerable.Repeat("ABCDEFGHIJ", 40));
        var modules = DataMatrixEncoder.Encode(text);
        Assert.True(modules.GetLength(0) >= 52);
        Assert.Equal(text, Assert.Single(Read(Draw(modules, 3))).Text);
    }

    [Fact]
    public void Gs1_data_matrix_is_flagged_and_separates_fields_with_group_separators()
    {
        var code = Assert.Single(Read(Draw(DataMatrixEncoder.EncodeGs1("(01)09506000134352(10)AB-123(17)270131"), 5)));
        Assert.True(code.IsGs1);
        Assert.Equal("010950600013435210AB-123\u001D17270131", code.Text);
    }

    [Theory]
    [InlineData("PdfPecker-128", 0)]
    [InlineData("PdfPecker-128", 180)]
    [InlineData("PdfPecker-128", 90)]
    [InlineData("1234567890123", 0)]
    [InlineData("mixed 12345678 Case", 270)]
    public void Code_128_reads_back_in_any_quarter_turn(string text, double degrees)
    {
        var code = Assert.Single(Read(Draw(Bars(LinearEncoder.Code128(text)), 2, degrees, quiet: 12)));
        Assert.Equal(BarcodeFormat.Code128, code.Format);
        Assert.Equal(text, code.Text);
    }

    [Theory]
    [InlineData("590123412345", "5901234123457", 0)]
    [InlineData("400638133393", "4006381333931", 180)]
    [InlineData("0012345678905", "0012345678905", 90)]
    public void Ean_13_reads_back_with_its_check_digit(string digits, string expected, double degrees)
    {
        var code = Assert.Single(Read(Draw(Bars(LinearEncoder.Ean13(LinearEncoder.Ean13Digits(digits)), 50), 2, degrees, quiet: 12)));
        Assert.Equal(BarcodeFormat.Ean13, code.Format);
        Assert.Equal(expected, code.Text);
    }

    [Fact]
    public void Formats_limit_what_is_looked_for()
    {
        var image = Draw(QrEncoder.Encode("only QR", QrErrorCorrection.Medium));
        Assert.Empty(Read(image, new BarcodeReaderOptions { Formats = BarcodeFormat.DataMatrix | BarcodeFormat.Code128 }));
        Assert.Single(Read(image, new BarcodeReaderOptions { Formats = BarcodeFormat.QrCode }));
    }

    [Fact]
    public void Several_codes_in_one_image_are_all_found_in_reading_order()
    {
        var a = Draw(QrEncoder.Encode("first", QrErrorCorrection.Medium));
        var b = Draw(DataMatrixEncoder.Encode("second"), 5);
        var c = Draw(QrEncoder.Encode("third", QrErrorCorrection.Medium));
        int width = a.Width + b.Width + 20, height = Math.Max(a.Height, b.Height) + c.Height + 20;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)235);
        void Paste(GrayImage image, int left, int top)
        {
            for (int y = 0; y < image.Height; y++)
                Array.Copy(image.Pixels, y * image.Width, pixels, (top + y) * width + left, image.Width);
        }
        Paste(a, 0, 0);
        Paste(b, a.Width + 20, 0);
        Paste(c, 0, Math.Max(a.Height, b.Height) + 20);
        var found = Read(new GrayImage(width, height, pixels));
        Assert.Equal(new[] { "first", "second", "third" }, found.Select(f => f.Text));
        Assert.Single(Read(new GrayImage(width, height, pixels), new BarcodeReaderOptions { MaxBarcodes = 1 }));
    }

    [Fact]
    public void Corners_and_bounds_are_in_image_pixels()
    {
        var modules = QrEncoder.Encode("where", QrErrorCorrection.Medium);
        int n = modules.GetLength(0);
        var code = Assert.Single(Read(Draw(modules, 4)));
        // The symbol starts after the quiet zone of 4 modules (16 px) and two pixels of margin.
        Assert.Equal(4, code.Corners.Count);
        Assert.InRange(code.X, 16, 20);
        Assert.InRange(code.Y, 16, 20);
        Assert.InRange(code.Width, n * 4 - 4, n * 4 + 4);
        Assert.InRange(code.Height, n * 4 - 4, n * 4 + 4);
        Assert.Equal(0, code.PageNumber);
    }

    [Fact]
    public void Epc_payment_codes_are_parsed()
    {
        var svg = Barcode.EpcQrCode(new EpcPayment { Name = "Example Ltd.", Iban = "DE89370400440532013000", Bic = "COBADEFFXXX", Amount = 12.30m, Text = "Invoice 42" });
        var png = HtmlToImage.Convert($"<div style='width:60mm'>{svg}</div>", new PdfOptions { PageSize = PageSize.A5 }, new ImageOptions { Dpi = 150 })[0];
        var code = Assert.Single(Barcode.Read(png, cancellationToken: CancellationToken.None));
        var payment = code.EpcPayment;
        Assert.NotNull(payment);
        Assert.Equal("Example Ltd.", payment.Name);
        Assert.Equal("DE89370400440532013000", payment.Iban);
        Assert.Equal("COBADEFFXXX", payment.Bic);
        Assert.Equal(12.30m, payment.Amount);
        Assert.Equal("Invoice 42", payment.Text);
        Assert.Null(code.SwissQrBill);
    }

    [Fact]
    public void Swiss_qr_bills_are_parsed()
    {
        var bill = new SwissQrBill
        {
            Account = "CH4431999123000889012",
            Creditor = new SwissQrAddress { Name = "Example AG", Street = "Bahnhofstrasse", HouseNumber = "1", PostalCode = "8001", Town = "Zürich" },
            Amount = 199.95m,
            Reference = "210000000003139471430009017",
            Message = "Order 2026-17",
        };
        var pdf = HtmlToPdf.Convert($"<div style='width:46mm'>{Barcode.SwissQrCode(bill)}</div>");
        var code = Assert.Single(PdfBarcodes.Find(pdf, cancellationToken: CancellationToken.None));
        var read = code.SwissQrBill;
        Assert.NotNull(read);
        Assert.Equal("CH4431999123000889012", read.Account);
        Assert.Equal("Example AG", read.Creditor!.Name);
        Assert.Equal("Bahnhofstrasse", read.Creditor!.Street);
        Assert.Equal("1", read.Creditor!.HouseNumber);
        Assert.Equal("8001", read.Creditor!.PostalCode);
        Assert.Equal("Zürich", read.Creditor!.Town);
        Assert.Equal(199.95m, read.Amount);
        Assert.Equal("210000000003139471430009017", read.Reference);
        Assert.Equal("Order 2026-17", read.Message);
        Assert.Null(code.EpcPayment);
    }

    [Fact]
    public void Codes_on_pdf_pages_come_with_their_page_and_position_in_points()
    {
        var html = "<style>body{margin:0} @page{size:A5;margin:0}</style>"
            + "<div style='margin:50pt 0 0 100pt;width:100pt'>" + Fill(Barcode.QrCode("page one")) + "</div>"
            + "<div style='break-before:page;margin:72pt 0 0 72pt;width:160pt'>" + Fill(Barcode.Code128("PAGE-2")) + "</div>"
            + "<div style='margin:20pt 0 0 72pt;width:40pt'>" + Fill(Barcode.DataMatrix("also page 2")) + "</div>";
        var pdf = HtmlToPdf.Convert(html);
        var found = PdfBarcodes.Find(pdf, cancellationToken: CancellationToken.None);
        Assert.Equal(new[] { (1, "page one"), (2, "PAGE-2"), (2, "also page 2") }, found.Select(f => (f.PageNumber, f.Text)));
        // The SVG is 100pt wide with a quiet zone of 4 modules around the 21 modules of the symbol.
        var qr = found[0];
        double module = 100.0 / 29;
        Assert.InRange(qr.X, 100 + 4 * module - 1.5, 100 + 4 * module + 1.5);
        Assert.InRange(qr.Y, 50 + 4 * module - 1.5, 50 + 4 * module + 1.5);
        Assert.InRange(qr.Width, 21 * module - 1.5, 21 * module + 1.5);

        var second = PdfBarcodes.Find(pdf, new PdfBarcodeOptions { Pages = PdfPageRange.Parse("2"), Formats = BarcodeFormat.Code128 },
            CancellationToken.None);
        Assert.Equal("PAGE-2", Assert.Single(second).Text);
    }

    [Fact]
    public void Rendered_pages_read_with_positions_in_their_pixels()
    {
        var pdf = HtmlToPdf.Convert("<style>body{margin:0}</style><div style='width:30mm'>" + Fill(Barcode.DataMatrix("rendered")) + "</div>");
        var page = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 200 }, CancellationToken.None);
        var code = Assert.Single(Barcode.Read(page, cancellationToken: CancellationToken.None));
        Assert.Equal("rendered", code.Text);
        // 30 mm at 200 dpi, less the quiet zone of 2 modules on each side.
        int modules = DataMatrixEncoder.Encode("rendered").GetLength(0);
        double expected = 30 / 25.4 * 200 * modules / (modules + 4);
        Assert.InRange(code.Width, expected - 4, expected + 4);
    }

    [Fact]
    public void Pages_without_codes_and_text_that_looks_like_bars_give_nothing()
    {
        var pdf = HtmlToPdf.Convert("<h1>No codes here</h1><p>" + string.Concat(Enumerable.Repeat("||| Il1 ||| ", 200)) + "</p>"
            + "<table border=1>" + string.Concat(Enumerable.Repeat("<tr><td>■</td><td>□</td><td>■■</td></tr>", 20)) + "</table>");
        Assert.Empty(PdfBarcodes.Find(pdf, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public void Bad_arguments_and_data_throw_documented_exceptions()
    {
        Assert.Throws<InvalidDataException>(() => Barcode.Read(Encoding.ASCII.GetBytes("not an image")));
        Assert.Throws<ArgumentNullException>(() => Barcode.Read((byte[])null!));
        var pdf = HtmlToPdf.Convert("<p>x</p>");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBarcodes.Find(pdf, new PdfBarcodeOptions { Dpi = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBarcodes.Find(pdf, new PdfBarcodeOptions { Pages = PdfPageRange.Parse("3") }));
        Assert.Throws<PdfLimitException>(() => PdfBarcodes.Find(HtmlToPdf.Convert("<p>1</p><p style='break-before:page'>2</p>"),
            new PdfBarcodeOptions { MaxPages = 1 }));
    }

    [Fact]
    public void Payment_parsers_reject_other_qr_texts()
    {
        Assert.Null(PaymentPayloads.Epc("https://example.com"));
        Assert.Null(PaymentPayloads.Epc("BCD\n003\n1\nSCT"));
        Assert.Null(PaymentPayloads.Swiss("SPC\n0200\n1"));
        Assert.Null(PaymentPayloads.Swiss("hello"));
    }

    [Theory]
    // Numeric, alphanumeric and byte segments, as a QR version 1 data stream: mode, count and data bits, then end.
    [InlineData("0001 0000000011 0001111011", "123")]
    [InlineData("0010 000000010 00111001101", "AB")]
    [InlineData("0100 00000010 01101000 01101001", "hi")]
    public void Qr_segments_parse(string bits, string expected)
    {
        bits = bits.Replace(" ", "", StringComparison.Ordinal) + "0000";
        var data = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
            if (bits[i] == '1') data[i / 8] |= (byte)(0x80 >> (i % 8));
        Assert.Equal(expected, QrDecoder.ParseSegments(data, 1)?.Text);
    }

    [Fact]
    public void Data_matrix_encodations_parse()
    {
        // ASCII "A", a digit pair "12", then C40 "ABC" (230, then 16-bit values of 1600 * c1 + 40 * c2 + c3 + 1), unlatch.
        int c40 = 1600 * 14 + 40 * 15 + 16 + 1;
        var data = new byte[] { (byte)('A' + 1), 130 + 12, 230, (byte)(c40 >> 8), (byte)c40, 254 };
        Assert.Equal("A12ABC", DataMatrixDecoder.Parse(data)?.Text);
        // Base 256: latch 231, then a length and bytes, each randomized by its position.
        var base256 = new List<byte> { 231 };
        byte[] payload = [4, (byte)'b', (byte)'y', (byte)'t', (byte)'e'];
        for (int i = 0; i < payload.Length; i++)
            base256.Add((byte)((payload[i] + (149 * (i + 2)) % 255 + 1) % 256));
        Assert.Equal("byte", DataMatrixDecoder.Parse(base256.ToArray())?.Text);
    }
}

[Collection(HostileInputCollection.Name)]
public class BarcodeReaderHostileInputTests
{
    private static List<BarcodeResult> ReadWithin(GrayImage image, int seconds)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return Barcode.Read(image, null, 0, 1, 1, cts.Token);
    }

    [Fact]
    public void Random_noise_ends_quickly_with_no_wrong_reads()
    {
        var random = new Random(1234);
        var pixels = new byte[1200 * 1200];
        random.NextBytes(pixels);
        Assert.Empty(ReadWithin(new GrayImage(1200, 1200, pixels), 30));
    }

    [Fact]
    public void An_image_full_of_finder_patterns_stops_at_the_sampling_budget()
    {
        // Hundreds of QR finder patterns form many thousand triples that each look like a symbol; the detector tries a
        // bounded number of them instead of all.
        var finder = new bool[7, 7];
        for (int y = 0; y < 7; y++)
            for (int x = 0; x < 7; x++)
                finder[y, x] = x is 0 or 6 || y is 0 or 6 || (x is >= 2 and <= 4 && y is >= 2 and <= 4);
        int cell = 11, count = 30, size = cell * count * 3;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)235);
        for (int cy = 0; cy < count; cy++)
            for (int cx = 0; cx < count; cx++)
                for (int y = 0; y < 21; y++)
                    for (int x = 0; x < 21; x++)
                        if (finder[y / 3, x / 3]) pixels[(cy * cell * 3 + 6 + y) * size + cx * cell * 3 + 6 + x] = 20;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(ReadWithin(new GrayImage(size, size, pixels), 60));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
    }

    [Fact]
    public void Stripes_and_checkerboards_give_nothing()
    {
        const int size = 800;
        var stripes = new byte[size * size];
        var checker = new byte[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                stripes[y * size + x] = (x / 3) % 2 == 0 ? (byte)0 : (byte)255;
                checker[y * size + x] = ((x / 4) + (y / 4)) % 2 == 0 ? (byte)0 : (byte)255;
            }
        }
        Assert.Empty(ReadWithin(new GrayImage(size, size, stripes), 30));
        Assert.Empty(ReadWithin(new GrayImage(size, size, checker), 30));
    }

    [Fact]
    public void Tiny_and_one_pixel_images_read_as_empty()
    {
        Assert.Empty(ReadWithin(new GrayImage(1, 1, [0]), 5));
        Assert.Empty(ReadWithin(new GrayImage(3, 1, [0, 255, 0]), 5));
        Assert.Empty(ReadWithin(new GrayImage(1, 300, new byte[300]), 5));
    }

    [Fact]
    public void Damaged_codeword_streams_fail_without_exceptions()
    {
        var random = new Random(99);
        var data = new byte[200];
        for (int i = 0; i < 2000; i++)
        {
            random.NextBytes(data);
            var length = random.Next(1, data.Length);
            var slice = data.AsSpan(0, length).ToArray();
            _ = QrDecoder.ParseSegments(slice, random.Next(1, 41));
            _ = DataMatrixDecoder.Parse(slice);
            var text = Encoding.Latin1.GetString(slice);
            _ = PaymentPayloads.Epc("BCD\n" + text);
            _ = PaymentPayloads.Swiss("SPC\n0200\n" + text);
        }
    }

    [Fact]
    public void Fuzz_timeout_fec224c6_ends_at_the_sampling_budget()
    {
        // barcode-read, timeout-fec224c6: grey pixels behind a 16-bit width, as the fuzz target reads them. The QR
        // detector tried hundreds of finder triples with every corner search and ran for minutes.
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "barcodes", "fuzz-timeout-fec224c6.gray"));
        int width = Math.Clamp((int)BitConverter.ToUInt16(data, 0), 1, 1024), height = (data.Length - 2) / width;
        var started = System.Diagnostics.Stopwatch.StartNew();
        _ = ReadWithin(new GrayImage(width, height, data.AsSpan(2, width * height).ToArray()), 60);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
    }

    [Fact]
    public void A_crowded_image_of_code_fragments_ends_in_bounded_time()
    {
        // Pieces of QR codes scattered over the image give hundreds of finder patterns and partial symbols.
        var random = new Random(7);
        const int size = 1024;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)235);
        var modules = QrEncoder.Encode("fragment", QrErrorCorrection.Low);
        int n = modules.GetLength(0);
        for (int piece = 0; piece < 400; piece++)
        {
            int left = random.Next(size - 3 * n), top = random.Next(size - 3 * n), scale = random.Next(2, 4);
            int fromX = random.Next(n / 2), fromY = random.Next(n / 2);
            for (int y = fromY; y < n; y++)
                for (int x = fromX; x < n; x++)
                    if (modules[y, x])
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                                pixels[(top + (y - fromY) * scale + sy) * size + left + (x - fromX) * scale + sx] = 20;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        _ = ReadWithin(new GrayImage(size, size, pixels), 60);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfPecker.Barcodes;

namespace PdfPecker.Tests;

public class BarcodeTests
{
    private static int QrSize(string svg) =>
        int.Parse(Regex.Match(svg, "viewBox=\"0 0 (\\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    private static string Grid(bool[,] modules)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < modules.GetLength(0); y++)
        {
            for (var x = 0; x < modules.GetLength(1); x++)
                sb.Append(modules[y, x] ? '#' : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Qr_code_matches_an_independent_encoder()
    {
        // "HELLO WORLD" at version 1-Q, the worked example of the QR code tutorials; the expected grid is the output of
        // the segno Python library with the same mask, and was also read back by the zbar scanner.
        string[] expected =
        {
            "#######.##....#######", "#.....#.#..#..#.....#", "#.###.#.#..##.#.###.#", "#.###.#.#.....#.###.#",
            "#.###.#.#.#...#.###.#", "#.....#...#...#.....#", "#######.#.#.#.#######", "........#............",
            ".##.#.##....#.#.#####", ".#......####....#...#", "..##.###.##...#.##...", ".##.##.#..##.#.#.###.",
            "#...#.#.#.###.###.#.#", "........##.#..#...#.#", "#######.#.#....#.##..", "#.....#..#.##.##.#...",
            "#.###.#.#.#...#######", "#.###.#..#.#.#.#...#.", "#.###.#.#..#.###.#..#", "#.....#.#.####...#.##",
            "#######....#.###....#",
        };
        Assert.Equal(string.Join('\n', expected) + "\n", Grid(QrEncoder.Encode("HELLO WORLD", QrErrorCorrection.Quartile)));
    }

    [Theory]
    [InlineData(QrErrorCorrection.Low, 17, 1)]
    [InlineData(QrErrorCorrection.Medium, 14, 1)]
    [InlineData(QrErrorCorrection.Quartile, 11, 1)]
    [InlineData(QrErrorCorrection.High, 7, 1)]
    [InlineData(QrErrorCorrection.Medium, 213, 10)]
    [InlineData(QrErrorCorrection.High, 1273, 40)]
    [InlineData(QrErrorCorrection.Low, 2953, 40)]
    public void Qr_code_capacity_follows_the_standard_tables(QrErrorCorrection level, int bytes, int version)
    {
        // Lower-case letters force byte mode. The largest text of a version fits it; one byte more needs the next.
        var text = new string('a', bytes);
        Assert.Equal(version * 4 + 17, QrEncoder.Encode(text, level).GetLength(0));
        if (version < 40)
            Assert.Equal(version * 4 + 21, QrEncoder.Encode(text + "a", level).GetLength(0));
        else
            Assert.Throws<ArgumentException>(() => Barcode.QrCode(text + "a", new BarcodeOptions { ErrorCorrection = level }));
    }

    [Fact]
    public void Qr_code_packs_digits_and_alphanumeric_text_densely()
    {
        Assert.Equal(177, QrEncoder.Encode(new string('7', 7089), QrErrorCorrection.Low).GetLength(0));
        Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('7', 7090), QrErrorCorrection.Low));
        Assert.Equal(177, QrEncoder.Encode(new string('A', 4296), QrErrorCorrection.Low).GetLength(0));
        Assert.Throws<ArgumentException>(() => QrEncoder.Encode(new string('A', 4297), QrErrorCorrection.Low));
    }

    [Fact]
    public void Every_version_keeps_its_function_patterns()
    {
        for (var version = 1; version <= 40; version++)
        {
            // A text just over the previous version's capacity lands in this version.
            var text = new string('1', version == 1 ? 1 : NumericCapacity(version - 1) + 1);
            var m = QrEncoder.Encode(text, QrErrorCorrection.Low);
            var size = m.GetLength(0);
            Assert.Equal(version * 4 + 17, size);
            foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            {
                Assert.True(m[cy, cx]);
                Assert.False(m[cy - 2, cx]);
                Assert.True(m[cy - 3, cx - 3]);
            }
            for (var i = 8; i < size - 8; i++)
                Assert.Equal(i % 2 == 0, m[6, i]);
            Assert.True(m[size - 8, 8]);
        }
    }

    private static int NumericCapacity(int version)
    {
        var low = 1;
        var high = 7089;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (QrEncoder.Encode(new string('1', mid), QrErrorCorrection.Low).GetLength(0) <= version * 4 + 17)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    [Fact]
    public void Qr_svg_has_quiet_zone_size_and_colours()
    {
        var svg = Barcode.QrCode("HELLO WORLD", new BarcodeOptions { ErrorCorrection = QrErrorCorrection.Quartile, ModuleSize = 2, Color = "navy", Background = "\"><script>" });
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"58\" height=\"58\" viewBox=\"0 0 29 29\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"navy\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"&quot;&gt;&lt;script&gt;\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", svg, StringComparison.Ordinal);

        var bare = Barcode.QrCode("HELLO WORLD", new BarcodeOptions { QuietZone = false, Background = null });
        Assert.Equal(21, QrSize(bare));
        Assert.DoesNotContain("<rect", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_does_not_depend_on_the_culture()
    {
        var invariant = Barcode.Code128("ABC-123", new BarcodeOptions { ModuleSize = 1.25f, Height = 33.3f });
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "ar-SA", "fa-IR", "da-DK" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(invariant, Barcode.Code128("ABC-123", new BarcodeOptions { ModuleSize = 1.25f, Height = 33.3f }));
                Assert.Contains("EUR1234.50", new EpcPayment { Name = "A", Iban = "DE89 3704 0044 0532 0130 00", Amount = 1234.5m }.ToPayload(), StringComparison.Ordinal);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.Contains("width=\"", invariant, StringComparison.Ordinal);
        Assert.DoesNotContain(",", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void Epc_payload_follows_the_guideline()
    {
        var payment = new EpcPayment
        {
            Name = "Red Cross of Belgium",
            Iban = "BE72 0000 0000 1616",
            Bic = "BPOTBEB1",
            Amount = 12.3m,
            Text = "Urgency fund",
        };
        Assert.Equal("BCD\n002\n1\nSCT\nBPOTBEB1\nRed Cross of Belgium\nBE72000000001616\nEUR12.30\n\n\nUrgency fund", payment.ToPayload());
        Assert.Equal("BCD\n002\n1\nSCT\n\nA\nDE89370400440532013000",
            new EpcPayment { Name = "A", Iban = "de89370400440532013000" }.ToPayload());
        Assert.StartsWith("<svg", Barcode.EpcQrCode(payment), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "DE89370400440532013000", null, null, null)]
    [InlineData("A", "DE89370400440532013001", null, null, null)]
    [InlineData("A", "DE89", null, null, null)]
    [InlineData("A", "DE89370400440532013000", "BAD", null, null)]
    [InlineData("A", "DE89370400440532013000", null, "0", null)]
    [InlineData("A", "DE89370400440532013000", null, "1.005", null)]
    [InlineData("A", "DE89370400440532013000", null, "1000000000", null)]
    [InlineData("A\nB", "DE89370400440532013000", null, null, null)]
    [InlineData("A", "DE89370400440532013000", null, null, "RF18539007547034")]
    public void Epc_payload_rejects_invalid_fields(string name, string iban, string? bic, string? amount, string? reference)
    {
        var payment = new EpcPayment
        {
            Name = name,
            Iban = iban,
            Bic = bic,
            Amount = amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture),
            Reference = reference,
            Text = reference is null ? null : "both",
        };
        Assert.Throws<ArgumentException>(() => payment.ToPayload());
        Assert.Throws<ArgumentException>(() => Barcode.EpcQrCode(payment));
    }

    private static int[] Code128Values(string text)
    {
        // Reads the symbol values back from the modules: each symbol is 11 modules, the stop symbol 13.
        var modules = LinearEncoder.Code128(text);
        Assert.Equal(13 % 11, modules.Length % 11);
        var result = new List<int>();
        for (var i = 0; i + 11 <= modules.Length - 13; i += 11)
        {
            var widths = new StringBuilder();
            for (var j = i; j < i + 11;)
            {
                var start = j;
                while (j < i + 11 && modules[j] == modules[start])
                    j++;
                widths.Append(j - start);
            }
            result.Add(Array.IndexOf(LinearEncoder.Code128Patterns, widths.ToString()));
        }
        return result.ToArray();
    }

    [Theory]
    [InlineData("123456", new[] { 105, 12, 34, 56, 44 })]
    [InlineData("12", new[] { 105, 12, 14 })]
    [InlineData("ABC", new[] { 104, 33, 34, 35, 1 })]
    [InlineData("12345", new[] { 104, 17, 99, 23, 45, 53 })]
    [InlineData("ab\t1", new[] { 104, 65, 66, 101, 73, 17, 54 })]
    [InlineData("X1234567890", new[] { 104, 56, 99, 12, 34, 56, 78, 90, 54 })]
    public void Code128_chooses_code_sets_and_checksum(string text, int[] expected)
    {
        Assert.Equal(expected, Code128Values(text));
    }

    [Fact]
    public void Code128_rejects_what_it_cannot_encode()
    {
        Assert.Throws<ArgumentException>(() => Barcode.Code128(""));
        Assert.Throws<ArgumentException>(() => Barcode.Code128("Blåbær"));
        Assert.Throws<ArgumentException>(() => Barcode.Code128(new string('x', 1001)));
        var svg = Barcode.Code128("tab\there");
        Assert.Contains(">tab here</text>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Ean13_adds_and_checks_the_check_digit()
    {
        Assert.Equal("4006381333931", LinearEncoder.Ean13Digits("400638133393"));
        Assert.Equal("5901234123457", LinearEncoder.Ean13Digits("5901234123457"));
        Assert.Throws<ArgumentException>(() => Barcode.Ean13("5901234123458"));
        Assert.Throws<ArgumentException>(() => Barcode.Ean13("12345"));
        Assert.Throws<ArgumentException>(() => Barcode.Ean13("59012341234x"));

        // 4006381333931: first digit 4 gives the parity pattern L G L L G G for the left half.
        var modules = string.Concat(LinearEncoder.Ean13("4006381333931").Select(b => b ? '1' : '0'));
        Assert.Equal("101" + "0001101" + "0100111" + "0101111" + "0111101" + "0001001" + "0110011" + "01010"
            + "1000010" + "1000010" + "1000010" + "1110100" + "1000010" + "1100110" + "101", modules);
    }

    [Fact]
    public void Barcodes_convert_inline_and_as_images()
    {
        var html = "<html><body>" + Barcode.QrCode("https://pdfpecker.net/") + Barcode.Ean13("590123412345")
            + "<img src=\"" + Barcode.ToDataUri(Barcode.Code128("PdfPecker")) + "\"></body></html>";
        var pdf = HtmlToPdf.Convert(html);
        var png = PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 72 });
        Assert.True(png.Length > 1000);
        Assert.StartsWith("data:image/svg+xml;base64,", Barcode.ToDataUri("<svg/>"), StringComparison.Ordinal);
    }
}

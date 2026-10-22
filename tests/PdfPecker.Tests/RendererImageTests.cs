using System.Globalization;
using System.Text;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// Image XObjects, inline images, stream filters, soft masks and blend modes, rendered from hand-built PDFs. Images are
/// drawn at an integer scale so the centre of each source pixel can be sampled.
/// </summary>
public class RendererImageTests
{
    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Draws an image of <paramref name="w"/> x <paramref name="h"/> samples at <paramref name="scale"/> pixels per sample.</summary>
    private static RenderedImage DrawImage(int w, int h, string entries, byte[] data, int scale = 4, Action<FixturePdf>? setup = null,
        string prefix = "", string resources = "")
    {
        var pdf = new FixturePdf();
        setup?.Invoke(pdf);
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width {w} /Height {h} {entries}", data);
        var page = pdf.Build(w * scale, h * scale, $"{prefix} q {w * scale} 0 0 {h * scale} 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >> {resources}");
        return Render(page);
    }

    /// <summary>Asserts the colour at the centre of every source pixel; <paramref name="expected"/> is row-major from the top.</summary>
    private static void AssertGrid(RenderedImage img, int w, int h, int scale, (int R, int G, int B)[] expected, int tolerance = 4)
    {
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var e = expected[y * w + x];
            AssertColor(Px(img, x * scale + scale / 2, y * scale + scale / 2), e.R, e.G, e.B, tolerance, $"Sample ({x},{y}): ");
        }
    }

    private static readonly (int, int, int) Red = (255, 0, 0), Green = (0, 255, 0), Blue = (0, 0, 255), White = (255, 255, 255), Black = (0, 0, 0);

    // ---------------------------------------------------------------- sample formats, Decode and indexed images

    [Fact]
    public void Indexed_image_with_two_bit_samples_uses_the_palette()
    {
        // Rows are padded to whole bytes: four 2-bit samples per row.
        byte[] data = { 0b00_01_10_11, 0b11_10_01_00 };
        var img = DrawImage(4, 2, "/BitsPerComponent 2 /ColorSpace [/Indexed /DeviceRGB 3 <FF0000 00FF00 0000FF FFFFFF>]", data);
        AssertGrid(img, 4, 2, 4, new[] { Red, Green, Blue, White, White, Blue, Green, Red });
    }

    [Fact]
    public void Indexed_image_with_a_reversed_decode_array_flips_the_indices()
    {
        // The decode range is in sample units for an indexed image: [3 0] maps 0..3 to 3..0.
        byte[] data = { 0b00_01_10_11 };
        var img = DrawImage(4, 1, "/BitsPerComponent 2 /Decode [3 0] /ColorSpace [/Indexed /DeviceRGB 3 <FF0000 00FF00 0000FF FFFFFF>]", data);
        AssertGrid(img, 4, 1, 4, new[] { White, Blue, Green, Red });
    }

    [Fact]
    public void Indexed_image_with_cmyk_base_and_an_index_beyond_hival()
    {
        // Index 5 is past hival (1) and must clamp to the last palette entry instead of failing.
        byte[] data = { 0, 1, 5 };
        var img = DrawImage(3, 1, "/BitsPerComponent 8 /ColorSpace [/Indexed /DeviceGray 1 <00FF>]", data);
        AssertGrid(img, 3, 1, 4, new[] { Black, White, White });
    }

    [Fact]
    public void Sixteen_bit_rgb_samples_use_the_high_byte()
    {
        byte[] data = { 0xFF, 0xFF, 0x00, 0x00, 0x80, 0x00, /* second pixel */ 0x00, 0x00, 0xFF, 0xFF, 0x40, 0x00 };
        var img = DrawImage(2, 1, "/BitsPerComponent 16 /ColorSpace /DeviceRGB", data);
        AssertGrid(img, 2, 1, 4, new[] { (255, 0, 128), (0, 255, 64) });
    }

    [Fact]
    public void Sixteen_bit_gray_samples_scale_to_the_full_range()
    {
        byte[] data = { 0x00, 0x00, 0x40, 0x00, 0x80, 0x00, 0xFF, 0xFF };
        var img = DrawImage(4, 1, "/BitsPerComponent 16 /ColorSpace /DeviceGray", data);
        AssertGrid(img, 4, 1, 4, new[] { Black, (64, 64, 64), (128, 128, 128), White });
    }

    [Fact]
    public void Four_bit_gray_samples_are_unpacked_per_row()
    {
        byte[] data = { 0x0F, 0x80, /* row 2 */ 0xF0, 0x80 }; // three samples per row, padded to a byte
        var img = DrawImage(3, 2, "/BitsPerComponent 4 /ColorSpace /DeviceGray", data);
        AssertGrid(img, 3, 2, 4, new[] { Black, White, (136, 136, 136), White, Black, (136, 136, 136) });
    }

    [Fact]
    public void One_bit_gray_and_a_reversed_decode()
    {
        byte[] data = { 0b1010_0000 };
        var normal = DrawImage(4, 1, "/BitsPerComponent 1 /ColorSpace /DeviceGray", data);
        AssertGrid(normal, 4, 1, 4, new[] { White, Black, White, Black });
        var flipped = DrawImage(4, 1, "/BitsPerComponent 1 /ColorSpace /DeviceGray /Decode [1 0]", data);
        AssertGrid(flipped, 4, 1, 4, new[] { Black, White, Black, White });
    }

    [Fact]
    public void Rgb_decode_array_inverts_only_the_chosen_channel()
    {
        byte[] data = { 255, 0, 0, 0, 255, 128 };
        var img = DrawImage(2, 1, "/BitsPerComponent 8 /ColorSpace /DeviceRGB /Decode [1 0 0 1 0 1]", data);
        AssertGrid(img, 2, 1, 4, new[] { (0, 0, 0), (255, 255, 128) });
    }

    [Fact]
    public void Decode_array_can_narrow_the_range()
    {
        byte[] data = { 0, 255 };
        var img = DrawImage(2, 1, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Decode [0.25 0.75]", data);
        AssertGrid(img, 2, 1, 4, new[] { (64, 64, 64), (191, 191, 191) });
    }

    [Fact]
    public void Lab_image_decode_defaults_to_the_lab_ranges()
    {
        // L = 100 and a = b = 0 stored as 255, 128, 128; the default decode maps that to white.
        byte[] data = { 255, 128, 128, 0, 128, 128 };
        var img = DrawImage(2, 1, "/BitsPerComponent 8 /ColorSpace [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-128 127 -128 127] >>]", data);
        AssertGrid(img, 2, 1, 4, new[] { White, Black }, 4);
    }

    [Fact]
    public void Separation_image_applies_the_tint_transform_per_sample()
    {
        byte[] data = { 0, 128, 255 };
        var img = DrawImage(3, 1, "/BitsPerComponent 8 /ColorSpace [/Separation /Spot /DeviceRGB 5 0 R]", data, 4, pdf =>
            pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>"));
        AssertGrid(img, 3, 1, 4, new[] { White, (255, 127, 127), Red });
    }

    [Fact]
    public void Image_mask_paints_the_current_fill_color_where_samples_are_zero()
    {
        byte[] data = { 0b1010_0000 };
        var img = DrawImage(4, 1, "/ImageMask true /BitsPerComponent 1", data, 4, null, "0 0 1 rg");
        AssertGrid(img, 4, 1, 4, new[] { White, Blue, White, Blue });
        var flipped = DrawImage(4, 1, "/ImageMask true /BitsPerComponent 1 /Decode [1 0]", data, 4, null, "0 0 1 rg");
        AssertGrid(flipped, 4, 1, 4, new[] { Blue, White, Blue, White });
    }

    // ---------------------------------------------------------------- inline images

    private static RenderedImage Inline(string content, int w = 8, int h = 4) => FixturePdf.Render(w, h, content);

    [Fact]
    public void Inline_image_with_hex_data_and_abbreviated_keys()
    {
        var img = Inline("q 8 0 0 4 0 0 cm BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx ID FF0000 0000FF> EI Q");
        AssertAt(img, 2, 2, 255, 0, 0);
        AssertAt(img, 6, 2, 0, 0, 255);
    }

    [Fact]
    public void Inline_image_with_binary_data_and_gray_space()
    {
        var content = new StringBuilder("q 8 0 0 4 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 ID ");
        content.Append((char)0).Append((char)200).Append(" EI Q");
        var img = Inline(content.ToString());
        AssertAt(img, 2, 2, 0, 0, 0);
        AssertAt(img, 6, 2, 200, 200, 200);
    }

    [Fact]
    public void Inline_image_mask_uses_the_fill_color()
    {
        var content = new StringBuilder("0 1 0 rg q 8 0 0 4 0 0 cm BI /W 2 /H 1 /IM true /BPC 1 /D [1 0] ID ");
        content.Append((char)0b0100_0000).Append(" EI Q");
        var img = Inline(content.ToString());
        AssertAt(img, 2, 2, 255, 255, 255);
        AssertAt(img, 6, 2, 0, 255, 0);
    }

    [Fact]
    public void Inline_indexed_image_with_a_literal_lookup_table()
    {
        var img = Inline("q 8 0 0 4 0 0 cm BI /W 2 /H 1 /CS [/I /RGB 1 <00FF00FF0000>] /BPC 1 /F /AHx ID 40> EI Q");
        AssertAt(img, 2, 2, 0, 255, 0);
        AssertAt(img, 6, 2, 255, 0, 0);
    }

    [Fact]
    public void Inline_image_with_a_named_color_space_from_the_resources()
    {
        var pdf = new FixturePdf();
        var page = pdf.Build(8, 4, "q 8 0 0 4 0 0 cm BI /W 2 /H 1 /CS /Cs1 /BPC 8 /F /AHx ID 00FF> EI Q",
            "/ColorSpace << /Cs1 [/Indexed /DeviceRGB 1 <FF8000 0080FF>] >>");
        var img = Render(page);
        AssertAt(img, 2, 2, 255, 128, 0);
        AssertAt(img, 6, 2, 0, 128, 255);
    }

    // ---------------------------------------------------------------- stream filters

    private static byte[] Pattern(int count) => Enumerable.Range(0, count).Select(i => (byte)(i * 37 + (i >> 3) * 11)).ToArray();

    private static readonly byte[] RgbCells = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0, 10, 20, 30, 200, 100, 50, 0, 0, 0, 255, 255, 255 };

    private static void AssertRgbCells(RenderedImage img)
    {
        var expected = new (int, int, int)[8];
        for (int i = 0; i < 8; i++) expected[i] = (RgbCells[i * 3], RgbCells[i * 3 + 1], RgbCells[i * 3 + 2]);
        AssertGrid(img, 4, 2, 4, expected, 2);
    }

    [Fact]
    public void Asciihex_filter_decodes_an_image()
        => AssertRgbCells(DrawImage(4, 2, "/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /ASCIIHexDecode", TestEncoders.AsciiHex(RgbCells)));

    [Fact]
    public void Asciihex_filter_tolerates_whitespace_and_an_odd_final_digit()
    {
        var img = DrawImage(2, 1, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /AHx", Encoding.ASCII.GetBytes("00 F\n>"));
        AssertGrid(img, 2, 1, 4, new[] { Black, (240, 240, 240) });
    }

    [Fact]
    public void Ascii85_filter_decodes_an_image()
        => AssertRgbCells(DrawImage(4, 2, "/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /ASCII85Decode", TestEncoders.Ascii85(RgbCells)));

    [Fact]
    public void Ascii85_filter_handles_the_z_shortcut_and_a_partial_group()
    {
        byte[] data = { 0, 0, 0, 0, 255, 128 };
        var img = DrawImage(6, 1, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /A85", TestEncoders.Ascii85(data));
        AssertGrid(img, 6, 1, 4, new[] { Black, Black, Black, Black, White, (128, 128, 128) });
    }

    [Fact]
    public void Runlength_filter_decodes_literal_and_repeated_runs()
    {
        byte[] gray = { 0, 0, 0, 0, 0, 255, 100, 150, 255, 255, 255, 255 };
        var img = DrawImage(12, 1, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /RunLengthDecode", TestEncoders.RunLength(gray), 4);
        AssertGrid(img, 12, 1, 4, gray.Select(v => (int)v).Select(v => (v, v, v)).ToArray(), 1);
    }

    [Fact]
    public void Lzw_filter_decodes_an_image_across_code_width_changes_and_table_resets()
    {
        // 16384 noisy samples need more than 4094 table entries, so the encoder widens to 12 bits and resets the table.
        var rng = new Random(7);
        var gray = new byte[128 * 128];
        rng.NextBytes(gray);
        for (int i = 0; i < gray.Length; i += 5) gray[i] = (byte)(i / 64); // some repetition so the table is used
        var img = DrawImage(128, 128, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /LZWDecode", TestEncoders.Lzw(gray), 1);
        for (int y = 0; y < 128; y += 3)
        for (int x = 0; x < 128; x += 1)
        {
            int v = gray[y * 128 + x];
            AssertColor(Px(img, x, y), v, v, v, 1, $"Sample ({x},{y}): ");
        }
    }

    [Fact]
    public void Lzw_filter_with_early_change_zero()
    {
        var gray = new byte[64 * 64];
        new Random(3).NextBytes(gray);
        var img = DrawImage(64, 64, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /LZWDecode /DecodeParms << /EarlyChange 0 >>", TestEncoders.Lzw(gray, 0), 1);
        for (int y = 0; y < 64; y += 2)
        for (int x = 0; x < 64; x++)
        {
            int v = gray[y * 64 + x];
            AssertColor(Px(img, x, y), v, v, v, 1, $"Sample ({x},{y}): ");
        }
    }

    [Fact]
    public void Filters_can_be_chained_in_an_array()
    {
        var stage = TestEncoders.Ascii85(TestEncoders.Deflate(TestEncoders.RunLength(RgbCells)));
        AssertRgbCells(DrawImage(4, 2, "/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter [/ASCII85Decode /FlateDecode /RunLengthDecode]", stage));
    }

    [Fact]
    public void Flate_filter_with_a_png_up_predictor()
    {
        // Each row is stored as filter type 2 (Up) followed by the difference to the row above.
        var rows = new[] { new byte[] { 10, 20, 30, 40 }, new byte[] { 15, 25, 35, 45 }, new byte[] { 255, 0, 255, 0 } };
        var raw = new List<byte>();
        byte[] prev = new byte[4];
        foreach (var row in rows)
        {
            raw.Add(2);
            for (int i = 0; i < 4; i++) raw.Add((byte)(row[i] - prev[i]));
            prev = row;
        }
        var img = DrawImage(4, 3, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode /DecodeParms << /Predictor 12 /Colors 1 /Columns 4 >>",
            TestEncoders.Deflate(raw.ToArray()));
        AssertGrid(img, 4, 3, 4, rows.SelectMany(r => r.Select(v => ((int)v, (int)v, (int)v))).ToArray(), 1);
    }

    [Fact]
    public void Flate_filter_with_a_tiff_predictor()
    {
        byte[] row = { 10, 30, 20, 120 };
        byte[] diff = { 10, 20, 246, 100 }; // horizontal differences, modulo 256
        var img = DrawImage(4, 1, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode /DecodeParms << /Predictor 2 /Colors 1 /Columns 4 >>",
            TestEncoders.Deflate(diff));
        AssertGrid(img, 4, 1, 4, row.Select(v => ((int)v, (int)v, (int)v)).ToArray(), 1);
    }

    // ---------------------------------------------------------------- CCITT fax

    private static readonly string[] FaxPattern =
    {
        "##########",
        "..........",
        "..##..##..",
        "#.#.#.#.#.",
        ".########.",
        "#........#",
        "...####...",
        "..........",
    };

    private static bool[][] FaxRows() => FaxPattern.Select(r => r.Select(c => c == '#').ToArray()).ToArray();

    private static void AssertFax(byte[] data, string parms, bool blackIs1 = false)
    {
        var img = DrawImage(10, 8, $"/BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /CCITTFaxDecode /DecodeParms << /Columns 10 /Rows 8 {parms} >>", data, 4);
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 10; x++)
        {
            // With BlackIs1 the black pixels are 1 bits, which a DeviceGray image shows as white.
            bool black = (FaxPattern[y][x] == '#') != blackIs1;
            int v = black ? 0 : 255;
            AssertColor(Px(img, x * 4 + 2, y * 4 + 2), v, v, v, 2, $"Pixel ({x},{y}): ");
        }
    }

    [Fact]
    public void Ccitt_group4_decodes_a_bitmap() => AssertFax(TestEncoders.Ccitt(FaxRows(), -1), "/K -1");

    [Fact]
    public void Ccitt_group4_with_blackis1_inverts_the_samples() => AssertFax(TestEncoders.Ccitt(FaxRows(), -1), "/K -1 /BlackIs1 true", true);

    [Fact]
    public void Ccitt_group4_with_byte_aligned_rows() => AssertFax(TestEncoders.Ccitt(FaxRows(), -1, byteAlign: true), "/K -1 /EncodedByteAlign true");

    [Fact]
    public void Ccitt_group3_one_dimensional_decodes_a_bitmap() => AssertFax(TestEncoders.Ccitt(FaxRows(), 0), "/K 0");

    [Fact]
    public void Ccitt_group3_one_dimensional_with_eol_codes() => AssertFax(TestEncoders.Ccitt(FaxRows(), 0, eols: true), "/K 0");

    [Fact]
    public void Ccitt_group3_one_dimensional_with_byte_aligned_rows() => AssertFax(TestEncoders.Ccitt(FaxRows(), 0, byteAlign: true), "/K 0 /EncodedByteAlign true");

    [Fact]
    public void Ccitt_group3_two_dimensional_mixes_one_and_two_dimensional_rows() => AssertFax(TestEncoders.Ccitt(FaxRows(), 4, eols: true), "/K 4");

    [Fact]
    public void Ccitt_image_mask_paints_black_pixels_in_the_fill_color()
    {
        var img = DrawImage(10, 8, "/ImageMask true /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /Columns 10 /Rows 8 /K -1 >>",
            TestEncoders.Ccitt(FaxRows(), -1), 4, null, "1 0 0 rg");
        // A stencil paints where the sample is 0, which is black in fax data.
        AssertAt(img, 2, 8 * 4 - 2, 255, 0, 0); // row 0 is entirely black
        AssertAt(img, 2, 8 * 4 - 6, 255, 255, 255); // row 1 is white
    }

    [Fact]
    public void Ccitt_rows_missing_from_the_data_are_white()
    {
        // The data holds 8 rows but the image is 12 high: rows that were never decoded must not come out black.
        var img = DrawImage(10, 12, "/BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /CCITTFaxDecode /DecodeParms << /Columns 10 /Rows 12 /K -1 >>",
            TestEncoders.Ccitt(FaxRows(), -1), 4);
        AssertColor(Px(img, 2, 2), 0, 0, 0, 2, "Row 0: ");
        for (int y = 8; y < 12; y++) AssertColor(Px(img, 18, y * 4 + 2), 255, 255, 255, 2, $"Row {y}: ");
    }

    // ---------------------------------------------------------------- DCT with an Adobe marker

    [Fact]
    public void Dct_cmyk_with_an_adobe_marker_and_a_decode_array_gives_the_right_inks()
    {
        // Photoshop-style CMYK JPEGs store each ink as 255 minus its value and the PDF carries /Decode [1 0 1 0 1 0 1 0].
        var black = TestEncoders.FlatJpeg(new byte[] { 255, 255, 255, 0 }, 0);
        var cyan = TestEncoders.FlatJpeg(new byte[] { 0, 255, 255, 255 }, 0);
        var white = TestEncoders.FlatJpeg(new byte[] { 255, 255, 255, 255 }, 0);
        var pdf = new FixturePdf();
        string entries = "/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceCMYK /Filter /DCTDecode /Decode [1 0 1 0 1 0 1 0]";
        int a = pdf.AddStream(entries, black), b = pdf.AddStream(entries, cyan), c = pdf.AddStream(entries, white);
        var page = pdf.Build(24, 8, "q 8 0 0 8 0 0 cm /A Do Q q 8 0 0 8 8 0 cm /B Do Q q 8 0 0 8 16 0 cm /C Do Q",
            $"/XObject << /A {a} 0 R /B {b} 0 R /C {c} 0 R >>");
        var img = Render(page);
        var k = At(img, 4, 4);
        Assert.True(k.R < 80 && k.G < 80 && k.B < 80, $"Full K should be dark, got {k}");
        var cy = At(img, 12, 4);
        Assert.True(cy.R < 40 && cy.B > 180, $"Full cyan should be cyan, got {cy}");
        AssertAt(img, 20, 4, 255, 255, 255, 3);
    }

    [Fact]
    public void Dct_cmyk_without_a_decode_array_is_not_inverted()
    {
        // The filter returns the stored values; only a Decode array turns them around. All four inks at 1 is near black.
        var jpeg = TestEncoders.FlatJpeg(new byte[] { 255, 255, 255, 255 }, 0);
        var img = DrawImage(8, 8, "/BitsPerComponent 8 /ColorSpace /DeviceCMYK /Filter /DCTDecode", jpeg, 1);
        var p = At(img, 4, 4);
        Assert.True(p.R < 40 && p.G < 40 && p.B < 40, $"Stored 255 in every channel is full ink, got {p}");
    }

    [Fact]
    public void Dct_rgb_and_gray_flat_blocks_decode_to_their_levels()
    {
        var rgb = TestEncoders.FlatJpeg(new byte[] { 200, 100, 50 }, 0); // Adobe transform 0: stored as RGB, not YCbCr
        var gray = TestEncoders.FlatJpeg(new byte[] { 77 }, null);
        var pdf = new FixturePdf();
        int a = pdf.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode", rgb);
        int b = pdf.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /DCTDecode", gray);
        var img = Render(pdf.Build(16, 8, "q 8 0 0 8 0 0 cm /A Do Q q 8 0 0 8 8 0 cm /B Do Q", $"/XObject << /A {a} 0 R /B {b} 0 R >>"));
        AssertAt(img, 4, 4, 200, 100, 50, 4);
        AssertAt(img, 12, 4, 77, 77, 77, 4);
    }

    // ---------------------------------------------------------------- image masks and soft masks

    private static byte[] Uniform(int count, params byte[] sample) => Enumerable.Range(0, count).SelectMany(_ => sample).ToArray();

    [Fact]
    public void Smask_alpha_blends_the_image_with_the_page()
    {
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", new byte[] { 255, 128 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {mask} 0 R", Uniform(2, 255, 0, 0));
        var img = Render(pdf.Build(8, 4, "q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 255, 0, 0);
        AssertAt(img, 6, 2, 255, 127, 127);
    }

    [Fact]
    public void Smask_with_a_different_resolution_is_stretched_over_the_image()
    {
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 1 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray", new byte[] { 255, 0 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {mask} 0 R", Uniform(16, 0, 0, 255));
        var img = Render(pdf.Build(8, 8, "q 8 0 0 8 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 4, 6, 0, 0, 255); // top half: opaque
        AssertAt(img, 4, 2, 255, 255, 255); // bottom half: transparent
    }

    [Fact]
    public void Color_key_mask_hides_samples_in_the_range()
    {
        var pdf = new FixturePdf();
        int im = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Mask [250 255 0 5 0 5]",
            new byte[] { 255, 0, 0, 0, 0, 255 });
        var img = Render(pdf.Build(8, 4, "q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 255, 255, 255); // pure red is keyed out
        AssertAt(img, 6, 2, 0, 0, 255);
    }

    [Theory]
    [InlineData(0, new byte[] { 128, 0, 0 })]     // red at alpha one half pre-blended with black
    [InlineData(1, new byte[] { 255, 127, 127 })] // the same red pre-blended with white
    public void Smask_matte_un_blends_the_pre_multiplied_colors(int matte, byte[] stored)
    {
        // With /Matte the samples are c' = m + alpha * (c - m); the reader recovers c before compositing, so both images
        // are full red at half alpha and give the same result over a black page (ISO 32000-1, 11.6.5.3).
        var pdf = new FixturePdf();
        int mask = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Matte [{matte} {matte} {matte}]",
            new byte[] { 128 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {mask} 0 R", stored);
        var img = Render(pdf.Build(4, 4, "0 g 0 0 4 4 re f q 4 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 128, 0, 0, 2);
    }

    [Fact]
    public void Smask_matte_leaves_fully_transparent_samples_alone()
    {
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Matte [1 1 1]", new byte[] { 0, 255 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {mask} 0 R", Uniform(2, 0, 0, 255));
        var img = Render(pdf.Build(8, 4, "0 g 0 0 8 4 re f q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 0, 0, 0, 2);
        AssertAt(img, 6, 2, 0, 0, 255, 2);
    }

    [Fact]
    public void Explicit_mask_shows_the_base_image_where_mask_samples_are_zero()
    {
        // An explicit mask's samples mean what an image mask's do: with the default Decode [0 1], 0 marks the area to
        // paint and 1 masks it out (ISO 32000-1, 8.9.6.2 and 8.9.6.3).
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ImageMask true /BitsPerComponent 1", new byte[] { 0b0100_0000 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Mask {mask} 0 R", Uniform(2, 255, 0, 0));
        var img = Render(pdf.Build(8, 4, "q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 255, 0, 0); // sample 0: painted
        AssertAt(img, 6, 2, 255, 255, 255); // sample 1: masked out
    }

    [Fact]
    public void Explicit_mask_finer_than_its_image_keeps_its_resolution()
    {
        // One red pixel under a 4x1 stencil: the image is drawn at the mask's resolution, so each quarter shows or
        // hides on its own instead of the whole pixel following one mask sample.
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 1 /ImageMask true /BitsPerComponent 1", new byte[] { 0b0101_0000 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Mask {mask} 0 R", Uniform(1, 255, 0, 0));
        var img = Render(pdf.Build(16, 4, "q 16 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertGrid(img, 4, 1, 4, new[] { Red, White, Red, White });
    }

    [Fact]
    public void Smask_finer_than_its_image_keeps_its_resolution()
    {
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 4 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray", new byte[] { 255, 0, 0, 255 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {mask} 0 R", new byte[] { 255, 0, 0, 0, 0, 255 });
        var img = Render(pdf.Build(16, 4, "q 16 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertGrid(img, 4, 1, 4, new[] { Red, White, White, Blue });
    }

    [Fact]
    public void Explicit_mask_with_a_reversed_decode_array_flips_the_mask()
    {
        var pdf = new FixturePdf();
        int mask = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ImageMask true /BitsPerComponent 1 /Decode [1 0]", new byte[] { 0b0100_0000 });
        int im = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Mask {mask} 0 R", Uniform(2, 255, 0, 0));
        var img = Render(pdf.Build(8, 4, "q 8 0 0 4 0 0 cm /Im Do Q", $"/XObject << /Im {im} 0 R >>"));
        AssertAt(img, 2, 2, 255, 255, 255);
        AssertAt(img, 6, 2, 255, 0, 0);
    }

    // ---------------------------------------------------------------- soft-mask groups

    private static RenderedImage SoftMask(string mask, string groupContent, string groupCs = "/DeviceGray", Action<FixturePdf>? setup = null, string fill = "1 0 0 rg 0 0 40 40 re f")
    {
        var pdf = new FixturePdf();
        setup?.Invoke(pdf);
        int form = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 40 40] /Group << /S /Transparency /CS {groupCs} >>", groupContent);
        int gs = pdf.Add($"<< /Type /ExtGState /SMask << /Type /Mask {mask.Replace("FORM", $"{form} 0 R")} >> >>");
        return Render(pdf.Build(40, 40, $"/GS gs {fill}", $"/ExtGState << /GS {gs} 0 R >>"));
    }

    [Fact]
    public void Luminosity_soft_mask_group_scales_the_alpha_of_later_painting()
    {
        var img = SoftMask("/S /Luminosity /G FORM", "0.5 g 0 0 20 40 re f");
        AssertAt(img, 10, 20, 255, 128, 128); // luminosity 0.5 gives half alpha
        AssertAt(img, 30, 20, 255, 255, 255); // outside the group the default black backdrop hides everything
    }

    [Fact]
    public void Luminosity_soft_mask_backdrop_color_applies_outside_the_painted_area()
    {
        var img = SoftMask("/S /Luminosity /G FORM /BC [1]", "0.5 g 0 0 20 40 re f");
        AssertAt(img, 10, 20, 255, 128, 128);
        AssertAt(img, 30, 20, 255, 0, 0); // white backdrop: fully opaque
    }

    [Fact]
    public void Luminosity_soft_mask_weights_rgb_components_like_the_spec()
    {
        // Lum = 0.3 R + 0.59 G + 0.11 B, so pure green gives alpha 0.59.
        var img = SoftMask("/S /Luminosity /G FORM", "0 1 0 rg 0 0 20 40 re f", "/DeviceRGB");
        AssertAt(img, 10, 20, 255, 105, 105, 6);
    }

    [Fact]
    public void Alpha_soft_mask_group_uses_coverage_not_color()
    {
        var img = SoftMask("/S /Alpha /G FORM", "0 g 0 0 20 40 re f");
        AssertAt(img, 10, 20, 255, 0, 0); // opaque black still has alpha 1
        AssertAt(img, 30, 20, 255, 255, 255);
    }

    [Fact]
    public void Soft_mask_transfer_function_remaps_the_mask_values()
    {
        var img = SoftMaskWithTransfer();
        AssertAt(img, 10, 20, 255, 64, 64); // 0.25 inverted to 0.75
    }

    private static RenderedImage SoftMaskWithTransfer()
    {
        var pdf = new FixturePdf();
        int tr = pdf.Add("<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>");
        int form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 40 40] /Group << /S /Transparency /CS /DeviceGray >>", "0.25 g 0 0 20 40 re f");
        int gs = pdf.Add($"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G {form} 0 R /TR {tr} 0 R /BC [0] >> >>");
        return Render(pdf.Build(40, 40, "/GS gs 1 0 0 rg 0 0 40 40 re f", $"/ExtGState << /GS {gs} 0 R >>"));
    }

    [Fact]
    public void Soft_mask_none_removes_an_active_mask()
    {
        var pdf = new FixturePdf();
        int form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 40 40] /Group << /S /Transparency /CS /DeviceGray >>", "0.5 g 0 0 40 40 re f");
        int gs = pdf.Add($"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G {form} 0 R >> >>");
        int off = pdf.Add("<< /Type /ExtGState /SMask /None >>");
        var img = Render(pdf.Build(40, 40, "/GS gs 1 0 0 rg 0 0 20 40 re f /Off gs 0 0 1 rg 20 0 20 40 re f",
            $"/ExtGState << /GS {gs} 0 R /Off {off} 0 R >>"));
        AssertAt(img, 10, 20, 255, 128, 128);
        AssertAt(img, 30, 20, 0, 0, 255);
    }

    [Fact]
    public void Soft_mask_is_positioned_by_the_ctm_in_effect_when_it_was_set()
    {
        var pdf = new FixturePdf();
        int form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Group << /S /Transparency /CS /DeviceGray >>", "1 g 0 0 20 20 re f");
        int gs = pdf.Add($"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G {form} 0 R >> >>");
        var img = Render(pdf.Build(40, 40, "1 0 0 1 20 20 cm /GS gs 0 0 1 rg -20 -20 40 40 re f", $"/ExtGState << /GS {gs} 0 R >>"));
        AssertAt(img, 30, 30, 0, 0, 255); // inside the translated 20x20 mask
        AssertAt(img, 10, 10, 255, 255, 255); // inside the fill but outside the mask
    }

    // ---------------------------------------------------------------- blend modes

    private static readonly double[] Backdrop = { 0.8, 0.4, 0.2 }, Source = { 0.3, 0.6, 0.9 };

    private static double Separable(string mode, double cb, double cs)
    {
        static double HardLight(double b, double s) => s <= 0.5 ? b * 2 * s : b + (2 * s - 1) - b * (2 * s - 1);
        switch (mode)
        {
            case "Normal": return cs;
            case "Multiply": return cb * cs;
            case "Screen": return cb + cs - cb * cs;
            case "Overlay": return HardLight(cs, cb);
            case "Darken": return Math.Min(cb, cs);
            case "Lighten": return Math.Max(cb, cs);
            case "ColorDodge": return cb == 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs));
            case "ColorBurn": return cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs);
            case "HardLight": return HardLight(cb, cs);
            case "SoftLight":
            {
                if (cs <= 0.5) return cb - (1 - 2 * cs) * cb * (1 - cb);
                double d = cb <= 0.25 ? ((16 * cb - 12) * cb + 4) * cb : Math.Sqrt(cb);
                return cb + (2 * cs - 1) * (d - cb);
            }
            case "Difference": return Math.Abs(cb - cs);
            case "Exclusion": return cb + cs - 2 * cb * cs;
        }
        throw new ArgumentException(mode);
    }

    private static double Lum(double[] c) => 0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2];

    private static double[] ClipColor(double[] c)
    {
        double l = Lum(c), n = c.Min(), x = c.Max();
        var r = (double[])c.Clone();
        if (n < 0) for (int i = 0; i < 3; i++) r[i] = l + (r[i] - l) * l / (l - n);
        if (x > 1) for (int i = 0; i < 3; i++) r[i] = l + (r[i] - l) * (1 - l) / (x - l);
        return r;
    }

    private static double[] SetLum(double[] c, double l)
    {
        double d = l - Lum(c);
        return ClipColor(new[] { c[0] + d, c[1] + d, c[2] + d });
    }

    private static double Sat(double[] c) => c.Max() - c.Min();

    private static double[] SetSat(double[] c, double s)
    {
        var order = Enumerable.Range(0, 3).OrderBy(i => c[i]).ToArray();
        int min = order[0], mid = order[1], max = order[2];
        var r = new double[3];
        if (c[max] > c[min])
        {
            r[mid] = (c[mid] - c[min]) * s / (c[max] - c[min]);
            r[max] = s;
        }
        return r;
    }

    private static double[] NonSeparable(string mode) => mode switch
    {
        "Hue" => SetLum(SetSat(Source, Sat(Backdrop)), Lum(Backdrop)),
        "Saturation" => SetLum(SetSat(Backdrop, Sat(Source)), Lum(Backdrop)),
        "Color" => SetLum(Source, Lum(Backdrop)),
        "Luminosity" => SetLum(Backdrop, Lum(Source)),
        _ => throw new ArgumentException(mode),
    };

    [Theory]
    [InlineData("Normal")]
    [InlineData("Multiply")]
    [InlineData("Screen")]
    [InlineData("Overlay")]
    [InlineData("Darken")]
    [InlineData("Lighten")]
    [InlineData("ColorDodge")]
    [InlineData("ColorBurn")]
    [InlineData("HardLight")]
    [InlineData("SoftLight")]
    [InlineData("Difference")]
    [InlineData("Exclusion")]
    [InlineData("Hue")]
    [InlineData("Saturation")]
    [InlineData("Color")]
    [InlineData("Luminosity")]
    public void Blend_mode_matches_the_specification_formula(string mode)
    {
        var pdf = new FixturePdf();
        int gs = pdf.Add($"<< /Type /ExtGState /BM /{mode} >>");
        string content = $"{F(Backdrop[0])} {F(Backdrop[1])} {F(Backdrop[2])} rg 0 0 40 40 re f /GS gs {F(Source[0])} {F(Source[1])} {F(Source[2])} rg 10 10 20 20 re f";
        var img = Render(pdf.Build(40, 40, content, $"/ExtGState << /GS {gs} 0 R >>"));
        AssertAt(img, 5, 5, 204, 102, 51, 2); // the backdrop alone
        double[] expected = mode is "Hue" or "Saturation" or "Color" or "Luminosity"
            ? NonSeparable(mode)
            : Enumerable.Range(0, 3).Select(i => Separable(mode, Backdrop[i], Source[i])).ToArray();
        AssertAt(img, 20, 20, (int)Math.Round(expected[0] * 255), (int)Math.Round(expected[1] * 255), (int)Math.Round(expected[2] * 255), 4);
    }

    [Fact]
    public void Blend_mode_array_uses_the_first_mode_the_reader_recognises()
    {
        // An array lets a writer name a newer mode followed by fallbacks; names the reader does not know are skipped
        // (ISO 32000-1, 11.6.3).
        var pdf = new FixturePdf();
        int gs = pdf.Add("<< /Type /ExtGState /BM [/NoSuchMode /Multiply /Screen] >>");
        var img = Render(pdf.Build(40, 40, "0.8 0.4 0.2 rg 0 0 40 40 re f /GS gs 0.3 0.6 0.9 rg 10 10 20 20 re f", $"/ExtGState << /GS {gs} 0 R >>"));
        AssertAt(img, 20, 20, (int)Math.Round(0.24 * 255), (int)Math.Round(0.24 * 255), (int)Math.Round(0.18 * 255), 4);
    }

    [Fact]
    public void Blend_mode_with_partial_alpha_mixes_the_blend_result_with_the_backdrop()
    {
        var pdf = new FixturePdf();
        int gs = pdf.Add("<< /Type /ExtGState /BM /Multiply /ca 0.5 >>");
        var img = Render(pdf.Build(40, 40, "0.8 0.4 0.2 rg 0 0 40 40 re f /GS gs 0.3 0.6 0.9 rg 10 10 20 20 re f", $"/ExtGState << /GS {gs} 0 R >>"));
        // Multiply gives (0.24, 0.24, 0.18); half of that over the backdrop.
        AssertAt(img, 20, 20, (int)Math.Round((0.8 + 0.24) / 2 * 255), (int)Math.Round((0.4 + 0.24) / 2 * 255), (int)Math.Round((0.2 + 0.18) / 2 * 255), 4);
    }

}

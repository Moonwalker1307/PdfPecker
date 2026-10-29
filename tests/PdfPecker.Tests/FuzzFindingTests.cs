using System.Text;
using PdfPecker.Rendering.Images;
using static PdfPecker.Tests.PdfTestHelpers;

namespace PdfPecker.Tests;

/// <summary>
/// Inputs found by the fuzzer (fuzz/PdfPecker.Fuzz) that escaped as exceptions the public API does not document, or
/// that exhausted memory. Malformed input may only fail with the documented exceptions or be tolerated.
/// </summary>
/// <remarks>
/// The damaged fonts are given as the few bytes the fuzzer changed in a fixture font, which keeps them small and shows
/// what was damaged; each one reproduces the crash it was found with.
/// </remarks>
[Collection(HostileInputCollection.Name)]
public class FuzzFindingTests
{
    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    private static byte[] Html(string html, PdfOptions? options = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return HtmlToPdf.Convert(html, options, cts.Token);
    }

    // ------------------------------------------------------------------ HTML and Markdown

    [Theory]
    [InlineData("<style>@page{margin:483")] // html, exception-8f292e15
    [InlineData("<style>@page{margin:553")] // html, exception-214517f5
    [InlineData("<style>@page{size:1px 1px;margin:1in}</style>x")]
    [InlineData("<style>@page{margin:1e30pt}</style>x")]
    [InlineData("<style>@page{margin:1e39pt}</style>x")]
    [InlineData("<style>@page{margin-left:calc(0/0);margin-right:9in}</style>x")]
    [InlineData("<style>@page :first{margin-left:900pt}</style>x")]
    [InlineData("<style>@page wide{margin:0 600pt} div{page:wide}</style><div>x</div>")]
    public void Css_page_margins_that_leave_no_room_are_fitted_to_the_page(string html)
    {
        var pdf = Html(html);
        AssertStructurallyValid(pdf);
        // The content area left is the minimum of 10pt, so a line of text may take a page or two, but no more.
        Assert.InRange(PageCount(pdf), 1, 3);
    }

    [Fact]
    public void Caller_margins_that_leave_no_room_still_throw()
    {
        var options = new PdfOptions { Margins = new PageMargins(400) };
        Assert.Throws<ArgumentException>(() => Html("<p>x</p>", options));
    }

    [Fact]
    public void Markdown_table_delimiter_row_of_a_lone_colon_is_text()
    {
        // markdown, exception-89e7142b: "|" then ":" sliced a cell with a negative length.
        var html = Bounded(() => MarkdownToHtml.ConvertDocument("|\n:"));
        Assert.DoesNotContain("<table", html);
        Assert.Contains(":", html);
    }

    // ------------------------------------------------------------------ PDF

    [Theory]
    [InlineData("\n1 0 obj<</Type/Catalog")] // pdf-text, exception-349a9083
    [InlineData("trailer<</Root 0")] // pdf-text, exception-d981583a
    public void Text_of_a_pdf_without_pages_is_empty(string pdf)
    {
        var bytes = Encoding.Latin1.GetBytes(pdf);
        Assert.Empty(Bounded(() => PdfText.ExtractPages(bytes)));
        Assert.Equal("", Bounded(() => PdfText.Extract(bytes)));
        // A page chosen by the caller that the PDF does not have is still the caller's documented error, as before.
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfText.Extract(bytes, new PdfTextOptions { Pages = PdfPageRange.Parse("1") }));
    }

    [Theory]
    [InlineData("\n1 0 obj<</Type/Catalog/Pages 2 0 R/2 0 obj<<<</Annots[11 0 R\n11 0<</Rect[0 0 0 0]/AP<</N 12 0 R\n12 0 obj<<///BBox[stream")] // pdf-text, exception-0a769117
    [InlineData("\n1 0 obj<</Type/Catalog/Pages 2 0 R/2 0 obj<<<</Annots[11 0 R\n11 0<</Rect[0 0 0 0]/AP<</N 12 0 R>12 0 obj<<///BBox[stream")] // pdf-render, exception-0af8d9c8
    [InlineData("%PDF-1.7\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj "
        + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 50 50]/Annots[<</Subtype/Stamp/Rect[0 0 40 40]/AP<</N 4 0 R>>>>]>>endobj "
        + "4 0 obj<</Type/XObject/Subtype/Form/BBox[]/Length 13>>stream\n0 0 10 10 re f\nendstream endobj trailer<</Root 1 0 R>>")]
    public void Annotation_with_an_empty_rect_or_a_short_bbox_is_tolerated(string pdf)
    {
        var bytes = Encoding.Latin1.GetBytes(pdf);
        Bounded(() => PdfText.Extract(bytes));
        var renderer = PdfRenderer.Open(bytes);
        for (int i = 0; i < renderer.PageCount; i++)
            Assert.NotEmpty(Bounded(() => renderer.RenderPageToPng(i, new ImageOptions { Dpi = 72 })));
    }

    // ------------------------------------------------------------------ images

    [Fact]
    public void Jpeg_with_a_truncated_frame_header_decodes_to_nothing()
    {
        // jpeg, exception-f66ff4c1: SOF0 of three components cut off after the first byte of the first one, which left
        // the components half built for the partial output of a damaged file.
        var jpeg = Convert.FromBase64String("/9j/wAARCAAApQMB");
        Assert.Null(Bounded(() => JpegDecoder.Decode(jpeg)));
    }

    [Fact]
    public void Jpeg_frame_header_over_the_pixel_cap_keeps_the_earlier_frame_size()
    {
        // jpeg, memory-d130ecb3: a 320 x 160 frame header, then a second one cut off after a size of 10573 x 60501.
        // The refused size replaced the first one, and the partial output of the first frame was allocated at it.
        var jpeg = Convert.FromBase64String("/9j/wAARCACgAUADHh4eHh4eHh4e/8AAEQjsVSlN");
        long allocated = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var image = JpegDecoder.Decode(jpeg);
            if (image is { } i) Assert.Equal((320, 160), (i.Width, i.Height));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.True(allocated < 16 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Jpeg_scans_stop_where_their_data_runs_out()
    {
        // jpeg, timeout-522fb4dc: a large frame with many scans of a few bytes each. Past its data a scan read zero bits
        // to the last block of the frame, because the zeros supplied at the marker counted as data.
        var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "rgb.jpg"));
        int scan = fixture.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        var jpeg = new MemoryStream();
        jpeg.Write(fixture, 0, scan);
        var bytes = jpeg.GetBuffer();
        int frame = fixture.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        bytes[frame + 5] = bytes[frame + 7] = 2000 >> 8; // 2000 x 2000 pixels
        bytes[frame + 6] = bytes[frame + 8] = 2000 & 0xFF;
        for (int i = 0; i < 2000; i++)
        {
            jpeg.Write(fixture, scan, 14);
            jpeg.Write(new byte[] { 0x12, 0x34 });
        }
        jpeg.Write(new byte[] { 0xFF, 0xD9 });
        var image = Bounded(() => JpegDecoder.Decode(jpeg.ToArray()));
        Assert.Equal((2000, 2000), (image!.Value.Width, image.Value.Height));
    }

    [Fact]
    public void Jbig2_halftone_region_larger_than_the_image_is_not_allocated()
    {
        // jbig2, memory-8128463a: the fuzzer's framing is width, height and globals length, then the stream. The
        // halftone region's header asked for a grid and region of gigabytes on a 160 x 100 image.
        var input = Convert.FromBase64String(
            "oAAAAGQAAAAAAAAAAAAAADAAAQAAABMAAPygAAAAZAAAAAAAAAAAAAAAAAAAARAAAQAAABQABAQAAAAHUA9I0AuYoyyE7AD/rAAAAAIWIAEBAAAAUQAAAIIAAL4+IJnGRY1//y8idPwSbtXfiJ9a/6wAAAADMQABAAAA");
        int width = BitConverter.ToInt32(input, 0), height = BitConverter.ToInt32(input, 4);
        long allocated = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Jbig2Decoder.Decode(input[12..], null, width, height);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.True(allocated < 16 << 20, $"Allocated {allocated:N0} bytes");
    }

    // ------------------------------------------------------------------ fonts

    private const string FontPage =
        "<!doctype html><body style=\"font-family:Fz;font-size:18px\"><p>Hamburgefonstiv 0123456789 fi ffl AV To Wa العربية שלום कि</p>"
        + "<p style=\"font-feature-settings:'smcp','liga','kern'\">Ligatures and kerning: office waffle</p></body>";

    private static byte[] Patched(string font, params (int Offset, byte Value)[] patches)
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", font));
        foreach (var (offset, value) in patches) data[offset] = value;
        return data;
    }

    public static TheoryData<string, string, (int, byte)[]> DamagedLayoutTables => new()
    {
        // The GSUB or GPOS script list, a language system or the feature list runs past the end of the font: these
        // were read while planning the shaping, outside the lookups' own error handling.
        { "font-woff exception-07ed398f (LangSys)", "WorkSans-Regular.woff", new (int, byte)[] { (0x262, 0xD8), (0x15DB, 0x0F), (0x20D8, 0xD6), (0x370C, 0xF9) } },
        { "font-woff exception-5517abd9 (FeatureLookups U32)", "WorkSans-Regular.woff", new (int, byte)[] { (0x60C, 0xD5), (0x2CFD, 0x92), (0x3732, 0xFF), (0x3733, 0xFF), (0x3734, 0xFF), (0x3735, 0xFF) } },
        { "font-woff exception-4d6da9b6 (FeatureLookups U16)", "WorkSans-Regular.woff", new (int, byte)[]
            {
                (0x1350, 0x6D), (0x13AF, 0), (0x13B0, 0), (0x13B1, 0), (0x13B2, 0), (0x13B3, 0), (0x13B4, 0), (0x13B5, 0), (0x13B6, 0), (0x13B7, 0),
                (0x13B8, 0), (0x13B9, 0), (0x13BA, 0), (0x13BB, 0), (0x13BC, 0), (0x13BD, 0), (0x13BE, 0), (0x370D, 0xFF), (0x370E, 0xFF), (0x370F, 0xFF), (0x3710, 0xFF),
            } },
        { "font-woff2 exception-c5822343 (LangSys U32)", "WorkSans-Regular.woff2", new (int, byte)[] { (0x1EE, 0xA7) } },
        // The GDEF glyph class definition runs past the end of the font: it is read for every glyph.
        { "font-woff exception-8c06bdda (GlyphProps)", "WorkSans-Regular.woff", new (int, byte)[] { (0x1536, 0x73), (0x15A7, 0x80), (0x15A8, 0), (0x1DBE, 0x6D), (0x36D8, 0xEE) } },
    };

    [Theory]
    [MemberData(nameof(DamagedLayoutTables))]
    public void Font_with_damaged_layout_tables_is_drawn_without_them(string finding, string font, (int, byte)[] patches)
    {
        _ = finding;
        var fonts = new FontCollection().Add(Patched(font, patches), "Fz");
        var pdf = Html(FontPage, new PdfOptions { Fonts = fonts });
        AssertStructurallyValid(pdf);
        Assert.Contains("Hamburgefonstiv", PdfText.Extract(pdf));
    }

    [Fact]
    public void Font_with_a_truncated_hhea_table_is_refused()
    {
        // font-woff, exception-1582580b: the hhea table's length is cut below its fixed 36 bytes, so the subsetter's
        // write of the glyph count went past the copy of it.
        var data = Patched("WorkSans-Regular.woff", (0xD3, 0), (0xD7, 0), (0xDB, 0), (0xDC, 0), (0xDD, 0), (0xDE, 0), (0x12D0, 0x86),
            (0x25F3, 0), (0x25F4, 0), (0x2902, 0x01), (0x2903, 0));
        Assert.Throws<FormatException>(() => new FontCollection().Add(data, "Fz"));
        // Through @font-face the damaged font is skipped and the text falls back to another font.
        var pdf = Html("<style>@font-face{font-family:Fz;src:url(data:font/woff;base64," + Convert.ToBase64String(data) + ")}</style>"
            + "<p style='font-family:Fz'>Fallback</p>");
        Assert.Contains("Fallback", PdfText.Extract(pdf));
    }

    [Fact]
    public void Woff_with_corrupt_zlib_data_is_a_format_error()
    {
        // font-woff, exception-52b2796c: the runtime reports some corrupt zlib data with ZLibException, an IOException.
        var data = Patched("WorkSans-Regular.woff");
        int numTables = data[12] << 8 | data[13];
        for (int i = 0; i < numTables; i++)
        {
            int e = 44 + i * 20;
            int offset = data[e + 4] << 24 | data[e + 5] << 16 | data[e + 6] << 8 | data[e + 7];
            int compLength = data[e + 8] << 24 | data[e + 9] << 16 | data[e + 10] << 8 | data[e + 11];
            int origLength = data[e + 12] << 24 | data[e + 13] << 16 | data[e + 14] << 8 | data[e + 15];
            if (compLength >= origLength) continue;
            // A valid zlib header that asks for a preset dictionary, which WOFF never has: zlib answers with a return
            // code that the runtime does not expect.
            data[offset] = 0x78;
            data[offset + 1] = 0xBB;
            break;
        }
        Assert.Throws<FormatException>(() => new FontCollection().Add(data, "Fz"));
    }

    [Fact]
    public void Jbig2_halftone_grid_whose_size_overflows_is_not_allocated()
    {
        // jbig2, memory-a0b96890: a grid of 3,829,930,124 x 3,318,518,034 cells overflowed the 64-bit cell count,
        // which then passed the bound and became a negative-looking int size.
        var input = Convert.FromBase64String(
            "oAAAAGQAAAAAAAAAAAAAADAAAQAAABMAAACgAAAAZAAAAAAAAAAAAAAAAAAAARAAAQAAABQABAIAAAAHUA9I0AuYowuE7AD/rAAAAAIWIAEBAAAAUQAAAIIAAABaAAABAAAAEwAAAORIGIzFzJESoBDjVoVk8xktrLyv");
        int width = BitConverter.ToInt32(input, 0), height = BitConverter.ToInt32(input, 4);
        long allocated = Bounded(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Jbig2Decoder.Decode(input[12..], null, width, height);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
        Assert.True(allocated < 16 << 20, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void Woff2_with_corrupt_brotli_data_is_a_format_error()
    {
        // font-woff2, exception-3f37d5d5: BrotliStream reports invalid data with InvalidOperationException.
        var data = Patched("Inter-Subr.woff2", (0x418F, 0), (0x4190, 0), (0x4191, 0), (0x4192, 0xFF));
        Assert.Throws<FormatException>(() => new FontCollection().Add(data, "Fz"));
    }
}

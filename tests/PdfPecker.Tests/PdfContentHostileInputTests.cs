using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>
/// Colour spaces, functions, ToUnicode maps and inline images whose sizes come from the file. A DeviceN space with
/// millions of colorants or a function with millions of outputs used to overflow the stack, which ends the process
/// instead of failing the call; a ToUnicode range ending at the last code never ended; an inline image whose width
/// overflowed 32 bits threw an undocumented exception. Every reader of page content must now finish with the page.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class PdfContentHostileInputTests
{
    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.Result;
    }

    /// <summary>Runs every public reader of page content on the PDF; each must finish without an exception.</summary>
    private static void ReadsEverywhere(byte[] pdf)
    {
        Assert.NotEmpty(Bounded(() => PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 36 })));
        Assert.NotNull(Bounded(() => PdfText.Extract(pdf)));
        Assert.NotNull(Bounded(() => PdfImages.Extract(pdf)));
        Assert.NotEmpty(Bounded(() => PdfToHtml.Convert(pdf)));
        Assert.NotNull(Bounded(() => PdfToPdfA.Convert(pdf)));
        Assert.NotNull(Bounded(() => PdfRedactor.FindText(pdf, "Hi")));
    }

    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

    /// <summary>
    /// A one-page PDF: the catalog, the page tree and the page are objects 1 to 3 and the content stream is object 4,
    /// then <paramref name="objects"/> from 5 on. <paramref name="packed"/> objects, numbered after those, go into a
    /// compressed object stream, where an array of millions of repeated names takes a few kilobytes.
    /// </summary>
    private static byte[] Page(string resources, string content, IReadOnlyList<string> objects, IReadOnlyList<string>? packed = null)
    {
        var all = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        all.AddRange(objects);
        packed ??= Array.Empty<string>();

        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new List<long>();
        Write("%PDF-1.7\n");
        for (int i = 0; i < all.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n{all[i]}\nendobj\n");
        }
        int objStm = all.Count + packed.Count + 1;
        if (packed.Count > 0)
        {
            var header = new StringBuilder();
            var body = new StringBuilder();
            for (int i = 0; i < packed.Count; i++)
            {
                header.Append(CultureInfo.InvariantCulture, $"{all.Count + 1 + i} {body.Length} ");
                body.Append(packed[i]).Append('\n');
            }
            var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(Encoding.Latin1.GetBytes(header.ToString() + body));
            offsets.Add(ms.Position);
            Write($"{objStm} 0 obj\n<< /Type /ObjStm /N {packed.Count} /First {header.Length} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            ms.Write(compressed.ToArray());
            Write("\nendstream\nendobj\n");
        }

        // A cross-reference stream, which is what can point into an object stream.
        int xrefNum = objStm + 1;
        var rows = new MemoryStream();
        void Row(int type, long field2, int field3)
        {
            Span<byte> row = stackalloc byte[7];
            row[0] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(row[1..], (uint)field2);
            BinaryPrimitives.WriteUInt16BigEndian(row[5..], (ushort)field3);
            rows.Write(row);
        }
        Row(0, 0, 65535);
        for (int i = 0; i < all.Count; i++) Row(1, offsets[i], 0);
        for (int i = 0; i < packed.Count; i++) Row(2, objStm, i);
        if (packed.Count > 0) Row(1, offsets[^1], 0);
        else Row(0, 0, 0);
        long xref = ms.Position;
        Row(1, xref, 0);
        Write($"{xrefNum} 0 obj\n<< /Type /XRef /Size {xrefNum + 1} /W [1 4 2] /Root 1 0 R /Length {rows.Length} >>\nstream\n");
        ms.Write(rows.ToArray());
        Write($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Repeat(string s, int n) => new StringBuilder(s.Length * n).Insert(0, s, n).ToString();

    [Fact]
    public void DeviceN_with_millions_of_colorants_does_not_overflow_the_stack()
    {
        // Two million colorant names in a 4.6 KB file: each colour conversion put that many doubles on the stack. Such a
        // space breaks the limit of 32 colorants and is invalid, so the fill is painted in the default gray instead.
        var pdf = Page("<< /ColorSpace << /CS0 5 0 R >> >>", "/CS0 cs 0.5 scn 10 10 100 100 re f", Array.Empty<string>(),
            new[] { "[/DeviceN [" + Repeat("/a", 2_000_000) + "] /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>]" });
        Assert.True(pdf.Length < 20_000);
        ReadsEverywhere(pdf);
    }

    [Fact]
    public void Function_with_millions_of_outputs_does_not_overflow_the_stack()
    {
        // A type 4 function with 1.5 million /Range pairs drives an axial shading: its colour table put an output buffer
        // of that size on the stack. A function with more outputs than a colour space can have components is invalid.
        // Object 5 is the function; the shading (6) and the Range array (7) are packed into an object stream.
        var pdf = Page("<< /Shading << /Sh0 6 0 R >> >>", "/Sh0 sh",
            new[] { "<< /FunctionType 4 /Domain [0 1] /Range 7 0 R /Length 3 >>\nstream\n{ }\nendstream" },
            new[] { "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function 5 0 R >>", "[" + Repeat("0 1 ", 1_500_000) + "]" });
        Assert.True(pdf.Length < 20_000);
        ReadsEverywhere(pdf);
    }

    [Fact]
    public void Colour_spaces_and_functions_above_the_limits_are_invalid()
    {
        var file = PdfFile.Open(Page("<< >>", "", Array.Empty<string>()), null);
        PdfArray DeviceN(int colorants)
        {
            var names = new PdfArray();
            for (int i = 0; i < colorants; i++) names.Items.Add(PdfName.Get("C" + i.ToString(CultureInfo.InvariantCulture)));
            return new PdfArray(new List<PdfObject> { PdfName.Get("DeviceN"), names, PdfName.Get("DeviceCMYK"), Exponential(4) });
        }
        PdfDict Exponential(int outputs)
        {
            var c1 = new PdfArray();
            for (int i = 0; i < outputs; i++) c1.Items.Add(PdfNumber.Int(1));
            return new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
            {
                ["FunctionType"] = PdfNumber.Int(2), ["Domain"] = new PdfArray(new List<PdfObject> { PdfNumber.Int(0), PdfNumber.Int(1) }),
                ["C0"] = new PdfArray(c1.Items.Select(_ => (PdfObject)PdfNumber.Int(0)).ToList()), ["C1"] = c1, ["N"] = PdfNumber.Int(1),
            });
        }

        Assert.Equal(32, ColorSpace.Parse(file, DeviceN(32), null)!.Components);
        Assert.Null(ColorSpace.Parse(file, DeviceN(33), null));
        Assert.Equal(32, PdfFunction.Create(file, Exponential(32))!.Outputs);
        Assert.Null(PdfFunction.Create(file, Exponential(33)));
        var parts = new PdfArray();
        for (int i = 0; i < 33; i++) parts.Items.Add(Exponential(1));
        Assert.Null(PdfFunction.Create(file, parts));
    }

    [Fact]
    public void Shading_in_a_space_with_more_components_than_its_function_has_outputs_renders()
    {
        // The colour table read one value per component from an output buffer sized by the function, so twelve
        // colorants fed by a one-output function read past its end. The missing outputs are 0, which is no ink.
        var pdf = Page("<< /Shading << /Sh0 5 0 R >> >>", "/Sh0 sh", new[]
        {
            "<< /ShadingType 2 /ColorSpace [/DeviceN [" + Repeat("/a ", 12) + "] /DeviceGray 6 0 R] /Coords [0 0 200 0] /Function 7 0 R >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>",
        });
        var image = Bounded(() => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 }));
        // The first colorant runs from 0 at the left to full tint at the right; the tint transform gives gray 1 - tint.
        Assert.True(image.Rgba[(100 * image.Width + 5) * 4] > 200);
        Assert.True(image.Rgba[(100 * image.Width + 195) * 4] < 55);
    }

    [Fact]
    public void ToUnicode_range_that_ends_at_the_last_code_ends()
    {
        // The loop over the codes of <FFFFFFFF> <FFFFFFFF> compared the code with the end after counting up, which wraps
        // to 0, so it wrote four billion entries; text extraction hung and filled memory.
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n"
            + "1 begincodespacerange <00> <FF> endcodespacerange\n"
            + "1 beginbfrange <FFFFFFFF> <FFFFFFFF> <0041> endbfrange\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end";
        var pdf = Page("<< /Font << /F1 5 0 R >> >>", "BT /F1 12 Tf 10 10 Td (Hi) Tj ET", new[]
        {
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
        });
        Assert.Contains("Hi", Bounded(() => PdfText.Extract(pdf)), StringComparison.Ordinal);
        ReadsEverywhere(pdf);
    }

    [Theory]
    [InlineData("<FFFFFFFE> <FFFFFFFF> <0041>")]
    [InlineData("<FFFFFFFE> <FFFFFFFF> [<0041> <0042> <0043> <0044>]")]
    public void ToUnicode_ranges_at_the_end_of_the_code_space_map_only_their_codes(string range)
    {
        var map = Bounded(() => CMap.Parse(Encoding.Latin1.GetBytes($"begincmap 1 beginbfrange {range} endbfrange endcmap")))!;
        Assert.Equal("A", map.ToUnicode(0xFFFFFFFE));
        Assert.Equal("B", map.ToUnicode(0xFFFFFFFF));
        Assert.Null(map.ToUnicode(0));
        Assert.Null(map.ToUnicode(1));
    }

    [Fact]
    public void ToUnicode_map_with_many_full_ranges_stops_at_its_budget()
    {
        // Each bfrange is bounded to 65,536 codes, but 40,000 of them, a few kilobytes once compressed, asked for 2.6
        // billion mappings: text extraction ran for minutes and filled memory. The whole map now has a budget.
        var cmap = new StringBuilder("begincmap 1 begincodespacerange <0000> <FFFF> endcodespacerange\n1 beginbfchar <0001> <0048> <0002> <0069> endbfchar\n");
        for (int i = 0; i < 40_000; i++)
        {
            if (i % 100 == 0) cmap.Append("100 beginbfrange\n");
            cmap.Append(CultureInfo.InvariantCulture, $"<{i + 1:X4}0000> <{i + 1:X4}FFFF> <0041>\n");
            if (i % 100 == 99) cmap.Append("endbfrange\n");
        }
        cmap.Append("endcmap");
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(Encoding.Latin1.GetBytes(cmap.ToString()));
        var data = Encoding.Latin1.GetString(compressed.ToArray());
        var pdf = Page("<< /Font << /F1 5 0 R >> >>", "BT /F1 12 Tf 10 10 Td (\x01\x02) Tj ET", new[]
        {
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            $"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n{data}\nendstream",
        });
        Assert.Contains("Hi", Bounded(() => PdfText.Extract(pdf)), StringComparison.Ordinal);
        ReadsEverywhere(pdf);

        // The mappings read before the budget ran out are kept, and nothing after it is mapped.
        var map = Bounded(() => CMap.Parse(Encoding.Latin1.GetBytes(cmap.ToString())))!;
        Assert.Equal("H", map.ToUnicode(1));
        Assert.Equal("A", map.ToUnicode(0x0001_0000));
        Assert.Equal("B", map.ToUnicode(0x0001_0001));
        Assert.Null(map.ToUnicode(0x9C40_0000));
    }

    [Fact]
    public void ToUnicode_map_with_long_destination_strings_stops_at_its_budget()
    {
        // A range of 65,536 codes whose destination is a 32 KB string asked for 4 GB of text.
        var cmap = $"begincmap 1 beginbfrange <0000> <FFFF> <{Repeat("0041", 16_384)}> endbfrange 1 beginbfchar <0001> <0042> endbfchar endcmap";
        var map = Bounded(() => CMap.Parse(Encoding.Latin1.GetBytes(cmap)))!;
        Assert.Equal(16_384, map.ToUnicode(0)!.Length);
        Assert.Null(map.ToUnicode(0xFFFF));
    }

    [Theory]
    [InlineData("/W 2147483640 /H 8 /BPC 1 /CS /G")]
    [InlineData("/W 2147483647 /H 2147483647 /BPC 16 /CS /CMYK")]
    [InlineData("/W 8 /H 8 /BPC 2147483647 /CS /RGB")]
    public void Inline_image_whose_data_length_overflows_32_bits_is_skipped(string dict)
    {
        // The expected length of the data, width times components times depth times height, wrapped in 32 bits and so
        // did its sum with the data's start; the search for EI then read before the start of the stream.
        var pdf = Page("<< /Font << /F1 5 0 R >> >>", "q Q\nq Q\nq Q\nq Q\nBI " + dict + " ID \u0000\u0001\u0002 EI\nBT /F1 12 Tf 10 10 Td (Hi) Tj ET",
            new[] { Font });
        Assert.Contains("Hi", Bounded(() => PdfText.Extract(pdf)), StringComparison.Ordinal);
        ReadsEverywhere(pdf);
    }
}

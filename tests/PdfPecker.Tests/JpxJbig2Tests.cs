using System.Diagnostics;
using System.Text;
using PdfPecker.Rendering.Images;

namespace PdfPecker.Tests;

/// <summary>
/// JPEG 2000 and JBIG2 decoding. The fixtures in Fixtures/codecs are generated: the JPEG 2000 files encode a synthetic
/// 61x47 pattern (see <see cref="Synthetic"/>) with OpenJPEG, and the JBIG2 files come from a small test encoder, each with
/// the expected bitmap as a PBM file.
/// </summary>
public class JpxJbig2Tests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "codecs", name));

    private const int W = 61, H = 47;

    private static (int R, int G, int B, int A) Synthetic(int x, int y) =>
        ((x * 4) & 255, (y * 5) & 255, ((x * y) ^ (x + y)) & 255, (x * 3 + y * 2) & 255);

    [Fact]
    public void Lossless_tiled_jpeg2000_decodes_exactly()
    {
        var img = JpxDecoder.Decode(Fixture("jpx-lossless.jp2"))!;
        Assert.Equal((W, H, 3), (img.Width, img.Height, img.Components));
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            var p = Synthetic(x, y);
            int o = (y * W + x) * 3;
            Assert.Equal((p.R, p.G, p.B), (img.Data[o], img.Data[o + 1], img.Data[o + 2]));
        }
    }

    [Fact]
    public void Lossy_jpeg2000_matches_the_reference_decoder()
    {
        var img = JpxDecoder.Decode(Fixture("jpx-lossy.j2k"))!;
        var reference = Fixture("jpx-lossy-ref.rgb");
        Assert.Equal(reference.Length, img.Data.Length);
        for (int i = 0; i < reference.Length; i++)
            Assert.True(Math.Abs(reference[i] - img.Data[i]) <= 2, $"Sample {i}: {img.Data[i]} vs {reference[i]}");
    }

    [Fact]
    public void Jpeg2000_alpha_channel_is_returned()
    {
        var img = JpxDecoder.Decode(Fixture("jpx-alpha.jp2"))!;
        Assert.Equal(3, img.Components);
        Assert.NotNull(img.Alpha);
        for (int y = 0; y < H; y += 7)
        for (int x = 0; x < W; x += 5)
            Assert.Equal(Synthetic(x, y).A, img.Alpha![y * W + x]);
    }

    [Fact]
    public void Renders_jpx_images_in_a_pdf_with_smask_in_data()
    {
        var pdf = ImagePdf("/Filter /JPXDecode /SMaskInData 1", Fixture("jpx-alpha.jp2"));
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72, Transparent = true });
        Assert.Equal((W, H), (img.Width, img.Height));
        foreach (var (x, y) in new[] { (10, 10), (40, 20), (55, 40) })
        {
            var p = Synthetic(x, y);
            int o = (y * W + x) * 4;
            Assert.True(Math.Abs(img.Rgba[o + 3] - p.A) <= 8, $"Alpha at {x},{y}: {img.Rgba[o + 3]} vs {p.A}");
        }
    }

    [Theory]
    [InlineData("generic-t0")]
    [InlineData("generic-t1")]
    [InlineData("generic-t2")]
    [InlineData("generic-t3")]
    [InlineData("generic-mmr")]
    [InlineData("striped")]
    [InlineData("refine-page")]
    [InlineData("compose-refine")]
    [InlineData("symbol-refine")]
    [InlineData("text-arith")]
    [InlineData("text-huff")]
    [InlineData("halftone")]
    public void Jbig2_decodes_exactly(string name)
    {
        var (w, h, expected) = ReadPbm(Fixture(name + ".pbm"));
        byte[]? globals = name == "text-arith" ? Fixture("text-arith.glob") : null;
        var rows = Jbig2Decoder.Decode(Fixture(name + ".jb2"), globals, w, h)!;
        int stride = (w + 7) / 8;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool black = (expected[y * stride + x / 8] >> (7 - x % 8) & 1) != 0;
            bool white = (rows[y * stride + x / 8] >> (7 - x % 8) & 1) != 0;
            Assert.True(black != white, $"{name}: pixel {x},{y}");
        }
    }

    [Fact]
    public void Renders_jbig2_images_with_globals_in_a_pdf()
    {
        var (w, h, expected) = ReadPbm(Fixture("text-arith.pbm"));
        var pdf = BuildPdf(w, h, $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 1 " +
            "/Filter /JBIG2Decode /DecodeParms << /JBIG2Globals 5 0 R >>", Fixture("text-arith.jb2"), Fixture("text-arith.glob"));
        var img = PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
        int stride = (w + 7) / 8, wrong = 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool black = (expected[y * stride + x / 8] >> (7 - x % 8) & 1) != 0;
            if (black != img.Rgba[(y * w + x) * 4] < 128) wrong++;
        }
        Assert.Equal(0, wrong);
    }

    [Theory]
    [InlineData("jpx-lossless.jp2")]
    [InlineData("jpx-lossy.j2k")]
    [InlineData("jpx-alpha.jp2")]
    [InlineData("text-arith.jb2")]
    [InlineData("text-huff.jb2")]
    [InlineData("halftone.jb2")]
    [InlineData("generic-mmr.jb2")]
    [InlineData("symbol-refine.jb2")]
    public void Damaged_streams_fail_cleanly_and_quickly(string name)
    {
        var data = Fixture(name);
        bool jpx = name.StartsWith("jpx");
        var rnd = new Random(name.Length * 7919);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 150; i++)
        {
            var d = (byte[])data.Clone();
            if (i % 3 == 0) Array.Resize(ref d, rnd.Next(d.Length));
            int edits = 1 + rnd.Next(8);
            for (int e = 0; e < edits && d.Length > 0; e++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
            try
            {
                if (jpx) JpxDecoder.Decode(d);
                else Jbig2Decoder.Decode(d, name == "text-arith.jb2" ? Fixture("text-arith.glob") : null, 160, 100);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException
                or ArgumentException or OverflowException or InvalidCastException) { }
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"Took {sw.Elapsed}");
    }

    [Fact]
    public void Garbage_jpx_and_jbig2_images_do_not_break_rendering()
    {
        var junk = Encoding.ASCII.GetBytes("\0\0\0\x0CjP  \r\n\x87\nnot really a jpeg 2000 file");
        PdfRenderer.Open(ImagePdf("/Filter /JPXDecode", junk)).RenderPage(0, new ImageOptions { Dpi = 72 });
        var pdf = BuildPdf(W, H, $"<< /Type /XObject /Subtype /Image /Width {W} /Height {H} /BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /JBIG2Decode", junk, null);
        PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72 });
    }

    [Fact]
    public void Huge_declared_jbig2_page_is_refused()
    {
        // A page information segment claiming 60000 x 60000 pixels, followed by nothing.
        var seg = new byte[] { 0, 0, 0, 0, 0x30, 0, 1, 0, 0, 0, 19, 0, 0, 0xEA, 0x60, 0, 0, 0xEA, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0 };
        byte[]? rows = null;
        var ex = Record.Exception(() => rows = Jbig2Decoder.Decode(seg, null, 60000, 60000));
        Assert.True(ex is InvalidDataException || (ex == null && rows == null),
            $"Expected the page to be refused, got {ex?.GetType().Name ?? $"{rows?.Length} bytes"}");
    }

    private static byte[] ImagePdf(string filter, byte[] data) =>
        BuildPdf(W, H, $"<< /Type /XObject /Subtype /Image /Width {W} /Height {H} {filter}", data, null);

    private static byte[] BuildPdf(int w, int h, string imageDict, byte[] image, byte[]? globals)
    {
        var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {w} {h}] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im0 4 0 R >> >> /Contents 6 0 R >>",
            imageDict + $" /Length {image.Length} >>\nstream\n" + Encoding.Latin1.GetString(image) + "\nendstream",
            globals == null ? "null" : $"<< /Length {globals.Length} >>\nstream\n" + Encoding.Latin1.GetString(globals) + "\nendstream",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static (int W, int H, byte[] Bits) ReadPbm(byte[] pbm)
    {
        int pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)pbm[pos]) || pbm[pos] == '#')
            {
                if (pbm[pos] == '#') while (pbm[pos] != '\n') pos++;
                pos++;
            }
            int start = pos;
            while (!char.IsWhiteSpace((char)pbm[pos])) pos++;
            return Encoding.ASCII.GetString(pbm, start, pos - start);
        }
        Assert.Equal("P4", Token());
        int w = int.Parse(Token()), h = int.Parse(Token());
        pos++;
        return (w, h, pbm[pos..]);
    }
}

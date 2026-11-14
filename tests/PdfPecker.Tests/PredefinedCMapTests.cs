using System.Text;
using PdfPecker.Rendering.Fonts;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// The predefined Chinese, Japanese and Korean CMaps that fonts name by their /Encoding (90ms-RKSJ-H, UniJIS-UCS2-H,
/// GBK-EUC-H and the others of ISO 32000-2 table 116), and the CID-to-Unicode maps of their orderings.
/// </summary>
public class PredefinedCMapTests
{
    static PredefinedCMapTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly byte[] NotoSansJP = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts", "NotoSansJP-Subset.ttf"));

    private static int Cid(string cmap, uint code) => PredefinedCMaps.Get(cmap)!.ToCid(code);

    [Fact]
    public void Codes_map_to_the_cids_of_adobe_s_files()
    {
        // ぁ is CID 842 in Adobe-Japan1, whether it comes as Shift-JIS, EUC or UCS-2.
        Assert.Equal(842, Cid("90ms-RKSJ-H", 0x829F));
        Assert.Equal(843, Cid("90ms-RKSJ-H", 0x82A0));
        Assert.Equal(842, Cid("UniJIS-UCS2-H", 0x3041));
        Assert.Equal(842, Cid("EUC-H", 0xA4A1));
        // One-byte codes of Shift-JIS are half-width Latin and katakana.
        Assert.Equal(264, Cid("90ms-RKSJ-H", 0x41));
        // The other orderings: 中 in GB 2312, Big Five and KS X 1001.
        Assert.Equal(PredefinedCMaps.Get("UniGB-UCS2-H")!.ToCid(0x4E2D), Cid("GBK-EUC-H", 0xD6D0));
        Assert.Equal(PredefinedCMaps.Get("UniCNS-UCS2-H")!.ToCid(0x4E2D), Cid("ETen-B5-H", 0xA4A4));
        Assert.Equal(PredefinedCMaps.Get("UniKS-UCS2-H")!.ToCid(0xAC00), Cid("KSCms-UHC-H", 0xB0A1));
        Assert.Null(PredefinedCMaps.Get("No-Such-CMap"));
    }

    [Fact]
    public void Vertical_cmaps_build_on_the_horizontal_ones()
    {
        var vertical = PredefinedCMaps.Get("90ms-RKSJ-V")!;
        Assert.True(vertical.Vertical);
        Assert.False(PredefinedCMaps.Get("90ms-RKSJ-H")!.Vertical);
        // The comma and small ぁ have vertical forms of their own; あ comes from the horizontal CMap the vertical one uses.
        Assert.Equal(7887, vertical.ToCid(0x8141));
        Assert.Equal(7918, vertical.ToCid(0x829F));
        Assert.Equal(843, vertical.ToCid(0x82A0));
    }

    [Fact]
    public void Codes_split_by_the_codespace_of_the_cmap()
    {
        var cmap = PredefinedCMaps.Get("90ms-RKSJ-H")!;
        byte[] bytes = { 0x41, 0x82, 0x9F, 0x42 };
        int pos = 0;
        var codes = new List<uint>();
        while (pos < bytes.Length) codes.Add(cmap.NextCode(bytes, ref pos, out _));
        Assert.Equal(new uint[] { 0x41, 0x829F, 0x42 }, codes);
    }

    [Fact]
    public void Each_ordering_maps_its_cids_to_unicode()
    {
        Assert.Equal("ぁ", PredefinedCMaps.CidToUnicode("Japan1")!.ToUnicode(842));
        Assert.Equal("中", PredefinedCMaps.CidToUnicode("GB1")!.ToUnicode((uint)PredefinedCMaps.Get("UniGB-UCS2-H")!.ToCid(0x4E2D)));
        Assert.NotNull(PredefinedCMaps.CidToUnicode("CNS1"));
        Assert.NotNull(PredefinedCMaps.CidToUnicode("Korea1"));
        Assert.Null(PredefinedCMaps.CidToUnicode("Identity"));
    }

    [Fact]
    public void An_embedded_cmap_can_build_on_a_predefined_one()
    {
        // The embedded CMap changes one code and leaves the rest to 90ms-RKSJ-H.
        var cmap = CMap.Parse(Encoding.ASCII.GetBytes("/90ms-RKSJ-H usecmap\n1 begincidchar\n<829f> 1\nendcidchar\n"))!;
        Assert.Equal(1, cmap.ToCid(0x829F));
        Assert.Equal(843, cmap.ToCid(0x82A0));
        int pos = 0;
        cmap.NextCode(new byte[] { 0x82, 0xA0 }, ref pos, out int length);
        Assert.Equal(2, length);
    }

    /// <summary>A page that shows <paramref name="text"/> in the Noto Sans JP subset, embedded as a CID TrueType font.</summary>
    private static byte[] Page(string encoding, byte[] text, bool embedded = true)
    {
        var pdf = new FixturePdf();
        string fontFile = embedded ? $"/FontFile2 {pdf.AddStream("", NotoSansJP)} 0 R " : "";
        int descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /NotoSansJP /Flags 4 /FontBBox [0 -120 1000 880] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 {fontFile}>>");
        int cidFont = pdf.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /NotoSansJP /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /FontDescriptor {descriptor} 0 R /DW 1000 >>");
        int font = pdf.Add($"<< /Type /Font /Subtype /Type0 /BaseFont /NotoSansJP /Encoding {encoding} /DescendantFonts [{cidFont} 0 R] >>");
        string content = "BT /F1 40 Tf 10 20 Td <" + Convert.ToHexString(text) + "> Tj ET";
        return pdf.Build(220, 80, content, $"/Font << /F1 {font} 0 R >>");
    }

    private static byte[] Glyphs(params int[] gids) => gids.SelectMany(g => new[] { (byte)(g >> 8), (byte)g }).ToArray();

    [Fact]
    public void An_embedded_truetype_font_with_a_predefined_cmap_draws_the_glyphs_its_cmap_gives()
    {
        // The reference: the same glyphs chosen by glyph id with Identity-H.
        var font = TrueTypeFont.Parse(NotoSansJP)!;
        var gids = "A一葛".Select(c => font.Lookup(3, 1, c)).ToArray();
        Assert.DoesNotContain(0, gids);
        var expected = Render(Page("/Identity-H", Glyphs(gids))).Rgba;

        var shiftJis = Encoding.GetEncoding(932).GetBytes("A一葛");
        Assert.Equal(expected, Render(Page("/90ms-RKSJ-H", shiftJis)).Rgba);
        Assert.Equal(expected, Render(Page("/UniJIS-UCS2-H", Encoding.BigEndianUnicode.GetBytes("A一葛"))).Rgba);
    }

    [Fact]
    public void Text_in_a_predefined_cmap_is_extracted_without_a_tounicode_map()
    {
        var shiftJis = Encoding.GetEncoding(932).GetBytes("一葛A");
        Assert.Equal("一葛A", PdfText.Extract(Page("/90ms-RKSJ-H", shiftJis)).Trim());
        // A font that is not embedded is read the same way.
        Assert.Equal("一葛A", PdfText.Extract(Page("/90ms-RKSJ-H", shiftJis, embedded: false)).Trim());
        Assert.Equal("一葛A", PdfText.Extract(Page("/UniJIS-UCS2-H", Encoding.BigEndianUnicode.GetBytes("一葛A"), embedded: false)).Trim());
    }
}

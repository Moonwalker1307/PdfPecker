using System.Globalization;
using System.Buffers.Binary;
using PdfPecker.Rendering.Content;
using static PdfPecker.Tests.FixturePdf;

namespace PdfPecker.Tests;

/// <summary>
/// ICCBased colour spaces with their profiles applied. The profiles are built here: matrix/TRC gray and RGB profiles,
/// and CMYK profiles with lut8, lut16 and lutAtoB tables, so the tests know what each colour must become.
/// </summary>
public class IccProfileTests
{
    // The sRGB primaries adapted to D50, as sRGB profiles list them.
    private static readonly double[][] SrgbColorants =
    {
        new[] { 0.4361, 0.2225, 0.0139 },
        new[] { 0.3851, 0.7169, 0.0971 },
        new[] { 0.1431, 0.0606, 0.7141 },
    };

    /// <summary>Builds an ICC profile from its header fields and tags, each tag's data padded to four bytes.</summary>
    internal static byte[] Profile(string colorSpace, string pcs, int version, params (string Tag, byte[] Data)[] tags)
    {
        int offset = 128 + 4 + 12 * tags.Length;
        var body = new List<byte>();
        var table = new List<byte>();
        foreach (var (tag, data) in tags)
        {
            table.AddRange(Tag(tag));
            table.AddRange(U32(offset + body.Count));
            table.AddRange(U32(data.Length));
            body.AddRange(data);
            while (body.Count % 4 != 0) body.Add(0);
        }
        var header = new byte[128];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), version);
        Tag(colorSpace).CopyTo(header, 16);
        Tag(pcs).CopyTo(header, 20);
        Tag("acsp").CopyTo(header, 36);
        var profile = header.Concat(U32(tags.Length)).Concat(table).Concat(body).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(profile, profile.Length);
        return profile;
    }

    private static byte[] Tag(string s) => s.Select(c => (byte)c).ToArray();
    private static byte[] U32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] S15(double v) => U32((int)Math.Round(v * 65536));
    private static byte[] Xyz(double[] v) => Tag("XYZ ").Concat(new byte[4]).Concat(v.SelectMany(S15)).ToArray();

    /// <summary>A 'curv' gamma, or the identity for gamma 1.</summary>
    private static byte[] Gamma(double gamma) => gamma == 1
        ? Tag("curv").Concat(new byte[4]).Concat(U32(0)).ToArray()
        : Tag("curv").Concat(new byte[4]).Concat(U32(1)).Concat(U16((int)Math.Round(gamma * 256))).ToArray();

    /// <summary>The sRGB curve as a 'para' function of type 3.</summary>
    private static byte[] SrgbCurve() => Tag("para").Concat(new byte[4]).Concat(U16(3)).Concat(new byte[2])
        .Concat(new[] { 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045 }.SelectMany(S15)).ToArray();

    private static byte[] RgbProfile(double[][] colorants, byte[] curve) => Profile("RGB ", "XYZ ", 0x02100000,
        ("rXYZ", Xyz(colorants[0])), ("gXYZ", Xyz(colorants[1])), ("bXYZ", Xyz(colorants[2])),
        ("rTRC", curve), ("gTRC", curve), ("bTRC", curve));

    private static (int R, int G, int B, int A) Fill(byte[] profile, int n, string color)
    {
        var pdf = new FixturePdf();
        int stream = pdf.AddStream($"/N {n}", profile);
        var page = pdf.Build(40, 40, $"/CS cs {color} sc 0 0 40 40 re f", $"/ColorSpace << /CS [/ICCBased {stream} 0 R] >>");
        return At(Render(page), 20, 20);
    }

    [Fact]
    public void An_srgb_profile_is_read_as_device_rgb()
    {
        var profile = RgbProfile(SrgbColorants, SrgbCurve());
        Assert.Same(ColorSpace.RgbSpace, IccSpace.Create(profile, 3));
        var device = At(Render(new FixturePdf().Build(40, 40, "0.2 0.5 0.9 rg 0 0 40 40 re f")), 20, 20);
        Assert.Equal(device, Fill(profile, 3, "0.2 0.5 0.9"));
    }

    [Fact]
    public void A_matrix_profile_mixes_its_colorants()
    {
        // Red and green swapped: the profile's red is sRGB green.
        var swapped = RgbProfile(new[] { SrgbColorants[1], SrgbColorants[0], SrgbColorants[2] }, SrgbCurve());
        AssertColor(Fill(swapped, 3, "1 0 0"), 0, 255, 0, 1);
        AssertColor(Fill(swapped, 3, "0 0.5 0"), 128, 0, 0, 1);
    }

    [Fact]
    public void A_gray_profile_applies_its_curve()
    {
        // Linear light: half of white is sRGB 188, where DeviceGray gives 128.
        var linear = Profile("GRAY", "XYZ ", 0x02100000, ("kTRC", Gamma(1)));
        AssertColor(Fill(linear, 1, "0.5"), 188, 188, 188, 1);
        AssertColor(Fill(linear, 1, "1"), 255, 255, 255, 0);
        // Gamma 1.8: half of white is 0.5^1.8 = 0.287 of the light, which is sRGB 146.
        var gamma = Profile("GRAY", "XYZ ", 0x02100000, ("kTRC", Gamma(1.8)));
        AssertColor(Fill(gamma, 1, "0.5"), 146, 146, 146, 1);
    }

    // ---------------------------------------------------------------- CMYK lookup tables

    /// <summary>
    /// The Lab of a CMYK corner of the test press: paper is the profile's white, cyan, magenta and yellow are their usual hues, and
    /// black ink alone is a dark neutral L* 20 that more inks make darker still.
    /// </summary>
    private static (double L, double A, double B) Press(int c, int m, int y, int k)
    {
        double l = 100 - 40 * c - 45 * m - 10 * y - 75 * k;
        double a = -35 * c + 70 * m - 5 * y;
        double b = -50 * c - 5 * m + 90 * y;
        int inks = c + m + y + k;
        if (k == 1) (l, a, b) = (20 - 3 * (inks - 1), 0, 0);
        else if (inks > 1) (a, b) = (a / inks, b / inks);
        return (Math.Max(l, 5), a, b);
    }

    private static IEnumerable<(double L, double A, double B)> PressGrid()
    {
        for (int c = 0; c < 2; c++)
        for (int m = 0; m < 2; m++)
        for (int y = 0; y < 2; y++)
        for (int k = 0; k < 2; k++)
            yield return Press(c, m, y, k);
    }

    /// <summary>A CMYK profile whose A2B0 is a lut16 ('mft2') of the press with a grid of two points a side.</summary>
    private static byte[] Lut16Profile()
    {
        var lut = new List<byte>();
        lut.AddRange(Tag("mft2"));
        lut.AddRange(new byte[4]);
        lut.AddRange(new byte[] { 4, 3, 2, 0 });
        foreach (double v in new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) lut.AddRange(S15(v));
        lut.AddRange(U16(2));
        lut.AddRange(U16(2));
        for (int i = 0; i < 4; i++) lut.AddRange(U16(0).Concat(U16(65535)));
        // Version 2 16-bit Lab: L* 100 and a* or b* 127 are at 0xFF00.
        foreach (var (l, a, b) in PressGrid())
        {
            lut.AddRange(U16((int)Math.Round(l / 100 * 65280)));
            lut.AddRange(U16((int)Math.Round((a + 128) / 255 * 65280)));
            lut.AddRange(U16((int)Math.Round((b + 128) / 255 * 65280)));
        }
        for (int i = 0; i < 3; i++) lut.AddRange(U16(0).Concat(U16(65535)));
        return Profile("CMYK", "Lab ", 0x02100000, ("A2B0", lut.ToArray()));
    }

    /// <summary>The same press as a version 4 lutAtoB ('mAB ') with identity A and B curves and an 8-bit table.</summary>
    private static byte[] AtoBProfile()
    {
        var identity = Gamma(1);
        var lut = new List<byte>();
        lut.AddRange(Tag("mAB "));
        lut.AddRange(new byte[4]);
        lut.AddRange(new byte[] { 4, 3, 0, 0 });
        int bOffset = 32, clutOffset = bOffset + 3 * identity.Length, aOffset = clutOffset + 20 + 16 * 3;
        lut.AddRange(U32(bOffset));
        lut.AddRange(U32(0));
        lut.AddRange(U32(0));
        lut.AddRange(U32(clutOffset));
        lut.AddRange(U32(aOffset));
        for (int i = 0; i < 3; i++) lut.AddRange(identity);
        lut.AddRange(new byte[] { 2, 2, 2, 2 }.Concat(new byte[12]));
        lut.AddRange(new byte[] { 1, 0, 0, 0 });
        // Version 4 Lab: L* 100 and a* or b* 127 are at the top of the range.
        foreach (var (l, a, b) in PressGrid())
        {
            lut.Add((byte)Math.Round(l / 100 * 255));
            lut.Add((byte)Math.Round(a + 128));
            lut.Add((byte)Math.Round(b + 128));
        }
        while (lut.Count < aOffset) lut.Add(0);
        for (int i = 0; i < 4; i++) lut.AddRange(identity);
        return Profile("CMYK", "Lab ", 0x04300000, ("A2B0", lut.ToArray()));
    }

    [Fact]
    public void A_cmyk_profile_shows_paper_as_white_and_full_ink_as_black()
    {
        var profile = Lut16Profile();
        // Paper shows as white; the darkest colour, L* 11 here, is scaled to black.
        AssertColor(Fill(profile, 4, "0 0 0 0"), 255, 255, 255, 1);
        AssertColor(Fill(profile, 4, "1 1 1 1"), 0, 0, 0, 1);
        // Black ink alone is close to but lighter than the darkest colour.
        var black = Fill(profile, 4, "0 0 0 1");
        Assert.InRange(black.R, 8, 50);
        AssertColor(black, black.R, black.R, black.R, 1);
        // Cyan is blue-green, and the press's cyan, not DeviceCMYK's.
        var cyan = Fill(profile, 4, "1 0 0 0");
        Assert.True(cyan.R < cyan.G && cyan.G < cyan.B, $"cyan came out as {cyan}");
        Assert.NotEqual(At(RenderCmyk("1 0 0 0"), 20, 20), cyan);
    }

    private static RenderedImage RenderCmyk(string color) => Render(new FixturePdf().Build(40, 40, $"{color} k 0 0 40 40 re f"));

    [Fact]
    public void Lut16_and_lut_a_to_b_tables_of_the_same_press_give_the_same_colours()
    {
        var lut16 = Lut16Profile();
        var atob = AtoBProfile();
        foreach (var color in new[] { "0 0 0 0", "1 0 0 0", "0 1 0 0", "0.3 0.6 0.2 0.1", "0.5 0.5 0.5 0.5", "0 0 0 1" })
        {
            var expected = Fill(lut16, 4, color);
            AssertColor(Fill(atob, 4, color), expected.R, expected.G, expected.B, 2);
        }
    }

    [Fact]
    public void Images_in_an_icc_space_take_the_colours_of_fills()
    {
        var profile = Lut16Profile();
        byte[] samples = { 255, 0, 0, 0, 77, 153, 51, 26, 0, 0, 0, 255 };
        string[] fills = { "1 0 0 0", string.Join(' ', new[] { 77, 153, 51, 26 }.Select(v => (v / 255.0).ToString("0.######", CultureInfo.InvariantCulture))), "0 0 0 1" };
        foreach (bool inverted in new[] { false, true })
        {
            var pdf = new FixturePdf();
            int icc = pdf.AddStream("/N 4", profile);
            var data = inverted ? samples.Select(s => (byte)(255 - s)).ToArray() : samples;
            string decode = inverted ? "/Decode [1 0 1 0 1 0 1 0] " : "";
            int image = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 3 /Height 1 /BitsPerComponent 8 /ColorSpace [/ICCBased {icc} 0 R] {decode}", data);
            var page = Render(pdf.Build(30, 10, "q 30 0 0 10 0 0 cm /Im Do Q", $"/XObject << /Im {image} 0 R >>"));
            for (int i = 0; i < 3; i++)
            {
                var expected = Fill(profile, 4, fills[i]);
                AssertColor(At(page, 10 * i + 5, 5), expected.R, expected.G, expected.B, 1);
            }
        }
    }

    [Fact]
    public void A_profile_this_cannot_read_falls_back_to_the_alternate()
    {
        var damaged = Lut16Profile()[..300];
        Assert.Equal(At(RenderCmyk("0 0 0 1"), 20, 20), Fill(damaged, 4, "0 0 0 1"));
        // A profile of another number of components than /N is not used either.
        Assert.Null(IccSpace.Create(RgbProfile(SrgbColorants, Gamma(2.2)), 4));
    }
}

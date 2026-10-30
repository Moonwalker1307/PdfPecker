using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Page trees that loop or nest without end, given to every API that reads a page tree. They share one reader, which
/// reads each node once and no deeper than 64 levels, so each call ends quickly with the one page the tree reaches.
/// </summary>
[Collection(HostileInputCollection.Name)]
public class PageTreeHostileInputTests
{
    private static readonly Lazy<X509Certificate2> Signer = new(() => SignatureVerificationTests.SelfSigned("Page tree signer"));

    public static TheoryData<string> Trees() => new() { "cyclic", "deep" };

    /// <summary>
    /// One page with a form field, under a tree that loops (a node that lists itself three times and its parent once) or
    /// that nests 2,000 levels deep, each level listing the next twice. Walking either without remembering the nodes it
    /// has read takes exponential time.
    /// </summary>
    private static byte[] Tree(string kind)
    {
        const string content = "0 0 1 rg 10 10 50 50 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            kind == "cyclic" ? "<< /Type /Pages /Kids [7 0 R 3 0 R] /Count 1 >>" : "<< /Type /Pages /Kids [7 0 R 7 0 R 3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 6 0 R /Annots [5 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Widget /T (name) /FT /Tx /Rect [20 120 180 150] /P 3 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        if (kind == "cyclic") objects.Add("<< /Type /Pages /Parent 2 0 R /Kids [7 0 R 7 0 R 2 0 R 7 0 R] /Count 1 >>");
        else
        {
            const int levels = 2000;
            for (int i = 0; i < levels; i++)
            {
                int num = 7 + i;
                objects.Add(i == levels - 1
                    ? $"<< /Type /Pages /Parent {num - 1} 0 R /Kids [3 0 R] /Count 1 >>"
                    : $"<< /Type /Pages /Parent {(i == 0 ? 2 : num - 1)} 0 R /Kids [{num + 1} 0 R {num + 1} 0 R 3 0 R] /Count 1 >>");
            }
        }
        return BuildPdf(objects);
    }

    private static byte[] BuildPdf(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static T Bounded<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.True(((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "Did not finish in time");
        return task.GetAwaiter().GetResult(); // throws what the work threw, not an AggregateException
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Rendering_reads_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        Assert.Equal(1, Bounded(() => PdfToImage.GetPageCount(pdf)));
        Assert.NotEmpty(Bounded(() => PdfToImage.ConvertPage(pdf, 0, new ImageOptions { Dpi = 36 })));
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Merging_reads_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        Assert.Equal(2, PdfToImage.GetPageCount(Bounded(() => PdfMerger.Merge(pdf, pdf))));
        Assert.Equal(1, PdfToImage.GetPageCount(Bounded(() => PdfPages.Extract(pdf, "1"))));
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Stamping_reads_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        Assert.Equal(1, PdfToImage.GetPageCount(Bounded(() => PdfStamper.Stamp(pdf, pdf))));
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Linearizing_reads_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        LinearizationTests.AssertLinearized(Bounded(() => PdfLinearizer.Linearize(pdf)), pages: 1);
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Form_filling_reads_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        Assert.Equal(1, Bounded(() => PdfForm.GetFields(pdf)).Single(f => f.Name == "name").Page);
        var filled = Bounded(() => PdfForm.Fill(pdf, new Dictionary<string, string> { ["name"] = "Filled" }));
        Assert.Equal("Filled", PdfForm.GetFields(filled).Single(f => f.Name == "name").Value);
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public void Signing_and_timestamping_read_the_one_page(string kind)
    {
        var pdf = Tree(kind);
        var options = new PdfSignatureOptions(Signer.Value) { Appearance = new PdfSignatureAppearance { Text = "Signed" } };
        var signed = Bounded(() => PdfSigner.Sign(pdf, options));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Bounded(() => PdfSigner.Sign(pdf, new PdfSignatureOptions(Signer.Value) { Appearance = new PdfSignatureAppearance { Page = 2 } })));

        using var tsa = SignatureVerificationTests.SelfSigned("Page tree TSA", timestamping: true);
        var ltv = Bounded(() => PdfSigner.AddValidationData(signed, new PdfLtvOptions
        {
            Certificates = new[] { Signer.Value },
            TimestampUrl = "http://tsa.example/",
            TimestampHttpClient = new HttpClient(new SignatureTests.FakeTsa(tsa)),
        }));
        var all = PdfSignatureVerifier.Verify(ltv, new PdfSignatureVerificationOptions { TrustedRoots = new X509Certificate2Collection { Signer.Value, tsa } });
        Assert.Equal(2, all.Count);
        Assert.True(all[1].IsDocumentTimestamp);
    }

    [Fact]
    public void A_page_listed_twice_is_one_page_to_the_signer()
    {
        // The signer used to count a page each time the tree listed it, so a signature box on "page 2" went on page 1
        // again; it now counts pages as the renderer and the other writers do.
        var pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 3 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });
        Assert.Equal(1, PdfToImage.GetPageCount(pdf));
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfSigner.Sign(pdf, new PdfSignatureOptions(Signer.Value) { Appearance = new PdfSignatureAppearance { Page = 2 } }));
        Assert.Contains("1 page(s)", ex.Message);
        Assert.NotEmpty(PdfSigner.Sign(pdf, new PdfSignatureOptions(Signer.Value) { Appearance = new PdfSignatureAppearance { Page = 1 } }));
    }
}

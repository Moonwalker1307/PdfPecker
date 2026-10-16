using System.Text;

namespace PdfPecker.Tests;

/// <summary>
/// Builds tiny single-page PDFs by hand for the renderer tests, so each PDF feature can be exercised without a third-party
/// file. Object numbers 1 to 4 are the catalog, page tree, page and content stream; <see cref="Add"/> hands out the rest.
/// </summary>
internal sealed class FixturePdf
{
    private readonly List<byte[]> _objects = new();

    /// <summary>Adds a non-stream object and returns its object number.</summary>
    public int Add(string body)
    {
        _objects.Add(Encoding.Latin1.GetBytes(body));
        return _objects.Count + 4;
    }

    /// <summary>Adds a stream object; <paramref name="entries"/> are the dictionary entries without the /Length.</summary>
    public int AddStream(string entries, byte[] data)
    {
        var head = Encoding.Latin1.GetBytes($"<< {entries} /Length {data.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        _objects.Add(head.Concat(data).Concat(tail).ToArray());
        return _objects.Count + 4;
    }

    public int AddStream(string entries, string latin1Data) => AddStream(entries, Encoding.Latin1.GetBytes(latin1Data));

    /// <summary>The object number the next <see cref="Add"/> or <see cref="AddStream"/> call will return.</summary>
    public int NextNumber => _objects.Count + 5;

    public byte[] Build(int width, int height, string content, string resources = "", string pageExtra = "")
    {
        var all = new List<byte[]>
        {
            Encoding.Latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.Latin1.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.Latin1.GetBytes($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << {resources} >> /Contents 4 0 R {pageExtra} >>"),
            Encoding.Latin1.GetBytes($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"),
        };
        all.AddRange(_objects);
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < all.Count; i++)
        {
            offsets.Add(ms.Position);
            Write($"{i + 1} 0 obj\n");
            ms.Write(all[i]);
            Write("\nendobj\n");
        }
        long xref = ms.Position;
        Write($"xref\n0 {all.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {all.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>Renders the first page at 72 dpi, so one PDF point is one pixel.</summary>
    public static RenderedImage Render(byte[] pdf, bool transparent = false)
        => PdfRenderer.Open(pdf).RenderPage(0, new ImageOptions { Dpi = 72, Transparent = transparent });

    /// <summary>One page of the given size that fills the whole page with the content.</summary>
    public static RenderedImage Render(int width, int height, string content, string resources = "", Action<FixturePdf>? setup = null)
    {
        var pdf = new FixturePdf();
        setup?.Invoke(pdf);
        return Render(pdf.Build(width, height, content, resources));
    }

    /// <summary>The pixel at device coordinates (x from the left, y from the top).</summary>
    public static (int R, int G, int B, int A) Px(RenderedImage img, int x, int y)
    {
        int o = (y * img.Width + x) * 4;
        return (img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2], img.Rgba[o + 3]);
    }

    /// <summary>The pixel at the centre of the PDF-space point (x, y), with y counted up from the page bottom.</summary>
    public static (int R, int G, int B, int A) At(RenderedImage img, double x, double y)
        => Px(img, (int)Math.Floor(x), img.Height - 1 - (int)Math.Floor(y));

    public static void AssertColor((int R, int G, int B, int A) actual, int r, int g, int b, int tolerance = 6, string? what = null)
    {
        Assert.True(Math.Abs(actual.R - r) <= tolerance && Math.Abs(actual.G - g) <= tolerance && Math.Abs(actual.B - b) <= tolerance,
            $"{what}Expected about ({r},{g},{b}) but got ({actual.R},{actual.G},{actual.B})");
    }

    /// <summary>Checks the pixel at a PDF-space point.</summary>
    public static void AssertAt(RenderedImage img, double x, double y, int r, int g, int b, int tolerance = 6)
        => AssertColor(At(img, x, y), r, g, b, tolerance, $"At ({x},{y}): ");

    public static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b.ToString("X2")));
}

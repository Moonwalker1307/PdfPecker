using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>A parsed PDF with its flattened page tree, ready to render pages (thread-safe).</summary>
internal sealed class RenderDocument
{
    internal sealed class PageInfo
    {
        public PdfDict Dict = null!;
        public PdfDict? Resources;
        public double X0, Y0, X1, Y1; // crop box in default user space
        public int Rotate;
        public double UserUnit = 1;
    }

    public readonly PdfFile File;
    public readonly List<PageInfo> Pages = new();
    private readonly RenderCache _cache = new();

    public RenderDocument(byte[] data, string? password)
    {
        File = PdfFile.Open(data, password);
        var root = File.GetDict(File.Catalog, "Pages");
        if (root != null) Collect(root, null, null, null, 0, new HashSet<PdfDict>(ReferenceEqualityComparer.Instance), 0);
    }

    private void Collect(PdfDict node, PdfDict? res, double[]? media, double[]? crop, int rotate, HashSet<PdfDict> seen, int depth)
    {
        if (depth > 64 || !seen.Add(node)) return;
        res = File.GetDict(node, "Resources") ?? res;
        media = Box(File.GetNumbers(node, "MediaBox")) ?? media;
        crop = Box(File.GetNumbers(node, "CropBox")) ?? crop;
        if (node["Rotate"] != null) rotate = File.GetInt(node, "Rotate", 0);
        var kids = File.GetArray(node, "Kids");
        var type = File.GetName(node, "Type");
        if (kids != null && type != "Page")
        {
            foreach (var k in kids.Items)
                if (File.Resolve(k) is PdfDict kid) Collect(kid, res, media, crop, rotate, seen, depth + 1);
            return;
        }
        var m = media ?? new double[] { 0, 0, 612, 792 };
        var c = crop;
        double x0 = m[0], y0 = m[1], x1 = m[2], y1 = m[3];
        if (c != null)
        {
            double cx0 = Math.Max(x0, c[0]), cy0 = Math.Max(y0, c[1]), cx1 = Math.Min(x1, c[2]), cy1 = Math.Min(y1, c[3]);
            if (cx1 > cx0 && cy1 > cy0) (x0, y0, x1, y1) = (cx0, cy0, cx1, cy1);
        }
        if (x1 - x0 < 1) x1 = x0 + 1;
        if (y1 - y0 < 1) y1 = y0 + 1;
        rotate %= 360;
        if (rotate < 0) rotate += 360;
        rotate = rotate / 90 * 90;
        double uu = File.GetNumber(node, "UserUnit", 1);
        Pages.Add(new PageInfo { Dict = node, Resources = res, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Rotate = rotate, UserUnit = uu > 0 ? uu : 1 });
    }

    private static double[]? Box(double[]? b)
    {
        if (b == null || b.Length < 4) return null;
        return new[] { Math.Min(b[0], b[2]), Math.Min(b[1], b[3]), Math.Max(b[0], b[2]), Math.Max(b[1], b[3]) };
    }

    /// <summary>The page size in points as displayed (after /Rotate and /UserUnit).</summary>
    public (double Width, double Height) PageSize(int index)
    {
        var p = Pages[index];
        double w = (p.X1 - p.X0) * p.UserUnit, h = (p.Y1 - p.Y0) * p.UserUnit;
        return p.Rotate % 180 == 0 ? (w, h) : (h, w);
    }

    /// <summary>Renders a page at the given pixels per point (x and y) onto a canvas of premultiplied RGBA pixels.</summary>
    public Canvas Render(int index, double scaleX, double scaleY, int width, int height, uint background, bool annotations, CancellationToken cancel)
    {
        var p = Pages[index];
        // Pages are drawn on a transparent backdrop, as PDF blend modes expect, then put on the background.
        var canvas = new Canvas(width, height, 0);
        double sx = scaleX * p.UserUnit, sy = scaleY * p.UserUnit;
        Matrix m = p.Rotate switch
        {
            90 => new Matrix(0, sy, sx, 0, -p.Y0 * sx, -p.X0 * sy),
            180 => new Matrix(-sx, 0, 0, sy, p.X1 * sx, -p.Y0 * sy),
            270 => new Matrix(0, -sy, -sx, 0, p.Y1 * sx, p.X1 * sy),
            _ => new Matrix(sx, 0, 0, -sy, -p.X0 * sx, p.Y1 * sy),
        };
        var r = new PageRenderer(File, _cache, canvas, cancel);
        using (PdfFile.BeginRender(cancel)) r.RenderPage(p.Dict, p.Resources, m, annotations);
        if (background != 0)
        {
            var px = canvas.Pixels;
            for (int i = 0; i < px.Length; i++)
            {
                uint c = px[i];
                if (c == 0) px[i] = background;
                else if (c < 0xFF000000) px[i] = Canvas.Over(c, background);
            }
        }
        return canvas;
    }
}

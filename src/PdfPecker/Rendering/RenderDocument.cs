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
        PageTree.Walk(File, File.Catalog?["Pages"], new Inherited(null, null, null, 0), Inherit, AddPage);
    }

    /// <summary>What a page inherits, as the renderer reads it: resources or a box that are not usable leave the parent's in place.</summary>
    private readonly record struct Inherited(PdfDict? Resources, double[]? Media, double[]? Crop, int Rotate);

    private Inherited Inherit(PdfDict node, Inherited parent) => new(
        File.GetDict(node, "Resources") ?? parent.Resources,
        Box(File.GetNumbers(node, "MediaBox")) ?? parent.Media,
        Box(File.GetNumbers(node, "CropBox")) ?? parent.Crop,
        node["Rotate"] != null ? File.GetInt(node, "Rotate", 0) : parent.Rotate);

    private bool AddPage(PdfObject _, PdfDict node, Inherited inherited)
    {
        var m = inherited.Media ?? new double[] { 0, 0, 612, 792 };
        var c = inherited.Crop;
        double x0 = m[0], y0 = m[1], x1 = m[2], y1 = m[3];
        if (c != null)
        {
            double cx0 = Math.Max(x0, c[0]), cy0 = Math.Max(y0, c[1]), cx1 = Math.Min(x1, c[2]), cy1 = Math.Min(y1, c[3]);
            if (cx1 > cx0 && cy1 > cy0) (x0, y0, x1, y1) = (cx0, cy0, cx1, cy1);
        }
        if (x1 - x0 < 1) x1 = x0 + 1;
        if (y1 - y0 < 1) y1 = y0 + 1;
        int rotate = inherited.Rotate % 360;
        if (rotate < 0) rotate += 360;
        rotate = rotate / 90 * 90;
        double uu = File.GetNumber(node, "UserUnit", 1);
        Pages.Add(new PageInfo { Dict = node, Resources = inherited.Resources, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Rotate = rotate, UserUnit = uu > 0 ? uu : 1 });
        return true;
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

    /// <summary>The transform from a page's user space to points on the displayed page, origin top left, y downward.</summary>
    private static Matrix PageMatrix(PageInfo p, double sx, double sy) => p.Rotate switch
    {
        90 => new Matrix(0, sy, sx, 0, -p.Y0 * sx, -p.X0 * sy),
        180 => new Matrix(-sx, 0, 0, sy, p.X1 * sx, -p.Y0 * sy),
        270 => new Matrix(0, -sy, -sx, 0, p.Y1 * sx, p.X1 * sy),
        _ => new Matrix(sx, 0, 0, -sy, -p.X0 * sx, p.Y1 * sy),
    };

    /// <summary>The transform from a page's user space to points on the displayed page (after /Rotate and /UserUnit), origin top left.</summary>
    public Matrix DisplayMatrix(int index)
    {
        var p = Pages[index];
        return PageMatrix(p, p.UserUnit, p.UserUnit);
    }

    /// <summary>
    /// Runs a page for its text: the glyphs it shows, in content order, positioned in points on the displayed page (after
    /// /Rotate and /UserUnit), origin top left.
    /// </summary>
    public List<TextGlyph> CollectText(int index, bool annotations, CancellationToken cancel)
        => CollectText(index, annotations, rules: false, cancel).Glyphs;

    /// <summary>
    /// Runs a page for its text as <see cref="CollectText(int, bool, CancellationToken)"/> does and, when
    /// <paramref name="rules"/> is set, also for the straight lines it draws, which outline tables.
    /// </summary>
    public TextCollector CollectText(int index, bool annotations, bool rules, CancellationToken cancel)
    {
        var p = Pages[index];
        var collector = new TextCollector(rules);
        var r = new PageRenderer(File, _cache, collector, cancel);
        using (PdfFile.BeginRender(cancel)) r.RenderPage(p.Dict, p.Resources, PageMatrix(p, p.UserUnit, p.UserUnit), annotations);
        return collector;
    }

    /// <summary>
    /// Runs a page for its images: the images it draws, in content order, with their transforms to points on the
    /// displayed page (after /Rotate and /UserUnit), origin top left, and with <paramref name="clips"/> the box the
    /// clips leave visible of each.
    /// </summary>
    public List<ImageDraw> CollectImages(int index, bool annotations, CancellationToken cancel, bool clips = false)
    {
        var p = Pages[index];
        var collector = new ImageCollector(clips);
        var r = new PageRenderer(File, _cache, collector, cancel);
        using (PdfFile.BeginRender(cancel)) r.RenderPage(p.Dict, p.Resources, PageMatrix(p, p.UserUnit, p.UserUnit), annotations);
        return collector.Draws;
    }

    /// <summary>Renders a page at the given pixels per point (x and y) onto a canvas of premultiplied RGBA pixels.</summary>
    public Canvas Render(int index, double scaleX, double scaleY, int width, int height, uint background, bool annotations, CancellationToken cancel)
    {
        var p = Pages[index];
        // Pages are drawn on a transparent backdrop, as PDF blend modes expect, then put on the background.
        var canvas = new Canvas(width, height, 0);
        double sx = scaleX * p.UserUnit, sy = scaleY * p.UserUnit;
        var m = PageMatrix(p, sx, sy);
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

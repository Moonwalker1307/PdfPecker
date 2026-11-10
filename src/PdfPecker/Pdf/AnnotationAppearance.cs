using System.Globalization;
using System.Text;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Pdf;

/// <summary>
/// Draws annotations that have no appearance stream from their own properties, as viewers do: the markup over text,
/// shapes, lines and ink, a note icon and, for viewers, free text. The PDF/A converter stores the drawing as the
/// annotation's appearance, and the renderer runs it in place of the missing one.
/// </summary>
internal static class AnnotationAppearance
{
    /// <summary>The device colour spaces a drawing uses.</summary>
    [Flags]
    internal enum Spaces : byte { None = 0, Gray = 1, Rgb = 2, Cmyk = 4 }

    /// <summary>A drawn appearance: its content in the coordinates of the page, and what its resources need.</summary>
    internal sealed class Drawing
    {
        public required byte[] Content { get; init; }
        /// <summary>The annotation's rectangle, normalised, which is the appearance's bounding box.</summary>
        public required double[] BBox { get; init; }
        public Spaces Spaces { get; init; }
        /// <summary>Whether the content sets the graphics state /M, which multiplies, as highlighter ink does.</summary>
        public bool Multiply { get; init; }
        /// <summary>The annotation's opacity (/CA), which the graphics state /O sets when it is below 1.</summary>
        public double Opacity { get; init; } = 1;
        /// <summary>Whether the content shows text in the font /Helv, Helvetica.</summary>
        public bool Font { get; init; }
    }

    // The colours viewers draw markup in when the annotation gives none.
    private static readonly double[] Yellow = { 1, 1, 0 }, Black = { 0, 0, 0 }, NoteYellow = { 1, 0.85, 0.2 };

    // A damaged or hostile annotation can list millions of points; the drawing stops growing past this many characters,
    // which is far more than any real markup takes.
    private const int MaxContentLength = 4 << 20;

    // Free text: the most characters laid out, and the padding inside the border, in points.
    private const int MaxFreeTextLength = 64 * 1024;
    private const double FreeTextPadding = 2;

    /// <summary>The standard font free text is shown in; one dictionary, so the renderer loads the font once.</summary>
    private static readonly PdfDict Helvetica = new(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
    {
        ["Type"] = PdfName.Get("Font"),
        ["Subtype"] = PdfName.Get("Type1"),
        ["BaseFont"] = PdfName.Get("Helvetica"),
        ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
    });

    /// <summary>
    /// The drawing of an annotation without an appearance, or null for the types whose look depends on the viewer and
    /// for an annotation that would draw nothing. <paramref name="viewer"/> adds what a viewer shows but PDF/A cannot
    /// keep as drawn here: free text, which needs a font that is not embedded, and the annotation's opacity.
    /// </summary>
    public static Drawing? Draw(PdfFile file, PdfDict annot, string subtype, bool viewer)
    {
        var rect = file.GetNumbers(annot, "Rect");
        if (rect == null || rect.Length < 4) return null;
        double x0 = Math.Min(rect[0], rect[2]), y0 = Math.Min(rect[1], rect[3]), x1 = Math.Max(rect[0], rect[2]), y1 = Math.Max(rect[1], rect[3]);
        if (!(double.IsFinite(x0) && double.IsFinite(y0) && double.IsFinite(x1) && double.IsFinite(y1))) return null;
        var colour = file.GetNumbers(annot, "C");
        var interior = file.GetNumbers(annot, "IC");
        var bs = file.GetDict(annot, "BS");
        var borderArray = file.Resolve(annot["Border"]) as PdfArray;
        double width = file.GetNumber(bs, "W", borderArray is { Count: >= 3 } ? file.Num(borderArray[2], 1) : 1);
        if (!double.IsFinite(width)) width = 1;
        string? dash = Dash(file, bs, borderArray);
        var b = new StringBuilder();
        var space = Spaces.None;
        bool multiply = false, font = false;
        string? Colour(double[]? c, bool forStroke)
        {
            if (c == null) return null;
            string Num(double v) => Math.Round(Math.Clamp(v, 0, 1), 4).ToString(CultureInfo.InvariantCulture);
            switch (c.Length)
            {
                case 1:
                    space |= Spaces.Gray;
                    return Num(c[0]) + (forStroke ? " G\n" : " g\n");
                case 3:
                    space |= Spaces.Rgb;
                    return Num(c[0]) + " " + Num(c[1]) + " " + Num(c[2]) + (forStroke ? " RG\n" : " rg\n");
                case 4:
                    space |= Spaces.Cmyk;
                    return Num(c[0]) + " " + Num(c[1]) + " " + Num(c[2]) + " " + Num(c[3]) + (forStroke ? " K\n" : " k\n");
                default:
                    return null;
            }
        }
        var quads = file.GetNumbers(annot, "QuadPoints");
        switch (subtype)
        {
            case "Highlight" when quads != null && quads.Length >= 8:
                if (Colour(colour ?? Yellow, false) is not { } highlight) return null;
                multiply = true;
                b.Append("/M gs\n").Append(highlight);
                for (int i = 0; i + 8 <= quads.Length && b.Length < MaxContentLength; i += 8)
                    b.Append(P(quads[i])).Append(' ').Append(P(quads[i + 1])).Append(" m ").Append(P(quads[i + 2])).Append(' ').Append(P(quads[i + 3]))
                     .Append(" l ").Append(P(quads[i + 6])).Append(' ').Append(P(quads[i + 7])).Append(" l ").Append(P(quads[i + 4])).Append(' ')
                     .Append(P(quads[i + 5])).Append(" l h\n");
                b.Append("f\n");
                break;
            case "Underline" or "StrikeOut" or "Squiggly" when quads != null && quads.Length >= 8:
                if (Colour(colour ?? Black, true) is not { } markup) return null;
                b.Append(markup);
                for (int i = 0; i + 8 <= quads.Length && b.Length < MaxContentLength; i += 8)
                {
                    // The quadrilateral's bottom edge runs from its third point to its fourth, its top from the first to the second.
                    double bx0 = quads[i + 4], by0 = quads[i + 5], bx1 = quads[i + 6], by1 = quads[i + 7];
                    double tx0 = quads[i], ty0 = quads[i + 1];
                    double height = Math.Sqrt((tx0 - bx0) * (tx0 - bx0) + (ty0 - by0) * (ty0 - by0));
                    double lw = Math.Max(0.5, height / 14);
                    b.Append(P(lw)).Append(" w\n");
                    double t = subtype == "StrikeOut" ? 0.4 : 0.08;
                    double sx0 = bx0 + (tx0 - bx0) * t, sy0 = by0 + (ty0 - by0) * t;
                    double sx1 = bx1 + (quads[i + 2] - bx1) * t, sy1 = by1 + (quads[i + 3] - by1) * t;
                    if (subtype == "Squiggly")
                    {
                        double length = Math.Sqrt((sx1 - sx0) * (sx1 - sx0) + (sy1 - sy0) * (sy1 - sy0));
                        int steps = Math.Clamp((int)(length / Math.Max(1, height / 6)), 1, 2000);
                        double nx = -(sy1 - sy0) / Math.Max(length, 1e-9) * height / 12, ny = (sx1 - sx0) / Math.Max(length, 1e-9) * height / 12;
                        b.Append(P(sx0)).Append(' ').Append(P(sy0)).Append(" m\n");
                        for (int k = 1; k <= steps; k++)
                        {
                            double f = (double)k / steps, s = (k & 1) == 1 ? 1 : -1;
                            b.Append(P(sx0 + (sx1 - sx0) * f + nx * s)).Append(' ').Append(P(sy0 + (sy1 - sy0) * f + ny * s)).Append(" l\n");
                        }
                        b.Append("S\n");
                    }
                    else b.Append(P(sx0)).Append(' ').Append(P(sy0)).Append(" m ").Append(P(sx1)).Append(' ').Append(P(sy1)).Append(" l S\n");
                }
                break;
            case "Square" or "Circle":
            {
                var stroke = width > 0 ? Colour(colour, true) : null;
                var fill = Colour(interior, false);
                if (stroke == null && fill == null) return null;
                if (stroke != null) b.Append(stroke).Append(P(width)).Append(" w\n");
                if (stroke != null && dash != null) b.Append(dash);
                if (fill != null) b.Append(fill);
                // The rectangle differences (/RD) say how far inside the rectangle the shape is drawn.
                var rd = file.GetNumbers(annot, "RD");
                double inset = stroke != null ? width / 2 : 0;
                double ax0 = x0 + inset, ay0 = y0 + inset, ax1 = x1 - inset, ay1 = y1 - inset;
                if (rd is { Length: 4 } && rd.All(v => double.IsFinite(v) && v >= 0) && rd[0] + rd[2] < x1 - x0 && rd[1] + rd[3] < y1 - y0)
                {
                    ax0 += rd[0]; ay0 += rd[1]; ax1 -= rd[2]; ay1 -= rd[3];
                }
                if (subtype == "Square")
                    b.Append(P(ax0)).Append(' ').Append(P(ay0)).Append(' ').Append(P(ax1 - ax0)).Append(' ').Append(P(ay1 - ay0)).Append(" re\n");
                else
                    Ellipse(b, (ax0 + ax1) / 2, (ay0 + ay1) / 2, (ax1 - ax0) / 2, (ay1 - ay0) / 2);
                b.Append(stroke != null && fill != null ? "B\n" : stroke != null ? "S\n" : "f\n");
                break;
            }
            case "Line" or "Ink" or "Polygon" or "PolyLine":
            {
                if (Colour(colour, true) is not { } pen || width <= 0) return null;
                b.Append(pen).Append(P(width)).Append(" w 1 J 1 j\n");
                if (dash != null && subtype != "Ink") b.Append(dash);
                var inside = subtype == "Polygon" ? Colour(interior, false) : null;
                if (inside != null) b.Append(inside);
                var paths = new List<double[]>();
                if (subtype == "Line" && file.GetNumbers(annot, "L") is { Length: 4 } line) paths.Add(line);
                else if (subtype == "Ink" && file.GetArray(annot, "InkList") is { } ink)
                    foreach (var item in ink.Items.Take(1000))
                        if (file.ToNumbers(file.Resolve(item) as PdfArray) is { Length: >= 2 } points) paths.Add(points);
                if (subtype is "Polygon" or "PolyLine" && file.GetNumbers(annot, "Vertices") is { Length: >= 4 } vertices) paths.Add(vertices);
                if (paths.Count == 0) return null;
                foreach (var points in paths)
                {
                    for (int i = 0; i + 1 < points.Length && i < 20000 && b.Length < MaxContentLength; i += 2)
                        b.Append(P(points[i])).Append(' ').Append(P(points[i + 1])).Append(i == 0 ? " m\n" : " l\n");
                    if (subtype == "Polygon") b.Append("h\n");
                }
                b.Append(inside != null ? "B\n" : "S\n");
                if (subtype is "Line" or "PolyLine" && paths[0].Length >= 4)
                    LineEndings(file, annot, b, paths[0], width, dash != null, Colour(interior, false));
                break;
            }
            case "Text":
            {
                // A note icon: a page with a folded corner and three lines, in the annotation's colour.
                var paper = Colour(colour ?? NoteYellow, false) ?? "1 0.85 0.2 rg\n";
                space |= Spaces.Gray;
                double w = Math.Min(x1 - x0, 20), h = Math.Min(y1 - y0, 20);
                double top = y1;
                b.Append(paper).Append("0 G 0.6 w\n")
                 .Append(P(x0 + 1)).Append(' ').Append(P(top - h + 1)).Append(" m ").Append(P(x0 + w - 1)).Append(' ').Append(P(top - h + 1)).Append(" l ")
                 .Append(P(x0 + w - 1)).Append(' ').Append(P(top - 6)).Append(" l ").Append(P(x0 + w - 6)).Append(' ').Append(P(top - 1)).Append(" l ")
                 .Append(P(x0 + 1)).Append(' ').Append(P(top - 1)).Append(" l h B\n");
                for (int i = 1; i <= 3; i++)
                {
                    double ly = top - h * i / 4.5;
                    b.Append(P(x0 + 4)).Append(' ').Append(P(ly)).Append(" m ").Append(P(x0 + w - 5)).Append(' ').Append(P(ly)).Append(" l S\n");
                }
                break;
            }
            case "FreeText" when viewer:
            {
                var da = ReadDefaultAppearance(file, annot);
                // A border is drawn only when the annotation asks for one: free text written without an appearance
                // rarely means the default border of one point.
                bool border = (bs != null || borderArray != null) && width > 0;
                var fill = Colour(interior, false);
                var pen = border ? Colour(da.Stroke ?? da.Fill ?? Black, true) : null;
                if (fill != null || pen != null)
                {
                    if (fill != null) b.Append(fill);
                    if (pen != null) b.Append(pen).Append(P(width)).Append(" w\n");
                    if (pen != null && dash != null) b.Append(dash);
                    double inset = pen != null ? width / 2 : 0;
                    b.Append(P(x0 + inset)).Append(' ').Append(P(y0 + inset)).Append(' ').Append(P(x1 - x0 - 2 * inset)).Append(' ')
                     .Append(P(y1 - y0 - 2 * inset)).Append(" re\n").Append(fill != null && pen != null ? "B\n" : pen != null ? "S\n" : "f\n");
                }
                var text = (file.Resolve(annot["Contents"]) as PdfString)?.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    double pad = FreeTextPadding + (pen != null ? width : 0);
                    int align = file.GetInt(annot, "Q", 0);
                    var lines = WrapText(text.Length > MaxFreeTextLength ? text[..MaxFreeTextLength] : text, da.Size, x1 - x0 - 2 * pad);
                    double leading = da.Size * 1.15, baseline = y1 - pad - da.Size * StandardFontMetrics.Helvetica_Ascent / 1000.0;
                    font = true;
                    b.Append("BT\n/Helv ").Append(P(da.Size)).Append(" Tf\n").Append(Colour(da.Fill ?? Black, false));
                    foreach (var (bytes, lineWidth) in lines)
                    {
                        if (baseline < y0 - leading || b.Length >= MaxContentLength) break;
                        double x = align switch
                        {
                            1 => x0 + (x1 - x0 - lineWidth) / 2,
                            2 => x1 - pad - lineWidth,
                            _ => x0 + pad,
                        };
                        b.Append("1 0 0 1 ").Append(P(x)).Append(' ').Append(P(baseline)).Append(" Tm\n");
                        AppendString(b, bytes);
                        b.Append(" Tj\n");
                        baseline -= leading;
                    }
                    b.Append("ET\n");
                }
                if (b.Length == 0) return null;
                break;
            }
            default:
                return null;
        }
        double opacity = viewer ? Math.Clamp(file.GetNumber(annot, "CA", 1), 0, 1) : 1;
        if (double.IsNaN(opacity)) opacity = 1;
        if (opacity == 0) return null;
        if (opacity < 1) b.Insert(0, "/O gs\n");
        return new Drawing
        {
            Content = Encoding.Latin1.GetBytes(b.ToString()),
            BBox = new[] { x0, y0, x1, y1 },
            Spaces = space,
            Multiply = multiply,
            Opacity = opacity,
            Font = font,
        };
    }

    /// <summary>
    /// The resources a drawing's content uses: /M for multiplied highlighter ink, /O for the annotation's opacity and
    /// /Helv for free text.
    /// </summary>
    public static PdfDict Resources(Drawing drawing)
    {
        var resources = new PdfDict();
        if (drawing.Multiply || drawing.Opacity < 1)
        {
            var states = new PdfDict();
            if (drawing.Multiply)
                states.Entries["M"] = new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
                {
                    ["Type"] = PdfName.Get("ExtGState"),
                    ["BM"] = PdfName.Get("Multiply"),
                });
            if (drawing.Opacity < 1)
                states.Entries["O"] = new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal)
                {
                    ["Type"] = PdfName.Get("ExtGState"),
                    ["CA"] = PdfNumber.Real(drawing.Opacity),
                    ["ca"] = PdfNumber.Real(drawing.Opacity),
                });
            resources.Entries["ExtGState"] = states;
        }
        if (drawing.Font)
            resources.Entries["Font"] = new PdfDict(new Dictionary<string, PdfObject>(StringComparer.Ordinal) { ["Helv"] = Helvetica });
        return resources;
    }

    private static string P(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    /// <summary>The dash pattern of a dashed border (/BS /S /D, or the fourth entry of /Border) as a content operator.</summary>
    private static string? Dash(PdfFile file, PdfDict? bs, PdfArray? border)
    {
        double[]? pattern = null;
        if (bs != null && file.GetName(bs, "S") == "D") pattern = file.GetNumbers(bs, "D") ?? new double[] { 3 };
        else if (bs == null && border is { Count: >= 4 } && file.Resolve(border[3]) is PdfArray dashes) pattern = file.ToNumbers(dashes);
        // A pattern of nothing but gaps, or of unreadable lengths, would hide the line; it is drawn solid instead.
        if (pattern == null || pattern.Length is 0 or > 16 || pattern.Any(v => !double.IsFinite(v) || v < 0) || pattern.Sum() <= 0) return null;
        return "[" + string.Join(" ", pattern.Select(P)) + "] 0 d\n";
    }

    /// <summary>Four Bézier arcs, each a quarter of the ellipse.</summary>
    private static void Ellipse(StringBuilder b, double cx, double cy, double rx, double ry)
    {
        const double k = 0.5523;
        b.Append(P(cx + rx)).Append(' ').Append(P(cy)).Append(" m\n")
         .Append(P(cx + rx)).Append(' ').Append(P(cy + ry * k)).Append(' ').Append(P(cx + rx * k)).Append(' ').Append(P(cy + ry)).Append(' ').Append(P(cx)).Append(' ').Append(P(cy + ry)).Append(" c\n")
         .Append(P(cx - rx * k)).Append(' ').Append(P(cy + ry)).Append(' ').Append(P(cx - rx)).Append(' ').Append(P(cy + ry * k)).Append(' ').Append(P(cx - rx)).Append(' ').Append(P(cy)).Append(" c\n")
         .Append(P(cx - rx)).Append(' ').Append(P(cy - ry * k)).Append(' ').Append(P(cx - rx * k)).Append(' ').Append(P(cy - ry)).Append(' ').Append(P(cx)).Append(' ').Append(P(cy - ry)).Append(" c\n")
         .Append(P(cx + rx * k)).Append(' ').Append(P(cy - ry)).Append(' ').Append(P(cx + rx)).Append(' ').Append(P(cy - ry * k)).Append(' ').Append(P(cx + rx)).Append(' ').Append(P(cy)).Append(" c h\n");
    }

    /// <summary>
    /// The line endings (/LE) at the two ends of a line or polyline: arrows, squares, circles, diamonds, butts and
    /// slashes, filled with the interior colour where they are closed.
    /// </summary>
    private static void LineEndings(PdfFile file, PdfDict annot, StringBuilder b, double[] points, double width, bool dashed, string? fill)
    {
        if (file.GetArray(annot, "LE") is not { Count: >= 2 } le) return;
        int last = (points.Length / 2 - 1) * 2;
        var ends = new (string? Style, double X, double Y, double FromX, double FromY)[]
        {
            ((file.Resolve(le[0]) as PdfName)?.Value, points[0], points[1], points[2], points[3]),
            ((file.Resolve(le[1]) as PdfName)?.Value, points[last], points[last + 1], points[last - 2], points[last - 1]),
        };
        bool solid = false;
        // The fill colour is set before any ending's path, as no colour may be set inside a path.
        if (fill != null) b.Append(fill);
        double size = Math.Max(6, width * 5);
        foreach (var (style, x, y, fromX, fromY) in ends)
        {
            if (style is null or "None") continue;
            double length = Math.Sqrt((x - fromX) * (x - fromX) + (y - fromY) * (y - fromY));
            if (!(length > 1e-9) || !double.IsFinite(length)) continue;
            // The direction the line runs out of this end, and the one across it.
            double dx = (x - fromX) / length, dy = (y - fromY) / length, nx = -dy, ny = dx;
            if (dashed && !solid)
            {
                b.Append("[] 0 d\n");
                solid = true;
            }
            double h = size / 2;
            switch (style)
            {
                case "OpenArrow" or "ClosedArrow":
                    Arrow(b, x, y, x - dx * size, y - dy * size, nx * h, ny * h, style == "ClosedArrow", fill);
                    break;
                case "ROpenArrow" or "RClosedArrow":
                    Arrow(b, x, y, x + dx * size, y + dy * size, nx * h, ny * h, style == "RClosedArrow", fill);
                    break;
                case "Square":
                    b.Append(P(x - dx * h - nx * h)).Append(' ').Append(P(y - dy * h - ny * h)).Append(" m ")
                     .Append(P(x + dx * h - nx * h)).Append(' ').Append(P(y + dy * h - ny * h)).Append(" l ")
                     .Append(P(x + dx * h + nx * h)).Append(' ').Append(P(y + dy * h + ny * h)).Append(" l ")
                     .Append(P(x - dx * h + nx * h)).Append(' ').Append(P(y - dy * h + ny * h)).Append(" l h\n");
                    Close(b, fill);
                    break;
                case "Circle":
                    Ellipse(b, x, y, h, h);
                    Close(b, fill);
                    break;
                case "Diamond":
                    b.Append(P(x - dx * h)).Append(' ').Append(P(y - dy * h)).Append(" m ").Append(P(x + nx * h)).Append(' ').Append(P(y + ny * h)).Append(" l ")
                     .Append(P(x + dx * h)).Append(' ').Append(P(y + dy * h)).Append(" l ").Append(P(x - nx * h)).Append(' ').Append(P(y - ny * h)).Append(" l h\n");
                    Close(b, fill);
                    break;
                case "Butt":
                    b.Append(P(x - nx * h)).Append(' ').Append(P(y - ny * h)).Append(" m ").Append(P(x + nx * h)).Append(' ').Append(P(y + ny * h)).Append(" l S\n");
                    break;
                case "Slash":
                {
                    // A short line through the end, 30 degrees off the perpendicular.
                    double sx = nx * 0.866 + dx * 0.5, sy = ny * 0.866 + dy * 0.5;
                    b.Append(P(x - sx * h)).Append(' ').Append(P(y - sy * h)).Append(" m ").Append(P(x + sx * h)).Append(' ').Append(P(y + sy * h)).Append(" l S\n");
                    break;
                }
            }
        }
    }

    private static void Arrow(StringBuilder b, double tipX, double tipY, double baseX, double baseY, double nx, double ny, bool closed, string? fill)
    {
        b.Append(P(baseX + nx)).Append(' ').Append(P(baseY + ny)).Append(" m ").Append(P(tipX)).Append(' ').Append(P(tipY)).Append(" l ")
         .Append(P(baseX - nx)).Append(' ').Append(P(baseY - ny)).Append(" l");
        if (!closed)
        {
            b.Append(" S\n");
            return;
        }
        b.Append(" h\n");
        Close(b, fill);
    }

    /// <summary>Paints a closed line ending: filled with the interior colour when there is one, and stroked.</summary>
    private static void Close(StringBuilder b, string? fill) => b.Append(fill == null ? "S\n" : "B\n");

    /// <summary>The font size and colours of free text's default appearance string (/DA).</summary>
    private static (double Size, double[]? Fill, double[]? Stroke) ReadDefaultAppearance(PdfFile file, PdfDict annot)
    {
        var bytes = file.GetBytes(annot, "DA") ?? file.GetBytes(file.GetDict(file.Catalog, "AcroForm"), "DA");
        double size = 12;
        double[]? fill = null, stroke = null;
        if (bytes != null)
        {
            var numbers = new List<double>();
            foreach (var token in Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                {
                    numbers.Add(v);
                    continue;
                }
                int n = numbers.Count;
                switch (token)
                {
                    case "Tf" when n >= 1: size = numbers[n - 1]; break;
                    case "g" when n >= 1: fill = numbers.GetRange(n - 1, 1).ToArray(); break;
                    case "rg" when n >= 3: fill = numbers.GetRange(n - 3, 3).ToArray(); break;
                    case "k" when n >= 4: fill = numbers.GetRange(n - 4, 4).ToArray(); break;
                    case "G" when n >= 1: stroke = numbers.GetRange(n - 1, 1).ToArray(); break;
                    case "RG" when n >= 3: stroke = numbers.GetRange(n - 3, 3).ToArray(); break;
                    case "K" when n >= 4: stroke = numbers.GetRange(n - 4, 4).ToArray(); break;
                }
                if (!token.StartsWith('/')) numbers.Clear();
            }
        }
        // A size of 0 asks the viewer to fit the text; it is shown at the usual size instead.
        if (!(size > 0) || !double.IsFinite(size)) size = 12;
        return (Math.Clamp(size, 1, 300), fill, stroke);
    }

    /// <summary>Free text broken into lines that fit <paramref name="width"/>: each line's WinAnsi bytes and its width.</summary>
    private static List<(byte[] Bytes, double Width)> WrapText(string text, double size, double width)
    {
        var lines = new List<(byte[], double)>();
        var line = new List<byte>();
        double lineWidth = 0;
        double Advance(byte c) => (StandardFontMetrics.Helvetica[c] is > 0 and var w ? w : 278) * size / 1000;
        void Flush()
        {
            while (line.Count > 0 && line[^1] == (byte)' ')
            {
                lineWidth -= Advance((byte)' ');
                line.RemoveAt(line.Count - 1);
            }
            lines.Add((line.ToArray(), Math.Max(0, lineWidth)));
            line.Clear();
            lineWidth = 0;
        }
        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\r', '\n');
        foreach (var paragraph in paragraphs)
        {
            foreach (var word in paragraph.Split(' '))
            {
                var bytes = new byte[word.Length];
                double wordWidth = 0;
                for (int i = 0; i < word.Length; i++)
                {
                    bytes[i] = word[i] == '\t' ? (byte)' ' : WinAnsi.Encode(word[i]);
                    wordWidth += Advance(bytes[i]);
                }
                double space = line.Count > 0 ? Advance((byte)' ') : 0;
                if (line.Count > 0 && lineWidth + space + wordWidth > width) Flush();
                else if (line.Count > 0)
                {
                    line.Add((byte)' ');
                    lineWidth += space;
                }
                // A word longer than the line is broken between characters.
                foreach (var c in bytes)
                {
                    double advance = Advance(c);
                    if (line.Count > 0 && lineWidth + advance > width && lineWidth > 0 && line[^1] != (byte)' ') Flush();
                    line.Add(c);
                    lineWidth += advance;
                }
            }
            Flush();
        }
        return lines;
    }

    /// <summary>Appends a literal string, escaping what the content syntax needs.</summary>
    private static void AppendString(StringBuilder b, byte[] bytes)
    {
        b.Append('(');
        foreach (var c in bytes)
        {
            if (c is (byte)'(' or (byte)')' or (byte)'\\') b.Append('\\').Append((char)c);
            else if (c < 0x20) b.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else b.Append((char)c);
        }
        b.Append(')');
    }
}

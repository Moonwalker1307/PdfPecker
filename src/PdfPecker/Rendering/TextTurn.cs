using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Html;

namespace PdfPecker.Rendering;

/// <summary>
/// Turns a page whose text runs down, up or upside down, as on a page with /Rotate 90 or a landscape page drawn on its
/// side, so that its text is upright. Finding columns, boxes and tables assumes upright lines, so a page is read turned
/// and its lines and words are turned back onto the page as displayed.
/// </summary>
internal readonly struct TextTurn
{
    /// <summary>The turn in degrees clockwise on the displayed page (y down) that the page's text runs in: 0, 90, 180 or 270.</summary>
    public readonly int Degrees;

    /// <summary>The displayed page's size.</summary>
    private readonly double _width, _height;

    /// <summary>From the displayed page to the turned one, and back.</summary>
    private readonly Matrix _turn, _back;

    private TextTurn(int degrees, double width, double height)
    {
        Degrees = degrees;
        _width = width;
        _height = height;
        // Each matrix maps the direction the text runs in to the x axis and the page onto the box from the origin.
        _turn = degrees switch
        {
            90 => new Matrix(0, -1, 1, 0, 0, width),
            180 => new Matrix(-1, 0, 0, -1, width, height),
            270 => new Matrix(0, 1, -1, 0, height, 0),
            _ => Matrix.Identity,
        };
        _turn.TryInvert(out _back);
    }

    /// <summary>Whether the page is read turned.</summary>
    public bool Turned => Degrees != 0;

    /// <summary>The page's width when turned.</summary>
    public double Width => Degrees % 180 == 0 ? _width : _height;

    /// <summary>The page's height when turned.</summary>
    public double Height => Degrees % 180 == 0 ? _height : _width;

    /// <summary>
    /// The turn that makes more than half of a page's letters upright, on a page with /Rotate (<paramref name="rotate"/>)
    /// or one without upright text. Text in other directions on a mostly upright page, such as a turned table header or a label
    /// up the margin, leaves the page as it is. So does an upright heading over vertical writing, as in Chinese or
    /// Japanese, which is read before the writing below it only while the page is upright.
    /// </summary>
    public static TextTurn Of(List<TextGlyph> glyphs, double width, double height, int rotate)
    {
        Span<int> counts = stackalloc int[4];
        foreach (var g in glyphs)
        {
            if (string.IsNullOrWhiteSpace(g.Text)) continue;
            double angle = Math.Atan2(g.DirY, g.DirX) * 180 / Math.PI;
            int quarter = (int)Math.Round(angle / 90);
            // Text at an angle between the quarters, such as a stamp at 30 degrees, is read as it is.
            if (Math.Abs(angle - quarter * 90) > 5) continue;
            counts[(quarter % 4 + 4) % 4]++;
        }
        int most = 0, all = counts[0];
        for (int q = 1; q < 4; q++)
        {
            all += counts[q];
            if (counts[q] > counts[most]) most = q;
        }
        // A page of text boxes turned every way, as a poster or a test page can be, has no way up to read it in.
        bool clear = counts[most] * 2 > all;
        return new TextTurn(clear && (rotate != 0 || counts[0] == 0) ? most * 90 : 0, width, height);
    }

    /// <summary>Turns the glyphs, lines, bullets and fills the page drew.</summary>
    public void Apply(TextCollector collected)
    {
        var glyphs = collected.Glyphs;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            (g.X, g.Y) = _turn.Apply(g.X, g.Y);
            (g.EndX, g.EndY) = _turn.Apply(g.EndX, g.EndY);
            (g.DirX, g.DirY) = (_turn.A * g.DirX + _turn.C * g.DirY, _turn.B * g.DirX + _turn.D * g.DirY);
            glyphs[i] = g;
        }
        if (collected.Rules is { } rules)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                var (ax, ay) = r.Horizontal ? _turn.Apply(r.Start, r.Position) : _turn.Apply(r.Position, r.Start);
                var (bx, by) = r.Horizontal ? _turn.Apply(r.End, r.Position) : _turn.Apply(r.Position, r.End);
                bool horizontal = r.Horizontal == (Degrees % 180 == 0);
                rules[i] = horizontal
                    ? r with { Horizontal = true, Position = ay, Start = Math.Min(ax, bx), End = Math.Max(ax, bx) }
                    : r with { Horizontal = false, Position = ax, Start = Math.Min(ay, by), End = Math.Max(ay, by) };
            }
        }
        if (collected.Bullets is { } bullets)
            for (int i = 0; i < bullets.Count; i++) bullets[i] = Turn(bullets[i]);
        if (collected.Fills is { } fills)
        {
            for (int i = 0; i < fills.Count; i++)
            {
                var f = fills[i];
                var (x0, y0, x1, y1) = Turn((f.X0, f.Y0, f.X1, f.Y1));
                fills[i] = f with { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };
            }
        }
    }

    /// <summary>A box on the displayed page, turned.</summary>
    public (double X0, double Y0, double X1, double Y1) Turn((double X0, double Y0, double X1, double Y1) box) => Map(_turn, box);

    /// <summary>A link's box on the displayed page, turned.</summary>
    public PageLink Turn(PageLink link)
    {
        var (x0, y0, x1, y1) = Turn((link.X, link.Y, link.X + link.Width, link.Y + link.Height));
        return link with { X = x0, Y = y0, Width = x1 - x0, Height = y1 - y0 };
    }

    /// <summary>The lines and words of a page read turned, put back on the page as displayed.</summary>
    public PdfTextPage Back(PdfTextPage page)
    {
        var lines = new List<PdfTextLine>(page.Lines.Count);
        foreach (var line in page.Lines)
        {
            var words = new List<PdfTextWord>(line.Words.Count);
            foreach (var w in line.Words)
            {
                var (x0, y0, x1, y1) = Map(_back, (w.X, w.Y, w.X + w.Width, w.Y + w.Height));
                var (ox, oy) = _back.Apply(w.OriginX, w.OriginY);
                words.Add(new PdfTextWord(w.Text, x0, y0, x1 - x0, y1 - y0, w.FontSize, w.FontName)
                {
                    OriginX = ox, OriginY = oy, Advance = w.Advance, Color = w.Color, ColorRuns = w.ColorRuns,
                });
            }
            var (lx0, ly0, lx1, ly1) = Map(_back, (line.X, line.Y, line.X + line.Width, line.Y + line.Height));
            // Angles are counterclockwise, and the page is turned back clockwise.
            int angle = ((int)Math.Round(line.Angle) - Degrees) % 360;
            if (angle < 0) angle += 360;
            lines.Add(new PdfTextLine(line.Text, words, lx0, ly0, lx1 - lx0, ly1 - ly0, angle, line.RightToLeft)
            {
                Baseline = line.Baseline, Size = line.Size, ParagraphBefore = line.ParagraphBefore,
            });
        }
        return new PdfTextPage(page.PageNumber, _width, _height, page.Text, lines);
    }

    private static (double X0, double Y0, double X1, double Y1) Map(in Matrix m, (double X0, double Y0, double X1, double Y1) box)
    {
        var (ax, ay) = m.Apply(box.X0, box.Y0);
        var (bx, by) = m.Apply(box.X1, box.Y1);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }
}

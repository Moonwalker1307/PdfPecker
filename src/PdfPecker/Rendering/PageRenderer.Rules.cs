using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering;

internal sealed partial class PageRenderer
{
    /// <summary>
    /// Records the straight horizontal and vertical lines the current path draws, for finding the borders of tables.
    /// A stroke gives each of its straight segments that runs along an axis; a fill gives each of its subpaths that is
    /// thin enough to be a line, which is how most PDF writers draw table borders, and the top and bottom edges of wider
    /// ones. Curves are left out.
    /// </summary>
    private void CollectRules(bool stroke)
    {
        var collector = _text!;
        var gs = _gs;
        if (_hidden > 0 || (stroke ? gs.StrokeAlpha : gs.FillAlpha) <= 0) return;
        var ctm = gs.Ctm;
        var verbs = _path.Verbs;
        var pts = _path.Points;
        int pi = 0;
        double sx = 0, sy = 0, cx = 0, cy = 0; // subpath start and current point, on the page
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool open = false;
        for (int i = 0; i <= verbs.Count; i++)
        {
            byte verb = i < verbs.Count ? verbs[i] : PathData.MoveTo;
            if (verb == PathData.MoveTo)
            {
                if (open && !stroke) AddFilledRule(collector, x0, y0, x1, y1);
                if (i == verbs.Count) break;
                (sx, sy) = ctm.Apply(pts[pi], pts[pi + 1]);
                pi += 2;
                (cx, cy) = (sx, sy);
                (x0, y0, x1, y1) = (sx, sy, sx, sy);
                open = true;
                continue;
            }
            double nx, ny;
            switch (verb)
            {
                case PathData.LineTo:
                    (nx, ny) = ctm.Apply(pts[pi], pts[pi + 1]);
                    pi += 2;
                    break;
                case PathData.CubicTo:
                    // A curve is not a line of a table; its points still bound a filled shape.
                    for (int k = 0; k < 6; k += 2)
                    {
                        var (px, py) = ctm.Apply(pts[pi + k], pts[pi + k + 1]);
                        x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
                    }
                    (cx, cy) = ctm.Apply(pts[pi + 4], pts[pi + 5]);
                    pi += 6;
                    continue;
                default: // Close
                    (nx, ny) = (sx, sy);
                    break;
            }
            if (stroke) AddStrokedRule(collector, cx, cy, nx, ny);
            x0 = Math.Min(x0, nx); y0 = Math.Min(y0, ny); x1 = Math.Max(x1, nx); y1 = Math.Max(y1, ny);
            (cx, cy) = (nx, ny);
        }
    }

    private static void AddFilledRule(TextCollector rules, double x0, double y0, double x1, double y1)
    {
        double w = x1 - x0, h = y1 - y0;
        if (w is >= 1.5 and <= TextCollector.MaxBullet && h is >= 1.5 and <= TextCollector.MaxBullet && w <= 2 * h && h <= 2 * w)
            rules.AddBullet(x0, y0, x1, y1);
        if (w >= h && h <= TextCollector.MaxRuleThickness && w > 2 * h && w >= 2)
            rules.AddRule(new PageRule(true, (y0 + y1) / 2, x0, x1));
        else if (h > w && w <= TextCollector.MaxRuleThickness && h > 2 * w && h >= 2)
            rules.AddRule(new PageRule(false, (x0 + x1) / 2, y0, y1));
        else if (w > TextCollector.MaxRuleThickness && h > TextCollector.MaxRuleThickness)
        {
            // The top and bottom of a background, such as a table row's, can stand for the lines between rows.
            rules.AddRule(new PageRule(true, y0, x0, x1, Edge: true));
            rules.AddRule(new PageRule(true, y1, x0, x1, Edge: true));
        }
    }

    private static void AddStrokedRule(TextCollector rules, double ax, double ay, double bx, double by)
    {
        double dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay);
        if (dy <= 0.5 && dx >= 2) rules.AddRule(new PageRule(true, (ay + by) / 2, Math.Min(ax, bx), Math.Max(ax, bx)));
        else if (dx <= 0.5 && dy >= 2) rules.AddRule(new PageRule(false, (ax + bx) / 2, Math.Min(ay, by), Math.Max(ay, by)));
    }
}

using PdfPecker.Rendering.Content;
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
        // A pattern or shading has no one colour to give a background.
        bool plain = (stroke ? gs.StrokeSpace : gs.FillSpace) is not PatternSpace;
        uint color = stroke ? gs.StrokeRgb : gs.FillRgb;
        double width = gs.LineWidth * Math.Sqrt(Math.Abs(ctm.A * ctm.D - ctm.B * ctm.C));
        var verbs = _path.Verbs;
        var pts = _path.Points;
        int pi = 0;
        double sx = 0, sy = 0, cx = 0, cy = 0; // subpath start and current point, on the page
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool open = false;
        // How far the curves of the current subpath run: the four corners of a rounded box run its radius across and
        // down each, however finely the writer splits them.
        double arcs = 0;
        // Whether the straight sides of the current subpath all run along an axis, as a box's do and a turned one's don't.
        bool upright = true;
        int fills = collector.Fills?.Count ?? 0;
        for (int i = 0; i <= verbs.Count; i++)
        {
            byte verb = i < verbs.Count ? verbs[i] : PathData.MoveTo;
            if (verb == PathData.MoveTo)
            {
                if (open && !stroke) AddFilledRule(collector, x0, y0, x1, y1, color, plain && upright, new ClipBoxes(gs.ClipBoxes), arcs / 8);
                if (i == verbs.Count) break;
                arcs = 0;
                upright = true;
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
                {
                    var (ex, ey) = ctm.Apply(pts[pi + 4], pts[pi + 5]);
                    arcs += Math.Abs(ex - cx) + Math.Abs(ey - cy);
                    // A curve is not a line of a table; its points still bound a filled shape.
                    for (int k = 0; k < 6; k += 2)
                    {
                        var (px, py) = ctm.Apply(pts[pi + k], pts[pi + k + 1]);
                        x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
                    }
                    (cx, cy) = (ex, ey);
                    pi += 6;
                    continue;
                }
                default: // Close
                    (nx, ny) = (sx, sy);
                    break;
            }
            if (stroke) AddStrokedRule(collector, cx, cy, nx, ny, color, width, gs.Dash is { Length: > 0 });
            if (Math.Abs(nx - cx) > 0.5 && Math.Abs(ny - cy) > 0.5) upright = false;
            x0 = Math.Min(x0, nx); y0 = Math.Min(y0, ny); x1 = Math.Max(x1, nx); y1 = Math.Max(y1, ny);
            (cx, cy) = (nx, ny);
        }
        if (collector.Fills != null) DropFrames(collector.Fills, fills);
    }

    /// <summary>
    /// Removes the fills of one path, from <paramref name="start"/> on, that are a frame: a box with another inside it,
    /// as a border drawn as a filled ring is, colours the edge of the box and not the box.
    /// </summary>
    private static void DropFrames(List<PageFill> fills, int start)
    {
        // A path of hundreds of boxes is a pattern or a chart, not a frame, and comparing its boxes pairwise would be slow.
        if (fills.Count - start is < 2 or > 64) return;
        var frame = new bool[fills.Count - start];
        for (int i = start; i < fills.Count; i++)
            for (int k = start; k < fills.Count; k++)
            {
                var a = fills[i];
                var b = fills[k];
                if (i != k && a.X0 <= b.X0 + 0.01 && a.Y0 <= b.Y0 + 0.01 && a.X1 >= b.X1 - 0.01 && a.Y1 >= b.Y1 - 0.01)
                    frame[i - start] = frame[k - start] = true;
            }
        for (int i = fills.Count - 1; i >= start; i--)
            if (frame[i - start]) fills.RemoveAt(i);
    }

    private static void AddFilledRule(TextCollector rules, double x0, double y0, double x1, double y1, uint color, bool plain, in ClipBoxes clip, double radius)
    {
        double w = x1 - x0, h = y1 - y0;
        if (w is >= 1.5 and <= TextCollector.MaxBullet && h is >= 1.5 and <= TextCollector.MaxBullet && w <= 2 * h && h <= 2 * w)
            rules.AddBullet(x0, y0, x1, y1);
        if (w >= h && h <= TextCollector.MaxRuleThickness && w > 2 * h && w >= 2)
            rules.AddRule(new PageRule(true, (y0 + y1) / 2, x0, x1, Color: color, Width: h));
        else if (h > w && w <= TextCollector.MaxRuleThickness && h > 2 * w && h >= 2)
            rules.AddRule(new PageRule(false, (x0 + x1) / 2, y0, y1, Color: color, Width: w));
        else if (w > TextCollector.MaxRuleThickness && h > TextCollector.MaxRuleThickness)
        {
            // The top and bottom of a background, such as a table row's, can stand for the lines between rows.
            rules.AddRule(new PageRule(true, y0, x0, x1, Edge: true));
            rules.AddRule(new PageRule(true, y1, x0, x1, Edge: true));
            if (plain && clip.Paints((x0 + x1) / 2, (y0 + y1) / 2)) rules.AddFill(new PageFill(x0, y0, x1, y1, color, Math.Min(radius, Math.Min(w, h) / 2)));
        }
    }

    private static void AddStrokedRule(TextCollector rules, double ax, double ay, double bx, double by, uint color, double width, bool dashed)
    {
        double dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay);
        if (dy <= 0.5 && dx >= 2) rules.AddRule(new PageRule(true, (ay + by) / 2, Math.Min(ax, bx), Math.Max(ax, bx), Color: color, Width: width, Dashed: dashed));
        else if (dx <= 0.5 && dy >= 2) rules.AddRule(new PageRule(false, (ax + bx) / 2, Math.Min(ay, by), Math.Max(ay, by), Color: color, Width: width, Dashed: dashed));
    }

    /// <summary>The clip boxes in force with those of the current path's subpaths added, at most a few dozen levels.</summary>
    private (double X0, double Y0, double X1, double Y1)[][]? AddClipBoxes((double X0, double Y0, double X1, double Y1)[][]? boxes)
    {
        if (_path.IsEmpty || boxes is { Length: >= 32 }) return boxes;
        var ctm = _gs.Ctm;
        var verbs = _path.Verbs;
        var pts = _path.Points;
        var level = new List<(double, double, double, double)>();
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        int pi = 0;
        bool open = false;
        for (int i = 0; i <= verbs.Count; i++)
        {
            byte verb = i < verbs.Count ? verbs[i] : PathData.MoveTo;
            int count = verb switch { PathData.MoveTo or PathData.LineTo => 1, PathData.CubicTo => 3, _ => 0 };
            if (verb == PathData.MoveTo)
            {
                if (open && level.Count < 64) level.Add((x0, y0, x1, y1));
                if (i == verbs.Count) break;
                open = false;
            }
            for (int k = 0; k < count; k++, pi += 2)
            {
                var (px, py) = ctm.Apply(pts[pi], pts[pi + 1]);
                if (!open) { (x0, y0, x1, y1) = (px, py, px, py); open = true; }
                else { x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py); }
            }
        }
        if (level.Count == 0) return boxes;
        var result = new (double, double, double, double)[(boxes?.Length ?? 0) + 1][];
        boxes?.CopyTo(result, 0);
        result[^1] = level.ToArray();
        return result;
    }
}

/// <summary>
/// The clips in force as boxes, one set of subpath boxes per clip: a point is painted when, for every clip, it lies in
/// an odd number of its boxes, so a clip of a box with a hole in it leaves the hole out.
/// </summary>
internal readonly struct ClipBoxes((double X0, double Y0, double X1, double Y1)[][]? levels)
{
    public bool Paints(double x, double y)
    {
        if (levels == null) return true;
        foreach (var level in levels)
        {
            int inside = 0;
            foreach (var b in level)
                if (x >= b.X0 && x <= b.X1 && y >= b.Y0 && y <= b.Y1) inside++;
            if (inside % 2 == 0) return false;
        }
        return true;
    }

    /// <summary>The box no clip reaches outside of: within the outer box of every clip's subpaths; null for no clip.</summary>
    public (double X0, double Y0, double X1, double Y1)? Bounds()
    {
        if (levels == null) return null;
        double x0 = double.MinValue, y0 = double.MinValue, x1 = double.MaxValue, y1 = double.MaxValue;
        foreach (var level in levels)
        {
            if (level.Length == 0) continue;
            x0 = Math.Max(x0, level.Min(b => b.X0));
            y0 = Math.Max(y0, level.Min(b => b.Y0));
            x1 = Math.Min(x1, level.Max(b => b.X1));
            y1 = Math.Min(y1, level.Max(b => b.Y1));
        }
        return x0 == double.MinValue ? null : (x0, y0, x1, y1);
    }
}

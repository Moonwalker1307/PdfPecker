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

    private static void AddFilledRule(TextCollector rules, double x0, double y0, double x1, double y1, uint color, bool plain, in ClipBoxes clip, double radius,
        bool bullet = true)
    {
        double w = x1 - x0, h = y1 - y0;
        if (bullet && w is >= 1.5 and <= TextCollector.MaxBullet && h is >= 1.5 and <= TextCollector.MaxBullet && w <= 2 * h && h <= 2 * w)
            rules.AddBullet(x0, y0, x1, y1);
        if (w >= h && h <= TextCollector.MaxRuleThickness && w > 2 * h && w >= 2)
            rules.AddRule(new PageRule(true, (y0 + y1) / 2, x0, x1, Color: color, Width: h));
        else if (h > w && w <= TextCollector.MaxRuleThickness && h > 2 * w && h >= 2)
            rules.AddRule(new PageRule(false, (x0 + x1) / 2, y0, y1, Color: color, Width: w));
        else if (w > TextCollector.MaxRuleThickness && h > TextCollector.MaxRuleThickness)
        {
            // Inset, outset, groove and ridge borders fill a cell's whole box through a clip of one side's strip, so the
            // part of a box the clip leaves is what the page shows: a strip of it is a line, not the box's edges. Each
            // part is a line or an area; a bullet is drawn as a shape of its own.
            if (clip.Pieces(x0, y0, x1, y1) is { } pieces)
            {
                foreach (var (px0, py0, px1, py1) in pieces)
                    AddFilledRule(rules, px0, py0, px1, py1, color, plain, default, radius, bullet: false);
                return;
            }
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

    /// <summary>
    /// The clip boxes in force with those of the current path's subpaths added, at most a few dozen levels. The boxes are
    /// read under the even-odd rule; a clip under the nonzero rule is turned into boxes that paint the same.
    /// </summary>
    private (double X0, double Y0, double X1, double Y1)[][]? AddClipBoxes((double X0, double Y0, double X1, double Y1)[][]? boxes, bool evenOdd)
    {
        if (_path.IsEmpty || boxes is { Length: >= 32 }) return boxes;
        var ctm = _gs.Ctm;
        var verbs = _path.Verbs;
        var pts = _path.Points;
        var level = new List<(double X0, double Y0, double X1, double Y1)>();
        var turns = new List<int>(); // which way each subpath runs, from the sign of its area
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        double sx = 0, sy = 0, lx = 0, ly = 0, area = 0; // the subpath's start, its last point and twice its area
        int pi = 0;
        bool open = false;
        for (int i = 0; i <= verbs.Count; i++)
        {
            byte verb = i < verbs.Count ? verbs[i] : PathData.MoveTo;
            int count = verb switch { PathData.MoveTo or PathData.LineTo => 1, PathData.CubicTo => 3, _ => 0 };
            if (verb == PathData.MoveTo)
            {
                if (open && level.Count < 64)
                {
                    level.Add((x0, y0, x1, y1));
                    area += lx * sy - sx * ly;
                    // A point at infinity leaves the area NaN, which has no sign; such a subpath is taken to run neither way.
                    turns.Add(double.IsNaN(area) ? 0 : Math.Sign(area));
                }
                if (i == verbs.Count) break;
                open = false;
            }
            for (int k = 0; k < count; k++, pi += 2)
            {
                var (px, py) = ctm.Apply(pts[pi], pts[pi + 1]);
                if (!open) { (x0, y0, x1, y1) = (px, py, px, py); (sx, sy) = (px, py); area = 0; open = true; }
                else
                {
                    x0 = Math.Min(x0, px); y0 = Math.Min(y0, py); x1 = Math.Max(x1, px); y1 = Math.Max(y1, py);
                    area += lx * py - px * ly;
                }
                (lx, ly) = (px, py);
            }
        }
        if (level.Count == 0) return boxes;
        if (!evenOdd && level.Count >= 2) level = NonzeroBoxes(level, turns);
        var result = new (double, double, double, double)[(boxes?.Length ?? 0) + 1][];
        boxes?.CopyTo(result, 0);
        result[^1] = level.ToArray();
        return result;
    }

    /// <summary>
    /// The boxes that paint, under the even-odd rule, what the subpath boxes of a clip under the nonzero rule paint: a box
    /// inside another that runs the same way adds nothing, and one that runs the other way cuts a hole. Where two boxes
    /// overlap without one holding the other, the boxes are left as they are.
    /// </summary>
    private static List<(double X0, double Y0, double X1, double Y1)> NonzeroBoxes(List<(double X0, double Y0, double X1, double Y1)> boxes, List<int> turns)
    {
        int n = boxes.Count;
        static double Area((double X0, double Y0, double X1, double Y1) b) => (b.X1 - b.X0) * (b.Y1 - b.Y0);
        // Each box's parent: the smallest box that holds it, where of equal boxes the earlier holds the later.
        var parent = new int[n];
        for (int i = 0; i < n; i++)
        {
            parent[i] = -1;
            var b = boxes[i];
            for (int k = 0; k < n; k++)
            {
                if (k == i) continue;
                var a = boxes[k];
                bool holds = a.X0 <= b.X0 && a.Y0 <= b.Y0 && a.X1 >= b.X1 && a.Y1 >= b.Y1;
                bool held = b.X0 <= a.X0 && b.Y0 <= a.Y0 && b.X1 >= a.X1 && b.Y1 >= a.Y1;
                if (holds && held) holds = k < i;
                if (holds)
                {
                    if (parent[i] < 0 || Area(a) < Area(boxes[parent[i]]) || Area(a) == Area(boxes[parent[i]]) && k > parent[i]) parent[i] = k;
                }
                else if (!held && Math.Min(a.X1, b.X1) > Math.Max(a.X0, b.X0) && Math.Min(a.Y1, b.Y1) > Math.Max(a.Y0, b.Y0)) return boxes;
            }
        }
        // The winding number inside each box, outer boxes first; a box stays where it paints otherwise than its parent.
        var order = Enumerable.Range(0, n).OrderByDescending(i => Area(boxes[i])).ThenBy(i => i).ToList();
        var winding = new int[n];
        var result = new List<(double X0, double Y0, double X1, double Y1)>(n);
        var keep = new bool[n];
        foreach (int i in order)
        {
            winding[i] = turns[i] + (parent[i] >= 0 ? winding[parent[i]] : 0);
            keep[i] = (winding[i] != 0) != (parent[i] >= 0 && winding[parent[i]] != 0);
        }
        for (int i = 0; i < n; i++) if (keep[i]) result.Add(boxes[i]);
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

    /// <summary>Clip edges this close to a box's, in points, do not cut it.</summary>
    private const double Slack = 0.01;

    /// <summary>The most parts a box is cut into; more are a pattern of clips rather than a border.</summary>
    private const int MaxPieces = 16;

    /// <summary>
    /// The parts of a box the clips paint, when they cut it down: a clip of one box keeps the part inside it, and a clip
    /// of a box with another inside it, a frame, the part between them. Null when the clips leave the box whole, or are of
    /// a shape these boxes cannot follow, which leaves it whole too.
    /// </summary>
    public List<(double X0, double Y0, double X1, double Y1)>? Pieces(double x0, double y0, double x1, double y1)
    {
        if (levels == null) return null;
        var pieces = new List<(double X0, double Y0, double X1, double Y1)> { (x0, y0, x1, y1) };
        bool cut = false;
        foreach (var level in levels)
        {
            (double X0, double Y0, double X1, double Y1) outer, hole;
            if (level.Length == 1) (outer, hole) = (level[0], default);
            else if (level.Length == 2 && Holds(level[0], level[1])) (outer, hole) = (level[0], level[1]);
            else if (level.Length == 2 && Holds(level[1], level[0])) (outer, hole) = (level[1], level[0]);
            else return null;
            var next = new List<(double X0, double Y0, double X1, double Y1)>(pieces.Count);
            foreach (var p in pieces)
            {
                // A clip drawn along the box's own edges leaves it as it is.
                var c = (X0: outer.X0 > p.X0 + Slack ? outer.X0 : p.X0, Y0: outer.Y0 > p.Y0 + Slack ? outer.Y0 : p.Y0,
                    X1: outer.X1 < p.X1 - Slack ? outer.X1 : p.X1, Y1: outer.Y1 < p.Y1 - Slack ? outer.Y1 : p.Y1);
                if (c != p) cut = true;
                if (c.X1 - c.X0 <= Slack || c.Y1 - c.Y0 <= Slack) continue;
                if (level.Length == 1 || hole.X0 >= c.X1 - Slack || hole.X1 <= c.X0 + Slack || hole.Y0 >= c.Y1 - Slack || hole.Y1 <= c.Y0 + Slack)
                {
                    next.Add(c);
                    continue;
                }
                // What lies around the hole: the bands above and below it across the whole part, and beside it between them.
                cut = true;
                double top = Math.Max(c.Y0, hole.Y0), bottom = Math.Min(c.Y1, hole.Y1);
                if (top - c.Y0 > Slack) next.Add((c.X0, c.Y0, c.X1, top));
                if (c.Y1 - bottom > Slack) next.Add((c.X0, bottom, c.X1, c.Y1));
                if (hole.X0 - c.X0 > Slack) next.Add((c.X0, top, hole.X0, bottom));
                if (c.X1 - hole.X1 > Slack) next.Add((hole.X1, top, c.X1, bottom));
            }
            if (next.Count > MaxPieces) return null;
            pieces = next;
        }
        return cut ? pieces : null;

        static bool Holds((double X0, double Y0, double X1, double Y1) a, (double X0, double Y0, double X1, double Y1) b)
            => a.X0 <= b.X0 && a.Y0 <= b.Y0 && a.X1 >= b.X1 && a.Y1 >= b.Y1;
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

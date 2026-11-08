namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Finds Data Matrix codes in an image. A symbol's solid L and its dense modules make one connected dark shape, so the
/// detector labels the dark shapes, keeps those whose convex hull nearly fills their smallest enclosing rectangle,
/// finds the two solid sides of the L among the rectangle's sides, and reads the grid for each symbol size whose
/// timing pattern matches. It finds symbols at any rotation.
/// </summary>
internal static class DataMatrixDetector
{
    /// <summary>The most runs labelled, and the most shapes examined, so a noisy image stays bounded.</summary>
    private const int MaxRuns = 6_000_000, MaxCandidates = 400;

    public static void Find(BinaryImage image, List<Detection> results, int maxResults, CancellationToken cancellationToken)
    {
        var shapes = Label(image, cancellationToken);
        if (shapes == null) return;
        int examined = 0;
        foreach (var shape in shapes)
        {
            if (results.Count >= maxResults || examined++ >= MaxCandidates || image.BudgetSpent) break;
            cancellationToken.ThrowIfCancellationRequested();
            // A shape inside a code already found is one of its pieces.
            if (Inside(results, shape)) continue;
            if (TryShape(image, shape) is { } detection) results.Add(detection);
        }
    }

    private static bool Inside(List<Detection> results, Shape shape)
    {
        var centre = new Point2((shape.Left + shape.Right) / 2.0, (shape.Top + shape.Bottom) / 2.0);
        foreach (var r in results)
        {
            if (Contains(r.Corners, centre)) return true;
        }
        return false;
    }

    internal static bool Contains(Point2[] polygon, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].Y > p.Y) != (polygon[j].Y > p.Y)
                && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>A connected dark shape: its pixel count, bounding box and the outline points of its rows.</summary>
    private sealed class Shape
    {
        public int Pixels, Left = int.MaxValue, Top = int.MaxValue, Right, Bottom;
        public List<Point2> Outline = new();
    }

    /// <summary>
    /// Labels the dark pixels in 8-connected shapes from their runs, with a union-find over the runs of neighbouring
    /// rows, and returns the shapes big and dense enough to be a symbol, the largest first. Null when the image has too
    /// many runs.
    /// </summary>
    private static List<Shape>? Label(BinaryImage image, CancellationToken cancellationToken)
    {
        int width = image.Width, height = image.Height;
        var starts = new List<int>();
        var ends = new List<int>();
        var rowFirst = new int[height + 1];
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            rowFirst[y] = starts.Count;
            int row = y * width;
            int x = 0;
            while (x < width)
            {
                while (x < width && image.Bits[row + x] == 0) x++;
                if (x == width) break;
                int start = x;
                while (x < width && image.Bits[row + x] != 0) x++;
                starts.Add(start);
                ends.Add(x);
                if (starts.Count > MaxRuns) return null;
            }
        }
        rowFirst[height] = starts.Count;

        int count = starts.Count;
        var parent = new int[count];
        for (int i = 0; i < count; i++) parent[i] = i;
        for (int y = 1; y < height; y++)
        {
            int a = rowFirst[y - 1], aEnd = rowFirst[y], b = rowFirst[y], bEnd = rowFirst[y + 1];
            while (a < aEnd && b < bEnd)
            {
                // Runs touch, diagonally included, when each starts no later than one past the other's end.
                if (starts[a] <= ends[b] && starts[b] <= ends[a]) Union(parent, a, b);
                if (ends[a] < ends[b]) a++;
                else b++;
            }
        }

        var byRoot = new Dictionary<int, Shape>();
        for (int y = 0; y < height; y++)
        {
            for (int r = rowFirst[y]; r < rowFirst[y + 1]; r++)
            {
                int root = Find(parent, r);
                if (!byRoot.TryGetValue(root, out var shape)) byRoot[root] = shape = new Shape();
                shape.Pixels += ends[r] - starts[r];
                shape.Left = Math.Min(shape.Left, starts[r]);
                shape.Right = Math.Max(shape.Right, ends[r]);
                shape.Top = Math.Min(shape.Top, y);
                shape.Bottom = Math.Max(shape.Bottom, y + 1);
            }
        }

        var candidates = new List<Shape>();
        foreach (var shape in byRoot.Values)
        {
            int w = shape.Right - shape.Left, h = shape.Bottom - shape.Top;
            if (w < 8 || h < 8 || Math.Max(w, h) > 20 * Math.Min(w, h)) continue;
            double fill = (double)shape.Pixels / ((double)w * h);
            if (fill < 0.15 || fill > 0.92) continue;
            candidates.Add(shape);
        }
        candidates.Sort((p, q) => q.Pixels.CompareTo(p.Pixels));
        if (candidates.Count > MaxCandidates) candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);
        if (candidates.Count == 0) return candidates;

        // The outline of each kept shape: the ends of its leftmost and rightmost run in each row.
        var kept = new Dictionary<int, (Shape Shape, int[] Min, int[] Max)>();
        var chosen = new HashSet<Shape>(candidates);
        foreach (var (root, shape) in byRoot)
        {
            if (chosen.Contains(shape))
            {
                int h = shape.Bottom - shape.Top;
                var min = new int[h];
                var max = new int[h];
                Array.Fill(min, int.MaxValue);
                Array.Fill(max, int.MinValue);
                kept[root] = (shape, min, max);
            }
        }
        for (int y = 0; y < height; y++)
        {
            for (int r = rowFirst[y]; r < rowFirst[y + 1]; r++)
            {
                if (!kept.TryGetValue(Find(parent, r), out var entry)) continue;
                int i = y - entry.Shape.Top;
                entry.Min[i] = Math.Min(entry.Min[i], starts[r]);
                entry.Max[i] = Math.Max(entry.Max[i], ends[r]);
            }
        }
        foreach (var (shape, min, max) in kept.Values)
        {
            for (int i = 0; i < min.Length; i++)
            {
                if (min[i] == int.MaxValue) continue;
                int y = shape.Top + i;
                shape.Outline.Add(new Point2(min[i], y));
                shape.Outline.Add(new Point2(min[i], y + 1));
                shape.Outline.Add(new Point2(max[i], y));
                shape.Outline.Add(new Point2(max[i], y + 1));
            }
        }
        return candidates;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
    }

    private static Detection? TryShape(BinaryImage image, Shape shape)
    {
        var hull = ConvexHull(shape.Outline);
        if (hull.Count < 4) return null;
        var rect = MinimumRectangle(hull, out double rectArea);
        if (rectArea < 64) return null;
        double hullArea = Area(hull);
        // Two outlines of the symbol: the smallest rectangle around it, exact for a straight-on view, and the largest
        // quadrilateral on its hull, which follows a symbol seen at an angle.
        var quad = LargestQuadrilateral(hull, rect);
        double quadArea = Area([.. quad]);
        foreach (var (outline, area) in (ReadOnlySpan<(Point2[], double)>)[(rect, rectArea), (quad, quadArea)])
        {
            // The hull of a symbol fills most of its outline: the timing modules on the top and right sides only join
            // the rest through the data next to them, so a corner can fall short. About half of the outline is dark.
            if (Math.Min(hullArea, area) < 0.6 * Math.Max(hullArea, area) || shape.Pixels < 0.2 * area || shape.Pixels > 0.92 * area)
                continue;
            if (TryOutline(image, outline) is { } detection) return detection;
        }
        return null;
    }

    private static Detection? TryOutline(BinaryImage image, Point2[] outline)
    {
        // The solid sides of the L are dark all along, just inside the outline.
        Span<double> darkness = stackalloc double[4];
        for (int i = 0; i < 4; i++) darkness[i] = SideDarkness(image, outline[i], outline[(i + 1) % 4], outline[(i + 3) % 4], outline[(i + 2) % 4]);
        // Side i runs from corner i to corner i + 1, so corner k joins sides k - 1 and k.
        int bestCorner = -1;
        double bestScore = 0;
        for (int k = 0; k < 4; k++)
        {
            double a = darkness[(k + 3) % 4], b = darkness[k];
            if (a < 0.75 || b < 0.75) continue;
            double score = a + b - darkness[(k + 1) % 4] - darkness[(k + 2) % 4];
            if (score > bestScore)
            {
                bestScore = score;
                bestCorner = k;
            }
        }
        if (bestCorner < 0) return null;

        var corner = outline[bestCorner];
        var before = outline[(bestCorner + 3) % 4];
        var after = outline[(bestCorner + 1) % 4];
        var opposite = outline[(bestCorner + 2) % 4];
        // With y growing downward, bottom-right, bottom-left and top-left turn clockwise, unless the code is mirrored.
        foreach (bool mirrored in (ReadOnlySpan<bool>)[false, true])
        {
            Point2 bottomRight, topLeft;
            if ((Point2.Cross(corner, after, before) < 0) != mirrored) (bottomRight, topLeft) = (after, before);
            else (bottomRight, topLeft) = (before, after);
            if (TryCorners(image, corner, bottomRight, topLeft, opposite) is { } detection) return detection;
        }
        return null;
    }

    /// <summary>
    /// The quadrilateral of largest area with its corners on hull vertices, found by moving each corner of the one
    /// nearest to the rectangle's corners while that grows it.
    /// </summary>
    private static Point2[] LargestQuadrilateral(List<Point2> hull, Point2[] rect)
    {
        Span<int> index = stackalloc int[4];
        for (int k = 0; k < 4; k++)
        {
            double best = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                double d = (hull[i].X - rect[k].X) * (hull[i].X - rect[k].X) + (hull[i].Y - rect[k].Y) * (hull[i].Y - rect[k].Y);
                if (d < best)
                {
                    best = d;
                    index[k] = i;
                }
            }
        }
        Point2[] Quad(ReadOnlySpan<int> ix) => [hull[ix[0]], hull[ix[1]], hull[ix[2]], hull[ix[3]]];
        double area = Area([.. Quad(index)]);
        for (int round = 0; round < 8; round++)
        {
            bool improved = false;
            for (int k = 0; k < 4; k++)
            {
                int keep = index[k];
                for (int i = 0; i < hull.Count; i++)
                {
                    index[k] = i;
                    double a = Area([.. Quad(index)]);
                    if (a > area + 1e-9)
                    {
                        area = a;
                        keep = i;
                        improved = true;
                    }
                }
                index[k] = keep;
            }
            if (!improved) break;
        }
        return Quad(index);
    }

    private static Detection? TryCorners(BinaryImage image, Point2 bottomLeft, Point2 bottomRight, Point2 topLeft, Point2 topRight)
    {
        double widthPx = Point2.Distance(bottomLeft, bottomRight), heightPx = Point2.Distance(bottomLeft, topLeft);
        // The outline's fourth corner can fall short where the top right module is light; the parallelogram of the
        // other three is the second guess.
        var parallelogram = new Point2(topLeft.X + bottomRight.X - bottomLeft.X, topLeft.Y + bottomRight.Y - bottomLeft.Y);
        var sizes = new List<(int Index, double Score)>();
        for (int i = 0; i < DataMatrixDecoder.Sizes.Length; i++)
        {
            var size = DataMatrixDecoder.Sizes[i];
            double ratio = widthPx / heightPx / ((double)size.Columns / size.Rows);
            if (ratio < 0.75 || ratio > 1.33 || widthPx / size.Columns < 1.0 || heightPx / size.Rows < 1.0) continue;
            var transform = Transform(size, topLeft, parallelogram, bottomRight, bottomLeft);
            double score = Math.Max(TimingScore(image, transform, size), TimingScore(image, Transform(size, topLeft, topRight, bottomRight, bottomLeft), size));
            if (score >= 0.6) sizes.Add((i, score));
        }
        sizes.Sort((a, b) => b.Score.CompareTo(a.Score));
        for (int s = 0; s < Math.Min(3, sizes.Count); s++)
        {
            var size = DataMatrixDecoder.Sizes[sizes[s].Index];
            foreach (var corner in (ReadOnlySpan<Point2>)[topRight, parallelogram])
            {
                Span<Point2> corners = [topLeft, corner, bottomRight, bottomLeft];
                Refine(image, size, corners);
                var transform = Transform(size, corners[0], corners[1], corners[2], corners[3]);
                if (transform.Sample(image, size.Columns, size.Rows) is not { } grid) continue;
                if (DataMatrixDecoder.Decode(grid, sizes[s].Index) is { } content)
                {
                    return new Detection(PdfPecker.BarcodeFormat.DataMatrix, content,
                        [transform.Map(0, 0), transform.Map(size.Columns, 0), transform.Map(size.Columns, size.Rows), transform.Map(0, size.Rows)]);
                }
            }
        }
        return null;
    }

    private static Perspective Transform(DataMatrixDecoder.SymbolSize size, Point2 tl, Point2 tr, Point2 br, Point2 bl)
        => Perspective.Between(new(0, 0), new(size.Columns, 0), new(size.Columns, size.Rows), new(0, size.Rows), tl, tr, br, bl);

    /// <summary>
    /// Nudges the corners, a quarter of a module at a time, while that makes more of the finder and timing modules read
    /// as they should: the outline of a symbol in a photo is blurred and its fourth corner only estimated.
    /// </summary>
    private static void Refine(BinaryImage image, DataMatrixDecoder.SymbolSize size, Span<Point2> corners)
    {
        double module = (Point2.Distance(corners[3], corners[2]) / size.Columns + Point2.Distance(corners[3], corners[0]) / size.Rows) / 2;
        double step = module / 4;
        double best = PatternScore(image, Transform(size, corners[0], corners[1], corners[2], corners[3]), size);
        ReadOnlySpan<(int X, int Y)> moves = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (int round = 0; round < 12 && best < 1; round++)
        {
            bool improved = false;
            for (int k = 0; k < 4; k++)
            {
                foreach (var (mx, my) in moves)
                {
                    var keep = corners[k];
                    corners[k] = new Point2(keep.X + mx * step, keep.Y + my * step);
                    double score = PatternScore(image, Transform(size, corners[0], corners[1], corners[2], corners[3]), size);
                    if (score > best)
                    {
                        best = score;
                        improved = true;
                    }
                    else corners[k] = keep;
                }
            }
            if (!improved) break;
        }
    }

    /// <summary>The share of the finder and timing modules around each data region that read as they should.</summary>
    private static double PatternScore(BinaryImage image, Perspective transform, DataMatrixDecoder.SymbolSize size)
    {
        if (!image.Spend(2L * (size.Rows + size.Columns))) return 0;
        int matched = 0, total = 0;
        int blockY = size.RegionRows + 2, blockX = size.RegionColumns + 2;
        for (int y = 0; y < size.Rows; y++)
        {
            int localY = y % blockY;
            bool rowPattern = localY == 0 || localY == blockY - 1;
            for (int x = 0; x < size.Columns; x++)
            {
                int localX = x % blockX;
                if (!rowPattern && localX != 0 && localX != blockX - 1) continue;
                bool expected = localX == 0 || localY == blockY - 1 || (localY == 0 ? localX % 2 == 0 : localY % 2 == 1);
                var p = transform.Map(x + 0.5, y + 0.5);
                if (image.Get((int)Math.Floor(p.X), (int)Math.Floor(p.Y)) == expected) matched++;
                total++;
            }
        }
        return (double)matched / total;
    }

    /// <summary>The share of the top and right timing modules that read as the size predicts.</summary>
    private static double TimingScore(BinaryImage image, Perspective transform, DataMatrixDecoder.SymbolSize size)
    {
        if (!image.Spend(size.Rows + size.Columns)) return 0;
        int matched = 0, total = 0;
        for (int x = 0; x < size.Columns; x++)
        {
            int localX = x % (size.RegionColumns + 2);
            var p = transform.Map(x + 0.5, 0.5);
            if (image.Get((int)p.X, (int)p.Y) == (localX % 2 == 0)) matched++;
            total++;
        }
        for (int y = 0; y < size.Rows; y++)
        {
            int localY = y % (size.RegionRows + 2);
            var p = transform.Map(size.Columns - 0.5, y + 0.5);
            if (image.Get((int)p.X, (int)p.Y) == (localY % 2 == 1)) matched++;
            total++;
        }
        return (double)matched / total;
    }

    /// <summary>
    /// How dark a side from <paramref name="a"/> to <paramref name="b"/> is: the best share of dark samples on lines
    /// parallel to it, a little way in toward the opposite corners.
    /// </summary>
    private static double SideDarkness(BinaryImage image, Point2 a, Point2 b, Point2 aInner, Point2 bInner)
    {
        double length = Point2.Distance(a, b), depth = Point2.Distance(a, aInner);
        if (length < 1 || depth < 1) return 0;
        int samples = Math.Clamp((int)length, 16, 400);
        // The solid side is a module thick. Every symbol has at least 8 modules each way, so a module is at most an
        // eighth of the shorter side, and lines up to half that deep stay inside it.
        int insets = Math.Clamp((int)(Math.Min(length, depth) / 16), 1, 12);
        double best = 0;
        for (int step = 0; step < insets; step++)
        {
            double t = (step + 0.5) / depth;
            int dark = 0;
            for (int i = 0; i < samples; i++)
            {
                double u = 0.05 + 0.9 * i / (samples - 1);
                double x = a.X + (b.X - a.X) * u, y = a.Y + (b.Y - a.Y) * u;
                double ix = aInner.X + (bInner.X - aInner.X) * u, iy = aInner.Y + (bInner.Y - aInner.Y) * u;
                x += (ix - x) * t;
                y += (iy - y) * t;
                if (image.Get((int)Math.Floor(x), (int)Math.Floor(y))) dark++;
            }
            best = Math.Max(best, (double)dark / samples);
        }
        return best;
    }

    /// <summary>The convex hull, counter-clockwise on screen, by Andrew's monotone chain.</summary>
    private static List<Point2> ConvexHull(List<Point2> points)
    {
        var sorted = points.Distinct().ToList();
        sorted.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        if (sorted.Count < 3) return sorted;
        var hull = new List<Point2>(sorted.Count * 2);
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Point2.Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lower && Point2.Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Area(List<Point2> polygon)
    {
        double sum = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            sum += polygon[j].X * polygon[i].Y - polygon[i].X * polygon[j].Y;
        return Math.Abs(sum) / 2;
    }

    /// <summary>
    /// The smallest rectangle around the hull, which has one side along a hull edge; returns its corners in order
    /// around it.
    /// </summary>
    private static Point2[] MinimumRectangle(List<Point2> hull, out double bestArea)
    {
        bestArea = double.MaxValue;
        var best = new Point2[4];
        for (int i = 0; i < hull.Count; i++)
        {
            var p = hull[i];
            var q = hull[(i + 1) % hull.Count];
            double ex = q.X - p.X, ey = q.Y - p.Y, length = Math.Sqrt(ex * ex + ey * ey);
            if (length < 1e-9) continue;
            ex /= length;
            ey /= length;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var h in hull)
            {
                double u = (h.X - p.X) * ex + (h.Y - p.Y) * ey, v = -(h.X - p.X) * ey + (h.Y - p.Y) * ex;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }
            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                Point2 At(double u, double v) => new(p.X + u * ex - v * ey, p.Y + u * ey + v * ex);
                best = [At(minU, minV), At(maxU, minV), At(maxU, maxV), At(minU, maxV)];
            }
        }
        return best;
    }
}

namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Finds QR codes in an image: the three finder patterns (dark, light, dark, light, dark in the ratio 1:1:3:1:1 in every
/// direction), every set of three that could be the corners of one code, the alignment pattern near the fourth corner,
/// and the grid of modules in between. An image may hold many codes; each set of finder patterns is used once.
/// </summary>
internal static class QrDetector
{
    /// <summary>The most finder pattern candidates kept, and the most sets of three tried, so a busy image stays bounded.</summary>
    private const int MaxFinders = 400, MaxAttempts = 600;

    private sealed class Finder
    {
        public double X, Y, ModuleSize;
        public int Count;
        public bool Used;
    }

    public static void Find(BinaryImage image, List<Detection> results, int maxResults, CancellationToken cancellationToken)
    {
        var finders = FindFinders(image, cancellationToken);
        if (finders.Count < 3) return;

        // Sets of three with similar module sizes that form a right isosceles triangle, the most likely first.
        var triples = new List<(int A, int B, int C, double Score)>();
        finders.Sort((a, b) => a.ModuleSize.CompareTo(b.ModuleSize));
        for (int i = 0; i < finders.Count - 2; i++)
        {
            for (int j = i + 1; j < finders.Count - 1; j++)
            {
                if (finders[j].ModuleSize > finders[i].ModuleSize * 1.4) break;
                for (int k = j + 1; k < finders.Count; k++)
                {
                    if (finders[k].ModuleSize > finders[i].ModuleSize * 1.4) break;
                    if (TripleScore(finders[i], finders[j], finders[k]) is { } score) triples.Add((i, j, k, score));
                }
            }
            if (triples.Count > MaxAttempts * 20) break;
        }
        triples.Sort((a, b) => a.Score.CompareTo(b.Score));

        int attempts = 0;
        foreach (var (a, b, c, _) in triples)
        {
            if (results.Count >= maxResults || attempts >= MaxAttempts || image.BudgetSpent) break;
            if (finders[a].Used || finders[b].Used || finders[c].Used) continue;
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (TryDecode(image, finders[a], finders[b], finders[c]) is { } detection)
            {
                finders[a].Used = finders[b].Used = finders[c].Used = true;
                results.Add(detection);
            }
        }
    }

    /// <summary>How far three finder patterns are from the corners of one code (lower is better), or null when they cannot be.</summary>
    private static double? TripleScore(Finder a, Finder b, Finder c)
    {
        double ab = Dist(a, b), bc = Dist(b, c), ac = Dist(a, c);
        // The longest side is the diagonal; the other two are the sides of the code.
        double diagonal = Math.Max(ab, Math.Max(bc, ac));
        double side1, side2;
        if (diagonal == ab) (side1, side2) = (bc, ac);
        else if (diagonal == bc) (side1, side2) = (ab, ac);
        else (side1, side2) = (ab, bc);
        double moduleSize = (a.ModuleSize + b.ModuleSize + c.ModuleSize) / 3;
        double modules = (side1 + side2) / 2 / moduleSize;
        // A version 1 code has 14 modules between finder centres and version 40 has 170.
        if (modules < 10 || modules > 200) return null;
        double sideRatio = Math.Abs(side1 - side2) / Math.Min(side1, side2);
        double pythagoras = Math.Abs(diagonal - Math.Sqrt(side1 * side1 + side2 * side2)) / Math.Min(side1, side2);
        if (sideRatio > 0.4 || pythagoras > 0.25) return null;
        double sizeSpread = (Math.Max(a.ModuleSize, Math.Max(b.ModuleSize, c.ModuleSize)) - Math.Min(a.ModuleSize, Math.Min(b.ModuleSize, c.ModuleSize))) / moduleSize;
        return sideRatio + pythagoras + sizeSpread;
    }

    private static double Dist(Finder a, Finder b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Detection? TryDecode(BinaryImage image, Finder f1, Finder f2, Finder f3)
    {
        // The corner finder is opposite the longest side; the other two are ordered so the code is not read mirrored.
        double d12 = Dist(f1, f2), d23 = Dist(f2, f3), d13 = Dist(f1, f3);
        Finder topLeft, a, b;
        if (d23 >= d12 && d23 >= d13) (topLeft, a, b) = (f1, f2, f3);
        else if (d13 >= d12 && d13 >= d23) (topLeft, a, b) = (f2, f1, f3);
        else (topLeft, a, b) = (f3, f1, f2);
        var tl = new Point2(topLeft.X, topLeft.Y);
        var pa = new Point2(a.X, a.Y);
        var pb = new Point2(b.X, b.Y);
        // With y growing downward, top-right, top-left and bottom-left turn clockwise.
        Point2 tr, bl;
        if (Point2.Cross(tl, pa, pb) > 0) (tr, bl) = (pa, pb);
        else (tr, bl) = (pb, pa);

        double moduleSize = (topLeft.ModuleSize + a.ModuleSize + b.ModuleSize) / 3;
        int estimate = (int)Math.Round((Point2.Distance(tl, tr) + Point2.Distance(tl, bl)) / 2 / moduleSize) + 7;
        // The size is 17 + 4 * version; try the nearest valid sizes, the closest first.
        Span<int> sizes = stackalloc int[3];
        int nearest = estimate + (estimate % 4) switch { 0 => 1, 2 => -1, 3 => -2, _ => 0 };
        if (estimate % 4 == 3) nearest = estimate + 2;
        sizes[0] = nearest;
        sizes[1] = nearest + (estimate >= nearest ? 4 : -4);
        sizes[2] = nearest - (estimate >= nearest ? 4 : -4);
        foreach (int size in sizes)
        {
            if (size < 21 || size > 177) continue;
            if (TryDecodeSize(image, tl, tr, bl, moduleSize, size) is { } detection) return detection;
        }
        return null;
    }

    private static Detection? TryDecodeSize(BinaryImage image, Point2 tl, Point2 tr, Point2 bl, double moduleSize, int size)
    {
        int version = (size - 17) / 4;
        var br = new Point2(tr.X - tl.X + bl.X, tr.Y - tl.Y + bl.Y);
        Perspective transform;
        Point2? alignment = null;
        if (version >= 2)
        {
            // The bottom-right alignment pattern's centre is 3 modules in from the corner finder centres' grid.
            double toward = 1 - 3.0 / (size - 7);
            var estimate = new Point2(tl.X + toward * (br.X - tl.X), tl.Y + toward * (br.Y - tl.Y));
            foreach (int allowance in (ReadOnlySpan<int>)[4, 8, 16])
            {
                alignment = FindAlignment(image, estimate, moduleSize, allowance);
                if (alignment != null) break;
            }
        }
        if (alignment is { } al)
        {
            transform = Perspective.Between(new(3.5, 3.5), new(size - 3.5, 3.5), new(size - 6.5, size - 6.5), new(3.5, size - 3.5),
                tl, tr, al, bl);
            if (Read(image, transform, size) is { } found) return found;
        }
        transform = Perspective.Between(new(3.5, 3.5), new(size - 3.5, 3.5), new(size - 3.5, size - 3.5), new(3.5, size - 3.5),
            tl, tr, br, bl);
        if (Read(image, transform, size) is { } plain) return plain;
        // Larger versions have alignment patterns to anchor the fourth corner; a version up to 6 has at most one.
        if (alignment != null || version > 6) return null;

        // Without an alignment pattern to anchor it, the fourth corner of a code seen at an angle is not where the
        // parallelogram of the other three puts it. Nearby positions are tried, the closest first.
        double ux = (tr.X - tl.X) / (size - 7), uy = (tr.Y - tl.Y) / (size - 7);
        double vx = (bl.X - tl.X) / (size - 7), vy = (bl.Y - tl.Y) / (size - 7);
        int reach = Math.Clamp(size / 7, 2, 4);
        for (int ring = 1; ring <= reach * 2; ring++)
        {
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != ring) continue;
                    var corner = new Point2(br.X + dx * ux + dy * vx, br.Y + dx * uy + dy * vy);
                    transform = Perspective.Between(new(3.5, 3.5), new(size - 3.5, 3.5), new(size - 3.5, size - 3.5), new(3.5, size - 3.5),
                        tl, tr, corner, bl);
                    if (Read(image, transform, size) is { } moved) return moved;
                }
            }
        }
        return null;
    }

    private static Detection? Read(BinaryImage image, Perspective transform, int size)
    {
        if (transform.Sample(image, size, size) is not { } grid) return null;
        if (QrDecoder.Decode(grid) is not { } content) return null;
        return new Detection(PdfPecker.BarcodeFormat.QrCode, content,
            [transform.Map(0, 0), transform.Map(size, 0), transform.Map(size, size), transform.Map(0, size)]);
    }

    /// <summary>
    /// Scans every other row for runs in the ratio 1:1:3:1:1, confirms each across the column and the row through its
    /// centre, and merges the confirmations of one pattern. Sets of three that are not a code fail to decode, so the
    /// checks stay loose enough for codes seen at an angle.
    /// </summary>
    private static List<Finder> FindFinders(BinaryImage image, CancellationToken cancellationToken)
    {
        var finders = new List<Finder>();
        int width = image.Width, height = image.Height;
        Span<int> counts = stackalloc int[5];
        for (int y = 1; y < height; y += 2)
        {
            if ((y & 63) == 1) cancellationToken.ThrowIfCancellationRequested();
            counts.Clear();
            int state = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                bool dark = image.Bits[row + x] != 0;
                // A pattern starts with a dark run, so light pixels before the first one are skipped.
                if (!dark && state == 0 && counts[0] == 0) continue;
                if (dark)
                {
                    if ((state & 1) == 1) state++; // a light run ended
                    counts[state]++;
                    continue;
                }
                if ((state & 1) == 0)
                {
                    if (state == 4)
                    {
                        if (IsFinderRatio(counts) && finders.Count < MaxFinders * 4)
                            Confirm(image, counts, y, x, finders);
                        // Keep the last three runs as the start of the next possible pattern.
                        counts[0] = counts[2];
                        counts[1] = counts[3];
                        counts[2] = counts[4];
                        counts[3] = 1;
                        counts[4] = 0;
                        state = 3;
                    }
                    else
                    {
                        counts[++state]++;
                    }
                }
                else
                {
                    counts[state]++;
                }
            }
            if (state == 4 && IsFinderRatio(counts) && finders.Count < MaxFinders * 4) Confirm(image, counts, y, width, finders);
        }
        // Patterns confirmed by only one row are usually noise, unless they are all there is.
        finders.RemoveAll(f => f.Count < 2 && f.ModuleSize > 1.5);
        if (finders.Count > MaxFinders)
        {
            finders.Sort((a, b) => b.Count.CompareTo(a.Count));
            finders.RemoveRange(MaxFinders, finders.Count - MaxFinders);
        }
        return finders;
    }

    private static bool IsFinderRatio(ReadOnlySpan<int> counts)
    {
        int total = 0;
        for (int i = 0; i < 5; i++)
        {
            if (counts[i] == 0) return false;
            total += counts[i];
        }
        if (total < 7) return false;
        double module = total / 7.0, variance = module / 2;
        return Math.Abs(module - counts[0]) < variance && Math.Abs(module - counts[1]) < variance
            && Math.Abs(3 * module - counts[2]) < 3 * variance && Math.Abs(module - counts[3]) < variance
            && Math.Abs(module - counts[4]) < variance;
    }

    private static void Confirm(BinaryImage image, ReadOnlySpan<int> counts, int y, int end, List<Finder> finders)
    {
        int total = counts[0] + counts[1] + counts[2] + counts[3] + counts[4];
        double centerX = end - counts[4] - counts[3] - counts[2] / 2.0;
        double? centerY = CrossCheck(image, (int)centerX, y, 0, 1, total);
        if (centerY is not { } cy) return;
        double? cx = CrossCheck(image, (int)centerX, (int)cy, 1, 0, total);
        if (cx is not { } refinedX) return;
        double moduleSize = total / 7.0;
        foreach (var f in finders)
        {
            if (Math.Abs(cy - f.Y) <= moduleSize && Math.Abs(refinedX - f.X) <= moduleSize
                && Math.Abs(moduleSize - f.ModuleSize) <= Math.Max(1, f.ModuleSize))
            {
                // A running average, weighted by how often the pattern was seen.
                f.X = (f.X * f.Count + refinedX) / (f.Count + 1);
                f.Y = (f.Y * f.Count + cy) / (f.Count + 1);
                f.ModuleSize = (f.ModuleSize * f.Count + moduleSize) / (f.Count + 1);
                f.Count++;
                return;
            }
        }
        finders.Add(new Finder { X = refinedX, Y = cy, ModuleSize = moduleSize, Count = 1 });
    }

    /// <summary>
    /// Follows a line through (x, y) in direction (dx, dy) and back, counting the five runs of a finder pattern around
    /// it. Returns the centre along that line when the runs have the right proportions and length, or null.
    /// </summary>
    private static double? CrossCheck(BinaryImage image, int x, int y, int dx, int dy, double expectedTotal)
    {
        if (!image.Get(x, y)) return null;
        Span<int> counts = stackalloc int[5];
        counts.Clear();
        int limit = (int)(expectedTotal * 2) + 4;
        // Backward from the centre: the dark centre, the light ring, the dark ring.
        int step = 0;
        while (image.Get(x - step * dx, y - step * dy) && step <= limit) { counts[2]++; step++; }
        while (InImage(image, x - step * dx, y - step * dy) && !image.Get(x - step * dx, y - step * dy) && step <= limit) { counts[1]++; step++; }
        while (image.Get(x - step * dx, y - step * dy) && step <= limit) { counts[0]++; step++; }
        int back = step;
        step = 1;
        while (image.Get(x + step * dx, y + step * dy) && step <= limit) { counts[2]++; step++; }
        int centreEnd = step;
        while (InImage(image, x + step * dx, y + step * dy) && !image.Get(x + step * dx, y + step * dy) && step <= limit) { counts[3]++; step++; }
        while (image.Get(x + step * dx, y + step * dy) && step <= limit) { counts[4]++; step++; }
        int total = counts[0] + counts[1] + counts[2] + counts[3] + counts[4];
        if (5 * Math.Abs(total - expectedTotal) >= 2 * expectedTotal || !IsFinderRatio(counts)) return null;
        // The centre of the dark middle run, in continuous coordinates where pixel i covers [i, i + 1).
        int darkBack = back - counts[0] - counts[1];
        double middle = 0.5 + (centreEnd - darkBack) / 2.0;
        return dx != 0 ? x + middle : y + middle;
    }

    private static bool InImage(BinaryImage image, int x, int y) => (uint)x < (uint)image.Width && (uint)y < (uint)image.Height;

    /// <summary>
    /// Looks around <paramref name="estimate"/> for an alignment pattern: a dark module in a light ring in a dark ring,
    /// 1:1:1 across its middle in both directions. Returns the confirmed centre nearest to the estimate.
    /// </summary>
    private static Point2? FindAlignment(BinaryImage image, Point2 estimate, double moduleSize, int allowance)
    {
        int reach = (int)(allowance * moduleSize);
        int left = Math.Max(0, (int)estimate.X - reach), right = Math.Min(image.Width - 1, (int)estimate.X + reach);
        int top = Math.Max(0, (int)estimate.Y - reach), bottom = Math.Min(image.Height - 1, (int)estimate.Y + reach);
        if (right - left < moduleSize * 3 || bottom - top < moduleSize * 3) return null;
        Point2? best = null;
        double bestDistance = double.MaxValue;
        Span<int> counts = stackalloc int[3];
        for (int y = top; y <= bottom; y++)
        {
            // Light, dark, light: the inner light ring, the dark centre module and the light ring again.
            counts.Clear();
            int state = 0;
            int x = left;
            while (x <= right && !image[x, y]) x++;
            for (; x <= right; x++)
            {
                if (image[x, y])
                {
                    if (state == 1)
                    {
                        counts[1]++;
                    }
                    else if (state == 2)
                    {
                        if (IsAlignmentRatio(counts, moduleSize))
                        {
                            double cx = x - counts[2] - counts[1] / 2.0;
                            if (AlignmentVertical(image, (int)cx, y, moduleSize) is { } cy
                                && AlignmentHorizontal(image, (int)cx, (int)cy, moduleSize) is { } refinedX)
                            {
                                double distance = Math.Abs(refinedX - estimate.X) + Math.Abs(cy - estimate.Y);
                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    best = new Point2(refinedX, cy);
                                }
                            }
                        }
                        counts[0] = counts[2];
                        counts[1] = 1;
                        counts[2] = 0;
                        state = 1;
                    }
                    else
                    {
                        counts[++state]++;
                    }
                }
                else
                {
                    if (state == 1) state++;
                    counts[state]++;
                }
            }
        }
        return best;
    }

    private static bool IsAlignmentRatio(ReadOnlySpan<int> counts, double moduleSize)
    {
        double variance = moduleSize / 2;
        return Math.Abs(moduleSize - counts[0]) < variance && Math.Abs(moduleSize - counts[1]) < variance
            && Math.Abs(moduleSize - counts[2]) < variance;
    }

    /// <summary>The centre of the light, dark, light runs through a candidate's middle module, along one axis.</summary>
    private static double? AlignmentCheck(BinaryImage image, int x, int y, int dx, int dy, double moduleSize)
    {
        if (!image.Get(x, y)) return null;
        int limit = (int)(moduleSize * 3) + 2;
        int back = 0;
        while (image.Get(x - (back + 1) * dx, y - (back + 1) * dy) && back < limit) back++;
        int ringBack = 0;
        while (InImage(image, x - (back + 1 + ringBack) * dx, y - (back + 1 + ringBack) * dy)
               && !image.Get(x - (back + 1 + ringBack) * dx, y - (back + 1 + ringBack) * dy) && ringBack < limit) ringBack++;
        int forward = 0;
        while (image.Get(x + (forward + 1) * dx, y + (forward + 1) * dy) && forward < limit) forward++;
        int ringForward = 0;
        while (InImage(image, x + (forward + 1 + ringForward) * dx, y + (forward + 1 + ringForward) * dy)
               && !image.Get(x + (forward + 1 + ringForward) * dx, y + (forward + 1 + ringForward) * dy) && ringForward < limit) ringForward++;
        int centre = back + forward + 1;
        double variance = moduleSize / 2;
        if (Math.Abs(centre - moduleSize) >= variance * 1.5 || Math.Abs(ringBack - moduleSize) >= variance * 1.5
            || Math.Abs(ringForward - moduleSize) >= variance * 1.5) return null;
        double middle = 0.5 + (forward - back) / 2.0;
        return (dx != 0 ? x : y) + middle;
    }

    private static double? AlignmentVertical(BinaryImage image, int x, int y, double moduleSize) => AlignmentCheck(image, x, y, 0, 1, moduleSize);

    private static double? AlignmentHorizontal(BinaryImage image, int x, int y, double moduleSize) => AlignmentCheck(image, x, y, 1, 0, moduleSize);
}

/// <summary>A code found in an image: its format, its content and its corners in pixels, in the code's own reading order.</summary>
internal sealed record Detection(PdfPecker.BarcodeFormat Format, DecodedContent Content, Point2[] Corners);

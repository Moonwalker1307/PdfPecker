namespace PdfPecker.Barcodes.Reading;

/// <summary>
/// Reads Code 128 and EAN-13 barcodes along scan lines: rows for upright and upside-down codes, columns for codes turned
/// a quarter. Each line gets a threshold of its own from its histogram, is cut into bar and space widths, and the widths
/// are matched against the symbols' patterns. A code counts once at least two lines agree on it, which keeps a chance
/// match of the checksum on a line through text from being reported.
/// </summary>
internal static class LinearReader
{
    private const float MaxAverageVariance128 = 0.25f, MaxAverageVarianceEan = 0.48f, MaxIndividualVariance = 0.7f;

    private static readonly int[][] Code128 = BuildCode128();
    private static readonly int[][] EanL = BuildEan(reverse: false);
    private static readonly int[][] EanG = BuildEan(reverse: true);

    private static int[][] BuildCode128()
    {
        var patterns = new int[LinearEncoder.Code128Patterns.Length][];
        for (int i = 0; i < patterns.Length; i++)
            patterns[i] = LinearEncoder.Code128Patterns[i].Select(c => c - '0').ToArray();
        return patterns;
    }

    /// <summary>The widths of the left-hand digits, space first; set G is set L's widths reversed, and set R equals L, bar first.</summary>
    private static int[][] BuildEan(bool reverse)
    {
        var result = new int[10][];
        for (int d = 0; d < 10; d++)
        {
            var widths = new List<int>(4);
            int bits = LinearEncoder.EanL[d];
            bool previous = false;
            for (int i = 6; i >= 0; i--)
            {
                bool bar = ((bits >> i) & 1) != 0;
                if (widths.Count == 0 || bar != previous) widths.Add(1);
                else widths[^1]++;
                previous = bar;
            }
            if (reverse) widths.Reverse();
            result[d] = widths.ToArray();
        }
        return result;
    }

    private readonly record struct Hit(PdfPecker.BarcodeFormat Format, string Text, bool Gs1, bool Vertical, bool Reversed, int Line, int Start, int End);

    public static void Find(GrayImage gray, List<Detection> results, PdfPecker.BarcodeFormat formats, int maxResults, CancellationToken cancellationToken)
    {
        var hits = new List<Hit>();
        Scan(gray, vertical: false, formats, hits, cancellationToken);
        Scan(gray, vertical: true, formats, hits, cancellationToken);
        Merge(gray, hits, results, maxResults);
    }

    private static void Scan(GrayImage gray, bool vertical, PdfPecker.BarcodeFormat formats, List<Hit> hits, CancellationToken cancellationToken)
    {
        int lines = vertical ? gray.Width : gray.Height, length = vertical ? gray.Height : gray.Width;
        if (length < 20) return;
        int step = Math.Clamp(lines / 400, 1, 3);
        var luminance = new byte[length];
        var dark = new bool[length];
        var runs = new List<int>(256);
        var reversed = new List<int>(256);
        for (int line = step / 2; line < lines; line += step)
        {
            if ((line & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (vertical)
            {
                for (int i = 0; i < length; i++) luminance[i] = gray.Pixels[i * gray.Width + line];
            }
            else
            {
                Array.Copy(gray.Pixels, line * gray.Width, luminance, 0, length);
            }
            if (!Threshold(luminance, dark)) continue;
            Runs(dark, runs);
            if (runs.Count < 30) continue;
            ScanRuns(runs, formats, vertical, reverse: false, line, length, hits);
            reversed.Clear();
            for (int i = runs.Count - 1; i >= 0; i--) reversed.Add(runs[i]);
            // The runs alternate light and dark from a light one; reversed, an even count would start with a dark run.
            if (reversed.Count % 2 == 0) reversed.Insert(0, 0);
            ScanRuns(reversed, formats, vertical, reverse: true, line, length, hits);
        }
    }

    /// <summary>
    /// Marks the dark pixels of a line: a threshold between the histogram's two main peaks, against a slightly
    /// sharpened signal so that narrow bars blurred into their neighbours still separate. False for a line with too little
    /// contrast to hold a barcode.
    /// </summary>
    private static bool Threshold(ReadOnlySpan<byte> luminance, Span<bool> dark)
    {
        Span<int> buckets = stackalloc int[32];
        buckets.Clear();
        foreach (var l in luminance) buckets[l >> 3]++;
        int firstPeak = 0, firstPeakSize = 0, maxCount = 0;
        for (int i = 0; i < 32; i++)
        {
            if (buckets[i] > firstPeakSize)
            {
                firstPeak = i;
                firstPeakSize = buckets[i];
            }
            maxCount = Math.Max(maxCount, buckets[i]);
        }
        int secondPeak = 0;
        long secondScore = 0;
        for (int i = 0; i < 32; i++)
        {
            long distance = i - firstPeak;
            long score = buckets[i] * distance * distance;
            if (score > secondScore)
            {
                secondPeak = i;
                secondScore = score;
            }
        }
        if (firstPeak > secondPeak) (firstPeak, secondPeak) = (secondPeak, firstPeak);
        if (secondPeak - firstPeak <= 2) return false;
        int valley = secondPeak - 1;
        long valleyScore = -1;
        for (int i = secondPeak - 1; i > firstPeak; i--)
        {
            long fromFirst = i - firstPeak;
            long score = fromFirst * fromFirst * (secondPeak - i) * (maxCount - buckets[i]);
            if (score > valleyScore)
            {
                valley = i;
                valleyScore = score;
            }
        }
        int blackPoint = valley << 3;
        dark[0] = luminance[0] < blackPoint;
        dark[^1] = luminance[^1] < blackPoint;
        for (int i = 1; i < luminance.Length - 1; i++)
            dark[i] = (luminance[i] * 4 - luminance[i - 1] - luminance[i + 1]) / 2 < blackPoint;
        return true;
    }

    /// <summary>The run lengths of a line, starting with a light run (empty when the line starts dark).</summary>
    private static void Runs(ReadOnlySpan<bool> dark, List<int> runs)
    {
        runs.Clear();
        bool current = false;
        int count = 0;
        foreach (var d in dark)
        {
            if (d == current)
            {
                count++;
                continue;
            }
            runs.Add(count);
            current = d;
            count = 1;
        }
        runs.Add(count);
    }

    private static void ScanRuns(List<int> runs, PdfPecker.BarcodeFormat formats, bool vertical, bool reverse, int line, int length, List<Hit> hits)
    {
        var counts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(runs);
        int position = counts[0];
        for (int i = 1; i < counts.Length; i += 2)
        {
            // i is a dark run that starts at pixel `position`.
            if ((formats & PdfPecker.BarcodeFormat.Code128) != 0 && TryCode128(counts, i, out var text, out var gs1, out int endRun))
            {
                AddHit(PdfPecker.BarcodeFormat.Code128, text, gs1, counts, i, endRun, position);
                i = endRun - 1;
                position = Sum(counts, 0, i + 2);
                continue;
            }
            if ((formats & PdfPecker.BarcodeFormat.Ean13) != 0 && TryEan13(counts, i, out text, out endRun))
            {
                AddHit(PdfPecker.BarcodeFormat.Ean13, text, false, counts, i, endRun, position);
                i = endRun - 1;
                position = Sum(counts, 0, i + 2);
                continue;
            }
            position += counts[i] + (i + 1 < counts.Length ? counts[i + 1] : 0);
        }

        void AddHit(PdfPecker.BarcodeFormat format, string text, bool gs1, ReadOnlySpan<int> c, int first, int end, int start)
        {
            int stop = start + Sum(c, first, end);
            // Positions along the line, turned back to the image's direction for a reversed line.
            int a = reverse ? length - stop : start, b = reverse ? length - start : stop;
            hits.Add(new Hit(format, text, gs1, vertical, reverse, line, a, b));
        }
    }

    private static int Sum(ReadOnlySpan<int> counts, int start, int end)
    {
        int sum = 0;
        for (int i = start; i < end && i < counts.Length; i++) sum += counts[i];
        return sum;
    }

    /// <summary>
    /// How far the measured widths are from a pattern, as the average difference per module, or infinity when one width
    /// is off by more than <see cref="MaxIndividualVariance"/> of a module.
    /// </summary>
    private static float Variance(ReadOnlySpan<int> counts, ReadOnlySpan<int> pattern)
    {
        int total = 0, patternLength = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            total += counts[i];
            patternLength += pattern[i];
        }
        if (total < patternLength) return float.PositiveInfinity;
        float unit = (float)total / patternLength;
        float maxIndividual = MaxIndividualVariance * unit, variance = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            float difference = Math.Abs(counts[i] - pattern[i] * unit);
            if (difference > maxIndividual) return float.PositiveInfinity;
            variance += difference;
        }
        return variance / total;
    }

    private static int BestCode128(ReadOnlySpan<int> counts, int first, int last)
    {
        float best = MaxAverageVariance128;
        int match = -1;
        for (int code = first; code <= last; code++)
        {
            float variance = Variance(counts, Code128[code].AsSpan(0, 6));
            if (variance < best)
            {
                best = variance;
                match = code;
            }
        }
        return match;
    }

    private static bool TryCode128(ReadOnlySpan<int> counts, int start, out string text, out bool gs1, out int endRun)
    {
        text = "";
        gs1 = false;
        endRun = 0;
        if (start + 6 > counts.Length) return false;
        int startCode = BestCode128(counts[start..], 103, 105);
        if (startCode < 0) return false;
        int symbolWidth = Sum(counts, start, start + 6);
        // A quiet zone of at least half a symbol before the start.
        if (counts[start - 1] < symbolWidth / 2 && start - 1 != 0) return false;

        var codes = new List<int> { startCode };
        int i = start + 6;
        while (true)
        {
            if (i + 6 > counts.Length) return false;
            int code = BestCode128(counts[i..], 0, 106);
            if (code < 0) return false;
            codes.Add(code);
            i += 6;
            if (code == 106) break;
            if (codes.Count > LinearEncoder.MaxCode128Length + 4) return false;
        }
        // The stop symbol has a seventh element, a bar of two modules, then a quiet zone.
        if (i + 1 > counts.Length) return false;
        int stopWidth = Sum(counts, i - 6, i + 1);
        i++;
        if (i < counts.Length && counts[i] < stopWidth / 2 && i != counts.Length - 1) return false;
        endRun = i;

        // Start, data, checksum, stop: the checksum is the start plus each data value times its position, modulo 103.
        if (codes.Count < 4) return false;
        int checksum = codes[0];
        for (int k = 1; k < codes.Count - 2; k++) checksum += k * codes[k];
        if (checksum % 103 != codes[^2]) return false;
        return Code128Text(codes, out text, out gs1);
    }

    private static bool Code128Text(List<int> codes, out string text, out bool gs1)
    {
        bool isGs1 = false;
        gs1 = false;
        text = "";
        var builder = new CharacterSets.Builder(guessUtf8: false);
        int set = codes[0] - 103; // 0 A, 1 B, 2 C
        bool shift = false, fnc4 = false, fnc4Latch = false, any = false;
        for (int k = 1; k < codes.Count - 2; k++)
        {
            int code = codes[k];
            if (code > 102) return false;
            int active = shift ? 1 - set : set;
            shift = false;
            if (active == 2)
            {
                if (code < 100)
                {
                    builder.Byte((byte)('0' + code / 10));
                    builder.Byte((byte)('0' + code % 10));
                    any = true;
                }
                else if (code == 100) set = 1;
                else if (code == 101) set = 0;
                else Fnc1();
                continue;
            }
            if (code < 96)
            {
                int c = active == 0 ? (code < 64 ? code + 32 : code - 64) : code + 32;
                // FNC4 adds 128 to one character, or to all of them between two pairs of FNC4.
                if (fnc4 != fnc4Latch) c += 128;
                fnc4 = false;
                builder.Byte((byte)c);
                any = true;
                continue;
            }
            switch (code)
            {
                case 96: // FNC3: reader initialisation
                case 97: // FNC2: message append
                    break;
                case 98:
                    shift = true;
                    break;
                case 99:
                    set = 2;
                    break;
                case 100 when active == 0:
                    set = 1;
                    break;
                case 101 when active == 1:
                    set = 0;
                    break;
                case 100:
                case 101:
                    if (fnc4)
                    {
                        fnc4Latch = !fnc4Latch;
                        fnc4 = false;
                    }
                    else fnc4 = true;
                    break;
                case 102:
                    Fnc1();
                    break;
            }
        }
        text = builder.ToString();
        gs1 = isGs1;
        return true;

        void Fnc1()
        {
            if (!any) isGs1 = true;
            else builder.Byte(0x1D);
        }
    }

    private static bool TryEan13(ReadOnlySpan<int> counts, int start, out string text, out int endRun)
    {
        text = "";
        endRun = 0;
        // Guard, six digits of four elements, middle guard, six digits, guard.
        if (start + 59 > counts.Length) return false;
        ReadOnlySpan<int> guard = [1, 1, 1];
        if (Variance(counts[start..], guard) > MaxAverageVarianceEan) return false;
        int guardWidth = Sum(counts, start, start + 3);
        if (counts[start - 1] < guardWidth && start - 1 != 0) return false;

        Span<char> digits = stackalloc char[13];
        int parity = 0;
        int i = start + 3;
        for (int d = 0; d < 6; d++, i += 4)
        {
            int best = -1;
            float bestVariance = MaxAverageVarianceEan;
            bool g = false;
            for (int v = 0; v < 10; v++)
            {
                float l = Variance(counts[i..], EanL[v]);
                if (l < bestVariance) { bestVariance = l; best = v; g = false; }
                float gv = Variance(counts[i..], EanG[v]);
                if (gv < bestVariance) { bestVariance = gv; best = v; g = true; }
            }
            if (best < 0) return false;
            digits[d + 1] = (char)('0' + best);
            if (g) parity |= 1 << (5 - d);
        }
        if (Variance(counts[i..], [1, 1, 1, 1, 1]) > MaxAverageVarianceEan) return false;
        i += 5;
        for (int d = 0; d < 6; d++, i += 4)
        {
            int best = -1;
            float bestVariance = MaxAverageVarianceEan;
            for (int v = 0; v < 10; v++)
            {
                float r = Variance(counts[i..], EanL[v]);
                if (r < bestVariance) { bestVariance = r; best = v; }
            }
            if (best < 0) return false;
            digits[d + 7] = (char)('0' + best);
        }
        if (Variance(counts[i..], guard) > MaxAverageVarianceEan) return false;
        i += 3;
        int endGuard = Sum(counts, i - 3, i);
        if (i < counts.Length && counts[i] < endGuard && i != counts.Length - 1) return false;
        endRun = i;

        int first = Array.IndexOf(LinearEncoder.EanParity, parity);
        if (first < 0) return false;
        digits[0] = (char)('0' + first);
        // The whole code is 95 modules; a match whose halves differ a lot in module width is two codes run together.
        int leftWidth = Sum(counts, start, start + 27), rightWidth = Sum(counts, start + 32, i);
        if (leftWidth > rightWidth * 1.3 || rightWidth > leftWidth * 1.3) return false;
        int sum = 0;
        for (int k = 0; k < 12; k++) sum += (digits[k] - '0') * (k % 2 == 0 ? 1 : 3);
        if ((10 - sum % 10) % 10 != digits[12] - '0') return false;
        text = new string(digits);
        return true;
    }

    /// <summary>
    /// Joins the hits of one code on neighbouring lines and keeps those seen on at least two lines, or on one line of
    /// an image too narrow for two.
    /// </summary>
    private static void Merge(GrayImage gray, List<Hit> hits, List<Detection> results, int maxResults)
    {
        var groups = new List<List<Hit>>();
        foreach (var hit in hits)
        {
            List<Hit>? group = null;
            foreach (var g in groups)
            {
                var last = g[^1];
                if (last.Format == hit.Format && last.Vertical == hit.Vertical && last.Text == hit.Text
                    && hit.Line - last.Line <= 24 && hit.Start < last.End && last.Start < hit.End)
                {
                    group = g;
                    break;
                }
            }
            if (group != null) group.Add(hit);
            else groups.Add([hit]);
        }
        foreach (var g in groups)
        {
            if (results.Count >= maxResults) break;
            int lines = g[0].Vertical ? gray.Width : gray.Height;
            if (g.Count < 2 && lines > 8) continue;
            int start = g.Min(h => h.Start), end = g.Max(h => h.End);
            int first = g.Min(h => h.Line), last = g.Max(h => h.Line) + 1;
            bool reversed = g.Count(h => h.Reversed) * 2 > g.Count;
            Point2[] corners;
            if (!g[0].Vertical)
            {
                corners = reversed
                    ? [new(end, last), new(start, last), new(start, first), new(end, first)]
                    : [new(start, first), new(end, first), new(end, last), new(start, last)];
            }
            else
            {
                corners = reversed
                    ? [new(first, end), new(first, start), new(last, start), new(last, end)]
                    : [new(last, start), new(last, end), new(first, end), new(first, start)];
            }
            // The same code read along rows and along columns (a square-ish crop of one) is reported once.
            if (results.Any(r => r.Format == g[0].Format && r.Content.Text == g[0].Text && Overlaps(r.Corners, corners))) continue;
            var bytes = System.Text.Encoding.Latin1.GetBytes(g[0].Text);
            results.Add(new Detection(g[0].Format, new DecodedContent(g[0].Text, bytes, g[0].Gs1), corners));
        }
    }

    private static bool Overlaps(Point2[] a, Point2[] b)
    {
        double ax0 = a.Min(p => p.X), ax1 = a.Max(p => p.X), ay0 = a.Min(p => p.Y), ay1 = a.Max(p => p.Y);
        double bx0 = b.Min(p => p.X), bx1 = b.Max(p => p.X), by0 = b.Min(p => p.Y), by1 = b.Max(p => p.Y);
        return ax0 < bx1 && bx0 < ax1 && ay0 < by1 && by0 < ay1;
    }
}

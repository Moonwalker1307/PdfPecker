namespace PdfPecker.Barcodes.Reading;

/// <summary>Runs the detectors over one image and joins what they find.</summary>
internal static class BarcodeReader
{
    /// <summary>
    /// Images larger than this are shrunk by a whole factor before reading: a code that a 24-megapixel copy cannot show
    /// is too small to print, and the working copies stay within about 50 MB.
    /// </summary>
    private const long MaxWorkingPixels = 24_000_000;

    public static List<Detection> Read(GrayImage gray, BarcodeFormat formats, bool tryInverted, int maxResults, CancellationToken cancellationToken)
    {
        int factor = 1;
        while ((long)(gray.Width / factor) * (gray.Height / factor) > MaxWorkingPixels) factor++;
        if (factor > 1) gray = gray.Shrink(factor);

        var results = new List<Detection>();
        if (gray.Width < 8 || gray.Height < 8 || maxResults <= 0) return results;
        bool matrix = (formats & (BarcodeFormat.QrCode | BarcodeFormat.DataMatrix)) != 0;
        if (matrix)
        {
            var binary = BinaryImage.From(gray);
            FindMatrix(binary, formats, results, maxResults, cancellationToken);
            if (tryInverted && results.Count < maxResults)
            {
                var found = new List<Detection>();
                FindMatrix(binary.Inverted(), formats, found, maxResults - results.Count, cancellationToken);
                foreach (var d in found) AddNew(results, d);
            }
        }
        if ((formats & (BarcodeFormat.Code128 | BarcodeFormat.Ean13)) != 0 && results.Count < maxResults)
        {
            var found = new List<Detection>();
            LinearReader.Find(gray, found, formats, maxResults - results.Count, cancellationToken);
            foreach (var d in found) AddNew(results, d);
        }

        if (factor > 1)
        {
            for (int i = 0; i < results.Count; i++)
            {
                var d = results[i];
                results[i] = d with { Corners = Array.ConvertAll(d.Corners, p => new Point2(p.X * factor, p.Y * factor)) };
            }
        }
        // Top to bottom, then left to right, by the top left of each code's bounding box.
        results.Sort((a, b) =>
        {
            double ay = a.Corners.Min(p => p.Y), by = b.Corners.Min(p => p.Y);
            if (Math.Abs(ay - by) > 1) return ay.CompareTo(by);
            return a.Corners.Min(p => p.X).CompareTo(b.Corners.Min(p => p.X));
        });
        return results;
    }

    private static void FindMatrix(BinaryImage binary, BarcodeFormat formats, List<Detection> results, int maxResults, CancellationToken cancellationToken)
    {
        if ((formats & BarcodeFormat.QrCode) != 0) QrDetector.Find(binary, results, maxResults, cancellationToken);
        if ((formats & BarcodeFormat.DataMatrix) != 0 && results.Count < maxResults)
            DataMatrixDetector.Find(binary, results, maxResults, cancellationToken);
    }

    /// <summary>Adds a detection unless the same code was already found at the same place.</summary>
    private static void AddNew(List<Detection> results, Detection detection)
    {
        foreach (var r in results)
        {
            if (r.Format == detection.Format && r.Content.Text == detection.Content.Text
                && DataMatrixDetector.Contains(r.Corners, Centre(detection.Corners)))
                return;
        }
        results.Add(detection);
    }

    private static Point2 Centre(Point2[] corners) => new(corners.Average(p => p.X), corners.Average(p => p.Y));
}

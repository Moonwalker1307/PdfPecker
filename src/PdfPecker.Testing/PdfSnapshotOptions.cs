namespace PdfPecker.Testing;

/// <summary>What <see cref="PdfAssert.MatchesSnapshot(byte[], string, PdfSnapshotOptions?, string)"/> compares.</summary>
public enum PdfSnapshotComparison
{
    /// <summary>
    /// The files' objects, after leaving out dates, file IDs, XMP document IDs and the compressed bytes of streams. Any
    /// other change fails, including text, links, bookmarks and metadata that look the same on the page. This is the
    /// default, because PdfPecker writes the same objects for the same input.
    /// </summary>
    Content,

    /// <summary>
    /// The rendered pages, pixel by pixel. Changes that don't show on the page pass, such as a different object order
    /// after a library update; use it for PDFs from other producers, or when only the look matters.
    /// </summary>
    Appearance,
}

/// <summary>Settings for <see cref="PdfAssert.MatchesSnapshot(byte[], string, PdfSnapshotOptions?, string)"/>.</summary>
public sealed class PdfSnapshotOptions
{
    private float _dpi = 72;
    private int _colorTolerance;
    private long _maxDifferentPixels;

    /// <summary>
    /// The folder of the snapshot files. When null, it is a <c>Snapshots</c> folder next to the source file of the test
    /// that calls the assertion.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>What has to match. The default is <see cref="PdfSnapshotComparison.Content"/>.</summary>
    public PdfSnapshotComparison Comparison { get; set; } = PdfSnapshotComparison.Content;

    /// <summary>The resolution of the page images that are compared and written on a failure, 72 by default.</summary>
    public float Dpi
    {
        get => _dpi;
        set => _dpi = value is >= 9 and <= 600 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Dpi must be between 9 and 600.");
    }

    /// <summary>
    /// How far a pixel's colour or alpha channel may differ, from 0 to 255, before the pixel counts as different. 0, the
    /// default, counts any difference.
    /// </summary>
    public int ColorTolerance
    {
        get => _colorTolerance;
        set => _colorTolerance = value is >= 0 and <= 255 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "ColorTolerance must be between 0 and 255.");
    }

    /// <summary>
    /// How many different pixels a page may have and still match in <see cref="PdfSnapshotComparison.Appearance"/>
    /// comparisons. 0, the default, allows none.
    /// </summary>
    public long MaxDifferentPixels
    {
        get => _maxDifferentPixels;
        set => _maxDifferentPixels = value >= 0 ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxDifferentPixels can't be negative.");
    }

    /// <summary>
    /// Writes the PDF as the new snapshot instead of failing when it is missing or differs. Setting the
    /// <c>PDFPECKER_UPDATE_SNAPSHOTS</c> environment variable to <c>1</c> or <c>true</c> does the same for every test.
    /// </summary>
    public bool UpdateSnapshots { get; set; }

    /// <summary>
    /// Writes the expected, actual and diff images of the pages that differ when a comparison fails. On by default.
    /// </summary>
    public bool WriteDiffImages { get; set; } = true;

    /// <summary>The password of encrypted PDFs, used for both the snapshot and the PDF under test.</summary>
    public string? Password { get; set; }
}

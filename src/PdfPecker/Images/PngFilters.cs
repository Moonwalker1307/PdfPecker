namespace PdfPecker.Images;

/// <summary>
/// The row filters of PNG, which PDF's FlateDecode and LZWDecode predictors 10 to 15 use too. The PNG decoder, the
/// predictor of the PDF reader and the encoders share them, so that every reader undoes a row exactly as the writers
/// made it.
/// </summary>
internal static class PngFilters
{
    /// <summary>The filter type byte that starts each row.</summary>
    public const byte None = 0, Sub = 1, Up = 2, Average = 3, Paeth = 4;

    /// <summary>
    /// Undoes one row's filter in place. <paramref name="above"/> is the unfiltered row before it, all zeros for the first
    /// row, and is at least as long as <paramref name="row"/>. A filter type that PNG does not have leaves the row as it
    /// is.
    /// </summary>
    public static void Undo(byte filter, Span<byte> row, ReadOnlySpan<byte> above, int bytesPerPixel)
    {
        // Before the first pixel there is no byte to the left, and none to the upper left, so both count as zero.
        int first = Math.Min(bytesPerPixel, row.Length);
        switch (filter)
        {
            case Sub:
                for (int i = bytesPerPixel; i < row.Length; i++) row[i] += row[i - bytesPerPixel];
                break;
            case Up:
                for (int i = 0; i < row.Length; i++) row[i] += above[i];
                break;
            case Average:
                for (int i = 0; i < first; i++) row[i] += (byte)(above[i] >> 1);
                for (int i = first; i < row.Length; i++) row[i] += (byte)((row[i - bytesPerPixel] + above[i]) >> 1);
                break;
            case Paeth:
                // With left and upper left both zero, the predictor is the byte above.
                for (int i = 0; i < first; i++) row[i] += above[i];
                for (int i = first; i < row.Length; i++)
                    row[i] += (byte)PaethPredictor(row[i - bytesPerPixel], above[i], above[i - bytesPerPixel]);
                break;
        }
    }

    /// <summary>Picks whichever of the left, upper and upper-left bytes is closest to left + upper - upper-left.</summary>
    public static int PaethPredictor(int left, int above, int aboveLeft)
    {
        int estimate = left + above - aboveLeft;
        int toLeft = Math.Abs(estimate - left), toAbove = Math.Abs(estimate - above), toAboveLeft = Math.Abs(estimate - aboveLeft);
        if (toLeft <= toAbove && toLeft <= toAboveLeft) return left;
        return toAbove <= toAboveLeft ? above : aboveLeft;
    }
}

using System.Numerics;

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

    /// <summary>
    /// Applies the Paeth filter to one row: each byte of <paramref name="row"/> minus the Paeth predictor of its left,
    /// upper and upper-left bytes, into <paramref name="filtered"/>. <paramref name="above"/> is the unfiltered row
    /// before it. Unlike undoing the filter, applying it reads only unfiltered bytes, so it runs a vector at a time.
    /// </summary>
    public static void ApplyPaeth(ReadOnlySpan<byte> row, ReadOnlySpan<byte> above, int bytesPerPixel, Span<byte> filtered)
    {
        int n = row.Length, first = Math.Min(bytesPerPixel, n);
        // Before the first pixel there is no byte to the left or upper left, so the predictor is the byte above.
        for (int i = 0; i < first; i++) filtered[i] = (byte)(row[i] - above[i]);
        int at = first;
        if (Vector.IsHardwareAccelerated)
        {
            for (; at + Vector<byte>.Count <= n; at += Vector<byte>.Count)
            {
                var left = new Vector<byte>(row.Slice(at - bytesPerPixel));
                var up = new Vector<byte>(above.Slice(at));
                var upLeft = new Vector<byte>(above.Slice(at - bytesPerPixel));
                Vector.Widen(left, out var leftLow, out var leftHigh);
                Vector.Widen(up, out var upLow, out var upHigh);
                Vector.Widen(upLeft, out var upLeftLow, out var upLeftHigh);
                var predicted = Vector.Narrow(
                    PaethPredictor(Vector.AsVectorInt16(leftLow), Vector.AsVectorInt16(upLow), Vector.AsVectorInt16(upLeftLow)),
                    PaethPredictor(Vector.AsVectorInt16(leftHigh), Vector.AsVectorInt16(upHigh), Vector.AsVectorInt16(upLeftHigh)));
                (new Vector<byte>(row.Slice(at)) - predicted).CopyTo(filtered.Slice(at));
            }
        }
        for (; at < n; at++) filtered[at] = (byte)(row[at] - PaethPredictor(row[at - bytesPerPixel], above[at], above[at - bytesPerPixel]));
    }

    /// <summary><see cref="PaethPredictor(int, int, int)"/> for each lane of bytes widened to 16 bits.</summary>
    private static Vector<ushort> PaethPredictor(Vector<short> left, Vector<short> above, Vector<short> aboveLeft)
    {
        // The distances of the estimate left + above - aboveLeft from left, above and aboveLeft.
        var toLeft = Vector.Abs(above - aboveLeft);
        var toAbove = Vector.Abs(left - aboveLeft);
        var toAboveLeft = Vector.Abs(left + above - aboveLeft - aboveLeft);
        var useLeft = Vector.LessThanOrEqual(toLeft, toAbove) & Vector.LessThanOrEqual(toLeft, toAboveLeft);
        var aboveOrAboveLeft = Vector.ConditionalSelect(Vector.LessThanOrEqual(toAbove, toAboveLeft), above, aboveLeft);
        return Vector.AsVectorUInt16(Vector.ConditionalSelect(useLeft, left, aboveOrAboveLeft));
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

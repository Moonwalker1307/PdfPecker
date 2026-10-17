namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    private const int MinCopyLength = 3;
    private const int MaxCopyLength = 4096;
    private const int MaxCopyDistance = (1 << 20) - NumPlaneCodes;

    // Positions are found by a hash of two pixels; each one links to the previous position with the same hash.
    private const int HashBits = 18;
    private const uint SecondPixelHashMultiplier = 0x9E3779B1;

    // Distance codes 1..120 name nearby pixels, as in the decoder; larger codes are the distance plus 120. This is
    // the inverse of the decoder's map, indexed by row offset * 16 + 8 - column offset.
    private const int NumPlaneCodes = 120;
    private const int PlaneColumns = 16;
    private const int PlaneCenter = 8;
    private static readonly byte[] PlaneToCode = CreatePlaneToCode();

    private static byte[] CreatePlaneToCode()
    {
        byte[] codeToPlane =
        {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
            0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
            0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
            0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
            0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
        };
        var map = new byte[8 * PlaneColumns];
        for (int i = 0; i < codeToPlane.Length; i++) map[codeToPlane[i]] = (byte)(i + 1);
        return map;
    }

    /// <summary>A literal pixel (distance code 0, length 1) or a copy of earlier pixels.</summary>
    private readonly record struct BackwardReference(int Length, int DistanceCode)
    {
        public bool IsLiteral => DistanceCode == 0;
    }

    /// <summary>The LZ77 parse of an image, in a buffer that doubles when full.</summary>
    private sealed class References
    {
        private BackwardReference[] items;

        public References(int capacity) => items = new BackwardReference[Math.Max(capacity, 16)];

        public int Count { get; private set; }

        public BackwardReference this[int index] => items[index];

        public void AddLiteral() => Add(new BackwardReference(1, 0));

        public void AddCopy(int length, int distanceCode) => Add(new BackwardReference(length, distanceCode));

        private void Add(BackwardReference reference)
        {
            if (Count == items.Length) Array.Resize(ref items, Count * 2);
            items[Count++] = reference;
        }
    }

    /// <summary>
    /// A greedy LZ77 parse: at each pixel the longest match among the pixel to the left, the one above and the
    /// positions found through the hash chain, or a literal when none is at least three pixels long.
    /// </summary>
    private static References BackwardReferences(uint[] pixels, int width, Settings settings)
    {
        int n = pixels.Length;
        var references = new References(n < 4096 ? n : n / 4);
        if (n < 2 * MinCopyLength)
        {
            for (int i = 0; i < n; i++) references.AddLiteral();
            return references;
        }
        var head = new int[1 << HashBits]; // the latest position of each hash, or -1
        Array.Fill(head, -1);
        var chain = new int[n]; // the position before each one with the same hash
        int position = 0;
        while (position < n)
        {
            var (length, distance) = LongestMatch(pixels, position, width, settings, head, chain);
            int advance;
            if (length >= MinCopyLength)
            {
                references.AddCopy(length, PlaneCode(width, distance));
                advance = length;
            }
            else
            {
                references.AddLiteral();
                advance = 1;
            }
            for (int k = 0; k < advance; k++, position++)
            {
                if (position + 1 >= n) continue;
                int hash = Hash(pixels[position], pixels[position + 1]);
                chain[position] = head[hash];
                head[hash] = position;
            }
        }
        return references;
    }

    /// <summary>The longest match for the pixels at <paramref name="position"/> and its distance, or a length of 0.</summary>
    private static (int Length, int Distance) LongestMatch(uint[] pixels, int position, int width, Settings settings, int[] head, int[] chain)
    {
        int n = pixels.Length;
        int bestLength = 0, bestDistance = 0;
        int maxLength = Math.Min(MaxCopyLength, n - position);
        if (maxLength < MinCopyLength) return (0, 0);
        // Runs (the pixel to the left) and the row above first: they are the common long matches.
        if (position >= 1)
        {
            int length = MatchLength(pixels, position - 1, position, maxLength);
            if (length > bestLength) (bestLength, bestDistance) = (length, 1);
        }
        if (position >= width && width > 1 && bestLength < maxLength)
        {
            int length = MatchLength(pixels, position - width, position, maxLength);
            if (length > bestLength) (bestLength, bestDistance) = (length, width);
        }
        if (bestLength < settings.NiceLength && bestLength < maxLength && position + 1 < n)
        {
            int candidate = head[Hash(pixels[position], pixels[position + 1])];
            for (int steps = settings.ChainLength; candidate >= 0 && steps > 0; steps--, candidate = chain[candidate])
            {
                int distance = position - candidate;
                if (distance > MaxCopyDistance) break;
                // A candidate that differs at the best length so far cannot beat it.
                if (bestLength > 0 && pixels[candidate + bestLength] != pixels[position + bestLength]) continue;
                int length = MatchLength(pixels, candidate, position, maxLength);
                if (length > bestLength)
                {
                    (bestLength, bestDistance) = (length, distance);
                    if (length >= settings.NiceLength || length == maxLength) break;
                }
            }
        }
        return (bestLength, bestDistance);
    }

    private static int Hash(uint a, uint b) => (int)(((a * ColorHashMultiplier) ^ (b * SecondPixelHashMultiplier)) >> (32 - HashBits));

    private static int MatchLength(uint[] pixels, int from, int to, int max)
        => pixels.AsSpan(from, max).CommonPrefixLength(pixels.AsSpan(to, max));

    /// <summary>The distance code for a copy: one of the 120 short codes for nearby pixels, else distance + 120.</summary>
    private static int PlaneCode(int width, int distance)
    {
        int yOffset = distance / width, xOffset = distance - yOffset * width;
        // A short code names a pixel up to 8 columns to the left, in this row or up to 7 rows above, or up to 7 columns
        // to the right in the 7 rows above.
        if (xOffset <= PlaneCenter && yOffset < 8) return PlaneToCode[yOffset * PlaneColumns + PlaneCenter - xOffset];
        if (xOffset > width - PlaneCenter && yOffset < 7) return PlaneToCode[(yOffset + 1) * PlaneColumns + PlaneCenter + (width - xOffset)];
        return distance + NumPlaneCodes;
    }
}

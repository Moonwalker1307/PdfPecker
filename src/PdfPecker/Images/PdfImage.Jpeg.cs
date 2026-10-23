namespace PdfPecker.Images;

internal sealed partial class PdfImage
{
    // JPEG data is embedded as it is (DCTDecode); only the frame header is read, for the size and colour space.
    private const byte JpegMarkerPrefix = 0xFF;
    private const byte JpegStartOfImage = 0xD8;
    private const byte JpegStartOfScan = 0xDA;
    private const byte JpegApp14 = 0xEE;
    private const int AdobeSegmentMinLength = 12;

    // An APP14 segment can come after the frame header; the bytes searched for it there are bounded.
    private const int AdobeSearchLimit = 65536;

    // Segments that only describe the photo: APP1 holds EXIF and XMP, APP13 Photoshop's IPTC data, and COM a comment.
    // None of them changes how the image looks; APP0 (JFIF), APP2 (ICC profile) and APP14 (Adobe colour transform) do
    // or may, and are kept.
    private const byte JpegApp1 = 0xE1, JpegApp13 = 0xED, JpegComment = 0xFE;

    /// <summary>Whether the image is stored as JPEG data (DCTDecode).</summary>
    public bool IsDct => Filter == DctFilter;

    /// <summary>
    /// The JPEG data without its metadata segments (EXIF, XMP, IPTC and comments), or the data itself when it has none
    /// or its segments before the image data cannot be followed.
    /// </summary>
    public static byte[] WithoutMetadata(byte[] d)
    {
        if (!IsJpeg(d)) return d;
        var removed = new List<(int Start, int End)>();
        int i = 2;
        while (true)
        {
            if (i + 4 > d.Length || d[i] != JpegMarkerPrefix) return d;
            byte marker = d[i + 1];
            if (marker == JpegMarkerPrefix) { i++; continue; }
            if (IsJpegStandalone(marker)) { i += 2; continue; }
            if (marker == JpegStartOfScan) break;
            int length = d[i + 2] << 8 | d[i + 3];
            if (length < 2 || i + 2 + length > d.Length) return d;
            if (marker is JpegApp1 or JpegApp13 or JpegComment) removed.Add((i, i + 2 + length));
            i += 2 + length;
        }
        if (removed.Count == 0) return d;
        var result = new byte[d.Length - removed.Sum(r => r.End - r.Start)];
        int from = 0, to = 0;
        foreach (var (start, end) in removed)
        {
            d.AsSpan(from, start - from).CopyTo(result.AsSpan(to));
            to += start - from;
            from = end;
        }
        d.AsSpan(from).CopyTo(result.AsSpan(to));
        return result;
    }

    private static bool IsJpeg(byte[] data) => data.Length > 3 && data[0] == JpegMarkerPrefix && data[1] == JpegStartOfImage;

    /// <summary>
    /// SOF0 to SOF15, all the frame types. 0xC4 (DHT), 0xC8 (reserved) and 0xCC (DAC) share the range but are not frames.
    /// </summary>
    private static bool IsJpegFrame(byte marker) => marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    /// <summary>Markers without a length: start of image, the restart markers and TEM.</summary>
    private static bool IsJpegStandalone(byte marker) => marker is JpegStartOfImage or (>= 0xD0 and <= 0xD7) or 0x01;

    private static PdfImage? FromJpeg(byte[] d)
    {
        int i = 2;
        bool adobe = false;
        while (i + 4 <= d.Length)
        {
            if (d[i] != JpegMarkerPrefix) { i++; continue; }
            byte marker = d[i + 1];
            if (marker == JpegMarkerPrefix) { i++; continue; }
            if (IsJpegStandalone(marker)) { i += 2; continue; }
            int length = d[i + 2] << 8 | d[i + 3];
            if (marker == JpegApp14 && length >= AdobeSegmentMinLength && IsAdobeSegment(d, i + 4)) adobe = true;
            if (IsJpegFrame(marker)) return FromJpegFrame(d, i + 4, adobe);
            if (marker == JpegStartOfScan) return null; // start of scan before frame header: corrupt
            i += 2 + length;
        }
        return null;
    }

    /// <summary>Compares byte by byte, so that a segment cut off inside the text reads past the end of the data.</summary>
    private static bool IsAdobeSegment(byte[] d, int at)
        => d[at] == 'A' && d[at + 1] == 'd' && d[at + 2] == 'o' && d[at + 3] == 'b' && d[at + 4] == 'e';

    /// <summary>The image described by the frame header whose fields start at <paramref name="p"/>.</summary>
    private static PdfImage? FromJpegFrame(byte[] d, int p, bool adobe)
    {
        int bpc = d[p];
        int height = d[p + 1] << 8 | d[p + 2];
        int width = d[p + 3] << 8 | d[p + 4];
        int components = d[p + 5];
        if (width == 0 || height == 0) return null;
        var img = new PdfImage
        {
            Width = width,
            Height = height,
            BitsPerComponent = bpc == 12 ? 8 : bpc,
            Filter = DctFilter,
            Data = d,
            ColorSpace = components switch { 1 => DeviceGray, 4 => DeviceCmyk, _ => DeviceRgb },
        };
        if (components == 4 && (adobe || HasLaterAdobeSegment(d, p - 4))) img.Decode = InvertedCmykDecode;
        return img;
    }

    /// <summary>Looks for an APP14 Adobe segment from <paramref name="start"/> on, as one may follow the frame header.</summary>
    private static bool HasLaterAdobeSegment(byte[] d, int start)
    {
        for (int k = start; k + 9 < d.Length && k < AdobeSearchLimit; k++)
            if (d[k] == JpegMarkerPrefix && d[k + 1] == JpegApp14 && d[k + 4] == 'A' && d[k + 5] == 'd') return true;
        return false;
    }
}

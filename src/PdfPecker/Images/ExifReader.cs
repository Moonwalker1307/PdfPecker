using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PdfPecker.Images;

/// <summary>Reads the EXIF block of a JPEG file: the APP1 segment that holds a small TIFF file of tags.</summary>
internal static class ExifReader
{
    private const byte MarkerPrefix = 0xFF, StartOfImage = 0xD8, StartOfScan = 0xDA, EndOfImage = 0xD9, App1 = 0xE1;
    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;

    // Tags of the main directory (IFD0), the EXIF sub-directory and the GPS sub-directory.
    private const int Make = 0x010F, Model = 0x0110, Orientation = 0x0112, Software = 0x0131, DateTime = 0x0132;
    private const int ExifDirectory = 0x8769, GpsDirectory = 0x8825;
    private const int DateTimeOriginal = 0x9003;
    private const int GpsLatitudeRef = 1, GpsLatitude = 2, GpsLongitudeRef = 3, GpsLongitude = 4, GpsAltitudeRef = 5, GpsAltitude = 6;

    /// <summary>The EXIF data of a JPEG file, or null when it has none or it is not readable.</summary>
    public static PdfImageExif? Read(byte[] jpeg)
    {
        if (Find(jpeg) is not { } tiff || TiffDirectory.Read(tiff, 0) is not { } main) return null;
        var exif = main.Single(ExifDirectory, 0) is > 0 and var exifAt ? TiffDirectory.ReadAt(tiff, exifAt) : null;
        var gps = main.Single(GpsDirectory, 0) is > 0 and var gpsAt ? TiffDirectory.ReadAt(tiff, gpsAt) : null;
        double? altitude = gps != null && gps.Rationals(GpsAltitude) is [var a] && double.IsFinite(a) ? a : null;
        if (altitude != null && gps!.Single(GpsAltitudeRef, 0) == 1) altitude = -altitude;
        return new PdfImageExif(
            tiff,
            Text(main, Make),
            Text(main, Model),
            Text(main, Software),
            Date(exif, DateTimeOriginal),
            Date(main, DateTime),
            main.Single(Orientation, 0) is >= 1 and <= 8 and var o ? (int)o : null,
            Coordinate(gps, GpsLatitude, GpsLatitudeRef, 'S', 90),
            Coordinate(gps, GpsLongitude, GpsLongitudeRef, 'W', 180),
            altitude);
    }

    /// <summary>
    /// The EXIF orientation of a JPEG file, 1 to 8 (1 is upright as stored), or 1 when it has none or it is not
    /// readable. Only the main directory is read.
    /// </summary>
    public static int ReadOrientation(byte[] jpeg)
        => Find(jpeg) is { } tiff && TiffDirectory.Read(tiff, 0) is { } main && main.Single(Orientation, 1) is >= 1 and <= 8 and var o
            ? (int)o : 1;

    /// <summary>The TIFF data of the first EXIF APP1 segment, found among the segments before the image data.</summary>
    private static byte[]? Find(byte[] d)
        => FindSegment(d) is var (at, length) ? d.AsSpan(at + 4 + ExifHeader.Length, length - 2 - ExifHeader.Length).ToArray() : null;

    /// <summary>
    /// The first EXIF APP1 segment of a JPEG file, marker included, with its orientation set to 1, for the same photo
    /// once it has been turned upright; null when it has none or the orientation is not where the reader found it.
    /// </summary>
    public static byte[]? UprightSegment(byte[] jpeg)
    {
        if (FindSegment(jpeg) is not var (at, length)) return null;
        var segment = jpeg.AsSpan(at, 2 + length).ToArray();
        var tiff = segment.AsSpan(4 + ExifHeader.Length);
        bool little = tiff[0] == (byte)'I';
        uint U32(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        int U16(ReadOnlySpan<byte> b) => little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
        uint ifd = U32(tiff[4..]);
        if (ifd > (uint)tiff.Length - 2) return null;
        int count = U16(tiff[(int)ifd..]);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry > tiff.Length - 12) return null;
            // A SHORT whose value sits in the entry itself, in its first two bytes.
            if (U16(tiff[entry..]) != Orientation) continue;
            if (U16(tiff[(entry + 2)..]) != 3 || U32(tiff[(entry + 4)..]) != 1) return null;
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(tiff[(entry + 8)..], 1);
            else BinaryPrimitives.WriteUInt16BigEndian(tiff[(entry + 8)..], 1);
            return segment;
        }
        return null;
    }

    /// <summary>
    /// The start and length (after the marker) of the first EXIF APP1 segment with a TIFF header, found among the
    /// segments before the image data.
    /// </summary>
    private static (int At, int Length)? FindSegment(byte[] d)
    {
        int i = 2;
        while (i + 4 <= d.Length && d[i] == MarkerPrefix)
        {
            byte marker = d[i + 1];
            if (marker == MarkerPrefix) { i++; continue; }
            if (marker is StartOfScan or EndOfImage or StartOfImage) return null;
            int length = d[i + 2] << 8 | d[i + 3];
            if (length < 2 || i + 2 + length > d.Length) return null;
            var payload = d.AsSpan(i + 4, length - 2);
            if (marker == App1 && payload.StartsWith(ExifHeader))
            {
                var tiff = payload[ExifHeader.Length..];
                bool header = tiff.Length >= 8 && (tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8));
                return header ? (i, length) : null;
            }
            i += 2 + length;
        }
        return null;
    }

    /// <summary>An ASCII tag without its terminating zeros and padding spaces, or null when missing or empty.</summary>
    private static string? Text(TiffDirectory? directory, int tag)
    {
        if (directory?.Bytes(tag) is not { } bytes) return null;
        int end = bytes.AsSpan().IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>An EXIF date, "YYYY:MM:DD HH:MM:SS" in the camera's local time, or null when missing or not a date.</summary>
    private static DateTime? Date(TiffDirectory? directory, int tag)
        => Text(directory, tag) is { } text
           && System.DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    /// <summary>A GPS latitude or longitude in degrees, from degrees, minutes and seconds and the hemisphere letter.</summary>
    private static double? Coordinate(TiffDirectory? gps, int tag, int refTag, char negative, double limit)
    {
        if (gps == null || gps.Rationals(tag) is not [var degrees, var minutes, var seconds]) return null;
        double value = degrees + minutes / 60 + seconds / 3600;
        if (!double.IsFinite(value) || value > limit) return null;
        return Text(gps, refTag) is [var hemisphere] && hemisphere == negative ? -value : value;
    }
}

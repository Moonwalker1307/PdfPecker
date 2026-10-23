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

    /// <summary>The TIFF data of the first EXIF APP1 segment, found among the segments before the image data.</summary>
    private static byte[]? Find(byte[] d)
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
                return header ? tiff.ToArray() : null;
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

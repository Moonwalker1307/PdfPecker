using System.Globalization;

namespace PdfPecker.Pdf;

/// <summary>
/// The fixed date that makes a written document reproducible: the caller's own, or the one the build environment
/// sets in <c>SOURCE_DATE_EPOCH</c> (https://reproducible-builds.org/specs/source-date-epoch/).
/// </summary>
internal static class ReproducibleDate
{
    /// <summary>The environment variable reproducible build tools set to the time of the last source change.</summary>
    public const string EnvironmentVariable = "SOURCE_DATE_EPOCH";

    /// <summary>
    /// The fixed date, in UTC and truncated to whole seconds as PDF dates hold them, or null when neither
    /// <paramref name="explicitDate"/> nor a valid <c>SOURCE_DATE_EPOCH</c> gives one and the current time is used.
    /// </summary>
    public static DateTime? Resolve(DateTimeOffset? explicitDate)
    {
        if (explicitDate is { } d) return TruncateToSeconds(d.UtcDateTime);
        string? epoch;
        try { epoch = Environment.GetEnvironmentVariable(EnvironmentVariable); }
        catch (System.Security.SecurityException) { return null; }
        return ParseEpoch(epoch);
    }

    /// <summary>
    /// Reads a <c>SOURCE_DATE_EPOCH</c> value: decimal seconds since 1970-01-01 UTC. The specification lets a tool reject
    /// a malformed value, but a stray variable must not break a conversion, so such a value means the current time.
    /// </summary>
    public static DateTime? ParseEpoch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) return null;
        // The last second a four-digit PDF year can hold.
        if (seconds > 253_402_300_799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
    }

    /// <summary>A time with its fractions of a second dropped, as PDF and XMP dates are written to the second.</summary>
    public static DateTime TruncateToSeconds(DateTime t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>
    /// A file identifier derived from the document's bytes, so the same input and date give the same file. It is the
    /// first 16 bytes of the SHA-256 of everything written before the trailer.
    /// </summary>
    public static byte[] FileId(ReadOnlySpan<byte> body) => System.Security.Cryptography.SHA256.HashData(body)[..16];
}

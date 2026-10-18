using System.Globalization;

namespace PdfPecker;

/// <summary>Number parsing that does not depend on the culture of the machine the conversion runs on.</summary>
internal static class Invariant
{
    /// <summary>
    /// Parses an integer from CSS or an HTML attribute. The parameterless <c>int.TryParse</c> uses the current culture,
    /// and in cultures whose sign symbols carry a direction mark (Arabic, Persian, Pashto) it rejects a plain "-1" or "+1".
    /// </summary>
    public static bool TryParseInt(ReadOnlySpan<char> s, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Testing;

/// <summary>One indirect object of a file after normalising: its dictionary or value, and its stream's data.</summary>
internal readonly record struct NormalizedObject(string Number, string Dictionary, string Stream);

/// <summary>
/// Reads a PDF into its indirect objects in file order, without what changes from one run to the next while the
/// document stays the same: creation and modification dates, the file ID, XMP document IDs, the compressed bytes of
/// Flate streams (which depend on the runtime's zlib build) and the byte offsets in cross-reference streams.
/// </summary>
internal static partial class PdfNormalizer
{
    [GeneratedRegex(@"(?<![0-9])(\d+)[ \t\r\n\f\0]+\d+[ \t\r\n\f\0]+obj\b")]
    private static partial Regex ObjectStart();

    [GeneratedRegex(@"/Length[ \t\r\n\f\0]+(\d+)(?![ \t\r\n\f\0]+\d+[ \t\r\n\f\0]+R)")]
    private static partial Regex DirectLength();

    [GeneratedRegex(@"/Length[ \t\r\n\f\0]+\d+(?:[ \t\r\n\f\0]+\d+[ \t\r\n\f\0]+R)?")]
    private static partial Regex AnyLength();

    [GeneratedRegex(@"/ID[ \t\r\n\f\0]*\[[ \t\r\n\f\0]*<[0-9A-Fa-f]*>[ \t\r\n\f\0]*<[0-9A-Fa-f]*>[ \t\r\n\f\0]*\]")]
    private static partial Regex FileIds();

    [GeneratedRegex(@"/Type[ \t\r\n\f\0]*/XRef\b")]
    private static partial Regex XRefType();

    // Dates as literal strings, as hex strings after /CreationDate and /ModDate, and in XMP metadata, with the XMP
    // document and instance IDs, which some producers make random.
    [GeneratedRegex(@"\(D:[^)]*\)|(?<=/(?:CreationDate|ModDate|M)[ \t\r\n\f\0]*)<[0-9A-Fa-f]*>|<xmp:(?:CreateDate|ModifyDate|MetadataDate)>[^<]*|<xmpMM:(?:DocumentID|InstanceID)>[^<]*|xmp:(?:CreateDate|ModifyDate|MetadataDate)=""[^""]*""|xmpMM:(?:DocumentID|InstanceID)=""[^""]*""")]
    private static partial Regex Volatile();

    /// <summary>The largest inflated stream kept whole; longer data is compared by its first part and its length.</summary>
    private const int MaxInflatedBytes = 64 * 1024 * 1024;

    /// <summary>The file's indirect objects in file order, normalised.</summary>
    public static List<NormalizedObject> Normalize(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var objects = new List<NormalizedObject>();
        var pattern = ObjectStart();
        int position = 0;
        while (position < text.Length)
        {
            var m = pattern.Match(text, position);
            if (!m.Success) break;
            int start = m.Index + m.Length;
            int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
            int streamAt = text.IndexOf("stream", start, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            if (streamAt < 0 || streamAt > end)
            {
                objects.Add(new NormalizedObject(m.Groups[1].Value, Volatile().Replace(text[start..end], ""), ""));
                position = end;
                continue;
            }

            var dictionary = text[start..streamAt];
            int dataAt = streamAt + "stream".Length;
            if (dataAt < text.Length && text[dataAt] == '\r') dataAt++;
            if (dataAt < text.Length && text[dataAt] == '\n') dataAt++;
            int dataEnd = FindStreamEnd(text, dictionary, dataAt);
            var data = pdf.AsSpan(dataAt, dataEnd - dataAt).ToArray();
            if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal) && Inflate(data) is { } inflated)
            {
                data = inflated;
                // The compressed length depends on the zlib build, so the inflated data is compared instead.
                dictionary = AnyLength().Replace(dictionary, "");
            }

            bool xref = XRefType().IsMatch(dictionary);
            end = text.IndexOf("endobj", dataEnd, StringComparison.Ordinal);
            if (end < 0) end = text.Length;
            objects.Add(xref
                // A cross-reference stream holds byte offsets, which move with every compressed size, and the file ID.
                ? new NormalizedObject(m.Groups[1].Value, "xref", "")
                : new NormalizedObject(m.Groups[1].Value, FileIds().Replace(Volatile().Replace(dictionary, ""), ""),
                    Volatile().Replace(Encoding.Latin1.GetString(data), "")));
            position = end;
        }
        return objects;
    }

    /// <summary>Where the stream's data ends: at its direct /Length when endstream follows it, else before endstream.</summary>
    private static int FindStreamEnd(string text, string dictionary, int dataAt)
    {
        var length = DirectLength().Match(dictionary);
        if (length.Success && int.TryParse(length.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int size) && size <= text.Length - dataAt)
        {
            int after = dataAt + size;
            while (after < text.Length && text[after] is '\r' or '\n' or ' ') after++;
            if (string.CompareOrdinal(text, after, "endstream", 0, "endstream".Length) == 0) return dataAt + size;
        }
        int endstream = text.IndexOf("endstream", dataAt, StringComparison.Ordinal);
        if (endstream < 0) return text.Length;
        if (endstream > dataAt && text[endstream - 1] == '\n') endstream--;
        if (endstream > dataAt && text[endstream - 1] == '\r') endstream--;
        return endstream;
    }

    /// <summary>The inflated data, or null when the data is not a zlib stream (damaged or encrypted).</summary>
    private static byte[]? Inflate(byte[] data)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = z.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxInflatedBytes)
                {
                    // A stream this large is compared by its first part and the fact that it goes on; a test PDF has
                    // no reason to hold one, and a damaged snapshot must not take all the memory.
                    output.Write(buffer, 0, (int)(MaxInflatedBytes - output.Length));
                    output.Write("...(more)"u8);
                    break;
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>A description of the first difference, or null when the files have the same content.</summary>
    public static string? FirstDifference(List<NormalizedObject> expected, List<NormalizedObject> actual)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            var (e, a) = (expected[i], actual[i]);
            if (e.Number != a.Number)
                return $"the snapshot's object {Ordinal(i)} is number {e.Number}, the PDF's is number {a.Number}";
            if (e.Dictionary != a.Dictionary)
                return $"object {e.Number} differs: {Excerpt(e.Dictionary, a.Dictionary)}";
            if (e.Stream != a.Stream)
                return $"the stream of object {e.Number} differs ({Invariant(e.Stream.Length)} bytes in the snapshot, {Invariant(a.Stream.Length)} in the PDF): {Excerpt(e.Stream, a.Stream)}";
        }
        return expected.Count == actual.Count ? null
            : $"the snapshot has {Invariant(expected.Count)} objects and the PDF has {Invariant(actual.Count)}";
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Ordinal(int index) => "#" + Invariant(index + 1);

    /// <summary>The first differing line of each side, cut to a readable length.</summary>
    private static string Excerpt(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        int line = 0;
        while (line < e.Length && line < a.Length && e[line] == a[line]) line++;
        static string Clip(string[] lines, int i) =>
            i >= lines.Length ? "(end)" : lines[i].Length > 160 ? lines[i][..160] + "…" : lines[i];
        return $"line {Invariant(line + 1)} is \"{Clip(e, line).Trim()}\" in the snapshot and \"{Clip(a, line).Trim()}\" in the PDF";
    }
}

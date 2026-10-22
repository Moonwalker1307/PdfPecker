using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfPecker.Fluent.Tests;

/// <summary>
/// Compares PDFs object by object with their streams inflated. The compressed bytes depend on the runtime's zlib build
/// (.NET 9 compresses differently from .NET 8 and 10), so the baselines compare what the streams contain instead.
/// </summary>
internal static partial class PdfCompare
{
    [GeneratedRegex(@"(\d+) 0 obj\n")]
    private static partial Regex ObjectPattern();

    [GeneratedRegex(@"/Length (\d+)")]
    private static partial Regex LengthPattern();

    private sealed record PdfObject(string Number, string Dictionary, string Stream);

    /// <summary>A description of the first difference, or null when the files have the same content.</summary>
    public static string? FirstDifference(byte[] expected, byte[] actual)
    {
        var (e, a) = (Normalize(expected), Normalize(actual));
        for (int i = 0; i < Math.Min(e.Count, a.Count); i++)
        {
            if (e[i].Dictionary != a[i].Dictionary) return $"object {e[i].Number}: {e[i].Dictionary} vs {a[i].Dictionary}";
            if (e[i].Stream != a[i].Stream) return $"object {e[i].Number}: the stream differs ({e[i].Stream.Length} vs {a[i].Stream.Length} bytes)";
        }
        return e.Count == a.Count ? null : $"expected {e.Count} objects, got {a.Count}";
    }

    private static List<PdfObject> Normalize(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var objects = new List<PdfObject>();
        foreach (Match m in ObjectPattern().Matches(text))
        {
            int start = m.Index + m.Length;
            int end = text.IndexOf("endobj", start, StringComparison.Ordinal);
            if (end < 0) break;
            int streamAt = text.IndexOf("stream\n", start, StringComparison.Ordinal);
            if (streamAt < 0 || streamAt > end)
            {
                objects.Add(new PdfObject(m.Groups[1].Value, text[start..end], ""));
                continue;
            }
            var dictionary = text[start..streamAt];
            var length = LengthPattern().Match(dictionary);
            int size = length.Success ? int.Parse(length.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            int dataAt = streamAt + "stream\n".Length;
            var data = pdf.AsSpan(dataAt, Math.Min(size, pdf.Length - dataAt)).ToArray();
            if (dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                z.CopyTo(inflated);
                data = inflated.ToArray();
                dictionary = dictionary.Remove(length.Index, length.Length);
            }
            // A cross-reference stream holds byte offsets, which move with the compressed sizes.
            string stream = dictionary.Contains("/XRef", StringComparison.Ordinal) ? "" : Encoding.Latin1.GetString(data);
            objects.Add(new PdfObject(m.Groups[1].Value, dictionary.Contains("/XRef", StringComparison.Ordinal) ? "xref" : dictionary, stream));
        }
        return objects;
    }
}

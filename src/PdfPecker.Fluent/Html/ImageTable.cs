namespace PdfPecker.Fluent.Html;

/// <summary>
/// The images of a document. For a PDF they are served to PdfPecker through its resource resolver as
/// <c>fluent:img/N</c>, which skips the base64 round trip; for exported HTML they become data URIs, so the page
/// opens in a browser on its own. The same byte array used twice is stored once.
/// </summary>
internal sealed class ImageTable
{
    public const string Scheme = "fluent:img/";

    private readonly bool _dataUris;
    private readonly Dictionary<byte[], int> _index = new(ReferenceEqualityComparer.Instance);
    private readonly List<byte[]> _images = [];

    public ImageTable(bool dataUris) => _dataUris = dataUris;

    public string Source(byte[] data)
    {
        if (!_index.TryGetValue(data, out int index))
        {
            index = _images.Count;
            _images.Add(data);
            _index.Add(data, index);
        }
        return _dataUris
            ? "data:" + MediaType(data) + ";base64," + Convert.ToBase64String(data)
            : Scheme + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The bytes behind a <c>fluent:img/N</c> address, or null for any other address.</summary>
    public byte[]? Resolve(string source)
    {
        if (!source.StartsWith(Scheme, StringComparison.Ordinal)) return null;
        return int.TryParse(source.AsSpan(Scheme.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            && index < _images.Count ? _images[index] : null;
    }

    private static string MediaType(byte[] data)
    {
        ReadOnlySpan<byte> d = data;
        if (d.Length >= 4 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "image/png";
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.StartsWith("GIF8"u8)) return "image/gif";
        if (d.Length >= 12 && d.StartsWith("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (d.StartsWith("BM"u8)) return "image/bmp";
        if (d.StartsWith("II*\0"u8) || d.StartsWith("MM\0*"u8)) return "image/tiff";
        if (d.IndexOf("<svg"u8) >= 0) return "image/svg+xml";
        return "application/octet-stream";
    }
}

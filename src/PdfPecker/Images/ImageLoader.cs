namespace PdfPecker.Images;

/// <summary>Resolves image sources (data URIs, files, or a user callback) and caches decoded images per document.</summary>
internal sealed class ImageLoader
{
    private readonly string? _baseDirectory;
    private readonly Func<string, byte[]?>? _resolver;
    private readonly bool _allowFiles;
    private readonly Dictionary<string, PdfImage?> _cache = new(StringComparer.Ordinal);

    /// <summary>Parsed inline SVG drawings, shared by every layout pass of a document (pages, headers, footers).</summary>
    public readonly Dictionary<PdfPecker.Html.Element, PdfPecker.Svg.SvgImage> Svgs = new();

    public ImageLoader(string? baseDirectory, Func<string, byte[]?>? resolver, bool allowFiles = true)
    {
        _allowFiles = allowFiles;
        _baseDirectory = baseDirectory;
        _resolver = resolver;
    }

    public PdfImage? Load(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        src = src!.Trim();
        if (_cache.TryGetValue(src, out var cached)) return cached;
        PdfImage? img = null;
        try
        {
            var bytes = Fetch(src);
            if (bytes != null) img = PdfImage.FromBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException)
        {
            img = null; // a missing image must not fail the whole document
        }
        _cache[src] = img;
        return img;
    }

    private byte[]? Fetch(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = src.IndexOf(',');
            if (comma < 0) return null;
            var meta = src.Substring(5, comma - 5);
            var payload = src.Substring(comma + 1);
            if (meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(payload.Replace("\n", "").Replace("\r", "").Replace(" ", ""));
            return System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }

        // A user resolver gets the first chance at everything else (http, custom schemes, embedded resources).
        if (_resolver != null)
        {
            var bytes = _resolver(src);
            if (bytes != null) return bytes;
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null; // no network access unless the caller provides a resolver
        if (!_allowFiles) return null;

        string path = src;
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(src, UriKind.Absolute, out var uri))
            path = uri.LocalPath;
        else
        {
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            path = Uri.UnescapeDataString(path);
        }
        if (!Path.IsPathRooted(path))
        {
            if (_baseDirectory == null) return null;
            path = Path.Combine(_baseDirectory, path);
        }
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}

using System.Collections.Concurrent;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>A rasterized glyph: coverage bytes and the offset of its top-left pixel from the glyph origin's pixel.</summary>
internal sealed class GlyphBitmap
{
    public int X0, Y0, Width, Height;
    public byte[] Coverage = Array.Empty<byte>();
}

internal readonly record struct GlyphKey(object Source, int Gid, int A, int B, int C, int D, byte Fx, byte Fy);

/// <summary>Per-document caches shared by all pages: fonts, decoded images and rasterized glyphs.</summary>
internal sealed class RenderCache
{
    private const long ImageBudget = 512L * 1024 * 1024;
    private const int GlyphLimit = 50_000;

    private readonly Dictionary<PdfDict, PdfFont> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDict, DecodedImage?> _images = new(ReferenceEqualityComparer.Instance);
    private long _imageBytes;
    private readonly ConcurrentDictionary<GlyphKey, GlyphBitmap> _glyphs = new();
    private int _glyphCount;

    public PdfFont GetFont(PdfFile file, PdfDict dict)
    {
        lock (_fonts)
        {
            if (_fonts.TryGetValue(dict, out var f)) return f;
        }
        var font = PdfFont.Load(file, dict);
        lock (_fonts)
        {
            if (_fonts.TryGetValue(dict, out var f)) return f;
            _fonts[dict] = font;
            return font;
        }
    }

    public DecodedImage? GetImage(PdfStream stream, Func<DecodedImage?> decode)
    {
        lock (_images)
        {
            if (_images.TryGetValue(stream, out var img)) return img;
        }
        var decoded = decode();
        lock (_images)
        {
            if (_images.TryGetValue(stream, out var img)) return img;
            long size = decoded?.ByteSize ?? 0;
            if (_imageBytes + size <= ImageBudget)
            {
                _images[stream] = decoded;
                _imageBytes += size;
            }
            return decoded;
        }
    }

    public bool TryGetGlyph(in GlyphKey key, out GlyphBitmap bmp) => _glyphs.TryGetValue(key, out bmp!);

    public void AddGlyph(in GlyphKey key, GlyphBitmap bmp)
    {
        // ConcurrentDictionary.Count takes every lock, so keep a separate approximate count.
        if (Interlocked.Increment(ref _glyphCount) > GlyphLimit)
        {
            _glyphs.Clear();
            Interlocked.Exchange(ref _glyphCount, 0);
        }
        _glyphs[key] = bmp;
    }
}

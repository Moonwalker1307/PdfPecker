using PdfPecker.Fonts;

namespace PdfPecker.Rendering.Fonts;

/// <summary>The cmap table: its subtables by platform and encoding, read with <see cref="CmapTable"/>.</summary>
internal sealed partial class TrueTypeFont
{
    private void ReadCmapDirectory()
    {
        if (Table("cmap") is { } cmap) _cmaps = CmapTable.Subtables(_font, cmap.Offset, cmap.Length);
    }

    public bool HasCmap(int platform, int encoding) => _cmaps.Any(c => c.Platform == platform && c.Encoding == encoding);

    public bool HasAnyCmap => _cmaps.Count > 0;

    /// <summary>Looks a code up in the (platform, encoding) subtable. Returns 0 if absent.</summary>
    public int Lookup(int platform, int encoding, int code)
    {
        var map = GetCmap(platform, encoding);
        return map != null && map.TryGetValue(code, out int gid) ? gid : 0;
    }

    /// <summary>The first readable (platform, encoding) subtable as code → glyph, or null; the result is cached.</summary>
    public Dictionary<int, int>? GetCmap(int platform, int encoding)
    {
        lock (_lock)
        {
            int key = platform << 16 | encoding;
            if (_cmapCache.TryGetValue(key, out var cached)) return cached;
            Dictionary<int, int>? map = null;
            foreach (var subtable in _cmaps)
            {
                if (subtable.Platform != platform || subtable.Encoding != encoding) continue;
                // A damaged subtable is lost as a whole, and the next one for the same platform and encoding is tried.
                var sink = new CmapDictionary(new Dictionary<int, int>());
                try { map = CmapTable.Read(_font, subtable.Offset, ref sink) ? sink.Map : null; }
                catch (IndexOutOfRangeException ex) { SwallowedErrors.Record(ex); map = null; }
                if (map != null) break;
            }
            _cmapCache[key] = map;
            return map;
        }
    }

    /// <summary>The first usable subtable, for fonts whose cmap has an unusual platform.</summary>
    public Dictionary<int, int>? AnyCmap()
    {
        foreach (var subtable in _cmaps)
        {
            var map = GetCmap(subtable.Platform, subtable.Encoding);
            if (map != null && map.Count > 0) return map;
        }
        return null;
    }

    /// <summary>Collects a subtable's mappings as code → glyph.</summary>
    private readonly struct CmapDictionary(Dictionary<int, int> map) : ICmapSink
    {
        public Dictionary<int, int> Map => map;

        void ICmapSink.Map(int code, int glyph) => map[code] = glyph;
    }
}

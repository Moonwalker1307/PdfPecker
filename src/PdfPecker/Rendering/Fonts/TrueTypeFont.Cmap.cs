using System.Buffers.Binary;

namespace PdfPecker.Rendering.Fonts;

/// <summary>The cmap table: the subtable directory and the readers for formats 0, 2, 4, 6, 10, 12 and 13.</summary>
internal sealed partial class TrueTypeFont
{
    private const int CmapMinLength = 4, CmapSubtableCountField = 2, CmapRecordsStart = 4, CmapRecordSize = 8;
    private const int CmapRecordPlatformField = 0, CmapRecordEncodingField = 2, CmapRecordOffsetField = 4;

    // Subtable formats and the fields of each, relative to the subtable.
    private const int Format0ByteTable = 6;
    private const int Format2SubHeaderKeys = 6, Format2SubHeaderKeyCount = 256, Format2SubHeaderSize = 8;
    private const int Format4SegCountX2Field = 6, Format4EndCodes = 14;
    private const int Format6FirstCodeField = 6, Format6CountField = 8, Format6Glyphs = 10;
    private const int Format10FirstCodeField = 12, Format10CountField = 16, Format10Glyphs = 20, Format10MaxEntries = 65536;
    private const int Format12GroupCountField = 12, Format12Groups = 16, Format12GroupSize = 12;

    /// <summary>A format 12 or 13 subtable maps at most this many characters, so a damaged one cannot fill memory.</summary>
    private const int MaxGroupedCharacters = 200_000;

    private const int MaxCodePointRange = 0x10FFFF;

    private void ReadCmapDirectory()
    {
        if (Table("cmap") is not { Length: >= CmapMinLength } cmap) return;
        int count = _font.U16(cmap.Offset + CmapSubtableCountField);
        for (int i = 0; i < count; i++)
        {
            int record = cmap.Offset + CmapRecordsStart + i * CmapRecordSize;
            if (record + CmapRecordSize > cmap.Offset + cmap.Length) break;
            int platform = _font.U16(record + CmapRecordPlatformField);
            int encoding = _font.U16(record + CmapRecordEncodingField);
            int offset = (int)_font.U32(record + CmapRecordOffsetField);
            if (offset > 0 && offset < cmap.Length) _cmaps.Add(new CmapSubtable(platform, encoding, cmap.Offset + offset));
        }
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
                try { map = ReadCmapSubtable(subtable.Offset); }
                catch (IndexOutOfRangeException) { map = null; }
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

    private Dictionary<int, int>? ReadCmapSubtable(int at)
    {
        var map = new Dictionary<int, int>();
        switch (_font.U16(at))
        {
            case 0: ReadFormat0(at, map); break;
            case 2: ReadFormat2(at, map); break;
            case 4: ReadFormat4(at, map); break;
            case 6: ReadFormat6(at, map); break;
            case 10: ReadFormat10(at, map); break;
            case 12: ReadFormat12(at, map, sequential: true); break;
            case 13: ReadFormat12(at, map, sequential: false); break;
            default: return null;
        }
        return map;
    }

    // Byte encoding table: one glyph per single-byte code.
    private void ReadFormat0(int at, Dictionary<int, int> map)
    {
        var data = Data;
        for (int code = 0; code < 256; code++)
        {
            int gid = data[at + Format0ByteTable + code];
            if (gid != 0) map[code] = gid;
        }
    }

    // High-byte mapping through subHeaders (CJK): single bytes go through subHeader 0, other high bytes select one.
    private void ReadFormat2(int at, Dictionary<int, int> map)
    {
        int keys = at + Format2SubHeaderKeys;
        int subHeaders = keys + Format2SubHeaderKeyCount * 2;
        for (int high = 0; high < 256; high++)
        {
            int index = _font.U16(keys + high * 2) / Format2SubHeaderSize;
            int subHeader = subHeaders + index * Format2SubHeaderSize;
            int firstCode = _font.U16(subHeader), entryCount = _font.U16(subHeader + 2);
            int idDelta = _font.S16(subHeader + 4), idRangeOffset = _font.U16(subHeader + 6);
            // The range offset counts from its own field, which is the last of the sub-header.
            int glyphs = subHeader + 6 + idRangeOffset;
            for (int i = 0; i < entryCount; i++)
            {
                int address = glyphs + i * 2;
                if (address + 1 >= Data.Length) break;
                int gid = _font.U16(address);
                if (gid != 0) gid = (gid + idDelta) & 0xFFFF;
                if (gid == 0) continue;
                int code = index == 0 ? firstCode + i : high << 8 | (firstCode + i);
                map[code] = gid;
            }
        }
    }

    // Segment mapping to delta values: the usual Unicode BMP subtable.
    private void ReadFormat4(int at, Dictionary<int, int> map)
    {
        int segCountX2 = _font.U16(at + Format4SegCountX2Field);
        int endCodes = at + Format4EndCodes;
        int startCodes = endCodes + segCountX2 + 2; // after the reservedPad field
        int idDeltas = startCodes + segCountX2;
        int idRangeOffsets = idDeltas + segCountX2;
        for (int segment = 0; segment < segCountX2 / 2; segment++)
        {
            int end = _font.U16(endCodes + segment * 2), start = _font.U16(startCodes + segment * 2);
            int delta = _font.S16(idDeltas + segment * 2), rangeOffset = _font.U16(idRangeOffsets + segment * 2);
            if (start > end) continue;
            for (int code = start; code <= end && code < 0xFFFF; code++)
            {
                int gid;
                if (rangeOffset == 0)
                {
                    gid = (code + delta) & 0xFFFF;
                }
                else
                {
                    // The range offset counts from its own field into the glyph id array.
                    int address = idRangeOffsets + segment * 2 + rangeOffset + (code - start) * 2;
                    if (address + 1 >= Data.Length) continue;
                    gid = _font.U16(address);
                    if (gid != 0) gid = (gid + delta) & 0xFFFF;
                }
                if (gid != 0) map[code] = gid;
            }
        }
    }

    // Trimmed table mapping: a dense run of 16-bit codes.
    private void ReadFormat6(int at, Dictionary<int, int> map)
    {
        int first = _font.U16(at + Format6FirstCodeField), count = _font.U16(at + Format6CountField);
        for (int i = 0; i < count; i++)
        {
            int gid = _font.U16(at + Format6Glyphs + i * 2);
            if (gid != 0) map[first + i] = gid;
        }
    }

    // Trimmed array: a dense run of 32-bit codes.
    private void ReadFormat10(int at, Dictionary<int, int> map)
    {
        int first = (int)Field32(at + Format10FirstCodeField);
        int count = (int)Field32(at + Format10CountField);
        for (int i = 0; i < count && i < Format10MaxEntries; i++)
        {
            int gid = _font.U16(at + Format10Glyphs + i * 2);
            if (gid != 0) map[first + i] = gid;
        }
    }

    // Segmented coverage (12: sequential glyph ids per group) and many-to-one range mappings (13: one glyph per group).
    private void ReadFormat12(int at, Dictionary<int, int> map, bool sequential)
    {
        int groups = (int)Field32(at + Format12GroupCountField);
        int total = 0;
        for (int group = 0; group < groups; group++)
        {
            int record = at + Format12Groups + group * Format12GroupSize;
            int start = (int)Field32(record), end = (int)Field32(record + 4), gid = (int)Field32(record + 8);
            if (end < start || end - start > MaxCodePointRange) continue;
            for (int code = start; code <= end; code++)
            {
                if (++total > MaxGroupedCharacters) return;
                map[code] = sequential ? gid + (code - start) : gid;
            }
        }
    }

    // The 32-bit fields of the formats 10, 12 and 13 are read as spans, which fail with ArgumentOutOfRangeException
    // when they run past the data; GetCmap passes that on, as it always has, while a short 16-bit read fails with
    // IndexOutOfRangeException and only loses the subtable.
    private uint Field32(int at) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(at));
}

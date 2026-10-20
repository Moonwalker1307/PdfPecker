using System.Text;
using PdfPecker.Fonts;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// Parser for TrueType outlines (glyf/loca), character maps, metrics and glyph names. It is lenient the way a PDF
/// renderer has to be: a table that is cut off or points outside the file loses only what depends on it, and a glyph
/// that cannot be read draws nothing.
/// </summary>
internal sealed partial class TrueTypeFont : IGlyphSource
{
    private const uint CollectionTag = 0x74746366; // 'ttcf'
    private const int MinimumFileLength = 12;

    // The offset table (the directory header) and the table records after it.
    private const int CollectionFirstFontField = 12;
    private const int TableCountField = 4;
    private const int TableRecordsStart = 12;
    private const int TableRecordSize = 16;
    private const int RecordTagLength = 4, RecordOffsetField = 8, RecordLengthField = 12;

    // Fields of the fixed-layout tables, and the shortest table that holds them.
    private const int MaxpNumGlyphsField = 4, MaxpMinLength = 6;
    private const int HeadUnitsPerEmField = 18, HeadIndexToLocFormatField = 50, HeadMinLength = 54;
    private const int HheaAscenderField = 4, HheaDescenderField = 6, HheaNumberOfHMetricsField = 34, HheaMinLength = 36;
    private const int HmtxRecordSize = 4;
    private const int ShortLocaEntrySize = 2, LongLocaEntrySize = 4;

    private const int DefaultUnitsPerEm = 1000, MinUnitsPerEm = 16, MaxUnitsPerEm = 16384;

    // The post table: the glyph names of formats 1 and 2.
    private const uint PostFormat1 = 0x00010000, PostFormat2 = 0x00020000;
    private const int PostMinLength = 34, PostGlyphCountField = 32, PostIndicesStart = 34;
    private const int StandardMacGlyphCount = 258;

    private readonly TableReader _font;
    private readonly Dictionary<string, TableRecord> _tables = new(StringComparer.Ordinal);
    private int[] _loca = Array.Empty<int>();
    private int _glyf = -1, _glyfLength;
    private int _numberOfHMetrics, _hmtx = -1;
    private readonly List<CmapSubtable> _cmaps = new();
    private readonly Dictionary<int, Dictionary<int, int>?> _cmapCache = new();
    private Dictionary<string, int>? _postNames;
    private bool _postLoaded;
    private PathData?[] _glyphCache = Array.Empty<PathData?>();
    private readonly object _lock = new();

    private readonly record struct TableRecord(int Offset, int Length);

    private readonly record struct CmapSubtable(int Platform, int Encoding, int Offset);

    public int UnitsPerEm { get; private set; } = DefaultUnitsPerEm;
    public int GlyphCount { get; private set; }
    public Matrix FontMatrix => Matrix.Scale(1.0 / UnitsPerEm, 1.0 / UnitsPerEm);
    public int Ascender { get; private set; }
    public int Descender { get; private set; }

    /// <summary>The raw CFF table of an OpenType font with PostScript outlines, if any.</summary>
    public byte[]? CffTable { get; private set; }

    private TrueTypeFont(byte[] data) => _font = new TableReader(data);

    private byte[] Data => _font.Data;

    public static TrueTypeFont? Parse(byte[] data)
    {
        try
        {
            return ParseCore(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a font whose table records were found elsewhere, such as one face of a collection that is not its first.
    /// </summary>
    public static TrueTypeFont? Parse(byte[] data, IEnumerable<(string Tag, int Offset, int Length)> tables)
    {
        try
        {
            return ParseCore(data, tables);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static TrueTypeFont? ParseCore(byte[] data, IEnumerable<(string Tag, int Offset, int Length)>? tables = null)
    {
        if (data.Length < MinimumFileLength) return null;
        var font = new TrueTypeFont(data);
        if (tables == null) font.ReadTableDirectory();
        else
            foreach (var (tag, offset, length) in tables)
                if (offset >= 0 && offset <= data.Length && length >= 0) font._tables[tag] = new TableRecord(offset, Math.Min(length, data.Length - offset));
        if (font.Table("maxp") is { Length: >= MaxpMinLength } maxp) font.GlyphCount = font._font.U16(maxp.Offset + MaxpNumGlyphsField);
        if (font.Table("head") is { Length: >= HeadMinLength } head)
        {
            int unitsPerEm = font._font.U16(head.Offset + HeadUnitsPerEmField);
            font.UnitsPerEm = unitsPerEm is >= MinUnitsPerEm and <= MaxUnitsPerEm ? unitsPerEm : DefaultUnitsPerEm;
        }
        if (font.Table("hhea") is { Length: >= HheaMinLength } hhea)
        {
            font.Ascender = font._font.S16(hhea.Offset + HheaAscenderField);
            font.Descender = font._font.S16(hhea.Offset + HheaDescenderField);
            font._numberOfHMetrics = font._font.U16(hhea.Offset + HheaNumberOfHMetricsField);
        }
        if (font.Table("hmtx") is { } hmtx) font._hmtx = hmtx.Offset;
        if (font.Table("CFF ") is { } cff) font.CffTable = data.AsSpan(cff.Offset, cff.Length).ToArray();
        font.ReadLoca();
        font.ReadCmapDirectory();
        font._glyphCache = new PathData?[Math.Max(0, font.GlyphCount)];
        return font;
    }

    private TableRecord? Table(string tag) => _tables.TryGetValue(tag, out var record) ? record : null;

    /// <summary>
    /// Reads the table records of the font, or of the first font of a collection. A record that points outside the
    /// file is dropped and one that runs past the end is cut at the end, so later readers can trust the offsets.
    /// </summary>
    private void ReadTableDirectory()
    {
        var data = Data;
        int directory = _font.U32(0) == CollectionTag ? (int)_font.U32(CollectionFirstFontField) : 0;
        int tableCount = _font.U16(directory + TableCountField);
        for (int i = 0; i < tableCount; i++)
        {
            int record = directory + TableRecordsStart + i * TableRecordSize;
            if (record + TableRecordSize > data.Length) break;
            string tag = Encoding.ASCII.GetString(data, record, RecordTagLength);
            int offset = (int)_font.U32(record + RecordOffsetField);
            int length = (int)_font.U32(record + RecordLengthField);
            if (offset < 0 || offset > data.Length) continue;
            _tables[tag] = new TableRecord(offset, Math.Min(length, data.Length - offset));
        }
    }

    /// <summary>
    /// Reads the glyph offsets. Subset fonts sometimes have a loca table shorter than maxp says, and a font without
    /// maxp is given as many glyphs as loca has entries for.
    /// </summary>
    private void ReadLoca()
    {
        if (Table("glyf") is not { } glyf || Table("loca") is not { } loca || Table("head") is not { } head) return;
        _glyf = glyf.Offset;
        _glyfLength = glyf.Length;
        bool longFormat = _font.S16(head.Offset + HeadIndexToLocFormatField) != 0;
        int entrySize = longFormat ? LongLocaEntrySize : ShortLocaEntrySize;
        int entries = loca.Length / entrySize;
        int numGlyphs = GlyphCount == 0 ? Math.Max(0, entries - 1) : GlyphCount;
        int count = Math.Min(entries, numGlyphs + 1);
        _loca = new int[count];
        for (int i = 0; i < count; i++)
        {
            int at = loca.Offset + i * entrySize;
            _loca[i] = longFormat ? (int)_font.U32(at) : _font.U16(at) * 2;
        }
        if (GlyphCount == 0) GlyphCount = Math.Max(0, count - 1);
    }

    // ------------------------------------------------------------------ post table glyph names

    public int GlyphByName(string name)
    {
        lock (_lock)
        {
            if (!_postLoaded)
            {
                _postLoaded = true;
                try { _postNames = ReadPostNames(); }
                catch (IndexOutOfRangeException) { _postNames = null; }
            }
        }
        return _postNames != null && _postNames.TryGetValue(name, out int gid) ? gid : -1;
    }

    /// <summary>All glyph names from the post table (format 2), by glyph id.</summary>
    public Dictionary<string, int>? PostNames
    {
        get
        {
            GlyphByName("");
            return _postNames;
        }
    }

    private Dictionary<string, int>? ReadPostNames()
    {
        if (Table("post") is not { Length: >= PostMinLength } post) return null;
        uint version = _font.U32(post.Offset);
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        if (version == PostFormat1)
        {
            for (int i = 0; i < MacGlyphNames.Length; i++) names.TryAdd(MacGlyphNames[i], i);
            return names;
        }
        if (version != PostFormat2) return null;
        int glyphCount = _font.U16(post.Offset + PostGlyphCountField);
        var indices = new int[glyphCount];
        for (int i = 0; i < glyphCount; i++) indices[i] = _font.U16(post.Offset + PostIndicesStart + i * 2);
        var customNames = ReadPascalStrings(post.Offset + PostIndicesStart + glyphCount * 2, post.Offset + post.Length);
        for (int gid = 0; gid < glyphCount; gid++)
        {
            int index = indices[gid];
            string? name = index < StandardMacGlyphCount ? MacGlyphNames[index]
                : index - StandardMacGlyphCount < customNames.Count ? customNames[index - StandardMacGlyphCount] : null;
            if (name != null) names.TryAdd(name, gid);
        }
        return names;
    }

    // The custom names of a format 2 post table: length-prefixed strings up to the end of the table.
    private List<string> ReadPascalStrings(int position, int end)
    {
        var data = Data;
        var strings = new List<string>();
        while (position < end)
        {
            int length = data[position++];
            if (position + length > end) break;
            strings.Add(Encoding.Latin1.GetString(data, position, length));
            position += length;
        }
        return strings;
    }

    // ------------------------------------------------------------------ metrics

    public double Advance(int gid)
    {
        if (_hmtx < 0 || _numberOfHMetrics <= 0) return -1;
        // Glyphs past the last metric share its advance, as the hmtx table defines.
        int record = _hmtx + Math.Min(gid, _numberOfHMetrics - 1) * HmtxRecordSize;
        if (record + 2 > Data.Length) return -1;
        return _font.U16(record);
    }
}

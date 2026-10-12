using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// A parsed TrueType or OpenType font program (an sfnt), read from a .ttf/.otf file, one font of a collection (.ttc)
/// or a WOFF or WOFF2 file. It gives layout the character map and advance widths, and gives the PDF writer the tables
/// to embed.
/// Instances are immutable after construction apart from lazily built lookup tables, so they can be shared by
/// documents converted in parallel.
/// </summary>
internal sealed class FontProgram
{
    private const uint TagCff = 0x43464620;  // 'CFF '
    private const uint TagCff2 = 0x43464632; // 'CFF2'

    internal readonly byte[] Data; // the sfnt; for a collection, the whole file (table offsets are absolute)
    private readonly Dictionary<uint, (int Offset, int Length)> _tables = new();

    public readonly int UnitsPerEm;
    public readonly int NumGlyphs;
    public readonly bool IsCff;
    public readonly short XMin, YMin, XMax, YMax;
    public readonly int Ascender, Descender, LineGap; // hhea, in font units
    public readonly int LineAscender, LineDescender, LineGapUnits; // what browsers space lines with: hhea, or OS/2 typo metrics
    public readonly int CapHeight;
    public readonly float ItalicAngle;
    public readonly bool FixedPitch;
    public readonly int WeightClass;
    public readonly bool IsItalic;
    public readonly string FamilyName;          // name ID 1
    public readonly string? TypographicFamily;  // name ID 16
    public readonly string FullName;            // name ID 4
    public readonly string PostScriptName;      // name ID 6, sanitized for use as a PDF name
    private readonly int _numHMetrics;
    private readonly int _hmtx, _hmtxLength;

    private readonly object _lock = new();
    private ushort[]? _bmp;                     // BMP code point -> glyph id
    private Dictionary<int, ushort>? _astral;   // supplementary planes
    private short[]? _charWidths;               // BMP code point -> advance in 1/1000 em
    private Kerning? _kerning;
    private bool _kerningLoaded;
    private Ligatures? _ligatures;
    private bool _ligaturesLoaded;

    private FontProgram(byte[] data, int dirOffset)
    {
        Data = data;
        int numTables = U16(dirOffset + 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = dirOffset + 12 + i * 16;
            uint tag = U32(rec);
            int off = (int)U32(rec + 8), len = (int)U32(rec + 12);
            if (off < 0 || len < 0 || off > data.Length || len > data.Length - off) continue;
            _tables[tag] = (off, len);
        }

        var head = Table("head") ?? throw new FormatException("The font has no head table.");
        UnitsPerEm = U16(head.Offset + 18);
        if (UnitsPerEm < 16 || UnitsPerEm > 16384) UnitsPerEm = 1000;
        XMin = S16(head.Offset + 36);
        YMin = S16(head.Offset + 38);
        XMax = S16(head.Offset + 40);
        YMax = S16(head.Offset + 42);
        int macStyle = U16(head.Offset + 44);

        var maxp = Table("maxp") ?? throw new FormatException("The font has no maxp table.");
        NumGlyphs = U16(maxp.Offset + 4);

        var hhea = Table("hhea") ?? throw new FormatException("The font has no hhea table.");
        Ascender = S16(hhea.Offset + 4);
        Descender = S16(hhea.Offset + 6);
        LineGap = S16(hhea.Offset + 8);
        _numHMetrics = U16(hhea.Offset + 34);
        var hmtx = Table("hmtx") ?? throw new FormatException("The font has no hmtx table.");
        _hmtx = hmtx.Offset;
        _hmtxLength = hmtx.Length;
        _numHMetrics = Math.Max(1, Math.Min(_numHMetrics, _hmtxLength / 4));

        IsCff = _tables.ContainsKey(TagCff) || _tables.ContainsKey(TagCff2);
        if (!IsCff && (Table("glyf") == null || Table("loca") == null)) throw new FormatException("The font has no outlines.");

        WeightClass = 400;
        CapHeight = (int)(Ascender * 0.7);
        int? typoA = null, typoD = null, typoG = null;
        if (Table("OS/2") is { } os2 && os2.Length >= 78)
        {
            WeightClass = U16(os2.Offset + 4);
            int fsSelection = U16(os2.Offset + 62);
            IsItalic = (fsSelection & 0x201) != 0; // ITALIC or OBLIQUE
            // USE_TYPO_METRICS, or an hhea table without line metrics: use the OS/2 typographic values, as Chrome does.
            if ((fsSelection & 0x80) != 0 || Ascender == 0 && Descender == 0)
            {
                typoA = S16(os2.Offset + 68);
                typoD = S16(os2.Offset + 70);
                typoG = S16(os2.Offset + 72);
            }
            if (U16(os2.Offset) >= 2 && os2.Length >= 90) CapHeight = S16(os2.Offset + 88);
        }
        if (WeightClass < 1 || WeightClass > 1000) WeightClass = 400;
        LineAscender = typoA ?? Ascender;
        LineDescender = typoD ?? Descender;
        LineGapUnits = typoG ?? LineGap;
        if ((macStyle & 2) != 0) IsItalic = true;

        if (Table("post") is { } post && post.Length >= 16)
        {
            ItalicAngle = S16(post.Offset + 4) + U16(post.Offset + 6) / 65536f;
            FixedPitch = U32(post.Offset + 12) != 0;
        }

        string? family = null, full = null, ps = null;
        if (Table("name") is { } name) ReadNames(name, ref family, ref TypographicFamily, ref full, ref ps);
        FamilyName = family ?? TypographicFamily ?? "Font";
        FullName = full ?? FamilyName;
        PostScriptName = SanitizeName(ps ?? FullName);
    }

    /// <summary>Parses a font file: one font for .ttf/.otf/.woff/.woff2, every font of a collection.</summary>
    public static List<FontProgram> Load(byte[] data)
    {
        var result = new List<FontProgram>();
        try
        {
            if (data.Length < 12) throw new FormatException("Not a font file.");
            uint sig = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (sig == 0x774F4646) // 'wOFF'
            {
                data = DecodeWoff(data);
                sig = BinaryPrimitives.ReadUInt32BigEndian(data);
            }
            else if (sig == 0x774F4632) // 'wOF2'
            {
                var sfnts = Woff2Decoder.Decode(data);
                if (sfnts.Count == 1) result.Add(new FontProgram(sfnts[0], 0));
                else
                    foreach (var sfnt in sfnts)
                    {
                        try { result.Add(new FontProgram(sfnt, 0)); }
                        catch (Exception ex) when (IsFormatError(ex)) { }
                    }
                return result;
            }
            if (sig == 0x74746366) // 'ttcf'
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
                for (int i = 0; i < count && i < 256; i++)
                {
                    int dir = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12 + i * 4));
                    try { result.Add(new FontProgram(data, dir)); }
                    catch (Exception ex) when (IsFormatError(ex)) { }
                }
                return result;
            }
            if (sig == 0x00010000 || sig == 0x4F54544F || sig == 0x74727565) // 1.0, 'OTTO', 'true'
                result.Add(new FontProgram(data, 0));
            else throw new FormatException("Not a TrueType or OpenType font.");
        }
        catch (Exception ex) when (ex is not (FormatException or NotSupportedException) && IsFormatError(ex))
        {
            throw new FormatException("The font file is damaged.", ex);
        }
        return result;
    }

    internal static bool IsFormatError(Exception ex)
        => ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException or NotSupportedException
            or EndOfStreamException;

    // ------------------------------------------------------------------ lookups

    public (int Offset, int Length)? Table(string tag) => _tables.TryGetValue(Tag(tag), out var t) ? t : null;

    public IEnumerable<(uint Tag, int Offset, int Length)> Tables => _tables.Select(kv => (kv.Key, kv.Value.Offset, kv.Value.Length));

    public static uint Tag(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    /// <summary>The glyph for a Unicode code point, or 0 (.notdef) when the font has none.</summary>
    public int GlyphId(int codePoint)
    {
        var bmp = _bmp ?? BuildCharMap();
        if ((uint)codePoint < 0x10000) return bmp[codePoint];
        return _astral != null && _astral.TryGetValue(codePoint, out var g) ? g : 0;
    }

    /// <summary>Advance width of a glyph in font units.</summary>
    public int Advance(int gid)
    {
        if ((uint)gid >= (uint)NumGlyphs) gid = 0;
        int i = Math.Min(gid, _numHMetrics - 1);
        return U16(_hmtx + i * 4);
    }

    /// <summary>Left side bearing of a glyph in font units.</summary>
    public short LeftSideBearing(int gid)
    {
        if (gid < _numHMetrics) return S16(_hmtx + gid * 4 + 2);
        int off = _numHMetrics * 4 + (gid - _numHMetrics) * 2;
        return off + 2 <= _hmtxLength ? S16(_hmtx + off) : (short)0;
    }

    /// <summary>Advance width in 1/1000 em, the unit used by layout and by PDF width arrays.</summary>
    public int Advance1000(int gid) => (int)Math.Round(Advance(gid) * 1000.0 / UnitsPerEm);

    /// <summary>The font's pair kerning, or null when it has none.</summary>
    public Kerning? Kerning
    {
        get
        {
            if (Volatile.Read(ref _kerningLoaded)) return _kerning;
            lock (_lock)
            {
                if (!_kerningLoaded)
                {
                    _kerning = Kerning.Load(this);
                    Volatile.Write(ref _kerningLoaded, true);
                }
                return _kerning;
            }
        }
    }

    /// <summary>The font's standard ligatures, or null when it has none.</summary>
    public Ligatures? Ligatures
    {
        get
        {
            if (Volatile.Read(ref _ligaturesLoaded)) return _ligatures;
            lock (_lock)
            {
                if (!_ligaturesLoaded)
                {
                    _ligatures = Ligatures.Load(this);
                    Volatile.Write(ref _ligaturesLoaded, true);
                }
                return _ligatures;
            }
        }
    }

    /// <summary>The kerning between two glyphs in 1/1000 em (usually negative), added to the first glyph's advance.</summary>
    public int Kern1000(int left, int right)
    {
        var k = Kerning;
        if (k == null) return 0;
        int v = k.Pair(left, right);
        return v == 0 ? 0 : (int)Math.Round(v * 1000.0 / UnitsPerEm);
    }

    /// <summary>Advance widths in 1/1000 em indexed by BMP code point.</summary>
    public short[] CharWidths => _charWidths ?? BuildCharWidths();

    private short[] BuildCharWidths()
    {
        lock (_lock)
        {
            if (_charWidths != null) return _charWidths;
            var bmp = _bmp ?? BuildCharMap();
            var gidWidths = new short[Math.Max(1, NumGlyphs)];
            for (int g = 0; g < gidWidths.Length; g++) gidWidths[g] = (short)Math.Clamp(Advance1000(g), short.MinValue, short.MaxValue);
            var w = new short[65536];
            for (int c = 0; c < w.Length; c++) w[c] = gidWidths[bmp[c]];
            _charWidths = w;
            return w;
        }
    }

    private ushort[] BuildCharMap()
    {
        lock (_lock)
        {
            if (_bmp != null) return _bmp;
            var bmp = new ushort[65536];
            Dictionary<int, ushort>? astral = null;
            if (Table("cmap") is { } cmap)
            try
            {
                int best = -1, bestScore = -1;
                int n = U16(cmap.Offset + 2);
                for (int i = 0; i < n; i++)
                {
                    int rec = cmap.Offset + 4 + i * 8;
                    int platform = U16(rec), encoding = U16(rec + 2);
                    int sub = cmap.Offset + (int)U32(rec + 4);
                    if (sub + 2 > cmap.Offset + cmap.Length) continue;
                    int format = U16(sub);
                    if (format is not (0 or 4 or 6 or 12)) continue;
                    int score = (platform, encoding) switch
                    {
                        (3, 10) => 6,
                        (0, 4) or (0, 6) => 5,
                        (3, 1) => 4,
                        (0, _) => 3,
                        (3, 0) => 2,
                        (1, 0) => 1,
                        _ => -1,
                    };
                    if (score > bestScore) { bestScore = score; best = sub; }
                }
                if (best >= 0)
                {
                    ReadCmap(best, bmp, ref astral);
                    if (bestScore == 2)
                    {
                        // Symbol fonts map their glyphs at U+F020..U+F0FF; make them reachable from plain ASCII/Latin-1 too.
                        for (int c = 0x20; c < 0x100; c++)
                            if (bmp[c] == 0) bmp[c] = bmp[0xF000 + c];
                    }
                }
            }
            catch (Exception ex) when (IsFormatError(ex))
            {
                // A damaged character map: keep what was read before the damage.
            }
            _astral = astral;
            _bmp = bmp;
            return bmp;
        }
    }

    private void ReadCmap(int sub, ushort[] bmp, ref Dictionary<int, ushort>? astral)
    {
        int numGlyphs = NumGlyphs;
        void Set(int c, int g, ref Dictionary<int, ushort>? a)
        {
            if (g <= 0 || g >= numGlyphs) return;
            if (c < 0x10000) { if (c >= 0) bmp[c] = (ushort)g; }
            else if (c <= 0x10FFFF) (a ??= new Dictionary<int, ushort>())[c] = (ushort)g;
        }

        switch (U16(sub))
        {
            case 0:
                for (int c = 0; c < 256; c++) Set(c, Data[sub + 6 + c], ref astral);
                break;
            case 6:
            {
                int first = U16(sub + 6), count = U16(sub + 8);
                for (int i = 0; i < count; i++) Set(first + i, U16(sub + 10 + i * 2), ref astral);
                break;
            }
            case 4:
            {
                int segX2 = U16(sub + 6);
                int ends = sub + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                for (int s = 0; s < segX2; s += 2)
                {
                    int end = U16(ends + s), start = U16(starts + s), delta = S16(deltas + s), ro = U16(ranges + s);
                    if (start > end) continue;
                    for (int c = start; c <= end && c != 0xFFFF; c++)
                    {
                        int g;
                        if (ro == 0) g = (c + delta) & 0xFFFF;
                        else
                        {
                            int addr = ranges + s + ro + (c - start) * 2;
                            if (addr + 2 > Data.Length) break;
                            g = U16(addr);
                            if (g != 0) g = (g + delta) & 0xFFFF;
                        }
                        Set(c, g, ref astral);
                    }
                }
                break;
            }
            case 12:
            {
                long groups = U32(sub + 12);
                for (long i = 0; i < groups; i++)
                {
                    int rec = sub + 16 + (int)i * 12;
                    long start = U32(rec), end = U32(rec + 4), g = U32(rec + 8);
                    if (end > 0x10FFFF || start > end) continue;
                    for (long c = start; c <= end; c++) Set((int)c, (int)(g + c - start), ref astral);
                }
                break;
            }
        }
    }

    // ------------------------------------------------------------------ names

    private void ReadNames((int Offset, int Length) table, ref string? family, ref string? typoFamily, ref string? full, ref string? ps)
    {
        int count = U16(table.Offset + 2), strings = table.Offset + U16(table.Offset + 4);
        var best = new int[26]; // per name ID: score of the record chosen so far
        for (int i = 0; i < count; i++)
        {
            int rec = table.Offset + 6 + i * 12;
            if (rec + 12 > table.Offset + table.Length) break;
            int platform = U16(rec), encoding = U16(rec + 2), language = U16(rec + 4), id = U16(rec + 6);
            int len = U16(rec + 8), off = strings + U16(rec + 10);
            if (id is not (1 or 4 or 6 or 16) || off + len > Data.Length) continue;
            int score = platform switch
            {
                3 when encoding is 1 or 10 => language == 0x409 ? 4 : 3,
                0 => 2,
                1 when encoding == 0 => language == 0 ? 1 : 0,
                _ => -1,
            };
            if (score < 0 || score + 1 <= best[id]) continue;
            best[id] = score + 1;
            string value = platform == 1 ? Encoding.Latin1.GetString(Data, off, len) : Encoding.BigEndianUnicode.GetString(Data, off, len);
            value = value.Trim('\0', ' ');
            if (value.Length == 0) continue;
            switch (id)
            {
                case 1: family = value; break;
                case 4: full = value; break;
                case 6: ps = value; break;
                case 16: typoFamily = value; break;
            }
        }
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            if (c > 32 && c < 127 && "()<>[]{}/%#".IndexOf(c) < 0) sb.Append(c);
        if (sb.Length == 0) sb.Append("Font");
        return sb.Length > 100 ? sb.ToString(0, 100) : sb.ToString();
    }

    // ------------------------------------------------------------------ WOFF

    private const long MaxWoffSize = 64L * 1024 * 1024;

    /// <summary>Unpacks a WOFF 1.0 file into the sfnt it wraps.</summary>
    private static byte[] DecodeWoff(byte[] woff)
    {
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(4));
        var tables = new List<(uint Tag, byte[] Data)>(numTables);
        long total = 0;
        for (int i = 0; i < numTables; i++)
        {
            var e = woff.AsSpan(44 + i * 20);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(e);
            int off = (int)BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            int compLen = (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            int origLen = (int)BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
            if (off < 0 || compLen < 0 || origLen < 0 || (long)off + compLen > woff.Length) throw new FormatException("Damaged WOFF table.");
            // The declared sizes are allocated up front: keep them to what zlib can produce from the data and to a sane
            // font size, so a small file cannot claim gigabytes.
            total += origLen;
            if (total > MaxWoffSize || (long)origLen > (long)compLen * 1100 + 1024) throw new FormatException("Damaged WOFF table.");
            byte[] data;
            if (compLen < origLen)
            {
                data = new byte[origLen];
                using var z = new ZLibStream(new MemoryStream(woff, off, compLen), CompressionMode.Decompress);
                z.ReadExactly(data);
            }
            else data = woff.AsSpan(off, origLen).ToArray();
            tables.Add((tag, data));
        }
        return SfntBuilder.Build(flavor, tables);
    }

    // ------------------------------------------------------------------ binary helpers

    internal int U16(int o) => Data[o] << 8 | Data[o + 1];
    internal short S16(int o) => (short)(Data[o] << 8 | Data[o + 1]);
    internal uint U32(int o) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(o, 4));
}

/// <summary>Assembles sfnt font files from tables, with a sorted table directory and correct checksums.</summary>
internal static class SfntBuilder
{
    public static byte[] Build(uint version, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        int n = tables.Count;
        int size = 12 + 16 * n;
        foreach (var t in tables) size += (t.Data.Length + 3) & ~3;
        var o = new byte[size];
        var span = o.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, version);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)n);
        int pow = 1, log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(pow * 16));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(n * 16 - pow * 16));
        int offset = 12 + 16 * n, headOffset = -1;
        for (int i = 0; i < n; i++)
        {
            var (tag, data) = tables[i];
            if (tag == 0x68656164 && data.Length >= 12) // 'head': checkSumAdjustment is computed over the file with it zeroed
            {
                Array.Clear(data, 8, 4);
                headOffset = offset;
            }
            data.CopyTo(o, offset);
            int rec = 12 + i * 16;
            BinaryPrimitives.WriteUInt32BigEndian(span[rec..], tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 4)..], Checksum(o.AsSpan(offset, (data.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 8)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 12)..], (uint)data.Length);
            offset += (data.Length + 3) & ~3;
        }
        if (headOffset >= 0) BinaryPrimitives.WriteUInt32BigEndian(span[(headOffset + 8)..], 0xB1B0AFBA - Checksum(o));
        return o;
    }

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        if (i < data.Length)
        {
            uint last = 0;
            for (int k = 0; k < 4; k++) last = last << 8 | (i + k < data.Length ? data[i + k] : 0u);
            sum += last;
        }
        return sum;
    }
}

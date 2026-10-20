using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace PdfPecker.Fonts;

/// <summary>
/// A parsed TrueType or OpenType font program (an sfnt), read from a .ttf/.otf file, one font of a collection (.ttc)
/// or a WOFF or WOFF2 file. It gives layout the character map and advance widths, and gives the PDF writer the tables
/// to embed.
/// Instances are immutable after construction apart from lazily built lookup tables, so they can be shared by
/// documents converted in parallel.
/// </summary>
internal sealed partial class FontProgram
{
    // sfnt versions: TrueType, OpenType with CFF outlines, Apple TrueType, a collection, and the two WOFF wrappers.
    private const uint TrueTypeVersion = 0x00010000, OpenTypeVersion = 0x4F54544F, AppleTrueTypeVersion = 0x74727565; // 'OTTO', 'true'
    private const uint CollectionVersion = 0x74746366, WoffSignature = 0x774F4646, Woff2Signature = 0x774F4632; // 'ttcf', 'wOFF', 'wOF2'
    private const int MinimumFileLength = 12;

    // The offset table and the table records that follow it.
    private const int NumTablesField = 4, OffsetTableLength = 12;
    private const int TableRecordLength = 16, RecordOffsetField = 8, RecordLengthField = 12;

    // A collection header: the font count and then one offset table position per font.
    private const int CollectionCountField = 8, CollectionOffsetsField = 12, MaxCollectionFonts = 256;

    private const uint TagCff = 0x43464620;  // 'CFF '
    private const uint TagCff2 = 0x43464632; // 'CFF2'

    // head: unitsPerEm, the bounding box and macStyle (bit 1 is italic).
    private const int HeadMinimumLength = 54;
    private const int UnitsPerEmField = 18, XMinField = 36, YMinField = 38, XMaxField = 40, YMaxField = 42, MacStyleField = 44;
    private const int MacStyleItalic = 0x2;
    private const int MinUnitsPerEm = 16, MaxUnitsPerEm = 16384, DefaultUnitsPerEm = 1000;

    // maxp: the glyph count.
    private const int MaxpMinimumLength = 6, NumGlyphsField = 4;

    // hhea: the line metrics and the number of full metrics in hmtx.
    private const int HheaMinimumLength = 36;
    private const int AscenderField = 4, DescenderField = 6, LineGapField = 8, NumberOfHMetricsField = 34;

    // hmtx: a full metric is an advance and a left side bearing; the glyphs after numberOfHMetrics have bearings only.
    private const int FullMetricLength = 4, BearingOnlyLength = 2, BearingField = 2;

    // OS/2: the weight class, fsSelection (ITALIC, OBLIQUE and USE_TYPO_METRICS) and the typographic metrics, and from
    // version 2 the cap height.
    private const int Os2MinimumLength = 78, Os2VersionField = 0, Os2CapHeightVersion = 2, Os2CapHeightMinimumLength = 90;
    private const int WeightClassField = 4, SelectionField = 62, TypoAscenderField = 68, TypoDescenderField = 70, TypoLineGapField = 72;
    private const int CapHeightField = 88, XHeightField = 86;
    private const int SelectionItalicOrOblique = 0x201, SelectionUseTypoMetrics = 0x80;
    private const int DefaultWeightClass = 400, MinWeightClass = 1, MaxWeightClass = 1000;
    // Without a cap height of its own the font is given this share of its ascender, which is typical of Latin designs.
    private const double CapHeightShareOfAscender = 0.7;

    // post: the italic angle as a 16.16 fixed number and the fixed pitch flag.
    private const int PostMinimumLength = 16, ItalicAngleField = 4, IsFixedPitchField = 12;

    // One font keeps at most this many instances, so a stylesheet with many weights cannot grow memory without bound;
    // further locations use the default instance.
    private const int MaxInstances = 64;

    internal readonly byte[] Data; // the sfnt; for a collection, the whole file (table offsets are absolute)
    private readonly Dictionary<uint, (int Offset, int Length)> _tables = new();

    public readonly int UnitsPerEm;
    public readonly int NumGlyphs;
    public readonly bool IsCff;
    public readonly short XMin, YMin, XMax, YMax;
    public readonly int Ascender, Descender, LineGap; // hhea, in font units
    public readonly int LineAscender, LineDescender, LineGapUnits; // what browsers space lines with: hhea, or OS/2 typo metrics
    public readonly int CapHeight;
    private readonly int _os2XHeight; // sxHeight of OS/2 version 2 and later; 0 when the font does not say
    public readonly float ItalicAngle;
    public readonly bool FixedPitch;
    public readonly int WeightClass;
    public readonly bool IsItalic;
    public readonly string FamilyName;          // name ID 1
    public readonly string? TypographicFamily;  // name ID 16
    public readonly string FullName;            // name ID 4
    public string PostScriptName { get; private set; } // name ID 6, sanitized for use as a PDF name
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
    private OpenTypeLayout? _layout;
    private bool _layoutLoaded;
    private VariationAxis[]? _axes;
    private readonly ConcurrentDictionary<string, FontProgram> _instances = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, FontProgram>? _featureVariants;

    private FontProgram(byte[] data, int offsetTable)
    {
        Data = data;
        ReadTableDirectory(offsetTable);

        var head = RequireTable("head", HeadMinimumLength, "The font has no complete head table.");
        UnitsPerEm = Reader.U16(head + UnitsPerEmField);
        if (UnitsPerEm < MinUnitsPerEm || UnitsPerEm > MaxUnitsPerEm) UnitsPerEm = DefaultUnitsPerEm;
        XMin = Reader.S16(head + XMinField);
        YMin = Reader.S16(head + YMinField);
        XMax = Reader.S16(head + XMaxField);
        YMax = Reader.S16(head + YMaxField);
        int macStyle = Reader.U16(head + MacStyleField);

        var maxp = RequireTable("maxp", MaxpMinimumLength, "The font has no complete maxp table.");
        NumGlyphs = Reader.U16(maxp + NumGlyphsField);

        var hhea = RequireTable("hhea", HheaMinimumLength, "The font has no complete hhea table.");
        Ascender = Reader.S16(hhea + AscenderField);
        Descender = Reader.S16(hhea + DescenderField);
        LineGap = Reader.S16(hhea + LineGapField);
        var hmtx = Table("hmtx") ?? throw new FormatException("The font has no hmtx table.");
        _hmtx = hmtx.Offset;
        _hmtxLength = hmtx.Length;
        // A count beyond the table would read past it; at least one metric keeps Advance well defined.
        _numHMetrics = Math.Max(1, Math.Min(Reader.U16(hhea + NumberOfHMetricsField), _hmtxLength / FullMetricLength));

        IsCff = _tables.ContainsKey(TagCff) || _tables.ContainsKey(TagCff2);
        if (!IsCff && (Table("glyf") == null || Table("loca") == null)) throw new FormatException("The font has no outlines.");

        var style = ReadStyle();
        WeightClass = style.WeightClass;
        IsItalic = style.Italic || (macStyle & MacStyleItalic) != 0;
        CapHeight = style.CapHeight ?? (int)(Ascender * CapHeightShareOfAscender);
        _os2XHeight = style.XHeight ?? 0;
        LineAscender = style.TypoAscender ?? Ascender;
        LineDescender = style.TypoDescender ?? Descender;
        LineGapUnits = style.TypoLineGap ?? LineGap;

        if (Table("post") is { Length: >= PostMinimumLength } post)
        {
            ItalicAngle = Reader.S16(post.Offset + ItalicAngleField) + Reader.U16(post.Offset + ItalicAngleField + 2) / 65536f;
            FixedPitch = Reader.U32(post.Offset + IsFixedPitchField) != 0;
        }

        var names = Table("name") is { } name ? ReadNames(name) : default;
        TypographicFamily = names.TypographicFamily;
        FamilyName = names.Family ?? TypographicFamily ?? "Font";
        FullName = names.Full ?? FamilyName;
        PostScriptName = SanitizeName(names.PostScript ?? FullName);
    }

    // Records whose table lies outside the file are left out, so a damaged directory loses tables rather than the font.
    private void ReadTableDirectory(int offsetTable)
    {
        int numTables = Reader.U16(offsetTable + NumTablesField);
        for (int i = 0; i < numTables; i++)
        {
            int record = offsetTable + OffsetTableLength + i * TableRecordLength;
            uint tag = Reader.U32(record);
            int offset = (int)Reader.U32(record + RecordOffsetField), length = (int)Reader.U32(record + RecordLengthField);
            if (offset < 0 || length < 0 || offset > Data.Length || length > Data.Length - offset) continue;
            _tables[tag] = (offset, length);
        }
    }

    private int RequireTable(string tag, int minimumLength, string missingMessage)
        => Table(tag) is { } table && table.Length >= minimumLength ? table.Offset : throw new FormatException(missingMessage);

    private readonly record struct StyleFields(int WeightClass, bool Italic, int? CapHeight, int? XHeight, int? TypoAscender, int? TypoDescender, int? TypoLineGap);

    // What the OS/2 table says about the style, with the defaults of a font that has none.
    private StyleFields ReadStyle()
    {
        var style = new StyleFields(DefaultWeightClass, false, null, null, null, null, null);
        if (Table("OS/2") is not { Length: >= Os2MinimumLength } os2) return style;
        int weightClass = Reader.U16(os2.Offset + WeightClassField);
        if (weightClass < MinWeightClass || weightClass > MaxWeightClass) weightClass = DefaultWeightClass;
        int selection = Reader.U16(os2.Offset + SelectionField);
        style = style with { WeightClass = weightClass, Italic = (selection & SelectionItalicOrOblique) != 0 };
        // USE_TYPO_METRICS, or an hhea table without line metrics: use the OS/2 typographic values, as Chrome does.
        if ((selection & SelectionUseTypoMetrics) != 0 || Ascender == 0 && Descender == 0)
            style = style with
            {
                TypoAscender = Reader.S16(os2.Offset + TypoAscenderField),
                TypoDescender = Reader.S16(os2.Offset + TypoDescenderField),
                TypoLineGap = Reader.S16(os2.Offset + TypoLineGapField),
            };
        if (Reader.U16(os2.Offset + Os2VersionField) >= Os2CapHeightVersion && os2.Length >= Os2CapHeightMinimumLength)
            style = style with { CapHeight = Reader.S16(os2.Offset + CapHeightField), XHeight = Reader.S16(os2.Offset + XHeightField) };
        return style;
    }

    /// <summary>Parses a font file: one font for .ttf/.otf/.woff/.woff2, every font of a collection.</summary>
    public static List<FontProgram> Load(byte[] data)
    {
        var result = new List<FontProgram>();
        try
        {
            if (data.Length < MinimumFileLength) throw new FormatException("Not a font file.");
            uint signature = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (signature == WoffSignature)
            {
                data = DecodeWoff(data);
                signature = BinaryPrimitives.ReadUInt32BigEndian(data);
            }
            else if (signature == Woff2Signature)
            {
                var sfnts = Woff2Decoder.Decode(data);
                if (sfnts.Count == 1) result.Add(new FontProgram(sfnts[0], 0));
                else
                    foreach (var sfnt in sfnts) TryAdd(result, sfnt, 0);
                return result;
            }
            if (signature == CollectionVersion)
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(CollectionCountField));
                for (int i = 0; i < count && i < MaxCollectionFonts; i++)
                    TryAdd(result, data, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(CollectionOffsetsField + i * 4)));
                return result;
            }
            if (signature is TrueTypeVersion or OpenTypeVersion or AppleTrueTypeVersion) result.Add(new FontProgram(data, 0));
            else throw new FormatException("Not a TrueType or OpenType font.");
        }
        catch (Exception ex) when (ex is not (FormatException or NotSupportedException) && IsFormatError(ex))
        {
            throw new FormatException("The font file is damaged.", ex);
        }
        return result;
    }

    // One damaged font of a collection is left out rather than failing the others.
    private static void TryAdd(List<FontProgram> fonts, byte[] data, int offsetTable)
    {
        try { fonts.Add(new FontProgram(data, offsetTable)); }
        catch (Exception ex) when (IsFormatError(ex)) { }
    }

    internal static bool IsFormatError(Exception ex)
        => ex is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException or NotSupportedException
            or EndOfStreamException;

    // ------------------------------------------------------------------ lookups

    public (int Offset, int Length)? Table(string tag) => _tables.TryGetValue(Tag(tag), out var t) ? t : null;

    public IEnumerable<(uint Tag, int Offset, int Length)> Tables => _tables.Select(kv => (kv.Key, kv.Value.Offset, kv.Value.Length));

    public static uint Tag(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    private int _xHeight = -1;

    /// <summary>
    /// The height of a lowercase x in font units, which font-size-adjust and the ex unit read: the OS/2 table's value, else
    /// the top of the glyph for x in a TrueType font, else half an em.
    /// </summary>
    public int XHeight
    {
        get
        {
            if (_xHeight >= 0) return _xHeight;
            int top = _os2XHeight > 0 ? _os2XHeight : GlyphTop(GlyphId('x'));
            return _xHeight = top > 0 ? top : UnitsPerEm / 2;
        }
    }

    // loca and glyf: a glyph's header holds its bounding box, whose last field is the top.
    private const int LocaFormatField = 50, GlyphHeaderLength = 10, GlyphTopField = 8;

    /// <summary>The top of a TrueType glyph's bounding box in font units; 0 for an empty glyph, a missing one or a CFF font.</summary>
    private int GlyphTop(int gid)
    {
        if (IsCff || gid <= 0 || gid >= NumGlyphs || Table("loca") is not { } loca || Table("glyf") is not { } glyf || Table("head") is not { } head) return 0;
        bool wide = Reader.U16(head.Offset + LocaFormatField) != 0;
        int step = wide ? 4 : 2;
        if ((gid + 2L) * step > loca.Length) return 0;
        long start = wide ? Reader.U32(loca.Offset + gid * 4) : Reader.U16(loca.Offset + gid * 2) * 2L;
        long end = wide ? Reader.U32(loca.Offset + gid * 4 + 4) : Reader.U16(loca.Offset + gid * 2 + 2) * 2L;
        if (end - start < GlyphHeaderLength || end > glyf.Length) return 0;
        return Reader.S16(glyf.Offset + (int)start + GlyphTopField);
    }

    /// <summary>The glyph for a Unicode code point, or 0 (.notdef) when the font has none.</summary>
    public int GlyphId(int codePoint)
    {
        var bmp = _bmp ?? BuildCharMap();
        if ((uint)codePoint < 0x10000) return bmp[codePoint];
        return _astral != null && _astral.TryGetValue(codePoint, out var g) ? g : 0;
    }

    /// <summary>
    /// This font with OpenType features such as tabular figures applied to its character map (a key from
    /// <see cref="FontFeatures.Key"/>). Returns this font when the features change nothing. Variants are cached.
    /// </summary>
    public FontProgram WithFeatures(string features)
    {
        var variants = LazyInitializer.EnsureInitialized(ref _featureVariants, () => new());
        if (variants.TryGetValue(features, out var cached)) return cached;
        // Each variant copies the character map, and the bundled fonts live as long as the process, so a server that
        // sees ever new feature settings would keep every copy; past the cap, as for instances, the font is used as is.
        if (variants.Count >= MaxInstances) return this;
        return variants.GetOrAdd(features, MakeVariant);
    }

    private FontProgram MakeVariant(string features)
    {
        int[]? map;
        try { map = FontFeatures.Substitutions(this, features); }
        catch (Exception ex) when (IsFormatError(ex)) { map = null; }
        if (map == null) return this;
        var bmp = _bmp ?? BuildCharMap();
        // The variant shares the font data and every table reader; only the character map and widths differ. Features
        // are applied after a variable font is instanced, so a variant never makes instances of its own.
        var variant = (FontProgram)MemberwiseClone();
        var mapped = new ushort[bmp.Length];
        for (int c = 0; c < bmp.Length; c++) mapped[c] = bmp[c] < map.Length ? (ushort)map[bmp[c]] : bmp[c];
        variant._astral = _astral?.ToDictionary(kv => kv.Key, kv => kv.Value < map.Length ? (ushort)map[kv.Value] : kv.Value);
        variant._bmp = mapped;
        variant._charWidths = null;
        variant._featureVariants = null;
        return variant;
    }

    /// <summary>Advance width of a glyph in font units.</summary>
    public int Advance(int gid)
    {
        if ((uint)gid >= (uint)NumGlyphs) gid = 0;
        int metric = Math.Min(gid, _numHMetrics - 1);
        return Reader.U16(_hmtx + metric * FullMetricLength);
    }

    /// <summary>Left side bearing of a glyph in font units.</summary>
    public short LeftSideBearing(int gid)
    {
        if (gid < _numHMetrics) return Reader.S16(_hmtx + gid * FullMetricLength + BearingField);
        int offset = _numHMetrics * FullMetricLength + (gid - _numHMetrics) * BearingOnlyLength;
        return offset + BearingOnlyLength <= _hmtxLength ? Reader.S16(_hmtx + offset) : (short)0;
    }

    /// <summary>The design axes of a variable font; empty for a static font.</summary>
    public VariationAxis[] Axes
    {
        get
        {
            var axes = Volatile.Read(ref _axes);
            if (axes != null) return axes;
            try { axes = FontVariations.CanInstance(this) ? FontVariations.ReadAxes(this) : Array.Empty<VariationAxis>(); }
            catch (Exception ex) when (IsFormatError(ex)) { axes = Array.Empty<VariationAxis>(); }
            Volatile.Write(ref _axes, axes);
            return axes;
        }
    }

    /// <summary>The axis with a tag, if the font varies along it.</summary>
    public VariationAxis? Axis(uint tag)
    {
        foreach (var axis in Axes) if (axis.Tag == tag) return axis;
        return null;
    }

    /// <summary>
    /// The static instance of a variable font at the given axis values (design units, such as wght 700); axes not
    /// given keep their default. Returns this font when it is not variable, when the values are its default (except for
    /// CFF2 fonts), or when the font's variation data is damaged. Instances are cached, so documents share them.
    /// </summary>
    public FontProgram Instance(IReadOnlyDictionary<uint, float> values)
    {
        var axes = Axes;
        if (axes.Length == 0 || values.Count == 0) return this;
        try
        {
            var coords = FontVariations.Normalize(this, axes, values);
            // CFF2 outlines are converted even at the default, since few PDF readers can draw them.
            if (coords.All(c => c == 0) && Table("CFF2") == null) return this;
            var key = string.Join(",", coords.Select(c => ((int)MathF.Round(c * 16384)).ToString(CultureInfo.InvariantCulture)));
            if (_instances.TryGetValue(key, out var cached)) return cached;
            if (_instances.Count >= MaxInstances) return this;
            var instance = new FontProgram(FontVariations.Instance(this, coords, values, axes), 0);
            instance.PostScriptName = SanitizeName(PostScriptName + InstanceNameSuffix(axes, coords, values));
            return _instances.GetOrAdd(key, instance);
        }
        catch (Exception ex) when (IsFormatError(ex) || ex is InvalidOperationException)
        {
            return this;
        }
    }

    // A name of its own, so the instances of one font are told apart in the PDF.
    private static string InstanceNameSuffix(VariationAxis[] axes, float[] coords, IReadOnlyDictionary<uint, float> values)
    {
        var suffix = new StringBuilder();
        for (int i = 0; i < axes.Length; i++)
        {
            if (coords[i] == 0) continue;
            float value = values.TryGetValue(axes[i].Tag, out var given) ? Math.Clamp(given, axes[i].Min, axes[i].Max) : axes[i].Default;
            suffix.Append('-').Append(axes[i].Name.Trim()).Append(MathF.Round(value, 1).ToString(CultureInfo.InvariantCulture));
        }
        return suffix.ToString();
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

    /// <summary>The font's OpenType layout tables for complex shaping, or null when it has neither GSUB nor GPOS.</summary>
    public OpenTypeLayout? Layout
    {
        get
        {
            if (Volatile.Read(ref _layoutLoaded)) return _layout;
            lock (_lock)
            {
                if (!_layoutLoaded)
                {
                    try
                    {
                        var layout = new OpenTypeLayout(this);
                        _layout = layout.HasGsub || layout.HasGpos ? layout : null;
                    }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
                    {
                        _layout = null;
                    }
                    Volatile.Write(ref _layoutLoaded, true);
                }
                return _layout;
            }
        }
    }

    /// <summary>The kerning between two glyphs in 1/1000 em (usually negative), added to the first glyph's advance.</summary>
    public int Kern1000(int left, int right)
    {
        var kerning = Kerning;
        if (kerning == null) return 0;
        int value = kerning.Pair(left, right);
        return value == 0 ? 0 : (int)Math.Round(value * 1000.0 / UnitsPerEm);
    }

    // ------------------------------------------------------------------ binary helpers

    // A reader is made where it is used rather than kept, so the font object stays as small as before.
    private TableReader Reader => new(Data);

    internal int U16(int offset) => Reader.U16(offset);
    internal short S16(int offset) => Reader.S16(offset);
    internal uint U32(int offset) => Reader.U32(offset);
}

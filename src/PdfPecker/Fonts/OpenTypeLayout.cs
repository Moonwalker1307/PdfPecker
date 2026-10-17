namespace PdfPecker.Fonts;

/// <summary>
/// A glyph being shaped by <see cref="ComplexShaper"/>: its glyph id, the characters it stands for, the features that
/// apply to it, and its position. Positions are in font units, as HarfBuzz keeps them.
/// </summary>
internal struct GlyphInfo
{
    public int Gid;
    public int Cluster, Length;   // the characters in the source string; Length 0 for the extra glyphs of a decomposition
    public int CodePoint;         // the character it was made from (the first one for a ligature)
    public uint Mask;             // the features that apply to it, one bit each
    public int Props;             // OpenTypeLayout.Base/Ligature/Mark, with the mark attachment class in bits 8-15
    public int LigId, LigComp, LigComps; // ligature tracking: marks remember the component they follow
    public int XAdvance, YAdvance, XOffset, YOffset;
    public int AttachChain;       // relative index of the glyph this one is attached to (0: none)
    public byte AttachType;       // OpenTypeLayout.AttachMark or AttachCursive
}

/// <summary>
/// The GSUB, GPOS and GDEF tables of a font, and the code that applies their lookups to a run of glyphs: all GSUB lookup
/// types (alternates take the first alternate) and all GPOS lookup types, with lookup flags, mark attachment classes
/// and mark filtering sets. Device tables and variations are not applied. The lists read to plan the shaping are
/// checked when the font is loaded; malformed subtables throw IndexOutOfRangeException while a lookup is applied, which
/// the shaper turns into unshaped text.
/// </summary>
/// <remarks>
/// The class is split by concern: this file opens the tables and classifies glyphs with GDEF, Lists has the script,
/// feature and lookup lists, Lookups runs a lookup over the glyphs with its flags, and Gsub, Gpos, Attachment and
/// Contextual hold the lookup types. Every offset is absolute in the font data, which the font may not be trusted to
/// keep inside: reads past its end throw, and reads past a table's end go on into the bytes after it, as they always
/// have, so a damaged font shapes exactly as it did.
/// </remarks>
internal sealed partial class OpenTypeLayout
{
    public const int Base = 2, Ligature = 4, Mark = 8; // glyph props, as LookupFlag's ignore bits
    public const byte AttachMark = 1, AttachCursive = 2;

    // GSUB and GPOS start with a version and the offsets of the script, feature and lookup lists.
    private const int SupportedMajorVersion = 1;
    private const int LayoutHeaderLength = 10;

    // GDEF: a version, the glyph class definitions, two tables this shaper does not use, the mark attachment class
    // definitions, and from version 1.2 the mark glyph sets.
    private const int GdefHeaderLength = 12, GdefMarkGlyphSetsHeaderLength = 14;
    private const int GdefMinorVersionField = 2, GlyphClassDefField = 4, MarkAttachClassDefField = 10, MarkGlyphSetsDefField = 12;
    private const int MarkGlyphSetsMinorVersion = 2;

    // GDEF glyph classes; component glyphs (class 4) and unclassified ones have no props.
    private const int BaseGlyphClass = 1, LigatureGlyphClass = 2, MarkGlyphClass = 3;

    // A mark's props carry its mark attachment class above the glyph class bits, where LookupFlag keeps the class
    // a lookup is limited to.
    private const int MarkAttachClassShift = 8;

    // Class definition tables: an array of classes from a start glyph (format 1), or ranges of glyphs (format 2).
    private const int ClassArrayFormat = 1, ClassRangeFormat = 2;
    private const int ClassArrayHeaderLength = 6, ClassArrayCountField = 4, ClassRangeCountField = 2, ClassRangesField = 4;
    private const int ClassDefMinimumLength = 4, ClassRangeRecordLength = 6;

    private readonly TableReader _reader;
    private readonly int _gsub = -1, _gpos = -1;
    private readonly int _glyphClassDef = -1, _markAttachClassDef = -1, _markGlyphSets = -1;

    public OpenTypeLayout(FontProgram font)
    {
        _reader = new TableReader(font.Data);
        // The script, feature and lookup lists and the glyph class definitions are read outside the lookups' own
        // error handling (to plan the shaping and classify every glyph), so they are checked once here: a table
        // whose lists run past the end of the font is ignored, and the text is shaped without it.
        _gsub = OpenLayoutTable(font.Table("GSUB"));
        _gpos = OpenLayoutTable(font.Table("GPOS"));
        if (font.Table("GDEF") is { Length: >= GdefHeaderLength } gdef && _reader.U16(gdef.Offset) == SupportedMajorVersion)
        {
            int table = gdef.Offset;
            _glyphClassDef = CheckedClassDef(table, GlyphClassDefField);
            _markAttachClassDef = CheckedClassDef(table, MarkAttachClassDefField);
            if (_reader.U16(table + GdefMinorVersionField) >= MarkGlyphSetsMinorVersion && gdef.Length >= GdefMarkGlyphSetsHeaderLength
                && _reader.U16(table + MarkGlyphSetsDefField) is int markGlyphSets and not 0)
                _markGlyphSets = table + markGlyphSets;
        }
    }

    public bool HasGsub => _gsub >= 0;
    public bool HasGpos => _gpos >= 0;
    public bool HasGlyphClasses => _glyphClassDef >= 0;

    /// <summary>The glyph props of a glyph from GDEF: base, ligature or mark (with its attachment class), or 0.</summary>
    public int GlyphProps(int gid)
    {
        if (_glyphClassDef < 0) return 0;
        return ClassOf(_glyphClassDef, gid) switch
        {
            BaseGlyphClass => Base,
            LigatureGlyphClass => Ligature,
            MarkGlyphClass => Mark | (_markAttachClassDef >= 0 ? ClassOf(_markAttachClassDef, gid) << MarkAttachClassShift : 0),
            _ => 0,
        };
    }

    /// <summary>The offset of a GSUB or GPOS table whose lists are inside the font, or -1.</summary>
    private int OpenLayoutTable((int Offset, int Length)? table)
        => table is { Length: >= LayoutHeaderLength } t && _reader.U16(t.Offset) == SupportedMajorVersion && ValidLists(t.Offset) ? t.Offset : -1;

    /// <summary>The class definitions a GDEF field points to, or -1 when it has none or they run past the font.</summary>
    private int CheckedClassDef(int gdef, int field)
    {
        int offset = _reader.U16(gdef + field);
        return offset != 0 && ValidClassDef(gdef + offset) ? gdef + offset : -1;
    }

    /// <summary>Whether a class definition table, which <see cref="GlyphProps"/> reads for every glyph, is inside the font.</summary>
    private bool ValidClassDef(int classDef)
    {
        if (!Fits(classDef, ClassDefMinimumLength)) return false;
        return _reader.U16(classDef) switch
        {
            ClassArrayFormat => Fits(classDef, ClassArrayHeaderLength)
                && Fits(classDef + ClassArrayHeaderLength, _reader.U16(classDef + ClassArrayCountField) * 2),
            ClassRangeFormat => Fits(classDef + ClassRangesField, _reader.U16(classDef + ClassRangeCountField) * ClassRangeRecordLength),
            _ => true, // an unknown format classifies nothing and reads no further
        };
    }

    /// <summary>Whether <paramref name="length"/> bytes at <paramref name="offset"/> are inside the font data.</summary>
    private bool Fits(int offset, int length) => offset >= 0 && (long)offset + length <= _reader.Data.Length;

    /// <summary>The class of a glyph in a class definition table, 0 for glyphs it does not list.</summary>
    private int ClassOf(int classDef, int glyph) => new ClassDefTable(_reader, classDef).ClassOf(glyph);
}

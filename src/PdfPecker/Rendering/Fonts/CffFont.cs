using System.Text;
using PdfPecker.Fonts.Cff;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// Parser for CFF (Compact Font Format) fonts, name- or CID-keyed, with a Type 2 charstring interpreter. The INDEX,
/// DICT and number formats come from <see cref="PdfPecker.Fonts.Cff"/>; this class reads the font-level structures
/// (charset, encoding, FDArray and FDSelect) and turns charstrings into outlines.
/// </summary>
internal sealed partial class CffFont : IGlyphSource
{
    private const int MinimumLength = 4;
    private const int MajorVersionField = 0, HeaderSizeField = 2;
    private const byte SupportedMajorVersion = 1; // CFF2 is not supported

    // Top DICT operators.
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18;
    private const int OpFontMatrix = CffDict.Escape + 7, OpRos = CffDict.Escape + 30, OpFdArray = CffDict.Escape + 36, OpFdSelect = CffDict.Escape + 37;

    // Private DICT operators.
    private const int OpSubrs = 19, OpDefaultWidthX = 20, OpNominalWidthX = 21;

    // The predefined charsets and encodings that an offset below the data stands for.
    private const int IsoAdobeCharset = 0, LastPredefinedCharset = 2;
    private const int StandardEncoding = 0, ExpertEncoding = 1;

    private const int CharsetFormat0 = 0, CharsetFormat1 = 1;
    private const int EncodingFormatMask = 0x7F, EncodingHasSupplements = 0x80;
    private const int FdSelectFormat0 = 0, FdSelectFormat3 = 3;

    private readonly byte[] _data;
    private CffIndex _charStrings;
    private CffIndex _globalSubrs;
    private PrivateValues _private;
    private readonly List<FontDict> _fontDicts = new();
    private byte[]? _fdSelect;
    private string[] _strings = Array.Empty<string>();
    private int[] _charset = Array.Empty<int>(); // gid -> SID (or CID)
    private Dictionary<string, int>? _nameToGid;
    private Dictionary<int, int>? _cidToGid;
    private PathData?[] _cache = Array.Empty<PathData?>();
    private double[] _widths = Array.Empty<double>();

    /// <summary>What a Private DICT gives the charstring interpreter.</summary>
    private readonly record struct PrivateValues(CffIndex Subrs, double DefaultWidth, double NominalWidth);

    /// <summary>One font of a CID-keyed font's FDArray.</summary>
    private readonly record struct FontDict(PrivateValues Private, Matrix? FontMatrix);

    public bool IsCid { get; private set; }
    public Matrix FontMatrix { get; private set; } = Matrix.Scale(0.001, 0.001);
    public int GlyphCount => _charStrings.Count;

    /// <summary>The font's built-in encoding: code to glyph id (0 when unmapped).</summary>
    public int[] BuiltinEncoding { get; private set; } = new int[256];

    private CffFont(byte[] data) => _data = data;

    public static CffFont? Parse(byte[] data)
    {
        try
        {
            var font = new CffFont(data);
            return font.Load() ? font : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private bool Load()
    {
        var data = _data;
        if (data.Length < MinimumLength || data[MajorVersionField] != SupportedMajorVersion) return false;
        int pos = data[HeaderSizeField];
        CffIndex.Read(data, ref pos); // the Name INDEX
        var topDicts = CffIndex.Read(data, ref pos);
        var strings = CffIndex.Read(data, ref pos);
        _globalSubrs = CffIndex.Read(data, ref pos);
        if (topDicts.Count < 1) return false;

        _strings = new string[strings.Count];
        for (int i = 0; i < _strings.Length; i++)
        {
            var (start, end) = strings.Range(i);
            _strings[i] = Encoding.Latin1.GetString(data, start, end - start);
        }

        var (topStart, topEnd) = topDicts.Range(0);
        var top = CffDict.Parse(data, topStart, topEnd);
        if (!top.TryGetValue(OpCharStrings, out var charStrings) || charStrings.Length < 1) return false;
        int charStringsPos = (int)charStrings[0];
        _charStrings = CffIndex.Read(data, ref charStringsPos);
        int glyphCount = _charStrings.Count;
        if (glyphCount <= 0) return false;
        _cache = new PathData?[glyphCount];
        _widths = new double[glyphCount];
        Array.Fill(_widths, double.NaN);

        if (MatrixOf(top) is { } matrix) FontMatrix = matrix;
        IsCid = top.ContainsKey(OpRos);
        _private = ReadPrivate(top);
        if (IsCid) LoadCidStructures(top, glyphCount);

        _charset = new int[glyphCount];
        int charsetOffset = Operand(top, OpCharset);
        if (charsetOffset == IsoAdobeCharset)
        {
            for (int gid = 0; gid < glyphCount; gid++) _charset[gid] = gid; // ISOAdobe: SID = gid
        }
        else if (charsetOffset > LastPredefinedCharset)
        {
            ReadCharset(charsetOffset, glyphCount);
        }

        if (!IsCid)
        {
            int encodingOffset = Operand(top, OpEncoding);
            if (encodingOffset is StandardEncoding or ExpertEncoding) UseStandardEncoding();
            else ReadEncoding(encodingOffset);
        }
        return true;
    }

    /// <summary>The first operand of an operator, or 0 when the DICT has none.</summary>
    private static int Operand(Dictionary<int, double[]> dict, int op)
        => dict.TryGetValue(op, out var operands) && operands.Length > 0 ? (int)operands[0] : 0;

    private static Matrix? MatrixOf(Dictionary<int, double[]> dict)
        => dict.TryGetValue(OpFontMatrix, out var m) && m.Length >= 6 ? new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]) : null;

    private void LoadCidStructures(Dictionary<int, double[]> top, int glyphCount)
    {
        if (top.TryGetValue(OpFdArray, out var fdArray) && fdArray.Length >= 1)
        {
            int pos = (int)fdArray[0];
            var fontDicts = CffIndex.Read(_data, ref pos);
            for (int i = 0; i < fontDicts.Count; i++)
            {
                var (start, end) = fontDicts.Range(i);
                var fontDict = CffDict.Parse(_data, start, end);
                _fontDicts.Add(new FontDict(ReadPrivate(fontDict), MatrixOf(fontDict)));
            }
            // A font-dict matrix scales glyph space; the top matrix then defaults to identity.
            if (_fontDicts.Count > 0 && _fontDicts[0].FontMatrix is { } fdMatrix)
                FontMatrix = top.ContainsKey(OpFontMatrix) ? fdMatrix.Multiply(FontMatrix) : fdMatrix;
        }
        if (top.TryGetValue(OpFdSelect, out var fdSelect) && fdSelect.Length >= 1) ReadFdSelect((int)fdSelect[0], glyphCount);
    }

    /// <summary>The Private DICT a Top or Font DICT points at (size, then offset), with its local subroutines.</summary>
    private PrivateValues ReadPrivate(Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(OpPrivate, out var operands) || operands.Length < 2) return default;
        int size = (int)operands[0], offset = (int)operands[1];
        if (offset <= 0 || offset + size > _data.Length) return default;
        var values = CffDict.Parse(_data, offset, offset + size);
        double defaultWidth = 0, nominalWidth = 0;
        if (values.TryGetValue(OpDefaultWidthX, out var dw) && dw.Length > 0) defaultWidth = dw[0];
        if (values.TryGetValue(OpNominalWidthX, out var nw) && nw.Length > 0) nominalWidth = nw[0];
        CffIndex subrs = default;
        if (values.TryGetValue(OpSubrs, out var so) && so.Length > 0)
        {
            // The Subrs offset is relative to the Private DICT.
            int subrsPos = offset + (int)so[0];
            if (subrsPos > 0 && subrsPos < _data.Length) subrs = CffIndex.Read(_data, ref subrsPos);
        }
        return new PrivateValues(subrs, defaultWidth, nominalWidth);
    }

    // Which font dict each glyph uses. An unknown format leaves every glyph on the first dict.
    private void ReadFdSelect(int at, int glyphCount)
    {
        var data = _data;
        var selectors = new byte[glyphCount];
        int format = data[at];
        if (format == FdSelectFormat0)
        {
            for (int gid = 0; gid < glyphCount; gid++) selectors[gid] = data[at + 1 + gid];
        }
        else if (format == FdSelectFormat3)
        {
            int rangeCount = data[at + 1] << 8 | data[at + 2];
            int p = at + 3;
            for (int range = 0; range < rangeCount; range++)
            {
                // Each range is a first glyph and a dict; the next range's first glyph (or the sentinel) ends it.
                int first = data[p] << 8 | data[p + 1];
                byte fd = data[p + 2];
                int next = data[p + 3] << 8 | data[p + 4];
                for (int gid = first; gid < next && gid < glyphCount; gid++) selectors[gid] = fd;
                p += 3;
            }
        }
        _fdSelect = selectors;
    }

    // The SID (or CID) of every glyph but .notdef, as a list (format 0) or as ranges (formats 1 and 2).
    private void ReadCharset(int at, int glyphCount)
    {
        var data = _data;
        int format = data[at];
        int p = at + 1;
        int gid = 1;
        if (format == CharsetFormat0)
        {
            for (; gid < glyphCount; gid++, p += 2) _charset[gid] = data[p] << 8 | data[p + 1];
            return;
        }
        while (gid < glyphCount)
        {
            int first = data[p] << 8 | data[p + 1];
            int left = format == CharsetFormat1 ? data[p + 2] : data[p + 2] << 8 | data[p + 3];
            p += format == CharsetFormat1 ? 3 : 4;
            for (int k = 0; k <= left && gid < glyphCount; k++) _charset[gid++] = first + k;
        }
    }

    // The standard and expert encodings both map through the standard encoding's glyph names here.
    private void UseStandardEncoding()
    {
        for (int code = 0; code < 256; code++)
        {
            if (Encodings.Standard[code] is { } name) BuiltinEncoding[code] = Math.Max(0, GlyphByName(name));
        }
    }

    // A custom encoding: codes by glyph (format 0) or as ranges (format 1), plus supplements that map codes by SID.
    private void ReadEncoding(int at)
    {
        var data = _data;
        int format = data[at];
        int p = at + 1;
        if ((format & EncodingFormatMask) == 0)
        {
            int codeCount = data[p++];
            for (int gid = 1; gid <= codeCount; gid++) BuiltinEncoding[data[p++]] = gid;
        }
        else
        {
            int rangeCount = data[p++];
            int gid = 1;
            for (int range = 0; range < rangeCount; range++)
            {
                int first = data[p], left = data[p + 1];
                p += 2;
                for (int k = 0; k <= left; k++)
                {
                    if (first + k < 256) BuiltinEncoding[first + k] = gid++;
                }
            }
        }
        if ((format & EncodingHasSupplements) != 0)
        {
            int supplementCount = data[p++];
            for (int i = 0; i < supplementCount; i++)
            {
                int code = data[p];
                int sid = data[p + 1] << 8 | data[p + 2];
                p += 3;
                int gid = Array.IndexOf(_charset, sid);
                if (gid > 0) BuiltinEncoding[code] = gid;
            }
        }
    }

    // ------------------------------------------------------------------ lookups

    public string? GlyphName(int gid) => IsCid || gid < 0 || gid >= _charset.Length ? null : StringOf(_charset[gid]);

    private string? StringOf(int sid)
    {
        if (sid < StandardStrings.Length) return StandardStrings[sid];
        sid -= StandardStrings.Length;
        return sid < _strings.Length ? _strings[sid] : null;
    }

    public int GlyphByName(string name)
    {
        if (IsCid) return -1;
        if (_nameToGid == null)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int gid = 0; gid < _charset.Length; gid++)
            {
                if (StringOf(_charset[gid]) is { } glyphName) map.TryAdd(glyphName, gid);
            }
            _nameToGid = map;
        }
        return _nameToGid.TryGetValue(name, out int found) ? found : -1;
    }

    public int GidForCid(int cid)
    {
        if (!IsCid) return cid;
        if (_cidToGid == null)
        {
            var map = new Dictionary<int, int>();
            for (int gid = 0; gid < _charset.Length; gid++) map.TryAdd(_charset[gid], gid);
            _cidToGid = map;
        }
        return _cidToGid.TryGetValue(cid, out int found) ? found : 0;
    }

    public double Advance(int gid)
    {
        if (gid < 0 || gid >= _widths.Length) return -1;
        if (double.IsNaN(_widths[gid])) GetGlyph(gid);
        return _widths[gid];
    }

    // ------------------------------------------------------------------ outlines

    public PathData? GetGlyph(int gid)
    {
        if (gid < 0 || gid >= _cache.Length) return null;
        var cached = Volatile.Read(ref _cache[gid]);
        if (cached != null) return cached;
        var glyph = Build(gid, seacComponents: true);
        var path = glyph.Finish();
        _widths[gid] = glyph.Width;
        Volatile.Write(ref _cache[gid], path);
        return path;
    }

    /// <summary>
    /// The outline of a seac component. The spec allows only one level of seac, so the component's own seac is not
    /// followed: that is what stops a glyph whose components lead back to it from recursing until the stack
    /// overflows. The result is not cached, so a glyph looks the same whether it was drawn alone or as a component.
    /// </summary>
    private PathData? SeacComponent(int gid)
        => gid < 0 || gid >= _cache.Length ? null : Build(gid, seacComponents: false).Finish();

    private Type2Glyph Build(int gid, bool seacComponents)
    {
        var glyph = new Type2Glyph(this, PrivateFor(gid), gid, seacComponents);
        try
        {
            var (start, end) = _charStrings.Range(gid);
            glyph.Run(start, end, 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            // A damaged charstring draws what it managed to before the damage.
        }
        return glyph;
    }

    // A CID-keyed font selects a glyph's subroutines and widths through FDSelect; other glyphs use the top-level ones.
    private PrivateValues PrivateFor(int gid)
    {
        if (IsCid && _fdSelect != null && gid < _fdSelect.Length && _fdSelect[gid] < _fontDicts.Count) return _fontDicts[_fdSelect[gid]].Private;
        return _private;
    }
}

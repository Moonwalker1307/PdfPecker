using System.Text;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// Parser for embedded Type 1 fonts (FontFile): the cleartext part for the encoding and matrix, eexec decryption of
/// the private part, and the Type 1 charstring interpreter. Fonts in PDFs are often damaged or oddly wrapped, so the
/// private part is scanned for its keywords rather than parsed as PostScript.
/// </summary>
internal sealed partial class Type1Font : IGlyphSource
{
    // The eexec and charstring ciphers.
    private const int EexecKey = 55665, CharstringKey = 4330;
    private const int CipherC1 = 52845, CipherC2 = 22719;
    private const int EexecLeadingBytes = 4, DefaultLenIV = 4;

    // PFB files wrap the font in segments: a marker byte, a type (1 text, 2 binary) and a little-endian length.
    private const byte PfbMarker = 0x80, PfbTextSegment = 1, PfbBinarySegment = 2;
    private const int PfbHeaderSize = 6;

    private static ReadOnlySpan<byte> EexecKeyword => "eexec"u8;

    /// <summary>A Length1 that puts the cleartext end further than this past the eexec keyword is not trusted.</summary>
    private const int Length1Tolerance = 64;

    private const int HexProbeLength = 4;
    private const int MaxSubrs = 65536;

    private readonly List<byte[]> _charStrings = new();
    private readonly List<byte[]> _subrs = new();
    private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
    private readonly List<string> _glyphNames = new();
    private PathData?[] _cache = Array.Empty<PathData?>();
    private double[] _widths = Array.Empty<double>();
    private int _lenIV = DefaultLenIV;

    public Matrix FontMatrix { get; private set; } = Matrix.Scale(0.001, 0.001);
    public int GlyphCount => _charStrings.Count;

    /// <summary>The font's built-in encoding as glyph names (null when unset).</summary>
    public string?[] BuiltinEncoding { get; } = new string?[256];

    public static Type1Font? Parse(byte[] data, int length1)
    {
        try
        {
            var font = new Type1Font();
            return font.Load(data, length1) ? font : null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or FormatException)
        {
            SwallowedErrors.Record(ex);
            return null;
        }
    }

    public int GlyphByName(string name) => _names.TryGetValue(name, out int gid) ? gid : -1;

    public string? GlyphName(int gid) => gid >= 0 && gid < _glyphNames.Count ? _glyphNames[gid] : null;

    public double Advance(int gid)
    {
        if (gid < 0 || gid >= _widths.Length) return -1;
        if (double.IsNaN(_widths[gid])) GetGlyph(gid);
        return _widths[gid];
    }

    private bool Load(byte[] data, int length1)
    {
        data = StripPfb(data, ref length1);
        int cleartextEnd = CleartextEnd(data, length1);
        ParseCleartext(data, Math.Min(cleartextEnd, data.Length));

        int start = cleartextEnd;
        while (start < data.Length && data[start] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t') start++;
        if (start >= data.Length) return false;
        var encrypted = data.AsSpan(start).ToArray();
        if (IsHex(encrypted)) encrypted = FromHex(encrypted);
        ParsePrivate(Decrypt(encrypted, EexecKey, EexecLeadingBytes));
        _cache = new PathData?[_charStrings.Count];
        _widths = new double[_charStrings.Count];
        Array.Fill(_widths, double.NaN);
        return _charStrings.Count > 0;
    }

    // Where the encrypted part begins: Length1 when it agrees with the eexec keyword, else just after the keyword.
    private static int CleartextEnd(byte[] data, int length1)
    {
        int eexec = IndexOf(data, EexecKeyword, 0);
        int afterKeyword = eexec + EexecKeyword.Length;
        if (length1 <= 0 || length1 > data.Length || (eexec >= 0 && afterKeyword > length1 + Length1Tolerance))
            length1 = eexec >= 0 ? afterKeyword : data.Length;
        if (eexec >= 0 && afterKeyword <= data.Length && length1 < afterKeyword) length1 = afterKeyword;
        return length1;
    }

    // Joins the segments of a PFB file; the first (text) segment's length becomes Length1.
    private static byte[] StripPfb(byte[] data, ref int length1)
    {
        if (data.Length < PfbHeaderSize || data[0] != PfbMarker || data[1] != PfbTextSegment) return data;
        var joined = new MemoryStream(data.Length);
        int p = 0, segment = 0;
        while (p + PfbHeaderSize <= data.Length && data[p] == PfbMarker && data[p + 1] is PfbTextSegment or PfbBinarySegment)
        {
            int length = data[p + 2] | data[p + 3] << 8 | data[p + 4] << 16 | data[p + 5] << 24;
            p += PfbHeaderSize;
            length = Math.Min(length, data.Length - p);
            joined.Write(data, p, length);
            if (segment == 0) length1 = length;
            p += length;
            segment++;
        }
        return joined.ToArray();
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> keyword, int from)
    {
        int i = data.AsSpan(from).IndexOf(keyword);
        return i < 0 ? -1 : i + from;
    }

    // The encrypted part is binary or hexadecimal; the first few bytes tell which.
    private static bool IsHex(byte[] data)
    {
        int probe = Math.Min(HexProbeLength, data.Length);
        for (int i = 0; i < probe; i++)
        {
            if (PdfLexer.HexVal(data[i]) < 0) return false;
        }
        return true;
    }

    // Hex digits in pairs, ignoring everything else; a trailing unpaired digit is dropped.
    private static byte[] FromHex(byte[] data)
    {
        var bytes = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            int value = PdfLexer.HexVal(b);
            if (value < 0) continue;
            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes.Add((byte)(high << 4 | value));
                high = -1;
            }
        }
        return bytes.ToArray();
    }

    /// <summary>The Type 1 cipher; the first <paramref name="skip"/> plaintext bytes are random padding and dropped.</summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> data, int key, int skip)
    {
        int r = key;
        var plain = new byte[Math.Max(0, data.Length - skip)];
        for (int i = 0; i < data.Length; i++)
        {
            int c = data[i];
            int p = c ^ (r >> 8);
            r = ((c + r) * CipherC1 + CipherC2) & 0xFFFF;
            if (i >= skip) plain[i - skip] = (byte)p;
        }
        return plain;
    }

    // ------------------------------------------------------------------ the cleartext part

    private void ParseCleartext(byte[] data, int end)
    {
        var lexer = new PdfLexer(data, 0, end);
        while (true)
        {
            var kind = lexer.Next();
            if (kind == TokenKind.Eof) break;
            if (kind != TokenKind.Name) continue;
            if (lexer.NameValue == "FontMatrix") ReadFontMatrix(lexer);
            else if (lexer.NameValue == "Encoding") ReadEncoding(lexer);
        }
    }

    private void ReadFontMatrix(PdfLexer lexer)
    {
        if (lexer.Next() is not (TokenKind.ArrayStart or TokenKind.BraceOpen)) return;
        var m = new double[6];
        int count = 0;
        while (count < 6 && lexer.Next() is TokenKind.Integer or TokenKind.Real) m[count++] = lexer.Number;
        if (count == 6 && m[0] != 0) FontMatrix = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
    }

    // "/Encoding StandardEncoding def", or an array filled with "dup <code> /<name> put" entries up to "readonly def".
    private void ReadEncoding(PdfLexer lexer)
    {
        int save = lexer.Pos;
        if (lexer.Next() == TokenKind.Keyword && lexer.KeywordIs("StandardEncoding"))
        {
            Array.Copy(Encodings.Standard, BuiltinEncoding, 256);
            return;
        }
        lexer.Pos = save;
        while (true)
        {
            var kind = lexer.Next();
            if (kind == TokenKind.Eof) break;
            if (kind == TokenKind.Keyword && (lexer.KeywordIs("def") || lexer.KeywordIs("readonly"))) break;
            if (kind != TokenKind.Keyword || !lexer.KeywordIs("dup")) continue;
            if (lexer.Next() != TokenKind.Integer) continue;
            int code = (int)lexer.Integer;
            if (lexer.Next() != TokenKind.Name) continue;
            if (code >= 0 && code < 256) BuiltinEncoding[code] = lexer.NameValue;
        }
    }

    // ------------------------------------------------------------------ the private part

    private static ReadOnlySpan<byte> LenIVKeyword => "/lenIV"u8;
    private static ReadOnlySpan<byte> SubrsKeyword => "/Subrs"u8;
    private static ReadOnlySpan<byte> CharStringsKeyword => "/CharStrings"u8;
    private static ReadOnlySpan<byte> DupKeyword => "dup"u8;

    private void ParsePrivate(byte[] text)
    {
        int lenIV = IndexOf(text, LenIVKeyword, 0);
        if (lenIV >= 0)
        {
            var lexer = new PdfLexer(text, lenIV + LenIVKeyword.Length);
            if (lexer.Next() == TokenKind.Integer) _lenIV = (int)lexer.Integer;
        }
        int subrs = IndexOf(text, SubrsKeyword, 0);
        int charStrings = IndexOf(text, CharStringsKeyword, 0);
        if (subrs >= 0 && (charStrings < 0 || subrs < charStrings)) ReadSubrs(text, subrs + SubrsKeyword.Length, charStrings);
        if (charStrings >= 0) ReadCharStrings(text, charStrings + CharStringsKeyword.Length);
    }

    // "/Subrs <count> array", then "dup <index> <length> RD <bytes> NP" entries, which must come before /CharStrings.
    private void ReadSubrs(byte[] text, int position, int charStrings)
    {
        var scanner = new PrivateScanner(text, position);
        int count = scanner.ReadInteger();
        var subrs = new byte[Math.Clamp(count, 0, MaxSubrs)][];
        for (int i = 0; i < subrs.Length; i++)
        {
            int dup = IndexOf(text, DupKeyword, scanner.Position);
            if (dup < 0 || (charStrings >= 0 && dup > charStrings)) break;
            scanner.Position = dup + DupKeyword.Length;
            int index = scanner.ReadInteger();
            if (!scanner.TryReadCharstring(out var charstring)) break;
            if (index >= 0 && index < subrs.Length) subrs[index] = DecryptCharstring(charstring);
        }
        foreach (var subr in subrs) _subrs.Add(subr ?? Array.Empty<byte>());
    }

    // "/CharStrings <count> dict dup begin", then "/<name> <length> RD <bytes> ND" entries; a repeated name keeps the first.
    private void ReadCharStrings(byte[] text, int position)
    {
        var scanner = new PrivateScanner(text, position);
        int count = scanner.ReadInteger();
        for (int i = 0; i < count && scanner.Position < text.Length; i++)
        {
            if (!scanner.TryReadName(out var name)) break;
            if (!scanner.TryReadCharstring(out var charstring)) break;
            if (_names.ContainsKey(name)) continue;
            _names[name] = _charStrings.Count;
            _glyphNames.Add(name);
            _charStrings.Add(DecryptCharstring(charstring));
        }
    }

    private byte[] DecryptCharstring(ReadOnlySpan<byte> encrypted) => Decrypt(encrypted, CharstringKey, Math.Max(0, _lenIV));

    /// <summary>
    /// Reads the numbers, names and binary entries of the decrypted private part. It is a scanner, not a lexer,
    /// because the binary charstrings in between would derail a PostScript tokenizer.
    /// </summary>
    private ref struct PrivateScanner(byte[] text, int position)
    {
        public int Position = position;

        /// <summary>The next integer, skipping anything before it; 0 when there are no digits.</summary>
        public int ReadInteger()
        {
            while (Position < text.Length && !IsDigit(text[Position]) && text[Position] != '-') Position++;
            bool negative = Position < text.Length && text[Position] == '-';
            if (negative) Position++;
            int value = 0;
            while (Position < text.Length && IsDigit(text[Position])) value = value * 10 + (text[Position++] - '0');
            return negative ? -value : value;
        }

        /// <summary>The next "/name": its regular characters, up to the first delimiter or white space.</summary>
        public bool TryReadName(out string name)
        {
            int slash = Array.IndexOf(text, (byte)'/', Position);
            if (slash < 0)
            {
                name = "";
                return false;
            }
            int start = slash + 1, end = start;
            while (end < text.Length && PdfLexer.IsRegular(text[end])) end++;
            name = Encoding.Latin1.GetString(text, start, end - start);
            Position = end;
            return true;
        }

        /// <summary>A binary entry: its length, the RD (or -|) operator, one space, then the bytes.</summary>
        public bool TryReadCharstring(out ReadOnlySpan<byte> bytes)
        {
            int length = ReadInteger();
            if (!SkipToken() || length < 0 || Position + 1 + length > text.Length)
            {
                bytes = default;
                return false;
            }
            Position++; // the single space after RD
            bytes = text.AsSpan(Position, length);
            Position += length;
            return true;
        }

        // Skips white space and one token; false when there is no token or it runs to the end.
        private bool SkipToken()
        {
            while (Position < text.Length && PdfLexer.IsWhite(text[Position])) Position++;
            int start = Position;
            while (Position < text.Length && !PdfLexer.IsWhite(text[Position])) Position++;
            return Position > start && Position < text.Length;
        }

        private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
    }
}

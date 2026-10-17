using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// A character map: splits byte strings into codes using codespace ranges, and maps codes to CIDs (encoding CMaps)
/// or to Unicode (ToUnicode CMaps).
/// </summary>
internal sealed class CMap
{
    /// <summary>A code has at most four bytes; longer strings in a CMap keep their first four.</summary>
    private const int MaxCodeBytes = 4;

    /// <summary>Without codespace ranges, codes are two bytes.</summary>
    private const int DefaultCodeBytes = 2;

    /// <summary>A bfrange longer than this is read as its first code only, so a damaged range cannot fill memory.</summary>
    private const uint MaxBfRangeLength = 65535;

    private readonly List<CodespaceRange> _codespace = new();
    private readonly Dictionary<uint, int> _single = new();
    private readonly List<CidRange> _ranges = new();
    private readonly Dictionary<uint, string> _unicode = new();

    private readonly record struct CodespaceRange(int Bytes, uint Low, uint High);

    private readonly record struct CidRange(uint Low, uint High, int FirstCid);

    public bool IsIdentity { get; private set; }
    public bool Vertical { get; private set; }

    public static CMap Identity(bool vertical) => new() { IsIdentity = true, Vertical = vertical };

    /// <summary>Reads the next code from <paramref name="s"/>.</summary>
    public uint NextCode(ReadOnlySpan<byte> s, ref int pos, out int length)
    {
        if (_codespace.Count == 0)
        {
            length = Math.Min(DefaultCodeBytes, s.Length - pos);
            uint code = Code(s, pos, length);
            pos += length;
            return code;
        }
        uint candidate = 0;
        for (int n = 1; n <= MaxCodeBytes && pos + n <= s.Length; n++)
        {
            candidate = candidate << 8 | s[pos + n - 1];
            foreach (var range in _codespace)
            {
                if (range.Bytes == n && candidate >= range.Low && candidate <= range.High)
                {
                    pos += n;
                    length = n;
                    return candidate;
                }
            }
        }
        // No codespace matched: consume the shortest code length (the ranges are sorted by it), or what is left.
        int shortest = Math.Min(_codespace[0].Bytes, s.Length - pos);
        uint partial = Code(s, pos, shortest);
        length = Math.Max(1, shortest);
        pos += length;
        return partial;
    }

    // The count bytes at pos as a big-endian code; a count of zero or less gives 0.
    private static uint Code(ReadOnlySpan<byte> s, int pos, int count)
    {
        uint code = 0;
        for (int i = 0; i < count; i++) code = code << 8 | s[pos + i];
        return code;
    }

    public int ToCid(uint code)
    {
        if (_single.TryGetValue(code, out int cid)) return cid;
        foreach (var range in _ranges)
        {
            if (code >= range.Low && code <= range.High) return range.FirstCid + (int)(code - range.Low);
        }
        return IsIdentity ? (int)code : 0;
    }

    public string? ToUnicode(uint code) => _unicode.TryGetValue(code, out var s) ? s : null;

    public static CMap? Parse(byte[] data, PdfFile? file = null, int depth = 0)
    {
        var map = new CMap();
        try
        {
            map.ParseInto(data);
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
            // A damaged CMap keeps the mappings read so far.
        }
        return map;
    }

    // The operands before an operator; only the last two are ever looked at (for "/WMode 1 def" and "/Name usecmap").
    private enum OperandKind : byte { None, Integer, Name, Other }

    private struct OperandWindow
    {
        public OperandKind LastKind, PreviousKind;
        public long LastInteger;
        public string? LastName, PreviousName;

        public void Push(OperandKind kind, long integer = 0, string? name = null)
        {
            PreviousKind = LastKind;
            PreviousName = LastName;
            LastKind = kind;
            LastInteger = integer;
            LastName = name;
        }

        public void Clear() => this = default;
    }

    private void ParseInto(byte[] data)
    {
        var lexer = new PdfLexer(data);
        var operands = new OperandWindow();
        while (true)
        {
            var kind = lexer.Next();
            if (kind == TokenKind.Eof) break;
            switch (kind)
            {
                case TokenKind.Integer:
                    operands.Push(OperandKind.Integer, integer: lexer.Integer);
                    break;
                case TokenKind.HexString:
                case TokenKind.String:
                    operands.Push(OperandKind.Other);
                    break;
                case TokenKind.Name:
                    operands.Push(OperandKind.Name, name: lexer.NameValue);
                    break;
                case TokenKind.ArrayStart:
                    SkipArray(lexer);
                    operands.Push(OperandKind.Other);
                    break;
                case TokenKind.Keyword:
                    Keyword(lexer, in operands);
                    operands.Clear();
                    break;
            }
        }
    }

    private static void SkipArray(PdfLexer lexer)
    {
        while (lexer.Next() is not (TokenKind.ArrayEnd or TokenKind.Eof))
        {
        }
    }

    private void Keyword(PdfLexer lexer, in OperandWindow operands)
    {
        if (lexer.KeywordIs("begincodespacerange")) ReadCodespace(lexer);
        else if (lexer.KeywordIs("begincidrange")) ReadCidRange(lexer);
        else if (lexer.KeywordIs("begincidchar")) ReadCidChar(lexer);
        else if (lexer.KeywordIs("beginbfchar")) ReadBfChar(lexer);
        else if (lexer.KeywordIs("beginbfrange")) ReadBfRange(lexer);
        else if (lexer.KeywordIs("usecmap"))
        {
            // Only the Identity parents are known; a CMap built on one maps unlisted codes to themselves.
            if (operands.LastKind == OperandKind.Name && operands.LastName!.StartsWith("Identity", StringComparison.Ordinal))
            {
                IsIdentity = true;
                Vertical = operands.LastName.EndsWith("-V", StringComparison.Ordinal);
            }
        }
        else if (lexer.KeywordIs("def"))
        {
            if (operands.PreviousKind == OperandKind.Name && operands.PreviousName == "WMode" && operands.LastKind == OperandKind.Integer)
                Vertical = operands.LastInteger == 1;
        }
    }

    // The first four bytes of a string as a big-endian code.
    private static uint Code(ReadOnlySpan<byte> bytes)
    {
        uint code = 0;
        for (int i = 0; i < Math.Min(MaxCodeBytes, bytes.Length); i++) code = code << 8 | bytes[i];
        return code;
    }

    private static bool IsString(TokenKind kind) => kind is TokenKind.HexString or TokenKind.String;

    /// <summary>Reads a string operand; false at the end of the section (any keyword) or at a token of another kind.</summary>
    private static bool NextIsString(PdfLexer lexer) => IsString(lexer.Next());

    // Each section runs to its end keyword; an entry that does not start with a string is skipped, and one that is
    // cut off ends the section.
    private void ReadCodespace(PdfLexer lexer)
    {
        while (true)
        {
            var kind = lexer.Next();
            if (kind is TokenKind.Eof or TokenKind.Keyword) break;
            if (!IsString(kind)) continue;
            var low = lexer.StringValue!;
            if (!NextIsString(lexer)) break;
            var high = lexer.StringValue!;
            _codespace.Add(new CodespaceRange(Math.Clamp(low.Length, 1, MaxCodeBytes), Code(low), Code(high)));
        }
        // NextCode tries the shortest codes first.
        _codespace.Sort((a, b) => a.Bytes.CompareTo(b.Bytes));
    }

    private void ReadCidRange(PdfLexer lexer)
    {
        while (true)
        {
            var kind = lexer.Next();
            if (kind is TokenKind.Eof or TokenKind.Keyword) break;
            if (!IsString(kind)) continue;
            var low = lexer.StringValue!;
            if (!NextIsString(lexer)) break;
            var high = lexer.StringValue!;
            if (lexer.Next() != TokenKind.Integer) break;
            uint l = Code(low), h = Code(high);
            if (l == h) _single[l] = (int)lexer.Integer;
            else _ranges.Add(new CidRange(l, h, (int)lexer.Integer));
        }
    }

    private void ReadCidChar(PdfLexer lexer)
    {
        while (true)
        {
            var kind = lexer.Next();
            if (kind is TokenKind.Eof or TokenKind.Keyword) break;
            if (!IsString(kind)) continue;
            var code = lexer.StringValue!;
            if (lexer.Next() != TokenKind.Integer) break;
            _single[Code(code)] = (int)lexer.Integer;
        }
    }

    // bfchar maps a code to a UTF-16BE string, or to a glyph name.
    private void ReadBfChar(PdfLexer lexer)
    {
        while (true)
        {
            var kind = lexer.Next();
            if (kind is TokenKind.Eof or TokenKind.Keyword) break;
            if (!IsString(kind)) continue;
            var source = lexer.StringValue!;
            var destination = lexer.Next();
            if (IsString(destination))
            {
                _unicode[Code(source)] = Utf16(lexer.StringValue!);
            }
            else if (destination == TokenKind.Name)
            {
                int u = Encodings.NameToUnicode(lexer.NameValue);
                if (u >= 0) _unicode[Code(source)] = char.ConvertFromUtf32(u);
            }
            else break;
        }
    }

    // bfrange maps a range of codes to consecutive strings (the last byte counts up) or to an array of strings.
    private void ReadBfRange(PdfLexer lexer)
    {
        while (true)
        {
            var kind = lexer.Next();
            if (kind is TokenKind.Eof or TokenKind.Keyword) break;
            if (!IsString(kind)) continue;
            var low = lexer.StringValue!;
            if (!NextIsString(lexer)) break;
            var high = lexer.StringValue!;
            uint l = Code(low), h = Code(high);
            if (h < l || h - l > MaxBfRangeLength) h = l;
            var destination = lexer.Next();
            if (IsString(destination))
            {
                var text = (byte[])lexer.StringValue!.Clone();
                for (uint code = l; code <= h; code++)
                {
                    _unicode[code] = Utf16(text);
                    Increment(text);
                }
            }
            else if (destination == TokenKind.ArrayStart)
            {
                uint code = l;
                while (true)
                {
                    var item = lexer.Next();
                    if (item is TokenKind.ArrayEnd or TokenKind.Eof) break;
                    if (IsString(item) && code <= h) _unicode[code++] = Utf16(lexer.StringValue!);
                }
            }
            else break;
        }
    }

    // Adds one to the string as a big-endian number, carrying into the earlier bytes; an overflow past the first
    // byte is dropped.
    private static void Increment(byte[] text)
    {
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (++text[i] != 0) return;
        }
    }

    private static string Utf16(byte[] b)
    {
        if (b.Length == 1) return ((char)b[0]).ToString();
        var chars = new char[b.Length / 2];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)(b[2 * i] << 8 | b[2 * i + 1]);
        return new string(chars);
    }
}

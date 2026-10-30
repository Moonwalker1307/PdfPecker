using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using PdfPecker.Pdf;

namespace PdfPecker.Rendering.Parsing;

internal enum TokenKind : byte
{
    Eof,
    Integer,
    Real,
    String,
    HexString,
    Name,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
    BraceOpen,
    BraceClose,
}

/// <summary>
/// Tokenizer and object parser for PDF syntax over a byte array. Used both for the file structure and, through
/// the content stream parser, for content streams.
/// </summary>
internal sealed class PdfLexer
{
    /// <summary>How deeply arrays and dictionaries may nest before the file is rejected, which bounds the recursion.</summary>
    private const int MaxNestingDepth = 200;

    /// <summary>The largest generation number a reference may carry, exclusive.</summary>
    private const int GenerationLimit = 65536;

    // Digits stop adding to a number at this size so that the value cannot overflow a long.
    private const long DigitAccumulationLimit = 100_000_000_000_000_000L;

    // A real keeps this many fraction digits; further ones are read but do not change the value.
    private const int MaxFractionDigits = 15;

    // A power of ten beyond this is infinity or zero for any double, so a longer exponent need not be tracked.
    private const int MaxExponent = 400;

    private const byte Backspace = 8;
    private const byte Tab = 9;
    private const byte LineFeed = 10;
    private const byte FormFeed = 12;
    private const byte CarriageReturn = 13;

    private enum CharClass : byte
    {
        Regular,
        Whitespace,
        Delimiter,
    }

    private readonly struct Whitespace : IBytePredicate
    {
        public static bool Matches(byte value) => IsWhite(value);
    }

    private readonly struct RegularCharacter : IBytePredicate
    {
        public static bool Matches(byte value) => IsRegular(value);
    }

    private static readonly CharClass[] CharClasses = BuildCharClasses();

    // A literal string needs byte-by-byte work only at one of these; up to the first one it is a plain copy.
    private static readonly SearchValues<byte> LiteralStringSpecials = SearchValues.Create("()\\\r"u8);

    private readonly NameCache names;

    public PdfLexer(byte[] data, int start = 0, int end = -1, NameCache? names = null)
    {
        Data = data;
        Pos = start;
        End = end < 0 ? data.Length : end;
        this.names = names ?? NameCache.Shared;
    }

    public byte[] Data { get; }

    public int Pos { get; set; }

    public int End { get; }

    // The last token. Which of these hold a value depends on Kind.
    public TokenKind Kind { get; private set; }
    public double Number { get; private set; }
    public long Integer { get; private set; }

    /// <summary>Where the bytes of the last keyword start in <see cref="Data"/>.</summary>
    public int TokStart { get; private set; }

    /// <summary>How many bytes the last keyword has.</summary>
    public int TokLen { get; private set; }

    public byte[]? StringValue { get; private set; }
    public string? NameValue { get; private set; }

    private static CharClass[] BuildCharClasses()
    {
        var classes = new CharClass[256];
        foreach (byte white in "\0\t\n\f\r "u8) classes[white] = CharClass.Whitespace;
        foreach (byte delimiter in "()<>[]{}/%"u8) classes[delimiter] = CharClass.Delimiter;
        return classes;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhite(byte b) => CharClasses[b] == CharClass.Whitespace;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRegular(byte b) => CharClasses[b] == CharClass.Regular;

    // The value of each byte as a hexadecimal digit, or -1; a table is faster than comparing against the three ranges.
    private static readonly sbyte[] HexValues = BuildHexValues();

    private static sbyte[] BuildHexValues()
    {
        var values = new sbyte[256];
        Array.Fill(values, (sbyte)-1);
        for (int digit = 0; digit < 16; digit++)
        {
            char lower = "0123456789abcdef"[digit];
            values[lower] = (sbyte)digit;
            values[char.ToUpperInvariant(lower)] = (sbyte)digit;
        }
        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HexVal(byte c) => HexValues[c];

    /// <summary>
    /// Decodes hexadecimal digits, ignoring every other byte, the way hex strings and the ASCIIHexDecode filter read
    /// them. A last digit without a partner counts as the high half of a byte.
    /// </summary>
    public static byte[] DecodeHex(ReadOnlySpan<byte> digits)
    {
        // Counting first gives the result its exact size, so that a string with spaces or junk in it is not copied.
        int digitCount = 0;
        foreach (byte c in digits)
        {
            if (HexVal(c) >= 0) digitCount++;
        }
        if (digitCount == 0) return Array.Empty<byte>();

        var bytes = new byte[(digitCount + 1) / 2];
        int count = 0;
        int high = -1;
        foreach (byte c in digits)
        {
            int value = HexVal(c);
            if (value < 0) continue;
            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes[count++] = (byte)(high << 4 | value);
                high = -1;
            }
        }
        if (high >= 0) bytes[count] = (byte)(high << 4);
        return bytes;
    }

    public void SkipWhite()
    {
        var scan = Scan();
        SkipWhite(ref scan);
        Pos = scan.Position;
    }

    public bool KeywordIs(string s)
        => Kind == TokenKind.Keyword && TokLen == s.Length && Ascii.Equals(Data.AsSpan(TokStart, TokLen), s);

    // The scanner stops at End, so that a lexer over a prefix of the array never reads past it.
    private ByteScanner Scan() => new(Data.AsSpan(0, End), Pos);

    // ------------------------------------------------------------------ tokens

    /// <summary>Reads the next token. Strings and names are materialized; numbers are not boxed.</summary>
    public TokenKind Next()
    {
        var scan = Scan();
        while (true)
        {
            SkipWhite(ref scan);
            if (scan.AtEnd) return Finish(ref scan, TokenKind.Eof);

            byte b = scan.Current;
            switch (b)
            {
                case (byte)'[': scan.Advance(); return Finish(ref scan, TokenKind.ArrayStart);
                case (byte)']': scan.Advance(); return Finish(ref scan, TokenKind.ArrayEnd);
                case (byte)'{': scan.Advance(); return Finish(ref scan, TokenKind.BraceOpen);
                case (byte)'}': scan.Advance(); return Finish(ref scan, TokenKind.BraceClose);
                case (byte)'(':
                    scan.Advance();
                    StringValue = ReadLiteralString(scan.Rest, out int stringLength);
                    scan.Advance(stringLength);
                    return Finish(ref scan, TokenKind.String);
                case (byte)'<':
                    scan.Advance();
                    if (scan.TryConsume((byte)'<')) return Finish(ref scan, TokenKind.DictStart);
                    StringValue = ReadHexString(scan.Rest, out int hexLength);
                    scan.Advance(hexLength);
                    return Finish(ref scan, TokenKind.HexString);
                case (byte)'>':
                    scan.Advance();
                    if (scan.TryConsume((byte)'>')) return Finish(ref scan, TokenKind.DictEnd);
                    continue; // a stray '>'
                case (byte)')':
                    scan.Advance();
                    continue; // a stray ')'
                case (byte)'/':
                    scan.Advance();
                    NameValue = ReadName(scan.Rest, out int nameLength);
                    scan.Advance(nameLength);
                    return Finish(ref scan, TokenKind.Name);
            }

            if (StartsNumber(b) && TryReadNumber(ref scan)) return Finish(ref scan, Kind);
            if (TryReadKeyword(ref scan)) return Finish(ref scan, TokenKind.Keyword);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TokenKind Finish(ref ByteScanner scan, TokenKind kind)
    {
        Pos = scan.Position;
        return Kind = kind;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SkipWhite(ref ByteScanner scan)
    {
        while (true)
        {
            scan.SkipWhile<Whitespace>();
            if (scan.AtEnd || scan.Current != '%') return;
            scan.Advance(CommentLength(scan.Rest));
        }
    }

    // A comment runs to the end of its line, and to the end of the data when it has no line break.
    private static int CommentLength(ReadOnlySpan<byte> rest)
    {
        int lineEnd = rest.IndexOfAny((byte)'\n', (byte)'\r');
        return lineEnd < 0 ? rest.Length : lineEnd;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool StartsNumber(byte b) => b is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'.';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>Reads a run of regular characters as a keyword. A byte that is no token at all is skipped instead.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadKeyword(ref ByteScanner scan)
    {
        int start = scan.Position;
        scan.SkipWhile<RegularCharacter>();
        TokStart = start;
        TokLen = scan.Position - start;
        if (TokLen > 0) return true;
        scan.Advance();
        return false;
    }

    /// <summary>
    /// Reads a number at the cursor and sets <see cref="Kind"/> and the value, or returns false and leaves the cursor
    /// alone, so that the bytes are read as a keyword instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReadNumber(ref ByteScanner scan)
    {
        int length = ParseNumber(scan.Rest);
        if (length == 0) return false;
        scan.Advance(length);
        return true;
    }

    // The number is parsed over a span and counted in bytes, not with a scanner passed by reference, because the loops
    // then keep their position in a register, which is much faster on the hottest path of the lexer. Returns how many
    // bytes the number has, or zero when there is none.
    private int ParseNumber(ReadOnlySpan<byte> text)
    {
        int signLength = CountSigns(text, out bool negative);
        var afterSigns = text.Slice(signLength);
        long whole = ReadDigits(afterSigns, out int wholeDigits);
        var rest = afterSigns.Slice(wholeDigits);
        int length = signLength + wholeDigits;

        if (!rest.IsEmpty && rest[0] == '.')
        {
            var afterPoint = rest.Slice(1);
            double fraction = ReadFraction(afterPoint, out int fractionDigits, out double scale);
            if (wholeDigits + fractionDigits == 0) return 0;
            // Junk glued to the number, such as a second '.', is dropped.
            int junkLength = CountRegular(afterPoint.Slice(fractionDigits));
            double value = whole + fraction / scale;
            return SetReal(negative ? -value : value, length + 1 + fractionDigits + junkLength);
        }

        if (wholeDigits == 0) return 0;
        if (!rest.IsEmpty && IsRegular(rest[0]))
        {
            // Letters after the digits are junk, except for an exponent in the form of "1e5".
            if (!TryReadExponent(rest, whole, out int exponentLength, out double scaled)) return 0;
            return SetReal(negative ? -scaled : scaled, length + exponentLength);
        }

        Kind = TokenKind.Integer;
        Integer = negative ? -whole : whole;
        Number = Integer;
        return length;
    }

    private int SetReal(double value, int length)
    {
        Kind = TokenKind.Real;
        Number = value;
        return length;
    }

    /// <summary>Counts the leading signs, tolerating repeated ones ("--5", "+-5"), and tells whether the number is negative.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountSigns(ReadOnlySpan<byte> text, out bool negative)
    {
        negative = false;
        int count = 0;
        while (count < text.Length && text[count] is (byte)'-' or (byte)'+')
        {
            if (text[count] == '-') negative = !negative;
            count++;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadDigits(ReadOnlySpan<byte> text, out int digits)
    {
        long value = 0;
        int count = 0;
        while (count < text.Length && IsDigit(text[count]))
        {
            if (value < DigitAccumulationLimit) value = value * 10 + (text[count] - '0');
            count++;
        }
        digits = count;
        return value;
    }

    /// <summary>Reads the digits after the decimal point, counting all of them in <paramref name="digits"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ReadFraction(ReadOnlySpan<byte> text, out int digits, out double scale)
    {
        double fraction = 0;
        scale = 1;
        int count = 0;
        while (count < text.Length && IsDigit(text[count]))
        {
            if (count < MaxFractionDigits)
            {
                fraction = fraction * 10 + (text[count] - '0');
                scale *= 10;
            }
            count++;
        }
        digits = count;
        return fraction;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountRegular(ReadOnlySpan<byte> text)
    {
        int count = 0;
        while (count < text.Length && IsRegular(text[count])) count++;
        return count;
    }

    /// <summary>
    /// Reads "e5" or "E-5" at the start of <paramref name="text"/>, which follows the digits of <paramref name="whole"/>,
    /// if what follows is a complete exponent.
    /// </summary>
    private static bool TryReadExponent(ReadOnlySpan<byte> text, long whole, out int length, out double value)
    {
        length = 0;
        value = 0;
        if (text[0] is not ((byte)'e' or (byte)'E')) return false;

        int position = 1;
        bool negative = false;
        if (position < text.Length && text[position] is (byte)'-' or (byte)'+')
        {
            negative = text[position] == '-';
            position++;
        }
        int exponent = 0;
        int exponentStart = position;
        while (position < text.Length && IsDigit(text[position]))
        {
            exponent = Math.Min(MaxExponent, exponent * 10 + (text[position] - '0'));
            position++;
        }
        // The number must end where the exponent ends: a further regular character makes the whole token a keyword.
        bool endsHere = position >= text.Length || !IsRegular(text[position]);
        if (position == exponentStart || !endsHere) return false;

        length = position;
        value = whole * Math.Pow(10, negative ? -exponent : exponent);
        return true;
    }

    /// <summary>Reads the string after its opening parenthesis from <paramref name="rest"/> and tells how many bytes it took.</summary>
    private static byte[] ReadLiteralString(ReadOnlySpan<byte> rest, out int consumed)
    {
        int special = rest.IndexOfAny(LiteralStringSpecials);
        if (special < 0)
        {
            // Unterminated: the string runs to the end.
            consumed = rest.Length;
            return rest.ToArray();
        }
        if (rest[special] == ')')
        {
            consumed = special + 1;
            return rest.Slice(0, special).ToArray();
        }
        return ReadComplexLiteralString(rest, special, out consumed);
    }

    /// <summary>Reads a string that has escapes, nested parentheses or line breaks, after its plain start of <paramref name="plainLength"/> bytes.</summary>
    private static byte[] ReadComplexLiteralString(ReadOnlySpan<byte> rest, int plainLength, out int consumed)
    {
        var scan = new ByteScanner(rest, plainLength);
        var text = new ByteBuffer(Math.Max(16, plainLength + 16));
        text.Bytes(rest.Slice(0, plainLength));
        int depth = 1;
        while (scan.TryReadByte(out byte c))
        {
            if (c == '\\')
            {
                if (!TryReadEscape(ref scan, text)) break;
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (--depth == 0) break;
            }
            else if (c == CarriageReturn)
            {
                // End-of-line markers inside strings are read as a single line feed.
                scan.TryConsume(LineFeed);
                text.Byte(LineFeed);
                continue;
            }
            text.Byte(c);
        }
        consumed = scan.Position;
        return text.Span.ToArray();
    }

    /// <summary>Reads the escape after a backslash. Returns false when the input ends there.</summary>
    private static bool TryReadEscape(ref ByteScanner scan, ByteBuffer text)
    {
        if (!scan.TryReadByte(out byte escape)) return false;
        switch (escape)
        {
            case (byte)'n': text.Byte(LineFeed); break;
            case (byte)'r': text.Byte(CarriageReturn); break;
            case (byte)'t': text.Byte(Tab); break;
            case (byte)'b': text.Byte(Backspace); break;
            case (byte)'f': text.Byte(FormFeed); break;
            case CarriageReturn: scan.TryConsume(LineFeed); break; // a line continuation adds nothing
            case LineFeed: break;
            case >= (byte)'0' and <= (byte)'7': text.Byte(ReadOctalEscape(ref scan, escape - '0')); break;
            default: text.Byte(escape); break; // "\(", "\)", "\\" and an unknown escape stand for the character itself
        }
        return true;
    }

    /// <summary>Reads up to two more octal digits after the first. A value above 255 keeps its low eight bits.</summary>
    private static byte ReadOctalEscape(ref ByteScanner scan, int value)
    {
        for (int i = 0; i < 2 && !scan.AtEnd && scan.Current is >= (byte)'0' and <= (byte)'7'; i++)
        {
            value = value * 8 + (scan.Current - '0');
            scan.Advance();
        }
        return (byte)value;
    }

    private static byte[] ReadHexString(ReadOnlySpan<byte> rest, out int consumed)
    {
        int close = rest.IndexOf((byte)'>');
        if (close < 0)
        {
            consumed = rest.Length;
            return DecodeHex(rest);
        }
        consumed = close + 1;
        return DecodeHex(rest.Slice(0, close));
    }

    private string ReadName(ReadOnlySpan<byte> rest, out int consumed)
    {
        int length = 0;
        while (length < rest.Length && IsRegular(rest[length])) length++;
        consumed = length;
        var raw = rest.Slice(0, length);
        if (!raw.Contains((byte)'#')) return names.Get(raw);

        Span<byte> decoded = raw.Length <= 256 ? stackalloc byte[256] : new byte[raw.Length];
        return names.Get(decoded.Slice(0, DecodeNameEscapes(raw, decoded)));
    }

    /// <summary>Replaces each "#xx" in a name with its byte. A '#' without two hex digits after it stays as it is.</summary>
    private static int DecodeNameEscapes(ReadOnlySpan<byte> raw, Span<byte> decoded)
    {
        int count = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#')
            {
                int high = i + 1 < raw.Length ? HexVal(raw[i + 1]) : -1;
                int low = i + 2 < raw.Length ? HexVal(raw[i + 2]) : -1;
                if (high >= 0 && low >= 0)
                {
                    decoded[count++] = (byte)(high << 4 | low);
                    i += 2;
                    continue;
                }
            }
            decoded[count++] = raw[i];
        }
        return count;
    }

    // ------------------------------------------------------------------ objects

    /// <summary>Parses one object at the current position. Integers followed by "g R" become references.</summary>
    public PdfObject? ReadObject(int depth = 0)
    {
        var kind = Next();
        return ObjectFromToken(kind, depth);
    }

    public PdfObject? ObjectFromToken(TokenKind kind, int depth)
    {
        if (depth > MaxNestingDepth) throw new PdfFormatException("Objects nested too deeply.");
        switch (kind)
        {
            case TokenKind.Integer: return ReadIntegerOrReference();
            case TokenKind.Real: return PdfNumber.Real(Number);
            case TokenKind.String:
            case TokenKind.HexString: return new PdfString(StringValue!, kind == TokenKind.HexString);
            case TokenKind.Name: return PdfName.Get(NameValue!);
            case TokenKind.ArrayStart: return ReadArray(depth);
            case TokenKind.DictStart: return ReadDictionary(depth);
            case TokenKind.Keyword: return KeywordObject();
            default: return null;
        }
    }

    /// <summary>Reads the integer just tokenized, or a reference when "generation R" follows it.</summary>
    private PdfObject ReadIntegerOrReference()
    {
        long number = Integer;
        int afterNumber = Pos;
        if (number >= 0 && Next() == TokenKind.Integer)
        {
            long generation = Integer;
            if (Next() == TokenKind.Keyword && KeywordIs("R") && generation is >= 0 and < GenerationLimit && number < int.MaxValue)
                return new PdfRef((int)number, (int)generation);
        }
        Pos = afterNumber;
        Kind = TokenKind.Integer;
        return PdfNumber.Int(number);
    }

    private PdfArray ReadArray(int depth)
    {
        var items = new List<PdfObject>();
        while (true)
        {
            var kind = Next();
            if (kind is TokenKind.ArrayEnd or TokenKind.Eof) break;
            if (kind == TokenKind.DictEnd) continue;
            if (kind == TokenKind.Keyword && IsStructureKeyword())
            {
                // An unclosed array ends where the next object starts.
                Pos = TokStart;
                break;
            }
            var item = ObjectFromToken(kind, depth + 1);
            if (item != null) items.Add(item);
        }
        return new PdfArray(items);
    }

    private PdfDict ReadDictionary(int depth)
    {
        var entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        while (true)
        {
            var kind = Next();
            if (kind is TokenKind.DictEnd or TokenKind.Eof) break;
            if (kind == TokenKind.Keyword && IsStructureKeyword())
            {
                Pos = TokStart;
                break;
            }
            if (kind != TokenKind.Name) continue; // a malformed key: skip

            string key = NameValue!;
            int afterKey = Pos;
            var valueKind = Next();
            if (valueKind == TokenKind.DictEnd)
            {
                entries[key] = PdfNull.Instance;
                break;
            }
            if (valueKind == TokenKind.Keyword && (KeywordIs("endobj") || KeywordIs("stream")))
            {
                Pos = afterKey;
                break;
            }
            var value = ObjectFromToken(valueKind, depth + 1);
            if (value != null && !value.IsNull) entries[key] = value;
        }
        return new PdfDict(entries);
    }

    private PdfObject? KeywordObject()
    {
        if (KeywordIs("null")) return PdfNull.Instance;
        if (KeywordIs("true")) return PdfBool.True;
        if (KeywordIs("false")) return PdfBool.False;
        return null;
    }

    private bool IsStructureKeyword() => KeywordIs("endobj") || KeywordIs("obj") || KeywordIs("stream");
}

/// <summary>Maps name bytes to interned strings without allocating on hits.</summary>
internal sealed class NameCache
{
    private const int SlotCount = 4096;
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    public static readonly NameCache Shared = new();

    // A slot holds a reference to an immutable entry, so a thread that races another on the same slot sees one entry or
    // the other, never bytes from one and a string from the other.
    private sealed class Entry(byte[] bytes, string value)
    {
        public byte[] Bytes { get; } = bytes;

        public string Value { get; } = value;
    }

    private readonly Entry?[] slots = new Entry?[SlotCount];

    public string Get(byte[] data, int start, int length) => Get(data.AsSpan(start, length));

    public string Get(ReadOnlySpan<byte> name)
    {
        int slot = (int)(Hash(name) & (SlotCount - 1));
        var entry = slots[slot];
        if (entry != null && name.SequenceEqual(entry.Bytes)) return entry.Value;

        var bytes = name.ToArray();
        var value = Encoding.Latin1.GetString(bytes);
        slots[slot] = new Entry(bytes, value);
        return value;
    }

    private static uint Hash(ReadOnlySpan<byte> name)
    {
        uint hash = FnvOffsetBasis;
        foreach (byte b in name) hash = (hash ^ b) * FnvPrime;
        return hash;
    }
}

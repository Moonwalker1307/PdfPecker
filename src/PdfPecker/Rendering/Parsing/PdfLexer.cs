using System.Runtime.CompilerServices;

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
    public readonly byte[] Data;
    public int Pos;
    public readonly int End;

    // Last token.
    public TokenKind Kind;
    public double Number;
    public long Integer;
    public int TokStart, TokLen; // keyword bytes
    public byte[]? StringValue;
    public string? NameValue;

    private readonly NameCache _names;

    public PdfLexer(byte[] data, int start = 0, int end = -1, NameCache? names = null)
    {
        Data = data;
        Pos = start;
        End = end < 0 ? data.Length : end;
        _names = names ?? NameCache.Shared;
    }

    private static readonly byte[] CharClass = BuildClasses(); // 0 regular, 1 whitespace, 2 delimiter

    private static byte[] BuildClasses()
    {
        var c = new byte[256];
        foreach (var w in new byte[] { 0, 9, 10, 12, 13, 32 }) c[w] = 1;
        foreach (var d in "()<>[]{}/%") c[d] = 2;
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsWhite(byte b) => CharClass[b] == 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRegular(byte b) => CharClass[b] == 0;

    public void SkipWhite()
    {
        var d = Data;
        while (Pos < End)
        {
            byte b = d[Pos];
            if (CharClass[b] == 1) { Pos++; continue; }
            if (b == '%')
            {
                while (Pos < End && d[Pos] != '\n' && d[Pos] != '\r') Pos++;
                continue;
            }
            break;
        }
    }

    public bool KeywordIs(string s)
    {
        if (Kind != TokenKind.Keyword || TokLen != s.Length) return false;
        for (int i = 0; i < s.Length; i++) if (Data[TokStart + i] != s[i]) return false;
        return true;
    }

    public string KeywordString() => System.Text.Encoding.Latin1.GetString(Data, TokStart, TokLen);

    /// <summary>Reads the next token. Strings and names are materialized; numbers are not boxed.</summary>
    public TokenKind Next()
    {
        SkipWhite();
        var d = Data;
        if (Pos >= End) return Kind = TokenKind.Eof;
        byte b = d[Pos];
        switch (b)
        {
            case (byte)'[': Pos++; return Kind = TokenKind.ArrayStart;
            case (byte)']': Pos++; return Kind = TokenKind.ArrayEnd;
            case (byte)'{': Pos++; return Kind = TokenKind.BraceOpen;
            case (byte)'}': Pos++; return Kind = TokenKind.BraceClose;
            case (byte)'(':
                Pos++;
                StringValue = ReadLiteralString();
                return Kind = TokenKind.String;
            case (byte)'<':
                if (Pos + 1 < End && d[Pos + 1] == '<') { Pos += 2; return Kind = TokenKind.DictStart; }
                Pos++;
                StringValue = ReadHexString();
                return Kind = TokenKind.HexString;
            case (byte)'>':
                if (Pos + 1 < End && d[Pos + 1] == '>') { Pos += 2; return Kind = TokenKind.DictEnd; }
                Pos++;
                return Next(); // stray '>'
            case (byte)')':
                Pos++;
                return Next();
            case (byte)'/':
                Pos++;
                NameValue = ReadName();
                return Kind = TokenKind.Name;
        }
        if ((b >= '0' && b <= '9') || b == '-' || b == '+' || b == '.')
        {
            if (TryReadNumber()) return Kind;
        }
        TokStart = Pos;
        while (Pos < End && CharClass[d[Pos]] == 0) Pos++;
        TokLen = Pos - TokStart;
        if (TokLen == 0) { Pos++; return Next(); }
        return Kind = TokenKind.Keyword;
    }

    private bool TryReadNumber()
    {
        var d = Data;
        int p = Pos;
        bool neg = false;
        // Tolerate repeated signs ("--5", "+-5").
        while (p < End && (d[p] == '-' || d[p] == '+')) { if (d[p] == '-') neg = !neg; p++; }
        long ip = 0;
        int digits = 0;
        while (p < End && d[p] >= '0' && d[p] <= '9')
        {
            if (ip < 100_000_000_000_000_000L) ip = ip * 10 + (d[p] - '0');
            p++;
            digits++;
        }
        if (p < End && d[p] == '.')
        {
            p++;
            double frac = 0, scale = 1;
            int fd = 0;
            while (p < End && d[p] >= '0' && d[p] <= '9')
            {
                if (fd < 15) { frac = frac * 10 + (d[p] - '0'); scale *= 10; fd++; }
                p++;
                digits++;
            }
            if (digits == 0) return false;
            // Skip junk like a second '.' or exponent-ish tails glued to the number.
            while (p < End && CharClass[d[p]] == 0) p++;
            Pos = p;
            double v = ip + frac / scale;
            Number = neg ? -v : v;
            Kind = TokenKind.Real;
            return true;
        }
        if (digits == 0) return false;
        if (p < End && CharClass[d[p]] == 0)
        {
            // e.g. "1e5" or "12abc": treat leading digits as the number when followed by junk in content streams.
            if (d[p] == 'e' || d[p] == 'E')
            {
                int q = p + 1;
                bool eneg = false;
                if (q < End && (d[q] == '-' || d[q] == '+')) { eneg = d[q] == '-'; q++; }
                int e = 0, ed = 0;
                while (q < End && d[q] >= '0' && d[q] <= '9') { e = Math.Min(400, e * 10 + (d[q] - '0')); q++; ed++; }
                if (ed > 0 && (q >= End || CharClass[d[q]] != 0))
                {
                    Pos = q;
                    double v = ip * Math.Pow(10, eneg ? -e : e);
                    Number = neg ? -v : v;
                    Kind = TokenKind.Real;
                    return true;
                }
            }
            return false;
        }
        Pos = p;
        Integer = neg ? -ip : ip;
        Number = Integer;
        Kind = TokenKind.Integer;
        return true;
    }

    private byte[] ReadLiteralString()
    {
        var d = Data;
        // Fast path: no escapes, no nested parentheses.
        int start = Pos, p = Pos;
        while (p < End)
        {
            byte c = d[p];
            if (c == ')') { Pos = p + 1; return d.AsSpan(start, p - start).ToArray(); }
            if (c == '\\' || c == '(' || c == '\r') break;
            p++;
        }
        var buf = new List<byte>(Math.Max(16, p - start + 16));
        for (int i = start; i < p; i++) buf.Add(d[i]);
        int depth = 1;
        while (p < End)
        {
            byte c = d[p++];
            if (c == '\\')
            {
                if (p >= End) break;
                byte e = d[p++];
                switch (e)
                {
                    case (byte)'n': buf.Add(10); break;
                    case (byte)'r': buf.Add(13); break;
                    case (byte)'t': buf.Add(9); break;
                    case (byte)'b': buf.Add(8); break;
                    case (byte)'f': buf.Add(12); break;
                    case (byte)'\r':
                        if (p < End && d[p] == '\n') p++;
                        break;
                    case (byte)'\n': break;
                    default:
                        if (e >= '0' && e <= '7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && p < End && d[p] >= '0' && d[p] <= '7'; k++) v = v * 8 + (d[p++] - '0');
                            buf.Add((byte)v);
                        }
                        else buf.Add(e);
                        break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (--depth == 0) break;
            }
            else if (c == '\r')
            {
                // End-of-line markers inside strings are read as a single \n.
                if (p < End && d[p] == '\n') p++;
                buf.Add(10);
                continue;
            }
            buf.Add(c);
        }
        Pos = p;
        return buf.ToArray();
    }

    private byte[] ReadHexString()
    {
        var d = Data;
        var buf = new List<byte>(32);
        int hi = -1;
        while (Pos < End)
        {
            byte c = d[Pos++];
            if (c == '>') break;
            int v = HexVal(c);
            if (v < 0) continue;
            if (hi < 0) hi = v;
            else { buf.Add((byte)(hi << 4 | v)); hi = -1; }
        }
        if (hi >= 0) buf.Add((byte)(hi << 4));
        return buf.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int HexVal(byte c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private string ReadName()
    {
        var d = Data;
        int start = Pos;
        bool escaped = false;
        while (Pos < End && CharClass[d[Pos]] == 0)
        {
            if (d[Pos] == '#') escaped = true;
            Pos++;
        }
        if (!escaped) return _names.Get(d, start, Pos - start);
        var bytes = new List<byte>(Pos - start);
        for (int i = start; i < Pos; i++)
        {
            if (d[i] == '#')
            {
                int h1 = i + 1 < Pos ? HexVal(d[i + 1]) : -1, h2 = i + 2 < Pos ? HexVal(d[i + 2]) : -1;
                if (h1 >= 0 && h2 >= 0) { bytes.Add((byte)(h1 << 4 | h2)); i += 2; continue; }
            }
            bytes.Add(d[i]);
        }
        var arr = bytes.ToArray();
        return _names.Get(arr, 0, arr.Length);
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
        if (depth > 200) throw new PdfFormatException("Objects nested too deeply.");
        switch (kind)
        {
            case TokenKind.Integer:
            {
                long v = Integer;
                // Look ahead for "gen R".
                int save = Pos;
                if (v >= 0 && Next() == TokenKind.Integer)
                {
                    long gen = Integer;
                    if (Next() == TokenKind.Keyword && TokLen == 1 && Data[TokStart] == 'R' && gen >= 0 && gen < 65536 && v < int.MaxValue)
                        return new PdfRef((int)v, (int)gen);
                }
                Pos = save;
                Kind = TokenKind.Integer;
                return PdfNumber.Int(v);
            }
            case TokenKind.Real: return PdfNumber.Real(Number);
            case TokenKind.String:
            case TokenKind.HexString: return new PdfString(StringValue!, kind == TokenKind.HexString);
            case TokenKind.Name: return PdfName.Get(NameValue!);
            case TokenKind.ArrayStart:
            {
                var list = new List<PdfObject>();
                while (true)
                {
                    var k = Next();
                    if (k == TokenKind.ArrayEnd || k == TokenKind.Eof) break;
                    if (k == TokenKind.DictEnd) continue;
                    if (k == TokenKind.Keyword && (KeywordIs("endobj") || KeywordIs("obj") || KeywordIs("stream"))) { Pos = TokStart; break; }
                    var o = ObjectFromToken(k, depth + 1);
                    if (o != null) list.Add(o);
                }
                return new PdfArray(list);
            }
            case TokenKind.DictStart:
            {
                var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                while (true)
                {
                    var k = Next();
                    if (k == TokenKind.DictEnd || k == TokenKind.Eof) break;
                    if (k == TokenKind.Keyword && (KeywordIs("endobj") || KeywordIs("obj") || KeywordIs("stream"))) { Pos = TokStart; break; }
                    if (k != TokenKind.Name) continue; // malformed key: skip
                    string key = NameValue!;
                    int save = Pos;
                    var vk = Next();
                    if (vk == TokenKind.DictEnd) { dict[key] = PdfNull.Instance; break; }
                    if (vk == TokenKind.Keyword && (KeywordIs("endobj") || KeywordIs("stream"))) { Pos = save; break; }
                    var val = ObjectFromToken(vk, depth + 1);
                    if (val != null && !val.IsNull) dict[key] = val;
                }
                return new PdfDict(dict);
            }
            case TokenKind.Keyword:
                if (KeywordIs("null")) return PdfNull.Instance;
                if (KeywordIs("true")) return PdfBool.True;
                if (KeywordIs("false")) return PdfBool.False;
                return null;
            default:
                return null;
        }
    }
}

/// <summary>Maps name bytes to interned strings without allocating on hits.</summary>
internal sealed class NameCache
{
    public static readonly NameCache Shared = new();

    private sealed class Entry
    {
        public readonly byte[] Bytes;
        public readonly string Value;
        public Entry(byte[] b, string v) { Bytes = b; Value = v; }
    }

    private readonly Entry?[] _slots = new Entry?[4096];

    public string Get(byte[] d, int start, int len)
    {
        uint h = 2166136261;
        for (int i = 0; i < len; i++) h = (h ^ d[start + i]) * 16777619;
        int slot = (int)(h & 4095);
        var e = _slots[slot];
        if (e != null && e.Bytes.Length == len && d.AsSpan(start, len).SequenceEqual(e.Bytes)) return e.Value;
        var bytes = d.AsSpan(start, len).ToArray();
        var s = System.Text.Encoding.Latin1.GetString(bytes);
        _slots[slot] = new Entry(bytes, s);
        return s;
    }
}

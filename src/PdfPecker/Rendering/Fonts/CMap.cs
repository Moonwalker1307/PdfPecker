using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Fonts;

/// <summary>
/// A character map: splits byte strings into codes using codespace ranges, and maps codes to CIDs (encoding CMaps)
/// or to Unicode (ToUnicode CMaps).
/// </summary>
internal sealed class CMap
{
    private readonly List<(int Bytes, uint Lo, uint Hi)> _codespace = new();
    private readonly Dictionary<uint, int> _single = new();
    private readonly List<(uint Lo, uint Hi, int Start)> _ranges = new();
    private readonly Dictionary<uint, string> _unicode = new();
    public bool IsIdentity { get; private set; }
    public bool Vertical { get; private set; }

    public static CMap Identity(bool vertical) => new() { IsIdentity = true, Vertical = vertical };

    /// <summary>Reads the next code from <paramref name="s"/>.</summary>
    public uint NextCode(ReadOnlySpan<byte> s, ref int pos, out int length)
    {
        if (_codespace.Count == 0)
        {
            int n = 2;
            if (pos + n > s.Length) n = s.Length - pos;
            uint c = 0;
            for (int i = 0; i < n; i++) c = c << 8 | s[pos + i];
            pos += n;
            length = n;
            return c;
        }
        uint code = 0;
        for (int n = 1; n <= 4 && pos + n <= s.Length; n++)
        {
            code = code << 8 | s[pos + n - 1];
            foreach (var (bytes, lo, hi) in _codespace)
            {
                if (bytes == n && code >= lo && code <= hi)
                {
                    pos += n;
                    length = n;
                    return code;
                }
            }
        }
        // No codespace matched: consume the shortest code length.
        int min = _codespace.Min(c => c.Bytes);
        min = Math.Min(min, s.Length - pos);
        code = 0;
        for (int i = 0; i < min; i++) code = code << 8 | s[pos + i];
        pos += Math.Max(1, min);
        length = Math.Max(1, min);
        return code;
    }

    public int ToCid(uint code)
    {
        if (_single.TryGetValue(code, out int cid)) return cid;
        foreach (var (lo, hi, start) in _ranges)
            if (code >= lo && code <= hi) return start + (int)(code - lo);
        return IsIdentity ? (int)code : 0;
    }

    public string? ToUnicode(uint code) => _unicode.TryGetValue(code, out var s) ? s : null;

    public static CMap? Parse(byte[] data, PdfFile? file = null, int depth = 0)
    {
        var map = new CMap();
        try
        {
            map.ParseInto(data, file, depth);
        }
        catch (Exception ex) when (ex is PdfFormatException or IndexOutOfRangeException or ArgumentException)
        {
        }
        return map;
    }

    private void ParseInto(byte[] data, PdfFile? file, int depth)
    {
        var lx = new PdfLexer(data);
        var operands = new List<object>();
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof) break;
            switch (k)
            {
                case TokenKind.Integer:
                    operands.Add(lx.Integer);
                    break;
                case TokenKind.HexString:
                case TokenKind.String:
                    operands.Add(lx.StringValue!);
                    break;
                case TokenKind.Name:
                    operands.Add(lx.NameValue!);
                    break;
                case TokenKind.ArrayStart:
                {
                    var arr = new List<object>();
                    while (true)
                    {
                        var ak = lx.Next();
                        if (ak is TokenKind.ArrayEnd or TokenKind.Eof) break;
                        if (ak is TokenKind.HexString or TokenKind.String) arr.Add(lx.StringValue!);
                        else if (ak == TokenKind.Integer) arr.Add(lx.Integer);
                    }
                    operands.Add(arr);
                    break;
                }
                case TokenKind.Keyword:
                {
                    if (lx.KeywordIs("begincodespacerange")) { ReadCodespace(lx); operands.Clear(); }
                    else if (lx.KeywordIs("begincidrange")) { ReadCidRange(lx); operands.Clear(); }
                    else if (lx.KeywordIs("begincidchar")) { ReadCidChar(lx); operands.Clear(); }
                    else if (lx.KeywordIs("beginbfchar")) { ReadBfChar(lx); operands.Clear(); }
                    else if (lx.KeywordIs("beginbfrange")) { ReadBfRange(lx); operands.Clear(); }
                    else if (lx.KeywordIs("usecmap"))
                    {
                        if (operands.Count > 0 && operands[^1] is string parent)
                        {
                            if (parent.StartsWith("Identity", StringComparison.Ordinal)) { IsIdentity = true; Vertical = parent.EndsWith("-V", StringComparison.Ordinal); }
                        }
                        operands.Clear();
                    }
                    else if (lx.KeywordIs("def"))
                    {
                        if (operands.Count >= 2 && operands[^2] is string key && key == "WMode" && operands[^1] is long wm) Vertical = wm == 1;
                        operands.Clear();
                    }
                    else operands.Clear();
                    break;
                }
                default:
                    break;
            }
        }
    }

    private static uint Code(byte[] b, out int len)
    {
        len = Math.Clamp(b.Length, 1, 4);
        uint c = 0;
        for (int i = 0; i < Math.Min(4, b.Length); i++) c = c << 8 | b[i];
        return c;
    }

    private void ReadCodespace(PdfLexer lx)
    {
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof || k == TokenKind.Keyword) break;
            if (k is not (TokenKind.HexString or TokenKind.String)) continue;
            var lo = lx.StringValue!;
            if (lx.Next() is not (TokenKind.HexString or TokenKind.String)) break;
            var hi = lx.StringValue!;
            _codespace.Add((Math.Clamp(lo.Length, 1, 4), Code(lo, out _), Code(hi, out _)));
        }
        _codespace.Sort((a, b) => a.Bytes.CompareTo(b.Bytes));
    }

    private void ReadCidRange(PdfLexer lx)
    {
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof || k == TokenKind.Keyword) break;
            if (k is not (TokenKind.HexString or TokenKind.String)) continue;
            var lo = lx.StringValue!;
            if (lx.Next() is not (TokenKind.HexString or TokenKind.String)) break;
            var hi = lx.StringValue!;
            if (lx.Next() != TokenKind.Integer) break;
            uint l = Code(lo, out _), h = Code(hi, out _);
            if (l == h) _single[l] = (int)lx.Integer;
            else _ranges.Add((l, h, (int)lx.Integer));
        }
    }

    private void ReadCidChar(PdfLexer lx)
    {
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof || k == TokenKind.Keyword) break;
            if (k is not (TokenKind.HexString or TokenKind.String)) continue;
            var c = lx.StringValue!;
            if (lx.Next() != TokenKind.Integer) break;
            _single[Code(c, out _)] = (int)lx.Integer;
        }
    }

    private void ReadBfChar(PdfLexer lx)
    {
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof || k == TokenKind.Keyword) break;
            if (k is not (TokenKind.HexString or TokenKind.String)) continue;
            var src = lx.StringValue!;
            var k2 = lx.Next();
            if (k2 is TokenKind.HexString or TokenKind.String) _unicode[Code(src, out _)] = Utf16(lx.StringValue!);
            else if (k2 == TokenKind.Name) { var u = Encodings.NameToUnicode(lx.NameValue); if (u >= 0) _unicode[Code(src, out _)] = char.ConvertFromUtf32(u); }
            else break;
        }
    }

    private void ReadBfRange(PdfLexer lx)
    {
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof || k == TokenKind.Keyword) break;
            if (k is not (TokenKind.HexString or TokenKind.String)) continue;
            var lo = lx.StringValue!;
            if (lx.Next() is not (TokenKind.HexString or TokenKind.String)) break;
            var hi = lx.StringValue!;
            uint l = Code(lo, out _), h = Code(hi, out _);
            if (h < l || h - l > 65535) h = l;
            var k3 = lx.Next();
            if (k3 is TokenKind.HexString or TokenKind.String)
            {
                var dst = lx.StringValue!;
                for (uint c = l; c <= h; c++)
                {
                    var b = (byte[])dst.Clone();
                    // Increment the last byte(s).
                    uint add = c - l;
                    for (int i = b.Length - 1; i >= 0 && add > 0; i--)
                    {
                        uint v = b[i] + add;
                        b[i] = (byte)v;
                        add = v >> 8;
                    }
                    _unicode[c] = Utf16(b);
                }
            }
            else if (k3 == TokenKind.ArrayStart)
            {
                uint c = l;
                while (true)
                {
                    var ak = lx.Next();
                    if (ak is TokenKind.ArrayEnd or TokenKind.Eof) break;
                    if (ak is TokenKind.HexString or TokenKind.String && c <= h) _unicode[c++] = Utf16(lx.StringValue!);
                }
            }
            else break;
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

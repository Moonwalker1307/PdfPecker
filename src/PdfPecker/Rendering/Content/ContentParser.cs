using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Content;

internal enum OperandKind : byte { Number, Name, String, Array, Dict, Bool, Null }

/// <summary>A content stream operand: numbers are stored unboxed.</summary>
internal readonly struct Operand
{
    public readonly OperandKind Kind;
    public readonly double Num;
    public readonly object? Obj;

    public Operand(double n) { Kind = OperandKind.Number; Num = n; Obj = null; }
    public Operand(OperandKind kind, object? obj) { Kind = kind; Num = 0; Obj = obj; }

    public string? Name => Kind == OperandKind.Name ? (string)Obj! : null;
    public byte[]? Bytes => Kind == OperandKind.String ? (byte[])Obj! : null;
    public PdfArray? Array => Kind == OperandKind.Array ? (PdfArray)Obj! : null;
    public PdfDict? Dict => Kind == OperandKind.Dict ? (PdfDict)Obj! : null;
}

/// <summary>Splits a content stream into operators and their operands.</summary>
internal sealed class ContentParser
{
    private readonly PdfLexer _lx;
    public readonly List<Operand> Operands = new(16);
    public string Op = "";

    /// <summary>Inline image (BI ... ID ... EI) dictionary and data, set when <see cref="Op"/> is "BI".</summary>
    public PdfDict? InlineDict;
    public byte[]? InlineData;

    private static readonly NameCache Keywords = new();

    public ContentParser(byte[] data) => _lx = new PdfLexer(data);

    /// <summary>Advances to the next operator. Returns false at the end of the stream.</summary>
    public bool Next()
    {
        Operands.Clear();
        var lx = _lx;
        while (true)
        {
            var k = lx.Next();
            switch (k)
            {
                case TokenKind.Eof:
                    return false;
                case TokenKind.Integer:
                case TokenKind.Real:
                    Add(new Operand(lx.Number));
                    break;
                case TokenKind.Name:
                    Add(new Operand(OperandKind.Name, lx.NameValue));
                    break;
                case TokenKind.String:
                case TokenKind.HexString:
                    Add(new Operand(OperandKind.String, lx.StringValue));
                    break;
                case TokenKind.ArrayStart:
                {
                    PdfObject? o;
                    try { o = lx.ObjectFromToken(k, 0); }
                    catch (PdfFormatException) { o = null; }
                    Add(new Operand(OperandKind.Array, o as PdfArray ?? new PdfArray()));
                    break;
                }
                case TokenKind.DictStart:
                {
                    PdfObject? o;
                    try { o = lx.ObjectFromToken(k, 0); }
                    catch (PdfFormatException) { o = null; }
                    Add(new Operand(OperandKind.Dict, o as PdfDict ?? new PdfDict()));
                    break;
                }
                case TokenKind.Keyword:
                {
                    var op = Keywords.Get(lx.Data, lx.TokStart, lx.TokLen);
                    switch (op)
                    {
                        case "true": Add(new Operand(OperandKind.Bool, true)); continue;
                        case "false": Add(new Operand(OperandKind.Bool, false)); continue;
                        case "null": Add(new Operand(OperandKind.Null, null)); continue;
                        case "BI":
                            Op = "BI";
                            ReadInlineImage();
                            return true;
                    }
                    Op = op;
                    return true;
                }
                default:
                    // Stray delimiters: ignore.
                    break;
            }
        }
    }

    private void Add(in Operand o)
    {
        // Guard against garbage streams accumulating unbounded operands.
        if (Operands.Count < 4096) Operands.Add(o);
    }

    private void ReadInlineImage()
    {
        var lx = _lx;
        var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        while (true)
        {
            var k = lx.Next();
            if (k == TokenKind.Eof) { InlineDict = new PdfDict(dict); InlineData = System.Array.Empty<byte>(); return; }
            if (k == TokenKind.Keyword && lx.KeywordIs("ID")) break;
            if (k != TokenKind.Name) continue;
            string key = lx.NameValue!;
            PdfObject? v;
            try { v = lx.ReadObject(); }
            catch (PdfFormatException) { v = null; }
            if (v != null) dict[key] = v;
        }
        InlineDict = new PdfDict(dict);
        var d = lx.Data;
        int start = lx.Pos;
        if (start < lx.End && PdfLexer.IsWhite(d[start])) start++;
        // Find "EI" preceded by whitespace and followed by whitespace or the end of the stream.
        int end = -1;
        int expected = ExpectedLength(InlineDict);
        if (expected > 0 && start + expected <= lx.End)
        {
            int p = start + expected;
            int q = p;
            while (q < lx.End && PdfLexer.IsWhite(d[q])) q++;
            if (q + 1 < lx.End && d[q] == 'E' && d[q + 1] == 'I' && (q + 2 >= lx.End || !PdfLexer.IsRegular(d[q + 2]))) { end = p; lx.Pos = q + 2; }
        }
        if (end < 0)
        {
            for (int i = start; i + 1 < lx.End; i++)
            {
                if (d[i] != 'E' || d[i + 1] != 'I') continue;
                if (i > start && !PdfLexer.IsWhite(d[i - 1])) continue;
                if (i + 2 < lx.End && PdfLexer.IsRegular(d[i + 2])) continue;
                if (!LooksLikeContentFollows(d, i + 2, lx.End)) continue;
                end = i > start && PdfLexer.IsWhite(d[i - 1]) ? i - 1 : i;
                lx.Pos = i + 2;
                break;
            }
            if (end < 0) { end = lx.End; lx.Pos = lx.End; }
        }
        InlineData = d.AsSpan(start, Math.Max(0, end - start)).ToArray();
    }

    /// <summary>Checks that the bytes after a candidate "EI" read as content stream tokens, not image data.</summary>
    private static bool LooksLikeContentFollows(byte[] d, int p, int end)
    {
        int checkedBytes = 0;
        for (int i = p; i < end && checkedBytes < 32; i++, checkedBytes++)
        {
            byte b = d[i];
            if (b < 9 || (b > 13 && b < 32) || b > 126) return false;
        }
        return true;
    }

    private static int ExpectedLength(PdfDict dict)
    {
        if (dict["F"] != null || dict["Filter"] != null) return -1;
        int w = (dict["W"] ?? dict["Width"]) is PdfNumber wn ? wn.IntValue : 0;
        int h = (dict["H"] ?? dict["Height"]) is PdfNumber hn ? hn.IntValue : 0;
        bool mask = (dict["IM"] ?? dict["ImageMask"]) is PdfBool { Value: true };
        int bpc = mask ? 1 : (dict["BPC"] ?? dict["BitsPerComponent"]) is PdfNumber bn ? bn.IntValue : 8;
        int comps = 1;
        var cs = dict["CS"] ?? dict["ColorSpace"];
        if (!mask && cs is PdfName csn)
            comps = csn.Value switch { "RGB" or "DeviceRGB" => 3, "CMYK" or "DeviceCMYK" => 4, _ => 1 };
        else if (!mask && cs is PdfArray) return -1;
        if (w <= 0 || h <= 0 || bpc <= 0) return -1;
        return (w * comps * bpc + 7) / 8 * h;
    }
}

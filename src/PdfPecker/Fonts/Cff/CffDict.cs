using System.Globalization;
using System.Text;

namespace PdfPecker.Fonts.Cff;

internal enum CffDictToken { Operand, Operator, Reserved }

/// <summary>
/// Reads the tokens of a CFF or CFF2 DICT one at a time. Consumers that rewrite a DICT need each entry's raw bytes, and
/// consumers that only look values up want a dictionary, so both are built from this one tokenizer.
/// </summary>
internal struct CffDictReader
{
    private readonly byte[] _d;
    private readonly int _end;
    private readonly bool _cff2;

    /// <summary>The next byte to read; after the last token it may lie past the end when that token was cut off.</summary>
    public int Position;
    public CffDictToken Kind;
    public double Operand;
    public bool IsReal;

    /// <summary>The operator; an escaped one (12 x) is <see cref="CffDict.Escape"/> + x.</summary>
    public int Operator;

    public CffDictReader(byte[] d, int start, int end, bool cff2)
    {
        _d = d;
        Position = start;
        _end = end;
        _cff2 = cff2;
        Kind = default;
        Operand = 0;
        IsReal = false;
        Operator = 0;
    }

    public bool Next()
    {
        if (Position >= _end) return false;
        var d = _d;
        int p = Position;
        int b = d[p];
        IsReal = false;
        // CFF2 adds the operators vsindex (22), blend (23) and vstore (24); in CFF they are reserved.
        if (b <= 21 || _cff2 && b <= 24)
        {
            Kind = CffDictToken.Operator;
            Operator = b == 12 ? CffDict.Escape + d[p + 1] : b;
            p += b == 12 ? 2 : 1;
        }
        else
        {
            Kind = CffDictToken.Operand;
            if (b == 28) { Operand = (short)(d[p + 1] << 8 | d[p + 2]); p += 3; }
            else if (b == 29) { Operand = d[p + 1] << 24 | d[p + 2] << 16 | d[p + 3] << 8 | d[p + 4]; p += 5; }
            else if (b == 30) { Operand = ReadReal(ref p); IsReal = true; }
            else if (b >= 32 && b <= 246) { Operand = b - 139; p++; }
            else if (b >= 247 && b <= 250) { Operand = (b - 247) * 256 + d[p + 1] + 108; p += 2; }
            else if (b >= 251 && b <= 254) { Operand = -(b - 251) * 256 - d[p + 1] - 108; p += 2; }
            else { Kind = CffDictToken.Reserved; p++; }
        }
        Position = p;
        return true;
    }

    /// <summary>
    /// A real number: decimal nibbles up to an 0xF nibble. In CFF a real cut off by the end of the DICT keeps the digits
    /// read so far; the CFF2 reader has always read on into the following bytes and rejected a real that never ends.
    /// </summary>
    private double ReadReal(ref int p)
    {
        var d = _d;
        int limit = _cff2 ? d.Length : _end;
        p++;
        var s = new StringBuilder();
        bool done = false;
        while (!done && p < limit)
        {
            int b = d[p++];
            for (int half = 0; half < 2 && !done; half++)
            {
                int nib = half == 0 ? b >> 4 : b & 15;
                switch (nib)
                {
                    case <= 9: s.Append((char)('0' + nib)); break;
                    case 0xA: s.Append('.'); break;
                    case 0xB: s.Append('E'); break;
                    case 0xC: s.Append("E-"); break;
                    case 0xE: s.Append('-'); break;
                    case 0xF: done = true; break;
                }
            }
        }
        if (!done && _cff2) throw new FormatException("Bad CFF2 DICT real number.");
        return double.TryParse(s.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

/// <summary>Reading CFF and CFF2 DICTs into operator → operands maps, and encoding DICT entries.</summary>
internal static class CffDict
{
    /// <summary>Escaped operators (12 x) are keyed as this + x.</summary>
    public const int Escape = 1200;

    /// <summary>
    /// A CFF DICT as operator → operands; a repeated operator keeps its last operands. Reserved bytes are skipped, so a
    /// damaged DICT still yields what it can, which suits rendering fonts found in arbitrary PDF files.
    /// </summary>
    public static Dictionary<int, double[]> Parse(byte[] d, int start, int end) => Parse(d, start, end, false, null);

    /// <summary>A CFF2 DICT as operator → operands, with blends resolved through the store; reserved bytes are an error.</summary>
    public static Dictionary<int, double[]> ParseCff2(byte[] d, int start, int end, VariationStore? store)
        => Parse(d, start, end, true, store);

    private static Dictionary<int, double[]> Parse(byte[] d, int start, int end, bool cff2, VariationStore? store)
    {
        var result = new Dictionary<int, double[]>();
        var stack = new List<double>();
        int vsindex = 0;
        var r = new CffDictReader(d, start, end, cff2);
        while (r.Next())
        {
            if (r.Kind == CffDictToken.Operand) { stack.Add(r.Operand); continue; }
            if (r.Kind == CffDictToken.Reserved)
            {
                if (cff2) throw new FormatException("Bad CFF2 DICT.");
                continue;
            }
            int op = r.Operator;
            if (cff2 && op == 23) // blend: n defaults, then n × regions deltas, then n
            {
                int count = (int)stack[^1];
                var scalars = store?.RegionScalars(vsindex) ?? Array.Empty<float>();
                stack.RemoveAt(stack.Count - 1);
                int first = stack.Count - count * (scalars.Length + 1);
                if (first < 0) throw new FormatException("Bad blend in a CFF2 DICT.");
                for (int i = 0; i < count; i++)
                    for (int k = 0; k < scalars.Length; k++)
                        stack[first + i] += stack[first + count + i * scalars.Length + k] * scalars[k];
                stack.RemoveRange(first + count, stack.Count - first - count);
                continue;
            }
            if (cff2 && op == 22 && stack.Count > 0) vsindex = (int)stack[^1];
            result[op] = stack.ToArray();
            stack.Clear();
        }
        return result;
    }

    /// <summary>
    /// Writes an integer in its five-byte form, so a DICT holding offsets has the same size before and after the
    /// offsets are known.
    /// </summary>
    public static void WriteInt32(List<byte> o, int v)
    {
        o.Add(29);
        o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
    }

    public static void WriteOperator(List<byte> o, int op)
    {
        if (op >= Escape) { o.Add(12); o.Add((byte)(op - Escape)); }
        else o.Add((byte)op);
    }
}

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
    // Bytes up to this one are operators in CFF; CFF2 adds vsindex (22), blend (23) and vstore (24), which are
    // reserved in CFF.
    private const int LastCffOperator = 21, LastCff2Operator = 24;
    private const int EscapeOperator = 12;

    // The first byte of an operand says how it is encoded.
    private const int ShortIntOperand = 28, LongIntOperand = 29, RealOperand = 30;
    private const int SmallIntFirst = 32, SmallIntLast = 246, SmallIntBias = 139;
    private const int PositiveFirst = 247, PositiveLast = 250, NegativeFirst = 251, NegativeLast = 254;
    private const int TwoByteBias = 108;

    // The nibbles of a real number.
    private const int DecimalPoint = 0xA, Exponent = 0xB, NegativeExponent = 0xC, Minus = 0xE, EndOfNumber = 0xF;

    private readonly byte[] _data;
    private readonly int _end;
    private readonly bool _cff2;

    public CffDictReader(byte[] data, int start, int end, bool cff2)
    {
        _data = data;
        Position = start;
        _end = end;
        _cff2 = cff2;
    }

    /// <summary>The next byte to read; after the last token it may lie past the end when that token was cut off.</summary>
    public int Position { get; private set; }

    public CffDictToken Kind { get; private set; }

    public double Operand { get; private set; }

    public bool IsReal { get; private set; }

    /// <summary>The operator; an escaped one (12 x) is <see cref="CffDict.Escape"/> + x.</summary>
    public int Operator { get; private set; }

    public bool Next()
    {
        if (Position >= _end) return false;
        int at = Position;
        int first = _data[at];
        IsReal = false;
        if (first <= LastCffOperator || _cff2 && first <= LastCff2Operator)
        {
            Kind = CffDictToken.Operator;
            bool escaped = first == EscapeOperator;
            Operator = escaped ? CffDict.Escape + _data[at + 1] : first;
            Position = at + (escaped ? 2 : 1);
        }
        else
        {
            Kind = CffDictToken.Operand;
            Position = ReadOperand(first, at);
        }
        return true;
    }

    // Decodes the operand that starts at the byte and returns the position after it.
    private int ReadOperand(int first, int at)
    {
        var d = _data;
        switch (first)
        {
            case ShortIntOperand:
                Operand = (short)(d[at + 1] << 8 | d[at + 2]);
                return at + 3;
            case LongIntOperand:
                Operand = d[at + 1] << 24 | d[at + 2] << 16 | d[at + 3] << 8 | d[at + 4];
                return at + 5;
            case RealOperand:
                int afterReal = ReadReal(at);
                IsReal = true;
                return afterReal;
            case >= SmallIntFirst and <= SmallIntLast:
                Operand = first - SmallIntBias;
                return at + 1;
            case >= PositiveFirst and <= PositiveLast:
                Operand = (first - PositiveFirst) * 256 + d[at + 1] + TwoByteBias;
                return at + 2;
            case >= NegativeFirst and <= NegativeLast:
                Operand = -(first - NegativeFirst) * 256 - d[at + 1] - TwoByteBias;
                return at + 2;
            default:
                Kind = CffDictToken.Reserved;
                return at + 1;
        }
    }

    /// <summary>
    /// A real number: decimal nibbles up to an 0xF nibble. In CFF a real cut off by the end of the DICT keeps the digits
    /// read so far; the CFF2 reader has always read on into the following bytes and rejected a real that never ends.
    /// </summary>
    private int ReadReal(int at)
    {
        int limit = _cff2 ? _data.Length : _end;
        int p = at + 1;
        var text = new StringBuilder();
        bool done = false;
        while (!done && p < limit)
        {
            int b = _data[p++];
            done = AppendNibble(text, b >> 4) || AppendNibble(text, b & 15);
        }
        if (!done && _cff2) throw new FormatException("Bad CFF2 DICT real number.");
        Operand = double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return p;
    }

    // Appends what the nibble stands for; true when it ends the number. The reserved nibble 0xD adds nothing.
    private static bool AppendNibble(StringBuilder text, int nibble)
    {
        switch (nibble)
        {
            case <= 9: text.Append((char)('0' + nibble)); break;
            case DecimalPoint: text.Append('.'); break;
            case Exponent: text.Append('E'); break;
            case NegativeExponent: text.Append("E-"); break;
            case Minus: text.Append('-'); break;
            case EndOfNumber: return true;
        }
        return false;
    }
}

/// <summary>Reading CFF and CFF2 DICTs into operator → operands maps, and encoding DICT entries.</summary>
internal static class CffDict
{
    /// <summary>Escaped operators (12 x) are keyed as this + x.</summary>
    public const int Escape = 1200;

    // CFF2 operators that the DICT parser itself acts on.
    private const int VsindexOperator = 22, BlendOperator = 23;

    private const byte EscapeByte = 12, LongIntByte = 29;

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
        var entries = new Dictionary<int, double[]>();
        var operands = new List<double>();
        int vsindex = 0;
        var reader = new CffDictReader(d, start, end, cff2);
        while (reader.Next())
        {
            if (reader.Kind == CffDictToken.Operand)
            {
                operands.Add(reader.Operand);
                continue;
            }
            if (reader.Kind == CffDictToken.Reserved)
            {
                if (cff2) throw new FormatException("Bad CFF2 DICT.");
                continue;
            }
            int op = reader.Operator;
            if (cff2 && op == BlendOperator)
            {
                ResolveBlend(operands, store, vsindex);
                continue;
            }
            if (cff2 && op == VsindexOperator && operands.Count > 0) vsindex = (int)operands[^1];
            entries[op] = operands.ToArray();
            operands.Clear();
        }
        return entries;
    }

    // A blend operator takes n default values, then n × regions deltas, then n. It leaves the n values with the
    // deltas, weighted by the region scalars, added in.
    private static void ResolveBlend(List<double> operands, VariationStore? store, int vsindex)
    {
        int count = (int)operands[^1];
        var scalars = store?.RegionScalars(vsindex) ?? Array.Empty<float>();
        operands.RemoveAt(operands.Count - 1);
        int first = operands.Count - count * (scalars.Length + 1);
        if (first < 0) throw new FormatException("Bad blend in a CFF2 DICT.");
        for (int i = 0; i < count; i++)
            for (int k = 0; k < scalars.Length; k++)
                operands[first + i] += operands[first + count + i * scalars.Length + k] * scalars[k];
        operands.RemoveRange(first + count, operands.Count - first - count);
    }

    /// <summary>
    /// Writes an integer in its five-byte form, so a DICT holding offsets has the same size before and after the
    /// offsets are known.
    /// </summary>
    public static void WriteInt32(List<byte> o, int v)
    {
        o.Add(LongIntByte);
        o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
    }

    public static void WriteOperator(List<byte> o, int op)
    {
        if (op >= Escape) { o.Add(EscapeByte); o.Add((byte)(op - Escape)); }
        else o.Add((byte)op);
    }
}

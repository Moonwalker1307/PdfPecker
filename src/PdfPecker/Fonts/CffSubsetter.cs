using System.Buffers.Binary;

namespace PdfPecker.Fonts;

/// <summary>
/// Shrinks a CFF font program (the outlines of an OpenType .otf font) to the glyphs a document uses. Glyph ids are
/// kept, so the charset and FDSelect tables are copied as they are; unused glyphs get an empty charstring. Subroutines
/// are kept whole. Returns null when the font has a structure this does not handle; the caller then embeds it whole.
/// </summary>
internal static class CffSubsetter
{
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18;
    private const int OpFdArray = 1236, OpFdSelect = 1237; // escaped operators 12 36 and 12 37

    public static byte[]? Subset(byte[] cff, ICollection<int> keep)
    {
        try
        {
            return SubsetCore(cff, keep);
        }
        catch (Exception ex) when (FontProgram.IsFormatError(ex) || ex is InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? SubsetCore(byte[] d, ICollection<int> keep)
    {
        if (d.Length < 4 || d[0] != 1) return null; // CFF version 1 only (CFF2 is embedded whole)
        int pos = d[2];
        var name = ReadIndex(d, ref pos);
        var top = ReadIndex(d, ref pos);
        var strings = ReadIndex(d, ref pos);
        var gsubrs = ReadIndex(d, ref pos);
        if (top.Count != 1) return null;
        var topDict = ParseDict(top.Item(0));

        int charStringsOff = Operand(topDict, OpCharStrings, 0);
        if (charStringsOff <= 0) return null;
        int csPos = charStringsOff;
        var charStrings = ReadIndex(d, ref csPos);
        int nGlyphs = charStrings.Count;

        // charset: 0..2 are predefined; otherwise copy its bytes.
        int charsetOff = Operand(topDict, OpCharset, 0);
        byte[]? charset = charsetOff > 2 ? d.AsSpan(charsetOff, CharsetLength(d, charsetOff, nGlyphs)).ToArray() : null;

        bool cid = topDict.Exists(e => e.Op == OpFdArray);
        byte[]? fdSelect = null;
        List<List<DictEntry>>? fdDicts = null;
        var blocks = new List<PrivateBlock>();   // per font DICT: its Private DICT and local subroutines
        var privateSizes = new List<int>();
        if (cid)
        {
            int fdSelectOff = Operand(topDict, OpFdSelect, 0);
            fdSelect = d.AsSpan(fdSelectOff, FdSelectLength(d, fdSelectOff, nGlyphs)).ToArray();
            int fdPos = Operand(topDict, OpFdArray, 0);
            var fdArray = ReadIndex(d, ref fdPos);
            fdDicts = new List<List<DictEntry>>();
            for (int i = 0; i < fdArray.Count; i++)
            {
                var fd = ParseDict(fdArray.Item(i));
                fdDicts.Add(fd);
                AddPrivate(d, fd, blocks, privateSizes);
            }
        }
        else AddPrivate(d, topDict, blocks, privateSizes);

        // New CharStrings: used glyphs keep their program, the rest become a bare endchar.
        var used = new bool[nGlyphs];
        used[0] = true;
        foreach (int g in keep) if ((uint)g < (uint)nGlyphs) used[g] = true;
        var items = new byte[nGlyphs][];
        var endchar = new byte[] { 14 };
        var usage = new SubrUsage(gsubrs, blocks);
        for (int g = 0; g < nGlyphs; g++)
        {
            if (!used[g]) { items[g] = endchar; continue; }
            var cs = charStrings.Item(g);
            items[g] = cs.ToArray();
            usage.Glyph(cs, fdSelect != null ? FdOf(fdSelect, g) : 0);
        }
        var newCharStrings = WriteIndex(items);

        // Subroutines no used glyph calls become empty; their numbering (and so the charstrings) stays the same.
        var privates = new List<byte[]>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Subrs is not { } local) { privates.Add(b.Prefix); continue; }
            var subrs = Prune(local, usage.LocalUsed[i]);
            var block = new byte[b.Prefix.Length + subrs.Length];
            b.Prefix.CopyTo(block, 0);
            subrs.CopyTo(block, b.Prefix.Length);
            privates.Add(block);
        }

        // Layout: header, Name, Top DICT, Strings, Global Subrs, charset, FDSelect, CharStrings, FDArray, Privates.
        var headerAndIndexes = new List<byte[]> { d.AsSpan(0, d[2]).ToArray(), name.Raw(d), null!, strings.Raw(d), Prune(gsubrs, usage.GlobalUsed) };
        byte[] EncodeTop(int charsetAt, int fdSelectAt, int charStringsAt, int fdArrayAt, int privateAt)
        {
            var o = new List<byte>(128);
            foreach (var e in topDict)
            {
                switch (e.Op)
                {
                    case OpEncoding: continue; // OpenType CFF fonts are encoded through the cmap; CIDs are glyph ids here
                    case OpCharset: if (charset == null) { o.AddRange(e.Raw); continue; } Int5(o, charsetAt); break;
                    case OpFdSelect: Int5(o, fdSelectAt); break;
                    case OpCharStrings: Int5(o, charStringsAt); break;
                    case OpFdArray: Int5(o, fdArrayAt); break;
                    case OpPrivate:
                        if (cid) continue;
                        Int5(o, privateSizes[0]);
                        Int5(o, privateAt);
                        break;
                    default: o.AddRange(e.Raw); continue;
                }
                Operator(o, e.Op);
            }
            return o.ToArray();
        }

        int topSize = WriteIndex(new[] { EncodeTop(0, 0, 0, 0, 0) }).Length; // offsets are fixed-size, so the size is final
        int at = headerAndIndexes[0].Length + headerAndIndexes[1].Length + topSize + headerAndIndexes[3].Length + headerAndIndexes[4].Length;
        int charsetAt = at; at += charset?.Length ?? 0;
        int fdSelectAt = at; at += fdSelect?.Length ?? 0;
        int charStringsAt = at; at += newCharStrings.Length;
        int fdArrayAt = at;
        byte[]? newFdArray = null;
        var privateAt = new int[privates.Count];
        if (cid)
        {
            // FD dicts point at their Private DICTs, which follow the FDArray; the FDArray's size does not depend on them.
            var fdItems = fdDicts!.Select(fd => EncodeFd(fd, 0, 0)).ToArray();
            int fdArraySize = WriteIndex(fdItems).Length;
            at += fdArraySize;
            for (int i = 0; i < privates.Count; i++) { privateAt[i] = at; at += privates[i].Length; }
            int k = 0;
            fdItems = fdDicts!.Select(fd => EncodeFd(fd, privateSizes[k], privateAt[k++])).ToArray();
            newFdArray = WriteIndex(fdItems);
        }
        else if (privates.Count > 0)
        {
            privateAt[0] = at;
            at += privates[0].Length;
        }

        headerAndIndexes[2] = WriteIndex(new[] { EncodeTop(charsetAt, fdSelectAt, charStringsAt, fdArrayAt, privates.Count > 0 ? privateAt[0] : 0) });
        var result = new byte[at];
        int w = 0;
        foreach (var part in headerAndIndexes) { part.CopyTo(result, w); w += part.Length; }
        if (charset != null) { charset.CopyTo(result, w); w += charset.Length; }
        if (fdSelect != null) { fdSelect.CopyTo(result, w); w += fdSelect.Length; }
        newCharStrings.CopyTo(result, w); w += newCharStrings.Length;
        if (newFdArray != null) { newFdArray.CopyTo(result, w); w += newFdArray.Length; }
        foreach (var p in privates) { p.CopyTo(result, w); w += p.Length; }
        if (w != at) throw new InvalidOperationException("CFF layout mismatch.");
        return result;
    }

    private static byte[] EncodeFd(List<DictEntry> fd, int privateSize, int privateAt)
    {
        var o = new List<byte>(32);
        foreach (var e in fd)
        {
            if (e.Op != OpPrivate) { o.AddRange(e.Raw); continue; }
            Int5(o, privateSize);
            Int5(o, privateAt);
            Operator(o, OpPrivate);
        }
        return o.ToArray();
    }

    /// <summary>A Private DICT and the bytes up to its local subroutines (kept at the same relative offset), and the subroutines.</summary>
    private sealed class PrivateBlock
    {
        public byte[] Prefix = Array.Empty<byte>();
        public Index? Subrs;
    }

    private static void AddPrivate(byte[] d, List<DictEntry> dict, List<PrivateBlock> blocks, List<int> sizes)
    {
        var block = new PrivateBlock();
        blocks.Add(block);
        var e = dict.Find(x => x.Op == OpPrivate);
        if (e.Raw == null || e.Operands.Count < 2) { sizes.Add(0); return; }
        int size = (int)e.Operands[0], off = (int)e.Operands[1];
        sizes.Add(size);
        var priv = ParseDict(d.AsSpan(off, size));
        int subrs = Operand(priv, 19, 0); // Subrs, relative to the Private DICT
        if (subrs >= size)
        {
            int p = off + subrs;
            block.Subrs = ReadIndex(d, ref p);
            block.Prefix = d.AsSpan(off, subrs).ToArray();
        }
        else block.Prefix = d.AsSpan(off, size).ToArray();
    }

    private static byte[] Prune(Index index, bool[] used)
    {
        var items = new byte[index.Count][];
        for (int i = 0; i < items.Length; i++) items[i] = used[i] ? index.Item(i).ToArray() : Array.Empty<byte>();
        return WriteIndex(items);
    }

    private static int FdOf(byte[] fdSelect, int gid)
    {
        if (fdSelect[0] == 0) return fdSelect[1 + gid];
        int n = fdSelect[1] << 8 | fdSelect[2];
        for (int i = 0; i < n; i++)
        {
            int p = 3 + i * 3;
            int first = fdSelect[p] << 8 | fdSelect[p + 1];
            int next = fdSelect[p + 3] << 8 | fdSelect[p + 4]; // the next range's first glyph, or the sentinel
            if (gid >= first && gid < next) return fdSelect[p + 2];
        }
        return 0;
    }

    /// <summary>Finds the subroutines used by a set of Type 2 charstrings by walking their operators.</summary>
    private sealed class SubrUsage
    {
        private readonly Index _global;
        private readonly List<PrivateBlock> _blocks;
        public readonly bool[] GlobalUsed;
        public readonly bool[][] LocalUsed;
        private readonly double[] _stack = new double[64];
        private int _sp, _stems;
        private bool _done;

        // Subroutines that each call the next many times expand exponentially within the nesting limit, so the walk is
        // bounded per glyph (as the renderer bounds its charstrings) and per font, since a document may use thousands
        // of glyphs. A font past either bound is not subset but embedded whole, which keeps every subroutine.
        private const int GlyphBudget = 200_000, FontBudget = 50_000_000;
        private int _glyphBudget, _fontBudget = FontBudget;

        public SubrUsage(Index global, List<PrivateBlock> blocks)
        {
            _global = global;
            _blocks = blocks;
            GlobalUsed = new bool[global.Count];
            LocalUsed = blocks.Select(b => new bool[b.Subrs?.Count ?? 0]).ToArray();
        }

        private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        public void Glyph(ReadOnlySpan<byte> cs, int fd)
        {
            _sp = 0;
            _stems = 0;
            _done = false;
            _glyphBudget = GlyphBudget;
            if ((uint)fd >= (uint)_blocks.Count) fd = 0;
            Run(cs, fd, 0);
        }

        private void Run(ReadOnlySpan<byte> cs, int fd, int depth)
        {
            if (depth > 10) return; // the Type 2 limit on subroutine nesting
            int i = 0;
            while (i < cs.Length && !_done)
            {
                if (--_glyphBudget < 0 || --_fontBudget < 0) throw new InvalidOperationException("CFF charstrings call too many subroutines.");
                int b = cs[i];
                if (b >= 32 || b == 28)
                {
                    double v;
                    if (b == 28) { v = (short)(cs[i + 1] << 8 | cs[i + 2]); i += 3; }
                    else if (b <= 246) { v = b - 139; i++; }
                    else if (b <= 250) { v = (b - 247) * 256 + cs[i + 1] + 108; i += 2; }
                    else if (b <= 254) { v = -(b - 251) * 256 - cs[i + 1] - 108; i += 2; }
                    else { v = BinaryPrimitives.ReadInt32BigEndian(cs[(i + 1)..]) / 65536.0; i += 5; }
                    if (_sp < _stack.Length) _stack[_sp++] = v;
                    continue;
                }
                i++;
                switch (b)
                {
                    case 1: case 3: case 18: case 23: // hstem, vstem, hstemhm, vstemhm
                        _stems += _sp / 2;
                        _sp = 0;
                        break;
                    case 19: case 20: // hintmask, cntrmask: an implicit vstem may precede them
                        _stems += _sp / 2;
                        _sp = 0;
                        i += (_stems + 7) / 8;
                        break;
                    case 10: // callsubr
                    {
                        var local = _blocks[fd].Subrs;
                        if (_sp == 0 || local is not { } subrs) { _sp = 0; break; }
                        int n = (int)_stack[--_sp] + Bias(subrs.Count);
                        if ((uint)n < (uint)subrs.Count)
                        {
                            LocalUsed[fd][n] = true;
                            Run(subrs.Item(n), fd, depth + 1);
                        }
                        break;
                    }
                    case 29: // callgsubr
                    {
                        if (_sp == 0) break;
                        int n = (int)_stack[--_sp] + Bias(_global.Count);
                        if ((uint)n < (uint)_global.Count)
                        {
                            GlobalUsed[n] = true;
                            Run(_global.Item(n), fd, depth + 1);
                        }
                        break;
                    }
                    case 11: // return
                        return;
                    case 14: // endchar
                        _done = true;
                        return;
                    case 12: // escape: two-byte operators (flex and arithmetic) consume their operands
                        i++;
                        _sp = 0;
                        break;
                    default:
                        _sp = 0;
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ INDEX

    private readonly struct Index
    {
        public readonly int Start, End, Count, OffSize, DataStart;
        private readonly byte[] _d;

        public Index(byte[] d, int start, int end, int count, int offSize, int dataStart)
        {
            _d = d; Start = start; End = end; Count = count; OffSize = offSize; DataStart = dataStart;
        }

        private int Offset(int i)
        {
            int p = Start + 3 + i * OffSize, v = 0;
            for (int k = 0; k < OffSize; k++) v = v << 8 | _d[p + k];
            return v;
        }

        public ReadOnlySpan<byte> Item(int i)
        {
            int a = Offset(i), b = Offset(i + 1);
            if (a < 1 || b < a || DataStart + b - 1 > End) throw new FormatException("Damaged CFF INDEX.");
            return _d.AsSpan(DataStart + a - 1, b - a);
        }

        public byte[] Raw(byte[] d) => d.AsSpan(Start, End - Start).ToArray();
    }

    private static Index ReadIndex(byte[] d, ref int pos)
    {
        int start = pos;
        int count = d[pos] << 8 | d[pos + 1];
        if (count == 0) { pos += 2; return new Index(d, start, pos, 0, 1, pos); }
        int offSize = d[pos + 2];
        if (offSize < 1 || offSize > 4) throw new FormatException("Damaged CFF INDEX.");
        int last = 0;
        for (int k = 0; k < offSize; k++) last = last << 8 | d[pos + 3 + count * offSize + k];
        int dataStart = pos + 3 + (count + 1) * offSize;
        pos = dataStart + last - 1;
        if (pos > d.Length || pos < dataStart) throw new FormatException("Damaged CFF INDEX.");
        return new Index(d, start, pos, count, offSize, dataStart);
    }

    private static byte[] WriteIndex(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0) return new byte[2];
        int total = 1;
        foreach (var it in items) total += it.Length;
        int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        var o = new byte[3 + (items.Count + 1) * offSize + total - 1];
        BinaryPrimitives.WriteUInt16BigEndian(o, (ushort)items.Count);
        o[2] = (byte)offSize;
        int off = 1, p = 3, data = 3 + (items.Count + 1) * offSize;
        for (int i = 0; i <= items.Count; i++)
        {
            for (int k = offSize - 1; k >= 0; k--) o[p++] = (byte)(off >> (8 * k));
            if (i < items.Count)
            {
                items[i].CopyTo(o, data);
                data += items[i].Length;
                off += items[i].Length;
            }
        }
        return o;
    }

    // ------------------------------------------------------------------ DICT

    private struct DictEntry
    {
        public int Op;
        public List<double> Operands;
        public byte[] Raw; // operands and operator as they were encoded
    }

    private static List<DictEntry> ParseDict(ReadOnlySpan<byte> dict)
    {
        var entries = new List<DictEntry>();
        var operands = new List<double>();
        int start = 0, i = 0;
        while (i < dict.Length)
        {
            int b = dict[i];
            if (b <= 21)
            {
                int op = b;
                i++;
                if (b == 12) op = 1200 + dict[i++];
                entries.Add(new DictEntry { Op = op, Operands = operands, Raw = dict[start..i].ToArray() });
                operands = new List<double>();
                start = i;
            }
            else if (b == 28) { operands.Add((short)(dict[i + 1] << 8 | dict[i + 2])); i += 3; }
            else if (b == 29) { operands.Add(BinaryPrimitives.ReadInt32BigEndian(dict[(i + 1)..])); i += 5; }
            else if (b == 30)
            {
                // Real number: nibbles until 0xf; the value is not needed for the operators rewritten here.
                i++;
                while (i < dict.Length)
                {
                    int v = dict[i++];
                    if ((v & 0x0F) == 0x0F || (v >> 4) == 0x0F) break;
                }
                operands.Add(0);
            }
            else if (b >= 32 && b <= 246) { operands.Add(b - 139); i++; }
            else if (b >= 247 && b <= 250) { operands.Add((b - 247) * 256 + dict[i + 1] + 108); i += 2; }
            else if (b >= 251 && b <= 254) { operands.Add(-(b - 251) * 256 - dict[i + 1] - 108); i += 2; }
            else throw new FormatException("Damaged CFF DICT.");
        }
        return entries;
    }

    private static int Operand(List<DictEntry> dict, int op, int index)
    {
        foreach (var e in dict)
            if (e.Op == op && e.Operands.Count > index) return (int)e.Operands[index];
        return -1;
    }

    private static void Int5(List<byte> o, int v)
    {
        o.Add(29);
        o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
    }

    private static void Operator(List<byte> o, int op)
    {
        if (op >= 1200) { o.Add(12); o.Add((byte)(op - 1200)); }
        else o.Add((byte)op);
    }

    // ------------------------------------------------------------------ charset and FDSelect sizes

    private static int CharsetLength(byte[] d, int off, int nGlyphs)
    {
        int format = d[off], p = off + 1, covered = 1;
        switch (format)
        {
            case 0: return 1 + (nGlyphs - 1) * 2;
            case 1:
            case 2:
                while (covered < nGlyphs)
                {
                    int left = format == 1 ? d[p + 2] : d[p + 2] << 8 | d[p + 3];
                    p += format == 1 ? 3 : 4;
                    covered += left + 1;
                }
                return p - off;
            default: throw new FormatException("Unknown CFF charset format.");
        }
    }

    private static int FdSelectLength(byte[] d, int off, int nGlyphs)
    {
        int format = d[off];
        if (format == 0) return 1 + nGlyphs;
        if (format == 3) return 1 + 2 + (d[off + 1] << 8 | d[off + 2]) * 3 + 2;
        throw new FormatException("Unknown CFF FDSelect format.");
    }
}

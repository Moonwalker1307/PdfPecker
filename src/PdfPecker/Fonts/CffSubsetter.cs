using PdfPecker.Fonts.Cff;

namespace PdfPecker.Fonts;

/// <summary>
/// Shrinks a CFF font program (the outlines of an OpenType .otf font) to the glyphs a document uses. Glyph ids are
/// kept, so the charset and FDSelect tables are copied as they are; unused glyphs get an empty charstring. Subroutines
/// are kept whole. Returns null when the font has a structure this does not handle; the caller then embeds it whole.
/// </summary>
internal static class CffSubsetter
{
    private const int OpCharset = 15, OpEncoding = 16, OpCharStrings = 17, OpPrivate = 18;
    private const int OpFdArray = CffDict.Escape + 36, OpFdSelect = CffDict.Escape + 37;

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
        var name = CffIndex.Read(d, ref pos);
        var top = CffIndex.Read(d, ref pos);
        var strings = CffIndex.Read(d, ref pos);
        var gsubrs = CffIndex.Read(d, ref pos);
        if (top.Count != 1) return null;
        var topDict = ParseDict(d, top.CheckedRange(0));

        int charStringsOff = Operand(topDict, OpCharStrings, 0);
        if (charStringsOff <= 0) return null;
        int csPos = charStringsOff;
        var charStrings = CffIndex.Read(d, ref csPos);
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
            var fdArray = CffIndex.Read(d, ref fdPos);
            fdDicts = new List<List<DictEntry>>();
            for (int i = 0; i < fdArray.Count; i++)
            {
                var fd = ParseDict(d, fdArray.CheckedRange(i));
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
        var usage = new SubrUsage(d, gsubrs, blocks);
        for (int g = 0; g < nGlyphs; g++)
        {
            if (!used[g]) { items[g] = endchar; continue; }
            var cs = charStrings.Item(d, g);
            items[g] = cs.ToArray();
            usage.Glyph(cs, fdSelect != null ? FdOf(fdSelect, g) : 0);
        }
        var newCharStrings = CffIndex.Write(items);

        // Subroutines no used glyph calls become empty; their numbering (and so the charstrings) stays the same.
        var privates = new List<byte[]>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Subrs is not { } local) { privates.Add(b.Prefix); continue; }
            var subrs = Prune(d, local, usage.LocalUsed[i]);
            var block = new byte[b.Prefix.Length + subrs.Length];
            b.Prefix.CopyTo(block, 0);
            subrs.CopyTo(block, b.Prefix.Length);
            privates.Add(block);
        }

        // Layout: header, Name, Top DICT, Strings, Global Subrs, charset, FDSelect, CharStrings, FDArray, Privates.
        var headerAndIndexes = new List<byte[]> { d.AsSpan(0, d[2]).ToArray(), name.Raw(d), null!, strings.Raw(d), Prune(d, gsubrs, usage.GlobalUsed) };
        byte[] EncodeTop(int charsetAt, int fdSelectAt, int charStringsAt, int fdArrayAt, int privateAt)
        {
            var o = new List<byte>(128);
            foreach (var e in topDict)
            {
                switch (e.Op)
                {
                    case OpEncoding: continue; // OpenType CFF fonts are encoded through the cmap; CIDs are glyph ids here
                    case OpCharset: if (charset == null) { o.AddRange(e.Raw); continue; } CffDict.WriteInt32(o, charsetAt); break;
                    case OpFdSelect: CffDict.WriteInt32(o, fdSelectAt); break;
                    case OpCharStrings: CffDict.WriteInt32(o, charStringsAt); break;
                    case OpFdArray: CffDict.WriteInt32(o, fdArrayAt); break;
                    case OpPrivate:
                        if (cid) continue;
                        CffDict.WriteInt32(o, privateSizes[0]);
                        CffDict.WriteInt32(o, privateAt);
                        break;
                    default: o.AddRange(e.Raw); continue;
                }
                CffDict.WriteOperator(o, e.Op);
            }
            return o.ToArray();
        }

        int topSize = CffIndex.Write(new[] { EncodeTop(0, 0, 0, 0, 0) }).Length; // offsets are fixed-size, so the size is final
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
            int fdArraySize = CffIndex.Write(fdItems).Length;
            at += fdArraySize;
            for (int i = 0; i < privates.Count; i++) { privateAt[i] = at; at += privates[i].Length; }
            int k = 0;
            fdItems = fdDicts!.Select(fd => EncodeFd(fd, privateSizes[k], privateAt[k++])).ToArray();
            newFdArray = CffIndex.Write(fdItems);
        }
        else if (privates.Count > 0)
        {
            privateAt[0] = at;
            at += privates[0].Length;
        }

        headerAndIndexes[2] = CffIndex.Write(new[] { EncodeTop(charsetAt, fdSelectAt, charStringsAt, fdArrayAt, privates.Count > 0 ? privateAt[0] : 0) });
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
            CffDict.WriteInt32(o, privateSize);
            CffDict.WriteInt32(o, privateAt);
            CffDict.WriteOperator(o, OpPrivate);
        }
        return o.ToArray();
    }

    /// <summary>A Private DICT and the bytes up to its local subroutines (kept at the same relative offset), and the subroutines.</summary>
    private sealed class PrivateBlock
    {
        public byte[] Prefix = Array.Empty<byte>();
        public CffIndex? Subrs;
    }

    private static void AddPrivate(byte[] d, List<DictEntry> dict, List<PrivateBlock> blocks, List<int> sizes)
    {
        var block = new PrivateBlock();
        blocks.Add(block);
        var e = dict.Find(x => x.Op == OpPrivate);
        if (e.Raw == null || e.Operands.Count < 2) { sizes.Add(0); return; }
        int size = (int)e.Operands[0], off = (int)e.Operands[1];
        sizes.Add(size);
        if ((uint)off > (uint)d.Length || (uint)size > (uint)(d.Length - off)) throw new FormatException("Damaged CFF Private DICT.");
        var priv = ParseDict(d, (off, off + size));
        int subrs = Operand(priv, 19, 0); // Subrs, relative to the Private DICT
        if (subrs >= size)
        {
            int p = off + subrs;
            block.Subrs = CffIndex.Read(d, ref p);
            block.Prefix = d.AsSpan(off, subrs).ToArray();
        }
        else block.Prefix = d.AsSpan(off, size).ToArray();
    }

    private static byte[] Prune(byte[] d, CffIndex index, bool[] used)
    {
        var items = new byte[index.Count][];
        for (int i = 0; i < items.Length; i++) items[i] = used[i] ? index.Item(d, i).ToArray() : Array.Empty<byte>();
        return CffIndex.Write(items);
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
        private readonly byte[] _d;
        private readonly CffIndex _global;
        private readonly List<PrivateBlock> _blocks;
        public readonly bool[] GlobalUsed;
        public readonly bool[][] LocalUsed;
        private readonly Type2Stack _stack = new(64);
        private int _stems;
        private bool _done;

        // The walk is bounded per glyph and per font, since a document may use thousands of glyphs. A font past either
        // bound is not subset but embedded whole, which keeps every subroutine.
        private CharstringBudget _budget = new(50_000_000);

        public SubrUsage(byte[] d, CffIndex global, List<PrivateBlock> blocks)
        {
            _d = d;
            _global = global;
            _blocks = blocks;
            GlobalUsed = new bool[global.Count];
            LocalUsed = blocks.Select(b => new bool[b.Subrs?.Count ?? 0]).ToArray();
        }

        public void Glyph(ReadOnlySpan<byte> cs, int fd)
        {
            _stack.Clear();
            _stems = 0;
            _done = false;
            _budget.StartGlyph();
            if ((uint)fd >= (uint)_blocks.Count) fd = 0;
            Run(cs, fd, 0);
        }

        private void Run(ReadOnlySpan<byte> cs, int fd, int depth)
        {
            if (depth > Type2.MaxSubrDepth) return;
            var stack = _stack;
            int i = 0;
            while (i < cs.Length && !_done)
            {
                if (!_budget.Spend()) throw new InvalidOperationException("CFF charstrings call too many subroutines.");
                int b = cs[i++];
                if (Type2.IsNumber(b))
                {
                    stack.PushNumber(cs, b, ref i);
                    continue;
                }
                switch (b)
                {
                    case 1: case 3: case 18: case 23: // hstem, vstem, hstemhm, vstemhm
                        _stems += stack.Count / 2;
                        stack.Clear();
                        break;
                    case 19: case 20: // hintmask, cntrmask: an implicit vstem may precede them
                        _stems += stack.Count / 2;
                        stack.Clear();
                        i += Type2.MaskBytes(_stems);
                        break;
                    case 10: // callsubr
                    {
                        var local = _blocks[fd].Subrs;
                        if (stack.Count == 0 || local is not { } subrs) { stack.Clear(); break; }
                        int n = (int)stack.Pop() + Type2.SubrBias(subrs.Count);
                        if ((uint)n < (uint)subrs.Count)
                        {
                            LocalUsed[fd][n] = true;
                            Run(subrs.Item(_d, n), fd, depth + 1);
                        }
                        break;
                    }
                    case 29: // callgsubr
                    {
                        if (stack.Count == 0) break;
                        int n = (int)stack.Pop() + Type2.SubrBias(_global.Count);
                        if ((uint)n < (uint)_global.Count)
                        {
                            GlobalUsed[n] = true;
                            Run(_global.Item(_d, n), fd, depth + 1);
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
                        stack.Clear();
                        break;
                    default:
                        stack.Clear();
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ DICT

    private struct DictEntry
    {
        public int Op;
        public List<double> Operands;
        public byte[] Raw; // operands and operator as they were encoded
    }

    /// <summary>
    /// The DICT's entries in order, each with the bytes it was encoded as, so that the entries this does not rewrite are
    /// copied exactly. Real numbers read as 0: none of the operators rewritten here take one.
    /// </summary>
    private static List<DictEntry> ParseDict(byte[] d, (int Start, int End) range)
    {
        var (start, end) = range;
        var entries = new List<DictEntry>();
        var operands = new List<double>();
        var r = new CffDictReader(d, start, end, cff2: false);
        while (r.Next())
        {
            // A token cut off by the end of the DICT, or a reserved byte, is a damaged font.
            if (r.Position > end || r.Kind == CffDictToken.Reserved) throw new FormatException("Damaged CFF DICT.");
            if (r.Kind == CffDictToken.Operand) { operands.Add(r.IsReal ? 0 : r.Operand); continue; }
            entries.Add(new DictEntry { Op = r.Operator, Operands = operands, Raw = d.AsSpan(start, r.Position - start).ToArray() });
            operands = new List<double>();
            start = r.Position;
        }
        return entries;
    }

    private static int Operand(List<DictEntry> dict, int op, int index)
    {
        foreach (var e in dict)
            if (e.Op == op && e.Operands.Count > index) return (int)e.Operands[index];
        return -1;
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

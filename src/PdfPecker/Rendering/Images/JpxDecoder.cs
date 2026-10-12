using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

/// <summary>How the components of a decoded JPEG 2000 image are to be read when the PDF gives no color space.</summary>
internal enum JpxColor { Unknown, Gray, Rgb, Cmyk }

/// <summary>A decoded JPEG 2000 image: interleaved 8-bit color components and an optional alpha channel.</summary>
internal sealed class JpxImage
{
    public int Width, Height, Components;
    public byte[] Data = Array.Empty<byte>();
    public byte[]? Alpha;
    public JpxColor Color;
}

/// <summary>
/// JPEG 2000 (ISO/IEC 15444-1) decoder for JPXDecode image streams: JP2 files and raw codestreams, all progression orders
/// and progression changes, tiles, precincts, packed packet headers, every code-block style, 5/3 and 9/7 wavelets,
/// scalar quantization, ROI shifts, multiple component transforms, subsampled components, palettes, channel definitions
/// and sYCC. Images decode to 8 bits per component.
/// </summary>
internal sealed class JpxDecoder
{
    // ------------------------------------------------------------------ limits

    private const int MaxComponents = 256;
    private const int MaxLevels = 32;
    private const int MaxTiles = 65535;

    // ------------------------------------------------------------------ codestream parameters

    private sealed class ComponentInfo
    {
        public int Precision; // bits
        public bool Signed;
        public int XR, YR;
    }

    private sealed class CodingStyle
    {
        public int Levels;
        public int CbW, CbH; // log2 of the nominal code-block size
        public int CbStyle;
        public bool Reversible;
        public int[] PPx = Array.Empty<int>(), PPy = Array.Empty<int>(); // log2 precinct size per resolution
        public CodingStyle Clone() => (CodingStyle)MemberwiseClone();
    }

    private sealed class Quant
    {
        public int Guard;
        public int Style; // 0 none, 1 scalar derived, 2 scalar expounded
        public int[] Exp = Array.Empty<int>();
        public int[] Mant = Array.Empty<int>();
    }

    private sealed class GlobalStyle
    {
        public bool Sop, Eph;
        public int Progression, Layers, Mct;
    }

    private struct Poc
    {
        public int RS, CS, LYE, RE, CE, Order;
    }

    private sealed class TileData
    {
        public int Index;
        public readonly List<(int Start, int End)> Parts = new();
        public GlobalStyle? Style;
        public CodingStyle? Cod;
        public readonly Dictionary<int, CodingStyle> Coc = new();
        public Quant? Qcd;
        public readonly Dictionary<int, Quant> Qcc = new();
        public readonly Dictionary<int, int> Rgn = new();
        public readonly List<Poc> Pocs = new();
        public readonly SortedDictionary<int, byte[]> Ppt = new();
        public readonly List<byte[]> PpmChunks = new();
        public bool SawFirstPart;
    }

    private readonly byte[] _d;
    private int _xsiz, _ysiz, _x0, _y0, _tw, _th, _tx0, _ty0;
    private ComponentInfo[] _comps = Array.Empty<ComponentInfo>();
    private GlobalStyle _style = new();
    private CodingStyle? _cod;
    private readonly Dictionary<int, CodingStyle> _coc = new();
    private Quant? _qcd;
    private readonly Dictionary<int, Quant> _qcc = new();
    private readonly Dictionary<int, int> _rgn = new();
    private readonly List<Poc> _pocs = new();
    private readonly SortedDictionary<int, byte[]> _ppm = new();
    private Queue<byte[]>? _ppmChunks;

    // Decoded components at their own resolution, 8 bits each.
    private byte[][] _planes = Array.Empty<byte[]>();
    private int[] _planeW = Array.Empty<int>(), _planeH = Array.Empty<int>(), _planeX0 = Array.Empty<int>(), _planeY0 = Array.Empty<int>();

    private JpxDecoder(byte[] d) => _d = d;

    /// <summary>Decodes a JP2 file or raw JPEG 2000 codestream, or returns null when it cannot be read.</summary>
    public static JpxImage? Decode(byte[] data)
    {
        if (data.Length < 4) return null;
        int csStart, csEnd;
        Jp2Header? header = null;
        if (data[0] == 0xFF && data[1] == 0x4F)
        {
            csStart = 0;
            csEnd = data.Length;
        }
        else
        {
            header = new Jp2Header();
            if (!ParseBoxes(data, 0, data.Length, header) || header.CodestreamStart < 0) return null;
            csStart = header.CodestreamStart;
            csEnd = header.CodestreamEnd;
        }
        var dec = new JpxDecoder(data);
        if (!dec.DecodeCodestream(csStart, csEnd)) return null;
        return dec.Compose(header);
    }

    // ------------------------------------------------------------------ JP2 boxes

    private sealed class Jp2Header
    {
        public int CodestreamStart = -1, CodestreamEnd;
        public int EnumCs = -1;
        public int IccComponents;
        public int[][]? Palette; // [entry][column]
        public int[] PaletteBits = Array.Empty<int>();
        public (int Comp, int Type, int Col)[]? Cmap;
        public (int Channel, int Type, int Assoc)[]? Cdef;
    }

    private static bool ParseBoxes(byte[] d, int pos, int end, Jp2Header h)
    {
        for (int guard = 0; pos + 8 <= end && guard < 10000; guard++)
        {
            long len = Be32(d, pos);
            uint type = (uint)Be32(d, pos + 4);
            int hdr = 8;
            if (len == 1)
            {
                if (pos + 16 > end) return false;
                len = (long)((ulong)(uint)Be32(d, pos + 8) << 32 | (uint)Be32(d, pos + 12));
                hdr = 16;
            }
            else if (len == 0) len = end - pos;
            if (len < hdr || pos + len > end) len = end - pos; // tolerate a truncated last box
            int bodyStart = pos + hdr, bodyEnd = (int)(pos + len);
            switch (type)
            {
                case 0x6A703268: // jp2h
                    ParseBoxes(d, bodyStart, bodyEnd, h);
                    break;
                case 0x6A703263: // jp2c
                    if (h.CodestreamStart < 0)
                    {
                        h.CodestreamStart = bodyStart;
                        h.CodestreamEnd = bodyEnd;
                    }
                    break;
                case 0x636F6C72: // colr: the first one counts
                    if (h.EnumCs < 0 && h.IccComponents == 0 && bodyEnd - bodyStart >= 3)
                    {
                        int meth = d[bodyStart];
                        if (meth == 1 && bodyEnd - bodyStart >= 7) h.EnumCs = Be32(d, bodyStart + 3);
                        else if (meth is 2 or 3 && bodyEnd - bodyStart >= 3 + 20)
                        {
                            uint space = (uint)Be32(d, bodyStart + 3 + 16);
                            h.IccComponents = space switch
                            {
                                0x47524159 => 1, // GRAY
                                0x52474220 => 3, // RGB
                                0x434D594B => 4, // CMYK
                                _ => 0,
                            };
                        }
                    }
                    break;
                case 0x70636C72: // pclr
                    if (bodyEnd - bodyStart >= 3)
                    {
                        int ne = Be16(d, bodyStart), npc = d[bodyStart + 2];
                        if (ne is < 1 or > 1024 || npc < 1 || bodyStart + 3 + npc > bodyEnd) break;
                        var bits = new int[npc];
                        for (int i = 0; i < npc; i++) bits[i] = (d[bodyStart + 3 + i] & 0x7F) + 1;
                        var pal = new int[ne][];
                        int p = bodyStart + 3 + npc;
                        for (int e = 0; e < ne; e++)
                        {
                            pal[e] = new int[npc];
                            for (int c = 0; c < npc; c++)
                            {
                                int bytes = (bits[c] + 7) / 8, v = 0;
                                for (int k = 0; k < bytes; k++) v = v << 8 | (p < bodyEnd ? d[p++] : 0);
                                pal[e][c] = v;
                            }
                        }
                        h.Palette = pal;
                        h.PaletteBits = bits;
                    }
                    break;
                case 0x636D6170: // cmap
                {
                    int n = (bodyEnd - bodyStart) / 4;
                    var m = new (int, int, int)[n];
                    for (int i = 0; i < n; i++) m[i] = (Be16(d, bodyStart + 4 * i), d[bodyStart + 4 * i + 2], d[bodyStart + 4 * i + 3]);
                    h.Cmap = m;
                    break;
                }
                case 0x63646566: // cdef
                    if (bodyEnd - bodyStart >= 2)
                    {
                        int n = Math.Min(Be16(d, bodyStart), (bodyEnd - bodyStart - 2) / 6);
                        var c = new (int, int, int)[n];
                        for (int i = 0; i < n; i++)
                        {
                            int o = bodyStart + 2 + 6 * i;
                            c[i] = (Be16(d, o), Be16(d, o + 2), Be16(d, o + 4));
                        }
                        h.Cdef = c;
                    }
                    break;
            }
            pos = bodyEnd;
            if (len <= 0) break;
        }
        return true;
    }

    private static int Be16(byte[] d, int p) => p + 1 < d.Length ? d[p] << 8 | d[p + 1] : 0;
    private static int Be32(byte[] d, int p) => p + 3 < d.Length ? d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3] : 0;

    // ------------------------------------------------------------------ codestream markers

    private bool DecodeCodestream(int start, int end)
    {
        int pos = start + 2; // SOC
        bool haveSiz = false;
        var tiles = new Dictionary<int, TileData>();
        var tileOrder = new List<TileData>();
        TileData? current = null;
        bool inMain = true;

        while (pos + 2 <= end)
        {
            int marker = Be16(_d, pos);
            if (marker == 0xFFD9) break; // EOC
            if ((marker & 0xFF00) != 0xFF00) return haveSiz && tileOrder.Count > 0 && DecodeTiles(tileOrder);
            pos += 2;
            if (marker is >= 0xFF30 and <= 0xFF3F) continue; // reserved markers without a segment
            if (pos + 2 > end) break;
            int len = Be16(_d, pos);
            if (len < 2 || pos + len > end) break;
            int seg = pos + 2, segEnd = pos + len;
            switch (marker)
            {
                case 0xFF51: // SIZ
                    if (!ReadSiz(seg, segEnd)) return false;
                    haveSiz = true;
                    break;
                case 0xFF52: // COD
                {
                    if (!haveSiz) return false;
                    var (gs, cs) = ReadCod(seg, segEnd);
                    if (cs == null) return false;
                    if (inMain) { _style = gs!; _cod = cs; }
                    else if (current is { SawFirstPart: false }) { current.Style = gs; current.Cod = cs; }
                    break;
                }
                case 0xFF53: // COC
                {
                    if (!haveSiz) return false;
                    var (c, cs) = ReadCoc(seg, segEnd);
                    if (cs == null || c >= _comps.Length) break;
                    if (inMain) _coc[c] = cs;
                    else if (current is { SawFirstPart: false }) current.Coc[c] = cs;
                    break;
                }
                case 0xFF5C: // QCD
                {
                    var q = ReadQuant(seg, segEnd);
                    if (inMain) _qcd = q;
                    else if (current is { SawFirstPart: false }) current.Qcd = q;
                    break;
                }
                case 0xFF5D: // QCC
                {
                    if (!haveSiz) return false;
                    int cbytes = _comps.Length < 257 ? 1 : 2;
                    int c = cbytes == 1 ? _d[seg] : Be16(_d, seg);
                    if (c >= _comps.Length) break;
                    var q = ReadQuant(seg + cbytes, segEnd);
                    if (inMain) _qcc[c] = q;
                    else if (current is { SawFirstPart: false }) current.Qcc[c] = q;
                    break;
                }
                case 0xFF5E: // RGN
                {
                    if (!haveSiz) return false;
                    int cbytes = _comps.Length < 257 ? 1 : 2;
                    if (seg + cbytes + 2 > segEnd) break;
                    int c = cbytes == 1 ? _d[seg] : Be16(_d, seg);
                    int shift = _d[seg + cbytes + 1];
                    if (inMain) _rgn[c] = shift;
                    else if (current is { SawFirstPart: false }) current.Rgn[c] = shift;
                    break;
                }
                case 0xFF5F: // POC
                {
                    if (!haveSiz) return false;
                    var list = inMain ? _pocs : current?.Pocs;
                    if (list != null) ReadPoc(seg, segEnd, list);
                    break;
                }
                case 0xFF60: // PPM
                    if (inMain && segEnd > seg) _ppm[_d[seg]] = _d.AsSpan(seg + 1, segEnd - seg - 1).ToArray();
                    break;
                case 0xFF61: // PPT
                    if (current != null && segEnd > seg) current.Ppt[current.Ppt.Count] = _d.AsSpan(seg + 1, segEnd - seg - 1).ToArray();
                    break;
                case 0xFF90: // SOT
                {
                    if (!haveSiz || len < 10) return false;
                    int isot = Be16(_d, seg);
                    long psot = (uint)Be32(_d, seg + 2);
                    int tiles0 = NumTilesX * NumTilesY;
                    if (isot >= tiles0) return tileOrder.Count > 0 && DecodeTiles(tileOrder);
                    if (inMain)
                    {
                        inMain = false;
                        if (_ppm.Count > 0) _ppmChunks = SplitPpm();
                    }
                    if (!tiles.TryGetValue(isot, out var t))
                    {
                        t = new TileData { Index = isot };
                        tiles[isot] = t;
                        tileOrder.Add(t);
                    }
                    else t.SawFirstPart = true;
                    current = t;
                    // The tile-part header runs from here to SOD; its data runs to the tile-part's end.
                    int tpStart = pos - 2;
                    int tpEnd = psot == 0 ? end : (int)Math.Min(end, tpStart + psot);
                    if (_ppmChunks is { Count: > 0 }) t.PpmChunks.Add(_ppmChunks.Dequeue());
                    // Parse the remaining tile-part header markers.
                    int p = segEnd;
                    while (p + 4 <= tpEnd)
                    {
                        int m = Be16(_d, p);
                        if (m == 0xFF93) // SOD
                        {
                            t.Parts.Add((p + 2, tpEnd));
                            break;
                        }
                        int l = Be16(_d, p + 2);
                        if ((m & 0xFF00) != 0xFF00 || l < 2 || p + 2 + l > tpEnd) break;
                        HandleTileMarker(t, m, p + 4, p + 2 + l);
                        p += 2 + l;
                    }
                    t.SawFirstPart = true;
                    pos = tpEnd;
                    continue;
                }
                // TLM, PLM, PLT, CRG, COM and unknown marker segments carry nothing the decoder needs.
            }
            pos = segEnd;
        }
        return haveSiz && _cod != null && _qcd != null && DecodeTiles(tileOrder);
    }

    private void HandleTileMarker(TileData t, int marker, int seg, int segEnd)
    {
        bool first = !t.SawFirstPart;
        int cbytes = _comps.Length < 257 ? 1 : 2;
        switch (marker)
        {
            case 0xFF52 when first:
            {
                var (gs, cs) = ReadCod(seg, segEnd);
                if (cs != null) { t.Style = gs; t.Cod = cs; }
                break;
            }
            case 0xFF53 when first:
            {
                var (c, cs) = ReadCoc(seg, segEnd);
                if (cs != null && c < _comps.Length) t.Coc[c] = cs;
                break;
            }
            case 0xFF5C when first:
                t.Qcd = ReadQuant(seg, segEnd);
                break;
            case 0xFF5D when first:
            {
                int c = cbytes == 1 ? _d[seg] : Be16(_d, seg);
                if (c < _comps.Length) t.Qcc[c] = ReadQuant(seg + cbytes, segEnd);
                break;
            }
            case 0xFF5E when first:
                if (seg + cbytes + 2 <= segEnd) t.Rgn[cbytes == 1 ? _d[seg] : Be16(_d, seg)] = _d[seg + cbytes + 1];
                break;
            case 0xFF5F:
                ReadPoc(seg, segEnd, t.Pocs);
                break;
            case 0xFF61:
                if (segEnd > seg) t.Ppt[t.Ppt.Count] = _d.AsSpan(seg + 1, segEnd - seg - 1).ToArray();
                break;
        }
    }

    private Queue<byte[]> SplitPpm()
    {
        // PPM data is a run of (Nppm, packet headers) pairs, one per tile-part in codestream order, possibly split
        // across marker segments.
        var all = new List<byte>();
        foreach (var kv in _ppm) all.AddRange(kv.Value);
        var q = new Queue<byte[]>();
        int p = 0;
        while (p + 4 <= all.Count)
        {
            int n = all[p] << 24 | all[p + 1] << 16 | all[p + 2] << 8 | all[p + 3];
            p += 4;
            if (n < 0 || p + n > all.Count) n = all.Count - p;
            q.Enqueue(all.GetRange(p, n).ToArray());
            p += n;
        }
        return q;
    }

    private int NumTilesX => (int)Math.Max(1, ((long)_xsiz - _tx0 + _tw - 1) / _tw);
    private int NumTilesY => (int)Math.Max(1, ((long)_ysiz - _ty0 + _th - 1) / _th);

    private bool ReadSiz(int p, int end)
    {
        if (end - p < 36) return false;
        _xsiz = Be32(_d, p + 2);
        _ysiz = Be32(_d, p + 6);
        _x0 = Be32(_d, p + 10);
        _y0 = Be32(_d, p + 14);
        _tw = Be32(_d, p + 18);
        _th = Be32(_d, p + 22);
        _tx0 = Be32(_d, p + 26);
        _ty0 = Be32(_d, p + 30);
        int nc = Be16(_d, p + 34);
        if (_xsiz <= 0 || _ysiz <= 0 || _x0 < 0 || _y0 < 0 || _x0 >= _xsiz || _y0 >= _ysiz) return false;
        if (_tw <= 0 || _th <= 0 || _tx0 < 0 || _ty0 < 0 || _tx0 > _x0 || _ty0 > _y0 || (long)_tx0 + _tw <= _x0 || (long)_ty0 + _th <= _y0)
            return false;
        if (nc < 1 || nc > MaxComponents || p + 36 + 3 * nc > end) return false;
        if ((long)(_xsiz - _x0) * (_ysiz - _y0) > PdfImageDecoder.MaxPixels) return false;
        if ((long)NumTilesX * NumTilesY > MaxTiles) return false;
        _comps = new ComponentInfo[nc];
        for (int i = 0; i < nc; i++)
        {
            int s = _d[p + 36 + 3 * i];
            var c = new ComponentInfo
            {
                Precision = (s & 0x7F) + 1,
                Signed = (s & 0x80) != 0,
                XR = _d[p + 37 + 3 * i],
                YR = _d[p + 38 + 3 * i],
            };
            if (c.XR < 1 || c.YR < 1 || c.Precision > 30) return false;
            _comps[i] = c;
        }
        // Component planes (8 bits per sample).
        _planes = new byte[nc][];
        _planeW = new int[nc];
        _planeH = new int[nc];
        _planeX0 = new int[nc];
        _planeY0 = new int[nc];
        long total = 0;
        for (int i = 0; i < nc; i++)
        {
            var c = _comps[i];
            _planeX0[i] = CeilDiv(_x0, c.XR);
            _planeY0[i] = CeilDiv(_y0, c.YR);
            _planeW[i] = CeilDiv(_xsiz, c.XR) - _planeX0[i];
            _planeH[i] = CeilDiv(_ysiz, c.YR) - _planeY0[i];
            total += (long)_planeW[i] * _planeH[i];
        }
        if (total > int.MaxValue / 2) return false;
        PdfFile.ChargeDecoded(total);
        for (int i = 0; i < nc; i++) _planes[i] = new byte[_planeW[i] * _planeH[i]];
        return true;
    }

    private (GlobalStyle?, CodingStyle?) ReadCod(int p, int end)
    {
        if (end - p < 10) return (null, null);
        int scod = _d[p];
        var gs = new GlobalStyle
        {
            Sop = (scod & 2) != 0,
            Eph = (scod & 4) != 0,
            Progression = _d[p + 1],
            Layers = Be16(_d, p + 2),
            Mct = _d[p + 4],
        };
        if (gs.Layers < 1) return (null, null);
        var cs = ReadSpcod(p + 5, end, (scod & 1) != 0);
        return (gs, cs);
    }

    private (int, CodingStyle?) ReadCoc(int p, int end)
    {
        int cbytes = _comps.Length < 257 ? 1 : 2;
        if (end - p < cbytes + 1) return (0, null);
        int c = cbytes == 1 ? _d[p] : Be16(_d, p);
        int scoc = _d[p + cbytes];
        return (c, ReadSpcod(p + cbytes + 1, end, (scoc & 1) != 0));
    }

    private CodingStyle? ReadSpcod(int p, int end, bool customPrecincts)
    {
        if (end - p < 5) return null;
        var cs = new CodingStyle
        {
            Levels = _d[p],
            CbW = (_d[p + 1] & 0xF) + 2,
            CbH = (_d[p + 2] & 0xF) + 2,
            CbStyle = _d[p + 3],
            Reversible = _d[p + 4] == 1,
        };
        if (cs.Levels > MaxLevels || cs.CbW > 10 || cs.CbH > 10 || cs.CbW + cs.CbH > 12) return null;
        cs.PPx = new int[cs.Levels + 1];
        cs.PPy = new int[cs.Levels + 1];
        for (int r = 0; r <= cs.Levels; r++)
        {
            if (customPrecincts)
            {
                int v = p + 5 + r < end ? _d[p + 5 + r] : 0xFF;
                cs.PPx[r] = v & 0xF;
                cs.PPy[r] = v >> 4;
            }
            else
            {
                cs.PPx[r] = 15;
                cs.PPy[r] = 15;
            }
        }
        return cs;
    }

    private Quant ReadQuant(int p, int end)
    {
        var q = new Quant();
        if (p >= end) return q;
        int s = _d[p];
        q.Guard = s >> 5;
        q.Style = s & 0x1F;
        var exp = new List<int>();
        var mant = new List<int>();
        if (q.Style == 0)
        {
            for (int i = p + 1; i < end; i++) { exp.Add(_d[i] >> 3); mant.Add(0); }
        }
        else
        {
            for (int i = p + 1; i + 1 < end; i += 2)
            {
                int v = Be16(_d, i);
                exp.Add(v >> 11);
                mant.Add(v & 0x7FF);
            }
        }
        q.Exp = exp.ToArray();
        q.Mant = mant.ToArray();
        return q;
    }

    private void ReadPoc(int p, int end, List<Poc> list)
    {
        int cbytes = _comps.Length < 257 ? 1 : 2;
        int entry = 5 + 2 * cbytes;
        for (; p + entry <= end; p += entry)
        {
            var e = new Poc
            {
                RS = _d[p],
                CS = cbytes == 1 ? _d[p + 1] : Be16(_d, p + 1),
                LYE = Be16(_d, p + 1 + cbytes),
                RE = _d[p + 3 + cbytes],
                CE = cbytes == 1 ? _d[p + 4 + cbytes] : Be16(_d, p + 4 + cbytes),
                Order = _d[p + 4 + 2 * cbytes],
            };
            if (e.CE == 0) e.CE = 256;
            list.Add(e);
        }
    }

    private static int CeilDiv(long a, long b) => (int)((a + b - 1) / b);
    private static int CeilDivPow2(int a, int k) => (int)(((long)a + (1L << k) - 1) >> k);

    // ------------------------------------------------------------------ tile structures

    private sealed class CodeBlock
    {
        public int X0, Y0, X1, Y1;
        public bool Included;
        public int Lblock = 3;
        public int ZeroBitplanes;
        public int Passes;
        public byte[] Data = Array.Empty<byte>();
        public int DataLen;
        public readonly List<int[]> Segments = new(); // [passes, start, length, maxPasses]
    }

    private sealed class BandPrecinct
    {
        public int Band; // index into Resolution.Bands
        public int CbW, CbH;
        public CodeBlock[] Blocks = Array.Empty<CodeBlock>();
        public TagTree? Incl, Zbp;
    }

    private sealed class Band
    {
        public int Orient; // 0 LL, 1 HL, 2 LH, 3 HH
        public int X0, Y0, X1, Y1;
        public int OffX, OffY; // position in the tile-component buffer
        public int Mb;
        public float Delta;
        public int W => X1 - X0;
        public int H => Y1 - Y0;
    }

    private sealed class Resolution
    {
        public int X0, Y0, X1, Y1;
        public int PPx, PPy;
        public int PrecX0, PrecY0, NumPrecX, NumPrecY;
        public Band[] Bands = Array.Empty<Band>();
        public BandPrecinct[][] Precincts = Array.Empty<BandPrecinct[]>(); // [precinct][band]
        public int[] NextLayer = Array.Empty<int>();
        public int CbW, CbH; // log2 code-block size in this resolution's bands
        public int CbStyle;
    }

    private sealed class TileComp
    {
        public int X0, Y0, X1, Y1;
        public CodingStyle Cs = null!;
        public Resolution[] Res = Array.Empty<Resolution>();
        public int RoiShift;
        public float[] Buffer = Array.Empty<float>();
    }

    private sealed class TagTree
    {
        private readonly int[] _value, _low, _parent;
        private readonly int _leaves;

        public TagTree(int w, int h)
        {
            var levels = new List<(int W, int H)>();
            int lw = w, lh = h;
            int total = 0;
            while (true)
            {
                levels.Add((lw, lh));
                total += lw * lh;
                if (lw <= 1 && lh <= 1) break;
                lw = (lw + 1) >> 1;
                lh = (lh + 1) >> 1;
            }
            _leaves = w * h;
            _value = new int[total];
            _low = new int[total];
            _parent = new int[total];
            Array.Fill(_value, int.MaxValue);
            int offset = 0;
            for (int l = 0; l < levels.Count; l++)
            {
                var (cw, ch) = levels[l];
                int next = offset + cw * ch;
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                        _parent[offset + y * cw + x] = l + 1 < levels.Count ? next + (y >> 1) * levels[l + 1].W + (x >> 1) : -1;
                offset = next;
            }
        }

        /// <summary>Decodes until the leaf's value is known to be below <paramref name="threshold"/> or not.</summary>
        public bool Decode(int leaf, int threshold, PacketReader br)
        {
            Span<int> path = stackalloc int[40];
            int n = 0;
            for (int node = leaf; node >= 0 && n < path.Length; node = _parent[node]) path[n++] = node;
            int low = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                int node = path[i];
                if (low > _low[node]) _low[node] = low;
                else low = _low[node];
                while (low < threshold && low < _value[node])
                {
                    if (br.Bit() != 0) _value[node] = low;
                    else low++;
                }
                _low[node] = low;
            }
            return _value[leaf] < threshold;
        }

        public int Value(int leaf) => _value[leaf];

        public int Leaves => _leaves;
    }

    /// <summary>Reads packet-header bits with JPEG 2000 bit stuffing (a byte after 0xFF holds 7 bits).</summary>
    private sealed class PacketReader
    {
        public byte[] Data = Array.Empty<byte>();
        public int Pos, End;
        private int _cur, _bits;
        private bool _prevFF;
        public bool Overrun;

        public int Bit()
        {
            if (_bits == 0)
            {
                if (Pos >= End) { Overrun = true; _cur = 0; }
                else _cur = Data[Pos];
                Pos++;
                _bits = _prevFF ? 7 : 8;
                _prevFF = _cur == 0xFF;
            }
            _bits--;
            return (_cur >> _bits) & 1;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | Bit();
            return v;
        }

        public void Align()
        {
            _bits = 0;
            if (_prevFF) Pos++;
            _prevFF = false;
        }

        public void Reset(byte[] data, int pos, int end)
        {
            Data = data;
            Pos = pos;
            End = end;
            _bits = 0;
            _prevFF = false;
            Overrun = false;
        }
    }

    // ------------------------------------------------------------------ tiles

    private bool DecodeTiles(List<TileData> tiles)
    {
        if (_cod == null || _qcd == null) return false;
        bool any = false;
        foreach (var t in tiles)
        {
            PdfFile.CheckCancel();
            try
            {
                DecodeTile(t);
                any = true;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidDataException or ArgumentException or OverflowException)
            {
                // A damaged tile leaves its area blank; other tiles still draw.
                if (ex is InvalidDataException && ex.Message.StartsWith("The page", StringComparison.Ordinal)) throw;
            }
        }
        return any || tiles.Count == 0;
    }

    private void DecodeTile(TileData t)
    {
        int ntx = NumTilesX;
        int p = t.Index % ntx, q = t.Index / ntx;
        int tx0 = (int)Math.Max(_tx0 + (long)p * _tw, _x0), tx1 = (int)Math.Min(_tx0 + (long)(p + 1) * _tw, _xsiz);
        int ty0 = (int)Math.Max(_ty0 + (long)q * _th, _y0), ty1 = (int)Math.Min(_ty0 + (long)(q + 1) * _th, _ysiz);
        if (tx1 <= tx0 || ty1 <= ty0) return;

        var gs = t.Style ?? _style;
        int nc = _comps.Length;
        var tcs = new TileComp[nc];
        long work = 0;
        for (int c = 0; c < nc; c++)
        {
            var cs = t.Coc.GetValueOrDefault(c) ?? t.Cod ?? _coc.GetValueOrDefault(c) ?? _cod!;
            var qt = t.Qcc.GetValueOrDefault(c) ?? t.Qcd ?? _qcc.GetValueOrDefault(c) ?? _qcd!;
            var ci = _comps[c];
            var tc = new TileComp
            {
                X0 = CeilDiv(tx0, ci.XR), X1 = CeilDiv(tx1, ci.XR),
                Y0 = CeilDiv(ty0, ci.YR), Y1 = CeilDiv(ty1, ci.YR),
                Cs = cs,
                RoiShift = t.Rgn.TryGetValue(c, out int rs) ? rs : _rgn.GetValueOrDefault(c),
            };
            work += BuildResolutions(tc, ci, qt) + 4L * (tc.X1 - tc.X0) * (tc.Y1 - tc.Y0);
            tcs[c] = tc;
        }
        PdfFile.ChargeDecoded(work);

        // Packet data: the tile-parts' bodies joined, and the packed headers when present.
        int total = 0;
        foreach (var (s, e) in t.Parts) total += Math.Max(0, e - s);
        var body = new byte[total];
        int o = 0;
        foreach (var (s, e) in t.Parts)
        {
            if (e <= s) continue;
            Buffer.BlockCopy(_d, s, body, o, e - s);
            o += e - s;
        }
        byte[]? packed = null;
        if (t.PpmChunks.Count > 0) packed = Concat(t.PpmChunks);
        else if (t.Ppt.Count > 0) packed = Concat(t.Ppt.Values);

        ReadPackets(t, gs, tcs, body, packed);

        // Tier-1 decoding and the inverse wavelet transform for each component.
        var outputs = new float[nc][];
        for (int c = 0; c < nc; c++)
        {
            PdfFile.CheckCancel();
            var tc = tcs[c];
            tc.Buffer = RentBuffer(c, (tc.X1 - tc.X0) * (tc.Y1 - tc.Y0));
            DecodeCodeBlocks(tc);
            InverseDwt(tc);
            outputs[c] = tc.Buffer;
        }

        // Inverse multiple component transform.
        if (gs.Mct == 1 && nc >= 3 && SameSize(tcs[0], tcs[1]) && SameSize(tcs[0], tcs[2]))
        {
            float[] a = outputs[0], b = outputs[1], cc = outputs[2];
            int n = (tcs[0].X1 - tcs[0].X0) * (tcs[0].Y1 - tcs[0].Y0);
            if (tcs[0].Cs.Reversible)
            {
                for (int i = 0; i < n; i++)
                {
                    float y0 = a[i], y1 = b[i], y2 = cc[i];
                    float g = y0 - MathF.Floor((y2 + y1) / 4);
                    a[i] = y2 + g;
                    b[i] = g;
                    cc[i] = y1 + g;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float y = a[i], cb = b[i], cr = cc[i];
                    a[i] = y + 1.402f * cr;
                    b[i] = y - 0.344136f * cb - 0.714136f * cr;
                    cc[i] = y + 1.772f * cb;
                }
            }
        }

        // DC level shift, scaling to 8 bits and copying into the component planes.
        for (int c = 0; c < nc; c++)
        {
            var tc = tcs[c];
            var ci = _comps[c];
            var src = outputs[c];
            int w = tc.X1 - tc.X0, h = tc.Y1 - tc.Y0;
            if (w <= 0 || h <= 0) continue;
            float shift = 1 << (ci.Precision - 1);
            int max = (1 << ci.Precision) - 1;
            float scale = 255f / max;
            var plane = _planes[c];
            int pw = _planeW[c];
            int ox = tc.X0 - _planeX0[c], oy = tc.Y0 - _planeY0[c];
            for (int y = 0; y < h; y++)
            {
                int py = oy + y;
                if ((uint)py >= (uint)_planeH[c]) continue;
                int row = py * pw;
                for (int x = 0; x < w; x++)
                {
                    int px = ox + x;
                    if ((uint)px >= (uint)pw) continue;
                    float v = src[y * w + x] + shift;
                    int iv = (int)MathF.Round(v);
                    iv = iv < 0 ? 0 : iv > max ? max : iv;
                    plane[row + px] = ci.Precision == 8 ? (byte)iv : (byte)(iv * scale + 0.5f);
                }
            }
        }
    }

    // Tile-component buffers, reused from tile to tile.
    private float[][] _buffers = Array.Empty<float[]>();

    private float[] RentBuffer(int c, int size)
    {
        if (_buffers.Length <= c) Array.Resize(ref _buffers, _comps.Length);
        var b = _buffers[c];
        if (b == null || b.Length < size) _buffers[c] = b = new float[size];
        else Array.Clear(b, 0, size);
        return b;
    }

    private static bool SameSize(TileComp a, TileComp b) => a.X0 == b.X0 && a.X1 == b.X1 && a.Y0 == b.Y0 && a.Y1 == b.Y1;

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    /// <summary>Lays out resolutions, sub-bands, precincts and code-blocks. Returns an estimate of the memory used.</summary>
    private static long BuildResolutions(TileComp tc, ComponentInfo ci, Quant qt)
    {
        var cs = tc.Cs;
        int nl = cs.Levels;
        tc.Res = new Resolution[nl + 1];
        long work = 0;
        for (int r = 0; r <= nl; r++)
        {
            int shift = nl - r;
            var res = new Resolution
            {
                X0 = CeilDivPow2(tc.X0, shift), X1 = CeilDivPow2(tc.X1, shift),
                Y0 = CeilDivPow2(tc.Y0, shift), Y1 = CeilDivPow2(tc.Y1, shift),
                PPx = cs.PPx[r], PPy = cs.PPy[r],
                CbStyle = cs.CbStyle,
            };
            if (r > 0 && res.PPx == 0) res.PPx = 1;
            if (r > 0 && res.PPy == 0) res.PPy = 1;
            res.CbW = Math.Min(cs.CbW, r == 0 ? res.PPx : res.PPx - 1);
            res.CbH = Math.Min(cs.CbH, r == 0 ? res.PPy : res.PPy - 1);
            if (res.X1 > res.X0 && res.Y1 > res.Y0)
            {
                res.PrecX0 = res.X0 >> res.PPx;
                res.PrecY0 = res.Y0 >> res.PPy;
                res.NumPrecX = CeilDivPow2(res.X1, res.PPx) - res.PrecX0;
                res.NumPrecY = CeilDivPow2(res.Y1, res.PPy) - res.PrecY0;
            }

            // Sub-bands.
            if (r == 0)
                res.Bands = new[] { MakeBand(0, res.X0, res.Y0, res.X1, res.Y1, ci, qt, nl, 0, nl) };
            else
            {
                int lx0 = CeilDivPow2(res.X0, 1), lx1 = CeilDivPow2(res.X1, 1), hx0 = res.X0 >> 1, hx1 = res.X1 >> 1;
                int ly0 = CeilDivPow2(res.Y0, 1), ly1 = CeilDivPow2(res.Y1, 1), hy0 = res.Y0 >> 1, hy1 = res.Y1 >> 1;
                int nb = nl - r + 1;
                res.Bands = new[]
                {
                    MakeBand(1, hx0, ly0, hx1, ly1, ci, qt, nl, r, nb),
                    MakeBand(2, lx0, hy0, lx1, hy1, ci, qt, nl, r, nb),
                    MakeBand(3, hx0, hy0, hx1, hy1, ci, qt, nl, r, nb),
                };
                int lowW = Math.Max(0, lx1 - lx0), lowH = Math.Max(0, ly1 - ly0);
                res.Bands[0].OffX = lowW;
                res.Bands[1].OffY = lowH;
                res.Bands[2].OffX = lowW;
                res.Bands[2].OffY = lowH;
            }

            // Precincts and their code-blocks.
            long nprec = (long)res.NumPrecX * res.NumPrecY;
            if (nprec > 1 << 24) throw new InvalidDataException("Too many precincts.");
            res.Precincts = new BandPrecinct[nprec][];
            res.NextLayer = new int[nprec];
            int pbShift = r == 0 ? 0 : 1;
            for (int py = 0; py < res.NumPrecY; py++)
            {
                for (int px = 0; px < res.NumPrecX; px++)
                {
                    var bps = new BandPrecinct[res.Bands.Length];
                    for (int bi = 0; bi < res.Bands.Length; bi++)
                    {
                        var b = res.Bands[bi];
                        int sx = res.PPx - pbShift, sy = res.PPy - pbShift;
                        long pbx0 = Math.Max(b.X0, (long)(res.PrecX0 + px) << sx), pbx1 = Math.Min(b.X1, (long)(res.PrecX0 + px + 1) << sx);
                        long pby0 = Math.Max(b.Y0, (long)(res.PrecY0 + py) << sy), pby1 = Math.Min(b.Y1, (long)(res.PrecY0 + py + 1) << sy);
                        var bp = new BandPrecinct { Band = bi };
                        if (pbx1 > pbx0 && pby1 > pby0)
                        {
                            int cbx0 = (int)(pbx0 >> res.CbW), cbx1 = CeilDivPow2((int)pbx1, res.CbW);
                            int cby0 = (int)(pby0 >> res.CbH), cby1 = CeilDivPow2((int)pby1, res.CbH);
                            bp.CbW = cbx1 - cbx0;
                            bp.CbH = cby1 - cby0;
                            var blocks = new CodeBlock[bp.CbW * bp.CbH];
                            for (int j = 0; j < bp.CbH; j++)
                                for (int i = 0; i < bp.CbW; i++)
                                {
                                    blocks[j * bp.CbW + i] = new CodeBlock
                                    {
                                        X0 = (int)Math.Max(pbx0, (long)(cbx0 + i) << res.CbW),
                                        X1 = (int)Math.Min(pbx1, (long)(cbx0 + i + 1) << res.CbW),
                                        Y0 = (int)Math.Max(pby0, (long)(cby0 + j) << res.CbH),
                                        Y1 = (int)Math.Min(pby1, (long)(cby0 + j + 1) << res.CbH),
                                    };
                                }
                            bp.Blocks = blocks;
                            bp.Incl = new TagTree(bp.CbW, bp.CbH);
                            bp.Zbp = new TagTree(bp.CbW, bp.CbH);
                            work += blocks.Length * 160L;
                        }
                        bps[bi] = bp;
                    }
                    res.Precincts[py * res.NumPrecX + px] = bps;
                    work += 64;
                }
            }
            tc.Res[r] = res;
        }
        return work;
    }

    private static Band MakeBand(int orient, int x0, int y0, int x1, int y1, ComponentInfo ci, Quant qt, int nl, int r, int nb)
    {
        var b = new Band { Orient = orient, X0 = x0, Y0 = y0, X1 = Math.Max(x0, x1), Y1 = Math.Max(y0, y1) };
        int idx = r == 0 ? 0 : 1 + 3 * (r - 1) + (orient - 1);
        int exp, mant;
        if (qt.Style == 1)
        {
            exp = (qt.Exp.Length > 0 ? qt.Exp[0] : 0) - nl + nb;
            mant = qt.Mant.Length > 0 ? qt.Mant[0] : 0;
            if (r == 0) exp = qt.Exp.Length > 0 ? qt.Exp[0] : 0;
        }
        else
        {
            exp = idx < qt.Exp.Length ? qt.Exp[idx] : qt.Exp.Length > 0 ? qt.Exp[^1] : 0;
            mant = idx < qt.Mant.Length ? qt.Mant[idx] : 0;
        }
        int gain = orient == 0 ? 0 : orient == 3 ? 2 : 1;
        b.Mb = Math.Clamp(qt.Guard + exp - 1, 0, 31);
        b.Delta = qt.Style == 0 ? 1f : (float)(Math.Pow(2, ci.Precision + gain - exp) * (1 + mant / 2048.0));
        return b;
    }

    // ------------------------------------------------------------------ tier 2: packets

    private struct PrecinctRef
    {
        public int C, R, P;
        public long X, Y;
    }

    private void ReadPackets(TileData t, GlobalStyle gs, TileComp[] tcs, byte[] body, byte[]? packed)
    {
        int nc = tcs.Length;
        int maxRes = 0;
        foreach (var tc in tcs) maxRes = Math.Max(maxRes, tc.Cs.Levels + 1);
        var pocs = t.Pocs.Count > 0 ? t.Pocs : _pocs;
        var progs = new List<Poc>();
        if (pocs.Count > 0) progs.AddRange(pocs);
        // The default progression covers whatever the changes leave out.
        progs.Add(new Poc { RS = 0, CS = 0, LYE = gs.Layers, RE = maxRes, CE = nc, Order = gs.Progression });

        var hdr = new PacketReader();
        var st = new PacketState { Body = body, BodyPos = 0, Packed = packed, Hdr = hdr };
        if (packed != null) hdr.Reset(packed, 0, packed.Length);

        // Positional orders visit precincts sorted by where they start on the reference grid.
        List<PrecinctRef>? positions = null;

        foreach (var poc in progs)
        {
            int lye = Math.Min(poc.LYE, gs.Layers), re = Math.Min(poc.RE, maxRes), ce = Math.Min(poc.CE, nc);
            int rs = poc.RS, cs0 = poc.CS;
            switch (poc.Order)
            {
                case 0: // LRCP
                    for (int l = 0; l < lye; l++)
                        for (int r = rs; r < re; r++)
                            for (int c = cs0; c < ce; c++)
                                if (!PacketsAt(st, gs, tcs, c, r, l)) return;
                    break;
                case 1: // RLCP
                    for (int r = rs; r < re; r++)
                        for (int l = 0; l < lye; l++)
                            for (int c = cs0; c < ce; c++)
                                if (!PacketsAt(st, gs, tcs, c, r, l)) return;
                    break;
                default: // RPCL, PCRL, CPRL
                {
                    positions ??= PrecinctPositions(t, tcs);
                    var list = new List<PrecinctRef>();
                    foreach (var pr in positions)
                        if (pr.C >= cs0 && pr.C < ce && pr.R >= rs && pr.R < re) list.Add(pr);
                    int order = poc.Order;
                    list.Sort((a, b) =>
                    {
                        int k = order switch
                        {
                            2 => Cmp(a.R, b.R, a.Y, b.Y, a.X, b.X, a.C, b.C),
                            3 => Cmp(0, 0, a.Y, b.Y, a.X, b.X, a.C, b.C, a.R, b.R),
                            _ => Cmp(a.C, b.C, a.Y, b.Y, a.X, b.X, a.R, b.R),
                        };
                        return k;
                    });
                    foreach (var pr in list)
                    {
                        var res = tcs[pr.C].Res[pr.R];
                        for (int l = res.NextLayer[pr.P]; l < lye; l++)
                            if (!ReadPacket(st, gs, res, pr.P, l)) return;
                    }
                    break;
                }
            }
        }
    }

    private static int Cmp(long a0, long b0, long a1, long b1, long a2, long b2, long a3, long b3, long a4 = 0, long b4 = 0)
    {
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        if (a2 != b2) return a2.CompareTo(b2);
        if (a3 != b3) return a3.CompareTo(b3);
        return a4.CompareTo(b4);
    }

    private List<PrecinctRef> PrecinctPositions(TileData t, TileComp[] tcs)
    {
        int ntx = NumTilesX;
        int p = t.Index % ntx, q = t.Index / ntx;
        long tx0 = Math.Max(_tx0 + (long)p * _tw, _x0), ty0 = Math.Max(_ty0 + (long)q * _th, _y0);
        var list = new List<PrecinctRef>();
        for (int c = 0; c < tcs.Length; c++)
        {
            var tc = tcs[c];
            var ci = _comps[c];
            int nl = tc.Cs.Levels;
            for (int r = 0; r <= nl; r++)
            {
                var res = tc.Res[r];
                long sx = (long)ci.XR << (nl - r), sy = (long)ci.YR << (nl - r);
                for (int py = 0; py < res.NumPrecY; py++)
                {
                    long startY = Math.Max(res.Y0, (long)(res.PrecY0 + py) << res.PPy);
                    bool alignedY = (startY & ((1L << res.PPy) - 1)) == 0;
                    long refY = alignedY ? startY * sy : ty0;
                    for (int px = 0; px < res.NumPrecX; px++)
                    {
                        long startX = Math.Max(res.X0, (long)(res.PrecX0 + px) << res.PPx);
                        bool alignedX = (startX & ((1L << res.PPx) - 1)) == 0;
                        long refX = alignedX ? startX * sx : tx0;
                        list.Add(new PrecinctRef { C = c, R = r, P = py * res.NumPrecX + px, X = refX, Y = refY });
                    }
                }
            }
        }
        return list;
    }

    private sealed class PacketState
    {
        public byte[] Body = Array.Empty<byte>();
        public int BodyPos;
        public byte[]? Packed;
        public PacketReader Hdr = null!;
    }

    private bool PacketsAt(PacketState st, GlobalStyle gs, TileComp[] tcs, int c, int r, int l)
    {
        var tc = tcs[c];
        if (r > tc.Cs.Levels) return true;
        var res = tc.Res[r];
        int n = res.NumPrecX * res.NumPrecY;
        for (int p = 0; p < n; p++)
            if (res.NextLayer[p] == l && !ReadPacket(st, gs, res, p, l)) return false;
        return true;
    }

    /// <summary>Reads one packet. Returns false when the data has run out.</summary>
    private bool ReadPacket(PacketState st, GlobalStyle gs, Resolution res, int p, int layer)
    {
        if (res.NextLayer[p] != layer) return true;
        res.NextLayer[p] = layer + 1;
        var body = st.Body;
        var hdr = st.Hdr;
        if (gs.Sop && st.BodyPos + 6 <= body.Length && body[st.BodyPos] == 0xFF && body[st.BodyPos + 1] == 0x91) st.BodyPos += 6;
        if (st.Packed == null)
        {
            if (st.BodyPos >= body.Length) return false;
            hdr.Reset(body, st.BodyPos, body.Length);
        }
        else if (hdr.Pos >= hdr.End) return false;

        var included = new List<(CodeBlock Cb, int Passes, List<int> Lengths)>();
        if (hdr.Bit() != 0)
        {
            var bps = res.Precincts[p];
            foreach (var bp in bps)
            {
                for (int i = 0; i < bp.Blocks.Length; i++)
                {
                    var cb = bp.Blocks[i];
                    bool inc;
                    if (!cb.Included) inc = bp.Incl!.Decode(i, layer + 1, hdr);
                    else inc = hdr.Bit() != 0;
                    if (hdr.Overrun) return false;
                    if (!inc) continue;
                    if (!cb.Included)
                    {
                        int t = 1;
                        while (!bp.Zbp!.Decode(i, t, hdr))
                        {
                            if (hdr.Overrun || ++t > 64) return false;
                        }
                        cb.ZeroBitplanes = bp.Zbp.Value(i);
                        cb.Included = true;
                    }
                    int passes = ReadPassCount(hdr);
                    while (hdr.Bit() != 0)
                    {
                        if (++cb.Lblock > 32 || hdr.Overrun) return false;
                    }
                    // Split the new passes into codeword segments and read each segment's length.
                    var lengths = new List<int>();
                    int remaining = passes;
                    int cbStyle = res.CbStyle;
                    while (remaining > 0)
                    {
                        int segIdx, already;
                        if (cb.Segments.Count == 0 || cb.Segments[^1][0] >= cb.Segments[^1][3])
                        {
                            segIdx = cb.Segments.Count;
                            cb.Segments.Add(new[] { 0, cb.DataLen, 0, MaxPasses(cbStyle, segIdx, cb.Passes) });
                            already = 0;
                        }
                        else
                        {
                            segIdx = cb.Segments.Count - 1;
                            already = cb.Segments[segIdx][0];
                        }
                        var seg = cb.Segments[segIdx];
                        int take = Math.Min(remaining, seg[3] - already);
                        int bits = cb.Lblock + Log2(take);
                        if (bits > 31) return false;
                        int len = hdr.Bits(bits);
                        seg[0] += take;
                        cb.Passes += take;
                        remaining -= take;
                        lengths.Add(len);
                        // Track the segment's byte length now; its bytes arrive with the packet body.
                        seg[2] += len;
                    }
                    included.Add((cb, passes, lengths));
                }
            }
        }
        hdr.Align();
        if (hdr.Overrun) return false;
        if (gs.Eph && hdr.Pos + 2 <= hdr.End && hdr.Data[hdr.Pos] == 0xFF && hdr.Data[hdr.Pos + 1] == 0x92) hdr.Pos += 2;
        if (st.Packed == null) st.BodyPos = hdr.Pos;

        // Packet body: each included code-block's new bytes, in header order.
        foreach (var (cb, _, lengths) in included)
        {
            int sum = 0;
            foreach (int len in lengths) sum += len;
            int avail = Math.Max(0, Math.Min(sum, body.Length - st.BodyPos));
            if (cb.DataLen + avail > cb.Data.Length)
            {
                int cap = Math.Max(cb.Data.Length * 2, cb.DataLen + avail + 2);
                PdfFile.ChargeDecoded(cap - cb.Data.Length);
                Array.Resize(ref cb.Data, cap);
            }
            Buffer.BlockCopy(body, st.BodyPos, cb.Data, cb.DataLen, avail);
            cb.DataLen += avail;
            st.BodyPos += avail;
            if (avail < sum)
            {
                // Truncated: shorten the segments to the data that arrived.
                int missing = sum - avail;
                for (int k = cb.Segments.Count - 1; k >= 0 && missing > 0; k--)
                {
                    int cut = Math.Min(missing, cb.Segments[k][2]);
                    cb.Segments[k][2] -= cut;
                    missing -= cut;
                }
                return false;
            }
        }
        return true;
    }

    private static int MaxPasses(int cbStyle, int segIdx, int passesBefore)
    {
        if ((cbStyle & 4) != 0) return 1; // TERMALL: every pass is terminated
        if ((cbStyle & 1) != 0)
        {
            // Selective arithmetic-coding bypass: 10 MQ passes, then raw (SP + MR) and MQ (cleanup) segments alternate.
            if (segIdx == 0) return 10;
            return passesBefore < 10 ? 10 - passesBefore : ((passesBefore - 10) % 3 == 0 ? 2 : 1);
        }
        return int.MaxValue;
    }

    private static int ReadPassCount(PacketReader br)
    {
        if (br.Bit() == 0) return 1;
        if (br.Bit() == 0) return 2;
        int v = br.Bits(2);
        if (v != 3) return 3 + v;
        v = br.Bits(5);
        if (v != 31) return 6 + v;
        return 37 + br.Bits(7);
    }

    private static int Log2(int v)
    {
        int n = 0;
        while (v > 1) { v >>= 1; n++; }
        return n;
    }

    // ------------------------------------------------------------------ tier 1: code-blocks

    private static void DecodeCodeBlocks(TileComp tc)
    {
        int style = tc.Cs.CbStyle;
        var jobs = new List<(CodeBlock Cb, Band Band, int Numbps)>();
        foreach (var res in tc.Res)
            foreach (var bps in res.Precincts)
                foreach (var bp in bps)
                {
                    var band = res.Bands[bp.Band];
                    foreach (var cb in bp.Blocks)
                    {
                        if (cb.Passes == 0 || cb.Segments.Count == 0) continue;
                        int numbps = band.Mb + tc.RoiShift - cb.ZeroBitplanes;
                        if (numbps <= 0 || numbps > 31) continue;
                        jobs.Add((cb, band, numbps));
                    }
                }
        // Code-blocks are independent, so large images decode them on several cores.
        int stride = tc.X1 - tc.X0;
        ParallelRange(jobs.Count, 64, () => new Tier1(), (i, t1) =>
        {
            var (cb, band, numbps) = jobs[i];
            t1.Decode(cb, band, numbps, style, tc.RoiShift, tc.Cs.Reversible, tc.Buffer, stride);
        });
    }

    /// <summary>Runs <paramref name="body"/> for 0..count-1, in parallel when there are at least <paramref name="minParallel"/> items.</summary>
    private static void ParallelRange<T>(int count, int minParallel, Func<T> init, Action<int, T> body)
    {
        if (count < minParallel || Environment.ProcessorCount < 2)
        {
            var state = init();
            for (int i = 0; i < count; i++) body(i, state);
            return;
        }
        var cancel = PdfFile.CurrentCancellation;
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancel },
                init, (i, _, state) => { body(i, state); return state; }, _ => { });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
        }
    }

    private sealed class Tier1
    {
        // Per-coefficient flags: own state, significance of the 8 neighbors, and signs of the 4 direct neighbors.
        private const int Sig = 1, Neg = 2, Visit = 4, Refined = 8;
        private const int NbW = 1 << 4, NbE = 1 << 5, NbN = 1 << 6, NbS = 1 << 7, NbNW = 1 << 8, NbNE = 1 << 9, NbSW = 1 << 10, NbSE = 1 << 11;
        private const int NegW = 1 << 12, NegE = 1 << 13, NegN = 1 << 14, NegS = 1 << 15;
        private const int NbAll = 0xFF << 4;
        private const int BelowMask = ~(NbS | NbSW | NbSE | NegS); // vertically causal mode ignores the next stripe

        private int[] _flags = Array.Empty<int>();
        private int[] _mag = Array.Empty<int>();
        private byte[] _lastBp = Array.Empty<byte>();
        private readonly byte[] _cx = new byte[19];
        private readonly MqDecoder _mq = new();
        private int _w, _h, _fw;
        private byte[] _zc = ZcLut[0];
        private bool _vsc;

        // Raw (bypass) bit reader.
        private byte[] _raw = Array.Empty<byte>();
        private int _rawPos, _rawEnd, _rawBits, _rawCur;

        private static readonly byte[][] ZcLut = BuildZc();
        private static readonly byte[] SignLut = BuildSign(); // index: neighbor bits >> 4 & 0xFFF compressed to 8 bits

        private static byte[][] BuildZc()
        {
            var t = new byte[4][];
            for (int o = 0; o < 4; o++)
            {
                t[o] = new byte[256];
                for (int nb = 0; nb < 256; nb++)
                {
                    int f = nb << 4;
                    int h = ((f & NbW) != 0 ? 1 : 0) + ((f & NbE) != 0 ? 1 : 0);
                    int v = ((f & NbN) != 0 ? 1 : 0) + ((f & NbS) != 0 ? 1 : 0);
                    int d = ((f & NbNW) != 0 ? 1 : 0) + ((f & NbNE) != 0 ? 1 : 0) + ((f & NbSW) != 0 ? 1 : 0) + ((f & NbSE) != 0 ? 1 : 0);
                    t[o][nb] = (byte)ZcLabel(o, h, v, d);
                }
            }
            return t;
        }

        private static int ZcLabel(int orient, int h, int v, int d)
        {
            if (orient == 3)
            {
                int hv = h + v;
                if (d >= 3) return 8;
                if (d == 2) return hv >= 1 ? 7 : 6;
                if (d == 1) return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                return hv >= 2 ? 2 : hv == 1 ? 1 : 0;
            }
            if (orient == 1) (h, v) = (v, h); // HL: vertical neighbors lead
            if (h == 2) return 8;
            if (h == 1) return v >= 1 ? 7 : d >= 1 ? 6 : 5;
            if (v == 2) return 4;
            if (v == 1) return 3;
            return d >= 2 ? 2 : d == 1 ? 1 : 0;
        }

        // Sign context and XOR bit (bit 7) from the significance and signs of the 4 direct neighbors.
        private static int SignIndex(int f) =>
            (f >> 4 & 0xF) | (f >> 8 & 0xF0); // W E N S significance, then W E N S negative

        private static byte[] BuildSign()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int Contribution(int sigBit, int negBit) => (i & sigBit) == 0 ? 0 : (i & negBit) != 0 ? -1 : 1;
                int hc = Math.Clamp(Contribution(1, 16) + Contribution(2, 32), -1, 1);
                int vc = Math.Clamp(Contribution(4, 64) + Contribution(8, 128), -1, 1);
                int ctx, xor;
                if (hc == 1) { ctx = vc == 1 ? 13 : vc == 0 ? 12 : 11; xor = 0; }
                else if (hc == 0) { ctx = vc == 0 ? 9 : 10; xor = vc == -1 ? 1 : 0; }
                else { ctx = vc == 1 ? 11 : vc == 0 ? 12 : 13; xor = 1; }
                t[i] = (byte)(ctx | xor << 7);
            }
            return t;
        }

        private void ResetContexts()
        {
            Array.Clear(_cx);
            _cx[0] = 4 << 1;
            _cx[17] = 3 << 1;
            _cx[18] = 46 << 1;
        }

        public void Decode(CodeBlock cb, Band band, int numbps, int style, int roiShift, bool reversible, float[] dst, int stride)
        {
            _w = cb.X1 - cb.X0;
            _h = cb.Y1 - cb.Y0;
            if (_w <= 0 || _h <= 0) return;
            _fw = _w + 2;
            int fsize = _fw * (_h + 2);
            if (_flags.Length < fsize) _flags = new int[fsize];
            else Array.Clear(_flags, 0, fsize);
            int n = _w * _h;
            if (_mag.Length < n) { _mag = new int[n]; _lastBp = new byte[n]; }
            else { Array.Clear(_mag, 0, n); Array.Clear(_lastBp, 0, n); }
            _zc = ZcLut[band.Orient];
            _vsc = (style & 8) != 0;
            bool bypass = (style & 1) != 0, reset = (style & 2) != 0, segsym = (style & 0x20) != 0;
            ResetContexts();

            int bp = numbps - 1;
            int kind = 2; // 0 significance propagation, 1 magnitude refinement, 2 cleanup
            int passIdx = 0;
            var data = cb.Data;
            int segStart = 0;
            foreach (var seg in cb.Segments)
            {
                int passes = seg[0], len = seg[2];
                int start = segStart;
                segStart += len;
                bool raw = bypass && passIdx >= 10 && kind != 2;
                if (raw)
                {
                    _raw = data;
                    _rawPos = start;
                    _rawEnd = Math.Min(start + len, cb.DataLen);
                    _rawBits = 0;
                    _rawCur = 0;
                }
                else _mq.Init(data, start, Math.Min(start + len, cb.DataLen));
                for (int k = 0; k < passes && bp >= 0; k++)
                {
                    switch (kind)
                    {
                        case 0: SignificancePass(bp, raw); break;
                        case 1: RefinementPass(bp, raw); break;
                        default:
                            CleanupPass(bp);
                            if (segsym)
                                for (int s = 0; s < 4; s++) _mq.Decode(_cx, 18);
                            break;
                    }
                    if (reset) ResetContexts();
                    if (kind == 2) { kind = 0; bp--; }
                    else kind++;
                    passIdx++;
                }
                if (bp < 0) break;
            }

            // Write dequantized coefficients into the band.
            int ox = cb.X0 - band.X0 + band.OffX, oy = cb.Y0 - band.Y0 + band.OffY;
            float delta = band.Delta;
            bool scale = !reversible || delta != 1f;
            int roiThresh = roiShift > 0 && roiShift < 31 ? 1 << roiShift : int.MaxValue;
            for (int y = 0; y < _h; y++)
            {
                int fRow = (y + 1) * _fw + 1;
                int dRow = (oy + y) * stride + ox;
                int mRow = y * _w;
                for (int x = 0; x < _w; x++)
                {
                    int m = _mag[mRow + x];
                    if (m == 0) continue;
                    bool roi = m >= roiThresh;
                    int lb = _lastBp[mRow + x];
                    if (lb > 0) m += 1 << (lb - 1); // reconstruct at the middle of the undecoded range
                    if (roi) m >>= roiShift;
                    float v = scale ? m * delta : m;
                    dst[dRow + x] = (_flags[fRow + x] & Neg) != 0 ? -v : v;
                }
            }
        }

        private int RawBit()
        {
            if (_rawBits == 0)
            {
                if (_rawCur == 0xFF)
                {
                    int b = _rawPos < _rawEnd ? _raw[_rawPos] : 0xFF;
                    if (b > 0x8F) { _rawCur = 0xFF; _rawBits = 8; }
                    else { _rawCur = b; _rawPos++; _rawBits = 7; }
                }
                else
                {
                    _rawCur = _rawPos < _rawEnd ? _raw[_rawPos] : 0xFF;
                    _rawPos++;
                    _rawBits = 8;
                }
            }
            _rawBits--;
            return (_rawCur >> _rawBits) & 1;
        }

        /// <summary>Flags as seen from row <paramref name="y"/> (the next stripe is hidden in vertically causal mode).</summary>
        private int View(int f, int y) => _vsc && (y & 3) == 3 ? f & BelowMask : f;

        private int DecodeSign(int f, bool raw)
        {
            if (raw) return RawBit();
            int e = SignLut[SignIndex(f)];
            return _mq.Decode(_cx, e & 0x1F) ^ (e >> 7);
        }

        private void BecomeSignificant(int fi, int i, int y, int bp, bool raw)
        {
            int sign = DecodeSign(View(_flags[fi], y), raw);
            var fl = _flags;
            int fw = _fw;
            fl[fi] |= Sig | (sign != 0 ? Neg : 0);
            _mag[i] = 1 << bp;
            _lastBp[i] = (byte)bp;
            // Tell the neighbors.
            if (sign != 0)
            {
                fl[fi - 1] |= NbE | NegE;
                fl[fi + 1] |= NbW | NegW;
                fl[fi - fw] |= NbS | NegS;
                fl[fi + fw] |= NbN | NegN;
            }
            else
            {
                fl[fi - 1] |= NbE;
                fl[fi + 1] |= NbW;
                fl[fi - fw] |= NbS;
                fl[fi + fw] |= NbN;
            }
            fl[fi - fw - 1] |= NbSE;
            fl[fi - fw + 1] |= NbSW;
            fl[fi + fw - 1] |= NbNE;
            fl[fi + fw + 1] |= NbNW;
        }

        private void SignificancePass(int bp, bool raw)
        {
            var fl = _flags;
            var zc = _zc;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi = (y0 + 1) * _fw + x + 1;
                    for (int y = y0; y < y1; y++, fi += _fw)
                    {
                        int f = fl[fi];
                        if ((f & Sig) != 0) continue;
                        f = View(f, y);
                        if ((f & NbAll) == 0) continue;
                        int bit = raw ? RawBit() : _mq.Decode(_cx, zc[f >> 4 & 0xFF]);
                        if (bit != 0) BecomeSignificant(fi, y * _w + x, y, bp, raw);
                        fl[fi] |= Visit;
                    }
                }
            }
        }

        private void RefinementPass(int bp, bool raw)
        {
            var fl = _flags;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi = (y0 + 1) * _fw + x + 1;
                    for (int y = y0; y < y1; y++, fi += _fw)
                    {
                        int f = fl[fi];
                        if ((f & (Sig | Visit)) != Sig) continue;
                        int bit;
                        if (raw) bit = RawBit();
                        else
                        {
                            int ctx = (f & Refined) != 0 ? 16 : (View(f, y) & NbAll) != 0 ? 15 : 14;
                            bit = _mq.Decode(_cx, ctx);
                        }
                        int i = y * _w + x;
                        if (bit != 0) _mag[i] |= 1 << bp;
                        _lastBp[i] = (byte)bp;
                        fl[fi] = f | Refined;
                    }
                }
            }
        }

        private void CleanupPass(int bp)
        {
            var fl = _flags;
            var zc = _zc;
            int fw = _fw;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(y0 + 4, _h);
                for (int x = 0; x < _w; x++)
                {
                    int fi0 = (y0 + 1) * fw + x + 1;
                    int yStart = y0;
                    if (y1 - y0 == 4
                        && ((fl[fi0] | fl[fi0 + fw] | fl[fi0 + 2 * fw] | View(fl[fi0 + 3 * fw], 3)) & (Sig | Visit | NbAll)) == 0)
                    {
                        // Run mode: one decision for four insignificant coefficients with insignificant neighbors.
                        if (_mq.Decode(_cx, 17) == 0) continue;
                        int r = _mq.Decode(_cx, 18) << 1;
                        r |= _mq.Decode(_cx, 18);
                        int y = y0 + r;
                        BecomeSignificant(fi0 + r * fw, y * _w + x, y, bp, false);
                        yStart = y + 1;
                    }
                    int fi = fi0 + (yStart - y0) * fw;
                    for (int y = yStart; y < y1; y++, fi += fw)
                    {
                        int f = fl[fi];
                        if ((f & (Sig | Visit)) != 0) continue;
                        if (_mq.Decode(_cx, zc[View(f, y) >> 4 & 0xFF]) != 0) BecomeSignificant(fi, y * _w + x, y, bp, false);
                    }
                }
            }
            // Clear the visited marks for the next bit-plane.
            for (int y = 0; y < _h; y++)
            {
                int f = (y + 1) * fw + 1;
                for (int x = 0; x < _w; x++) fl[f + x] &= ~Visit;
            }
        }
    }

    // ------------------------------------------------------------------ inverse wavelet transform

    // The tile-component buffer holds the sub-bands in the usual layout: at each level the low-pass image sits at the
    // top left with HL to its right, LH below and HH diagonally. Each level is synthesized in place.
    private const int Strip = 32; // columns per vertical pass

    private void InverseDwt(TileComp tc)
    {
        var buf = tc.Buffer;
        int stride = tc.X1 - tc.X0;
        bool rev = tc.Cs.Reversible;
        for (int r = 1; r < tc.Res.Length; r++)
        {
            var res = tc.Res[r];
            int w = res.X1 - res.X0, h = res.Y1 - res.Y0;
            if (w <= 0 || h <= 0) continue;
            int lowW = CeilDivPow2(res.X1, 1) - CeilDivPow2(res.X0, 1), lowH = CeilDivPow2(res.Y1, 1) - CeilDivPow2(res.Y0, 1);

            // Rows: interleave low and high halves, then synthesize.
            int xLow = res.X0 & 1, x0Abs = res.X0;
            int minParallel = (long)w * h >= 1 << 18 ? 16 : int.MaxValue;
            ParallelRange(h, minParallel, () => new float[w], (y, row) =>
            {
                int o = y * stride;
                for (int i = 0, x = xLow; i < lowW; i++, x += 2) row[x] = buf[o + i];
                for (int i = lowW, x = 1 - xLow; i < w; i++, x += 2) row[x] = buf[o + i];
                SynthesizeRow(row.AsSpan(0, w), x0Abs, rev);
                Array.Copy(row, 0, buf, o, w);
            });

            // Columns, a strip at a time: gather rows interleaved, lift whole rows, scatter back.
            int yLow = res.Y0 & 1, y0Abs = res.Y0;
            ParallelRange((w + Strip - 1) / Strip, minParallel, () => new float[Strip * h], (k, strip) =>
            {
                int x0 = k * Strip;
                int sw = Math.Min(Strip, w - x0);
                for (int j = 0; j < h; j++)
                {
                    int dst = j < lowH ? yLow + 2 * j : 1 - yLow + 2 * (j - lowH);
                    Array.Copy(buf, j * stride + x0, strip, dst * sw, sw);
                }
                SynthesizeColumns(strip, sw, h, y0Abs, rev);
                for (int j = 0; j < h; j++) Array.Copy(strip, j * sw, buf, j * stride + x0, sw);
            });
        }
    }

    private const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f,
        Delta = 0.443506852043971f, K = 1.230174104914001f;

    // Lifting runs in place: with whole-sample symmetric extension every step's output stays symmetric, so a
    // neighbor past either end is its mirror image inside the signal.

    /// <summary>1-D synthesis of one row whose first sample has absolute index <paramref name="i0"/>.</summary>
    private static void SynthesizeRow(Span<float> x, int i0, bool reversible)
    {
        int n = x.Length;
        if (n == 1)
        {
            if ((i0 & 1) != 0) x[0] /= 2;
            return;
        }
        int even = i0 & 1, odd = 1 - even; // first local index of each parity
        if (reversible)
        {
            for (int j = even; j < n; j += 2) x[j] -= MathF.Floor((x[Mirror(j - 1, n)] + x[Mirror(j + 1, n)] + 2) / 4);
            for (int j = odd; j < n; j += 2) x[j] += MathF.Floor((x[Mirror(j - 1, n)] + x[Mirror(j + 1, n)]) / 2);
            return;
        }
        for (int j = even; j < n; j += 2) x[j] *= K;
        for (int j = odd; j < n; j += 2) x[j] *= 1 / K;
        Lift(x, even, Delta);
        Lift(x, odd, Gamma);
        Lift(x, even, Beta);
        Lift(x, odd, Alpha);
    }

    private static void Lift(Span<float> x, int start, float c)
    {
        int n = x.Length;
        int j = start;
        if (j == 0)
        {
            x[0] -= c * 2 * x[1];
            j = 2;
        }
        for (; j < n - 1; j += 2) x[j] -= c * (x[j - 1] + x[j + 1]);
        if (j == n - 1) x[j] -= c * 2 * x[n - 2];
    }

    private static int Mirror(int i, int n) => i < 0 ? -i : i >= n ? 2 * (n - 1) - i : i;

    /// <summary>Vertical synthesis, processing whole rows at a time.</summary>
    private static void SynthesizeColumns(float[] a, int w, int h, int i0, bool reversible)
    {
        if (h == 1)
        {
            if ((i0 & 1) != 0) for (int x = 0; x < w; x++) a[x] /= 2;
            return;
        }
        int even = i0 & 1, odd = 1 - even;
        if (reversible)
        {
            for (int j = even; j < h; j += 2) RowLift53(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), true);
            for (int j = odd; j < h; j += 2) RowLift53(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), false);
            return;
        }
        for (int j = 0; j < h; j++) RowScale(a.AsSpan(j * w, w), ((j - even) & 1) == 0 ? K : 1 / K);
        foreach (var (start, c) in new[] { (even, Delta), (odd, Gamma), (even, Beta), (odd, Alpha) })
            for (int j = start; j < h; j += 2) RowLift97(a, w, j, Mirror(j - 1, h), Mirror(j + 1, h), c);
    }

    private static void RowScale(Span<float> row, float s)
    {
        int x = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            int vn = System.Numerics.Vector<float>.Count;
            var vs = new System.Numerics.Vector<float>(s);
            for (; x + vn <= row.Length; x += vn)
                (new System.Numerics.Vector<float>(row.Slice(x, vn)) * vs).CopyTo(row.Slice(x, vn));
        }
        for (; x < row.Length; x++) row[x] *= s;
    }

    private static void RowLift97(float[] a, int w, int j, int ja, int jb, float c)
    {
        var dst = a.AsSpan(j * w, w);
        var pa = a.AsSpan(ja * w, w);
        var pb = a.AsSpan(jb * w, w);
        int x = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            int vn = System.Numerics.Vector<float>.Count;
            var vc = new System.Numerics.Vector<float>(c);
            for (; x + vn <= w; x += vn)
            {
                var d = new System.Numerics.Vector<float>(dst.Slice(x, vn));
                var s = new System.Numerics.Vector<float>(pa.Slice(x, vn)) + new System.Numerics.Vector<float>(pb.Slice(x, vn));
                (d - vc * s).CopyTo(dst.Slice(x, vn));
            }
        }
        for (; x < w; x++) dst[x] -= c * (pa[x] + pb[x]);
    }

    private static void RowLift53(float[] a, int w, int j, int ja, int jb, bool lowStep)
    {
        var dst = a.AsSpan(j * w, w);
        var pa = a.AsSpan(ja * w, w);
        var pb = a.AsSpan(jb * w, w);
        int x = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            int vn = System.Numerics.Vector<float>.Count;
            var two = new System.Numerics.Vector<float>(2f);
            var quarter = new System.Numerics.Vector<float>(0.25f);
            var half = new System.Numerics.Vector<float>(0.5f);
            for (; x + vn <= w; x += vn)
            {
                var d = new System.Numerics.Vector<float>(dst.Slice(x, vn));
                var s = new System.Numerics.Vector<float>(pa.Slice(x, vn)) + new System.Numerics.Vector<float>(pb.Slice(x, vn));
                var r = lowStep ? d - System.Numerics.Vector.Floor((s + two) * quarter) : d + System.Numerics.Vector.Floor(s * half);
                r.CopyTo(dst.Slice(x, vn));
            }
        }
        for (; x < w; x++)
            dst[x] = lowStep ? dst[x] - MathF.Floor((pa[x] + pb[x] + 2) / 4) : dst[x] + MathF.Floor((pa[x] + pb[x]) / 2);
    }

    // ------------------------------------------------------------------ output

    private JpxImage? Compose(Jp2Header? h)
    {
        int nc = _comps.Length;
        // When every component is subsampled alike, the image is the components' own size (as other readers show it).
        bool uniform = true;
        for (int c = 1; c < nc; c++) uniform &= _comps[c].XR == _comps[0].XR && _comps[c].YR == _comps[0].YR;
        int width = uniform ? _planeW[0] : _xsiz - _x0, height = uniform ? _planeH[0] : _ysiz - _y0;
        if (width <= 0 || height <= 0) return null;

        // Channels: components, or palette columns when a palette maps an index component.
        var channels = new List<byte[]>();
        if (h?.Palette != null && h.Cmap != null)
        {
            foreach (var (comp, type, col) in h.Cmap)
            {
                if (comp >= nc) return null;
                var full = Upsample(comp, width, height);
                if (type == 1 && col < h.PaletteBits.Length)
                {
                    var pal = h.Palette;
                    int bits = h.PaletteBits[col];
                    int prec = _comps[comp].Precision;
                    var mapped = new byte[full.Length];
                    for (int i = 0; i < full.Length; i++)
                    {
                        // Planes hold 8-bit values; recover the index at its original precision.
                        int idx = prec == 8 ? full[i] : (int)Math.Round(full[i] * ((1 << prec) - 1) / 255.0);
                        int v = idx < pal.Length ? pal[idx][col] : 0;
                        mapped[i] = bits == 8 ? (byte)v : (byte)(v * 255 / ((1 << bits) - 1));
                    }
                    channels.Add(mapped);
                }
                else channels.Add(full);
            }
        }
        else
        {
            for (int c = 0; c < nc; c++) channels.Add(Upsample(c, width, height));
        }

        // Channel definitions: color channels in order, opacity separately.
        byte[]? alpha = null;
        var color = new List<byte[]>();
        if (h?.Cdef is { Length: > 0 } cdef)
        {
            var ordered = new SortedDictionary<int, byte[]>();
            foreach (var (ch, type, assoc) in cdef)
            {
                if (ch >= channels.Count) continue;
                if (type is 1 or 2) alpha ??= channels[ch];
                else if (type == 0) ordered[assoc is 0 or 65535 ? 1000 + ch : assoc] = channels[ch];
            }
            color.AddRange(ordered.Values);
            for (int i = 0; i < channels.Count && color.Count == 0; i++) color.Add(channels[i]);
        }
        else color.AddRange(channels);

        int expected = h?.EnumCs switch
        {
            16 or 18 or 21 => 3, // sRGB, sYCC, ROMM-RGB
            17 => 1,
            12 => 4,
            _ => h?.IccComponents ?? 0,
        };
        if (expected == 0) expected = color.Count >= 4 ? 4 : color.Count >= 3 ? 3 : 1;
        if (color.Count > expected)
        {
            if (alpha == null && h?.Cdef == null && color.Count == expected + 1) alpha = color[expected];
            color.RemoveRange(expected, color.Count - expected);
        }
        if (color.Count == 2) { alpha ??= color[1]; color.RemoveAt(1); }

        if (h?.EnumCs == 18 && color.Count == 3) YccToRgb(color[0], color[1], color[2]);

        int n = color.Count;
        var data = new byte[(long)width * height * n];
        for (int c = 0; c < n; c++)
        {
            var src = color[c];
            for (int i = 0, o = c; i < src.Length; i++, o += n) data[o] = src[i];
        }
        return new JpxImage
        {
            Width = width,
            Height = height,
            Components = n,
            Data = data,
            Alpha = alpha,
            Color = n switch { 1 => JpxColor.Gray, 3 => JpxColor.Rgb, 4 => JpxColor.Cmyk, _ => JpxColor.Unknown },
        };
    }

    /// <summary>A component at full image resolution (subsampled components are repeated).</summary>
    private byte[] Upsample(int c, int width, int height)
    {
        var ci = _comps[c];
        var plane = _planes[c];
        if (_planeW[c] == width && _planeH[c] == height) return plane;
        PdfFile.ChargeDecoded((long)width * height);
        var o = new byte[width * height];
        int pw = _planeW[c], ph = _planeH[c];
        if (pw <= 0 || ph <= 0) return o;
        var xs = new int[width];
        for (int x = 0; x < width; x++) xs[x] = Math.Clamp((_x0 + x) / ci.XR - _planeX0[c], 0, pw - 1);
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp((_y0 + y) / ci.YR - _planeY0[c], 0, ph - 1);
            int srow = sy * pw, orow = y * width;
            for (int x = 0; x < width; x++) o[orow + x] = plane[srow + xs[x]];
        }
        return o;
    }

    private static void YccToRgb(byte[] y, byte[] cb, byte[] cr)
    {
        for (int i = 0; i < y.Length; i++)
        {
            float yy = y[i], u = cb[i] - 128, v = cr[i] - 128;
            y[i] = Clamp8(yy + 1.402f * v);
            cb[i] = Clamp8(yy - 0.344136f * u - 0.714136f * v);
            cr[i] = Clamp8(yy + 1.772f * u);
        }
    }

    private static byte Clamp8(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
}

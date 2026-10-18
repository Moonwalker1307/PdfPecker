using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

// Tier 2 (ISO/IEC 15444-1, annex B): the packets of a tile, in their progression order. Each packet header says which
// code-blocks of a precinct have new coding passes in a layer and how many bytes they take in the packet body.
internal sealed partial class JpxDecoder
{
    // Progression orders: the nesting of layer, resolution, component and position, outermost first.
    private const int OrderLrcp = 0, OrderRlcp = 1, OrderRpcl = 2, OrderPcrl = 3;

    private const int StartOfPacketLength = 6; // the marker, its segment length and the packet index
    private const int MaxZeroBitPlanes = 64, MaxLengthBits = 32, MaxSegmentLengthBits = 31;

    // Code-block style flags (SPcod), which decide where codeword segments end.
    private const int CodeBlockBypass = 1, CodeBlockReset = 2, CodeBlockTerminateAll = 4, CodeBlockCausal = 8, CodeBlockSegmentSymbols = 0x20;

    // In bypass mode the first ten passes (the cleanup pass of the top bit-plane and three more bit-planes) are
    // arithmetic-coded; after that the significance and refinement passes of each bit-plane are raw.
    private const int ArithmeticPassesBeforeBypass = 10;

    /// <summary>A precinct of a tile-component and the point on the reference grid where it starts, for the positional orders.</summary>
    private readonly record struct PrecinctRef(int Component, int Resolution, int Precinct, long X, long Y);

    /// <summary>A tile's packet data and how far it has been read. Packet headers are in the body or packed apart from it.</summary>
    private sealed class TilePackets
    {
        public TilePackets(byte[] body, byte[]? packedHeaders)
        {
            Body = body;
            HeadersPacked = packedHeaders != null;
            if (packedHeaders != null) Header.Reset(packedHeaders, 0, packedHeaders.Length);
        }

        public byte[] Body { get; }
        public int BodyPosition;
        public bool HeadersPacked { get; }
        public PacketReader Header { get; } = new();

        /// <summary>The code-blocks of the packet being read and the bytes each has in its body.</summary>
        public List<(CodeBlock Block, int Bytes)> Included { get; } = new();

        /// <summary>Every precinct and where it starts, worked out when a positional order first needs them.</summary>
        public List<PrecinctRef>? Positions;
    }

    private void ReadPackets(TileData tile, GeneralCodingStyle general, TileComponent[] components, byte[] body, byte[]? packedHeaders)
    {
        int resolutions = 0;
        foreach (var tc in components) resolutions = Math.Max(resolutions, tc.Style.Levels + 1);
        var changes = tile.Parameters.ProgressionChanges.Count > 0 ? tile.Parameters.ProgressionChanges : _main.ProgressionChanges;
        var packets = new TilePackets(body, packedHeaders);
        foreach (var change in changes)
            if (!ReadProgression(packets, tile, general, components, change, resolutions)) return;
        // The default progression covers whatever the progression changes leave out.
        ReadProgression(packets, tile, general, components, new ProgressionChange(0, 0, general.Layers, resolutions, components.Length, general.Progression), resolutions);
    }

    /// <summary>Reads the packets of one progression. Returns false when the data has run out.</summary>
    private bool ReadProgression(TilePackets packets, TileData tile, GeneralCodingStyle general, TileComponent[] components, ProgressionChange change, int resolutions)
    {
        int layers = Math.Min(change.EndLayer, general.Layers), endResolution = Math.Min(change.EndResolution, resolutions);
        int endComponent = Math.Min(change.EndComponent, components.Length);
        switch (change.Order)
        {
            case OrderLrcp:
                for (int l = 0; l < layers; l++)
                    for (int r = change.FirstResolution; r < endResolution; r++)
                        for (int c = change.FirstComponent; c < endComponent; c++)
                            if (!ReadPacketsOf(packets, general, components[c], r, l)) return false;
                return true;
            case OrderRlcp:
                for (int r = change.FirstResolution; r < endResolution; r++)
                    for (int l = 0; l < layers; l++)
                        for (int c = change.FirstComponent; c < endComponent; c++)
                            if (!ReadPacketsOf(packets, general, components[c], r, l)) return false;
                return true;
            default: // RPCL, PCRL and CPRL, which visit precincts by where they start
            {
                packets.Positions ??= PrecinctPositions(tile, components);
                var precincts = new List<PrecinctRef>();
                foreach (var p in packets.Positions)
                    if (p.Component >= change.FirstComponent && p.Component < endComponent && p.Resolution >= change.FirstResolution && p.Resolution < endResolution)
                        precincts.Add(p);
                precincts.Sort(PositionalOrder(change.Order));
                foreach (var p in precincts)
                {
                    var res = components[p.Component].Resolutions[p.Resolution];
                    for (int l = res.NextLayer[p.Precinct]; l < layers; l++)
                        if (!ReadPacket(packets, general, res, p.Precinct, l)) return false;
                }
                return true;
            }
        }
    }

    /// <summary>The comparison for a positional order; an unknown order is read as CPRL.</summary>
    private static Comparison<PrecinctRef> PositionalOrder(int order) => order switch
    {
        OrderRpcl => static (a, b) => Compare(a.Resolution, b.Resolution, a.Y, b.Y, a.X, b.X, a.Component, b.Component),
        OrderPcrl => static (a, b) => Compare(a.Y, b.Y, a.X, b.X, a.Component, b.Component, a.Resolution, b.Resolution),
        _ => static (a, b) => Compare(a.Component, b.Component, a.Y, b.Y, a.X, b.X, a.Resolution, b.Resolution),
    };

    private static int Compare(long a0, long b0, long a1, long b1, long a2, long b2, long a3, long b3)
    {
        if (a0 != b0) return a0.CompareTo(b0);
        if (a1 != b1) return a1.CompareTo(b1);
        if (a2 != b2) return a2.CompareTo(b2);
        return a3.CompareTo(b3);
    }

    /// <summary>
    /// Every precinct of the tile with where it starts on the reference grid. A precinct cut off by the tile's left or top
    /// edge counts as starting at the tile's corner.
    /// </summary>
    private List<PrecinctRef> PrecinctPositions(TileData tile, TileComponent[] components)
    {
        var (tileX0, tileY0, _, _) = TileArea(tile.Index);
        var list = new List<PrecinctRef>();
        for (int c = 0; c < components.Length; c++)
        {
            var tc = components[c];
            var component = _components[c];
            int levels = tc.Style.Levels;
            for (int r = 0; r <= levels; r++)
            {
                var res = tc.Resolutions[r];
                long scaleX = (long)component.SubsamplingX << (levels - r), scaleY = (long)component.SubsamplingY << (levels - r);
                for (int py = 0; py < res.PrecinctsDown; py++)
                {
                    long startY = Math.Max(res.Y0, (long)(res.PrecinctY0 + py) << res.PrecinctHeightLog2);
                    long y = (startY & ((1L << res.PrecinctHeightLog2) - 1)) == 0 ? startY * scaleY : tileY0;
                    for (int px = 0; px < res.PrecinctsAcross; px++)
                    {
                        long startX = Math.Max(res.X0, (long)(res.PrecinctX0 + px) << res.PrecinctWidthLog2);
                        long x = (startX & ((1L << res.PrecinctWidthLog2) - 1)) == 0 ? startX * scaleX : tileX0;
                        list.Add(new PrecinctRef(c, r, py * res.PrecinctsAcross + px, x, y));
                    }
                }
            }
        }
        return list;
    }

    /// <summary>Reads the packets of a layer of every precinct of a resolution. Returns false when the data has run out.</summary>
    private static bool ReadPacketsOf(TilePackets packets, GeneralCodingStyle general, TileComponent tc, int r, int layer)
    {
        if (r > tc.Style.Levels) return true;
        var res = tc.Resolutions[r];
        int count = res.PrecinctsAcross * res.PrecinctsDown;
        for (int p = 0; p < count; p++)
            if (res.NextLayer[p] == layer && !ReadPacket(packets, general, res, p, layer)) return false;
        return true;
    }

    /// <summary>Reads one packet: its header, then the new bytes of each code-block it includes. Returns false when the data has run out.</summary>
    private static bool ReadPacket(TilePackets packets, GeneralCodingStyle general, Resolution res, int precinct, int layer)
    {
        if (res.NextLayer[precinct] != layer) return true;
        res.NextLayer[precinct] = layer + 1;
        var body = packets.Body;
        var header = packets.Header;
        if (general.StartOfPacketMarkers && packets.BodyPosition + StartOfPacketLength <= body.Length && IsMarkerAt(body, packets.BodyPosition, StartOfPacket))
            packets.BodyPosition += StartOfPacketLength;
        if (!packets.HeadersPacked)
        {
            if (packets.BodyPosition >= body.Length) return false;
            header.Reset(body, packets.BodyPosition, body.Length);
        }
        else if (header.Position >= header.End) return false;

        var included = packets.Included;
        included.Clear();
        // The first bit says whether the packet has any code-block contributions at all.
        if (header.Bit() != 0)
        {
            foreach (var part in res.Precincts[precinct])
                if (!ReadContributions(header, part, layer, res.CodeBlockStyle, included)) return false;
        }
        header.Align();
        if (header.Overrun) return false;
        if (general.EndOfPacketHeaderMarkers) header.SkipMarker(EndOfPacketHeader);
        if (!packets.HeadersPacked) packets.BodyPosition = header.Position;
        return ReadPacketBody(packets, included);
    }

    private static bool IsMarkerAt(byte[] data, int at, int marker) => data[at] == marker >> 8 && data[at + 1] == (marker & 0xFF);

    /// <summary>
    /// Reads the packet header's entries for the code-blocks of one sub-band of the precinct: whether each is included,
    /// and for those that are, their new passes and the byte length of each codeword segment they add to. Returns false
    /// when the header is damaged or cut off.
    /// </summary>
    private static bool ReadContributions(PacketReader header, BandPrecinct part, int layer, int blockStyle, List<(CodeBlock, int)> included)
    {
        for (int i = 0; i < part.Blocks.Length; i++)
        {
            var block = part.Blocks[i];
            // A code-block's first inclusion is coded in a tag tree by layer; after that, one bit says whether it has more.
            bool contributes = block.Included ? header.Bit() != 0 : part.Inclusion!.Decode(i, layer + 1, header);
            if (header.Overrun) return false;
            if (!contributes) continue;
            if (!block.Included)
            {
                if (!ReadZeroBitPlanes(header, part, i)) return false;
                block.ZeroBitPlanes = part.ZeroBitPlanes!.Value(i);
                block.Included = true;
            }
            int passes = ReadPassCount(header);
            while (header.Bit() != 0)
            {
                if (++block.LengthBits > MaxLengthBits || header.Overrun) return false;
            }
            if (!ReadSegmentLengths(header, block, passes, blockStyle, out int bytes)) return false;
            included.Add((block, bytes));
        }
        return true;
    }

    /// <summary>Decodes the code-block's number of missing bit-planes from the tag tree. Returns false when the header is damaged.</summary>
    private static bool ReadZeroBitPlanes(PacketReader header, BandPrecinct part, int i)
    {
        int threshold = 1;
        while (!part.ZeroBitPlanes!.Decode(i, threshold, header))
        {
            if (header.Overrun || ++threshold > MaxZeroBitPlanes) return false;
        }
        return true;
    }

    /// <summary>
    /// Splits a code-block's new passes into its codeword segments and reads the byte length of each piece. The lengths
    /// are added to the segments now, and their bytes come with the packet body.
    /// </summary>
    private static bool ReadSegmentLengths(PacketReader header, CodeBlock block, int passes, int blockStyle, out int bytes)
    {
        bytes = 0;
        for (int remaining = passes; remaining > 0;)
        {
            var segments = block.Segments;
            if (segments.Count == 0 || segments[^1].Passes >= segments[^1].MaxPasses)
                segments.Add(new CodewordSegment(MaxPasses(blockStyle, segments.Count, block.Passes)));
            var segment = segments[^1];
            int take = Math.Min(remaining, segment.MaxPasses - segment.Passes);
            int bits = block.LengthBits + Log2(take);
            if (bits > MaxSegmentLengthBits) return false;
            int length = header.Bits(bits);
            segment.Passes += take;
            block.Passes += take;
            remaining -= take;
            bytes += length;
            segment.Length += length;
        }
        return true;
    }

    /// <summary>Copies the new bytes of each included code-block from the packet body. Returns false when the body is cut off.</summary>
    private static bool ReadPacketBody(TilePackets packets, List<(CodeBlock Block, int Bytes)> included)
    {
        var body = packets.Body;
        foreach (var (block, bytes) in included)
        {
            int available = Math.Max(0, Math.Min(bytes, body.Length - packets.BodyPosition));
            if (block.DataLength + available > block.Data.Length)
            {
                int capacity = Math.Max(block.Data.Length * 2, block.DataLength + available + 2);
                PdfFile.ChargeDecoded(capacity - block.Data.Length);
                Array.Resize(ref block.Data, capacity);
            }
            Buffer.BlockCopy(body, packets.BodyPosition, block.Data, block.DataLength, available);
            block.DataLength += available;
            packets.BodyPosition += available;
            if (available < bytes)
            {
                // Cut off: shorten the segments, from the last, to the data that arrived.
                int missing = bytes - available;
                for (int k = block.Segments.Count - 1; k >= 0 && missing > 0; k--)
                {
                    int cut = Math.Min(missing, block.Segments[k].Length);
                    block.Segments[k].Length -= cut;
                    missing -= cut;
                }
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The number of passes in the codeword segment that starts after <paramref name="passesBefore"/> passes, as the
    /// code-block style terminates them.
    /// </summary>
    private static int MaxPasses(int blockStyle, int segment, int passesBefore)
    {
        if ((blockStyle & CodeBlockTerminateAll) != 0) return 1;
        if ((blockStyle & CodeBlockBypass) != 0)
        {
            // The arithmetic-coded passes, then raw (significance and refinement) and arithmetic (cleanup) segments in turn.
            if (segment == 0) return ArithmeticPassesBeforeBypass;
            return passesBefore < ArithmeticPassesBeforeBypass
                ? ArithmeticPassesBeforeBypass - passesBefore
                : (passesBefore - ArithmeticPassesBeforeBypass) % 3 == 0 ? 2 : 1;
        }
        return int.MaxValue;
    }

    /// <summary>The number of new coding passes (ISO/IEC 15444-1, table B.4).</summary>
    private static int ReadPassCount(PacketReader header)
    {
        if (header.Bit() == 0) return 1;
        if (header.Bit() == 0) return 2;
        int v = header.Bits(2);
        if (v != 3) return 3 + v;
        v = header.Bits(5);
        if (v != 31) return 6 + v;
        return 37 + header.Bits(7);
    }

    private static int Log2(int v)
    {
        int n = 0;
        while (v > 1)
        {
            v >>= 1;
            n++;
        }
        return n;
    }

    /// <summary>
    /// A tag tree (ISO/IEC 15444-1, B.10.2): a quadtree of values over a grid of code-blocks, each node the minimum of its
    /// children, coded from the root down only as far as a decoder asks.
    /// </summary>
    private sealed class TagTree
    {
        private readonly int[] _value, _low, _parent;

        public TagTree(int width, int height)
        {
            // Each level halves the one below it, up to a single root.
            var levels = new List<(int Width, int Height)>();
            int total = 0;
            for (int w = width, h = height; ; w = (w + 1) >> 1, h = (h + 1) >> 1)
            {
                levels.Add((w, h));
                total += w * h;
                if (w <= 1 && h <= 1) break;
            }
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
                        _parent[offset + y * cw + x] = l + 1 < levels.Count ? next + (y >> 1) * levels[l + 1].Width + (x >> 1) : -1;
                offset = next;
            }
        }

        /// <summary>Decodes until the leaf's value is known to be below <paramref name="threshold"/> or not.</summary>
        public bool Decode(int leaf, int threshold, PacketReader header)
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
                    if (header.Bit() != 0) _value[node] = low;
                    else low++;
                }
                _low[node] = low;
            }
            return _value[leaf] < threshold;
        }

        public int Value(int leaf) => _value[leaf];
    }

    /// <summary>Reads packet-header bits with JPEG 2000 bit stuffing: a byte after 0xFF holds only 7 bits.</summary>
    private sealed class PacketReader
    {
        private byte[] _data = Array.Empty<byte>();
        private int _current, _bitsLeft;
        private bool _afterFF;

        public int Position { get; private set; }
        public int End { get; private set; }

        /// <summary>Whether a read went past the end; the missing bits read as zero.</summary>
        public bool Overrun { get; private set; }

        public int Bit()
        {
            if (_bitsLeft == 0)
            {
                if (Position >= End)
                {
                    Overrun = true;
                    _current = 0;
                }
                else _current = _data[Position];
                Position++;
                _bitsLeft = _afterFF ? 7 : 8;
                _afterFF = _current == 0xFF;
            }
            _bitsLeft--;
            return (_current >> _bitsLeft) & 1;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = v << 1 | Bit();
            return v;
        }

        /// <summary>Moves to the next byte boundary; a 0xFF byte is followed by a stuffed one, which is skipped.</summary>
        public void Align()
        {
            _bitsLeft = 0;
            if (_afterFF) Position++;
            _afterFF = false;
        }

        /// <summary>Skips <paramref name="marker"/> when it comes next.</summary>
        public void SkipMarker(int marker)
        {
            if (Position + MarkerLength <= End && IsMarkerAt(_data, Position, marker)) Position += MarkerLength;
        }

        public void Reset(byte[] data, int position, int end)
        {
            _data = data;
            Position = position;
            End = end;
            _bitsLeft = 0;
            _afterFF = false;
            Overrun = false;
        }
    }
}

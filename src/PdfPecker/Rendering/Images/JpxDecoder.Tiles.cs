using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering.Images;

// Tiles: each tile-component is laid out as resolutions, sub-bands, precincts and code-blocks (ISO/IEC 15444-1, annex B),
// filled from the packets, decoded, transformed back and stored into the component planes.
internal sealed partial class JpxDecoder
{
    // Sub-band orientations: the low-pass band of the lowest resolution, then the three high-pass bands of each level.
    private const int BandLL = 0, BandHL = 1, BandLH = 2, BandHH = 3;

    // The estimated bytes of a coefficient, a code-block and a precinct, which the decode budget is charged for.
    private const long CoefficientCost = 4, CodeBlockCost = 160, PrecinctCost = 64;
    private const long MaxPrecincts = 1 << 24;

    /// <summary>A tile-component: its area on its component's grid, its structure and, once decoded, its coefficients.</summary>
    private sealed class TileComponent
    {
        public int X0, Y0, X1, Y1;
        public ComponentCodingStyle Style = null!;
        public Resolution[] Resolutions = Array.Empty<Resolution>();
        public int RoiShift;
        public float[] Buffer = Array.Empty<float>();
        public int Width => X1 - X0;
        public int Height => Y1 - Y0;
    }

    /// <summary>A resolution level: its area, its precinct partition and code-block size, and its sub-bands.</summary>
    private sealed class Resolution
    {
        public int X0, Y0, X1, Y1;
        public int PrecinctWidthLog2, PrecinctHeightLog2;
        public int PrecinctX0, PrecinctY0, PrecinctsAcross, PrecinctsDown;
        public Band[] Bands = Array.Empty<Band>();
        public BandPrecinct[][] Precincts = Array.Empty<BandPrecinct[]>(); // [precinct][band]
        public int[] NextLayer = Array.Empty<int>(); // per precinct, the layer of its next packet
        public int CodeBlockWidthLog2, CodeBlockHeightLog2;
        public int CodeBlockStyle;
    }

    /// <summary>A sub-band: its area, where it lies in the tile-component buffer, and its quantization.</summary>
    private sealed class Band
    {
        public int Orientation;
        public int X0, Y0, X1, Y1;
        public int OffsetX, OffsetY;
        public int MaxBitPlanes;
        public float StepSize;
    }

    /// <summary>The part of a sub-band in a precinct: its code-blocks and the tag trees of their packet headers.</summary>
    private sealed class BandPrecinct
    {
        public int Band; // index into Resolution.Bands
        public int BlocksAcross, BlocksDown;
        public CodeBlock[] Blocks = Array.Empty<CodeBlock>();
        public TagTree? Inclusion, ZeroBitPlanes;
    }

    /// <summary>A code-block: its area in the sub-band, its state across packets and the coded bytes they brought.</summary>
    private sealed class CodeBlock
    {
        public int X0, Y0, X1, Y1;
        public bool Included;
        public int LengthBits = 3; // Lblock
        public int ZeroBitPlanes;
        public int Passes;
        public byte[] Data = Array.Empty<byte>();
        public int DataLength;
        public readonly List<CodewordSegment> Segments = new();
    }

    /// <summary>A run of coding passes that ends in a terminated codeword, and its length in bytes.</summary>
    private sealed class CodewordSegment(int maxPasses)
    {
        public int Passes;
        public int Length;
        public int MaxPasses { get; } = maxPasses;
    }

    // Tile-component buffers, reused from tile to tile.
    private float[][] _buffers = Array.Empty<float[]>();

    /// <summary>Decodes every tile. Returns false when there are tiles and none of them could be decoded.</summary>
    private bool DecodeTiles()
    {
        if (_main.Style == null || _main.Quantization == null) return false;
        bool any = false;
        foreach (var tile in _tileOrder)
        {
            PdfFile.CheckCancel();
            try
            {
                DecodeTile(tile);
                any = true;
            }
            catch (Exception ex) when (IsTileDamage(ex))
            {
                // A damaged tile leaves its area blank; other tiles still draw.
                SwallowedErrors.Record(ex);
            }
        }
        return any || _tileOrder.Count == 0;
    }

    /// <summary>
    /// The errors of damaged tile data. Running out of the page's decode budget is not one: it ends the page. Nor is
    /// running out of the image's own budget, which ends the image, because every later tile would need more.
    /// </summary>
    private static bool IsTileDamage(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or OverflowException
        || ex is InvalidDataException && !ex.Message.StartsWith("The page", StringComparison.Ordinal)
            && !string.Equals(ex.Message, OverBudget, StringComparison.Ordinal);

    private void DecodeTile(TileData tile)
    {
        var (x0, y0, x1, y1) = TileArea(tile.Index);
        if (x1 <= x0 || y1 <= y0) return;
        // DecodeTiles runs only after the main header's COD, which sets the general style with the component style.
        var general = tile.Parameters.General ?? _main.General!;
        var components = LayOutComponents(tile, x0, y0, x1, y1);
        ReadPackets(tile, general, components, JoinTileParts(tile), PackedHeaders(tile));
        for (int c = 0; c < components.Length; c++)
        {
            PdfFile.CheckCancel();
            var tc = components[c];
            tc.Buffer = RentBuffer(c, tc.Width * tc.Height);
            DecodeCodeBlocks(tc);
            InverseDwt(tc);
        }
        if (general.ComponentTransform == FirstThreeComponents && components.Length >= 3 && SameArea(components[0], components[1]) && SameArea(components[0], components[2]))
            InverseComponentTransform(components);
        for (int c = 0; c < components.Length; c++) StoreSamples(components[c], _components[c], _planes[c]);
    }

    /// <summary>A tile's area on the reference grid, cut to the image area.</summary>
    private (int X0, int Y0, int X1, int Y1) TileArea(int index)
    {
        int column = index % TilesAcross, row = index / TilesAcross;
        return ((int)Math.Max(_tileX0 + (long)column * _tileWidth, _imageX0), (int)Math.Max(_tileY0 + (long)row * _tileHeight, _imageY0),
            (int)Math.Min(_tileX0 + (long)(column + 1) * _tileWidth, _imageX1), (int)Math.Min(_tileY0 + (long)(row + 1) * _tileHeight, _imageY1));
    }

    /// <summary>
    /// Lays out each component of a tile with its own coding parameters, and charges the budget for its coefficients and
    /// structure before they are allocated.
    /// </summary>
    private TileComponent[] LayOutComponents(TileData tile, int x0, int y0, int x1, int y1)
    {
        var own = tile.Parameters;
        var components = new TileComponent[_components.Length];
        for (int c = 0; c < components.Length; c++)
        {
            // A tile's own segments take precedence over the main header's, and those for one component over defaults.
            var style = own.ComponentStyles.GetValueOrDefault(c) ?? own.Style ?? _main.ComponentStyles.GetValueOrDefault(c) ?? _main.Style!;
            var quantization = own.ComponentQuantizations.GetValueOrDefault(c) ?? own.Quantization
                ?? _main.ComponentQuantizations.GetValueOrDefault(c) ?? _main.Quantization!;
            var component = _components[c];
            var tc = new TileComponent
            {
                X0 = CeilDiv(x0, component.SubsamplingX), X1 = CeilDiv(x1, component.SubsamplingX),
                Y0 = CeilDiv(y0, component.SubsamplingY), Y1 = CeilDiv(y1, component.SubsamplingY),
                Style = style,
                RoiShift = own.RoiShifts.TryGetValue(c, out int shift) ? shift : _main.RoiShifts.GetValueOrDefault(c),
            };
            // A few header fields can describe a tile-component of millions of code-blocks, so the budget is charged
            // before its structure is built, not after.
            Charge(CoefficientCost * tc.Width * tc.Height);
            BuildResolutions(tc, component, quantization);
            components[c] = tc;
        }
        return components;
    }

    /// <summary>The packet data of a tile: the data of its tile-parts, joined.</summary>
    private byte[] JoinTileParts(TileData tile)
    {
        int total = 0;
        foreach (var (start, end) in tile.Parts) total += Math.Max(0, end - start);
        var body = new byte[total];
        int pos = 0;
        foreach (var (start, end) in tile.Parts)
        {
            if (end <= start) continue;
            _data.AsSpan(start, end - start).CopyTo(body.AsSpan(pos));
            pos += end - start;
        }
        return body;
    }

    /// <summary>A tile's packed packet headers, from the main header or else from its own, or null when its packets hold their headers.</summary>
    private static byte[]? PackedHeaders(TileData tile) =>
        tile.MainPackedHeaders.Count > 0 ? Concat(tile.MainPackedHeaders) : tile.PackedHeaders.Count > 0 ? Concat(tile.PackedHeaders) : null;

    private float[] RentBuffer(int c, int size)
    {
        if (_buffers.Length <= c) Array.Resize(ref _buffers, _components.Length);
        var buffer = _buffers[c];
        if (buffer == null || buffer.Length < size) _buffers[c] = buffer = new float[size];
        else Array.Clear(buffer, 0, size);
        return buffer;
    }

    private static bool SameArea(TileComponent a, TileComponent b) => a.X0 == b.X0 && a.X1 == b.X1 && a.Y0 == b.Y0 && a.Y1 == b.Y1;

    // ------------------------------------------------------------------ structure

    /// <summary>Lays out resolutions, sub-bands, precincts and code-blocks.</summary>
    private void BuildResolutions(TileComponent tc, ImageComponent component, Quantization quantization)
    {
        int levels = tc.Style.Levels;
        tc.Resolutions = new Resolution[levels + 1];
        for (int r = 0; r <= levels; r++)
        {
            var res = MakeResolution(tc, r);
            res.Bands = MakeBands(res, r, levels, component, quantization);
            MakePrecincts(res, r);
            tc.Resolutions[r] = res;
        }
    }

    private static Resolution MakeResolution(TileComponent tc, int r)
    {
        var style = tc.Style;
        int shift = style.Levels - r;
        var res = new Resolution
        {
            X0 = CeilDivPow2(tc.X0, shift), X1 = CeilDivPow2(tc.X1, shift),
            Y0 = CeilDivPow2(tc.Y0, shift), Y1 = CeilDivPow2(tc.Y1, shift),
            PrecinctWidthLog2 = style.PrecinctWidthLog2[r], PrecinctHeightLog2 = style.PrecinctHeightLog2[r],
            CodeBlockStyle = style.CodeBlockStyle,
        };
        // Above the lowest resolution a precinct is halved for the sub-bands, so it is at least two samples across.
        if (r > 0 && res.PrecinctWidthLog2 == 0) res.PrecinctWidthLog2 = 1;
        if (r > 0 && res.PrecinctHeightLog2 == 0) res.PrecinctHeightLog2 = 1;
        // A code-block never spans more than a precinct of its sub-band.
        res.CodeBlockWidthLog2 = Math.Min(style.CodeBlockWidthLog2, r == 0 ? res.PrecinctWidthLog2 : res.PrecinctWidthLog2 - 1);
        res.CodeBlockHeightLog2 = Math.Min(style.CodeBlockHeightLog2, r == 0 ? res.PrecinctHeightLog2 : res.PrecinctHeightLog2 - 1);
        if (res.X1 > res.X0 && res.Y1 > res.Y0)
        {
            res.PrecinctX0 = res.X0 >> res.PrecinctWidthLog2;
            res.PrecinctY0 = res.Y0 >> res.PrecinctHeightLog2;
            res.PrecinctsAcross = CeilDivPow2(res.X1, res.PrecinctWidthLog2) - res.PrecinctX0;
            res.PrecinctsDown = CeilDivPow2(res.Y1, res.PrecinctHeightLog2) - res.PrecinctY0;
        }
        return res;
    }

    /// <summary>
    /// The sub-bands of a resolution: LL for the lowest, else HL, LH and HH. They lie in the tile-component buffer as
    /// the inverse wavelet transform expects them, with the lower resolution at the top left.
    /// </summary>
    private static Band[] MakeBands(Resolution res, int r, int levels, ImageComponent component, Quantization quantization)
    {
        if (r == 0) return new[] { MakeBand(BandLL, res.X0, res.Y0, res.X1, res.Y1, component, quantization, levels, 0) };
        int lowX0 = CeilDivPow2(res.X0, 1), lowX1 = CeilDivPow2(res.X1, 1), highX0 = res.X0 >> 1, highX1 = res.X1 >> 1;
        int lowY0 = CeilDivPow2(res.Y0, 1), lowY1 = CeilDivPow2(res.Y1, 1), highY0 = res.Y0 >> 1, highY1 = res.Y1 >> 1;
        var bands = new[]
        {
            MakeBand(BandHL, highX0, lowY0, highX1, lowY1, component, quantization, levels, r),
            MakeBand(BandLH, lowX0, highY0, lowX1, highY1, component, quantization, levels, r),
            MakeBand(BandHH, highX0, highY0, highX1, highY1, component, quantization, levels, r),
        };
        int lowWidth = Math.Max(0, lowX1 - lowX0), lowHeight = Math.Max(0, lowY1 - lowY0);
        bands[0].OffsetX = lowWidth;
        bands[1].OffsetY = lowHeight;
        bands[2].OffsetX = lowWidth;
        bands[2].OffsetY = lowHeight;
        return bands;
    }

    /// <summary>A sub-band, with its number of magnitude bit-planes and quantization step (ISO/IEC 15444-1, E.1).</summary>
    private static Band MakeBand(int orientation, int x0, int y0, int x1, int y1, ImageComponent component, Quantization q, int levels, int r)
    {
        var band = new Band { Orientation = orientation, X0 = x0, Y0 = y0, X1 = Math.Max(x0, x1), Y1 = Math.Max(y0, y1) };
        int exponent, mantissa;
        if (q.Style == ScalarDerived)
        {
            // Only the LL band's step is given; the others follow from its decomposition level.
            int level = r == 0 ? levels : levels - r + 1;
            exponent = r == 0 ? FirstOrZero(q.Exponents) : FirstOrZero(q.Exponents) - levels + level;
            mantissa = FirstOrZero(q.Mantissas);
        }
        else
        {
            // One step per band in band order; missing exponents repeat the last one.
            int index = r == 0 ? 0 : 1 + 3 * (r - 1) + (orientation - 1);
            exponent = index < q.Exponents.Length ? q.Exponents[index] : q.Exponents.Length > 0 ? q.Exponents[^1] : 0;
            mantissa = index < q.Mantissas.Length ? q.Mantissas[index] : 0;
        }
        int gain = orientation == BandLL ? 0 : orientation == BandHH ? 2 : 1;
        band.MaxBitPlanes = Math.Clamp(q.GuardBits + exponent - 1, 0, 31);
        band.StepSize = q.Style == NoQuantization ? 1f : (float)(Math.Pow(2, component.Precision + gain - exponent) * (1 + mantissa / 2048.0));
        return band;
    }

    private static int FirstOrZero(int[] values) => values.Length > 0 ? values[0] : 0;

    /// <summary>
    /// Lays out a resolution's precincts and their code-blocks, charging the budget for each before it is allocated.
    /// </summary>
    private void MakePrecincts(Resolution res, int r)
    {
        long count = (long)res.PrecinctsAcross * res.PrecinctsDown;
        if (count > MaxPrecincts) throw new InvalidDataException("Too many precincts.");
        Charge(count * PrecinctCost);
        res.Precincts = new BandPrecinct[count][];
        res.NextLayer = new int[count];
        for (int py = 0; py < res.PrecinctsDown; py++)
        {
            for (int px = 0; px < res.PrecinctsAcross; px++)
            {
                var precinct = new BandPrecinct[res.Bands.Length];
                for (int b = 0; b < precinct.Length; b++) precinct[b] = MakeBandPrecinct(res, r, b, px, py);
                res.Precincts[py * res.PrecinctsAcross + px] = precinct;
            }
        }
    }

    /// <summary>The part of sub-band <paramref name="b"/> in a precinct, split into code-blocks.</summary>
    private BandPrecinct MakeBandPrecinct(Resolution res, int r, int b, int px, int py)
    {
        var band = res.Bands[b];
        // A precinct covers half as many samples in the sub-bands of a resolution above the lowest.
        int shiftX = res.PrecinctWidthLog2 - (r == 0 ? 0 : 1), shiftY = res.PrecinctHeightLog2 - (r == 0 ? 0 : 1);
        long x0 = Math.Max(band.X0, (long)(res.PrecinctX0 + px) << shiftX), x1 = Math.Min(band.X1, (long)(res.PrecinctX0 + px + 1) << shiftX);
        long y0 = Math.Max(band.Y0, (long)(res.PrecinctY0 + py) << shiftY), y1 = Math.Min(band.Y1, (long)(res.PrecinctY0 + py + 1) << shiftY);
        var part = new BandPrecinct { Band = b };
        if (x1 <= x0 || y1 <= y0) return part;
        int blockWidthLog2 = res.CodeBlockWidthLog2, blockHeightLog2 = res.CodeBlockHeightLog2;
        int firstX = (int)(x0 >> blockWidthLog2), firstY = (int)(y0 >> blockHeightLog2);
        part.BlocksAcross = CeilDivPow2((int)x1, blockWidthLog2) - firstX;
        part.BlocksDown = CeilDivPow2((int)y1, blockHeightLog2) - firstY;
        Charge((long)part.BlocksAcross * part.BlocksDown * CodeBlockCost);
        var blocks = new CodeBlock[part.BlocksAcross * part.BlocksDown];
        for (int j = 0; j < part.BlocksDown; j++)
        {
            for (int i = 0; i < part.BlocksAcross; i++)
            {
                blocks[j * part.BlocksAcross + i] = new CodeBlock
                {
                    X0 = (int)Math.Max(x0, (long)(firstX + i) << blockWidthLog2),
                    X1 = (int)Math.Min(x1, (long)(firstX + i + 1) << blockWidthLog2),
                    Y0 = (int)Math.Max(y0, (long)(firstY + j) << blockHeightLog2),
                    Y1 = (int)Math.Min(y1, (long)(firstY + j + 1) << blockHeightLog2),
                };
            }
        }
        part.Blocks = blocks;
        part.Inclusion = new TagTree(part.BlocksAcross, part.BlocksDown);
        part.ZeroBitPlanes = new TagTree(part.BlocksAcross, part.BlocksDown);
        return part;
    }

    // ------------------------------------------------------------------ components

    /// <summary>The inverse component transform (ISO/IEC 15444-1, annex G): RCT for the 5/3 wavelet, else ICT.</summary>
    private static void InverseComponentTransform(TileComponent[] components)
    {
        float[] a = components[0].Buffer, b = components[1].Buffer, c = components[2].Buffer;
        int n = components[0].Width * components[0].Height;
        if (components[0].Style.Reversible)
        {
            for (int i = 0; i < n; i++)
            {
                float y0 = a[i], y1 = b[i], y2 = c[i];
                float g = y0 - MathF.Floor((y2 + y1) / 4);
                a[i] = y2 + g;
                b[i] = g;
                c[i] = y1 + g;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                float y = a[i], cb = b[i], cr = c[i];
                a[i] = y + 1.402f * cr;
                b[i] = y - 0.344136f * cb - 0.714136f * cr;
                c[i] = y + 1.772f * cb;
            }
        }
    }

    /// <summary>Undoes the DC level shift, scales the samples to 8 bits and copies them into the component's plane.</summary>
    private static void StoreSamples(TileComponent tc, ImageComponent component, ComponentPlane plane)
    {
        var src = tc.Buffer;
        int w = tc.Width, h = tc.Height;
        if (w <= 0 || h <= 0) return;
        float shift = 1 << (component.Precision - 1);
        int max = (1 << component.Precision) - 1;
        float scale = 255f / max;
        var samples = plane.Samples;
        int pw = plane.Width;
        int ox = tc.X0 - plane.X0, oy = tc.Y0 - plane.Y0;
        for (int y = 0; y < h; y++)
        {
            int py = oy + y;
            if ((uint)py >= (uint)plane.Height) continue;
            int row = py * pw;
            for (int x = 0; x < w; x++)
            {
                int px = ox + x;
                if ((uint)px >= (uint)pw) continue;
                float v = src[y * w + x] + shift;
                int iv = (int)MathF.Round(v);
                iv = iv < 0 ? 0 : iv > max ? max : iv;
                samples[row + px] = component.Precision == 8 ? (byte)iv : (byte)(iv * scale + 0.5f);
            }
        }
    }
}

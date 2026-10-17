namespace PdfPecker.Rendering.Images;

internal sealed partial class JpegDecoder
{
    // The natural position of each coefficient in zigzag order. A damaged progressive scan can name a spectral band
    // past coefficient 63: the sixteen extra entries map those positions to the last coefficient, as before.
    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55,
        62, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    };

    private const int LastCoefficient = 63;
    private const int ZeroRunLength = 15; // the run of a ZRL symbol, which skips sixteen zero coefficients

    /// <summary>
    /// The coefficients a progressive scan codes: the spectral band from <see cref="Start"/> to <see cref="End"/> in
    /// zigzag order, and the bit position <see cref="Low"/> it codes, with <see cref="High"/> the position of the scan
    /// before it (0 for a first scan).
    /// </summary>
    private readonly record struct ScanBand(int Start, int End, int High, int Low);

    private BitReader _bits;
    private int _eobRun; // blocks left in an end-of-band run of a progressive AC scan

    /// <summary>Decodes the scan whose header starts at <paramref name="p"/> and returns the position of the marker after it.</summary>
    private int ReadScan(int p)
    {
        var d = _data;
        int length = ReadUInt16(p - 2);
        int count = d[p];
        var scanComponents = new Component[count];
        for (int i = 0; i < count; i++)
        {
            int id = d[p + 1 + i * 2], tables = d[p + 2 + i * 2];
            // A damaged header can name a component the frame does not have: take the one in the same place instead.
            var c = FindComponent(id) ?? _components[Math.Min(i, _components.Length - 1)];
            c.DcTable = tables >> 4 & (TableSlots - 1);
            c.AcTable = tables & (TableSlots - 1);
            scanComponents[i] = c;
        }
        int q = p + 1 + count * 2;
        var band = new ScanBand(Start: d[q], End: d[q + 1], High: d[q + 2] >> 4, Low: d[q + 2] & 15);
        _bits = new BitReader(d, p - 2 + length);
        ResetPredictions();

        // One component alone is coded block by block over the part of its plane the image covers; several are coded
        // in MCUs, each with every component's blocks.
        bool single = count == 1;
        int mcusPerLine, total;
        if (single)
        {
            var c = scanComponents[0];
            mcusPerLine = (_width * c.H + 8 * _hMax - 1) / (8 * _hMax);
            total = mcusPerLine * ((_height * c.V + 8 * _vMax - 1) / (8 * _vMax));
        }
        else
        {
            mcusPerLine = _mcusX;
            total = _mcusX * _mcusY;
        }

        int restart = _restartInterval;
        for (int m = 0; m < total; m++)
        {
            if (restart > 0 && m > 0 && m % restart == 0)
            {
                if (!_bits.SyncRestart()) break;
                ResetPredictions();
            }
            int row = m / mcusPerLine, column = m % mcusPerLine;
            if (single) DecodeBlock(scanComponents[0], row, column, band);
            else
            {
                foreach (var c in scanComponents)
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            DecodeBlock(c, row * c.V + v, column * c.H + h, band);
            }
            // The data ran out before the scan's last MCU: stop, unless a restart marker is due, where it resyncs.
            if (_bits.Exhausted && m + 1 < total && (restart == 0 || (m + 1) % restart != 0)) break;
        }
        return NextMarker(_bits.Position);
    }

    private Component? FindComponent(int id)
    {
        foreach (var c in _components)
            if (c.Id == id) return c;
        return null;
    }

    private void ResetPredictions()
    {
        foreach (var c in _components) c.DcPrediction = 0;
        _eobRun = 0;
    }

    /// <summary>The position of the first marker at or after <paramref name="p"/> that is not a restart marker.</summary>
    private int NextMarker(int p)
    {
        var d = _data;
        for (; p + 1 < d.Length; p++)
        {
            if (d[p] != MarkerPrefix) continue;
            byte next = d[p + 1];
            if (next != StuffedZero && next is not (>= Restart0 and <= Restart7) && next != MarkerPrefix) break;
        }
        return p;
    }

    /// <summary>A Huffman table a scan uses; a damaged file can name one it never defined.</summary>
    private static HuffmanTable Table(HuffmanTable?[] tables, int index) => tables[index] ?? throw new InvalidDataException("Undefined Huffman table");

    /// <summary>The value of a coefficient coded as <paramref name="size"/> extra bits (T.81, F.2.2.1).</summary>
    private static int Extend(int bits, int size) => bits < 1 << (size - 1) ? bits - (1 << size) + 1 : bits;

    private void DecodeBlock(Component c, int blockRow, int blockColumn, in ScanBand band)
    {
        var block = c.Coefficients.AsSpan((blockRow * c.BlocksPerLine + blockColumn) * BlockSize, BlockSize);
        if (!_progressive) DecodeSequential(c, block);
        else if (band.Start == 0)
        {
            if (band.High == 0) block[0] = (short)(DecodeDc(c) << band.Low);
            else if (_bits.ReadBit()) block[0] |= (short)(1 << band.Low);
        }
        else if (band.High == 0) DecodeAcFirst(c, block, band);
        else DecodeAcRefinement(c, block, band);
    }

    /// <summary>Decodes the next DC difference and returns the component's new DC value.</summary>
    private int DecodeDc(Component c)
    {
        int size = _bits.Decode(Table(_dcTables, c.DcTable));
        int diff = size == 0 ? 0 : Extend(_bits.ReadBits(size), size);
        c.DcPrediction += diff;
        return c.DcPrediction;
    }

    /// <summary>A block of a baseline or extended sequential scan: the DC difference, then run-length coded AC coefficients.</summary>
    private void DecodeSequential(Component c, Span<short> block)
    {
        block[0] = (short)DecodeDc(c);
        var ac = Table(_acTables, c.AcTable);
        for (int k = 1; k <= LastCoefficient;)
        {
            int symbol = _bits.Decode(ac);
            int run = symbol >> 4, size = symbol & 15;
            if (size == 0)
            {
                if (run == ZeroRunLength) { k += 16; continue; }
                break; // end of block
            }
            k += run;
            if (k > LastCoefficient) break;
            block[ZigZag[k]] = (short)Extend(_bits.ReadBits(size), size);
            k++;
        }
    }

    /// <summary>The first scan of an AC band: coefficients scaled by the bit position, and runs of empty blocks.</summary>
    private void DecodeAcFirst(Component c, Span<short> block, in ScanBand band)
    {
        if (_eobRun > 0)
        {
            _eobRun--;
            return;
        }
        var ac = Table(_acTables, c.AcTable);
        for (int k = band.Start; k <= band.End;)
        {
            int symbol = _bits.Decode(ac);
            int run = symbol >> 4, size = symbol & 15;
            if (size == 0)
            {
                if (run < ZeroRunLength)
                {
                    // The band is empty in this block and in 2^run - 1 more, plus the count in the next run bits.
                    _eobRun = (1 << run) - 1;
                    if (run > 0) _eobRun += _bits.ReadBits(run);
                    break;
                }
                k += 16;
                continue;
            }
            k += run;
            if (k > LastCoefficient) break;
            block[ZigZag[k]] = (short)(Extend(_bits.ReadBits(size), size) * (1 << band.Low));
            k++;
        }
    }

    /// <summary>
    /// A refinement scan of an AC band (T.81, G.1.2.3): one more bit of each coefficient that is already nonzero, and
    /// the coefficients that become nonzero at this bit position, placed by counting the zero ones they skip.
    /// </summary>
    private void DecodeAcRefinement(Component c, Span<short> block, in ScanBand band)
    {
        int plusOne = 1 << band.Low, minusOne = -1 << band.Low;
        var ac = Table(_acTables, c.AcTable);
        int k = band.Start;
        if (_eobRun <= 0)
        {
            while (k <= band.End)
            {
                int symbol = _bits.Decode(ac);
                int run = symbol >> 4, size = symbol & 15;
                int value = 0;
                if (size == 0)
                {
                    if (run < ZeroRunLength)
                    {
                        _eobRun = 1 << run;
                        if (run > 0) _eobRun += _bits.ReadBits(run);
                        break;
                    }
                }
                else value = _bits.ReadBit() ? plusOne : minusOne;
                while (k <= band.End)
                {
                    int z = ZigZag[k];
                    if (block[z] != 0) RefineCoefficient(block, z, plusOne, minusOne);
                    else
                    {
                        if (run == 0)
                        {
                            if (value != 0) block[z] = (short)value;
                            k++;
                            break;
                        }
                        run--;
                    }
                    k++;
                }
            }
        }
        if (_eobRun > 0)
        {
            // Inside an end-of-band run only the nonzero coefficients get their next bit.
            for (; k <= band.End; k++)
            {
                int z = ZigZag[k];
                if (block[z] != 0) RefineCoefficient(block, z, plusOne, minusOne);
            }
            _eobRun--;
        }
    }

    /// <summary>Adds the next bit to a nonzero coefficient, away from zero, unless it already has that bit.</summary>
    private void RefineCoefficient(Span<short> block, int z, int plusOne, int minusOne)
    {
        if (_bits.ReadBit() && (block[z] & plusOne) == 0) block[z] = (short)(block[z] >= 0 ? block[z] + plusOne : block[z] + minusOne);
    }
}

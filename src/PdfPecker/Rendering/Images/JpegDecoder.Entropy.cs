namespace PdfPecker.Rendering.Images;

internal sealed partial class JpegDecoder
{
    /// <summary>A Huffman table of a DHT segment, with a lookup table for the short codes that most symbols have.</summary>
    private sealed class HuffmanTable
    {
        public const int MaxCodeLength = 16;

        // Codes of up to nine bits are found with one lookup of the next nine bits: (code length << 8 | symbol), or 0
        // when the code is longer. Longer codes are found bit by bit against the largest code of each length.
        public const int FastBits = 9;

        public readonly ushort[] Fast = new ushort[1 << FastBits];
        public readonly int[] MaxCode = new int[MaxCodeLength + 1];
        public readonly int[] ValueOffset = new int[MaxCodeLength + 1];
        public readonly byte[] Values;

        /// <summary>Builds the canonical codes from the number of codes of each length (1 to 16) and the symbols.</summary>
        public HuffmanTable(ReadOnlySpan<int> counts, byte[] values)
        {
            Values = values;
            int code = 0, symbol = 0;
            for (int length = 1; length <= MaxCodeLength; length++)
            {
                ValueOffset[length] = symbol - code;
                for (int i = 0; i < counts[length]; i++, symbol++, code++)
                {
                    if (length > FastBits || symbol >= values.Length) continue;
                    // An over-full table has codes past the end of the lookup table, which throws like other damage.
                    int shift = FastBits - length;
                    Fast.AsSpan(code << shift, 1 << shift).Fill((ushort)(length << 8 | values[symbol]));
                }
                MaxCode[length] = counts[length] > 0 ? code - 1 : -1;
                code <<= 1;
            }
        }
    }

    /// <summary>
    /// Reads the entropy-coded data of a scan most significant bit first, dropping the zero byte stuffed after each
    /// 0xFF. At a marker or the end of the data it supplies zero bits, so a damaged scan still fills its blocks.
    /// </summary>
    private struct BitReader
    {
        private readonly byte[] _data;
        private uint _bits; // the next bits, left-aligned
        private int _count; // how many of them come from the data
        private bool _hitMarker;

        public BitReader(byte[] data, int position)
        {
            _data = data;
            Position = position;
        }

        /// <summary>The next byte of the data to be read.</summary>
        public int Position { get; private set; }

        /// <summary>Whether every bit up to the marker or the end of the data has been read.</summary>
        public readonly bool Exhausted => _hitMarker && _count <= 0;

        private void Fill()
        {
            var d = _data;
            while (_count <= 24)
            {
                int b = 0;
                if (!_hitMarker && Position < d.Length)
                {
                    b = d[Position];
                    if (b == MarkerPrefix)
                    {
                        int next = Position + 1 < d.Length ? d[Position + 1] : 0;
                        if (next == StuffedZero) Position += 2;
                        else if (next == MarkerPrefix) { Position++; continue; } // a fill byte
                        else { _hitMarker = true; b = 0; }
                    }
                    else Position++;
                }
                else if (Position >= d.Length) _hitMarker = true;
                _bits |= (uint)b << (24 - _count);
                _count += 8;
            }
        }

        public int ReadBits(int n)
        {
            if (n == 0) return 0;
            if (_count < n) Fill();
            int value = (int)(_bits >> (32 - n));
            _bits <<= n;
            _count -= n;
            return value;
        }

        public bool ReadBit() => ReadBits(1) != 0;

        /// <summary>Decodes one symbol; a code the table does not have decodes to zero.</summary>
        public int Decode(HuffmanTable table)
        {
            if (_count < HuffmanTable.MaxCodeLength) Fill();
            int fast = table.Fast[_bits >> (32 - HuffmanTable.FastBits)];
            if (fast != 0)
            {
                int length = fast >> 8;
                _bits <<= length;
                _count -= length;
                return fast & 0xFF;
            }
            int code = 0;
            for (int length = 1; length <= HuffmanTable.MaxCodeLength; length++)
            {
                code = code << 1 | (int)(_bits >> 31);
                _bits <<= 1;
                _count--;
                if (code <= table.MaxCode[length])
                {
                    int index = code + table.ValueOffset[length];
                    return (uint)index < (uint)table.Values.Length ? table.Values[index] : 0;
                }
            }
            return 0;
        }

        /// <summary>
        /// Drops the bits left before a restart marker and moves past it. Returns false, stopping at the marker, when
        /// another marker or the end of the data comes first.
        /// </summary>
        public bool SyncRestart()
        {
            var d = _data;
            for (int p = Position; p + 1 < d.Length; p++)
            {
                if (d[p] != MarkerPrefix) continue;
                byte next = d[p + 1];
                if (next is >= Restart0 and <= Restart7)
                {
                    this = new BitReader(d, p + 2);
                    return true;
                }
                if (next != StuffedZero && next != MarkerPrefix)
                {
                    this = new BitReader(d, p);
                    return false;
                }
            }
            return false;
        }
    }
}

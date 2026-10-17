namespace PdfPecker.Images;

internal sealed partial class Vp8Decoder
{
    /// <summary>
    /// The boolean entropy decoder of RFC 6386 section 7, over one partition. Reading past the end of the partition
    /// yields zeros, as a truncated image then decodes to what its data describes.
    /// </summary>
    private sealed class BoolDecoder
    {
        private const int EvenProba = 0x80;

        private readonly byte[] _data;
        private readonly int _end;
        private int _position;
        private uint _value; // the next two bytes of the stream, less what the decoded bits took
        private uint _range = 255;
        private int _bitCount; // bits shifted out of the low byte of _value since a byte was last read into it

        public BoolDecoder(byte[] data, int offset, int length)
        {
            _data = data;
            _position = offset;
            _end = Math.Min(data.Length, offset + Math.Max(0, length));
            _value = (uint)(NextByte() << 8 | NextByte());
        }

        private int NextByte() => _position < _end ? _data[_position++] : 0;

        /// <summary>Reads one bit that is zero with a probability of <paramref name="proba"/> in 256.</summary>
        public int GetBit(int proba)
        {
            uint split = 1 + (((_range - 1) * (uint)proba) >> 8);
            uint bigSplit = split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = split;
            }
            // Keep the range at 128 or more, shifting in the next byte after every eight bits.
            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)NextByte();
                }
            }
            return bit;
        }

        public bool ReadFlag() => GetBit(EvenProba) == 1;

        /// <summary>An unsigned number of <paramref name="bits"/> bits, most significant first, each at an even chance.</summary>
        public int GetValue(int bits)
        {
            int v = 0;
            while (bits-- > 0) v |= GetBit(EvenProba) << bits;
            return v;
        }

        /// <summary>A magnitude of <paramref name="bits"/> bits followed by a sign bit.</summary>
        public int GetSignedValue(int bits)
        {
            int value = GetValue(bits);
            return ReadFlag() ? -value : value;
        }

        /// <summary>
        /// Reads a value coded with a tree (RFC 6386, 8.1): each entry is the index of the next pair of entries, or
        /// minus the value at a leaf, and each pair is chosen by a bit with its own probability.
        /// </summary>
        public int ReadTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probas)
        {
            int i = 0;
            while ((i = tree[i + GetBit(probas[i >> 1])]) > 0) { }
            return -i;
        }
    }
}

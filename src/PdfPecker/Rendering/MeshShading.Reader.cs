using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;

namespace PdfPecker.Rendering;

internal static partial class MeshShading
{
    /// <summary>
    /// Reads the flags, points and colours of a mesh stream and maps them through the Decode array: points into device
    /// space, colours into a <see cref="VertexColor"/>.
    /// </summary>
    private ref struct MeshReader
    {
        // Decode holds [xmin xmax ymin ymax] and then a min and max for each colour component.
        private const int XMin = 0, XMax = 1, YMin = 2, YMax = 3;
        public const int ColorDecodeStart = 4;

        private BitReader _bits;
        private readonly ReadOnlySpan<double> _decode;
        private readonly Matrix _toDevice;
        private readonly ColorSpace _space;
        private readonly bool _parametric;
        private readonly float[] _components;

        // Parametric is true when the shading has a function, so that each colour is a single parameter t.
        public MeshReader(ReadOnlySpan<byte> data, MeshFormat format, ReadOnlySpan<double> decode, in Matrix toDevice, ColorSpace space, bool parametric)
        {
            _bits = new BitReader(data);
            Format = format;
            _decode = decode;
            _toDevice = toDevice;
            _space = space;
            _parametric = parametric;
            _components = new float[Math.Max(1, space.Components)];
        }

        public MeshFormat Format { get; }

        public readonly bool HasMore(long bits) => _bits.HasMore(bits);

        public void AlignToByte() => _bits.AlignToByte();

        public int ReadFlag() => (int)_bits.Read(Format.BitsPerFlag);

        public (double X, double Y) ReadPoint()
        {
            int bits = Format.BitsPerCoordinate;
            double x = Decode(_bits.Read(bits), bits, _decode[XMin], _decode[XMax]);
            double y = Decode(_bits.Read(bits), bits, _decode[YMin], _decode[YMax]);
            return _toDevice.Apply(x, y);
        }

        public VertexColor ReadColor()
        {
            int bits = Format.BitsPerComponent;
            if (_parametric) return new VertexColor(Decode(_bits.Read(bits), bits, _decode[ColorDecodeStart], _decode[ColorDecodeStart + 1]), 0, 0);
            int count = Format.ColorComponents;
            for (int i = 0; i < count; i++)
            {
                int range = ColorDecodeStart + 2 * i;
                _components[i] = (float)Decode(_bits.Read(bits), bits, _decode[range], _decode[range + 1]);
            }
            uint rgb = _space.ToRgb(_components);
            return new VertexColor(rgb & 0xFF, (rgb >> 8) & 0xFF, (rgb >> 16) & 0xFF);
        }

        public Vertex ReadVertex()
        {
            var (x, y) = ReadPoint();
            return new Vertex(x, y, ReadColor());
        }

        /// <summary>Maps a raw value of the given bit width linearly onto [min, max].</summary>
        private static double Decode(uint raw, int bits, double min, double max)
        {
            double maxRaw = bits >= 32 ? uint.MaxValue : (1L << bits) - 1;
            return min + raw * (max - min) / maxRaw;
        }
    }

    /// <summary>
    /// Reads values of up to 32 bits, most significant bit first. The mesh walks only accept the widths the
    /// specification allows and check <see cref="HasMore"/> before they read, so a read never runs past the end; if one
    /// did, it would stop one bit past the end rather than read outside the data.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _position;

        public BitReader(ReadOnlySpan<byte> data) => _data = data;

        public readonly bool HasMore(long bits) => _position + bits <= (long)_data.Length * 8;

        public void AlignToByte() => _position = (_position + 7) & ~7L;

        public uint Read(int bits)
        {
            uint value = 0;
            for (int i = 0; i < bits; i++)
            {
                long bit = _position++;
                long index = bit >> 3;
                if (index >= _data.Length) return value << (bits - i - 1);
                value = value << 1 | (uint)((_data[(int)index] >> (7 - (int)(bit & 7))) & 1);
            }
            return value;
        }
    }
}

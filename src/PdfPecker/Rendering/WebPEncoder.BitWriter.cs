using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    /// <summary>
    /// Writes bits least significant first, 32 at a time into a buffer that doubles when full. A value must fit in
    /// its bit count; higher bits would spill into the fields after it.
    /// </summary>
    private sealed class BitWriter
    {
        private const int MinCapacity = 256;
        private byte[] buffer;
        private int length;
        private ulong pending;
        private int pendingBits;

        public BitWriter(int capacity) => buffer = new byte[Math.Max(capacity, MinCapacity)];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value, int bits)
        {
            if (bits == 0) return;
            pending |= (ulong)value << pendingBits;
            pendingBits += bits;
            if (pendingBits < 32) return;
            if (length + 4 > buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), (uint)pending);
            length += 4;
            pending >>= 32;
            pendingBits -= 32;
        }

        /// <summary>The bits written so far, the last byte padded with zeros.</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[length + (pendingBits + 7) / 8];
            buffer.AsSpan(0, length).CopyTo(bytes);
            for (int i = length; i < bytes.Length; i++)
            {
                bytes[i] = (byte)pending;
                pending >>= 8;
            }
            return bytes;
        }
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PdfPecker.Rendering;

internal static partial class WebPEncoder
{
    /// <summary>
    /// Writes bits least significant first, 32 at a time into a rented buffer that doubles when full. A value must fit
    /// in its bit count; higher bits would spill into the fields after it. <see cref="ToArray"/> returns the buffer.
    /// </summary>
    private sealed class BitWriter
    {
        private const int MinCapacity = 256;
        private byte[] buffer;
        private int length;
        private ulong pending;
        private int pendingBits;

        public BitWriter(int capacity) => buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, MinCapacity));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint value, int bits)
        {
            if (bits == 0) return;
            pending |= (ulong)value << pendingBits;
            pendingBits += bits;
            if (pendingBits < 32) return;
            if (length + 4 > buffer.Length) Grow();
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), (uint)pending);
            length += 4;
            pending >>= 32;
            pendingBits -= 32;
        }

        private void Grow()
        {
            var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
            buffer.AsSpan(0, length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = larger;
        }

        /// <summary>The bits written so far, the last byte padded with zeros. The writer cannot be used afterwards.</summary>
        public byte[] ToArray()
        {
            var bytes = new byte[length + (pendingBits + 7) / 8];
            buffer.AsSpan(0, length).CopyTo(bytes);
            for (int i = length; i < bytes.Length; i++)
            {
                bytes[i] = (byte)pending;
                pending >>= 8;
            }
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = Array.Empty<byte>();
            return bytes;
        }
    }
}

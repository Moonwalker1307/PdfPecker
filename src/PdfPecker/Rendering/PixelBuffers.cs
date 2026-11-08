using System.Collections.Concurrent;

namespace PdfPecker.Rendering;

/// <summary>
/// Page pixel arrays that one multi-page conversion hands from a page that has been encoded to the next page it renders.
/// Pages of a document are usually all one size, so this saves allocating, and clearing, a large array per page. It
/// lives only as long as the conversion and holds at most one array per page being rendered at once.
/// </summary>
internal sealed class PixelBuffers
{
    private readonly ConcurrentBag<uint[]> _free = new();

    /// <summary>A free array of exactly <paramref name="length"/> pixels, or null. Its contents are left over.</summary>
    public uint[]? Take(int length)
    {
        // An array of another size is dropped rather than kept: the next pages are most likely this size too.
        while (_free.TryTake(out var pixels))
            if (pixels.Length == length) return pixels;
        return null;
    }

    /// <summary>Gives back an array whose image has been encoded and is no longer used.</summary>
    public void Return(uint[] pixels) => _free.Add(pixels);
}

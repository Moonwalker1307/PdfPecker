using System.Runtime.CompilerServices;
using PdfPecker.Rendering;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Tests;

/// <summary>The image cache's handling of large images that only one page draws, such as the scans of a scanned PDF.</summary>
public class RenderImageCacheTests
{
    // 36 MB of pixels, as a 300 dpi A4 scan.
    private const int ScanPixels = 9_000_000;

    private static PdfStream Stream() => new(new Dictionary<string, PdfObject>(), Array.Empty<byte>());

    private static PageCacheUse Use(object page) => new() { Page = page };

    /// <summary>Gets an image from the cache, counting the decodes in <paramref name="decodes"/>.</summary>
    private static DecodedImage? Get(RenderCache cache, PdfStream stream, PageCacheUse use, Counter decodes, int pixels = ScanPixels, long expectedBytes = 0)
        => cache.GetImage(stream, () =>
        {
            decodes.Count++;
            return new DecodedImage { Width = pixels, Height = 1, Pixels = new uint[pixels] };
        }, use, expectedBytes > 0 ? expectedBytes : 4L * pixels);

    private sealed class Counter
    {
        public int Count;
    }

    [Fact]
    public void A_scan_one_page_draws_gives_way_to_the_next_pages_scan()
    {
        var cache = new RenderCache();
        var decodes = new Counter();
        object page1 = new(), page2 = new();
        PdfStream scan1 = Stream(), scan2 = Stream();
        var first = Get(cache, scan1, Use(page1), decodes);
        // The same page drawn again, as at another size, finds its scan.
        Assert.Same(first, Get(cache, scan1, Use(page1), decodes));
        Assert.Equal(1, decodes.Count);

        // The next page's scan takes its place, so the first is no longer held and is decoded again when asked for.
        Get(cache, scan2, Use(page2), decodes);
        Get(cache, scan1, Use(page1), decodes);
        Assert.Equal(3, decodes.Count);
    }

    [Fact]
    public void An_image_a_second_page_draws_is_kept_for_every_page()
    {
        var cache = new RenderCache();
        var decodes = new Counter();
        object page1 = new(), page2 = new(), page3 = new();
        PdfStream logo = Stream(), other = Stream();
        var image = Get(cache, logo, Use(page1), decodes);
        Assert.Same(image, Get(cache, logo, Use(page2), decodes));
        // Other large images that one page draws do not push it out.
        for (int i = 0; i < 4; i++) Get(cache, Stream(), Use(new object()), decodes);
        Get(cache, other, Use(page3), decodes);
        Assert.Same(image, Get(cache, logo, Use(page3), decodes));
        Assert.Same(image, Get(cache, logo, Use(page1), decodes));
        Assert.Equal(6, decodes.Count);
    }

    [Fact]
    public void Small_images_and_pages_unknown_to_the_cache_use_the_shared_pool()
    {
        var cache = new RenderCache();
        var decodes = new Counter();
        PdfStream small = Stream(), large = Stream();
        var image = Get(cache, small, Use(new object()), decodes, 1000);
        Assert.Same(image, Get(cache, small, Use(new object()), decodes, 1000));
        var unknown = Get(cache, large, new PageCacheUse(), decodes);
        for (int i = 0; i < 4; i++) Get(cache, Stream(), Use(new object()), decodes);
        Assert.Same(unknown, Get(cache, large, new PageCacheUse(), decodes));
        Assert.Equal(6, decodes.Count);
    }

    [Fact]
    public void Images_two_pages_draw_are_not_held_past_the_pools_once_the_shared_pool_is_full()
    {
        // Pools of 1 MB and 256 KB, and images of 64 KB that the cache takes for large ones (their dictionaries say
        // 4 MB), so that a hundred of them fill the shared pool six times over.
        const long shared = 1024 * 1024, singlePage = 256 * 1024;
        const int pixels = 16 * 1024;
        var cache = new RenderCache(shared, singlePage);
        var images = DrawEachOnTwoPages(cache, 100, pixels);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long held = images.Count(w => w.IsAlive) * 4L * pixels;
        // Before, an image that moved to the full shared pool stayed in the single-page pool's queue: all 6.4 MB.
        Assert.InRange(held, 0, shared + singlePage);
        GC.KeepAlive(cache);
    }

    /// <summary>Draws each of <paramref name="count"/> images on two pages in turn, as pages 2k and 2k + 1 draw image k.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> DrawEachOnTwoPages(RenderCache cache, int count, int pixels)
    {
        var decodes = new Counter();
        var images = new List<WeakReference>();
        for (int k = 0; k < count; k++)
        {
            var stream = Stream();
            images.Add(new WeakReference(Get(cache, stream, Use(new object()), decodes, pixels, 4L * 1024 * 1024)));
            Get(cache, stream, Use(new object()), decodes, pixels, 4L * 1024 * 1024);
        }
        Assert.Equal(count, decodes.Count);
        return images;
    }
}

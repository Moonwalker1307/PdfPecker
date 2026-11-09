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
    private static DecodedImage? Get(RenderCache cache, PdfStream stream, PageCacheUse use, Counter decodes, int pixels = ScanPixels)
        => cache.GetImage(stream, () =>
        {
            decodes.Count++;
            return new DecodedImage { Width = pixels, Height = 1, Pixels = new uint[pixels] };
        }, use, 4L * pixels);

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
}

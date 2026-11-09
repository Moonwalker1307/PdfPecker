using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Fonts;
using PdfPecker.Rendering.Images;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>A rasterized glyph: coverage bytes and the offset of its top-left pixel from the glyph origin's pixel.</summary>
internal sealed class GlyphBitmap
{
    public int X0, Y0, Width, Height;
    public byte[] Coverage = Array.Empty<byte>();
}

internal readonly record struct GlyphKey(object Source, int Gid, int A, int B, int C, int D, byte Fx, byte Fy);

/// <summary>
/// Per-document caches shared by all pages: fonts, decoded images, colour spaces, functions and rasterized glyphs.
/// </summary>
/// <remarks>
/// What a page shows must not depend on which pages were rendered before it, or on how many at once, while each page
/// has a decode budget of its own (<see cref="PdfFile.MaxDecodedPerPage"/>). So a result made after the page's budget
/// ran out, which may lack what the budget cut off, is never kept for other pages; and a page that finds a result
/// another page made is charged what making it cost, once, as if it had made it itself (see <see cref="PageCacheUse"/>).
/// </remarks>
internal sealed class RenderCache
{
    /// <summary>The least a colour space or function is counted as, for the entry itself.</summary>
    private const long MinColorEntrySize = 256;
    private const int GlyphLimit = 50_000;

    private readonly Pool _fontPool = new(long.MaxValue);
    private readonly Pool _imagePool;
    // Most colour spaces and functions are tiny, but a sampled function keeps its whole table, up to 128 MB.
    private readonly Pool _colorPool = new(128L * 1024 * 1024);
    private readonly Dictionary<PdfDict, Entry<PdfFont>> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfDict, Entry<DecodedImage?>> _images = new(ReferenceEqualityComparer.Instance);
    // Colour spaces named in a resource dictionary are kept by the dictionary and the name, since a colour space array
    // may name further colour spaces of the same resources; those given as objects by the object, as are functions.
    private readonly Dictionary<NamedKey, Entry<ColorSpace?>> _namedSpaces = new();
    private readonly Dictionary<PdfObject, Entry<ColorSpace?>> _spaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PdfObject, Entry<PdfFunction?>> _functions = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<GlyphKey, GlyphBitmap> _glyphs = new();
    private int _glyphCount;

    // A large image that one page draws, such as the scan of a scanned page, goes to a small pool of its own whose
    // least recently drawn entries give way to new ones, until another page draws it too: kept with the others, every
    // page's scan (35 MB at 300 dpi A4) stayed until the image pool's 512 MB were full, though no other page draws it.
    // The pool always keeps the image drawn last, even one larger than the pool, so a page drawn again soon after, as a
    // thumbnail and then in full or at several resolutions, finds its scan; it holds the pool's limit or that one
    // image, whichever is more.
    private const long SinglePageMinBytes = 4L * 1024 * 1024;
    private readonly Pool _singlePagePool;
    private readonly Dictionary<PdfDict, LinkedListNode<SinglePageImage>> _singlePageImages = new(ReferenceEqualityComparer.Instance);
    // The pool's entries, least recently drawn first. An image that moves to the shared pool leaves the list at once, so
    // the list never holds an image the pool no longer counts.
    private readonly LinkedList<SinglePageImage> _singlePageOrder = new();
    // The page that first drew each large image; small, as it holds only the keys.
    private readonly Dictionary<PdfDict, object> _imagePages = new(ReferenceEqualityComparer.Instance);

    public RenderCache() : this(512L * 1024 * 1024, 64L * 1024 * 1024)
    {
    }

    /// <summary>A cache whose image pools hold at most the given bytes; tests use small ones.</summary>
    internal RenderCache(long imageLimit, long singlePageImageLimit)
    {
        _imagePool = new Pool(imageLimit);
        _singlePagePool = new Pool(singlePageImageLimit);
    }

    public PdfFont GetFont(PdfFile file, PdfDict dict, PageCacheUse use)
        => GetOrAdd(_fonts, _fontPool, dict, use, () => (PdfFont.Load(file, dict), 0));

    /// <summary>
    /// The decoded image of <paramref name="stream"/>, which its dictionary says takes about
    /// <paramref name="expectedBytes"/>, made by <paramref name="decode"/> when it is not cached.
    /// </summary>
    public DecodedImage? GetImage(PdfStream stream, Func<DecodedImage?> decode, PageCacheUse use, long expectedBytes = 0)
    {
        Func<(DecodedImage? Value, long Size)> create = () =>
        {
            var image = decode();
            return (image, image?.ByteSize ?? 0);
        };
        if (expectedBytes < SinglePageMinBytes || use.Page == null) return GetOrAdd(_images, _imagePool, stream, use, create);

        Entry<DecodedImage?>? promoted = null;
        lock (_singlePagePool)
        {
            ref var first = ref CollectionsMarshal.GetValueRefOrAddDefault(_imagePages, stream, out bool seen);
            if (!seen) first = use.Page;
            if (ReferenceEquals(first, SharedOwner)) promoted = SharedMarker;
            else if (ReferenceEquals(first, use.Page)) promoted = null;
            else
            {
                // From now on every page, the first one too, finds the image in the shared pool.
                first = SharedOwner;
                if (_singlePageImages.Remove(stream, out var node))
                {
                    _singlePageOrder.Remove(node);
                    promoted = node.Value.Entry;
                    _singlePagePool.Held -= promoted.Size;
                }
                else promoted = SharedMarker;
            }
        }
        if (promoted == null) return GetOrAddSinglePage(stream, use, create);
        if (ReferenceEquals(promoted, SharedMarker)) return GetOrAdd(_images, _imagePool, stream, use, create);

        // A second page draws the image: it moves to the shared pool, as if that page had found it there.
        use.Charge(promoted);
        lock (_imagePool)
        {
            if (_images.TryGetValue(stream, out var other)) return other.Value;
            if (_imagePool.Held + promoted.Size <= _imagePool.Limit)
            {
                _images[stream] = promoted;
                _imagePool.Held += promoted.Size;
            }
        }
        return promoted.Value;
    }

    /// <summary>Stands for "look in the shared pool" in <see cref="GetImage"/>; never stored.</summary>
    private static readonly Entry<DecodedImage?> SharedMarker = new(null, 0);

    /// <summary>The page recorded for an image that more than one page draws.</summary>
    private static readonly object SharedOwner = new();

    /// <summary>
    /// <see cref="GetOrAdd"/> for the single-page pool, which makes room for a new entry by dropping the least recently
    /// drawn ones; an image larger than the pool takes the place of all of them.
    /// </summary>
    private DecodedImage? GetOrAddSinglePage(PdfStream key, PageCacheUse use, Func<(DecodedImage? Value, long Size)> create)
    {
        var pool = _singlePagePool;
        Entry<DecodedImage?>? entry = null;
        lock (pool)
        {
            if (_singlePageImages.TryGetValue(key, out var node))
            {
                entry = node.Value.Entry;
                _singlePageOrder.Remove(node);
                _singlePageOrder.AddLast(node);
            }
        }
        if (entry != null)
        {
            use.Charge(entry);
            return entry.Value;
        }
        long before = PdfFile.DecodeLimit;
        var (value, size) = create();
        long after = PdfFile.DecodeLimit;
        if (after == 0) return value;
        entry = new Entry<DecodedImage?>(value, before - after);
        use.Made(entry);
        lock (pool)
        {
            if (_singlePageImages.TryGetValue(key, out var other))
            {
                use.Made(other.Value.Entry);
                return other.Value.Entry.Value;
            }
            // Drawn by another page while this one decoded it: that page keeps it. An image larger than even the shared
            // pool, which no pool would have kept before, is not kept either.
            if (size > _imagePool.Limit || ReferenceEquals(_imagePages.GetValueOrDefault(key), SharedOwner)) return value;
            entry.Size = size;
            while (pool.Held + size > pool.Limit && _singlePageOrder.First is { } oldest)
            {
                _singlePageOrder.RemoveFirst();
                _singlePageImages.Remove(oldest.Value.Key);
                pool.Held -= oldest.Value.Entry.Size;
            }
            _singlePageImages[key] = _singlePageOrder.AddLast(new SinglePageImage(key, entry));
            pool.Held += size;
        }
        return value;
    }

    /// <summary>An entry of the single-page pool, with its key, so that dropping the oldest finds its map entry.</summary>
    private readonly record struct SinglePageImage(PdfDict Key, Entry<DecodedImage?> Entry);

    /// <summary>The colour space a name stands for in the resources, as <see cref="ColorSpace.Parse"/> reads it.</summary>
    public ColorSpace? GetColorSpace(PdfFile file, string name, PdfDict? resources, PageCacheUse use)
    {
        // Without resources a name can only be a device space, which needs no parsing.
        if (resources == null) return ColorSpace.Parse(file, PdfName.Get(name), null);
        return GetOrAdd(_namedSpaces, _colorPool, new NamedKey(resources, name), use,
            () => Measured(() => ColorSpace.Parse(file, PdfName.Get(name), resources)));
    }

    /// <summary>A colour space given as an object outside any resources, as shadings and transparency groups give it.</summary>
    public ColorSpace? GetColorSpace(PdfFile file, PdfObject? obj, PageCacheUse use)
    {
        // A name is a device space, which needs no parsing.
        if (file.Resolve(obj) is not PdfArray array) return ColorSpace.Parse(file, obj, null);
        return GetOrAdd(_spaces, _colorPool, array, use, () => Measured(() => ColorSpace.Parse(file, array, null)));
    }

    /// <summary>A function, as <see cref="PdfFunction.Create"/> reads it.</summary>
    public PdfFunction? GetFunction(PdfFile file, PdfObject? obj, PageCacheUse use)
    {
        var resolved = file.Resolve(obj);
        if (resolved is not (PdfDict or PdfArray)) return null;
        return GetOrAdd(_functions, _colorPool, resolved, use, () => Measured(() => PdfFunction.Create(file, resolved)));
    }

    public bool TryGetGlyph(in GlyphKey key, out GlyphBitmap bmp) => _glyphs.TryGetValue(key, out bmp!);

    public void AddGlyph(in GlyphKey key, GlyphBitmap bmp)
    {
        // ConcurrentDictionary.Count takes every lock, so keep a separate approximate count.
        if (Interlocked.Increment(ref _glyphCount) > GlyphLimit)
        {
            _glyphs.Clear();
            Interlocked.Exchange(ref _glyphCount, 0);
        }
        _glyphs[key] = bmp;
    }

    /// <summary>
    /// The cached result for <paramref name="key"/>, or else a new one from <paramref name="create"/>, which also gives
    /// its size in bytes; it is kept while the pool has room for it. The pool is also the lock of the map.
    /// </summary>
    private static T GetOrAdd<TKey, T>(Dictionary<TKey, Entry<T>> map, Pool pool, TKey key, PageCacheUse use, Func<(T Value, long Size)> create)
        where TKey : notnull
    {
        Entry<T>? entry;
        lock (pool) map.TryGetValue(key, out entry);
        if (entry != null)
        {
            use.Charge(entry);
            return entry.Value;
        }
        long before = PdfFile.DecodeLimit;
        var (value, size) = create();
        long after = PdfFile.DecodeLimit;
        // A result made after the page's budget ran out may lack what the budget cut off, which another page could make.
        if (after == 0) return value;
        entry = new Entry<T>(value, before - after);
        use.Made(entry);
        lock (pool)
        {
            if (map.TryGetValue(key, out var other))
            {
                use.Made(other);
                return other.Value;
            }
            if (pool.Held + size <= pool.Limit)
            {
                map[key] = entry;
                pool.Held += size;
            }
        }
        return value;
    }

    /// <summary>
    /// A colour space or function with what parsing it allocated, which a sampled function's table dominates. That
    /// counts the decoded stream and other passing allocations too, so it errs on the side of keeping less.
    /// </summary>
    private static (T Value, long Size) Measured<T>(Func<T> parse)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        var value = parse();
        return (value, Math.Max(MinColorEntrySize, GC.GetAllocatedBytesForCurrentThread() - start));
    }

    /// <summary>A cached result, with the decode budget making it took.</summary>
    private sealed class Entry<T> : PageCacheUse.ICharged
    {
        public readonly T Value;

        public Entry(T value, long cost)
        {
            Value = value;
            Cost = cost;
        }

        public long Cost { get; }

        /// <summary>The bytes the entry holds, for the single-page pool.</summary>
        public long Size { get; set; }
    }

    /// <summary>The bytes the entries of one or more caches may hold together, and what they hold.</summary>
    private sealed class Pool
    {
        public readonly long Limit;
        public long Held;

        public Pool(long limit) => Limit = limit;
    }

    /// <summary>A name in a resource dictionary: the dictionary by reference, the name by its characters.</summary>
    private readonly struct NamedKey : IEquatable<NamedKey>
    {
        private readonly PdfDict _resources;
        private readonly string _name;

        public NamedKey(PdfDict resources, string name)
        {
            _resources = resources;
            _name = name;
        }

        public bool Equals(NamedKey other) => ReferenceEquals(_resources, other._resources) && string.Equals(_name, other._name, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is NamedKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_resources), StringComparer.Ordinal.GetHashCode(_name));
    }
}

/// <summary>
/// The shared cache entries one page has used, so that each is charged to the page's decode budget once: when the
/// page makes it, by the decoding itself, or else when the page first finds it made by another page.
/// </summary>
internal sealed class PageCacheUse
{
    private readonly HashSet<ICharged> _charged = new(ReferenceEqualityComparer.Instance);

    /// <summary>A cache entry, with the decode budget making it took.</summary>
    public interface ICharged
    {
        long Cost { get; }
    }

    /// <summary>The page being drawn (its dictionary), which the image cache tells apart from other pages; null when unknown.</summary>
    public object? Page { get; set; }

    /// <summary>Notes an entry this page made, whose decoding was already charged to it.</summary>
    public void Made(ICharged entry) => _charged.Add(entry);

    /// <summary>
    /// Charges an entry the first time this page uses it, unless the page made it; throws the budget's
    /// <see cref="InvalidDataException"/> when the page cannot afford it, as making it would have.
    /// </summary>
    public void Charge(ICharged entry)
    {
        if (_charged.Contains(entry)) return;
        PdfFile.ChargeDecoded(entry.Cost);
        _charged.Add(entry);
    }
}

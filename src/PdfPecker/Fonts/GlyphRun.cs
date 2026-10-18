using System.Runtime.InteropServices;

namespace PdfPecker.Fonts;

/// <summary>
/// The glyphs being shaped: a list of <see cref="GlyphInfo"/> that keeps its free room at the place it was last changed
/// (a gap buffer), and otherwise behaves as <see cref="List{T}"/> does, exceptions included.
/// </summary>
/// <remarks>
/// A lookup goes through the run from its start and inserts or removes glyphs where it is, so the gap follows it and
/// each change moves only the glyphs between it and the previous one. A list moves every glyph after the change each
/// time instead, which makes a lookup that decomposes or deletes every glyph quadratic in the length of the run.
/// </remarks>
internal sealed class GlyphRun
{
    private const int InitialCapacity = 16;

    // The glyphs are _items[0.._gapStart) followed by _items[_gapEnd..].
    private GlyphInfo[] _items = new GlyphInfo[InitialCapacity];
    private int _gapStart, _gapEnd = InitialCapacity;

    public int Count => _items.Length - (_gapEnd - _gapStart);

    public GlyphInfo this[int index]
    {
        get => _items[Slot(index)];
        set => _items[Slot(index)] = value;
    }

    private int Slot(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return index < _gapStart ? index : index + (_gapEnd - _gapStart);
    }

    public void Clear()
    {
        _gapStart = 0;
        _gapEnd = _items.Length;
    }

    public void Add(GlyphInfo glyph) => Insert(Count, glyph);

    public void Insert(int index, GlyphInfo glyph)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        MoveGap(index, 1);
        _items[_gapStart++] = glyph;
    }

    public void InsertRange(int index, List<GlyphInfo> glyphs)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        MoveGap(index, glyphs.Count);
        CollectionsMarshal.AsSpan(glyphs).CopyTo(_items.AsSpan(_gapStart));
        _gapStart += glyphs.Count;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        MoveGap(index, 0);
        _gapEnd++;
    }

    public void RemoveRange(int index, int count)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (Count - index < count) throw new ArgumentException("The range is past the end of the run.");
        MoveGap(index, 0);
        _gapEnd += count;
    }

    public List<GlyphInfo> GetRange(int index, int count)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (Count - index < count) throw new ArgumentException("The range is past the end of the run.");
        var range = new List<GlyphInfo>(count);
        for (int i = index; i < index + count; i++) range.Add(this[i]);
        return range;
    }

    /// <summary>Removes the glyphs that match, keeping the order of the others, and returns how many it removed.</summary>
    public int RemoveAll(Predicate<GlyphInfo> match)
    {
        int count = Count;
        MoveGap(count, 0);
        int kept = 0;
        for (int i = 0; i < count; i++)
            if (!match(_items[i])) _items[kept++] = _items[i];
        _gapStart = kept;
        return count - kept;
    }

    // Moves the gap to the index, first making it at least the given size.
    private void MoveGap(int index, int room)
    {
        if (_gapEnd - _gapStart < room) Grow(room);
        if (index < _gapStart)
        {
            int moved = _gapStart - index;
            Array.Copy(_items, index, _items, _gapEnd - moved, moved);
            _gapStart = index;
            _gapEnd -= moved;
        }
        else if (index > _gapStart)
        {
            int moved = index - _gapStart;
            Array.Copy(_items, _gapEnd, _items, _gapStart, moved);
            _gapStart = index;
            _gapEnd += moved;
        }
    }

    // Doubles the room, as a list does, or makes as much as is needed when that is more.
    private void Grow(int room)
    {
        int count = Count, after = _items.Length - _gapEnd;
        long capacity = Math.Max(2L * _items.Length, (long)count + room);
        if (capacity > Array.MaxLength) capacity = Math.Max(Array.MaxLength, (long)count + room);
        var items = new GlyphInfo[capacity];
        Array.Copy(_items, items, _gapStart);
        Array.Copy(_items, _gapEnd, items, capacity - after, after);
        _gapEnd = (int)(capacity - after);
        _items = items;
    }
}

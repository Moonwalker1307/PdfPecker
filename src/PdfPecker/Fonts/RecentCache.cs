using System.Diagnostics.CodeAnalysis;

namespace PdfPecker.Fonts;

/// <summary>
/// Values kept by string keys that drops the least recently used one when a new one would pass the capacity, so that a
/// font the process keeps for every conversion costs no more than the capacity however many keys it sees. Safe for
/// several threads: two threads that make a value for the same key at once both get the one that was kept.
/// </summary>
internal sealed class RecentCache<T> where T : class
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<(string Key, T Value)>> _nodes = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, T Value)> _order = new(); // most recently used first

    public RecentCache(int capacity) => _capacity = capacity;

    /// <summary>The number of values kept.</summary>
    public int Count
    {
        get
        {
            lock (_nodes) return _nodes.Count;
        }
    }

    /// <summary>The value kept for a key, which becomes the most recently used.</summary>
    public bool TryGet(string key, [MaybeNullWhen(false)] out T value)
    {
        lock (_nodes)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                MoveToFront(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>Keeps a value for a key, or returns the one another thread kept for it meanwhile.</summary>
    public T Add(string key, T value)
    {
        lock (_nodes)
        {
            if (_nodes.TryGetValue(key, out var node))
            {
                MoveToFront(node);
                return node.Value.Value;
            }
            _nodes[key] = _order.AddFirst((key, value));
            if (_nodes.Count > _capacity)
            {
                _nodes.Remove(_order.Last!.Value.Key);
                _order.RemoveLast();
            }
            return value;
        }
    }

    private void MoveToFront(LinkedListNode<(string Key, T Value)> node)
    {
        if (node == _order.First) return;
        _order.Remove(node);
        _order.AddFirst(node);
    }
}

namespace ClashTray.Core;

public sealed class BoundedBuffer<T>
{
    private readonly object _gate = new();
    private readonly LinkedList<T> _items = new();
    private readonly int _capacity;

    public BoundedBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items.AddLast(item);
            if (_items.Count > _capacity)
            {
                _items.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}

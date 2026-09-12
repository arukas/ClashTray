using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Keeps the UI-facing log stream bounded and folds identical adjacent lines.
/// </summary>
public sealed class BoundedLogBuffer
{
    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _items = new();
    private readonly int _capacity;
    private IReadOnlyList<LogEntry> _snapshot = Array.Empty<LogEntry>();
    private bool _snapshotDirty;

    public BoundedLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    public void Add(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry with { Message = ErrorSanitizer.Sanitize(entry.Message) };
        lock (_gate)
        {
            LogEntry? last = _items.Last?.Value;
            if (last is not null
                && string.Equals(last.Source, entry.Source, StringComparison.Ordinal)
                && string.Equals(last.Level, entry.Level, StringComparison.OrdinalIgnoreCase)
                && string.Equals(last.Message, entry.Message, StringComparison.Ordinal))
            {
                _items.Last!.Value = last with
                {
                    Timestamp = entry.Timestamp,
                    RepeatCount = last.RepeatCount + entry.RepeatCount
                };
            }
            else
            {
                _items.AddLast(entry);
            }

            while (_items.Count > _capacity)
            {
                _items.RemoveFirst();
            }

            _snapshotDirty = true;
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            if (_snapshotDirty)
            {
                _snapshot = _items.ToArray();
                _snapshotDirty = false;
            }

            return _snapshot;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return;
            }

            _items.Clear();
            _snapshotDirty = true;
        }
    }
}

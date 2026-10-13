using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Owns the runtime snapshot and its monotonic revision. Reducers are small
/// synchronous functions: external I/O must finish before a fact is submitted
/// here, and publication is deliberately outside this state lock.
/// </summary>
internal sealed class RuntimeStateStore
{
    private readonly object _gate = new();
    private RuntimeSnapshot _snapshot;
    private long _revision;

    public RuntimeStateStore(RuntimeSnapshot initialSnapshot)
    {
        _snapshot = Normalize(initialSnapshot);
    }

    public RuntimeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public long Revision => Interlocked.Read(ref _revision);

    public long Update(Func<RuntimeSnapshot, RuntimeSnapshot> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        lock (_gate)
        {
            RuntimeSnapshot next = Normalize(reducer(_snapshot));
            if (ReferenceEquals(next, _snapshot))
            {
                return _revision;
            }

            _snapshot = next;
            return ++_revision;
        }
    }

    public bool TryUpdate(
        long expectedRevision,
        Func<RuntimeSnapshot, RuntimeSnapshot> reducer,
        out long committedRevision)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        lock (_gate)
        {
            if (_revision != expectedRevision)
            {
                committedRevision = _revision;
                return false;
            }

            RuntimeSnapshot next = Normalize(reducer(_snapshot));
            if (ReferenceEquals(next, _snapshot))
            {
                committedRevision = _revision;
                return true;
            }

            _snapshot = next;
            committedRevision = ++_revision;
            return true;
        }
    }

    public bool TryUpdate(
        Func<RuntimeSnapshot, bool> condition,
        Func<RuntimeSnapshot, RuntimeSnapshot> reducer,
        out long committedRevision)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(reducer);
        lock (_gate)
        {
            if (!condition(_snapshot))
            {
                committedRevision = _revision;
                return false;
            }

            RuntimeSnapshot next = Normalize(reducer(_snapshot));
            if (ReferenceEquals(next, _snapshot))
            {
                committedRevision = _revision;
                return true;
            }

            _snapshot = next;
            committedRevision = ++_revision;
            return true;
        }
    }

    private static RuntimeSnapshot Normalize(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? error = ErrorSanitizer.SanitizeNullable(snapshot.ErrorMessage);
        string? coreError = ErrorSanitizer.SanitizeNullable(snapshot.Core.ErrorMessage);
        if (string.Equals(error, snapshot.ErrorMessage, StringComparison.Ordinal)
            && string.Equals(coreError, snapshot.Core.ErrorMessage, StringComparison.Ordinal))
        {
            return snapshot;
        }

        return snapshot with
        {
            Core = snapshot.Core with { ErrorMessage = coreError },
            ErrorMessage = error
        };
    }
}

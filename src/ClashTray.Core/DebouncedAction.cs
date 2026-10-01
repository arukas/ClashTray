namespace ClashTray.Core;

/// <summary>Coalesces input changes and rejects callbacks queued before cancellation or newer input.</summary>
public sealed class DebouncedAction : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<Action> _dispatch;
    private readonly Action _action;
    private readonly TimeProvider _timeProvider;
    private ITimer? _timer;
    private long _revision;
    private bool _pending;
    private bool _disposed;

    public DebouncedAction(Action<Action> dispatch, Action action, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(action);
        _dispatch = dispatch;
        _action = action;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsPending { get { lock (_gate) { return _pending; } } }

    public void Schedule()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long revision = ++_revision;
            _pending = true;
            _timer?.Dispose();
            _timer = _timeProvider.CreateTimer(_ => _dispatch(() => Run(revision)), null,
                TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _revision++;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Run(long revision)
    {
        lock (_gate)
        {
            if (_disposed || revision != _revision || !_pending)
            {
                return;
            }

            _pending = false;
            _timer?.Dispose();
            _timer = null;
            _action();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Cancel();
        }

        GC.SuppressFinalize(this);
    }
}

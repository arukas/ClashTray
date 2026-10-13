namespace ClashTray.Core;

internal sealed class SnapshotPublishThrottle : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Action _publish;
    private readonly CancellationTokenSource _stopCts;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeSpan _interval;
    private readonly Task _workerTask;
    private bool _pending;
    private bool _disposed;
    private TaskCompletionSource? _pendingCompletion;
    private Task? _disposeTask;
    private Exception? _fault;
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

    public SnapshotPublishThrottle(
        Action publish,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workerTask = RunAsync();
    }

    public Exception? Fault
    {
        get
        {
            lock (_gate)
            {
                return _fault;
            }
        }
    }

    public void Queue()
    {
        lock (_gate)
        {
            if (_disposed || _stopCts.IsCancellationRequested || _fault is not null || _pending)
            {
                return;
            }

            _pending = true;
            _signal.Release();
        }
    }

    public Task RequestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task pendingPublish;
        lock (_gate)
        {
            if (_fault is not null)
            {
                return Task.FromException(_fault);
            }

            if (_disposed || _stopCts.IsCancellationRequested)
            {
                return Task.FromCanceled(_stopCts.Token);
            }

            _pendingCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pendingPublish = _pendingCompletion.Task;
            if (!_pending)
            {
                _pending = true;
                _signal.Release();
            }
        }

        return pendingPublish.WaitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        await _stopCts.CancelAsync().ConfigureAwait(false);
        TaskCompletionSource? pendingCompletion;
        lock (_gate)
        {
            _disposed = true;
            pendingCompletion = _pendingCompletion;
            _pendingCompletion = null;
            _pending = false;
        }

        pendingCompletion?.TrySetCanceled(_stopCts.Token);
        _signal.Release();
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        finally
        {
            _signal.Dispose();
            _stopCts.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A publish callback is an application boundary; its failure is captured as a terminal, observable worker fault.")]
    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stopCts.Token).ConfigureAwait(false);
                while (true)
                {
                    TimeSpan delay;
                    TaskCompletionSource? completion = null;
                    lock (_gate)
                    {
                        if (!_pending)
                        {
                            break;
                        }

                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        delay = _lastPublished + _interval - now;
                        if (delay <= TimeSpan.Zero)
                        {
                            _pending = false;
                            _lastPublished = now;
                            completion = _pendingCompletion;
                            _pendingCompletion = null;
                        }
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _stopCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        _publish();
                        completion?.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        completion?.TrySetException(exception);
                        SetTerminalFault(exception);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
            CancelPending();
        }
        catch (Exception exception)
        {
            SetTerminalFault(exception);
        }
    }

    private void SetTerminalFault(Exception exception)
    {
        TaskCompletionSource? pendingCompletion;
        lock (_gate)
        {
            _fault ??= exception;
            pendingCompletion = _pendingCompletion;
            _pendingCompletion = null;
            _pending = false;
        }

        pendingCompletion?.TrySetException(exception);
    }

    private void CancelPending()
    {
        TaskCompletionSource? pendingCompletion;
        lock (_gate)
        {
            pendingCompletion = _pendingCompletion;
            _pendingCompletion = null;
            _pending = false;
        }

        pendingCompletion?.TrySetCanceled(_stopCts.Token);
    }
}

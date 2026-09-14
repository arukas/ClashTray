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
    private TaskCompletionSource? _pendingCompletion;
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

    public void Queue()
    {
        lock (_gate)
        {
            if (_stopCts.IsCancellationRequested || _pending)
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
            if (_stopCts.IsCancellationRequested)
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

    public async ValueTask DisposeAsync()
    {
        await _stopCts.CancelAsync();
        TaskCompletionSource? pendingCompletion;
        lock (_gate)
        {
            pendingCompletion = _pendingCompletion;
            _pendingCompletion = null;
            _pending = false;
        }

        pendingCompletion?.TrySetCanceled(_stopCts.Token);
        _signal.Release();
        try
        {
            await _workerTask;
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
        }
        finally
        {
            _signal.Dispose();
            _stopCts.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stopCts.Token);
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
                        await Task.Delay(delay, _stopCts.Token);
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
                        FailPending(exception);
                        throw;
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
            FailPending(exception);
            throw;
        }
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

    private void FailPending(Exception exception)
    {
        TaskCompletionSource? pendingCompletion;
        lock (_gate)
        {
            pendingCompletion = _pendingCompletion;
            _pendingCompletion = null;
            _pending = false;
        }

        pendingCompletion?.TrySetException(exception);
    }
}

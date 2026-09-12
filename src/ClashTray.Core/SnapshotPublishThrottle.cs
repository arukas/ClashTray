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

    public void Request()
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

    public async ValueTask DisposeAsync()
    {
        await _stopCts.CancelAsync();
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
                        }
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _stopCts.Token);
                        continue;
                    }

                    _publish();
                }
            }
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
        }
    }
}

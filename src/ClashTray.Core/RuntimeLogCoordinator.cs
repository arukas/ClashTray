using System.Net.WebSockets;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Owns the local core log pipeline: the bounded log buffer mirrored into the
/// runtime snapshot, Mihomo process stdout/stderr ingestion, and the live
/// controller log stream used while the service hosts the core. Logs of a
/// selected remote endpoint stay inside <see cref="RemoteControllerRefreshCoordinator"/>.
/// </summary>
internal sealed class RuntimeLogCoordinator : IDisposable
{
    private readonly RuntimeStateStore _stateStore;
    private readonly Func<MihomoApiClient?> _apiAccessor;
    private readonly Func<bool> _serviceCoreAccessor;
    private readonly Func<string> _logLevelAccessor;
    private readonly Action _queueThrottledPublish;
    private readonly Action _publish;
    private readonly CancellationToken _runtimeCancellation;
    private readonly BoundedLogBuffer _logBuffer = new(500);
    private readonly object _logStreamGate = new();
    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;

    public RuntimeLogCoordinator(
        RuntimeStateStore stateStore,
        Func<MihomoApiClient?> apiAccessor,
        Func<bool> serviceCoreAccessor,
        Func<string> logLevelAccessor,
        Action queueThrottledPublish,
        Action publish,
        CancellationToken runtimeCancellation)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(apiAccessor);
        ArgumentNullException.ThrowIfNull(serviceCoreAccessor);
        ArgumentNullException.ThrowIfNull(logLevelAccessor);
        ArgumentNullException.ThrowIfNull(queueThrottledPublish);
        ArgumentNullException.ThrowIfNull(publish);
        _stateStore = stateStore;
        _apiAccessor = apiAccessor;
        _serviceCoreAccessor = serviceCoreAccessor;
        _logLevelAccessor = logLevelAccessor;
        _queueThrottledPublish = queueThrottledPublish;
        _publish = publish;
        _runtimeCancellation = runtimeCancellation;
    }

    internal IReadOnlyList<LogEntry> Snapshot() => _logBuffer.Snapshot();

    internal void AddApplicationLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
    }

    internal void AddMihomoLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
        _queueThrottledPublish();
    }

    internal void OnProcessLogLine(string line, bool isError)
    {
        AddMihomoLog(new LogEntry(DateTimeOffset.UtcNow, "mihomo", isError ? "error" : "info", line));
    }

    internal void ClearLogs()
    {
        _logBuffer.Clear();
        _stateStore.Update(snapshot => snapshot with { Logs = [] });
        _publish();
    }

    internal void EnsureLogStreamStarted()
    {
        if (!_serviceCoreAccessor())
        {
            return;
        }

        lock (_logStreamGate)
        {
            MihomoApiClient? api = _apiAccessor();
            if (api is null || _logStreamTask is { IsCompleted: false })
            {
                return;
            }

            _logStreamCts?.Dispose();
            CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(_runtimeCancellation);
            _logStreamCts = streamCts;
            _logStreamTask = Task.Run(
                () => RunLogStreamAsync(api, streamCts.Token),
                CancellationToken.None);
        }
    }

    internal async Task StopLogStreamAsync()
    {
        Task? task;
        CancellationTokenSource? streamCts;
        lock (_logStreamGate)
        {
            task = _logStreamTask;
            streamCts = _logStreamCts;
            _logStreamTask = null;
            _logStreamCts = null;
        }

        if (streamCts is not null)
        {
            try
            {
                await streamCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // A concurrent Dispose may have released the source after it was detached.
            }
        }
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            streamCts?.Dispose();
        }
    }

    private async Task RunLogStreamAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = TimeSpan.FromSeconds(1);
        string path = $"/logs?level={Uri.EscapeDataString(_logLevelAccessor())}&format=structured";
        while (!cancellationToken.IsCancellationRequested && ReferenceEquals(_apiAccessor(), api))
        {
            try
            {
                using ClientWebSocket socket = await api.ConnectWebSocketAsync(path, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(1);
                await ControllerLogStreamReceiver.ReceiveLogMessagesAsync(
                    socket,
                    "mihomo",
                    AddMihomoLog,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (WebSocketException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }

            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_apiAccessor(), api))
            {
                break;
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? streamCts;
        lock (_logStreamGate)
        {
            streamCts = _logStreamCts;
            _logStreamCts = null;
        }

        if (streamCts is not null)
        {
            try
            {
                streamCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            streamCts.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}

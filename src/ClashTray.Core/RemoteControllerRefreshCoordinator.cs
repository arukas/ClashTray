using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal enum MutationRefreshScope
{
    Full,
    Mode,
    ProxySelection
}

/// <summary>
/// Owns the remote-endpoint controller pipeline: refresh lifecycle, snapshot
/// data cache, live log stream, and mutation follow-up refreshes for the
/// currently selected remote session. All state transitions are keyed by the
/// session manager's generation and selection revision, so work belonging to
/// a replaced session is discarded instead of racing the new one. The local
/// (service-owned) core pipeline stays in <see cref="ClashTrayRuntime"/>.
/// </summary>
internal sealed class RemoteControllerRefreshCoordinator : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly EndpointSessionManager _sessions;
    private readonly CancellationToken _runtimeCancellation;
    private readonly Func<string> _logLevelAccessor;
    private readonly Action _publishAppSnapshot;
    private readonly Action _queueThrottledPublish;
    private readonly Action<string, string, Exception, int> _logControllerFailure;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<EndpointSession, EndpointSessionStatusEventArgs, CancellationToken, Task> _logStreamRunner;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private BoundedLogBuffer _logBuffer = new(500);
    private CancellationTokenSource? _refreshCts;
    private Task? _refreshTask;
    private RemoteControllerData? _controllerData;
    private RemoteRefreshCompletion? _refreshCompletion;

    public RemoteControllerRefreshCoordinator(
        EndpointSessionManager sessions,
        Func<string> logLevelAccessor,
        Action publishAppSnapshot,
        Action queueThrottledPublish,
        Action<string, string, Exception, int> logControllerFailure,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        Func<EndpointSession, EndpointSessionStatusEventArgs, CancellationToken, Task>? logStreamRunner,
        CancellationToken runtimeCancellation)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logLevelAccessor);
        ArgumentNullException.ThrowIfNull(publishAppSnapshot);
        ArgumentNullException.ThrowIfNull(queueThrottledPublish);
        ArgumentNullException.ThrowIfNull(logControllerFailure);
        _sessions = sessions;
        _runtimeCancellation = runtimeCancellation;
        _logLevelAccessor = logLevelAccessor;
        _publishAppSnapshot = publishAppSnapshot;
        _queueThrottledPublish = queueThrottledPublish;
        _logControllerFailure = logControllerFailure;
        _delayAsync = delayAsync ?? Task.Delay;
        _logStreamRunner = logStreamRunner ?? RunRemoteLogStreamAsync;
    }

    internal void HandleSessionStatusChanged(object? sender, EndpointSessionStatusEventArgs status)
    {
        CancellationTokenSource? previousRefresh;
        RemoteRefreshCompletion? previousCompletion;
        lock (_gate)
        {
            previousRefresh = _refreshCts;
            previousCompletion = _refreshCompletion;
            _refreshCts = null;
            _controllerData = null;
            _logBuffer = new BoundedLogBuffer(500);
            _refreshCompletion = status.State == EndpointSessionState.Connected
                ? new RemoteRefreshCompletion(
                    status.Generation,
                    status.SelectionRevision,
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously))
                : null;
        }

        if (previousCompletion is not null)
        {
            previousCompletion.Completion.TrySetCanceled();
        }

        if (previousRefresh is not null)
        {
            _ = CancelRefreshAsync(previousRefresh);
        }

        _publishAppSnapshot();
        _ = RestartRefreshAsync(status);
    }

    internal ControllerSessionSnapshot? BuildActiveControllerSnapshot()
    {
        EndpointSessionStatusEventArgs status = _sessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return null;
        }

        EndpointSession? session = _sessions.Current;
        ControllerSessionSnapshot snapshot = EndpointSessionSnapshotFactory.Create(
            status.Endpoint,
            status,
            session?.Handshake);
        MihomoControllerSnapshotData? remoteData = null;
        lock (_gate)
        {
            if (_controllerData is { } currentData
                && currentData.Generation == status.Generation
                && currentData.SelectionRevision == status.SelectionRevision)
            {
                remoteData = currentData.Snapshot;
            }
        }

        return remoteData is null
            ? snapshot
            : snapshot with
            {
                State = remoteData.ErrorMessage is null
                    ? snapshot.State
                    : snapshot.State == EndpointSessionState.Connected
                        ? EndpointSessionState.Reconnecting
                        : snapshot.State,
                LastConfirmedAt = remoteData.LastConfirmedAt ?? snapshot.LastConfirmedAt,
                Status = remoteData.Status,
                ProxyGroups = remoteData.ProxyGroups,
                ProxyNodes = remoteData.ProxyNodes,
                Connections = remoteData.Connections,
                Rules = remoteData.Rules,
                Providers = remoteData.Providers,
                RuleProviders = remoteData.RuleProviders,
                Logs = remoteData.Logs,
                ErrorMessage = remoteData.ErrorMessage ?? snapshot.ErrorMessage,
                ErrorCode = remoteData.ErrorMessage is null
                    ? snapshot.ErrorCode
                    : ErrorCode.EndpointStaleResult
            };
    }

    internal EndpointSession? CaptureActiveRemoteSession(
        EndpointCommand command,
        string staleSessionMessage)
    {
        EndpointSessionStatusEventArgs status = _sessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return null;
        }

        EndpointSession? session = _sessions.Current;
        if (session is null || !IsCurrentRemoteSession(session, status))
        {
            throw new InvalidOperationException(staleSessionMessage);
        }

        EndpointCommandPolicy.EnsureAllowed(
            session.Endpoint.Kind,
            session.Capabilities,
            command);
        return session;
    }

    internal bool IsCurrentRemoteSession(
        EndpointSession session,
        EndpointSessionStatusEventArgs status)
    {
        EndpointSession? current = _sessions.Current;
        EndpointSessionStatusEventArgs currentStatus = _sessions.Status;
        return ReferenceEquals(current, session)
            && current.Generation == status.Generation
            && current.SelectionRevision == status.SelectionRevision
            && currentStatus.Endpoint.Id == status.Endpoint.Id
            && currentStatus.Generation == status.Generation
            && currentStatus.SelectionRevision == status.SelectionRevision
            && currentStatus.State == EndpointSessionState.Connected;
    }

    internal async Task WaitForRefreshAsync(
        EndpointSession session,
        CancellationToken cancellationToken)
    {
        Task? completion = null;
        lock (_gate)
        {
            if (_refreshCompletion is { } current
                && current.Generation == session.Generation
                && current.SelectionRevision == session.SelectionRevision)
            {
                completion = current.Completion.Task;
            }
        }

        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<bool> RefreshSnapshotAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        CancellationToken cancellationToken)
    {
        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MihomoControllerSnapshotData? previousData = null;
            lock (_gate)
            {
                if (_controllerData is { } currentData
                    && currentData.Generation == status.Generation
                    && currentData.SelectionRevision == status.SelectionRevision)
                {
                    previousData = currentData.Snapshot;
                }
            }

            MihomoControllerSnapshotData snapshot = await MihomoControllerSnapshotReader.ReadAsync(
                    session.Api,
                    session.Handshake.Version,
                    $"mihomo/{status.Endpoint.DisplayName}",
                    previousData,
                    includeLogs: previousData is null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentRemoteSession(session, status))
            {
                return false;
            }

            lock (_gate)
            {
                if (previousData is null)
                {
                    foreach (LogEntry entry in snapshot.Logs)
                    {
                        _logBuffer.Add(entry);
                    }
                }

                snapshot = snapshot with { Logs = _logBuffer.Snapshot() };
                _controllerData = new RemoteControllerData(
                    status.Generation,
                    status.SelectionRevision,
                    snapshot);
                CompleteRefreshUnsafe(
                    status.Generation,
                    status.SelectionRevision,
                    succeeded: true);
            }

            _publishAppSnapshot();
            return true;
        }
        finally
        {
            _readLock.Release();
        }
    }

    internal async Task<bool> RefreshMutationSnapshotAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        MutationRefreshScope refreshScope,
        CancellationToken cancellationToken)
    {
        if (refreshScope == MutationRefreshScope.Full)
        {
            return await RefreshSnapshotAsync(session, status, cancellationToken)
                .ConfigureAwait(false);
        }

        await _readLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MihomoControllerSnapshotData snapshot;
            lock (_gate)
            {
                snapshot = _controllerData is { } current
                    && current.Generation == status.Generation
                    && current.SelectionRevision == status.SelectionRevision
                    ? current.Snapshot
                    : CreateRemoteSnapshotBaseline(session);
            }

            if (refreshScope == MutationRefreshScope.Mode)
            {
                using JsonDocument configuration = await session.Api.GetConfigurationAsync(
                    force: false,
                    cancellationToken);
                ProxyMode mode = MihomoDataParser.ParseMode(configuration) ?? snapshot.Status.Mode;
                snapshot = snapshot with { Status = snapshot.Status with { Mode = mode, ErrorMessage = null }, ErrorMessage = null };
            }
            else
            {
                using JsonDocument proxies = await session.Api.GetProxiesAsync(cancellationToken);
                (IReadOnlyList<ProxyGroup> groups, IReadOnlyList<ProxyNode> nodes) = MihomoDataParser.ParseProxies(proxies);
                snapshot = snapshot with
                {
                    ProxyGroups = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyGroups, groups, SnapshotDataComparer.ProxyGroupsEqual),
                    ProxyNodes = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyNodes, nodes, SnapshotDataComparer.ProxyNodesEqual),
                    ErrorMessage = null
                };
            }

            if (!IsCurrentRemoteSession(session, status))
            {
                return false;
            }

            lock (_gate)
            {
                snapshot = snapshot with
                {
                    Status = snapshot.Status with { ErrorMessage = null },
                    Logs = _logBuffer.Snapshot(),
                    ErrorMessage = null,
                    LastConfirmedAt = DateTimeOffset.UtcNow
                };
                _controllerData = new RemoteControllerData(
                    status.Generation,
                    status.SelectionRevision,
                    snapshot);
            }

            _publishAppSnapshot();
            return true;
        }
        finally
        {
            _readLock.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Refresh task failure during shutdown is logged and must not escape the disposal path.")]
    internal async Task StopRefreshAsync()
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? refreshCts;
            Task? refreshTask;
            lock (_gate)
            {
                refreshCts = _refreshCts;
                refreshTask = _refreshTask;
                _refreshCts = null;
                _refreshTask = null;
                _controllerData = null;
                _refreshCompletion?.Completion.TrySetCanceled();
                _refreshCompletion = null;
            }

            if (refreshCts is not null)
            {
                await refreshCts.CancelAsync().ConfigureAwait(false);
            }
            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                }
            }

            refreshCts?.Dispose();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        _refreshCts?.Dispose();
        _refreshCts = null;
        _lifecycleLock.Dispose();
        _readLock.Dispose();
        GC.SuppressFinalize(this);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Refresh cancellation is best-effort during session replacement; any failure is logged and must not escape a fire-and-forget task.")]
    private async Task CancelRefreshAsync(CancellationTokenSource refreshCts)
    {
        try
        {
            await refreshCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            _logControllerFailure("远程 Controller 刷新取消", "/configs 或 /proxies", exception, 0);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Awaiting the replaced refresh task is best-effort cleanup; its failure is logged and must not escape a fire-and-forget task.")]
    private async Task RestartRefreshAsync(EndpointSessionStatusEventArgs requestedStatus)
    {
        try
        {
            await _lifecycleLock.WaitAsync(_runtimeCancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtimeCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            CancellationTokenSource? previousRefresh;
            Task? previousTask;
            lock (_gate)
            {
                previousRefresh = _refreshCts;
                previousTask = _refreshTask;
                _refreshCts = null;
                _refreshTask = null;
            }

            if (previousRefresh is not null)
            {
                await previousRefresh.CancelAsync().ConfigureAwait(false);
            }
            if (previousTask is not null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    _logControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                }
            }

            previousRefresh?.Dispose();
            if (requestedStatus.State != EndpointSessionState.Connected)
            {
                return;
            }

            EndpointSession? session = _sessions.Current;
            EndpointSessionStatusEventArgs currentStatus = _sessions.Status;
            if (session is null
                || currentStatus.Endpoint.Id != requestedStatus.Endpoint.Id
                || currentStatus.Generation != requestedStatus.Generation
                || currentStatus.SelectionRevision != requestedStatus.SelectionRevision
                || currentStatus.State != EndpointSessionState.Connected)
            {
                return;
            }

            CancellationTokenSource refreshCts = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCancellation);
            Task refreshTask = RunRefreshLoopAsync(
                session,
                requestedStatus,
                refreshCts);
            lock (_gate)
            {
                _refreshCts = refreshCts;
                _refreshTask = refreshTask;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The refresh loop keeps remote session data alive across arbitrary failures: each failure is logged, surfaced in the snapshot, and retried with backoff.")]
    private async Task RunRefreshLoopAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        CancellationTokenSource refreshCts)
    {
        bool initialRefreshPending = true;
        bool remoteLogStreamStarted = false;
        Task? remoteLogStreamTask = null;
        TimeSpan retryDelay = RetryDelay;
        try
        {
            while (true)
            {
                refreshCts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (!await RefreshSnapshotAsync(
                            session,
                            status,
                            refreshCts.Token)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    if (!remoteLogStreamStarted)
                    {
                        remoteLogStreamStarted = true;
                        remoteLogStreamTask = _logStreamRunner(
                            session,
                            status,
                            refreshCts.Token);
                    }

                    initialRefreshPending = false;
                    retryDelay = RetryDelay;
                    await _delayAsync(RefreshInterval, refreshCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (!IsCurrentRemoteSession(session, status))
                    {
                        return;
                    }

                    MarkControllerFailure(session, status, exception);

                    lock (_gate)
                    {
                        if (initialRefreshPending)
                        {
                            CompleteRefreshUnsafe(
                                status.Generation,
                                status.SelectionRevision,
                                succeeded: false);
                            initialRefreshPending = false;
                        }
                    }

                    _logControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                    _publishAppSnapshot();
                    await _delayAsync(retryDelay, refreshCts.Token)
                        .ConfigureAwait(false);
                    retryDelay = TimeSpan.FromTicks(Math.Min(
                        MaxRetryDelay.Ticks,
                        retryDelay.Ticks * 2));
                }
            }
        }
        catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRemoteSession(session, status))
            {
                MarkControllerFailure(session, status, exception);
                lock (_gate)
                {
                    CompleteRefreshUnsafe(
                        status.Generation,
                        status.SelectionRevision,
                        succeeded: false);
                }
                _logControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                _publishAppSnapshot();
            }
        }
        finally
        {
            if (remoteLogStreamTask is not null)
            {
                try
                {
                    await refreshCts.CancelAsync().ConfigureAwait(false);
                    await remoteLogStreamTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _logControllerFailure("远程 Mihomo 实时日志", "/logs", exception, 0);
                }
            }
        }
    }

    private void MarkControllerFailure(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        Exception exception)
    {
        if (!IsCurrentRemoteSession(session, status))
        {
            return;
        }

        string errorMessage = $"远程 Controller 部分数据刷新失败：{ErrorSanitizer.Sanitize(exception)}";
        lock (_gate)
        {
            MihomoControllerSnapshotData snapshot = _controllerData is { } currentData
                && currentData.Generation == status.Generation
                && currentData.SelectionRevision == status.SelectionRevision
                ? currentData.Snapshot
                : CreateRemoteSnapshotBaseline(session);
            _controllerData = new RemoteControllerData(
                status.Generation,
                status.SelectionRevision,
                snapshot with
                {
                    Status = snapshot.Status with { ErrorMessage = errorMessage },
                    ErrorMessage = errorMessage
                });
        }
    }

    private void CompleteRefreshUnsafe(
        long generation,
        long selectionRevision,
        bool succeeded)
    {
        if (_refreshCompletion is { } completion
            && completion.Generation == generation
            && completion.SelectionRevision == selectionRevision)
        {
            completion.Completion.TrySetResult(succeeded);
        }
    }

    private static MihomoControllerSnapshotData CreateRemoteSnapshotBaseline(EndpointSession session)
    {
        CoreStatus status = new(
            CoreState.Running,
            session.Handshake.Version,
            ConfigurationName: null,
            ProxyMode.Rule,
            UploadBytesPerSecond: 0,
            DownloadBytesPerSecond: 0,
            UploadBytes: 0,
            DownloadBytes: 0,
            ConnectionCount: 0,
            MemoryBytes: 0,
            ErrorMessage: null);
        return new MihomoControllerSnapshotData(status, [], [], [], [], [], [], [], null, null);
    }

    private async Task RunRemoteLogStreamAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        CancellationToken cancellationToken)
    {
        TimeSpan retryDelay = TimeSpan.FromSeconds(1);
        string path = $"/logs?level={Uri.EscapeDataString(_logLevelAccessor())}&format=structured";
        string logSource = $"mihomo/{status.Endpoint.DisplayName}";
        while (!cancellationToken.IsCancellationRequested
            && IsCurrentRemoteSession(session, status))
        {
            try
            {
                using ClientWebSocket socket = await session.ConnectWebSocketAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(1);
                await ControllerLogStreamReceiver.ReceiveLogMessagesAsync(
                    socket,
                    logSource,
                    entry => AddRemoteLog(session, status, entry),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
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
            catch (AuthenticationException)
            {
            }
            catch (CryptographicException)
            {
            }

            if (cancellationToken.IsCancellationRequested
                || !IsCurrentRemoteSession(session, status))
            {
                break;
            }

            _logControllerFailure(
                "远程 Mihomo 实时日志",
                "/logs",
                new IOException("远程日志 WebSocket 已断开。"),
                retryDelay == TimeSpan.FromSeconds(1) ? 0 : 1);
            _queueThrottledPublish();
            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
        }
    }

    private void AddRemoteLog(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        LogEntry entry)
    {
        lock (_gate)
        {
            if (!IsCurrentRemoteSession(session, status)
                || _controllerData is not { } currentData
                || currentData.Generation != status.Generation
                || currentData.SelectionRevision != status.SelectionRevision)
            {
                return;
            }

            _logBuffer.Add(entry);
            _controllerData = currentData with
            {
                Snapshot = currentData.Snapshot with
                {
                    Logs = _logBuffer.Snapshot()
                }
            };
        }

        _queueThrottledPublish();
    }

    private sealed record RemoteControllerData(
        long Generation,
        long SelectionRevision,
        MihomoControllerSnapshotData Snapshot);

    private sealed record RemoteRefreshCompletion(
        long Generation,
        long SelectionRevision,
        TaskCompletionSource<bool> Completion);
}

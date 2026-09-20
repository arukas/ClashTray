using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ModeIntent(ProxyMode Mode, bool RouteToRemote);

internal sealed record ProxySelectionIntent(string Group, string Proxy);

/// <summary>
/// Executes one controller mutation against the active local or remote
/// endpoint and lands the confirmed follow-up refresh. Implemented by
/// <see cref="ClashTrayRuntime"/> because the refresh path still owns the
/// shared data lock and polling pipeline.
/// </summary>
internal delegate Task ControllerMutationExecutor(
    EndpointCommand command,
    string staleSessionMessage,
    string remoteRefreshFailureMessage,
    Func<MihomoApiClient, long, CancellationToken, Task> localOperation,
    Func<EndpointSession, CancellationToken, Task> remoteOperation,
    CancellationToken cancellationToken,
    bool routeToRemote,
    bool includeRulesAndProviders,
    MutationRefreshScope refreshScope);

/// <summary>
/// Owns the user-facing proxy operations: mode switching, node selection, and
/// latency testing. Mode and selection intents are coalesced latest-wins,
/// delay tests are single-flight per node/group, and every mutation runs on
/// the shared lane of the runtime operation gate. The intent tables are
/// bounded so attacker-controlled remote group names cannot grow them without
/// limit.
/// </summary>
internal sealed class ProxyOperationCoordinator
{
    private static readonly Uri DelayTestUri = new("https://www.gstatic.com/generate_204");

    private readonly OperationGate _operationGate;
    private readonly RuntimeStateStore _stateStore;
    private readonly EndpointSessionManager _endpointSessions;
    private readonly RemoteControllerRefreshCoordinator _remoteRefresh;
    private readonly RuntimeLogCoordinator _logs;
    private readonly ControllerSessionGuard _controllerGuard;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly ControllerMutationExecutor _mutationExecutor;
    private readonly Func<MihomoApiClient, long, IReadOnlyDictionary<string, int?>, CancellationToken, Task> _groupDelayCommitter;
    private readonly Action _publish;
    private readonly CancellationToken _runtimeCancellation;
    private readonly LatestWinsOperation<ModeIntent> _modeOperation = new("模式");
    private readonly object _proxyOperationGate = new();
    private readonly Dictionary<string, LatestWinsOperation<ProxySelectionIntent>> _proxyOperations =
        new(StringComparer.Ordinal);
    private readonly object _delayOperationGate = new();
    private readonly Dictionary<string, SingleFlightOperation<int?>> _proxyDelayOperations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SingleFlightOperation<IReadOnlyDictionary<string, int?>>> _proxyGroupDelayOperations =
        new(StringComparer.Ordinal);

    public ProxyOperationCoordinator(
        OperationGate operationGate,
        RuntimeStateStore stateStore,
        EndpointSessionManager endpointSessions,
        RemoteControllerRefreshCoordinator remoteRefresh,
        RuntimeLogCoordinator logs,
        ControllerSessionGuard controllerGuard,
        Func<AppSettings> settingsAccessor,
        ControllerMutationExecutor mutationExecutor,
        Func<MihomoApiClient, long, IReadOnlyDictionary<string, int?>, CancellationToken, Task> groupDelayCommitter,
        Action publish,
        CancellationToken runtimeCancellation)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(endpointSessions);
        ArgumentNullException.ThrowIfNull(remoteRefresh);
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(controllerGuard);
        ArgumentNullException.ThrowIfNull(settingsAccessor);
        ArgumentNullException.ThrowIfNull(mutationExecutor);
        ArgumentNullException.ThrowIfNull(groupDelayCommitter);
        ArgumentNullException.ThrowIfNull(publish);
        _operationGate = operationGate;
        _stateStore = stateStore;
        _endpointSessions = endpointSessions;
        _remoteRefresh = remoteRefresh;
        _logs = logs;
        _controllerGuard = controllerGuard;
        _settingsAccessor = settingsAccessor;
        _mutationExecutor = mutationExecutor;
        _groupDelayCommitter = groupDelayCommitter;
        _publish = publish;
        _runtimeCancellation = runtimeCancellation;
    }

    public async Task SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        await _modeOperation.RequestAsync(
                new ModeIntent(mode, RouteToRemote: true),
                (intent, token) => SetModeIntentCoreAsync(
                    intent,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetLocalModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        await _modeOperation.RequestAsync(
                new ModeIntent(mode, RouteToRemote: false),
                (intent, token) => SetModeIntentCoreAsync(
                    intent,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ModeIntent> SetModeIntentCoreAsync(
        ModeIntent intent,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationGate.AcquireSharedAsync(cancellationToken))
        {
            await _mutationExecutor(
                EndpointCommand.SwitchMode,
                "模式切换期间核心会话已切换，请重试。",
                "远程端点模式切换结果无法确认，请重试。",
                (api, _, token) => api.SetModeAsync(intent.Mode, token),
                (session, token) => session.Api.SetModeAsync(intent.Mode, token),
                cancellationToken,
                intent.RouteToRemote,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Mode);
        }

        return intent;
    }

    public Task SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);
        LatestWinsOperation<ProxySelectionIntent> operation = GetProxySelectionOperation(group);
        ProxySelectionIntent intent = new(group, proxy);
        return SelectProxyLatestAsync(operation, intent, cancellationToken);
    }

    private LatestWinsOperation<ProxySelectionIntent> GetProxySelectionOperation(string group)
    {
        lock (_proxyOperationGate)
        {
            if (!_proxyOperations.TryGetValue(group, out LatestWinsOperation<ProxySelectionIntent>? operation))
            {
                // Keep the intent table bounded even if a remote endpoint
                // returns attacker-controlled group names over time.
                if (!TrimIdleLatestOperations(_proxyOperations))
                {
                    throw new OperationBusyException("节点");
                }

                operation = new LatestWinsOperation<ProxySelectionIntent>("节点");
                _proxyOperations[group] = operation;
            }

            return operation;
        }
    }

    private async Task SelectProxyLatestAsync(
        LatestWinsOperation<ProxySelectionIntent> operation,
        ProxySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        await operation.RequestAsync(
                intent,
                (requested, token) => SelectProxyIntentCoreAsync(requested, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A failed best-effort connection cleanup after a successful node switch is captured into disconnectException and surfaced as a typed InvalidOperationException; it must not fault the mutation itself.")]
    private async Task<ProxySelectionIntent> SelectProxyIntentCoreAsync(
        ProxySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationGate.AcquireSharedAsync(cancellationToken))
        {
            string? previousProxy = _stateStore.Snapshot.ProxyGroups
                .FirstOrDefault(item => string.Equals(item.Name, intent.Group, StringComparison.Ordinal))
                ?.Current;
            Exception? disconnectException = null;
            bool selectionChanged = previousProxy is not null
                && !string.Equals(previousProxy, intent.Proxy, StringComparison.Ordinal);

            await _mutationExecutor(
                EndpointCommand.SwitchProxy,
                "节点切换期间核心会话已切换，请重新选择节点。",
                "远程端点节点切换结果无法确认，请重试。",
                async (api, generation, token) =>
                {
                    await api.SelectProxyAsync(intent.Group, intent.Proxy, token);
                    _controllerGuard.EnsureSession(
                        api,
                        generation,
                        "节点切换期间核心会话已切换，请重新选择节点。");
                    if (_settingsAccessor().DisconnectConnectionsAfterProxySwitch && selectionChanged)
                    {
                        try
                        {
                            _controllerGuard.EnsureCommand(
                                api,
                                generation,
                                EndpointCommand.CloseConnection,
                                "节点切换期间核心会话已切换，请重新选择节点。");
                            await api.CloseAllConnectionsAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            _controllerGuard.EnsureSession(
                                api,
                                generation,
                                "节点切换期间核心会话已切换，请重新选择节点。");
                            disconnectException = exception;
                            _logs.AddApplicationLog(new LogEntry(
                                DateTimeOffset.UtcNow,
                                "ClashTray",
                                "error",
                                $"节点已切换，但未能断开旧连接：{ErrorSanitizer.Sanitize(exception)}"));
                        }
                    }
                },
                (session, token) => session.Api.SelectProxyAsync(intent.Group, intent.Proxy, token),
                cancellationToken,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.ProxySelection);

            if (disconnectException is not null)
            {
                const string message = "节点已切换，但未能断开旧连接。";
                _stateStore.Update(snapshot => snapshot with
                {
                    ErrorMessage = message,
                    Logs = _logs.Snapshot()
                });
                _publish();
                throw new InvalidOperationException(message, disconnectException);
            }
        }

        return intent;
    }

    public Task<int?> TestProxyDelayAsync(string proxy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);
        SingleFlightOperation<int?> operation = GetProxyDelayOperation(proxy);
        return operation.RequestAsync(
            token => TestProxyDelayCoreAsync(proxy, token),
            cancellationToken);
    }

    private SingleFlightOperation<int?> GetProxyDelayOperation(string proxy)
    {
        lock (_delayOperationGate)
        {
            if (!_proxyDelayOperations.TryGetValue(proxy, out SingleFlightOperation<int?>? operation))
            {
                if (!TrimIdleOperations(_proxyDelayOperations))
                {
                    throw new OperationBusyException("节点测速");
                }

                operation = new SingleFlightOperation<int?>("节点测速");
                _proxyDelayOperations[proxy] = operation;
            }

            return operation;
        }
    }

    private async Task<int?> TestProxyDelayCoreAsync(
        string proxy,
        CancellationToken operationCancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            _runtimeCancellation);
        CancellationToken cancellationToken = linked.Token;
        ThrowIfQuiescing();
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
            EndpointCommand.TestDelay,
            "测速期间远程端点会话已切换，请重新测速。");
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            using JsonDocument remoteResponse = await remoteSession.Api.TestDelayAsync(
                proxy,
                DelayTestUri,
                5000,
                cancellationToken);
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(
                    "测速期间远程端点会话已切换，请重新测速。");
            }

            int? remoteDelay = remoteResponse.RootElement.TryGetProperty(
                    "delay",
                    out JsonElement remoteDelayElement)
                && remoteDelayElement.TryGetInt32(out int remoteMilliseconds)
                ? remoteMilliseconds
                : null;
            if (!await _remoteRefresh.RefreshSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "远程节点测速结果无法确认，请重试。");
            }

            return remoteDelay;
        }

        (MihomoApiClient api, long generation) = _controllerGuard.Capture();
        _controllerGuard.EnsureCommand(
            api,
            generation,
            EndpointCommand.TestDelay,
            "测速期间核心会话已切换，请重新测速。");

        using JsonDocument response = await api.TestDelayAsync(
            proxy,
            DelayTestUri,
            5000,
            cancellationToken);
        _controllerGuard.EnsureSession(api, generation, "测速期间核心会话已切换，请重新测速。");
        if (response.RootElement.TryGetProperty("delay", out JsonElement delay)
            && delay.TryGetInt32(out int milliseconds))
        {
            return milliseconds;
        }

        return null;
    }

    public Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayAsync(
        string group,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        SingleFlightOperation<IReadOnlyDictionary<string, int?>> operation = GetProxyGroupDelayOperation(group);
        return operation.RequestAsync(
            token => TestProxyGroupDelayCoreAsync(group, token),
            cancellationToken);
    }

    private SingleFlightOperation<IReadOnlyDictionary<string, int?>> GetProxyGroupDelayOperation(string group)
    {
        lock (_delayOperationGate)
        {
            if (!_proxyGroupDelayOperations.TryGetValue(group, out SingleFlightOperation<IReadOnlyDictionary<string, int?>>? operation))
            {
                if (!TrimIdleOperations(_proxyGroupDelayOperations))
                {
                    throw new OperationBusyException("代理组测速");
                }

                operation = new SingleFlightOperation<IReadOnlyDictionary<string, int?>>("代理组测速");
                _proxyGroupDelayOperations[group] = operation;
            }

            return operation;
        }
    }

    private async Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayCoreAsync(
        string group,
        CancellationToken operationCancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            _runtimeCancellation);
        CancellationToken token = linked.Token;
        ThrowIfQuiescing();
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
            EndpointCommand.TestDelay,
            "测速期间远程端点会话已切换，请重新测速。");
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            using JsonDocument remoteResponse = await remoteSession.Api.TestGroupDelayAsync(
                group,
                DelayTestUri,
                5000,
                token);
            IReadOnlyDictionary<string, int?> remoteDelays =
                MihomoDataParser.ParseGroupDelays(remoteResponse);
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(
                    "测速期间远程端点会话已切换，请重新测速。");
            }

            if (!await _remoteRefresh.RefreshSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    token)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "远程代理组测速结果无法确认，请重试。");
            }

            return remoteDelays;
        }

        (MihomoApiClient api, long generation) = _controllerGuard.Capture();
        _controllerGuard.EnsureCommand(
            api,
            generation,
            EndpointCommand.TestDelay,
            "测速期间核心会话已切换，请重新测速。");
        using JsonDocument response = await api.TestGroupDelayAsync(group, DelayTestUri, 5000, token);
        IReadOnlyDictionary<string, int?> delays = MihomoDataParser.ParseGroupDelays(response);
        await _groupDelayCommitter(api, generation, delays, token).ConfigureAwait(false);
        return delays;
    }

    private static bool TrimIdleOperations<T>(Dictionary<string, SingleFlightOperation<T>> operations)
    {
        const int MaxRetainedOperations = 256;
        if (operations.Count < MaxRetainedOperations)
        {
            return true;
        }

        string? idleKey = operations
            .FirstOrDefault(entry => !entry.Value.IsBusy)
            .Key;
        if (idleKey is not null)
        {
            operations.Remove(idleKey);
            return true;
        }

        return false;
    }

    private static bool TrimIdleLatestOperations(
        Dictionary<string, LatestWinsOperation<ProxySelectionIntent>> operations)
    {
        const int MaxRetainedOperations = 256;
        if (operations.Count < MaxRetainedOperations)
        {
            return true;
        }

        string? idleKey = operations
            .FirstOrDefault(entry => !entry.Value.IsBusy)
            .Key;
        if (idleKey is not null)
        {
            operations.Remove(idleKey);
            return true;
        }

        return false;
    }

    private void ThrowIfQuiescing()
    {
        if (_operationGate.IsQuiescing)
        {
            throw new RuntimeQuiescingException();
        }
    }
}

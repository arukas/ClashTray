using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ProxyDataResult(
    bool Succeeded,
    IReadOnlyList<ProxyGroup> Groups,
    IReadOnlyList<ProxyNode> Nodes);

internal readonly record struct TrafficDataResult(bool Succeeded, TrafficSnapshot? Value);

internal readonly record struct MemoryDataResult(bool Succeeded, long Value);

internal readonly record struct ConnectionDataResult(
    bool Succeeded,
    IReadOnlyList<ConnectionInfo> Value,
    ControllerListSummary? Summary = null);

internal readonly record struct CoreBindingEpochs(
    long LifecycleEpoch,
    long ProcessGeneration,
    long ControllerGeneration);

/// <summary>
/// Records a controller communication failure with phase, endpoint path, retry
/// count and optional measured duration. Implemented by
/// <see cref="ClashTrayRuntime"/> so the hosting mode (service vs local) stays
/// attached to the message.
/// </summary>
internal delegate void ControllerFailureLogger(
    string phase,
    string path,
    Exception exception,
    int retryCount,
    TimeSpan? elapsed);

/// <summary>
/// Owns the local controller data-ingestion pipeline: bounded per-endpoint
/// readers with snapshot fallbacks, the aggregated optional-data refresh, and
/// the scoped mode/proxy-selection refreshes applied after mutations. Every
/// commit is guarded by the core binding epochs so results from a replaced
/// core process or controller session are discarded instead of racing the new
/// binding. The polling loop and core-health confirmation stay in
/// <see cref="ClashTrayRuntime"/>.
/// </summary>
internal sealed class RuntimeDataRefreshCoordinator
{
    private readonly RuntimeStateStore _stateStore;
    private readonly ControllerSessionGuard _controllerGuard;
    private readonly SemaphoreSlim _dataRefreshLock;
    private readonly Func<CoreBindingEpochs> _captureEpochs;
    private readonly Func<MihomoApiClient, long, long, long, bool> _isCurrentCoreBinding;
    private readonly ControllerFailureLogger _failureLogger;
    private readonly Func<CancellationToken, Task> _requestThrottledPublish;
    private readonly Action _publish;

    public RuntimeDataRefreshCoordinator(
        RuntimeStateStore stateStore,
        ControllerSessionGuard controllerGuard,
        SemaphoreSlim dataRefreshLock,
        Func<CoreBindingEpochs> captureEpochs,
        Func<MihomoApiClient, long, long, long, bool> isCurrentCoreBinding,
        ControllerFailureLogger failureLogger,
        Func<CancellationToken, Task> requestThrottledPublish,
        Action publish)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(controllerGuard);
        ArgumentNullException.ThrowIfNull(dataRefreshLock);
        ArgumentNullException.ThrowIfNull(captureEpochs);
        ArgumentNullException.ThrowIfNull(isCurrentCoreBinding);
        ArgumentNullException.ThrowIfNull(failureLogger);
        ArgumentNullException.ThrowIfNull(requestThrottledPublish);
        ArgumentNullException.ThrowIfNull(publish);
        _stateStore = stateStore;
        _controllerGuard = controllerGuard;
        _dataRefreshLock = dataRefreshLock;
        _captureEpochs = captureEpochs;
        _isCurrentCoreBinding = isCurrentCoreBinding;
        _failureLogger = failureLogger;
        _requestThrottledPublish = requestThrottledPublish;
        _publish = publish;
    }

    internal async Task RefreshOptionalDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken,
        bool includeRulesAndProviders = true,
        ControllerDataDemand? demand = null)
    {
        ControllerDataDemand requested = demand ?? (includeRulesAndProviders
            ? ControllerDataDemand.All
            : ControllerDataDemand.All & ~ControllerDataDemand.RulesAndProviders);
        if (requested == ControllerDataDemand.None)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        bool includeProxies = (requested & ControllerDataDemand.Proxies) != 0;
        bool includeMetrics = (requested & ControllerDataDemand.Metrics) != 0;
        bool includeConnections = (requested & ControllerDataDemand.Connections) != 0;
        bool includeRules = (requested & ControllerDataDemand.Rules) != 0;
        bool includeProviders = (requested & ControllerDataDemand.Providers) != 0;
        CoreBindingEpochs epochs = _captureEpochs();
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentBinding(api, epochs))
            {
                return;
            }

            Task<ProxyDataResult> proxyTask = includeProxies
                ? TryGetProxyDataAsync(api, cancellationToken)
                : Task.FromResult(new ProxyDataResult(false, [], []));
            Task<TrafficDataResult> trafficTask = includeMetrics
                ? TryGetTrafficSnapshotAsync(api, cancellationToken)
                : Task.FromResult(default(TrafficDataResult));
            Task<MemoryDataResult> memoryTask = includeMetrics
                ? TryGetMemoryAsync(api, cancellationToken)
                : Task.FromResult(default(MemoryDataResult));
            Task<ConnectionDataResult> connectionsTask = includeConnections
                ? TryGetConnectionDataAsync(api, cancellationToken)
                : Task.FromResult(new ConnectionDataResult(false, []));
            Task<ControllerListData<RuleInfo>> rulesTask = includeRules
                ? TryGetRulesAsync(api, cancellationToken)
                : Task.FromResult(new ControllerListData<RuleInfo>([], null));
            Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providersTask =
                includeProviders
                    ? TryGetProvidersAsync(api, cancellationToken)
                    : Task.FromResult<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>(
                        ([], []));
            await Task.WhenAll(proxyTask, trafficTask, memoryTask, connectionsTask, rulesTask, providersTask);

            if (!IsCurrentBinding(api, epochs))
            {
                return;
            }

            ProxyDataResult proxyData = await proxyTask;
            TrafficDataResult trafficData = await trafficTask;
            MemoryDataResult memoryData = await memoryTask;
            ConnectionDataResult connectionData = await connectionsTask;
            ControllerListData<RuleInfo> rulesData = await rulesTask;
            (IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders) providerData = await providersTask;
            TrafficSnapshot? traffic = trafficData.Value;
            bool committed = _stateStore.TryUpdate(
                snapshot => snapshot.Core.State == CoreState.Running
                    && IsCurrentBinding(api, epochs),
                snapshot =>
                {
                    CoreStatus currentCore = snapshot.Core;
                    return snapshot with
                    {
                        Core = currentCore with
                        {
                            UploadBytes = traffic?.UploadBytes ?? currentCore.UploadBytes,
                            DownloadBytes = traffic?.DownloadBytes ?? currentCore.DownloadBytes,
                            UploadBytesPerSecond = traffic?.UploadBytesPerSecond ?? currentCore.UploadBytesPerSecond,
                            DownloadBytesPerSecond = traffic?.DownloadBytesPerSecond ?? currentCore.DownloadBytesPerSecond,
                            TrafficAvailable = includeMetrics ? trafficData.Succeeded : currentCore.TrafficAvailable,
                            ConnectionCount = connectionData.Succeeded
                                ? connectionData.Summary?.ReportedCount ?? connectionData.Value.Count
                                : currentCore.ConnectionCount,
                            MemoryBytes = includeMetrics ? memoryData.Value : currentCore.MemoryBytes,
                            MemoryAvailable = includeMetrics ? memoryData.Succeeded : currentCore.MemoryAvailable
                        },
                        ProxyGroups = proxyData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyGroups, proxyData.Groups, SnapshotDataComparer.ProxyGroupsEqual)
                            : snapshot.ProxyGroups,
                        ProxyNodes = proxyData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyNodes, proxyData.Nodes, SnapshotDataComparer.ProxyNodesEqual)
                            : snapshot.ProxyNodes,
                        Connections = connectionData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Connections,
                                connectionData.Value,
                                EqualityComparer<ConnectionInfo>.Default.Equals)
                            : snapshot.Connections,
                        ConnectionsSummary = connectionData.Succeeded ? connectionData.Summary : snapshot.ConnectionsSummary,
                        RulesSummary = includeRules ? rulesData.Summary : snapshot.RulesSummary,
                        Rules = includeRules
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Rules,
                                rulesData.Items,
                                EqualityComparer<RuleInfo>.Default.Equals)
                            : snapshot.Rules,
                        Providers = includeProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Providers,
                                providerData.Providers,
                                EqualityComparer<ProviderStatus>.Default.Equals)
                            : snapshot.Providers,
                        RuleProviders = includeProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.RuleProviders,
                                providerData.RuleProviders,
                                EqualityComparer<ProviderStatus>.Default.Equals)
                            : snapshot.RuleProviders
                    };
                },
                out _);
            if (committed)
            {
                await _requestThrottledPublish(cancellationToken);
            }
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    internal async Task RefreshModeSnapshotAsync(
        MihomoApiClient api,
        long generation,
        CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _controllerGuard.EnsureSession(api, generation, "模式刷新期间核心会话已切换，请重试。");
            using JsonDocument configuration = await api.GetConfigurationAsync(
                force: false,
                cancellationToken);
            _controllerGuard.EnsureSession(api, generation, "模式刷新期间核心会话已切换，请重试。");
            ProxyMode? mode = MihomoDataParser.ParseMode(configuration);
            if (mode is not ProxyMode confirmedMode)
            {
                throw new InvalidOperationException("Mihomo 未返回可识别的代理模式。");
            }

            _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { Mode = confirmedMode } });
            _publish();
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    internal async Task RefreshProxySelectionSnapshotAsync(
        MihomoApiClient api,
        long generation,
        CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _controllerGuard.EnsureSession(api, generation, "节点刷新期间核心会话已切换，请重试。");
            using JsonDocument proxies = await api.GetProxiesAsync(cancellationToken);
            _controllerGuard.EnsureSession(api, generation, "节点刷新期间核心会话已切换，请重试。");
            (IReadOnlyList<ProxyGroup> groups, IReadOnlyList<ProxyNode> nodes) = MihomoDataParser.ParseProxies(proxies);
            _stateStore.Update(snapshot => snapshot with
            {
                ProxyGroups = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyGroups, groups, SnapshotDataComparer.ProxyGroupsEqual),
                ProxyNodes = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyNodes, nodes, SnapshotDataComparer.ProxyNodesEqual)
            });
            _publish();
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    internal async Task<ProxyDataResult> TryGetProxyDataAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<(IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes)> result =
            await ReadResourceAsync("代理数据刷新", "/proxies", () => api.GetProxiesAsync(cancellationToken),
                MihomoDataParser.ParseProxies,
                () => (_stateStore.Snapshot.ProxyGroups, _stateStore.Snapshot.ProxyNodes), cancellationToken).ConfigureAwait(false);
        return new(result.Succeeded, result.Value.Groups, result.Value.Nodes);
    }

    internal async Task<TrafficDataResult> TryGetTrafficSnapshotAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<TrafficSnapshot?> result = await ReadResourceAsync<TrafficSnapshot?>(
            "指标刷新", "/traffic", () => api.GetTrafficAsync(cancellationToken),
            MihomoDataParser.ParseTraffic, static () => null, cancellationToken, measureDuration: true).ConfigureAwait(false);
        return new(result.Succeeded, result.Value);
    }

    internal async Task<MemoryDataResult> TryGetMemoryAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<long> result = await ReadResourceAsync(
            "指标刷新", "/memory", () => api.GetMemoryAsync(cancellationToken),
            MihomoDataParser.ParseMemoryBytes, () => _stateStore.Snapshot.Core.MemoryBytes,
            cancellationToken, measureDuration: true).ConfigureAwait(false);
        return new(result.Succeeded, result.Value);
    }

    internal async Task<ConnectionDataResult> TryGetConnectionDataAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<ControllerListData<ConnectionInfo>> result = await ReadResourceAsync(
            "连接数据刷新", "/connections", () => api.GetConnectionsAsync(cancellationToken),
            MihomoDataParser.ParseConnectionsWithSummary, () =>
            {
                RuntimeSnapshot previous = _stateStore.Snapshot;
                return new(previous.Connections, previous.ConnectionsSummary);
            }, cancellationToken).ConfigureAwait(false);
        return new(result.Succeeded, result.Value.Items, result.Value.Summary);
    }

    internal async Task<ControllerListData<RuleInfo>> TryGetRulesAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<ControllerListData<RuleInfo>> result = await ReadResourceAsync(
            "规则数据刷新", "/rules", () => api.GetRulesAsync(cancellationToken),
            MihomoDataParser.ParseRulesWithSummary, () =>
            {
                RuntimeSnapshot previous = _stateStore.Snapshot;
                return new(previous.Rules, previous.RulesSummary);
            }, cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    internal async Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> TryGetProvidersAsync(
        MihomoApiClient api, CancellationToken cancellationToken)
    {
        ControllerResourceRead<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> result =
            await ControllerResourceSampler.ReadProvidersAsync(api,
                () => (_stateStore.Snapshot.Providers, _stateStore.Snapshot.RuleProviders), cancellationToken,
                ControllerReadCancellationPolicy.PropagateRequested).ConfigureAwait(false);
        if (result.Failure is Exception failure) { _failureLogger("Provider 数据刷新", "/providers", failure, 0, null); }
        return result.Value;
    }

    private async Task<ControllerResourceRead<T>> ReadResourceAsync<T>(
        string phase, string path, Func<Task<JsonDocument>> request, Func<JsonDocument, T> parse,
        Func<T> fallback, CancellationToken cancellationToken, bool measureDuration = false)
    {
        ControllerResourceRead<T> result = await ControllerResourceSampler.ReadDocumentAsync(
            request, parse, fallback, cancellationToken, ControllerReadCancellationPolicy.PropagateRequested).ConfigureAwait(false);
        if (result.Failure is Exception failure)
        {
            _failureLogger(phase, path, failure, 0, measureDuration ? result.Elapsed : null);
        }
        return result;
    }

    private bool IsCurrentBinding(MihomoApiClient api, CoreBindingEpochs epochs) =>
        _isCurrentCoreBinding(
            api,
            epochs.ControllerGeneration,
            epochs.LifecycleEpoch,
            epochs.ProcessGeneration);
}

using System.Diagnostics;
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
    IReadOnlyList<ConnectionInfo> Value);

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
        bool includeRulesAndProviders = true)
    {
        CoreBindingEpochs epochs = _captureEpochs();
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentBinding(api, epochs))
            {
                return;
            }

            Task<ProxyDataResult> proxyTask = TryGetProxyDataAsync(api, cancellationToken);
            Task<TrafficDataResult> trafficTask = TryGetTrafficSnapshotAsync(api, cancellationToken);
            Task<MemoryDataResult> memoryTask = TryGetMemoryAsync(api, cancellationToken);
            Task<ConnectionDataResult> connectionsTask = TryGetConnectionDataAsync(api, cancellationToken);
            Task<IReadOnlyList<RuleInfo>> rulesTask = includeRulesAndProviders
                ? TryGetRulesAsync(api, cancellationToken)
                : Task.FromResult<IReadOnlyList<RuleInfo>>([]);
            Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providersTask =
                includeRulesAndProviders
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
            IReadOnlyList<RuleInfo> rulesData = await rulesTask;
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
                            TrafficAvailable = trafficData.Succeeded,
                            ConnectionCount = connectionData.Succeeded
                                ? connectionData.Value.Count
                                : currentCore.ConnectionCount,
                            MemoryBytes = memoryData.Value,
                            MemoryAvailable = memoryData.Succeeded
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
                        Rules = includeRulesAndProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Rules,
                                rulesData,
                                EqualityComparer<RuleInfo>.Default.Equals)
                            : snapshot.Rules,
                        Providers = includeRulesAndProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Providers,
                                providerData.Providers,
                                EqualityComparer<ProviderStatus>.Default.Equals)
                            : snapshot.Providers,
                        RuleProviders = includeRulesAndProviders
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<ProxyDataResult> TryGetProxyDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await api.GetProxiesAsync(cancellationToken);
            (IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes) data = MihomoDataParser.ParseProxies(document);
            return new ProxyDataResult(true, data.Groups, data.Nodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("代理数据刷新", "/proxies", exception, 0, null);
            return new ProxyDataResult(false, _stateStore.Snapshot.ProxyGroups, _stateStore.Snapshot.ProxyNodes);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<TrafficDataResult> TryGetTrafficSnapshotAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using JsonDocument document = await api.GetTrafficAsync(cancellationToken);
            return new TrafficDataResult(true, MihomoDataParser.ParseTraffic(document));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("指标刷新", "/traffic", exception, 0, stopwatch.Elapsed);
            return new TrafficDataResult(false, null);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<MemoryDataResult> TryGetMemoryAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using JsonDocument memory = await api.GetMemoryAsync(cancellationToken);
            return new MemoryDataResult(true, MihomoDataParser.ParseMemoryBytes(memory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("指标刷新", "/memory", exception, 0, stopwatch.Elapsed);
            return new MemoryDataResult(false, _stateStore.Snapshot.Core.MemoryBytes);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<ConnectionDataResult> TryGetConnectionDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await api.GetConnectionsAsync(cancellationToken);
            return new ConnectionDataResult(true, MihomoDataParser.ParseConnections(document));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("连接数据刷新", "/connections", exception, 0, null);
            return new ConnectionDataResult(false, _stateStore.Snapshot.Connections);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<IReadOnlyList<RuleInfo>> TryGetRulesAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await api.GetRulesAsync(cancellationToken);
            return MihomoDataParser.ParseRules(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("规则数据刷新", "/rules", exception, 0, null);
            return _stateStore.Snapshot.Rules;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "Controller read failures are downgraded to the last confirmed snapshot values and logged; they must not fault the aggregated refresh pipeline.")]
    internal async Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> TryGetProvidersAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument providers = await api.GetProvidersAsync(cancellationToken);
            using JsonDocument ruleProviders = await api.GetRuleProvidersAsync(cancellationToken);
            return (MihomoDataParser.ParseProviders(providers, "proxy"), MihomoDataParser.ParseProviders(ruleProviders, "rule"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _failureLogger("Provider 数据刷新", "/providers", exception, 0, null);
            return (_stateStore.Snapshot.Providers, _stateStore.Snapshot.RuleProviders);
        }
    }

    private bool IsCurrentBinding(MihomoApiClient api, CoreBindingEpochs epochs) =>
        _isCurrentCoreBinding(
            api,
            epochs.ControllerGeneration,
            epochs.LifecycleEpoch,
            epochs.ProcessGeneration);
}

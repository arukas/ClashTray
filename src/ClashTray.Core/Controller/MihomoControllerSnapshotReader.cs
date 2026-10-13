using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record MihomoControllerSnapshotData(
    CoreStatus Status,
    IReadOnlyList<ProxyGroup> ProxyGroups,
    IReadOnlyList<ProxyNode> ProxyNodes,
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<RuleInfo> Rules,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<ProviderStatus> RuleProviders,
    IReadOnlyList<LogEntry> Logs,
    string? ErrorMessage,
    DateTimeOffset? LastConfirmedAt = null,
    ControllerListSummary? ConnectionsSummary = null,
    ControllerListSummary? RulesSummary = null);

/// <summary>
/// Reads the bounded, read-only Controller data used by an active endpoint snapshot.
/// Each optional endpoint is isolated so one unavailable resource does not erase confirmed data.
/// </summary>
public sealed class MihomoControllerSnapshotReader
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Each optional Controller resource is isolated so a malformed or unavailable response cannot erase confirmed data.")]
    public static Task<MihomoControllerSnapshotData> ReadAsync(
        MihomoApiClient api,
        string? version,
        string logSource,
        MihomoControllerSnapshotData? previous = null,
        bool includeLogs = true,
        CancellationToken cancellationToken = default) =>
        ReadWithDemandAsync(api, version, logSource, previous, includeLogs, ControllerDataDemand.All, cancellationToken);

    internal static async Task<MihomoControllerSnapshotData> ReadWithDemandAsync(
        MihomoApiClient api,
        string? version,
        string logSource,
        MihomoControllerSnapshotData? previous,
        bool includeLogs,
        ControllerDataDemand demand,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(logSource);

        CoreStatus previousStatus = previous?.Status
            ?? new CoreStatus(
                CoreState.Running,
                version,
                ConfigurationName: null,
                ProxyMode.Rule,
                UploadBytesPerSecond: 0,
                DownloadBytesPerSecond: 0,
                UploadBytes: 0,
                DownloadBytes: 0,
                ConnectionCount: 0,
                MemoryBytes: 0,
                ErrorMessage: null);
        IReadOnlyList<ProxyGroup> previousGroups = previous?.ProxyGroups ?? [];
        IReadOnlyList<ProxyNode> previousNodes = previous?.ProxyNodes ?? [];
        IReadOnlyList<ConnectionInfo> previousConnections = previous?.Connections ?? [];
        IReadOnlyList<RuleInfo> previousRules = previous?.Rules ?? [];
        IReadOnlyList<ProviderStatus> previousProviders = previous?.Providers ?? [];
        IReadOnlyList<ProviderStatus> previousRuleProviders = previous?.RuleProviders ?? [];
        IReadOnlyList<LogEntry> previousLogs = previous?.Logs ?? [];

        Task<ControllerResourceRead<(ProxyMode? Mode, bool? Tun)>> configurationTask = ControllerResourceSampler.ReadDocumentAsync(
            () => api.GetConfigurationAsync(force: false, cancellationToken),
            document => (MihomoDataParser.ParseMode(document), MihomoDataParser.ParseTunEnabled(document)),
            () => (previousStatus.Mode, (bool?)null),
            cancellationToken);
        Task<ControllerResourceRead<(IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes)>> proxyTask = ControllerResourceSampler.ReadOptionalDocumentAsync(
            (demand & ControllerDataDemand.Proxies) != 0,
            () => api.GetProxiesAsync(cancellationToken),
            MihomoDataParser.ParseProxies,
            (previousGroups, previousNodes),
            cancellationToken);
        Task<ControllerResourceRead<TrafficSnapshot>> trafficTask = ControllerResourceSampler.ReadOptionalDocumentAsync(
            (demand & ControllerDataDemand.Metrics) != 0,
            () => api.GetTrafficAsync(cancellationToken),
            MihomoDataParser.ParseTraffic,
            new TrafficSnapshot(
                previousStatus.UploadBytes,
                previousStatus.DownloadBytes,
                previousStatus.UploadBytesPerSecond,
                previousStatus.DownloadBytesPerSecond,
                DateTimeOffset.UtcNow),
            cancellationToken);
        Task<ControllerResourceRead<long>> memoryTask = ControllerResourceSampler.ReadOptionalDocumentAsync(
            (demand & ControllerDataDemand.Metrics) != 0,
            () => api.GetMemoryAsync(cancellationToken),
            MihomoDataParser.ParseMemoryBytes,
            previousStatus.MemoryBytes,
            cancellationToken);
        Task<ControllerResourceRead<ControllerListData<ConnectionInfo>>> connectionsTask = ControllerResourceSampler.ReadOptionalDocumentAsync(
            (demand & ControllerDataDemand.Connections) != 0,
            () => api.GetConnectionsAsync(cancellationToken),
            MihomoDataParser.ParseConnectionsWithSummary,
            new ControllerListData<ConnectionInfo>(previousConnections, previous?.ConnectionsSummary),
            cancellationToken);
        Task<ControllerResourceRead<ControllerListData<RuleInfo>>> rulesTask = ControllerResourceSampler.ReadOptionalDocumentAsync(
            (demand & ControllerDataDemand.Rules) != 0,
            () => api.GetRulesAsync(cancellationToken),
            MihomoDataParser.ParseRulesWithSummary,
            new ControllerListData<RuleInfo>(previousRules, previous?.RulesSummary),
            cancellationToken);
        Task<ControllerResourceRead<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>> providersTask =
            (demand & ControllerDataDemand.Providers) != 0
                ? ControllerResourceSampler.ReadProvidersAsync(api, () => (previousProviders, previousRuleProviders), cancellationToken)
                : Task.FromResult(new ControllerResourceRead<(IReadOnlyList<ProviderStatus>, IReadOnlyList<ProviderStatus>)>(false, (previousProviders, previousRuleProviders)));
        Task<ControllerResourceRead<IReadOnlyList<LogEntry>>> logsTask = includeLogs
            ? ControllerResourceSampler.ReadDocumentAsync(
                () => api.GetLogsAsync(cancellationToken: cancellationToken),
                document => MihomoDataParser.ParseLogs(document, logSource),
                () => previousLogs,
                cancellationToken)
            : Task.FromResult(new ControllerResourceRead<IReadOnlyList<LogEntry>>(
                true,
                previousLogs,
                ErrorMessage: null));

        await Task.WhenAll(
            configurationTask,
            proxyTask,
            trafficTask,
            memoryTask,
            connectionsTask,
            rulesTask,
            providersTask,
            logsTask).ConfigureAwait(false);

        ControllerResourceRead<(ProxyMode? Mode, bool? Tun)> configuration = await configurationTask.ConfigureAwait(false);
        ControllerResourceRead<(IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes)> proxies =
            await proxyTask.ConfigureAwait(false);
        ControllerResourceRead<TrafficSnapshot> traffic = await trafficTask.ConfigureAwait(false);
        ControllerResourceRead<long> memory = await memoryTask.ConfigureAwait(false);
        ControllerResourceRead<ControllerListData<ConnectionInfo>> connections = await connectionsTask.ConfigureAwait(false);
        ControllerResourceRead<ControllerListData<RuleInfo>> rules = await rulesTask.ConfigureAwait(false);
        ControllerResourceRead<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providers =
            await providersTask.ConfigureAwait(false);
        ControllerResourceRead<IReadOnlyList<LogEntry>> logs = await logsTask.ConfigureAwait(false);

        List<string> errors = [];
        AddError(errors, "配置", configuration);
        AddError(errors, "代理", proxies);
        AddError(errors, "流量", traffic);
        AddError(errors, "内存", memory);
        AddError(errors, "连接", connections);
        AddError(errors, "规则", rules);
        AddError(errors, "Provider", providers);
        AddError(errors, "日志", logs);
        string? errorMessage = errors.Count == 0
            ? null
            : $"远程 Controller 部分数据刷新失败：{string.Join("；", errors)}";
        DateTimeOffset? lastConfirmedAt = errorMessage is null
            ? DateTimeOffset.UtcNow
            : previous?.LastConfirmedAt;

        TrafficSnapshot trafficValue = traffic.Value;
        CoreStatus status = new(
            CoreState.Running,
            version ?? previousStatus.Version,
            previousStatus.ConfigurationName,
            configuration.Value.Mode ?? previousStatus.Mode,
            trafficValue.UploadBytesPerSecond,
            trafficValue.DownloadBytesPerSecond,
            trafficValue.UploadBytes,
            trafficValue.DownloadBytes,
            connections.Succeeded ? connections.Value.Summary?.ReportedCount ?? connections.Value.Items.Count : previousStatus.ConnectionCount,
            memory.Value,
            errorMessage,
            TrafficAvailable: traffic.Succeeded || previousStatus.TrafficAvailable,
            MemoryAvailable: memory.Succeeded || previousStatus.MemoryAvailable);

        return new MihomoControllerSnapshotData(
            status,
            proxies.Succeeded ? proxies.Value.Groups : previousGroups,
            proxies.Succeeded ? proxies.Value.Nodes : previousNodes,
            connections.Value.Items,
            rules.Value.Items,
            providers.Succeeded ? providers.Value.Providers : previousProviders,
            providers.Succeeded ? providers.Value.RuleProviders : previousRuleProviders,
            logs.Succeeded ? logs.Value : previousLogs,
            errorMessage,
            lastConfirmedAt,
            connections.Value.Summary,
            rules.Value.Summary);
    }

    private static void AddError<T>(
        List<string> errors,
        string resource,
        ControllerResourceRead<T> result)
    {
        if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            errors.Add($"{resource}：{result.ErrorMessage}");
        }
    }
}

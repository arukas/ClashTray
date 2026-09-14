using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
    string? ErrorMessage);

/// <summary>
/// Reads the bounded, read-only Controller data used by an active endpoint snapshot.
/// Each optional endpoint is isolated so one unavailable resource does not erase confirmed data.
/// </summary>
public sealed class MihomoControllerSnapshotReader
{
    private sealed record ReadResult<T>(bool Succeeded, T Value, string? ErrorMessage);

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Each optional Controller resource is isolated so a malformed or unavailable response cannot erase confirmed data.")]
    public static async Task<MihomoControllerSnapshotData> ReadAsync(
        MihomoApiClient api,
        string? version,
        string logSource,
        MihomoControllerSnapshotData? previous = null,
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

        Task<ReadResult<(ProxyMode? Mode, bool? Tun)>> configurationTask = ReadDocumentAsync(
            () => api.GetConfigurationAsync(force: false, cancellationToken),
            document => (MihomoDataParser.ParseMode(document), MihomoDataParser.ParseTunEnabled(document)),
            (previousStatus.Mode, null),
            cancellationToken);
        Task<ReadResult<(IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes)>> proxyTask = ReadDocumentAsync(
            () => api.GetProxiesAsync(cancellationToken),
            MihomoDataParser.ParseProxies,
            (previousGroups, previousNodes),
            cancellationToken);
        Task<ReadResult<TrafficSnapshot>> trafficTask = ReadDocumentAsync(
            () => api.GetTrafficAsync(cancellationToken),
            MihomoDataParser.ParseTraffic,
            new TrafficSnapshot(
                previousStatus.UploadBytes,
                previousStatus.DownloadBytes,
                previousStatus.UploadBytesPerSecond,
                previousStatus.DownloadBytesPerSecond,
                DateTimeOffset.UtcNow),
            cancellationToken);
        Task<ReadResult<long>> memoryTask = ReadDocumentAsync(
            () => api.GetMemoryAsync(cancellationToken),
            MihomoDataParser.ParseMemoryBytes,
            previousStatus.MemoryBytes,
            cancellationToken);
        Task<ReadResult<IReadOnlyList<ConnectionInfo>>> connectionsTask = ReadDocumentAsync(
            () => api.GetConnectionsAsync(cancellationToken),
            MihomoDataParser.ParseConnections,
            previousConnections,
            cancellationToken);
        Task<ReadResult<IReadOnlyList<RuleInfo>>> rulesTask = ReadDocumentAsync(
            () => api.GetRulesAsync(cancellationToken),
            MihomoDataParser.ParseRules,
            previousRules,
            cancellationToken);
        Task<ReadResult<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>> providersTask =
            ReadDocumentAsync(
                async () =>
                {
                    using JsonDocument proxyProviders = await api.GetProvidersAsync(cancellationToken);
                    using JsonDocument ruleProviders = await api.GetRuleProvidersAsync(cancellationToken);
                    using JsonDocument combined = JsonDocument.Parse(
                        $$"""{"proxyProviders":{{proxyProviders.RootElement.GetRawText()}},"ruleProviders":{{ruleProviders.RootElement.GetRawText()}}}""");
                    return combined;
                },
                document =>
                {
                    JsonElement proxyProviders = document.RootElement.GetProperty("proxyProviders");
                    JsonElement ruleProviders = document.RootElement.GetProperty("ruleProviders");
                    using JsonDocument proxyDocument = JsonDocument.Parse(proxyProviders.GetRawText());
                    using JsonDocument ruleDocument = JsonDocument.Parse(ruleProviders.GetRawText());
                    return (
                        MihomoDataParser.ParseProviders(proxyDocument, "proxy"),
                        MihomoDataParser.ParseProviders(ruleDocument, "rule"));
                },
                (previousProviders, previousRuleProviders),
                cancellationToken);
        Task<ReadResult<IReadOnlyList<LogEntry>>> logsTask = ReadDocumentAsync(
            () => api.GetLogsAsync(cancellationToken: cancellationToken),
            document => MihomoDataParser.ParseLogs(document, logSource),
            previousLogs,
            cancellationToken);

        await Task.WhenAll(
            configurationTask,
            proxyTask,
            trafficTask,
            memoryTask,
            connectionsTask,
            rulesTask,
            providersTask,
            logsTask).ConfigureAwait(false);

        ReadResult<(ProxyMode? Mode, bool? Tun)> configuration = await configurationTask.ConfigureAwait(false);
        ReadResult<(IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes)> proxies =
            await proxyTask.ConfigureAwait(false);
        ReadResult<TrafficSnapshot> traffic = await trafficTask.ConfigureAwait(false);
        ReadResult<long> memory = await memoryTask.ConfigureAwait(false);
        ReadResult<IReadOnlyList<ConnectionInfo>> connections = await connectionsTask.ConfigureAwait(false);
        ReadResult<IReadOnlyList<RuleInfo>> rules = await rulesTask.ConfigureAwait(false);
        ReadResult<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providers =
            await providersTask.ConfigureAwait(false);
        ReadResult<IReadOnlyList<LogEntry>> logs = await logsTask.ConfigureAwait(false);

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
            connections.Succeeded ? connections.Value.Count : previousStatus.ConnectionCount,
            memory.Value,
            errorMessage,
            TrafficAvailable: traffic.Succeeded || previousStatus.TrafficAvailable,
            MemoryAvailable: memory.Succeeded || previousStatus.MemoryAvailable);

        return new MihomoControllerSnapshotData(
            status,
            proxies.Succeeded ? proxies.Value.Groups : previousGroups,
            proxies.Succeeded ? proxies.Value.Nodes : previousNodes,
            connections.Succeeded ? connections.Value : previousConnections,
            rules.Succeeded ? rules.Value : previousRules,
            providers.Succeeded ? providers.Value.Providers : previousProviders,
            providers.Succeeded ? providers.Value.RuleProviders : previousRuleProviders,
            logs.Succeeded ? logs.Value : previousLogs,
            errorMessage);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This helper converts each non-cancellation resource failure into a bounded read result.")]
    private static async Task<ReadResult<T>> ReadDocumentAsync<T>(
        Func<Task<JsonDocument>> request,
        Func<JsonDocument, T> parse,
        T fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = await request().ConfigureAwait(false);
            return new ReadResult<T>(true, parse(document), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ReadResult<T>(
                false,
                fallback,
                ErrorSanitizer.Sanitize(exception));
        }
    }

    private static void AddError<T>(
        List<string> errors,
        string resource,
        ReadResult<T> result)
    {
        if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            errors.Add($"{resource}：{result.ErrorMessage}");
        }
    }
}

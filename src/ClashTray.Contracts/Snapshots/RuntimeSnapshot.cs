namespace ClashTray.Contracts;

public sealed record RuntimeSnapshot(
    CoreStatus Core,
    SystemProxyState SystemProxy,
    TunState Tun,
    SubscriptionState Subscription,
    IReadOnlyList<ConfigurationProfile> Configurations,
    IReadOnlyList<ProxyGroup> ProxyGroups,
    IReadOnlyList<ProxyNode> ProxyNodes,
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<RuleInfo> Rules,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<ProviderStatus> RuleProviders,
    IReadOnlyList<LogEntry> Logs,
    string? ErrorMessage,
    NetworkSwitchStatus? NetworkSwitch = null,
    ControllerListSummary? ConnectionsSummary = null,
    ControllerListSummary? RulesSummary = null);

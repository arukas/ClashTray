namespace ClashTray.Contracts;

public enum CoreState
{
    Missing,
    Stopped,
    Validating,
    Starting,
    Running,
    Stopping,
    Restarting,
    Failed
}

public enum SystemProxyState
{
    Off,
    Enabling,
    On,
    Disabling,
    RestoreRequired,
    Failed
}

public enum TunState
{
    Unavailable,
    Unknown,
    Off,
    Enabling,
    On,
    Disabling,
    Failed
}

public enum SubscriptionState
{
    Idle,
    Downloading,
    Validating,
    Applying,
    Succeeded,
    Failed
}

public enum ProxyMode
{
    Rule,
    Global,
    Direct
}

public enum TrayState
{
    Stopped,
    Connecting,
    Running,
    SystemProxy,
    Tun,
    Paused,
    Error
}

public sealed record CoreStatus(
    CoreState State,
    string? Version,
    string? ConfigurationName,
    ProxyMode Mode,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    long UploadBytes,
    long DownloadBytes,
    int ConnectionCount,
    long MemoryBytes,
    string? ErrorMessage,
    bool TrafficAvailable = false,
    bool MemoryAvailable = false);

public sealed record ConfigurationProfile(
    string Id,
    string Name,
    string Path,
    Uri? SubscriptionUri,
    DateTimeOffset? LastRefreshed,
    bool IsActive);

public sealed record ProxyNode(
    string Name,
    string Type,
    string? Delay,
    bool IsCurrent,
    IReadOnlyList<string> Providers);

public sealed record ProxyGroup(
    string Name,
    string Type,
    string? Current,
    IReadOnlyList<string> Members,
    string? Delay = null);

public sealed record TrafficSnapshot(
    long UploadBytes,
    long DownloadBytes,
    double UploadBytesPerSecond,
    double DownloadBytesPerSecond,
    DateTimeOffset Timestamp);

public sealed record ConnectionInfo(
    string Id,
    string Network,
    string Source,
    string Destination,
    string Rule,
    string Chain,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset StartTime,
    string RulePayload = "-");

public sealed record RuleInfo(string Type, string Payload, string Proxy, int Size);

public sealed record ProviderStatus(
    string Name,
    string Type,
    string VehicleType,
    DateTimeOffset? UpdatedAt,
    string? Error,
    int Count);

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Source,
    string Level,
    string Message,
    int RepeatCount = 1);

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
    string? ErrorMessage);

public sealed record AppSettings(
    string? ActiveConfigurationId = null,
    bool StartWithWindows = false,
    bool StartCoreAutomatically = false,
    int HttpPort = 7892,
    int SocksPort = 7891,
    int MixedPort = 7890,
    int ControllerPort = 9090,
    bool AllowLan = false,
    bool Ipv6 = false,
    bool TcpConcurrent = false,
    string LogLevel = "info",
    string BypassList = "localhost;127.*;192.168.*;10.*;172.16.*;<local>",
    int SubscriptionRefreshHours = 24,
    string Theme = "system",
    bool SystemProxyEnabled = false,
    bool TunEnabled = false,
    bool NakhimovUnlocked = false);

public enum ServiceCommand
{
    GetStatus,
    StartCore,
    StopCore,
    RestartCore,
    InstallCore,
    RollbackCore,
    EnableTun,
    DisableTun
}

public sealed record ServiceRequest(Guid RequestId, ServiceCommand Command, string? Payload = null);

public sealed record ServiceResponse(
    Guid RequestId,
    bool Succeeded,
    TunState Tun,
    string? Payload = null,
    string? Error = null,
    CoreState Core = CoreState.Stopped);

public sealed record ServiceCorePayload(
    string ConfigurationPath,
    string WorkingDirectory,
    int ControllerPort,
    string ControllerSecret);

public sealed record ServiceCoreUpdatePayload(
    string Version,
    Uri DownloadUri,
    string Sha256);

public sealed record ServiceTunPayload(
    int ControllerPort,
    string ControllerSecret,
    bool Enabled);

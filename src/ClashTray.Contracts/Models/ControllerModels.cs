namespace ClashTray.Contracts;

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
    int RepeatCount = 1,
    long Sequence = 0);

public sealed record ControllerListSummary(int ReportedCount, bool IsTruncated);

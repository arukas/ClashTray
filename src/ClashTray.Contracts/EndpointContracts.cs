namespace ClashTray.Contracts;

public enum EndpointKind
{
    Local,
    Remote
}

public enum EndpointSessionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    AuthenticationFailed,
    CertificateFailed,
    Incompatible,
    Failed
}

public enum EndpointTransportSecurity
{
    Loopback,
    HttpsSystemTrust,
    HttpsCustomCertificate,
    HttpExplicitlyConfirmed
}

public enum ErrorCode
{
    None = 0,
    EndpointCommandDenied = 1000,
    EndpointCapabilityUnavailable = 1001,
    UnsupportedEndpointCommand = 1002,
    ConfigurationSwitchJournalCorrupt = 1100,
    ConfigurationSwitchTargetNotFound = 1101,
    ConfigurationSwitchFailed = 1102,
    ConfigurationSwitchRollbackFailed = 1103,
    ConfigurationSwitchRecoveryRequired = 1104
}

[Flags]
public enum EndpointCapability
{
    None = 0,
    ObserveStatus = 1 << 0,
    ObserveProxies = 1 << 1,
    ObserveProviders = 1 << 2,
    ObserveRules = 1 << 3,
    ObserveConnections = 1 << 4,
    ObserveLogs = 1 << 5,
    SwitchMode = 1 << 6,
    SwitchProxy = 1 << 7,
    TestDelay = 1 << 8,
    RefreshProvider = 1 << 9,
    CloseConnection = 1 << 10,
    ClearCache = 1 << 11,
    UpdateGeo = 1 << 12,
    ManageLocalConfiguration = 1 << 13,
    ControlLocalCore = 1 << 14,
    ControlSystemProxy = 1 << 15,
    ControlTun = 1 << 16,
    UpdateLocalCore = 1 << 17,
    OpenLocalDashboard = 1 << 18
}

public static class EndpointCapabilityDefaults
{
    public const EndpointCapability ControllerRead =
        EndpointCapability.ObserveStatus
        | EndpointCapability.ObserveProxies
        | EndpointCapability.ObserveProviders
        | EndpointCapability.ObserveRules
        | EndpointCapability.ObserveConnections
        | EndpointCapability.ObserveLogs;

    public const EndpointCapability ControllerOperations =
        EndpointCapability.SwitchMode
        | EndpointCapability.SwitchProxy
        | EndpointCapability.TestDelay
        | EndpointCapability.RefreshProvider
        | EndpointCapability.CloseConnection
        | EndpointCapability.ClearCache
        | EndpointCapability.UpdateGeo;

    public const EndpointCapability LocalOnly =
        EndpointCapability.ManageLocalConfiguration
        | EndpointCapability.ControlLocalCore
        | EndpointCapability.ControlSystemProxy
        | EndpointCapability.ControlTun
        | EndpointCapability.UpdateLocalCore
        | EndpointCapability.OpenLocalDashboard;

    public const EndpointCapability Local =
        ControllerRead | ControllerOperations | LocalOnly;

    public const EndpointCapability Remote =
        ControllerRead | ControllerOperations;
}

public readonly record struct EndpointId(string Value)
{
    public static EndpointId Local { get; } = new("local");

    public override string ToString() => Value;
}

public sealed record EndpointDescriptor(
    EndpointId Id,
    EndpointKind Kind,
    string DisplayName,
    Uri BaseUri,
    EndpointTransportSecurity Security,
    bool IsEnabled = true);

public sealed record LocalDeviceSnapshot(
    CoreState CoreState,
    string? CoreVersion,
    string? ActiveConfigurationId,
    string? ActiveConfigurationName,
    IReadOnlyList<ConfigurationProfile> Configurations,
    SystemProxyState SystemProxy,
    TunState Tun,
    SubscriptionState Subscription,
    bool DesiredSystemProxyEnabled,
    bool DesiredTunEnabled,
    string? ErrorMessage,
    NetworkSwitchStatus? NetworkSwitch = null);

public sealed record ControllerSessionSnapshot(
    EndpointDescriptor Endpoint,
    EndpointSessionState State,
    long Generation,
    DateTimeOffset? LastConfirmedAt,
    CoreStatus? Status,
    IReadOnlyList<ProxyGroup> ProxyGroups,
    IReadOnlyList<ProxyNode> ProxyNodes,
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<RuleInfo> Rules,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<ProviderStatus> RuleProviders,
    IReadOnlyList<LogEntry> Logs,
    EndpointCapability Capabilities,
    string? ErrorMessage);

public sealed record AppSnapshot(
    LocalDeviceSnapshot LocalDevice,
    ControllerSessionSnapshot ActiveController,
    IReadOnlyList<EndpointDescriptor> Endpoints,
    string Language,
    string Theme,
    string? ErrorMessage);

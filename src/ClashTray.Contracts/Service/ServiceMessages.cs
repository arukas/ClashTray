namespace ClashTray.Contracts;

public sealed record ServiceResponse(
    Guid RequestId,
    bool Succeeded,
    TunState Tun,
    string? Payload = null,
    string? Error = null,
    CoreState Core = CoreState.Stopped,
    ServiceErrorCode ErrorCode = ServiceErrorCode.None,
    ServiceDispatchState DispatchState = ServiceDispatchState.Completed,
    int ProtocolVersion = 0,
    CoreRuntimeBinding? RuntimeBinding = null,
    Guid ServiceInstanceId = default);

public sealed record ServiceCorePayload(
    string ConfigurationPath,
    string WorkingDirectory,
    int ControllerPort,
    string ControllerSecret,
    ControllerPortConflictPolicy ControllerPortConflictPolicy = ControllerPortConflictPolicy.AutomaticFallback,
    bool UseAvailableControllerPortOnce = false,
    int HttpPort = 0,
    int SocksPort = 0,
    int MixedPort = 0,
    bool AllowLan = false,
    bool Ipv6 = false);

public sealed record ServiceCoreUpdatePayload(
    string Version,
    Uri DownloadUri,
    string Sha256);

public sealed record ServiceTunPayload(
    int ControllerPort,
    string ControllerSecret,
    bool Enabled,
    Guid InstanceId = default,
    Guid OwnerInstanceId = default);

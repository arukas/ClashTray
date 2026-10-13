namespace ClashTray.Contracts;

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

public static class ServiceProtocol
{
    public const int CurrentVersion = 3;
    public const int MaximumRequestCharacters = 64 * 1024;
    public const int MaximumResponseCharacters = 256 * 1024;
}

public enum ServiceDispatchState
{
    NotDispatched,
    DispatchedAwaitingResult,
    Completed
}

public sealed record ServiceRequest(
    Guid RequestId,
    ServiceCommand Command,
    string? Payload = null,
    // 0 means the peer did not state a version (legacy payload); both sides
    // must send ServiceProtocol.CurrentVersion explicitly so a version skew
    // can never masquerade as the current protocol.
    int ProtocolVersion = 0,
    bool RecoveryOnly = false,
    Guid ExpectedServiceInstanceId = default);

public enum ServiceErrorCode
{
    None,
    OperationBusy,
    TunConfigurationMissingAddress,
    TunMissingInterfaceAddress,
    TunStateUnknown,
    ControllerPortConflict,
    ProxyPortConflict,
    ControllerOwnershipUnconfirmed,
    CoreReadinessFailed,
    ControllerCandidatesExhausted,
    InvalidConfiguration,
    OperationTimedOut,
    OperationCancelled,
    RuntimeBindingMetadataMissing,
    RuntimeBindingInvalid,
    RequestResultUnavailable,
    RequestIdentityConflict
}

using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Projects endpoint session state into the controller portion of the UI snapshot.
/// It does not perform any controller I/O.
/// </summary>
public static class EndpointSessionSnapshotFactory
{
    public static ControllerSessionSnapshot Create(
        EndpointDescriptor endpoint,
        EndpointSessionStatusEventArgs sessionStatus,
        EndpointHandshakeResult? handshake = null,
        CoreStatus? status = null,
        IReadOnlyList<ProxyGroup>? proxyGroups = null,
        IReadOnlyList<ProxyNode>? proxyNodes = null,
        IReadOnlyList<ConnectionInfo>? connections = null,
        IReadOnlyList<RuleInfo>? rules = null,
        IReadOnlyList<ProviderStatus>? providers = null,
        IReadOnlyList<ProviderStatus>? ruleProviders = null,
        IReadOnlyList<LogEntry>? logs = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(sessionStatus);
        EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
        if (endpoint.Id != sessionStatus.Endpoint.Id)
        {
            throw new ArgumentException(
                "Session status belongs to a different endpoint.",
                nameof(sessionStatus));
        }

        EndpointSessionState state = sessionStatus.State;
        if (handshake is { IsCompatible: false })
        {
            state = handshake.State;
        }

        EndpointCapability capabilities = state == EndpointSessionState.Connected
            ? handshake?.Capabilities ?? EndpointCapability.None
            : EndpointCapability.None;
        if (endpoint.Kind == EndpointKind.Remote)
        {
            capabilities &= EndpointCapabilityDefaults.Remote;
        }

        CoreStatus? effectiveStatus = status;
        if (handshake?.Version is not null && status is not null)
        {
            effectiveStatus = status with { Version = handshake.Version };
        }

        string? error = sessionStatus.ErrorMessage ?? handshake?.ErrorMessage;
        ErrorCode errorCode = sessionStatus.ErrorCode;
        if (errorCode == ErrorCode.None && handshake is { IsCompatible: false })
        {
            errorCode = handshake.State switch
            {
                EndpointSessionState.AuthenticationFailed => ErrorCode.EndpointAuthenticationFailed,
                EndpointSessionState.CertificateFailed => ErrorCode.EndpointCertificateFailed,
                EndpointSessionState.Incompatible => ErrorCode.EndpointIncompatible,
                _ => ErrorCode.EndpointTransportFailed
            };
        }

        return new ControllerSessionSnapshot(
            endpoint,
            state,
            sessionStatus.Generation,
            sessionStatus.LastConfirmedAt,
            effectiveStatus,
            proxyGroups ?? [],
            proxyNodes ?? [],
            connections ?? [],
            rules ?? [],
            providers ?? [],
            ruleProviders ?? [],
            logs ?? [],
            capabilities,
            error,
            errorCode);
    }
}

using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed class LocalDeviceCoordinator
{
    private readonly IServicePipeClient _servicePipeClient;
    private readonly ISystemProxyController _systemProxy;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public LocalDeviceCoordinator(
        EndpointKind endpointKind,
        IServicePipeClient servicePipeClient,
        ISystemProxyController systemProxy)
    {
        ArgumentNullException.ThrowIfNull(servicePipeClient);
        ArgumentNullException.ThrowIfNull(systemProxy);
        EndpointCommandPolicy.EnsureAllowed(
            endpointKind,
            EndpointCapabilityDefaults.Local,
            EndpointCommand.ControlLocalCore);

        _servicePipeClient = servicePipeClient;
        _systemProxy = systemProxy;
    }

    public SystemProxyState SystemProxyState => _systemProxy.State;

    public SystemProxyState DetectSystemProxyState() => _systemProxy.DetectState();

    public Task EnableSystemProxyAsync(
        int port,
        string bypassList,
        CancellationToken cancellationToken = default) =>
        _systemProxy.EnableAsync(port, bypassList, cancellationToken);

    public Task DisableSystemProxyAsync(CancellationToken cancellationToken = default) =>
        _systemProxy.DisableAsync(cancellationToken);

    public Task<ServiceResponse> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.GetStatus, null, cancellationToken);

    public Task<ServiceResponse> StartCoreAsync(
        ServiceCorePayload payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.StartCore, Serialize(payload), cancellationToken);

    public Task<ServiceResponse> StopCoreAsync(CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.StopCore, null, cancellationToken);

    public Task<ServiceResponse> RestartCoreAsync(
        ServiceCorePayload payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.RestartCore, Serialize(payload), cancellationToken);

    public Task<ServiceResponse> InstallCoreAsync(
        ServiceCoreUpdatePayload payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.InstallCore, Serialize(payload), cancellationToken);

    public Task<ServiceResponse> RollbackCoreAsync(CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.RollbackCore, null, cancellationToken);

    public Task<ServiceResponse> EnableTunAsync(
        ServiceTunPayload payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.EnableTun, Serialize(payload), cancellationToken);

    public Task<ServiceResponse> DisableTunAsync(
        ServiceTunPayload payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(ServiceCommand.DisableTun, Serialize(payload), cancellationToken);

    public Task<ServiceResponse> SetTunAsync(
        ServiceTunPayload payload,
        CancellationToken cancellationToken = default) =>
        payload.Enabled
            ? EnableTunAsync(payload, cancellationToken)
            : DisableTunAsync(payload, cancellationToken);

    private Task<ServiceResponse> SendAsync(
        ServiceCommand command,
        string? payload,
        CancellationToken cancellationToken) =>
        _servicePipeClient.SendAsync(command, payload, cancellationToken);

    private string Serialize<T>(T payload) =>
        JsonSerializer.Serialize(payload, _jsonOptions);
}

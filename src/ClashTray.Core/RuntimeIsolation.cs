using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed class IsolatedSystemProxyController : ISystemProxyController
{
    public SystemProxyState State { get; private set; }
    public SystemProxyState DetectState() => State;
    public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = SystemProxyState.On;
        return Task.CompletedTask;
    }
    public Task DisableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = SystemProxyState.Off;
        return Task.CompletedTask;
    }
}

internal sealed class IsolatedServicePipeClient : IServicePipeClient
{
    public Task<ServiceResponse> SendAsync(
        ServiceCommand command,
        string? payload = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ServiceResponse>(
            new ServiceUnavailableException(
                "ClashTray service access is disabled for an isolated runtime.",
                new InvalidOperationException("The runtime was created with custom paths.")));
}

using ClashTray.Contracts;

namespace ClashTray.Core;

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

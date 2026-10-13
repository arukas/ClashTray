using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface ISystemProxyController
{
    public SystemProxyState State { get; }

    public SystemProxyState DetectState();

    public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default);

    public Task DisableAsync(CancellationToken cancellationToken = default);
}

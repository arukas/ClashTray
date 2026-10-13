using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public interface INetworkContextSource : IAsyncDisposable
{
    [SuppressMessage(
        "Design",
        "CA1003:Use generic event handler instances",
        Justification = "The immutable network snapshot is the event payload and is intentionally not modeled as mutable EventArgs.")]
    public event EventHandler<NetworkContextSnapshot>? ContextChanged;

    public Task<NetworkContextSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);
}

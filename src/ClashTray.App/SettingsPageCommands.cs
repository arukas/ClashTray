using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

internal sealed record PageCommandResult(bool Admitted, bool Succeeded, string? Error, PageCommandRunner? PresentationOwner = null, long PageGeneration = 0)
{
    // The UI continuation may be queued behind navigation after the operation
    // completes. Validate when presentation occurs, not just at completion.
    public bool CanPresent => PresentationOwner?.CanPresent(PageGeneration) == true;
}

internal sealed class PageCommandRunner
{
    private int _busy;
    private long _pageGeneration;
    private bool _active = true;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool CanPresent(long generation) => Volatile.Read(ref _active) && generation == Volatile.Read(ref _pageGeneration);
    public void Activate() { Interlocked.Increment(ref _pageGeneration); Volatile.Write(ref _active, true); }
    public void Deactivate() { Volatile.Write(ref _active, false); Interlocked.Increment(ref _pageGeneration); }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The awaitable page boundary returns sanitized failures; leaving a page only suppresses presentation, not already admitted side effects.")]
    public async Task<PageCommandResult> RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { return new(false, false, null); }
        long generation = Volatile.Read(ref _pageGeneration);
        try
        {
            await operation().ConfigureAwait(false);
            return new(true, true, null, this, generation);
        }
        catch (Exception exception)
        { return new(true, false, ErrorSanitizer.Sanitize(exception), this, generation); }
        finally { Volatile.Write(ref _busy, 0); }
    }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by SettingsPage in the App; linked into Core.Tests to test the command boundary without WinUI.")]
internal sealed class SettingsPageCommands(ClashTrayRuntime runtime)
{
    public PageCommandRunner Settings { get; } = new();
    public PageCommandRunner Network { get; } = new();
    public PageCommandRunner Maintenance { get; } = new();
    public PageCommandRunner Endpoints { get; } = new();
    public void Activate() { Settings.Activate(); Network.Activate(); Maintenance.Activate(); Endpoints.Activate(); }
    public void Deactivate() { Settings.Deactivate(); Network.Deactivate(); Maintenance.Deactivate(); Endpoints.Deactivate(); }
    public Task<PageCommandResult> SaveAsync(AppSettingsPatch patch) => Settings.RunAsync(() => runtime.UpdateSettingsAsync(patch, reconcileStartup: true));
    public Task<PageCommandResult> SetSystemProxyAsync(bool enabled) => Network.RunAsync(() => runtime.SetSystemProxyAsync(enabled));
    public Task<PageCommandResult> SetTunAsync(bool enabled) => Network.RunAsync(() => runtime.SetTunAsync(enabled));
    public Task<PageCommandResult> ClearDnsAsync(EndpointCommandTarget? target) => Maintenance.RunAsync(() => runtime.ClearDnsCacheAsync(target));
    public Task<PageCommandResult> ClearFakeIpAsync(EndpointCommandTarget? target) => Maintenance.RunAsync(() => runtime.ClearFakeIpCacheAsync(target));
    public Task<PageCommandResult> UpdateGeoAsync(EndpointCommandTarget? target) => Maintenance.RunAsync(() => runtime.UpdateGeoAsync(target));
    public Task<PageCommandResult> RefreshProviderAsync(string name, bool ruleProvider, EndpointCommandTarget? target) =>
        Maintenance.RunAsync(() => runtime.RefreshProviderAsync(name, ruleProvider, target));
    public Task<PageCommandResult> InstallCoreAsync(CoreUpdateManifest manifest) => Maintenance.RunAsync(() => runtime.InstallCoreUpdateAsync(manifest));
}

using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface ICoreOwnershipObservationSource
{
    public MihomoListenerPorts ReadControllerPorts(JsonDocument configuration, CoreRuntimeBinding binding);
    public bool IsProcessCurrent(LocalCoreProcessIdentity identity);
    public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners);
}

internal sealed class WindowsCoreOwnershipObservationSource : ICoreOwnershipObservationSource
{
    public MihomoListenerPorts ReadControllerPorts(JsonDocument configuration, CoreRuntimeBinding binding) => MihomoDataParser.ParseListenerPorts(configuration);
    public bool IsProcessCurrent(LocalCoreProcessIdentity identity) => WindowsListenerOwnerTable.IsCurrentProcessIdentity(identity);
    public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners) =>
        WindowsListenerOwnerTable.CreateObservation(identity, listeners).Inspect;
}

internal sealed record CoreHealthObservation(string? Version, ProxyMode? Mode, bool? TunEnabled);
internal sealed record CoreLaunchRequest(string ExecutablePath, ServiceCorePayload Payload, string SafePaths);
internal sealed record CoreLaunchResult(bool Succeeded, ServiceResponse? ServiceResponse = null);

internal interface ICoreLifecycleExecutor
{
    public Task<CoreLaunchResult> StartAsync(CoreLaunchRequest request, CancellationToken cancellationToken);
    public Task<ServiceResponse?> StopAsync(CancellationToken cancellationToken);
}

internal sealed class LocalCoreLifecycleExecutor(MihomoProcessManager process) : ICoreLifecycleExecutor
{
    public async Task<CoreLaunchResult> StartAsync(CoreLaunchRequest request, CancellationToken cancellationToken)
    {
        ServiceCorePayload payload = request.Payload;
        if (!await process.ValidateAsync(request.ExecutablePath, payload.ConfigurationPath, payload.WorkingDirectory,
            safePaths: request.SafePaths, cancellationToken: cancellationToken).ConfigureAwait(false))
        { return new CoreLaunchResult(false); }
        await process.StartAsync(request.ExecutablePath, payload.ConfigurationPath, payload.WorkingDirectory,
            safePaths: request.SafePaths, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new CoreLaunchResult(true);
    }
    public async Task<ServiceResponse?> StopAsync(CancellationToken cancellationToken)
    {
        await process.StopAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }
}

internal sealed class ServiceCoreLifecycleExecutor(LocalDeviceCoordinator device) : ICoreLifecycleExecutor
{
    // Only the allow-listed service payload crosses IPC. Executable paths and
    // desktop ownership observations never become service security facts.
    public async Task<CoreLaunchResult> StartAsync(CoreLaunchRequest request, CancellationToken cancellationToken)
    {
        ServiceResponse response = await device.StartCoreAsync(request.Payload, cancellationToken).ConfigureAwait(false);
        return new CoreLaunchResult(response.Succeeded, response);
    }
    public async Task<ServiceResponse?> StopAsync(CancellationToken cancellationToken) =>
        await device.StopCoreAsync(cancellationToken).ConfigureAwait(false);
}

// Owns local process resources, hosting identity, binding and health evidence.
// Runtime retains operation admission, preferences, recovery and UI projection.
// No reference or callbacks to ClashTrayRuntime are held by this module.
internal sealed class CoreLifecycleCoordinator : IAsyncDisposable
{
    private ICoreOwnershipObservationSource _observations;
    private CoreRuntimeBinding? _binding;
    private HealthStamp? _health;
    private long _epoch;
    private sealed record HealthStamp(long Epoch, long ProcessGeneration, long ControllerGeneration, CoreRuntimeBinding? Binding);

    public CoreLifecycleCoordinator(LocalDeviceCoordinator device, ICoreOwnershipObservationSource observations)
    {
        _observations = observations;
        LocalExecutor = new LocalCoreLifecycleExecutor(Process);
        ServiceExecutor = new ServiceCoreLifecycleExecutor(device);
    }
    public MihomoProcessManager Process { get; } = new();
    public ICoreLifecycleExecutor LocalExecutor { get; }
    public ICoreLifecycleExecutor ServiceExecutor { get; }
    public bool UsingServiceCore { get; set; }
    public long Epoch => Volatile.Read(ref _epoch);
    public CoreRuntimeBinding? Binding => Volatile.Read(ref _binding);
    public long BeginTransition() => Interlocked.Increment(ref _epoch);
    public void StoreBinding(CoreRuntimeBinding? binding) => Interlocked.Exchange(ref _binding, binding);
    public void SetObservationSource(ICoreOwnershipObservationSource observations) => Volatile.Write(ref _observations, observations);
    public bool IsProcessCurrent(LocalCoreProcessIdentity identity) => _observations.IsProcessCurrent(identity);
    public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners) =>
        _observations.CreateListenerSnapshot(identity, listeners);

    public async Task<CoreHealthObservation> ObserveHealthAsync(MihomoApiClient api, CoreRuntimeBinding binding, CancellationToken cancellationToken)
    {
        using JsonDocument version = await api.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await api.GetConfigurationAsync(false, cancellationToken).ConfigureAwait(false);
        RuntimeBindingValidationResult validation = RuntimeBindingValidator.Validate(binding);
        if (!validation.IsValid) { throw RuntimeBindingValidator.CreateAdmissionException(validation); }
        MihomoListenerPorts ports = _observations.ReadControllerPorts(configuration, binding);
        LocalCoreProcessIdentity identity = new(binding.ProcessId, binding.ProcessStartedUtcTicks, binding.ExecutablePath);
        Func<LocalPortBinding, ListenerOwnerObservation> inspect = CreateListenerSnapshot(identity, validation.Listeners);
        if (!binding.ControllerReady || ports.Http != binding.HttpPort || ports.Socks != binding.SocksPort || ports.Mixed != binding.MixedPort
            || !binding.HttpReady || !binding.SocksReady || binding.MixedReady && binding.MixedPort <= 0
            || !RuntimeBindingValidator.AreListenersOwned(binding, inspect) || !IsProcessCurrent(identity))
        { throw new ManagedCoreOwnershipException("当前 Mihomo 有效监听或进程所有权与运行绑定不一致。"); }
        return new CoreHealthObservation(MihomoDataParser.ParseVersion(version), MihomoDataParser.ParseMode(configuration), MihomoDataParser.ParseTunEnabled(configuration));
    }

    public bool IsHealthConfirmed(long controllerGeneration, bool hasController)
    {
        HealthStamp? stamp = Volatile.Read(ref _health);
        return hasController && stamp is not null && stamp.Epoch == Epoch && stamp.ProcessGeneration == Process.Generation
            && stamp.ControllerGeneration == controllerGeneration && ReferenceEquals(stamp.Binding, Binding);
    }
    public void ConfirmHealth(long epoch, long processGeneration, long controllerGeneration, CoreRuntimeBinding? observedBinding) =>
        Volatile.Write(ref _health, new HealthStamp(epoch, processGeneration, controllerGeneration, observedBinding));
    public void InvalidateHealth() => Volatile.Write(ref _health, null);
    public ValueTask DisposeAsync() => Process.DisposeAsync();
}

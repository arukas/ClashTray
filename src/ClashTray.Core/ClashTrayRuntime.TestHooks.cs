using System.Diagnostics;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

// Compatibility adapter for existing controller fixtures: missing port fields
// are supplied as observation facts, while the same production admission,
// ownership and generation predicates still execute.
internal sealed class FixtureCoreOwnershipObservationSource : ICoreOwnershipObservationSource
{
    public MihomoListenerPorts ReadControllerPorts(JsonDocument configuration, CoreRuntimeBinding binding)
    {
        MihomoListenerPorts ports = MihomoDataParser.ParseListenerPorts(configuration);
        return new(ports.Http ?? binding.HttpPort, ports.Socks ?? binding.SocksPort, ports.Mixed ?? binding.MixedPort);
    }
    public bool IsProcessCurrent(LocalCoreProcessIdentity identity) => true;
    public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners) =>
        _ => new ListenerOwnerObservation(ListenerOwnerState.Owned);
}

public sealed partial class ClashTrayRuntime
{

    internal void AttachControllerForTesting(MihomoApiClient api, bool usingServiceCore)
    {
        ArgumentNullException.ThrowIfNull(api);
        _coreLifecycle.SetObservationSource(new FixtureCoreOwnershipObservationSource());
        using Process process = Process.GetCurrentProcess();
        int controllerPort = api.ControllerUri.Port;
        SetRuntimeBinding(new CoreRuntimeBinding(
            _settings.ControllerPort,
            controllerPort,
            Guid.NewGuid(),
            Guid.NewGuid(),
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            Math.Max(1, _processManager.Generation),
            _settings.HttpPort,
            _settings.SocksPort,
            _settings.MixedPort,
            ControllerReady: true,
            HttpReady: _settings.HttpPort > 0,
            SocksReady: _settings.SocksPort > 0,
            MixedReady: _settings.MixedPort > 0,
            Environment.ProcessPath ?? string.Empty,
            ListenerBindings: CreateLoopbackListenerBindingsForTesting(controllerPort)));
        SetController(api);
        _usingServiceCore = usingServiceCore;
        ConfirmCoreHealth(
            _coreLifecycle.Epoch,
            _processManager.Generation,
            ControllerGeneration);
        _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { State = CoreState.Running } });
    }

    internal void SetRuntimeBindingForTesting(CoreRuntimeBinding binding)
    {
        bool healthWasConfirmed = CoreHealthConfirmed;
        _coreLifecycle.SetObservationSource(new FixtureCoreOwnershipObservationSource());
        // Downstream eligibility tests deliberately inject incomplete facts.
        // Production admission always goes through SetRuntimeBinding instead.
        StoreRuntimeBinding(binding);
        if (healthWasConfirmed)
        {
            ConfirmCoreHealth(_coreLifecycle.Epoch, _processManager.Generation, ControllerGeneration);
        }
    }

    internal void SetShutdownStateForTesting(
        bool serviceOwnsCore,
        CoreState core,
        TunState tun,
        SystemProxyState systemProxy)
    {
        _coreLifecycle.SetObservationSource(new FixtureCoreOwnershipObservationSource());
        _usingServiceCore = serviceOwnsCore;
        if (serviceOwnsCore && core == CoreState.Running && ActiveRuntimeBinding is null)
        {
            using Process process = Process.GetCurrentProcess();
            SetRuntimeBinding(new CoreRuntimeBinding(
                _settings.ControllerPort,
                _settings.ControllerPort,
                Guid.NewGuid(),
                Guid.NewGuid(),
                process.Id,
                process.StartTime.ToUniversalTime().Ticks,
                Math.Max(1, _processManager.Generation),
                _settings.HttpPort,
                _settings.SocksPort,
                _settings.MixedPort,
                ControllerReady: true,
                HttpReady: true,
                SocksReady: true,
                MixedReady: true,
                Environment.ProcessPath ?? string.Empty,
                ListenerBindings: CreateLoopbackListenerBindingsForTesting(_settings.ControllerPort)));
        }

        _confirmedTunState = tun;
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = core },
            Tun = tun,
            SystemProxy = systemProxy
        });
    }
    internal bool IsCoreHealthConfirmedForTesting => CoreHealthConfirmed;

    internal Task ApplyProgramOverridesForTestingAsync(CancellationToken cancellationToken = default) =>
        ApplyProgramOverridesWithLeaseAsync(coreRunning: IsCoreHealthy(), cancellationToken);

    private List<RuntimeListenerBinding> CreateLoopbackListenerBindingsForTesting(int controllerPort) =>
        CreateRuntimeListenerBindings(controllerPort, MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(
        [
            "allow-lan: false",
            $"port: {_settings.HttpPort}",
            $"socks-port: {_settings.SocksPort}",
            $"mixed-port: {_settings.MixedPort}"
        ]));

    internal Task<OperationGate.Lease> AcquireSharedOperationForTestingAsync() =>
        _operationLock.AcquireSharedAsync();

    internal void SetCoreStateForTesting(CoreState state) => UpdateCoreState(state, null);

    internal Task RefreshControllerDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken);

    internal Task RefreshPollingDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshPollingDataAsync(cancellationToken);

    internal Task RefreshControllerDataForTestingAsync(
        bool includeRulesAndProviders,
        CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken, includeRulesAndProviders);

    internal async Task RevokeSystemProxyForTestingAsync()
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync();
        await RevokeSystemProxyForCoreLossAsync(operationLease);
    }
}

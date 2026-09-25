using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    internal void AttachControllerForTesting(MihomoApiClient api, bool usingServiceCore)
    {
        ArgumentNullException.ThrowIfNull(api);
        SetController(api);
        _usingServiceCore = usingServiceCore;
        ConfirmCoreHealth(
            Volatile.Read(ref _coreLifecycleEpoch),
            _processManager.Generation,
            ControllerGeneration);
        _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { State = CoreState.Running } });
    }

    internal void SetShutdownStateForTesting(
        bool serviceOwnsCore,
        CoreState core,
        TunState tun,
        SystemProxyState systemProxy)
    {
        _usingServiceCore = serviceOwnsCore;
        _confirmedTunState = tun;
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = core },
            Tun = tun,
            SystemProxy = systemProxy
        });
    }
    internal bool IsCoreHealthConfirmedForTesting => CoreHealthConfirmed;

    internal Task<OperationGate.Lease> AcquireSharedOperationForTestingAsync() =>
        _operationLock.AcquireSharedAsync();

    internal void SetCoreStateForTesting(CoreState state) => UpdateCoreState(state, null);

    internal Task RefreshControllerDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken);

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

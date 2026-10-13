using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

// Test-only assembly composition. Paths are mandatory; omitted machine services
// are isolated. Tests supplying a fake controller must explicitly opt into fake
// ownership observations. Real process/listener tests keep Windows observations.
internal static class RuntimeTestBuilder
{
    public static ClashTrayRuntime Create(
        AppPaths paths,
        IStartupRegistration? startupRegistration = null,
        IServicePipeClient? servicePipeClient = null,
        ISettingsStore? settingsStore = null,
        ISystemProxyController? systemProxy = null,
        IConfigurationCandidateValidator? candidateValidator = null,
        INetworkContextSource? networkContextSource = null,
        IEndpointSessionConnector? endpointSessionConnector = null,
        Func<TimeSpan, CancellationToken, Task>? remoteRefreshDelayAsync = null,
        Func<EndpointSession, EndpointSessionStatusEventArgs, CancellationToken, Task>? remoteLogStreamRunner = null,
        Func<MihomoApiClient>? controllerApiFactory = null,
        TimeSpan? disposeCleanupTimeout = null,
        Func<string, CancellationToken, Task>? shutdownStepTestHook = null,
        Func<LocalCoreProcessIdentity?>? localCoreProcessIdentityProvider = null,
        Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>>? localCoreRecoveryAction = null,
        TimeSpan? coreStartupBudget = null,
        HttpMessageHandler? controllerHttpMessageHandler = null,
        TimeProvider? shutdownTimeProvider = null,
        ICoreOwnershipObservationSource? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RuntimeEnvironmentDependencies environment = new(paths,
            startupRegistration ?? new StartupManager(new InMemoryStartupRegistry()),
            servicePipeClient ?? new IsolatedServicePipeClient(),
            settingsStore ?? new SettingsStore(paths),
            systemProxy ?? new IsolatedSystemProxyController(),
            ownership ?? new WindowsCoreOwnershipObservationSource());
        return new(environment, candidateValidator, networkContextSource, endpointSessionConnector,
            remoteRefreshDelayAsync, remoteLogStreamRunner, controllerApiFactory, disposeCleanupTimeout,
            shutdownStepTestHook, localCoreProcessIdentityProvider, localCoreRecoveryAction,
            coreStartupBudget, controllerHttpMessageHandler, shutdownTimeProvider);
    }

    public static void AttachControllerForTesting(this ClashTrayRuntime runtime, MihomoApiClient api, bool usingServiceCore) =>
        runtime.AttachControllerForTesting(api, usingServiceCore, new FixtureCoreOwnershipObservationSource());

    public static void SetRuntimeBindingForTesting(this ClashTrayRuntime runtime, CoreRuntimeBinding binding) =>
        runtime.SetRuntimeBindingForTesting(binding, new FixtureCoreOwnershipObservationSource());

    public static void SetShutdownStateForTesting(this ClashTrayRuntime runtime,
        bool serviceOwnsCore, CoreState core, TunState tun, SystemProxyState systemProxy) =>
        runtime.SetShutdownStateForTesting(serviceOwnsCore, core, tun, systemProxy, new FixtureCoreOwnershipObservationSource());
}

internal sealed class FixtureCoreOwnershipObservationSource : ICoreOwnershipObservationSource
{
    public MihomoListenerPorts ReadControllerPorts(JsonDocument configuration, CoreRuntimeBinding binding)
    {
        MihomoListenerPorts ports = MihomoDataParser.ParseListenerPorts(configuration);
        return new(ports.Http ?? binding.HttpPort, ports.Socks ?? binding.SocksPort, ports.Mixed ?? binding.MixedPort);
    }
    public bool IsProcessCurrent(LocalCoreProcessIdentity identity) => true;
    public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(
        LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners) =>
        _ => new ListenerOwnerObservation(ListenerOwnerState.Owned);
}

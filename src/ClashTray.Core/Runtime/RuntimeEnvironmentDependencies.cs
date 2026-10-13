namespace ClashTray.Core;

internal sealed record RuntimeEnvironmentDependencies(
    AppPaths Paths,
    IStartupRegistration Startup,
    IServicePipeClient Service,
    ISettingsStore Settings,
    ISystemProxyController SystemProxy,
    ICoreOwnershipObservationSource Ownership)
{
    public static RuntimeEnvironmentDependencies Production(AppPaths paths) => new(paths,
        new StartupManager(), new ServicePipeClient(), new SettingsStore(paths), new SystemProxyManager(paths), new WindowsCoreOwnershipObservationSource());

    public static RuntimeEnvironmentDependencies Isolated(AppPaths paths) => new(paths,
        new StartupManager(new InMemoryStartupRegistry()), new IsolatedServicePipeClient(),
        new SettingsStore(paths), new IsolatedSystemProxyController(), new WindowsCoreOwnershipObservationSource());
}

public static class ClashTrayRuntimeFactory
{
    public static ClashTrayRuntime CreateProduction(AppPaths? paths = null, INetworkContextSource? networkContextSource = null) =>
        new(RuntimeEnvironmentDependencies.Production(paths ?? new AppPaths()), networkContextSource: networkContextSource);

    public static ClashTrayRuntime CreateIsolated(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(RuntimeEnvironmentDependencies.Isolated(paths));
    }
}

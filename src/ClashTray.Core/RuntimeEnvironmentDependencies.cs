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

    public static RuntimeEnvironmentDependencies Compatible(AppPaths? paths, IStartupRegistration? startup,
        IServicePipeClient? service, ISettingsStore? settings, ISystemProxyController? systemProxy, ICoreOwnershipObservationSource? ownership)
    {
        AppPaths resolved = paths ?? new AppPaths();
        return new(resolved,
            startup ?? (paths is null ? new StartupManager() : new StartupManager(new InMemoryStartupRegistry())),
            service ?? (paths is null ? new ServicePipeClient() : new IsolatedServicePipeClient()),
            settings ?? new SettingsStore(resolved), systemProxy ?? new SystemProxyManager(resolved),
            ownership ?? new WindowsCoreOwnershipObservationSource());
    }
}

public static class ClashTrayRuntimeFactory
{
    public static ClashTrayRuntime CreateProduction(AppPaths? paths = null, INetworkContextSource? networkContextSource = null) =>
        new(RuntimeEnvironmentDependencies.Production(paths ?? new AppPaths()), networkContextSource: networkContextSource);

    public static ClashTrayRuntime CreateIsolated(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(RuntimeEnvironmentDependencies.Compatible(paths, null, null, null, new IsolatedSystemProxyController(), null));
    }
}

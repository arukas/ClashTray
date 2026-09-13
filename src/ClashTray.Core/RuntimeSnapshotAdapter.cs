using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeSnapshotAdapter
{
    public static AppSnapshot ToAppSnapshot(
        RuntimeSnapshot snapshot,
        AppSettings settings,
        long controllerGeneration = 0,
        DateTimeOffset? lastConfirmedAt = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        EndpointDescriptor localEndpoint = new(
            EndpointId.Local,
            EndpointKind.Local,
            EndpointId.Local.Value,
            new Uri($"http://127.0.0.1:{settings.ControllerPort}/"),
            EndpointTransportSecurity.Loopback);

        EndpointSessionState sessionState = snapshot.Core.State switch
        {
            CoreState.Validating or CoreState.Starting or CoreState.Restarting => EndpointSessionState.Connecting,
            CoreState.Stopping => EndpointSessionState.Reconnecting,
            CoreState.Running => EndpointSessionState.Connected,
            _ => EndpointSessionState.Disconnected
        };

        LocalDeviceSnapshot localDevice = new(
            snapshot.Core.State,
            snapshot.Core.Version,
            settings.ActiveConfigurationId,
            snapshot.Core.ConfigurationName
                ?? snapshot.Configurations.FirstOrDefault(configuration => configuration.IsActive)?.Name,
            snapshot.Configurations,
            snapshot.SystemProxy,
            snapshot.Tun,
            snapshot.Subscription,
            settings.SystemProxyEnabled,
            settings.TunEnabled,
            snapshot.ErrorMessage ?? snapshot.Core.ErrorMessage);

        ControllerSessionSnapshot controller = new(
            localEndpoint,
            sessionState,
            controllerGeneration,
            lastConfirmedAt,
            snapshot.Core,
            snapshot.ProxyGroups,
            snapshot.ProxyNodes,
            snapshot.Connections,
            snapshot.Rules,
            snapshot.Providers,
            snapshot.RuleProviders,
            snapshot.Logs,
            EndpointCapabilityDefaults.Local,
            snapshot.ErrorMessage ?? snapshot.Core.ErrorMessage);

        return new AppSnapshot(
            localDevice,
            controller,
            [localEndpoint],
            settings.Language,
            settings.Theme,
            snapshot.ErrorMessage);
    }
}

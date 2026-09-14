using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeSnapshotAdapter
{
    public static AppSnapshot ToAppSnapshot(
        RuntimeSnapshot snapshot,
        AppSettings settings,
        long controllerGeneration = 0,
        DateTimeOffset? lastConfirmedAt = null,
        IReadOnlyList<EndpointDescriptor>? endpoints = null)
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
            BuildEndpointList(localEndpoint, endpoints),
            settings.Language,
            settings.Theme,
            snapshot.ErrorMessage);
    }

    private static List<EndpointDescriptor> BuildEndpointList(
        EndpointDescriptor localEndpoint,
        IReadOnlyList<EndpointDescriptor>? endpoints)
    {
        List<EndpointDescriptor> result = [localEndpoint];
        HashSet<EndpointId> ids = [EndpointId.Local];
        if (endpoints is null)
        {
            return result;
        }

        foreach (EndpointDescriptor endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            if (endpoint.Id == EndpointId.Local)
            {
                continue;
            }

            if (endpoint.Kind != EndpointKind.Remote)
            {
                throw new ArgumentException(
                    "Snapshot endpoint summaries may contain remote endpoints only.",
                    nameof(endpoints));
            }

            Uri normalizedBaseUri = EndpointUriNormalizer.NormalizeBaseUri(
                endpoint.BaseUri,
                endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed);
            bool isHttps = normalizedBaseUri.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase);
            EndpointTransportSecurity expectedSecurity = isHttps
                ? EndpointTransportSecurity.HttpsSystemTrust
                : EndpointTransportSecurity.HttpExplicitlyConfirmed;
            if (endpoint.Security != EndpointTransportSecurity.HttpsCustomCertificate
                && endpoint.Security != expectedSecurity)
            {
                throw new ArgumentException(
                    "Snapshot endpoint security does not match its URI.",
                    nameof(endpoints));
            }

            if (!ids.Add(endpoint.Id))
            {
                throw new ArgumentException(
                    "Snapshot endpoint IDs must be unique.",
                    nameof(endpoints));
            }

            result.Add(endpoint with { BaseUri = normalizedBaseUri });
        }

        return result;
    }
}

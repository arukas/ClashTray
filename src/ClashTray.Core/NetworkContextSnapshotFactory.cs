using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record NetworkConnectionObservation(
    NetworkConnectivityKind ConnectivityKind,
    string? Ssid,
    string? InterfaceIdentity);

public static class NetworkContextSnapshotFactory
{
    public static NetworkContextSnapshot Create(
        long revision,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<NetworkConnectionObservation> activeConnections,
        NetworkPermissionState permissionState = NetworkPermissionState.Allowed,
        string? errorMessage = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentNullException.ThrowIfNull(activeConnections);

        NetworkConnectionObservation[] connections = activeConnections
            .Where(connection => connection is not null
                && connection.ConnectivityKind != NetworkConnectivityKind.None)
            .Distinct()
            .ToArray();
        NetworkConnectionObservation[] wifiConnections = connections
            .Where(connection => connection.ConnectivityKind == NetworkConnectivityKind.WiFi)
            .ToArray();
        DateTimeOffset observedUtc = observedAtUtc.ToUniversalTime();
        string? normalizedError = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;

        if (wifiConnections.Length > 0)
        {
            bool ambiguous = wifiConnections.Length > 1;
            NetworkConnectionObservation primary = wifiConnections[0];
            return new NetworkContextSnapshot(
                revision,
                observedUtc,
                NetworkConnectivityKind.WiFi,
                ambiguous ? null : primary.Ssid,
                ambiguous ? null : NormalizeInterfaceIdentity(primary.InterfaceIdentity),
                permissionState,
                ambiguous,
                true,
                normalizedError);
        }

        NetworkConnectionObservation[] ethernetConnections = connections
            .Where(connection => connection.ConnectivityKind == NetworkConnectivityKind.Ethernet)
            .ToArray();
        if (ethernetConnections.Length > 0)
        {
            return new NetworkContextSnapshot(
                revision,
                observedUtc,
                NetworkConnectivityKind.Ethernet,
                null,
                ethernetConnections.Length == 1
                    ? NormalizeInterfaceIdentity(ethernetConnections[0].InterfaceIdentity)
                    : null,
                permissionState,
                false,
                true,
                normalizedError);
        }

        if (connections.Length > 0)
        {
            NetworkConnectionObservation primary = connections[0];
            return new NetworkContextSnapshot(
                revision,
                observedUtc,
                primary.ConnectivityKind,
                null,
                connections.Length == 1
                    ? NormalizeInterfaceIdentity(primary.InterfaceIdentity)
                    : null,
                permissionState,
                false,
                true,
                normalizedError);
        }

        return new NetworkContextSnapshot(
            revision,
            observedUtc,
            NetworkConnectivityKind.None,
            null,
            null,
            permissionState,
            false,
            false,
            normalizedError);
    }

    private static string? NormalizeInterfaceIdentity(string? interfaceIdentity) =>
        string.IsNullOrWhiteSpace(interfaceIdentity) ? null : interfaceIdentity;
}

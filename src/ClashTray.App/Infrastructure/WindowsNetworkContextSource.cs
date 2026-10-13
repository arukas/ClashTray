using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using ClashTray.Contracts;
using ClashTray.Core;
using Windows.Networking.Connectivity;

namespace ClashTray.App;

[SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "The App composition root owns this platform adapter for the normal tray runtime.")]
internal sealed class WindowsNetworkContextSource : INetworkContextSource
{
    private const uint EthernetIanaInterfaceType = 6;
    private const int EAccessDenied = unchecked((int)0x80070005);

    private readonly object _stateGate = new();
    private long _revision;
    private bool _disposed;

    public WindowsNetworkContextSource()
    {
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public event EventHandler<NetworkContextSnapshot>? ContextChanged;

    public Task<NetworkContextSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadSnapshot());
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            ContextChanged = null;
        }

        return ValueTask.CompletedTask;
    }

    private void OnNetworkStatusChanged(object sender)
    {
        NetworkContextSnapshot snapshot;
        EventHandler<NetworkContextSnapshot>? handler;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            snapshot = ReadSnapshotLocked();
            handler = ContextChanged;
        }

        handler?.Invoke(this, snapshot);
    }

    private NetworkContextSnapshot ReadSnapshot()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadSnapshotLocked();
        }
    }

    private NetworkContextSnapshot ReadSnapshotLocked()
    {
        long revision = ++_revision;
        DateTimeOffset observedAtUtc = TimeProvider.System.GetUtcNow();
        return ReadSnapshotCore(revision, observedAtUtc);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "NetworkInformation is an OS boundary; expected and unexpected failures become a safe unavailable snapshot so a status callback cannot terminate the tray process.")]
    private static NetworkContextSnapshot ReadSnapshotCore(long revision, DateTimeOffset observedAtUtc)
    {
        try
        {
            List<NetworkConnectionObservation> activeConnections = [];
            NetworkPermissionState permissionState = NetworkPermissionState.Allowed;
            foreach (ConnectionProfile profile in NetworkInformation.GetConnectionProfiles())
            {
                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                {
                    continue;
                }

                NetworkConnectivityKind connectivityKind = GetConnectivityKind(profile);
                string? ssid = null;
                if (connectivityKind == NetworkConnectivityKind.WiFi)
                {
                    try
                    {
                        ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
                    {
                        permissionState = NetworkPermissionState.Denied;
                    }
                    catch (COMException exception) when (exception.HResult == EAccessDenied)
                    {
                        permissionState = NetworkPermissionState.Denied;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or COMException)
                    {
                        permissionState = NetworkPermissionState.Unavailable;
                    }
                }

                activeConnections.Add(new NetworkConnectionObservation(
                    connectivityKind,
                    ssid,
                    GetInterfaceIdentity(profile)));
            }

            return NetworkContextSnapshotFactory.Create(
                revision,
                observedAtUtc,
                activeConnections,
                permissionState);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException)
        {
            return UnavailableSnapshot(
                revision,
                observedAtUtc,
                NetworkPermissionState.Denied,
                LocalizationService.Get("NetworkPermissionDenied"));
        }
        catch (COMException exception) when (exception.HResult == EAccessDenied)
        {
            return UnavailableSnapshot(
                revision,
                observedAtUtc,
                NetworkPermissionState.Denied,
                LocalizationService.Get("NetworkPermissionDenied"));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or NotImplementedException
            or PlatformNotSupportedException
            or TypeLoadException
            or COMException)
        {
            return UnavailableSnapshot(
                revision,
                observedAtUtc,
                NetworkPermissionState.Unavailable,
                LocalizationService.Get("NetworkApiUnavailable"));
        }
        catch (Exception)
        {
            return UnavailableSnapshot(
                revision,
                observedAtUtc,
                NetworkPermissionState.Unavailable,
                LocalizationService.Get("NetworkStateReadFailed"));
        }
    }

    private static NetworkConnectivityKind GetConnectivityKind(ConnectionProfile profile)
    {
        if (profile.IsWlanConnectionProfile)
        {
            return NetworkConnectivityKind.WiFi;
        }

        if (profile.IsWwanConnectionProfile)
        {
            return NetworkConnectivityKind.Other;
        }

        return profile.NetworkAdapter?.IanaInterfaceType == EthernetIanaInterfaceType
            ? NetworkConnectivityKind.Ethernet
            : NetworkConnectivityKind.Other;
    }

    private static string? GetInterfaceIdentity(ConnectionProfile profile) =>
        profile.NetworkAdapter?.NetworkAdapterId.ToString("D");

    private static NetworkContextSnapshot UnavailableSnapshot(
        long revision,
        DateTimeOffset observedAtUtc,
        NetworkPermissionState permissionState,
        string errorMessage) =>
        NetworkContextSnapshotFactory.Create(
            revision,
            observedAtUtc,
            [],
            permissionState,
            errorMessage);
}

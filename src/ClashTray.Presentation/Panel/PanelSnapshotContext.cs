using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

// Keep local device facts and selected controller facts together and explicit.
// Only the existing page API receives the compatibility projection.
internal sealed record PanelSnapshotContext(
    LocalDeviceSnapshot Device,
    ControllerSessionSnapshot Controller,
    RuntimeSnapshot Pages)
{
    public string ControllerIdentity { get; } = $"{Controller.Endpoint.Id.Value}:{Controller.Generation}";
    public EndpointCommandTarget CommandTarget { get; } = new(Controller.Endpoint.Id, Controller.Generation);

    public static PanelSnapshotContext Create(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot.LocalDevice, snapshot.ActiveController, RuntimeSnapshotAdapter.ToRuntimeSnapshot(snapshot));
    }
}

internal static class TrayStateProjection
{
    public static TrayState FromDevice(LocalDeviceSnapshot device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.CoreState is CoreState.Failed
            ? TrayState.Error
            : device.CoreState is CoreState.Validating or CoreState.Starting or CoreState.Stopping or CoreState.Restarting
                ? TrayState.Connecting
                : device.Tun is TunState.On
                    ? TrayState.Tun
                    : device.SystemProxy is SystemProxyState.On
                        ? TrayState.SystemProxy
                        : device.CoreState is CoreState.Running
                            ? TrayState.Running
                            : device.CoreState is CoreState.Stopped
                                ? TrayState.Paused
                                : TrayState.Stopped;
    }
}

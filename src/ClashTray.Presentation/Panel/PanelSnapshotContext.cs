using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.App;

// Created from one application observation. Pages cannot pair data from one
// controller with a writable flag, identity or target captured from another.
internal sealed record PanelSnapshotContext
{
    private PanelSnapshotContext(AppSnapshot snapshot)
    {
        Device = snapshot.LocalDevice;
        ControllerSessionSnapshot controller = snapshot.ActiveController;
        EndpointCapability capabilities = controller.Endpoint.Kind == EndpointKind.Remote
            ? controller.Capabilities & EndpointCapabilityDefaults.Remote
            : controller.Capabilities;
        Controller = capabilities == controller.Capabilities ? controller : controller with { Capabilities = capabilities };
        Pages = RuntimeSnapshotAdapter.ToRuntimeSnapshot(snapshot with { ActiveController = Controller });
    }

    public LocalDeviceSnapshot Device { get; }
    public ControllerSessionSnapshot Controller { get; }
    public RuntimeSnapshot Pages { get; }
    public bool ControllerWritable => Controller.Endpoint.Kind == EndpointKind.Local || Controller.State == EndpointSessionState.Connected;
    public EndpointCapability ControllerCapabilities => Controller.Capabilities;
    public string ControllerIdentity => $"{Controller.Endpoint.Id.Value}:{Controller.Generation}";
    public EndpointCommandTarget CommandTarget => new(Controller.Endpoint.Id, Controller.Generation);

    public static PanelSnapshotContext Create(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot);
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

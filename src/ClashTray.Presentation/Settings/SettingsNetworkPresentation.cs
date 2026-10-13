using ClashTray.Contracts;

namespace ClashTray.App;

internal sealed record NetworkSwitchPresentation(bool IsOn, bool IsEnabled, string StateKey);
internal sealed record SettingsNetworkPresentation(
    bool CanStartWithAvailableControllerPort,
    NetworkSwitchPresentation SystemProxy,
    NetworkSwitchPresentation Tun)
{
    public static SettingsNetworkPresentation Create(
        RuntimeSnapshot snapshot,
        bool savingSettings,
        bool applyingNetworkControl,
        bool canControlSystemProxy,
        bool canControlTun)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool coreRunning = snapshot.Core.State == CoreState.Running;
        return new(
            !savingSettings && snapshot.Core.State is not (CoreState.Running or CoreState.Starting or CoreState.Restarting),
            new(snapshot.SystemProxy == SystemProxyState.On,
                !applyingNetworkControl && coreRunning && canControlSystemProxy
                && snapshot.SystemProxy is not (SystemProxyState.Enabling or SystemProxyState.Disabling),
                snapshot.SystemProxy switch
                {
                    SystemProxyState.On => "SwitchStateOn",
                    SystemProxyState.Enabling => "SwitchStateEnabling",
                    SystemProxyState.Disabling => "SwitchStateDisabling",
                    SystemProxyState.RestoreRequired => "SwitchStateRestoreRequired",
                    SystemProxyState.Failed => "SwitchStateFailed",
                    _ => "SwitchStateOff"
                }),
            new(snapshot.Tun == TunState.On,
                !applyingNetworkControl && coreRunning && canControlTun
                && snapshot.Tun is TunState.Off or TunState.On,
                snapshot.Tun switch
                {
                    TunState.On => "SwitchStateOn",
                    TunState.Enabling => "SwitchStateEnabling",
                    TunState.Disabling => "SwitchStateDisabling",
                    TunState.Unavailable => "TunStateUnavailable",
                    TunState.Unknown => "TunStateUnknown",
                    TunState.Failed => "SwitchStateFailed",
                    _ => "SwitchStateOff"
                }));
    }
}

using ClashTray.Contracts;

namespace ClashTray.App;

internal enum PanelStatusTone
{
    Neutral,
    Running,
    Pending,
    Failed
}

internal static class PanelStatusPresentation
{
    public static PanelStatusTone CoreTone(CoreState state) => state switch
    {
        CoreState.Running => PanelStatusTone.Running,
        CoreState.Validating or CoreState.Starting or CoreState.Stopping or CoreState.Restarting => PanelStatusTone.Pending,
        CoreState.Failed => PanelStatusTone.Failed,
        _ => PanelStatusTone.Neutral
    };

    public static PanelStatusTone EndpointTone(EndpointSessionState state) => state switch
    {
        EndpointSessionState.Connected => PanelStatusTone.Running,
        EndpointSessionState.Connecting or EndpointSessionState.Reconnecting => PanelStatusTone.Pending,
        EndpointSessionState.AuthenticationFailed
            or EndpointSessionState.CertificateFailed
            or EndpointSessionState.Incompatible
            or EndpointSessionState.Failed => PanelStatusTone.Failed,
        _ => PanelStatusTone.Neutral
    };
}

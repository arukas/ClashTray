using ClashTray.Contracts;

namespace ClashTray.App;

internal static class PanelDisplayFormatting
{
    public static string CoreStateKey(CoreState state) => state switch
    {
        CoreState.Running => "CoreStateRunning",
        CoreState.Validating => "CoreStateValidating",
        CoreState.Starting => "CoreStateStarting",
        CoreState.Stopping => "CoreStateStopping",
        CoreState.Restarting => "CoreStateRestarting",
        CoreState.Failed => "CoreStateFailed",
        CoreState.Missing => "CoreStateMissing",
        _ => "CoreStateStopped"
    };

    public static string EndpointStateKey(EndpointSessionState state) => state switch
    {
        EndpointSessionState.Connected => "ControllerStateConnected",
        EndpointSessionState.Connecting => "ControllerStateConnecting",
        EndpointSessionState.Reconnecting => "ControllerStateReconnecting",
        EndpointSessionState.AuthenticationFailed => "ControllerStateAuthenticationFailed",
        EndpointSessionState.CertificateFailed => "ControllerStateCertificateFailed",
        EndpointSessionState.Incompatible => "ControllerStateIncompatible",
        EndpointSessionState.Failed => "ControllerStateFailed",
        _ => "ControllerStateDisconnected"
    };

    public static string? ErrorKey(ErrorCode errorCode) => errorCode switch
    {
        ErrorCode.EndpointAuthenticationFailed => "ControllerErrorAuthentication",
        ErrorCode.EndpointCertificateFailed => "ControllerErrorCertificate",
        ErrorCode.EndpointIncompatible => "ControllerErrorIncompatible",
        ErrorCode.EndpointStaleResult => "ControllerErrorStale",
        ErrorCode.EndpointTransportFailed => "ControllerErrorTransport",
        _ => null
    };

    public static string FormatRate(double bytesPerSecond)
    {
        double value = bytesPerSecond;
        string unit = "B/s";
        if (value >= 1024)
        {
            value /= 1024;
            unit = "KB/s";
        }
        if (value >= 1024)
        {
            value /= 1024;
            unit = "MB/s";
        }
        return $"{value:0.#} {unit}";
    }

    public static string FormatBytes(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:0.#} KB"
        : $"{bytes / 1024d / 1024d:0.#} MB";
}

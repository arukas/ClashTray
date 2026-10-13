using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Shared guards over the controller session registry: capture the current
/// binding and prove that a captured binding still owns the session before a
/// mutation is issued or an observed result is allowed to land.
/// </summary>
internal sealed class ControllerSessionGuard
{
    private readonly MihomoControllerSessionRegistry _sessions;

    public ControllerSessionGuard(MihomoControllerSessionRegistry sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public (MihomoApiClient Api, long Generation) Capture()
    {
        MihomoControllerSession session = _sessions.Capture();
        return (session.Api, session.Generation);
    }

    public void EnsureSession(MihomoApiClient api, long generation, string message)
    {
        if (!_sessions.IsCurrent(api, generation))
        {
            throw new InvalidOperationException(message);
        }
    }

    public void EnsureCommand(
        MihomoApiClient api,
        long generation,
        EndpointCommand command,
        string staleSessionMessage)
    {
        EndpointId endpointId = _sessions.Current?.Endpoint.Id ?? EndpointId.Local;
        TargetCommand targetCommand = new(endpointId, generation, command);
        _sessions.EnsureTargetCommandAllowed(api, targetCommand, staleSessionMessage);
    }
}

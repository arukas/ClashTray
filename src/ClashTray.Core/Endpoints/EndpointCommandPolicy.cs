using ClashTray.Contracts;

namespace ClashTray.Core;

public enum EndpointCommand
{
    ObserveStatus,
    ObserveProxies,
    ObserveProviders,
    ObserveRules,
    ObserveConnections,
    ObserveLogs,
    SwitchMode,
    SwitchProxy,
    TestDelay,
    RefreshProvider,
    CloseConnection,
    ClearCache,
    UpdateGeo,
    ManageLocalConfiguration,
    ControlLocalCore,
    ControlSystemProxy,
    ControlTun,
    UpdateLocalCore,
    OpenLocalDashboard
}

public readonly record struct EndpointCommandDecision(
    bool Allowed,
    ErrorCode ErrorCode,
    EndpointCapability RequiredCapability)
{
    public static EndpointCommandDecision Allow(EndpointCapability requiredCapability) =>
        new(true, ErrorCode.None, requiredCapability);

    public static EndpointCommandDecision Deny(
        ErrorCode errorCode,
        EndpointCapability requiredCapability = EndpointCapability.None) =>
        new(false, errorCode, requiredCapability);
}

public sealed class EndpointCommandDeniedException : InvalidOperationException
{
    public EndpointCommandDeniedException()
    {
    }

    public EndpointCommandDeniedException(string message)
        : base(message)
    {
    }

    public EndpointCommandDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public EndpointCommandDeniedException(
        EndpointKind endpointKind,
        EndpointCommand command,
        EndpointCommandDecision decision)
        : base($"Endpoint command '{command}' is not allowed for endpoint kind '{endpointKind}'.")
    {
        EndpointKind = endpointKind;
        Command = command;
        ErrorCode = decision.ErrorCode;
        RequiredCapability = decision.RequiredCapability;
    }

    public EndpointKind EndpointKind { get; }

    public EndpointCommand Command { get; }

    public ErrorCode ErrorCode { get; }

    public EndpointCapability RequiredCapability { get; }
}

public static class EndpointCommandPolicy
{
    public static EndpointCapability GetRequiredCapability(EndpointCommand command)
    {
        if (!TryGetRequiredCapability(command, out EndpointCapability requiredCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        return requiredCapability;
    }

    public static EndpointCommandDecision Evaluate(
        EndpointKind endpointKind,
        EndpointCapability capabilities,
        EndpointCommand command)
    {
        if (endpointKind is not (EndpointKind.Local or EndpointKind.Remote))
        {
            return EndpointCommandDecision.Deny(ErrorCode.UnsupportedEndpointCommand);
        }

        if (!TryGetRequiredCapability(command, out EndpointCapability requiredCapability))
        {
            return EndpointCommandDecision.Deny(ErrorCode.UnsupportedEndpointCommand);
        }

        if (endpointKind == EndpointKind.Remote
            && (requiredCapability & EndpointCapabilityDefaults.LocalOnly) != EndpointCapability.None)
        {
            return EndpointCommandDecision.Deny(
                ErrorCode.EndpointCommandDenied,
                requiredCapability);
        }

        return (capabilities & requiredCapability) == requiredCapability
            ? EndpointCommandDecision.Allow(requiredCapability)
            : EndpointCommandDecision.Deny(
                ErrorCode.EndpointCapabilityUnavailable,
                requiredCapability);
    }

    public static void EnsureAllowed(
        EndpointKind endpointKind,
        EndpointCapability capabilities,
        EndpointCommand command)
    {
        EndpointCommandDecision decision = Evaluate(endpointKind, capabilities, command);
        if (!decision.Allowed)
        {
            throw new EndpointCommandDeniedException(endpointKind, command, decision);
        }
    }

    private static bool TryGetRequiredCapability(
        EndpointCommand command,
        out EndpointCapability requiredCapability)
    {
        requiredCapability = command switch
        {
            EndpointCommand.ObserveStatus => EndpointCapability.ObserveStatus,
            EndpointCommand.ObserveProxies => EndpointCapability.ObserveProxies,
            EndpointCommand.ObserveProviders => EndpointCapability.ObserveProviders,
            EndpointCommand.ObserveRules => EndpointCapability.ObserveRules,
            EndpointCommand.ObserveConnections => EndpointCapability.ObserveConnections,
            EndpointCommand.ObserveLogs => EndpointCapability.ObserveLogs,
            EndpointCommand.SwitchMode => EndpointCapability.SwitchMode,
            EndpointCommand.SwitchProxy => EndpointCapability.SwitchProxy,
            EndpointCommand.TestDelay => EndpointCapability.TestDelay,
            EndpointCommand.RefreshProvider => EndpointCapability.RefreshProvider,
            EndpointCommand.CloseConnection => EndpointCapability.CloseConnection,
            EndpointCommand.ClearCache => EndpointCapability.ClearCache,
            EndpointCommand.UpdateGeo => EndpointCapability.UpdateGeo,
            EndpointCommand.ManageLocalConfiguration => EndpointCapability.ManageLocalConfiguration,
            EndpointCommand.ControlLocalCore => EndpointCapability.ControlLocalCore,
            EndpointCommand.ControlSystemProxy => EndpointCapability.ControlSystemProxy,
            EndpointCommand.ControlTun => EndpointCapability.ControlTun,
            EndpointCommand.UpdateLocalCore => EndpointCapability.UpdateLocalCore,
            EndpointCommand.OpenLocalDashboard => EndpointCapability.OpenLocalDashboard,
            _ => EndpointCapability.None
        };

        return requiredCapability != EndpointCapability.None;
    }
}

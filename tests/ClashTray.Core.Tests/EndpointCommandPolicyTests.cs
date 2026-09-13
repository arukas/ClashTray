using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointCommandPolicyTests
{
    private static readonly EndpointCommand[] LocalCommands =
    [
        EndpointCommand.ManageLocalConfiguration,
        EndpointCommand.ControlLocalCore,
        EndpointCommand.ControlSystemProxy,
        EndpointCommand.ControlTun,
        EndpointCommand.UpdateLocalCore,
        EndpointCommand.OpenLocalDashboard
    ];

    private static readonly EndpointCommand[] ControllerCommands =
    [
        EndpointCommand.ObserveStatus,
        EndpointCommand.ObserveProxies,
        EndpointCommand.ObserveProviders,
        EndpointCommand.ObserveRules,
        EndpointCommand.ObserveConnections,
        EndpointCommand.ObserveLogs,
        EndpointCommand.SwitchMode,
        EndpointCommand.SwitchProxy,
        EndpointCommand.TestDelay,
        EndpointCommand.RefreshProvider,
        EndpointCommand.CloseConnection,
        EndpointCommand.ClearCache,
        EndpointCommand.UpdateGeo
    ];

    [TestMethod]
    public void LocalEndpointAllowsEveryDeclaredLocalCommand()
    {
        foreach (EndpointCommand command in Enum.GetValues<EndpointCommand>())
        {
            EndpointCommandDecision decision = EndpointCommandPolicy.Evaluate(
                EndpointKind.Local,
                EndpointCapabilityDefaults.Local,
                command);

            Assert.IsTrue(decision.Allowed, command.ToString());
            Assert.AreEqual(ErrorCode.None, decision.ErrorCode, command.ToString());
        }
    }

    [TestMethod]
    public void RemoteEndpointRejectsLocalCommandsEvenWhenCapabilitiesAreOverReported()
    {
        foreach (EndpointCommand command in LocalCommands)
        {
            EndpointCommandDecision decision = EndpointCommandPolicy.Evaluate(
                EndpointKind.Remote,
                EndpointCapabilityDefaults.Local,
                command);

            Assert.IsFalse(decision.Allowed, command.ToString());
            Assert.AreEqual(ErrorCode.EndpointCommandDenied, decision.ErrorCode, command.ToString());
        }
    }

    [TestMethod]
    public void RemoteEndpointUsesIntersectionOfStaticAndHandshakeCapabilities()
    {
        foreach (EndpointCommand command in ControllerCommands)
        {
            EndpointCommandDecision allowed = EndpointCommandPolicy.Evaluate(
                EndpointKind.Remote,
                EndpointCapabilityDefaults.Remote,
                command);
            EndpointCommandDecision denied = EndpointCommandPolicy.Evaluate(
                EndpointKind.Remote,
                EndpointCapability.None,
                command);

            Assert.IsTrue(allowed.Allowed, command.ToString());
            Assert.AreEqual(ErrorCode.None, allowed.ErrorCode, command.ToString());
            Assert.IsFalse(denied.Allowed, command.ToString());
            Assert.AreEqual(ErrorCode.EndpointCapabilityUnavailable, denied.ErrorCode, command.ToString());
        }
    }

    [TestMethod]
    public void UnknownCommandIsDeniedWithStableErrorCode()
    {
        EndpointCommandDecision decision = EndpointCommandPolicy.Evaluate(
            EndpointKind.Local,
            EndpointCapabilityDefaults.Local,
            (EndpointCommand)int.MaxValue);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual(ErrorCode.UnsupportedEndpointCommand, decision.ErrorCode);
    }

    [TestMethod]
    public void EnsureAllowedExposesStableDenialDetails()
    {
        EndpointCommandDeniedException exception = Assert.ThrowsExactly<EndpointCommandDeniedException>(() =>
            EndpointCommandPolicy.EnsureAllowed(
                EndpointKind.Remote,
                EndpointCapabilityDefaults.Remote,
                EndpointCommand.ControlTun));

        Assert.AreEqual(ErrorCode.EndpointCommandDenied, exception.ErrorCode);
        Assert.AreEqual(EndpointKind.Remote, exception.EndpointKind);
        Assert.AreEqual(EndpointCommand.ControlTun, exception.Command);
        Assert.AreEqual(EndpointCapability.ControlTun, exception.RequiredCapability);
    }
}

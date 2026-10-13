using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointSessionSnapshotFactoryTests
{
    [TestMethod]
    public void ConnectedRemoteSnapshotCarriesHandshakeDataButNoLocalCapabilities()
    {
        EndpointDescriptor endpoint = CreateEndpoint();
        EndpointSessionStatusEventArgs status = new(
            endpoint,
            EndpointSessionState.Connected,
            generation: 4,
            selectionRevision: 2,
            lastConfirmedAt: DateTimeOffset.UtcNow,
            attempt: 1,
            nextRetryDelay: null,
            errorMessage: null);
        EndpointHandshakeResult handshake = new(
            EndpointSessionState.Connected,
            "v1.19.30",
            EndpointCapabilityDefaults.Remote | EndpointCapability.ControlTun,
            null);
        CoreStatus core = new(
            CoreState.Running,
            "old-version",
            null,
            ProxyMode.Rule,
            0,
            0,
            0,
            0,
            0,
            0,
            null);

        ControllerSessionSnapshot snapshot = EndpointSessionSnapshotFactory.Create(
            endpoint,
            status,
            handshake,
            core);

        Assert.AreEqual(EndpointSessionState.Connected, snapshot.State);
        Assert.AreEqual("v1.19.30", snapshot.Status!.Version);
        Assert.AreEqual(EndpointCapabilityDefaults.Remote, snapshot.Capabilities);
        Assert.IsFalse(snapshot.Capabilities.HasFlag(EndpointCapability.ControlTun));
        Assert.AreEqual(4, snapshot.Generation);
        Assert.AreEqual(ErrorCode.None, snapshot.ErrorCode);
    }

    [TestMethod]
    public void IncompatibleHandshakeRemovesCapabilitiesAndPreservesFailureState()
    {
        EndpointDescriptor endpoint = CreateEndpoint();
        EndpointSessionStatusEventArgs status = new(
            endpoint,
            EndpointSessionState.Connected,
            generation: 5,
            selectionRevision: 3,
            lastConfirmedAt: null,
            attempt: 1,
            nextRetryDelay: null,
            errorMessage: null);
        EndpointHandshakeResult handshake = new(
            EndpointSessionState.Incompatible,
            null,
            EndpointCapability.None,
            "version missing");

        ControllerSessionSnapshot snapshot = EndpointSessionSnapshotFactory.Create(
            endpoint,
            status,
            handshake);

        Assert.AreEqual(EndpointSessionState.Incompatible, snapshot.State);
        Assert.AreEqual(EndpointCapability.None, snapshot.Capabilities);
        Assert.AreEqual("version missing", snapshot.ErrorMessage);
        Assert.AreEqual(ErrorCode.EndpointIncompatible, snapshot.ErrorCode);
    }

    [TestMethod]
    public void StatusForAnotherEndpointIsRejectedBeforeProjection()
    {
        EndpointDescriptor endpoint = CreateEndpoint();
        EndpointDescriptor other = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("other"),
            "Other",
            new Uri("https://other.example.test"));
        EndpointSessionStatusEventArgs status = new(
            other,
            EndpointSessionState.Connected,
            generation: 1,
            selectionRevision: 1,
            lastConfirmedAt: null,
            attempt: 1,
            nextRetryDelay: null,
            errorMessage: null);

        Assert.ThrowsExactly<ArgumentException>(() => EndpointSessionSnapshotFactory.Create(
            endpoint,
            status));
    }

    private static EndpointDescriptor CreateEndpoint() =>
        EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
}

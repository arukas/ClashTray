using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSnapshotAdapterTests
{
    [TestMethod]
    public void ProjectionSeparatesLocalDeviceStateFromControllerState()
    {
        ConfigurationProfile configuration = new(
            "home",
            "Home",
            "home.yaml",
            null,
            null,
            true);
        RuntimeSnapshot snapshot = CreateSnapshot(
            new CoreStatus(
                CoreState.Running,
                "v1.19.30",
                "Home",
                ProxyMode.Rule,
                12,
                34,
                100,
                200,
                3,
                4096,
                null),
            [configuration]);
        AppSettings settings = new(
            ActiveConfigurationId: configuration.Id,
            ControllerPort: 9191,
            SystemProxyEnabled: true,
            TunEnabled: true,
            Language: "en-US",
            Theme: "dark");

        AppSnapshot projected = RuntimeSnapshotAdapter.ToAppSnapshot(
            snapshot,
            settings,
            controllerGeneration: 17);

        Assert.AreEqual(CoreState.Running, projected.LocalDevice.CoreState);
        Assert.AreEqual(configuration.Id, projected.LocalDevice.ActiveConfigurationId);
        Assert.AreEqual("Home", projected.LocalDevice.ActiveConfigurationName);
        Assert.IsTrue(projected.LocalDevice.DesiredSystemProxyEnabled);
        Assert.IsTrue(projected.LocalDevice.DesiredTunEnabled);
        Assert.AreEqual(EndpointId.Local, projected.ActiveController.Endpoint.Id);
        Assert.AreEqual(EndpointKind.Local, projected.ActiveController.Endpoint.Kind);
        Assert.AreEqual("http://127.0.0.1:9191/", projected.ActiveController.Endpoint.BaseUri.AbsoluteUri);
        Assert.AreEqual(EndpointSessionState.Connected, projected.ActiveController.State);
        Assert.AreEqual(17, projected.ActiveController.Generation);
        Assert.AreEqual(EndpointCapabilityDefaults.Local, projected.ActiveController.Capabilities);
        Assert.AreSame(snapshot.ProxyGroups, projected.ActiveController.ProxyGroups);
        Assert.AreEqual("en-US", projected.Language);
        Assert.AreEqual("dark", projected.Theme);
    }

    [TestMethod]
    public void NonRunningCoreProjectsAsDisconnectedWithoutInventingConfirmation()
    {
        RuntimeSnapshot snapshot = CreateSnapshot(
            new CoreStatus(
                CoreState.Failed,
                null,
                null,
                ProxyMode.Direct,
                0,
                0,
                0,
                0,
                0,
                0,
                "core failed"),
            []);
        AppSettings settings = new(
            ActiveConfigurationId: null,
            SystemProxyEnabled: true,
            TunEnabled: true);

        AppSnapshot projected = RuntimeSnapshotAdapter.ToAppSnapshot(snapshot, settings);

        Assert.AreEqual(EndpointSessionState.Disconnected, projected.ActiveController.State);
        Assert.IsNull(projected.ActiveController.LastConfirmedAt);
        Assert.IsTrue(projected.ActiveController.Capabilities.HasFlag(EndpointCapability.ControlSystemProxy));
        Assert.AreEqual(SystemProxyState.Off, projected.LocalDevice.SystemProxy);
        Assert.IsTrue(projected.LocalDevice.DesiredSystemProxyEnabled);
        Assert.AreEqual("core failed", projected.LocalDevice.ErrorMessage);
    }

    [TestMethod]
    public void ProjectionIncludesOnlyValidatedRemoteEndpointSummaries()
    {
        RuntimeSnapshot snapshot = CreateSnapshot(
            new CoreStatus(
                CoreState.Stopped,
                null,
                null,
                ProxyMode.Direct,
                0,
                0,
                0,
                0,
                0,
                0,
                null),
            []);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("HTTPS://Office.Example.Test:443"));

        AppSnapshot projected = RuntimeSnapshotAdapter.ToAppSnapshot(
            snapshot,
            new AppSettings(),
            endpoints: [
                remote,
                new EndpointDescriptor(
                    EndpointId.Local,
                    EndpointKind.Local,
                    "stale local",
                    new Uri("http://127.0.0.1:1/"),
                    EndpointTransportSecurity.Loopback)]);

        Assert.AreEqual(2, projected.Endpoints.Count);
        Assert.AreEqual(EndpointId.Local, projected.Endpoints[0].Id);
        Assert.AreEqual("http://127.0.0.1:9090/", projected.Endpoints[0].BaseUri.AbsoluteUri);
        Assert.AreEqual(new EndpointId("office"), projected.Endpoints[1].Id);
        Assert.AreEqual("https://office.example.test/", projected.Endpoints[1].BaseUri.AbsoluteUri);
    }

    [TestMethod]
    public void ProjectionRejectsDuplicateOrNonRemoteEndpointSummaries()
    {
        RuntimeSnapshot snapshot = CreateSnapshot(
            new CoreStatus(
                CoreState.Stopped,
                null,
                null,
                ProxyMode.Direct,
                0,
                0,
                0,
                0,
                0,
                0,
                null),
            []);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        Assert.ThrowsExactly<ArgumentException>(() => RuntimeSnapshotAdapter.ToAppSnapshot(
            snapshot,
            new AppSettings(),
            endpoints: [remote, remote]));
        Assert.ThrowsExactly<ArgumentException>(() => RuntimeSnapshotAdapter.ToAppSnapshot(
            snapshot,
            new AppSettings(),
            endpoints: [new EndpointDescriptor(
                new EndpointId("invalid"),
                EndpointKind.Local,
                "invalid",
                new Uri("http://127.0.0.1:9090/"),
                EndpointTransportSecurity.Loopback)]));
    }

    private static RuntimeSnapshot CreateSnapshot(
        CoreStatus core,
        IReadOnlyList<ConfigurationProfile> configurations) =>
        new(
            core,
            SystemProxyState.Off,
            TunState.Off,
            SubscriptionState.Idle,
            configurations,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null);
}

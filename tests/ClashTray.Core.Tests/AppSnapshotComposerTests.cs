using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class AppSnapshotComposerTests
{
    [TestMethod]
    public void RemoteControllerProjectionDoesNotChangeLocalDeviceState()
    {
        ConfigurationProfile localConfiguration = new(
            "home",
            "Home",
            "home.yaml",
            null,
            null,
            true);
        AppSettings settings = new(
            ActiveConfigurationId: localConfiguration.Id,
            SystemProxyEnabled: true,
            TunEnabled: true);
        RuntimeSnapshot local = CreateLocalSnapshot([localConfiguration]);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        ControllerSessionSnapshot remoteController = new(
            remote,
            EndpointSessionState.Connected,
            Generation: 8,
            LastConfirmedAt: DateTimeOffset.UtcNow,
            Status: new CoreStatus(
                CoreState.Running,
                "v1.19.30",
                "remote",
                ProxyMode.Global,
                1,
                2,
                3,
                4,
                5,
                6,
                null),
            ProxyGroups: [],
            ProxyNodes: [],
            Connections: [],
            Rules: [],
            Providers: [],
            RuleProviders: [],
            Logs: [],
            Capabilities: EndpointCapabilityDefaults.Remote,
            ErrorMessage: null);

        AppSnapshot projected = AppSnapshotComposer.Compose(
            local,
            settings,
            [
                ControllerEndpointFactory.CreateLocal(settings.ControllerPort),
                remote
            ],
            remoteController);

        Assert.IsTrue(projected.LocalDevice.Configurations.Single().IsActive);
        Assert.AreEqual(localConfiguration.Id, projected.LocalDevice.ActiveConfigurationId);
        Assert.AreEqual(SystemProxyState.Off, projected.LocalDevice.SystemProxy);
        Assert.AreEqual(EndpointId.Local, projected.Endpoints[0].Id);
        Assert.AreEqual(new EndpointId("office"), projected.ActiveController.Endpoint.Id);
        Assert.AreEqual(ProxyMode.Global, projected.ActiveController.Status!.Mode);
    }

    [TestMethod]
    public void ActiveControllerMustBeListedAndRemoteCapabilitiesAreMasked()
    {
        RuntimeSnapshot local = CreateLocalSnapshot([]);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        ControllerSessionSnapshot notListed = new(
            remote,
            EndpointSessionState.Connected,
            1,
            null,
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            EndpointCapabilityDefaults.Local,
            null);

        Assert.ThrowsExactly<ArgumentException>(() => AppSnapshotComposer.Compose(
            local,
            new AppSettings(),
            activeController: notListed));

        AppSnapshot masked = AppSnapshotComposer.Compose(
            local,
            new AppSettings(),
            endpoints: [ControllerEndpointFactory.CreateLocal(9090), remote],
            activeController: notListed);
        Assert.AreEqual(EndpointCapabilityDefaults.Remote, masked.ActiveController.Capabilities);
    }

    private static RuntimeSnapshot CreateLocalSnapshot(
        IReadOnlyList<ConfigurationProfile> configurations) =>
        new(
            new CoreStatus(
                CoreState.Running,
                "v1.19.30",
                "Home",
                ProxyMode.Rule,
                0,
                0,
                0,
                0,
                0,
                0,
                null),
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

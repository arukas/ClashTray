using ClashTray.App;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class PanelSnapshotContextTests
{
    [TestMethod]
    public void RemoteControllerStatusDoesNotReplaceLocalTrayOrNetworkFacts()
    {
        RuntimeSnapshot local = new(new(CoreState.Running, "local", null, ProxyMode.Rule, 0, 0, 0, 0, 0, 0, null),
            SystemProxyState.On, TunState.Off, SubscriptionState.Idle, [], [], [], [], [], [], [], [], null);
        AppSnapshot app = RuntimeSnapshotAdapter.ToAppSnapshot(local, new());
        EndpointDescriptor remote = new(new("remote"), EndpointKind.Remote, "Remote", new("https://example.test/"), EndpointTransportSecurity.HttpsSystemTrust);
        ControllerSessionSnapshot controller = app.ActiveController with
        {
            Endpoint = remote, Generation = 17, State = EndpointSessionState.Failed,
            Status = local.Core with { State = CoreState.Failed, Version = "remote" },
            Capabilities = EndpointCapabilityDefaults.Remote
        };
        PanelSnapshotContext context = PanelSnapshotContext.Create(app with { ActiveController = controller });

        Assert.AreEqual(TrayState.SystemProxy, TrayStateProjection.FromDevice(context.Device));
        Assert.AreSame(controller, context.Controller);
        Assert.AreEqual(CoreState.Failed, context.Pages.Core.State);
        Assert.AreEqual(SystemProxyState.On, context.Pages.SystemProxy);
        Assert.AreEqual(TunState.Off, context.Pages.Tun);
        Assert.AreEqual(new EndpointCommandTarget(remote.Id, 17), context.CommandTarget);
        Assert.AreEqual("remote:17", context.ControllerIdentity);
    }
}

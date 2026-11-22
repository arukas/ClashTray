using ClashTray.App;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ProxyPanelDataComparerTests
{
    [TestMethod]
    public void SameReferenceAndDeepValuesDoNotRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();

        Assert.IsTrue(ProxyPanelDataComparer.Equals(baseline, baseline));
        Assert.IsTrue(ProxyPanelDataComparer.Equals(baseline, CreateSnapshot()));
    }

    [TestMethod]
    public void NullPreviousSnapshotRequiresRendering()
    {
        Assert.IsFalse(ProxyPanelDataComparer.Equals(null, CreateSnapshot()));
    }

    [TestMethod]
    public void UnrelatedRuntimeChangesDoNotRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();
        RuntimeSnapshot changed = baseline with
        {
            Core = baseline.Core with { ConnectionCount = 99 }
        };

        Assert.IsTrue(ProxyPanelDataComparer.Equals(baseline, changed));
    }

    [TestMethod]
    public void GroupContentChangesRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();
        RuntimeSnapshot changed = baseline with
        {
            ProxyGroups = [baseline.ProxyGroups[0] with { Delay = "60" }]
        };

        Assert.IsFalse(ProxyPanelDataComparer.Equals(baseline, changed));
    }

    [TestMethod]
    public void MemberOrderChangesRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();
        RuntimeSnapshot changed = baseline with
        {
            ProxyGroups = [baseline.ProxyGroups[0] with { Members = ["node-b", "node-a"] }]
        };

        Assert.IsFalse(ProxyPanelDataComparer.Equals(baseline, changed));
    }

    [TestMethod]
    public void NodeProviderChangesRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();
        RuntimeSnapshot changed = baseline with
        {
            ProxyNodes = [baseline.ProxyNodes[0] with { Providers = ["other"] }]
        };

        Assert.IsFalse(ProxyPanelDataComparer.Equals(baseline, changed));
    }

    [TestMethod]
    public void ProviderMetadataChangesRequireRendering()
    {
        RuntimeSnapshot baseline = CreateSnapshot();
        RuntimeSnapshot changed = baseline with
        {
            Providers = [baseline.Providers[0] with { Count = 4 }]
        };

        Assert.IsFalse(ProxyPanelDataComparer.Equals(baseline, changed));
    }

    private static RuntimeSnapshot CreateSnapshot() => new(
        new CoreStatus(CoreState.Running, "v1", "config", ProxyMode.Rule, 0, 0, 0, 0, 0, 0, null, true, true),
        SystemProxyState.Off,
        TunState.Off,
        SubscriptionState.Idle,
        [],
        [new ProxyGroup("group", "Selector", "node-a", ["node-a", "node-b"], "34")],
        [new ProxyNode("node-a", "Shadowsocks", "34", true, ["provider"])],
        [],
        [],
        [new ProviderStatus("provider", "Proxy", "HTTP", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 3)],
        [],
        [],
        null);
}

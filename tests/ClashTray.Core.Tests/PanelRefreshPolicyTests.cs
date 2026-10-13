using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class PanelRefreshPolicyTests
{
    [TestMethod]
    public void HiddenPanelDefersOptionalReadsAndKeepsIndependentControllerDeadlines()
    {
        PollingClock clock = new();
        PanelRefreshPolicy policy = new(clock);
        Assert.IsTrue(policy.Set(false, ControllerPanelPage.Proxy));
        Assert.AreEqual(ControllerDataDemand.None, policy.GetImmediateDemand());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.AreEqual(ControllerDataDemand.None, policy.GetPollingDemand(EndpointKind.Local, true));
        clock.Advance(TimeSpan.FromSeconds(1));
        ControllerDataDemand background = ControllerDataDemand.Metrics | ControllerDataDemand.Connections;
        Assert.AreEqual(background, policy.GetPollingDemand(EndpointKind.Local, true));
        Assert.AreEqual(background, policy.GetPollingDemand(EndpointKind.Remote, true));
        Assert.AreEqual(ControllerDataDemand.None, policy.GetPollingDemand(EndpointKind.Local, true));
        Assert.IsFalse(policy.Set(false, ControllerPanelPage.Proxy));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(background, policy.GetPollingDemand(EndpointKind.Local, true));
    }

    [TestMethod]
    public void SwitchingAndReopeningRequestsCurrentPageImmediately()
    {
        PanelRefreshPolicy policy = new(new PollingClock());
        policy.Set(false, ControllerPanelPage.Rules);
        Assert.IsTrue(policy.Set(true, ControllerPanelPage.Rules));
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections | ControllerDataDemand.RulesAndProviders,
            policy.GetImmediateDemand());
        policy.Set(true, ControllerPanelPage.Connections);
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections, policy.GetImmediateDemand());
        policy.Set(false, ControllerPanelPage.Connections);
        Assert.AreEqual(ControllerDataDemand.None, policy.GetImmediateDemand());
        policy.Set(true, ControllerPanelPage.Proxy);
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections | ControllerDataDemand.Proxies | ControllerDataDemand.Providers,
            policy.GetImmediateDemand());
    }

    [TestMethod]
    public void VisibleRulePageRefreshesMetricsEveryPollAndLargeListsEveryTenSeconds()
    {
        PollingClock clock = new();
        PanelRefreshPolicy policy = new(clock);
        policy.Set(true, ControllerPanelPage.Rules);
        ControllerDataDemand fullPage = ControllerDataDemand.Metrics | ControllerDataDemand.Connections | ControllerDataDemand.RulesAndProviders;
        Assert.AreEqual(fullPage, policy.GetPollingDemand(EndpointKind.Local, true));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(ControllerDataDemand.Metrics, policy.GetPollingDemand(EndpointKind.Local, true));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.AreEqual(fullPage, policy.GetPollingDemand(EndpointKind.Local, true));
        policy.Set(true, ControllerPanelPage.Settings);
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections | ControllerDataDemand.Providers,
            policy.GetImmediateDemand());
        policy.Set(true, ControllerPanelPage.Logs);
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections, policy.GetImmediateDemand());
    }

    [TestMethod]
    public void InactiveLocalControllerDoesNotFetchRemotePageLists()
    {
        PollingClock clock = new();
        PanelRefreshPolicy policy = new(clock);
        policy.Set(true, ControllerPanelPage.Proxy);
        Assert.AreEqual(ControllerDataDemand.None, policy.GetPollingDemand(EndpointKind.Local, false));
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Connections | ControllerDataDemand.Proxies | ControllerDataDemand.Providers,
            policy.GetPollingDemand(EndpointKind.Remote, true));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(ControllerDataDemand.Metrics | ControllerDataDemand.Proxies,
            policy.GetPollingDemand(EndpointKind.Remote, true));
    }

    [TestMethod]
    public void HeadlessHostRetainsExistingPollingAndInvalidPageIsRejected()
    {
        PanelRefreshPolicy policy = new(new PollingClock());
        Assert.AreEqual(ControllerDataDemand.All, policy.GetImmediateDemand());
        Assert.AreEqual(ControllerDataDemand.All & ~ControllerDataDemand.RulesAndProviders,
            policy.GetPollingDemand(EndpointKind.Local, true));
        Assert.AreEqual(ControllerDataDemand.All, policy.GetPollingDemand(EndpointKind.Remote, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => policy.Set(true, (ControllerPanelPage)99));
    }

    internal sealed class PollingClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}

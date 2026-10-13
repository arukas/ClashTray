using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class NetworkContextSnapshotFactoryTests
{
    [TestMethod]
    public void EmptyConnectionsProduceAnUnstableDisconnectedSnapshot()
    {
        DateTimeOffset observed = new DateTimeOffset(2026, 9, 13, 1, 2, 3, TimeSpan.FromHours(8));

        NetworkContextSnapshot snapshot = NetworkContextSnapshotFactory.Create(4, observed, []);

        Assert.AreEqual(4, snapshot.Revision);
        Assert.AreEqual(observed.ToUniversalTime(), snapshot.ObservedAtUtc);
        Assert.AreEqual(NetworkConnectivityKind.None, snapshot.ConnectivityKind);
        Assert.IsFalse(snapshot.IsStable);
        Assert.IsNull(snapshot.CurrentSsid);
        Assert.IsNull(snapshot.InterfaceIdentity);
    }

    [TestMethod]
    public void OneWifiConnectionPreservesSsidAndInterfaceIdentity()
    {
        NetworkContextSnapshot snapshot = NetworkContextSnapshotFactory.Create(
            7,
            DateTimeOffset.UtcNow,
            [new NetworkConnectionObservation(NetworkConnectivityKind.WiFi, "Home WiFi", "wifi-1")]);

        Assert.AreEqual(NetworkConnectivityKind.WiFi, snapshot.ConnectivityKind);
        Assert.AreEqual("Home WiFi", snapshot.CurrentSsid);
        Assert.AreEqual("wifi-1", snapshot.InterfaceIdentity);
        Assert.IsFalse(snapshot.IsAmbiguous);
        Assert.IsTrue(snapshot.IsStable);
        Assert.AreEqual(NetworkPermissionState.Allowed, snapshot.PermissionState);
    }

    [TestMethod]
    public void MultipleWifiConnectionsAreAmbiguousAndDoNotExposeAnSsid()
    {
        NetworkContextSnapshot snapshot = NetworkContextSnapshotFactory.Create(
            8,
            DateTimeOffset.UtcNow,
            [
                new NetworkConnectionObservation(NetworkConnectivityKind.WiFi, "Home", "wifi-1"),
                new NetworkConnectionObservation(NetworkConnectivityKind.WiFi, "Office", "wifi-2")
            ]);

        Assert.AreEqual(NetworkConnectivityKind.WiFi, snapshot.ConnectivityKind);
        Assert.IsTrue(snapshot.IsAmbiguous);
        Assert.IsTrue(snapshot.IsStable);
        Assert.IsNull(snapshot.CurrentSsid);
        Assert.IsNull(snapshot.InterfaceIdentity);
    }

    [TestMethod]
    public void WifiTakesPrecedenceWhenEthernetIsAlsoActive()
    {
        NetworkContextSnapshot snapshot = NetworkContextSnapshotFactory.Create(
            9,
            DateTimeOffset.UtcNow,
            [
                new NetworkConnectionObservation(NetworkConnectivityKind.Ethernet, null, "ethernet-1"),
                new NetworkConnectionObservation(NetworkConnectivityKind.WiFi, "Home", "wifi-1")
            ]);

        Assert.AreEqual(NetworkConnectivityKind.WiFi, snapshot.ConnectivityKind);
        Assert.AreEqual("Home", snapshot.CurrentSsid);
        Assert.IsFalse(snapshot.IsAmbiguous);
    }

    [TestMethod]
    public void PermissionAndErrorInformationArePreservedForPolicy()
    {
        NetworkContextSnapshot snapshot = NetworkContextSnapshotFactory.Create(
            10,
            DateTimeOffset.UtcNow,
            [new NetworkConnectionObservation(NetworkConnectivityKind.WiFi, null, "wifi-1")],
            NetworkPermissionState.Unavailable,
            "Windows 网络状态 API 当前不可用。");
        NetworkSwitchPolicyInput input = new(
            true,
            snapshot,
            [new NetworkSwitchRule("home", "Home", "home")],
            null,
            null,
            new HashSet<string>(["home"], StringComparer.OrdinalIgnoreCase));

        NetworkSwitchDecision decision = NetworkSwitchPolicyEngine.Evaluate(input);

        Assert.AreEqual(NetworkSwitchState.PermissionRequired, decision.State);
        Assert.AreEqual(NetworkSwitchReason.PermissionRequired, decision.Reason);
        Assert.AreEqual(snapshot.ErrorMessage, decision.Message);
    }
}

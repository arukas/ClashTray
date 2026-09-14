using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeNetworkSwitchTests
{
    [TestMethod]
    public async Task InitializeLoadsNetworkSwitchRulesIntoSnapshotWithoutNetworkSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new(paths);
        await store.SaveAsync(new NetworkSwitchRuleSet(
            true,
            "fallback",
            [new NetworkSwitchRule("home-rule", "Home", "home")]));
        await using ClashTrayRuntime runtime = new(paths);

        try
        {
            await runtime.InitializeAsync();

            Assert.IsNotNull(runtime.Snapshot.NetworkSwitch);
            Assert.IsFalse(runtime.Snapshot.NetworkSwitch!.Available);
            Assert.AreEqual(NetworkSwitchState.WaitingForNetwork, runtime.Snapshot.NetworkSwitch.State);
            Assert.IsTrue(runtime.NetworkSwitchRules.AutomaticSwitchingEnabled);
            Assert.AreEqual("fallback", runtime.NetworkSwitchRules.DefaultConfigurationId);
            Assert.AreEqual("Home", runtime.NetworkSwitchRules.Rules[0].Ssid);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

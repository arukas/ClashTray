using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class NetworkRuleStoreTests
{
    [TestMethod]
    public async Task RoundTripProtectsSsidAndPreservesRuleValues()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new NetworkRuleStore(paths);
        NetworkSwitchRuleSet expected = new(
            true,
            "fallback",
            [
                new NetworkSwitchRule("home-rule", "Home WiFi", "home"),
                new NetworkSwitchRule("office-rule", "Office WiFi", "office", false)
            ]);

        try
        {
            await store.SaveAsync(expected);
            string persisted = await File.ReadAllTextAsync(paths.NetworkRulesFile);
            NetworkRuleStoreLoadResult loaded = await store.LoadAsync();

            Assert.IsFalse(persisted.Contains("Home WiFi", StringComparison.Ordinal));
            Assert.IsFalse(persisted.Contains("Office WiFi", StringComparison.Ordinal));
            Assert.AreEqual(NetworkRuleStoreLoadStatus.Loaded, loaded.Status);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.AreEqual(expected.AutomaticSwitchingEnabled, loaded.Rules.AutomaticSwitchingEnabled);
            Assert.AreEqual(expected.DefaultConfigurationId, loaded.Rules.DefaultConfigurationId);
            CollectionAssert.AreEqual(expected.Rules.ToArray(), loaded.Rules.Rules.ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ExactDuplicateEnabledSsidIsRejectedButCaseVariantRemainsDistinct()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new NetworkRuleStore(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new NetworkSwitchRuleSet(
                true,
                null,
                [
                    new NetworkSwitchRule("first", "Home", "one"),
                    new NetworkSwitchRule("second", "Home", "two")
                ])));

            await store.SaveAsync(new NetworkSwitchRuleSet(
                true,
                null,
                [
                    new NetworkSwitchRule("upper", "Home", "one"),
                    new NetworkSwitchRule("lower", "home", "two")
                ]));
            NetworkRuleStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(2, loaded.Rules.Rules.Count);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CorruptStoreIsQuarantinedAndAutomaticSwitchingDefaultsOff()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.NetworkRulesFile, "{not-json");
        NetworkRuleStore store = new NetworkRuleStore(paths);

        try
        {
            NetworkRuleStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(NetworkRuleStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.IsFalse(loaded.Rules.AutomaticSwitchingEnabled);
            Assert.IsFalse(File.Exists(paths.NetworkRulesFile));
            Assert.AreEqual(
                1,
                Directory.GetFiles(paths.LocalRoot, "network-rules.json.corrupt-*").Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InvalidProtectedSsidIsQuarantinedWithoutLeakingPlaintext()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.NetworkRulesFile,
            "{\"schemaVersion\":1,\"automaticSwitchingEnabled\":true,\"rules\":[{\"ruleId\":\"rule\",\"protectedSsid\":\"not-base64\",\"configurationId\":\"home\",\"enabled\":true}]}" );
        NetworkRuleStore store = new NetworkRuleStore(paths);

        try
        {
            NetworkRuleStoreLoadResult loaded = await store.LoadAsync();

            Assert.AreEqual(NetworkRuleStoreLoadStatus.Recovered, loaded.Status);
            Assert.IsTrue(loaded.WasQuarantined);
            Assert.IsFalse(loaded.Rules.AutomaticSwitchingEnabled);
            Assert.IsFalse(loaded.Message!.Contains("not-base64", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task OversizedRuleIdentifiersAndTargetsAreRejectedBeforeWriting()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        NetworkRuleStore store = new NetworkRuleStore(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new NetworkSwitchRuleSet(
                true,
                new string('d', 129),
                [new NetworkSwitchRule("rule", "Home", "home")])));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new NetworkSwitchRuleSet(
                true,
                null,
                [new NetworkSwitchRule(new string('r', 129), "Home", "home")])));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(new NetworkSwitchRuleSet(
                true,
                null,
                [new NetworkSwitchRule("rule", "Home", new string('c', 129))])));

            Assert.IsFalse(File.Exists(paths.NetworkRulesFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

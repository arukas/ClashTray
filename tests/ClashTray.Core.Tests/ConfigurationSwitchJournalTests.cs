using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationSwitchJournalTests
{
    [TestMethod]
    public async Task JournalRoundTripsAndClearsAtomically()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationSwitchJournalStore store = new ConfigurationSwitchJournalStore(paths);
        ConfigurationSwitchJournal expected = ConfigurationSwitchJournal.Create(
            ConfigurationSwitchSource.Manual,
            "old",
            "new",
            previousCoreWasRunning: true,
            previousSystemProxyPreference: true,
            SystemProxyState.On,
            previousTunPreference: false,
            TunState.Off,
            previousControllerGeneration: 12).WithStage(ConfigurationSwitchStage.RuntimePromoted);

        try
        {
            await store.SaveAsync(expected);

            ConfigurationSwitchJournalLoadResult loaded = await store.LoadAsync();

            Assert.IsNotNull(loaded.Journal);
            Assert.IsFalse(loaded.WasQuarantined);
            Assert.IsNull(loaded.Message);
            Assert.AreEqual(expected, loaded.Journal);

            await store.ClearAsync();

            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.IsNull((await store.LoadAsync()).Journal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CorruptJournalIsQuarantinedWithoutBlockingLocalStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationSwitchJournalStore store = new ConfigurationSwitchJournalStore(paths);

        try
        {
            await File.WriteAllTextAsync(paths.ConfigurationSwitchJournalFile, "{ not valid json");

            ConfigurationSwitchJournalLoadResult result = await store.LoadAsync();

            Assert.IsNull(result.Journal);
            Assert.IsTrue(result.WasQuarantined);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.AreEqual(
                1,
                Directory.EnumerateFiles(paths.LocalRoot, "configuration-switch-journal.json.corrupt-*").Count());
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

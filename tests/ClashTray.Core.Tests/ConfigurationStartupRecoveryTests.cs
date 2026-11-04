using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationStartupRecoveryTests
{
    [TestMethod]
    public async Task MissingContentStopsTheObservedCoreAndRetainsRecoveryEvidence()
    {
        await VerifyRecoveryAsync(contentRestored: false, selectionRestored: true, healthy: true, "stop", clearsJournal: false);
    }

    [TestMethod]
    public async Task FailedSelectionDoesNotRestartTheCoreOrClearItsJournal()
    {
        await VerifyRecoveryAsync(contentRestored: true, selectionRestored: false, healthy: true, string.Empty, clearsJournal: false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalIsClearedOnlyAfterTheRestoredCorePassesHealthConfirmation(bool healthy)
    {
        await VerifyRecoveryAsync(contentRestored: true, selectionRestored: true, healthy, "restart", clearsJournal: healthy);
    }

    private static async Task VerifyRecoveryAsync(bool contentRestored, bool selectionRestored, bool healthy, string expectedCalls, bool clearsJournal)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "user"), Path.Combine(root, "service"));
            paths.EnsureDirectories();
            ConfigurationSwitchJournalStore journals = new(paths);
            ConfigurationSwitchJournal journal = ConfigurationSwitchJournal.Create(
                ConfigurationSwitchSource.Manual, "previous", "candidate", true, false, SystemProxyState.Off, false, TunState.Off, 1);
            await journals.SaveAsync(journal);
            List<string> calls = [];
            Task Record(string name) { calls.Add(name); return Task.CompletedTask; }
            ConfigurationStartupRecovery recovery = new(new ConfigurationStore(paths), journals,
                new SettingsCommitCoordinator(new SettingsStore(paths), new SettingsRecoveryJournal(paths)),
                new(_ => Record("start"), _ => Record("stop"), _ => Record("restart"), () => healthy));

            ConfigurationRecoveryState result = await recovery.CompleteAsync(
                new(journal, contentRestored, selectionRestored, null), CoreState.Running, CancellationToken.None);

            Assert.AreEqual(expectedCalls, string.Join(',', calls));
            Assert.AreEqual(clearsJournal, result.Journal is null);
            Assert.AreEqual(!clearsJournal, File.Exists(paths.ConfigurationSwitchJournalFile));
        });
    }
}

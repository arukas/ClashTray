using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationSettingsRecoveryTests
{
    [TestMethod]
    public async Task AppRestartCoordinatesBothJournalsWithoutRestoringOldNetworkPreferences()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            NetworkDisableRecoveryTests.RecoveryStore store = new();
            string source = Path.Combine(root, "source.yaml");
            await File.WriteAllTextAsync(source, "proxies: []\nproxy-groups: []\nrules: []\n");
            ConfigurationProfile first;
            ConfigurationProfile second;
            await using (ClashTrayRuntime seed = Create(paths, store))
            {
                first = await seed.ImportLocalConfigurationAsync(source, "first");
                second = await seed.ImportLocalConfigurationAsync(source, "second");
            }
            AppSettings previous = store.Settings with { SystemProxyEnabled = true, TunEnabled = true };
            SettingsRecoveryJournal settingsJournal = new(paths);
            await settingsJournal.PrepareAsync(previous, previous with { AllowLan = true }, CancellationToken.None);
            await settingsJournal.WriteNetworkDisableIntentAsync(new(SystemProxyOff: true, TunOff: true), CancellationToken.None);
            store.Settings = previous with { AllowLan = true, Theme = "dark" };
            store.FailNextSaves = 1;
            await new ConfigurationSwitchJournalStore(paths).SaveAsync(ConfigurationSwitchJournal.Create(
                ConfigurationSwitchSource.Manual, first.Id, second.Id, false, true, SystemProxyState.On, true, TunState.On, 1)
                .WithStage(ConfigurationSwitchStage.RuntimePromoted));
            await using ClashTrayRuntime reopened = Create(paths, store);
            await reopened.InitializeAsync();
            Assert.AreEqual(first.Id, store.Settings.ActiveConfigurationId);
            Assert.AreEqual("dark", store.Settings.Theme);
            Assert.IsFalse(store.Settings.AllowLan);
            Assert.IsFalse(store.Settings.SystemProxyEnabled);
            Assert.IsFalse(store.Settings.TunEnabled);
            Assert.IsFalse(settingsJournal.Exists);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        });
    }

    [TestMethod]
    public async Task UnsupportedLegacyRecoverySchemaIsReportedAndRetained()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            await File.WriteAllTextAsync(paths.SettingsRecoveryFile, System.Text.Json.JsonSerializer.Serialize(new SettingsRecoveryRecord(99, new(), new(Theme: "dark")), System.Text.Json.JsonSerializerOptions.Web));
            NetworkDisableRecoveryTests.RecoveryStore store = new();
            await using ClashTrayRuntime runtime = Create(paths, store);
            await runtime.InitializeAsync();
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "版本不兼容", StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(paths.SettingsRecoveryFile));
            Assert.AreEqual(0, store.SaveCount);
        });
    }
    [TestMethod]
    [DataRow("switch")]
    [DataRow("delete")]
    [DataRow("reload")]
    public async Task PendingRecoveryThenConfigurationOperationAndAppRestartPreserveSelection(string operation)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            NetworkDisableRecoveryTests.RecoveryStore store = new();
            string source = Path.Combine(root, "source.yaml");
            await File.WriteAllTextAsync(source, "proxies: []\nproxy-groups: []\nrules: []\n");
            ConfigurationProfile first;
            ConfigurationProfile second;
            await using (ClashTrayRuntime seed = Create(paths, store))
            {
                first = await seed.ImportLocalConfigurationAsync(source, "first");
                second = await seed.ImportLocalConfigurationAsync(source, "second");
            }
            AppSettings previous = store.Settings;
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(previous, previous with { SystemProxyEnabled = true, TunEnabled = true }, CancellationToken.None);
            store.Settings = previous with { SystemProxyEnabled = true, TunEnabled = true };
            store.FailNextSaves = 1;
            string? expected = operation == "switch" ? first.Id : operation == "delete" ? null : second.Id;
            await using (ClashTrayRuntime runtime = Create(paths, store))
            {
                await runtime.InitializeAsync();
                Assert.IsTrue(journal.Exists);
                if (operation == "switch") { await runtime.SetActiveConfigurationAsync(first.Id); }
                else if (operation == "delete") { await runtime.DeleteConfigurationAsync(second); }
                else { await runtime.ReloadConfigurationAsync(second); }
                await runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
                Assert.AreEqual(expected, runtime.Settings.ActiveConfigurationId);
                Assert.IsFalse(runtime.Settings.SystemProxyEnabled);
                Assert.IsFalse(runtime.Settings.TunEnabled);
            }
            await using ClashTrayRuntime reopened = Create(paths, store);
            await reopened.InitializeAsync();
            Assert.AreEqual(expected, store.Settings.ActiveConfigurationId);
            Assert.AreEqual(expected, reopened.Settings.ActiveConfigurationId);
            Assert.AreEqual("dark", reopened.Settings.Theme);
            Assert.IsFalse(journal.Exists);
        });
    }

    [TestMethod]
    public async Task LegacySchemaOneRestoresOnlyChangedFieldsAndPreservesUnrelatedExternalValues()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            AppSettings previous = new(ActiveConfigurationId: "old-selection");
            AppSettings attempted = previous with { SystemProxyEnabled = true, TunEnabled = true };
            SettingsRecoveryJournal journal = new(paths);
            // The old wire representation has no new scope/migration fields.
            await File.WriteAllTextAsync(paths.SettingsRecoveryFile, System.Text.Json.JsonSerializer.Serialize(new SettingsRecoveryRecord(1, previous, attempted), System.Text.Json.JsonSerializerOptions.Web));
            NetworkDisableRecoveryTests.RecoveryStore store = new()
            {
                Settings = attempted with { ActiveConfigurationId = "new-selection", Theme = "dark", LogLevel = "debug", Ipv6 = true }
            };
            await using ClashTrayRuntime runtime = Create(paths, store);
            await runtime.InitializeAsync();
            Assert.AreEqual("new-selection", store.Settings.ActiveConfigurationId);
            Assert.AreEqual("dark", store.Settings.Theme);
            Assert.AreEqual("debug", store.Settings.LogLevel);
            Assert.IsTrue(store.Settings.Ipv6);
            Assert.IsFalse(store.Settings.SystemProxyEnabled);
            Assert.IsFalse(store.Settings.TunEnabled);
            Assert.IsFalse(journal.Exists);
        });
    }

    [TestMethod]
    public async Task RealSameFieldExternalConflictRetainsJournalAndValues()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(new(Theme: "light"), new(Theme: "dark"), CancellationToken.None);
            NetworkDisableRecoveryTests.RecoveryStore store = new() { Settings = new(Theme: "system", ActiveConfigurationId: "external-selection") };
            await using ClashTrayRuntime runtime = Create(paths, store);
            await runtime.InitializeAsync();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(LogLevel: SettingPatchValue.Set("debug"))));
            Assert.AreEqual("system", store.Settings.Theme);
            Assert.AreEqual("external-selection", store.Settings.ActiveConfigurationId);
            Assert.AreEqual("info", store.Settings.LogLevel);
            Assert.IsTrue(journal.Exists);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "其他入口", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public async Task ConfigurationCommitFailureRollsBackOnlySelectionAndRetainsDisableIntent()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            NetworkDisableRecoveryTests.RecoveryStore store = new();
            await using ClashTrayRuntime runtime = Create(paths, store);
            string source = Path.Combine(root, "source.yaml");
            await File.WriteAllTextAsync(source, "proxies: []\nproxy-groups: []\nrules: []\n");
            ConfigurationProfile first = await runtime.ImportLocalConfigurationAsync(source, "first");
            ConfigurationProfile second = await runtime.ImportLocalConfigurationAsync(source, "second");
            await runtime.SetSystemProxyAsync(false);
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            store.FailNextSaves = 1;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetActiveConfigurationAsync(first.Id));
            Assert.AreEqual(second.Id, store.Settings.ActiveConfigurationId);
            Assert.AreEqual(second.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual("dark", store.Settings.Theme);
            Assert.IsFalse(store.Settings.SystemProxyEnabled);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.IsTrue(File.Exists(paths.SettingsNetworkOffFile));
        });
    }

    private static ClashTrayRuntime Create(AppPaths paths, NetworkDisableRecoveryTests.RecoveryStore store) =>
        new(paths, null, null, store, new FakeSystemProxyController(SystemProxyState.Off), new AcceptingCandidateValidator());
    [TestMethod]
    public async Task PendingRecoveryThenImportAndSettingsSavePreserveNewSelection()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(new(), new(SystemProxyEnabled: true), CancellationToken.None);
            NetworkDisableRecoveryTests.RecoveryStore store = new() { Settings = new(SystemProxyEnabled: true), FailNextSaves = 1 };
            await using ClashTrayRuntime runtime = new(paths, null, null, store, new FakeSystemProxyController(SystemProxyState.Off), new AcceptingCandidateValidator());
            await runtime.InitializeAsync();
            Assert.IsTrue(journal.Exists, "Injected startup write failure retains the recovery record.");
            string config = Path.Combine(root, "import.yaml");
            await File.WriteAllTextAsync(config, "proxies: []\nproxy-groups: []\nrules: []\n");
            ConfigurationProfile imported = await runtime.ImportLocalConfigurationAsync(config);
            Assert.AreEqual(imported.Id, store.Settings.ActiveConfigurationId);
            Console.WriteLine($"R3 before next settings write: selected={store.Settings.ActiveConfigurationId == imported.Id}, journal={journal.Exists}");
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            Assert.AreEqual(imported.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(imported.Id, store.Settings.ActiveConfigurationId);
            Assert.IsFalse(store.Settings.SystemProxyEnabled);
            Assert.AreEqual("dark", store.Settings.Theme);
            Assert.IsFalse(journal.Exists);
        });
    }
}

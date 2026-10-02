using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using ClashTray.Testing;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSubscriptionSwitchTests
{
    private static readonly string[] SelectionThenClose = ["select", "close"];

    [TestMethod]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Runtime owns and disposes the injected file-lease source; it deterministically verifies cleanup ordering.")]
    public async Task SubscriptionSuccessIsPublishedOnlyAfterSwitchCompletes()
    {
        string root = TestFixtureDirectory.Create();
        Console.WriteLine($"Fixture lifetime root: {root}");
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        byte[] body = "port: 17890\nsocks-port: 17891\nmode: rule\nlog-level: info\n"u8.ToArray();
        using LoopbackSubscriptionServer server = new(body);
        Uri subscriptionUri = new($"http://127.0.0.1:{server.Port}/subscription.yaml");
        ConfigurationStore setupStore = new(paths);
        ConfigurationProfile imported = await setupStore.ImportSubscriptionAsync(subscriptionUri, "sub");
        TestSettingsStore settings = new TestSettingsStore(new AppSettings(ActiveConfigurationId: "other-config"));
        Directory.CreateDirectory(root);
        DisposalFileLeaseSource fileLease = new(Path.Combine(root, "candidate-lifetime.tmp"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(
            paths, null, null, settings, null, new FailingCandidateValidator(), networkContextSource: fileLease);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            List<SubscriptionState> transitions = new();
            object gate = new();
            runtime.SnapshotChanged += (_, snapshot) =>
            {
                lock (gate)
                {
                    if (transitions.Count == 0 || transitions[^1] != snapshot.Subscription)
                    {
                        transitions.Add(snapshot.Subscription);
                    }
                }
            };

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => runtime.RefreshSubscriptionAsync(imported with { IsActive = true }));

            lock (gate)
            {
                CollectionAssert.Contains(transitions, SubscriptionState.Applying);
                CollectionAssert.Contains(transitions, SubscriptionState.Failed);
                Assert.IsFalse(
                    transitions.Contains(SubscriptionState.Succeeded),
                    "Succeeded must not be published before the configuration switch commits.");
            }
        }, async () =>
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    private sealed class DisposalFileLeaseSource(string path) : INetworkContextSource
    {
        private readonly FileStream _lease = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        public event EventHandler<NetworkContextSnapshot>? ContextChanged { add { } remove { } }
        public Task<NetworkContextSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkContextSnapshot(0, DateTimeOffset.UtcNow, NetworkConnectivityKind.None, null, null, NetworkPermissionState.Unavailable, false, false));
        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    [TestMethod]
    public async Task SwitchingProxyOptionDisconnectsOnlyAfterSuccessfulSelection()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using ProxySwitchHandler handler = new ProxySwitchHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(DisconnectConnectionsAfterProxySwitch: SettingPatchValue.Set(true)));
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();

            await runtime.SelectProxyAsync("Auto", "new");

            CollectionAssert.AreEqual(SelectionThenClose, handler.Operations.Take(2).ToArray());
            Assert.AreEqual("new", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "Auto").Current);
        }, async () =>
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task FailedDisconnectReportsPartialSuccessWithoutChangingSelection()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using ProxySwitchHandler handler = new ProxySwitchHandler { FailCloseAll = true };
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(DisconnectConnectionsAfterProxySwitch: SettingPatchValue.Set(true)));
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.RefreshControllerDataForTestingAsync();

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                runtime.SelectProxyAsync("Auto", "new"));

            Assert.AreEqual("节点已切换，但未能断开旧连接。", exception.Message);
            CollectionAssert.AreEqual(SelectionThenClose, handler.Operations.Take(2).ToArray());
            Assert.AreEqual("new", runtime.Snapshot.ProxyGroups.Single(group => group.Name == "Auto").Current);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "节点已切换", StringComparison.Ordinal);
            Assert.IsTrue(runtime.Snapshot.Logs.Any(log => log.Message.Contains("未能断开旧连接", StringComparison.Ordinal)));
        }, async () =>
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task RuntimeInitializationKeepsOnlyOneActiveConfiguration()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "first.yaml"),
            "first");
        ConfigurationProfile second = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "second.yaml"),
            "second");
        await RuntimeTestHelpers.WriteMetadataAsync(paths, first with { IsActive = true });
        await RuntimeTestHelpers.WriteMetadataAsync(paths, second with { IsActive = true });

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);
            await runtime.InitializeAsync();

            Assert.AreEqual(1, runtime.Snapshot.Configurations.Count(configuration => configuration.IsActive));
            Assert.AreEqual(first.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
        }, async () =>
        {
            await TestFixtureDirectory.DeleteAsync(root);
        });
    }

    [TestMethod]
    public async Task SelectingConfigurationWhileCoreIsStoppedCommitsWithoutStartingCore()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        Directory.CreateDirectory(paths.CoreRoot);
        File.Copy(Environment.ProcessPath!, paths.ManagedCoreExecutable);
        ManagedCoreMetadata metadata = new(
            "v0.0.0-test",
            new Uri("https://example.com/mihomo.zip"),
            new string('0', 64),
            new string('0', 64));
        await File.WriteAllTextAsync(paths.ManagedCoreMetadata, JsonSerializer.Serialize(metadata));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "first.yaml"),
            "first");
        ConfigurationProfile second = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "second.yaml"),
            "second");
        TestSettingsStore settings = new TestSettingsStore(
            new AppSettings(ActiveConfigurationId: first.Id));

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(
                paths,
                null,
                null,
                settings,
                candidateValidator: new AcceptingCandidateValidator());
            await runtime.InitializeAsync();

            await runtime.SetActiveConfigurationAsync(second.Id);

            Assert.AreEqual(second.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(CoreState.Stopped, runtime.Snapshot.Core.State);
            Assert.AreEqual("second", runtime.Snapshot.Core.ConfigurationName);
            Assert.AreEqual(second.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(1, settings.SaveCount);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task SelectingUnknownConfigurationDoesNotPersistOrChangeActiveSelection()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "first.yaml"),
            "first");
        TestSettingsStore settings = new TestSettingsStore(
            new AppSettings(ActiveConfigurationId: first.Id));

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings);
            await runtime.InitializeAsync();

            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
                runtime.SetActiveConfigurationAsync("missing-configuration"));

            Assert.AreEqual(first.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(first.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(0, settings.SaveCount);
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task FailedCandidateValidationDoesNotPersistOrChangeActiveSelection()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "first.yaml"),
            "first");
        ConfigurationProfile second = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "second.yaml"),
            "second");
        TestSettingsStore settings = new TestSettingsStore(
            new AppSettings(ActiveConfigurationId: first.Id));

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(
                paths,
                null,
                null,
                settings,
                candidateValidator: new RejectingCandidateValidator());
            await runtime.InitializeAsync();

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                runtime.SetActiveConfigurationAsync(second.Id));

            Assert.AreEqual(first.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(first.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(0, settings.SaveCount);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task StartupRecoversIncompleteSwitchBeforeUsingCandidateConfiguration()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile first = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "first.yaml"),
            "first");
        ConfigurationProfile second = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "second.yaml"),
            "second");
        TestSettingsStore settings = new TestSettingsStore(
            new AppSettings(ActiveConfigurationId: second.Id));
        ConfigurationSwitchJournalStore journalStore = new ConfigurationSwitchJournalStore(paths);
        ConfigurationSwitchJournal journal = ConfigurationSwitchJournal.Create(
            Guid.NewGuid(),
            ConfigurationSwitchSource.Manual,
            first.Id,
            second.Id,
            previousCoreWasRunning: false,
            previousSystemProxyPreference: false,
            previousSystemProxyState: SystemProxyState.Off,
            previousTunPreference: false,
            previousTunState: TunState.Unavailable,
            previousControllerGeneration: 0).WithStage(ConfigurationSwitchStage.RuntimePromoted);
        await journalStore.SaveAsync(journal);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings);
            await runtime.InitializeAsync();

            Assert.AreEqual(first.Id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(first.Id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(1, settings.SaveCount);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task StartupRestoresPersistedSubscriptionBackupBeforeCompletingRecovery()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        string id = "0123456789abcdef";
        string configurationPath = Path.Combine(paths.ConfigurationsRoot, $"{id}.yaml");
        ConfigurationProfile profile = new(
            id,
            "subscription",
            configurationPath,
            new Uri("https://subscription.invalid/config"),
            DateTimeOffset.UtcNow,
            false);
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
        await RuntimeTestHelpers.WriteMetadataAsync(paths, profile);
        ConfigurationProfileBackup backup = await store.CaptureBackupAsync(profile);
        Guid backupId = Guid.NewGuid();
        await store.SavePersistentBackupAsync(backupId, profile, backup);
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7891\n");
        await RuntimeTestHelpers.WriteMetadataAsync(paths, profile with { LastRefreshed = DateTimeOffset.UtcNow.AddMinutes(1) });

        TestSettingsStore settings = new TestSettingsStore(
            new AppSettings(ActiveConfigurationId: id));
        ConfigurationSwitchJournalStore journalStore = new ConfigurationSwitchJournalStore(paths);
        ConfigurationSwitchJournal journal = ConfigurationSwitchJournal.Create(
            Guid.NewGuid(),
            ConfigurationSwitchSource.SubscriptionRefresh,
            id,
            id,
            previousCoreWasRunning: false,
            previousSystemProxyPreference: false,
            previousSystemProxyState: SystemProxyState.Off,
            previousTunPreference: false,
            previousTunState: TunState.Unavailable,
            previousControllerGeneration: 0)
            .WithContentBackup(backupId)
            .WithStage(ConfigurationSwitchStage.RuntimePromoted);
        await journalStore.SaveAsync(journal);

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings);
            await runtime.InitializeAsync();

            Assert.AreEqual("mixed-port: 7890\n", await File.ReadAllTextAsync(configurationPath));
            Assert.AreEqual(id, runtime.Settings.ActiveConfigurationId);
            Assert.AreEqual(id, runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Id);
            Assert.AreEqual(1, settings.SaveCount);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.IsFalse(Directory.EnumerateFiles(paths.ConfigurationSwitchBackupsRoot).Any());
        }, async () =>
        {
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task ReimportingActiveConfigurationRefreshesSnapshotAfterNoOpSelection()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        string source = await RuntimeTestHelpers.WriteConfigAsync(root, "active.yaml");
        ConfigurationProfile initial = await store.ImportLocalAsync(source, "old name");
        TestSettingsStore settings = new(new AppSettings(ActiveConfigurationId: initial.Id));
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            settings,
            candidateValidator: new AcceptingCandidateValidator());

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await runtime.InitializeAsync();
            Assert.AreEqual(
                "old name",
                runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Name);

            await runtime.ImportLocalConfigurationAsync(source, "new name");

            Assert.AreEqual(
                "new name",
                runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive).Name);
        }, async () =>
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task DeleteConfigurationDoesNotHoldReadRefreshBehindSettingsSave()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        BlockingSettingsStore settings = new(new AppSettings());
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        BlockingInstallService service = new();
        using RuntimeControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            service,
            settings,
            proxy,
            new AcceptingCandidateValidator());
        Task? delete = null;
        Task? refresh = null;

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            string source = Path.Combine(root, "source.yaml");
            await File.WriteAllTextAsync(source, "proxies: []\n");
            ConfigurationProfile profile = await runtime.ImportLocalConfigurationAsync(source);
            runtime.AttachControllerForTesting(api, usingServiceCore: true);
            profile = runtime.Snapshot.Configurations.Single(configuration => configuration.IsActive);

            settings.BlockNextSave();
            delete = runtime.DeleteConfigurationAsync(profile);
            await settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            refresh = runtime.RefreshDataAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(delete is { IsCompleted: false });

            settings.ReleaseSave();
            await Task.WhenAll(delete, refresh);
        }, async () =>
        {
            settings.ReleaseSave();
            if (delete is not null)
            {
                await delete.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (refresh is not null)
            {
                await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }

    [TestMethod]
    public async Task ReloadConfigurationDoesNotHoldReadRefreshDuringValidation()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationStore store = new ConfigurationStore(paths);
        ConfigurationProfile imported = await store.ImportLocalAsync(
            await RuntimeTestHelpers.WriteConfigAsync(root, "reload.yaml"),
            "reload");
        BlockingCandidateValidator validator = new();
        TestSettingsStore settings = new(new AppSettings());
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            settings,
            null,
            validator);
        Task? reload = null;
        Task? refresh = null;

        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await runtime.InitializeAsync();
            ConfigurationProfile profile = runtime.Snapshot.Configurations
                .Single(configuration => configuration.Id == imported.Id);
            validator.BlockNextValidation();
            reload = runtime.ReloadConfigurationAsync(profile);
            await validator.ValidationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            refresh = runtime.RefreshDataAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(reload is { IsCompleted: false });

            validator.ReleaseValidation();
            await Task.WhenAll(reload, refresh);
        }, async () =>
        {
            validator.ReleaseValidation();
            if (reload is not null)
            {
                await reload.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (refresh is not null)
            {
                await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await runtime.DisposeAsync();
            if (Directory.Exists(root))
            {
                await TestFixtureDirectory.DeleteAsync(root);
            }
        });
    }
}

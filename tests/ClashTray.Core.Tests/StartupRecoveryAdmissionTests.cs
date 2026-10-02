using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class StartupRecoveryAdmissionTests
{
    [TestMethod]
    public async Task PendingSelectionStillAdoptsRunningServiceBindingAndAllowsTunCleanup()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings() with { Theme = "system", TunEnabled = true };
            await new SettingsRecoveryJournal(paths).PrepareAsync(settings with { Theme = "light" }, settings with { Theme = "dark" }, CancellationToken.None);
            await new ConfigurationSwitchJournalStore(paths).SaveAsync(ConfigurationSwitchJournal.Create(
                ConfigurationSwitchSource.Manual, null, "interrupted", true, false, SystemProxyState.Off, true, TunState.On, 1));
            CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(settings);
            RunningService service = new(binding);
            using RuntimeControllerHandler handler = new();
            using HttpClient client = new(handler);
            await using ClashTrayRuntime runtime = new(paths, null, service,
                new NetworkDisableRecoveryTests.RecoveryStore { Settings = settings }, new FakeSystemProxyController(SystemProxyState.Off),
                controllerApiFactory: () => new MihomoApiClient(client, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty));
            await runtime.InitializeAsync();
            Assert.AreEqual(binding.InstanceId, runtime.ActiveRuntimeBinding?.InstanceId);
            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.AreEqual(0, service.StopCalls, "Settings failure must not restart/stop the adopted core as though configuration recovery completed.");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetTunAsync(false));
            Assert.AreEqual(1, service.DisableCalls);
            Assert.AreEqual(TunState.Off, runtime.Snapshot.Tun);
            Assert.IsTrue(File.Exists(paths.SettingsNetworkOffFile));
        });
    }

    [TestMethod]
    [DataRow("conflict", false)]
    [DataRow("write", false)]
    [DataRow("unreadable", false)]
    [DataRow("conflict", true)]
    [DataRow("write", true)]
    [DataRow("unreadable", true)]
    public async Task PendingSelectionFailureStillObservesServiceAndRestoresOwnedProxy(string failure, bool serviceUnavailable)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(new(Theme: "light"), new(Theme: "dark"), CancellationToken.None);
            await new ConfigurationSwitchJournalStore(paths).SaveAsync(ConfigurationSwitchJournal.Create(
                ConfigurationSwitchSource.Manual, null, "interrupted-first-import", false, true, SystemProxyState.On, true, TunState.On, 1)
                .WithStage(ConfigurationSwitchStage.RuntimePromoted));
            NetworkDisableRecoveryTests.RecoveryStore store = new()
            {
                Settings = new(Theme: failure == "conflict" ? "system" : "dark"),
                FailSave = failure == "write"
            };
            FakeSystemProxyController proxy = new(SystemProxyState.On);
            ObservedService service = new(serviceUnavailable);
            using FileStream? locked = failure == "unreadable"
                ? new(paths.SettingsRecoveryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            await using ClashTrayRuntime runtime = new(paths, null, service, store, proxy);
            await runtime.InitializeAsync();
            Assert.AreEqual(1, service.StatusCalls);
            Assert.AreEqual(1, proxy.DisableCount);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.IsTrue(journal.Exists);
            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile), "Failed selection recovery must never be marked committed, including with an unavailable service.");
            Assert.AreEqual(0, service.StartCalls);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "恢复", StringComparison.Ordinal);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetActiveConfigurationAsync("another"));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepairedEmptyRecoveryLocationClearsFailureOnlyWhenSettingsAreReadable(bool settingsUnreadable)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            Directory.CreateDirectory(paths.SettingsNetworkOffFile);
            ReadableStore store = new();
            FakeSystemProxyController proxy = new(SystemProxyState.Off);
            await using ClashTrayRuntime runtime = new(paths, null, new ObservedService(false), store, proxy);
            await runtime.InitializeAsync();
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置恢复未完成", StringComparison.Ordinal);
            Directory.Delete(paths.SettingsNetworkOffFile);
            store.ReadFailed = settingsUnreadable;
            AppSettingsPatch patch = new(Theme: SettingPatchValue.Set("dark"));
            if (settingsUnreadable)
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.UpdateSettingsAsync(patch));
                StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置恢复未完成", StringComparison.Ordinal);
            }
            else
            {
                await runtime.UpdateSettingsAsync(patch);
                Assert.AreEqual("dark", store.Settings.Theme);
                Assert.IsFalse(runtime.Snapshot.ErrorMessage?.Contains("设置恢复未完成", StringComparison.Ordinal) ?? false);
                using RuntimeControllerHandler handler = new();
                using HttpClient client = new(handler);
                runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), false);
                await runtime.ApplyProgramOverridesForTestingAsync();
                Assert.AreEqual(1, proxy.EnableCount);
                Assert.AreEqual(SystemProxyState.On, runtime.Snapshot.SystemProxy);
            }
        });
    }

    private sealed class ReadableStore : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = new(SystemProxyEnabled: true);
        public bool ReadFailed { get; set; }
        public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(Settings, ReadFailed ? SettingsLoadStatus.ReadFailed : SettingsLoadStatus.Loaded, null));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; return Task.CompletedTask; }
    }

    private sealed class ObservedService(bool unavailable) : IServicePipeClient
    {
        public int StatusCalls { get; private set; }
        public int StartCalls { get; private set; }
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
        {
            if (command == ServiceCommand.GetStatus) { StatusCalls++; }
            if (command == ServiceCommand.StartCore) { StartCalls++; }
            if (unavailable) { throw new TimeoutException("Isolated service unavailable."); }
            return Task.FromResult(new ServiceResponse(Guid.NewGuid(), true, TunState.Off, Core: CoreState.Stopped, ProtocolVersion: ServiceProtocol.CurrentVersion));
        }
    }

    private sealed class RunningService(CoreRuntimeBinding binding) : IServicePipeClient
    {
        public int DisableCalls { get; private set; }
        public int StopCalls { get; private set; }
        private TunState _tun = TunState.On;
        private CoreState _core = CoreState.Running;
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
        {
            if (command == ServiceCommand.DisableTun) { DisableCalls++; _tun = TunState.Off; }
            if (command == ServiceCommand.StopCore) { StopCalls++; _core = CoreState.Stopped; _tun = TunState.Off; }
            return Task.FromResult(new ServiceResponse(Guid.NewGuid(), true, _tun, Core: _core,
                ProtocolVersion: ServiceProtocol.CurrentVersion, RuntimeBinding: binding));
        }
    }
}

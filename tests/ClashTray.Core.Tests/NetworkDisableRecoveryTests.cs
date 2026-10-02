using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class NetworkDisableRecoveryTests
{
    [TestMethod]
    public async Task ExplicitEnableClearsOnlyItsOwnDurableDisableIntent()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            FakeSystemProxyController proxy = new(SystemProxyState.Off);
            TrackingTunService service = new();
            await using ClashTrayRuntime runtime = new(paths, null, service, new RecoveryStore(), proxy);
            using RuntimeControllerHandler handler = new();
            using HttpClient client = new(handler);
            runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
            await runtime.SetSystemProxyAsync(false);
            await runtime.SetTunAsync(false);
            await runtime.SetSystemProxyAsync(true);
            NetworkDisableIntent remaining = await new SettingsRecoveryJournal(paths).ReadNetworkDisableIntentAsync(CancellationToken.None);
            Assert.IsFalse(remaining.SystemProxyOff);
            Assert.IsTrue(remaining.TunOff);
            await runtime.ApplyProgramOverridesForTestingAsync();
            Assert.AreEqual(0, service.EnableCount);
            Assert.AreEqual(1, proxy.EnableCount);
            await runtime.SetTunAsync(true);
            Assert.IsFalse(File.Exists(paths.SettingsNetworkOffFile));
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.IsTrue(runtime.Settings.TunEnabled);
        });
    }

    [TestMethod]
    public async Task UnreadableDisableIntentBlocksAutomaticEnablesWithoutVetoingNewDisables()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            await new SettingsRecoveryJournal(paths).WriteNetworkDisableIntentAsync(new(SystemProxyOff: true, TunOff: true), CancellationToken.None);
            using FileStream locked = new(paths.SettingsNetworkOffFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            FakeSystemProxyController proxy = new(SystemProxyState.Off);
            TrackingTunService service = new();
            await using ClashTrayRuntime runtime = new(paths, null, service, new RecoveryStore { Settings = new(SystemProxyEnabled: true, TunEnabled: true) }, proxy);
            await runtime.InitializeAsync();
            using RuntimeControllerHandler handler = new();
            using HttpClient client = new(handler);
            runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
            await runtime.ApplyProgramOverridesForTestingAsync();
            Assert.AreEqual(0, proxy.EnableCount);
            Assert.AreEqual(0, service.EnableCount);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetSystemProxyAsync(false));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetTunAsync(false));
            Assert.AreEqual(1, proxy.DisableCount);
            Assert.AreEqual(1, service.DisableCount);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(TunState.Off, runtime.Snapshot.Tun);
        });
    }
    [TestMethod]
    public async Task PendingRecoveryDoesNotOverwriteExternallyChangedProxyOwnership()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            IsolatedProxyRegistry registry = new();
            SystemProxyManager proxy = new(paths, registry, static () => { });
            await proxy.EnableAsync(7890, "localhost");
            ProxyRegistryState external = registry.State with { ProxyServer = "another-client:8080" };
            registry.State = external;
            int previousWrites = registry.Writes;
            await new SettingsRecoveryJournal(paths).PrepareAsync(new(Theme: "light"), new(Theme: "dark"), CancellationToken.None);
            await using ClashTrayRuntime runtime = new(paths, null, null, new RecoveryStore(), proxy);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetSystemProxyAsync(false));
            Assert.AreEqual(previousWrites, registry.Writes, "Safety cleanup retains the original ownership conflict guard.");
            Assert.AreEqual(external, registry.State);
            Assert.AreEqual(SystemProxyState.RestoreRequired, runtime.Snapshot.SystemProxy);
            Assert.IsTrue(File.Exists(paths.ProxyBackupFile));
            Assert.IsFalse(runtime.Snapshot.ErrorMessage?.Contains("已确认关闭", StringComparison.Ordinal) ?? false);
        });
    }

    private sealed class IsolatedProxyRegistry : ISystemProxyRegistry
    {
        public ProxyRegistryState State { get; set; } = new(0, null, null, null, 0);
        public int Writes { get; private set; }
        public ProxyRegistryState ReadCurrentState() => State;
        public void SetProxyEnable(int value) { Writes++; State = State with { ProxyEnable = value }; }
        public void SetProxyServer(string? value) { Writes++; State = State with { ProxyServer = value }; }
        public void SetProxyOverride(string? value) { Writes++; State = State with { ProxyOverride = value }; }
        public void WriteState(ProxyRegistryState state) { Writes++; State = state; }
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisableWithSaveFailureSurvivesPollingNewBindingAndAppRestart(bool tun)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            RecoveryStore store = new() { Settings = new(SystemProxyEnabled: !tun, TunEnabled: tun) };
            FakeSystemProxyController proxy = new(SystemProxyState.Off);
            TrackingTunService service = new();
            using RuntimeControllerHandler handler = new();
            using HttpClient client = new(handler);
            await using (ClashTrayRuntime runtime = new(paths, null, service, store, proxy))
            {
                await runtime.InitializeAsync();
                runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
                proxy.SetState(SystemProxyState.On);
                store.FailSave = true;
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tun ? runtime.SetTunAsync(false) : runtime.SetSystemProxyAsync(false));
                Assert.AreEqual(1, tun ? service.DisableCount : proxy.DisableCount);
                Assert.IsTrue(File.Exists(paths.SettingsNetworkOffFile));
                await runtime.ApplyProgramOverridesForTestingAsync();
                // A new confirmed binding is the same preference path used by
                // a core restart. The old enabled preference stays suppressed.
                runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
                await runtime.ApplyProgramOverridesForTestingAsync();
                Assert.AreEqual(0, service.EnableCount);
                Assert.AreEqual(0, proxy.EnableCount);
            }
            await using ClashTrayRuntime reopened = new(paths, null, service, store, proxy);
            await reopened.InitializeAsync();
            reopened.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
            await reopened.ApplyProgramOverridesForTestingAsync();
            Assert.IsFalse(tun ? reopened.Settings.TunEnabled : reopened.Settings.SystemProxyEnabled);
            Assert.AreEqual(0, service.EnableCount);
            Assert.AreEqual(0, proxy.EnableCount);
            // Delayed recovery after storage works again must persist Off.
            store.FailSave = false;
            await reopened.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            Assert.IsFalse(tun ? store.Settings.TunEnabled : store.Settings.SystemProxyEnabled);
            await reopened.ApplyProgramOverridesForTestingAsync();
            Assert.AreEqual(0, service.EnableCount);
            Assert.AreEqual(0, proxy.EnableCount);
            if (tun) { await reopened.SetTunAsync(true); Assert.IsTrue(reopened.Settings.TunEnabled); Assert.AreEqual(1, service.EnableCount); }
            else { await reopened.SetSystemProxyAsync(true); Assert.IsTrue(reopened.Settings.SystemProxyEnabled); Assert.AreEqual(1, proxy.EnableCount); }
            Assert.IsFalse(File.Exists(paths.SettingsNetworkOffFile), "A confirmed explicit enable supersedes only its off intent.");
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealCleanupFailureNeverReportsConfirmedOff(bool tun)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            FailedDisableProxy proxy = new();
            TrackingTunService service = new() { RejectDisable = true };
            await using ClashTrayRuntime runtime = new(paths, null, service, new RecoveryStore(), proxy);
            runtime.SetShutdownStateForTesting(true, CoreState.Running, TunState.On, SystemProxyState.On);
            if (tun) { await Assert.ThrowsExactlyAsync<ServiceCommandException>(() => runtime.SetTunAsync(false)); Assert.AreEqual(TunState.On, runtime.Snapshot.Tun); }
            else { await Assert.ThrowsExactlyAsync<IOException>(() => runtime.SetSystemProxyAsync(false)); Assert.AreEqual(SystemProxyState.On, runtime.Snapshot.SystemProxy); }
            Assert.IsFalse(runtime.Snapshot.ErrorMessage?.Contains("已确认关闭", StringComparison.Ordinal) ?? false);
            Assert.IsTrue(File.Exists(paths.SettingsNetworkOffFile), "Retry intent is retained even after actual cleanup failure.");
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnableRetainsPendingRecoveryAdmissionBarrier(bool tun)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            FakeSystemProxyController proxy = new(SystemProxyState.Off);
            TrackingTunService service = new();
            await using ClashTrayRuntime runtime = new(paths, null, service, new RecoveryStore { Settings = new(Theme: "system") }, proxy);
            await new SettingsRecoveryJournal(paths).PrepareAsync(new(Theme: "light"), new(Theme: "dark"), CancellationToken.None);
            runtime.SetShutdownStateForTesting(true, CoreState.Running, TunState.Off, SystemProxyState.Off);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => tun ? runtime.SetTunAsync(true) : runtime.SetSystemProxyAsync(true));
            Assert.AreEqual(0, service.EnableCount);
            Assert.AreEqual(0, proxy.EnableCount);
        });
    }

    private sealed class FailedDisableProxy : ISystemProxyController
    {
        public SystemProxyState State => SystemProxyState.On;
        public SystemProxyState DetectState() => State;
        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.FromException(new IOException("owned proxy restoration failed"));
    }
    [TestMethod]
    [DataRow(false, "conflict")]
    [DataRow(true, "conflict")]
    [DataRow(false, "unreadable")]
    [DataRow(true, "unreadable")]
    [DataRow(false, "write")]
    [DataRow(true, "write")]
    public async Task PendingRecoveryCannotPreventConfirmedNetworkDisable(bool tun, string failure)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(new(Theme: "light", SystemProxyEnabled: true, TunEnabled: true), new(Theme: "dark", SystemProxyEnabled: true, TunEnabled: true), CancellationToken.None);
            RecoveryStore store = new() { Settings = new(Theme: failure == "conflict" ? "system" : "dark", SystemProxyEnabled: true, TunEnabled: true), FailSave = failure == "write" };
            FakeSystemProxyController proxy = new(SystemProxyState.On);
            TrackingTunService service = new();
            await using ClashTrayRuntime runtime = new(paths, null, service, store, proxy);
            runtime.SetShutdownStateForTesting(true, CoreState.Running, TunState.On, SystemProxyState.On);
            using FileStream? lockedJournal = failure == "unreadable"
                ? new FileStream(paths.SettingsRecoveryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            Exception? error = null;
            try
            {
                if (tun) { await runtime.SetTunAsync(false); }
                else { await runtime.SetSystemProxyAsync(false); }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException) { error = exception; }
            Console.WriteLine($"R1 {failure}, tun={tun}: proxyDisables={proxy.DisableCount}, serviceDisables={service.DisableCount}, proxy={runtime.Snapshot.SystemProxy}, tun={runtime.Snapshot.Tun}, error={error?.GetType().Name}");
            Assert.AreEqual(1, tun ? service.DisableCount : proxy.DisableCount, "Storage recovery must not veto actual safety cleanup.");
            if (tun) { Assert.AreEqual(TunState.Off, runtime.Snapshot.Tun); Assert.IsFalse(runtime.Settings.TunEnabled); }
            else { Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy); Assert.IsFalse(runtime.Settings.SystemProxyEnabled); }
            Assert.IsNotNull(error, "Confirmed cleanup with pending storage failure must report partial success.");
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "关闭", StringComparison.Ordinal);
        });
    }

    internal sealed class RecoveryStore : ISettingsStore
    {
        public AppSettings Settings { get; set; } = new();
        public bool FailSave { get; set; }
        public int FailNextSaves { get; set; }
        public int SaveCount { get; private set; }
        public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(Settings, SettingsLoadStatus.Loaded, null));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            if (FailSave || FailNextSaves-- > 0) { throw new IOException("Injected settings write failure"); }
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    internal sealed class TrackingTunService : IServicePipeClient
    {
        public int DisableCount { get; private set; }
        public int EnableCount { get; private set; }
        public bool RejectDisable { get; set; }
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default)
        {
            if (command == ServiceCommand.DisableTun) { DisableCount++; }
            if (command == ServiceCommand.EnableTun) { EnableCount++; }
            bool rejected = RejectDisable && command == ServiceCommand.DisableTun;
            return Task.FromResult(new ServiceResponse(Guid.NewGuid(), !rejected, rejected || command == ServiceCommand.EnableTun ? TunState.On : TunState.Off,
                Error: rejected ? "TUN rollback failed" : null, Core: CoreState.Stopped, ProtocolVersion: ServiceProtocol.CurrentVersion));
        }
    }
}

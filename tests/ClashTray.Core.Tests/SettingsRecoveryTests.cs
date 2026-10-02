using ClashTray.Testing;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SettingsRecoveryTests
{
    [TestMethod]
    public async Task InitialWriteSettingsRestoreAndStartupRollbackFailuresRetainAllThreeErrors()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            await using ClashTrayRuntime runtime = new(paths, new FailingStartupRollback(), null,
                new FaultingSettingsStore { FailEverySave = true }, new FakeSystemProxyController(SystemProxyState.Off));
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")), reconcileStartup: true));
            Assert.IsInstanceOfType<AggregateException>(error.InnerException);
            AggregateException failures = ((AggregateException)error.InnerException).Flatten();
            Assert.AreEqual(3, failures.InnerExceptions.Count, "Initial persistence, settings restoration and startup restoration must all remain diagnosable.");
            Assert.IsTrue(File.Exists(paths.SettingsRecoveryFile));
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置恢复未完成", StringComparison.Ordinal);
        });
    }

    private sealed class FailingStartupRollback : IStartupRegistration
    {
        public StartupRegistrationStatus GetStatus() => new(false, false, null);
        public StartupRegistrationChange Ensure(bool enabled, string? executablePath) =>
            new(true, "fixture", false, false, null, Microsoft.Win32.RegistryValueKind.String);
        public void Rollback(StartupRegistrationChange change) => throw new IOException("startup rollback failed");
    }

    [TestMethod]
    public async Task ProxyFailureAndRollbackWriteFailureRetainBothErrorsAndRecoverAfterRestart()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FaultingSettingsStore store = new() { FailSaveNumber = 2 };
        try
        {
            await using (ClashTrayRuntime runtime = new(paths, null, null, store, new FailingProxy()))
            {
                using RuntimeControllerHandler handler = new();
                using HttpClient client = new(handler);
                runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), false);
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    () => runtime.SetSystemProxyAsync(true));

                Assert.IsInstanceOfType<AggregateException>(error.InnerException);
                Assert.IsFalse(runtime.Settings.SystemProxyEnabled, "The safety preference stays off while recovery is pending.");
                Assert.IsTrue(store.Settings.SystemProxyEnabled, "The first write succeeded; the rollback write failed.");
                Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
                StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置恢复未完成", StringComparison.Ordinal);
                Assert.IsTrue(runtime.Snapshot.Logs.Any(log => log.Level == "error" && log.Message.Contains("设置恢复", StringComparison.Ordinal)));
                Assert.IsFalse(runtime.Snapshot.ErrorMessage!.Contains("credential-value", StringComparison.Ordinal));
            }

            store.FailSaveNumber = 0;
            await using ClashTrayRuntime reopened = new(paths, null, null, store, new FakeSystemProxyController(SystemProxyState.Off));
            await reopened.InitializeAsync();
            Assert.IsFalse(reopened.Settings.SystemProxyEnabled);
            Assert.AreEqual(reopened.Settings, store.Settings);
        }
        finally
        {
            await TestDirectoryCleanup.DeleteAsync(root);
        }
    }

    [TestMethod]
    public async Task InitialPreferenceWriteFailureNeverPublishesOrAppliesTheNewPreference()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FaultingSettingsStore store = new() { FailSaveNumber = 1 };
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        try
        {
            await using ClashTrayRuntime runtime = new(paths, null, null, store, proxy);
            await Assert.ThrowsExactlyAsync<IOException>(() => runtime.SetSystemProxyAsync(true));
            Assert.IsFalse(runtime.Settings.SystemProxyEnabled);
            Assert.AreEqual(runtime.Settings, store.Settings);
            Assert.AreEqual(0, proxy.EnableCount);
        }
        finally
        {
            await TestDirectoryCleanup.DeleteAsync(root);
        }
    }

    private sealed class FaultingSettingsStore : ISettingsStore
    {
        private int _saveCount;
        public int FailSaveNumber { get; set; }
        public bool FailEverySave { get; set; }
        public AppSettings Settings { get; private set; } = new();
        public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SettingsLoadResult(Settings, SettingsLoadStatus.Loaded, null));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_saveCount == FailSaveNumber || FailEverySave)
            {
                throw new IOException("rollback write failed: token=credential-value");
            }
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneralSettingsRollbackAlwaysRestoresConfirmedNetworkAndRetainsPendingStorageRecovery(bool failRollback)
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FaultingSettingsStore store = new() { FailSaveNumber = failRollback ? 2 : 0 };
        try
        {
            await using ClashTrayRuntime runtime = new(paths, null, null, store, new FakeSystemProxyController(SystemProxyState.Off));
            using RuntimeControllerHandler handler = new() { FailNextAllowLanEnable = true };
            using HttpClient client = new(handler);
            runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), false);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(AllowLan: SettingPatchValue.Set(true))));
            Assert.IsFalse(handler.AllowLan);
            Assert.IsFalse(runtime.Settings.AllowLan);
            Assert.AreEqual(failRollback, store.Settings.AllowLan);
            Assert.AreEqual(failRollback, File.Exists(paths.SettingsRecoveryFile));
            store.FailSaveNumber = 0;
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            Assert.AreEqual(runtime.Settings, store.Settings);
            Assert.AreEqual("dark", store.Settings.Theme);
            Assert.IsFalse(store.Settings.AllowLan);
            Assert.IsFalse(File.Exists(paths.SettingsRecoveryFile));
        }
        finally { await TestDirectoryCleanup.DeleteAsync(root); }
    }

    [TestMethod]
    public async Task FailedTunPreferenceStorageDoesNotEraseConfirmedServiceState()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FaultingSettingsStore store = new() { FailEverySave = true };
        try
        {
            await using ClashTrayRuntime runtime = new(paths, null, new ConfirmedTunService(), store, new FakeSystemProxyController(SystemProxyState.Off));
            using RuntimeControllerHandler handler = new();
            using HttpClient client = new(handler);
            runtime.AttachControllerForTesting(new MihomoApiClient(client, new Uri("http://127.0.0.1:9090/"), string.Empty), true);
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.SetTunAsync(true));
            Assert.IsInstanceOfType<AggregateException>(error.InnerException);
            Assert.AreEqual(TunState.On, runtime.Snapshot.Tun);
            Assert.IsFalse(runtime.Settings.TunEnabled);
            Assert.IsTrue(File.Exists(paths.SettingsRecoveryFile));
        }
        finally { await TestDirectoryCleanup.DeleteAsync(root); }
    }

    [TestMethod]
    public async Task RecoveryRefusesExternalSettingsConflictAndOversizedRecord()
    {
        string root = TestFixtureDirectory.Create();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        try
        {
            paths.EnsureDirectories();
            SettingsRecoveryJournal journal = new(paths);
            await journal.PrepareAsync(new AppSettings(Theme: "light"), new AppSettings(Theme: "dark"), CancellationToken.None);
            TestSettingsStore store = new(new AppSettings(Theme: "system"));
            await using ClashTrayRuntime runtime = new(paths, null, null, store, new FakeSystemProxyController(SystemProxyState.Off));
            await runtime.InitializeAsync();
            Assert.AreEqual("system", store.Settings.Theme);
            Assert.IsTrue(journal.Exists);
            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "其他入口", StringComparison.Ordinal);
            await File.WriteAllBytesAsync(paths.SettingsRecoveryFile, new byte[65537]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.ReadAsync(CancellationToken.None));
        }
        finally { await TestDirectoryCleanup.DeleteAsync(root); }
    }

    private sealed class ConfirmedTunService : IServicePipeClient
    {
        public Task<ServiceResponse> SendAsync(ServiceCommand command, string? payload = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServiceResponse(Guid.NewGuid(), true, command == ServiceCommand.EnableTun ? TunState.On : TunState.Off));
    }

    private sealed class FailingProxy : ISystemProxyController
    {
        public SystemProxyState State => SystemProxyState.Off;
        public SystemProxyState DetectState() => State;
        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("network operation failed"));
        public Task DisableAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

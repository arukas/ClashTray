using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSystemProxyEnableRaceTests
{
    [TestMethod]
    public async Task EnableFinalizationFailureDoesNotDiscardQueuedCoreLossRecovery()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureDirectories();
            AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
            CrashingEnableSystemProxyController proxy = new();
            using RuntimeControllerHandler handler = new();
            using HttpClient httpClient = new(handler);
            MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
            await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
                paths,
                settingsStore: new TestSettingsStore(settings),
                systemProxy: proxy,
                ownership: new FixtureCoreOwnershipObservationSource(),
                controllerApiFactory: () => api);
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            FileStream? blockedIntent = null;
            runtime.SystemProxyEnableLivenessObservedForTesting = () =>
            {
                // The proxy has committed. Simulate an intent-file IO failure
                // in finalization after queuing the real core-loss recovery.
                File.WriteAllText(paths.SettingsNetworkOffFile, "{}");
                blockedIntent = new FileStream(paths.SettingsNetworkOffFile, FileMode.Open, FileAccess.Read, FileShare.None);
                runtime.SignalCoreLossForTesting();
            };

            try
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => runtime.SetSystemProxyAsync(true));
                await runtime.AwaitSystemProxyRecoveryForTestingAsync();

                Assert.AreEqual(1, proxy.EnableCount);
                Assert.AreEqual(1, proxy.DisableCount, "A failure after the ownership commit must not invalidate its queued recovery.");
                Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
                Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
                Assert.IsFalse(runtime.Settings.SystemProxyEnabled, "The failed enable must retain its existing preference rollback behavior.");
            }
            finally
            {
                if (blockedIntent is not null)
                {
                    await blockedIntent.DisposeAsync();
                }
            }
        });
    }

    [TestMethod]
    [DataRow(SystemProxyState.Failed, true, true)]
    [DataRow(SystemProxyState.RestoreRequired, false, true)]
    [DataRow(SystemProxyState.Failed, true, false)]
    [DataRow(SystemProxyState.RestoreRequired, false, false)]
    public async Task IncompleteCoreLossRecoveryRetainsErrorAndEnabledPreference(
        SystemProxyState recoveryState,
        bool throwOnDisable,
        bool loseCoreDuringEnable)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
            CrashingEnableSystemProxyController proxy = new(loseCoreDuringEnable ? SystemProxyState.Off : SystemProxyState.On)
            {
                DisableResult = recoveryState,
                ThrowOnDisable = throwOnDisable
            };
            using RuntimeControllerHandler handler = new();
            using HttpClient httpClient = new(handler);
            MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
            await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
                paths,
                settingsStore: new TestSettingsStore(settings),
                systemProxy: proxy,
                ownership: new FixtureCoreOwnershipObservationSource(),
                controllerApiFactory: () => api);
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            if (loseCoreDuringEnable)
            {
                proxy.OnEnable = () => runtime.SetCoreStateForTesting(CoreState.Failed);
            }
            else
            {
                runtime.SetCoreStateForTesting(CoreState.Failed);
            }

            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(loseCoreDuringEnable ? 1 : 0, proxy.EnableCount);
            Assert.AreEqual(1, proxy.DisableCount);
            Assert.AreEqual(recoveryState, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
            Assert.AreEqual("核心不可用时撤销系统代理失败，系统代理状态需要恢复。", runtime.Snapshot.ErrorMessage);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled, "Incomplete core-loss recovery must not roll back the user's enabled preference.");
            if (throwOnDisable)
            {
                Assert.IsTrue(runtime.Snapshot.Logs.Any(entry => entry.Message.Contains("撤销系统代理失败", StringComparison.Ordinal)));
            }
        });
    }

    [TestMethod]
    public async Task CoreLossAfterEnableLivenessCheckStillRevokesCommittedProxy()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
            CrashingEnableSystemProxyController proxy = new();
            using RuntimeControllerHandler handler = new();
            using HttpClient httpClient = new(handler);
            MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
            await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
                paths,
                settingsStore: new TestSettingsStore(settings),
                systemProxy: proxy,
                ownership: new FixtureCoreOwnershipObservationSource(),
                controllerApiFactory: () => api);
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            // Inject the real core-loss handler immediately after observing Running.
            // Its queued recovery waits for enable to release the operation lease.
            runtime.SystemProxyEnableLivenessObservedForTesting = runtime.SignalCoreLossForTesting;

            await runtime.SetSystemProxyAsync(true);
            await runtime.AwaitSystemProxyRecoveryForTestingAsync();

            Assert.AreEqual(1, proxy.EnableCount);
            Assert.AreEqual(1, proxy.DisableCount, "The enable commit must not invalidate recovery queued after its liveness check.");
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
            Assert.IsNull(runtime.ActiveRuntimeBinding);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
        });
    }

    [TestMethod]
    public async Task SystemProxyEnableCommitRechecksCoreLiveness()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        AppSettings settings = RuntimeTestHelpers.CreatePortSafeSettings();
        CrashingEnableSystemProxyController proxy = new();
        using RuntimeControllerHandler handler = new();
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri($"http://127.0.0.1:{settings.ControllerPort}/"), string.Empty);
        await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
            paths,
            null,
            null,
            new TestSettingsStore(settings),
            proxy,
            ownership: new FixtureCoreOwnershipObservationSource(), controllerApiFactory: () => api);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            // The core dies while the OS proxy write is in flight; crash cleanup
            // that observed the proxy still Off became a no-op, so the enable
            // commit is the last word unless it re-checks liveness.
            proxy.OnEnable = () => runtime.SetCoreStateForTesting(CoreState.Failed);

            await runtime.SetSystemProxyAsync(true);

            Assert.AreEqual(1, proxy.EnableCount);
            Assert.AreEqual(1, proxy.DisableCount, "The just-enabled proxy must be revoked when the core is no longer running.");
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.AreEqual(CoreState.Failed, runtime.Snapshot.Core.State);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled, "Revoking after a core loss must keep the saved preference for re-application.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class CrashingEnableSystemProxyController(SystemProxyState initialState = SystemProxyState.Off) : ISystemProxyController
    {
        private Action? _onEnable;

        public SystemProxyState State { get; private set; } = initialState;

        public SystemProxyState DisableResult { get; init; } = SystemProxyState.Off;

        public bool ThrowOnDisable { get; init; }

        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public Action? OnEnable
        {
            get => _onEnable;
            set => _onEnable = value;
        }

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
        {
            EnableCount++;
            State = SystemProxyState.On;
            _onEnable?.Invoke();
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            DisableCount++;
            State = DisableResult;
            if (ThrowOnDisable)
            {
                throw new IOException("Simulated proxy restoration failure.");
            }
            return Task.CompletedTask;
        }
    }
}

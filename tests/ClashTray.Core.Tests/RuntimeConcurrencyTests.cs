using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeConcurrencyTests
{
    [TestMethod]
    public async Task ControllerOperationsAreSerialized()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using DelayControllerHandler handler = new DelayControllerHandler(holdFirstRequest: true);
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            Task<int?> first = runtime.TestProxyDelayAsync("node");
            await handler.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Task<int?> second = runtime.TestProxyDelayAsync("node");
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            Assert.AreEqual(1, handler.MaxInFlight);
            handler.ReleaseFirstRequest();
            int?[] results = await Task.WhenAll(first, second);

            CollectionAssert.AreEqual(new int?[] { 10, 10 }, results);
            Assert.AreEqual(1, handler.MaxInFlight);
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
    public async Task ProxyDelayDoesNotHoldLocalMutationLane()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using DelayControllerHandler handler = new(holdFirstRequest: true);
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            Task<int?> delay = runtime.TestProxyDelayAsync("node");
            await handler.FirstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task modeChange = runtime.SetModeAsync(ProxyMode.Direct);
            await handler.ModePatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(delay.IsCompleted);

            handler.ReleaseFirstRequest();
            Assert.AreEqual(10, await delay.WaitAsync(TimeSpan.FromSeconds(2)));
            await modeChange.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            handler.ReleaseFirstRequest();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ControllerOperationRejectsResultFromReplacedSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using DelayControllerHandler oldHandler = new DelayControllerHandler(holdFirstRequest: false);
        using DelayControllerHandler replacementHandler = new DelayControllerHandler(holdFirstRequest: false);
        using HttpClient oldHttpClient = new HttpClient(oldHandler);
        using HttpClient replacementHttpClient = new HttpClient(replacementHandler);
        MihomoApiClient oldApi = new MihomoApiClient(oldHttpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        MihomoApiClient replacementApi = new MihomoApiClient(
            replacementHttpClient,
            new Uri("http://127.0.0.1:9090/"),
            string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            oldHandler.BeforeFirstRequest = () =>
                runtime.AttachControllerForTesting(replacementApi, usingServiceCore: false);
            runtime.AttachControllerForTesting(oldApi, usingServiceCore: false);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.TestProxyDelayAsync("node"));

            Assert.AreEqual("测速期间核心会话已切换，请重新测速。", exception.Message);
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
    public async Task ManualRefreshDoesNotHoldMutationLane()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler
        {
            HoldFirstVersionRequest = true
        };
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            Task refresh = runtime.RefreshDataAsync();
            await handler.FirstVersionRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task modeChange = runtime.SetModeAsync(ProxyMode.Direct);
            await handler.ModePatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(1, handler.ModePatchCount);
            handler.ReleaseFirstVersionRequest();
            await Task.WhenAll(refresh, modeChange);

            Assert.AreEqual(1, handler.ModePatchCount);
            Assert.AreEqual(ProxyMode.Direct, handler.Mode);
            Assert.AreEqual(ProxyMode.Direct, runtime.Snapshot.Core.Mode);
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
    public async Task CoreUpdateRejectsUnapprovedReleaseBeforeCallingService()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        BlockingInstallService service = new();
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, service, null);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.InstallCoreUpdateAsync(new CoreUpdateManifest(
                "v1.19.31",
                new Uri("https://github.com/example-owner/example-repo/raw/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1.19.31.zip"),
                string.Empty)));

            Assert.AreEqual(0, service.InstallRequestCount);
            Assert.IsFalse(service.InstallEntered.Task.IsCompleted);
            Assert.IsFalse(File.Exists(paths.ManagedCoreExecutable));
        }
        finally
        {
            service.ReleaseInstall();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CoreUpdateDoesNotHoldReadRefreshBehindInstall()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        BlockingInstallService service = new();
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(
            paths,
            null,
            service,
            null);
        Task? update = null;
        Task? refresh = null;

        try
        {
            update = runtime.InstallCoreUpdateAsync(new CoreUpdateManifest(
                "v1.19.30",
                new Uri("https://github.com/MetaCubeX/mihomo/releases/download/v1.19.30/mihomo-windows-amd64-v1.19.30.zip"),
                new string('0', 64)));
            await service.InstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            refresh = runtime.RefreshDataAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(update is { IsCompleted: false });

            service.ReleaseInstall();
            await Task.WhenAll(update, refresh);
        }
        finally
        {
            service.ReleaseInstall();
            if (update is not null)
            {
                await update.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (refresh is not null)
            {
                await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task NetworkSwitchRuleSaveWaitsForSettingsOperation()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        BlockingSettingsStore settings = new(new AppSettings());
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new(paths, null, null, settings, proxy);
        Task? settingsUpdate = null;
        Task? rulesUpdate = null;

        try
        {
            await runtime.InitializeAsync();
            settings.BlockNextSave();
            settingsUpdate = runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            await settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            rulesUpdate = runtime.UpdateNetworkSwitchRulesAsync(
                new NetworkSwitchRuleSet(false, null, []));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.IsFalse(rulesUpdate.IsCompleted);

            settings.ReleaseSave();
            await Task.WhenAll(settingsUpdate, rulesUpdate);
            Assert.IsFalse(runtime.NetworkSwitchRules.AutomaticSwitchingEnabled);
        }
        finally
        {
            settings.ReleaseSave();
            if (settingsUpdate is not null)
            {
                await settingsUpdate.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (rulesUpdate is not null)
            {
                await rulesUpdate.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task NetworkSwitchManualOverrideClearWaitsForSettingsOperation()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        BlockingSettingsStore settings = new(new AppSettings());
        await using ClashTrayRuntime runtime = new(paths, null, null, settings);
        Task? settingsUpdate = null;
        Task? clearOverride = null;

        try
        {
            await runtime.InitializeAsync();
            settings.BlockNextSave();
            settingsUpdate = runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            await settings.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            clearOverride = runtime.ClearNetworkSwitchManualOverrideAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.IsFalse(clearOverride.IsCompleted);

            settings.ReleaseSave();
            await Task.WhenAll(settingsUpdate, clearOverride);
        }
        finally
        {
            settings.ReleaseSave();
            if (settingsUpdate is not null)
            {
                await settingsUpdate.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (clearOverride is not null)
            {
                await clearOverride.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsUnapprovedSourceBeforeNetworkAccess()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        using CoreUpdater updater = new CoreUpdater(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => updater.DownloadAndInstallAsync(
                new CoreUpdateManifest("v0", new Uri("https://example.com/mihomo.zip"), new string('0', 64))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

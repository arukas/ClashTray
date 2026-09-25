using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSettingsTests
{
    [TestMethod]
    public async Task FailedRunningCoreSettingsRestartRestoresPreviousSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings());
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings);

        try
        {
            AppSettings previous = runtime.Settings;
            runtime.AttachControllerForTesting(api, usingServiceCore: false);

            InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => runtime.UpdateSettingsAsync(new AppSettingsPatch(HttpPort: SettingPatchValue.Set(previous.HttpPort + 1))));

            Assert.AreEqual(previous, runtime.Settings);
            Assert.AreEqual(previous, settings.Settings);
            StringAssert.Contains(exception.Message, "设置应用失败", StringComparison.Ordinal);

            await runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));
            Assert.AreEqual("dark", runtime.Settings.Theme);
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
    public async Task QueuedSettingsUpdatesPreserveChangesToDifferentFields()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(new AppSettings());
        await using ClashTrayRuntime runtime = new(paths, null, null, settings);
        OperationGate.Lease blocker = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            AppSettings stale = runtime.Settings;
            AppSettingsPatch settingsPagePatch = AppSettingsPatch.Diff(stale, stale with { SubscriptionRefreshHours = 37 });
            Task refreshInterval = runtime.UpdateSettingsAsync(settingsPagePatch);
            Task theme = runtime.UpdateSettingsAsync(new AppSettingsPatch(Theme: SettingPatchValue.Set("dark")));

            blocker.Dispose();
            await Task.WhenAll(refreshInterval, theme);

            Assert.AreEqual(37, runtime.Settings.SubscriptionRefreshHours);
            Assert.AreEqual("dark", runtime.Settings.Theme);
            Assert.AreEqual(runtime.Settings, settings.Settings);
        }
        finally
        {
            blocker.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task QueuedUpdatesToSameFieldUseOperationOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(new AppSettings());
        await using ClashTrayRuntime runtime = new(paths, null, null, settings);
        OperationGate.Lease blocker = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            Task first = runtime.UpdateSettingsAsync(new AppSettingsPatch(
                SubscriptionRefreshHours: SettingPatchValue.Set(31)));
            Task second = runtime.UpdateSettingsAsync(new AppSettingsPatch(
                SubscriptionRefreshHours: SettingPatchValue.Set(37)));

            blocker.Dispose();
            await Task.WhenAll(first, second);

            Assert.AreEqual(37, runtime.Settings.SubscriptionRefreshHours);
            Assert.AreEqual(37, settings.Settings.SubscriptionRefreshHours);
        }
        finally
        {
            blocker.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ThemePatchPreservesActiveConfigurationProxyAndTunSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(new AppSettings());
        FakeSystemProxyController proxy = new(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new(paths, null, null, settings, proxy);

        try
        {
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(
                ActiveConfigurationId: SettingPatchValue.Set<string?>("profile"),
                SystemProxyEnabled: SettingPatchValue.Set(true),
                TunEnabled: SettingPatchValue.Set(true)));

            await runtime.UpdateSettingsAsync(new AppSettingsPatch(
                Theme: SettingPatchValue.Set("dark")));

            Assert.AreEqual("dark", runtime.Settings.Theme);
            Assert.AreEqual("profile", runtime.Settings.ActiveConfigurationId);
            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.IsTrue(runtime.Settings.TunEnabled);
            Assert.AreEqual(runtime.Settings, settings.Settings);
            Assert.AreEqual(0, proxy.EnableCount);
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
    public async Task FailedQueuedSettingsApplicationRollsBackToLatestSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new(new AppSettings());
        using RuntimeControllerHandler handler = new() { FailNextAllowLanEnable = true };
        using HttpClient httpClient = new(handler);
        MihomoApiClient api = new(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new(paths, null, null, settings);
        await runtime.InitializeAsync();
        runtime.AttachControllerForTesting(api, usingServiceCore: false);
        OperationGate.Lease blocker = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            Task theme = runtime.UpdateSettingsAsync(new AppSettingsPatch(
                Theme: SettingPatchValue.Set("dark")));
            Task allowLan = runtime.UpdateSettingsAsync(new AppSettingsPatch(
                AllowLan: SettingPatchValue.Set(true)));

            blocker.Dispose();
            await theme;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => allowLan);

            Assert.AreEqual("dark", runtime.Settings.Theme);
            Assert.IsFalse(runtime.Settings.AllowLan);
            Assert.AreEqual(runtime.Settings, settings.Settings);
            Assert.IsTrue(handler.RequestedPaths.Contains("/configs"));
        }
        finally
        {
            blocker.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidSubscriptionRefreshInterval()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(SubscriptionRefreshHours: SettingPatchValue.Set(0))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidPortSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(HttpPort: SettingPatchValue.Set(0))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsDuplicatePorts()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(HttpPort: SettingPatchValue.Set(runtime.Settings.MixedPort))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeRejectsInvalidLogLevel()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.UpdateSettingsAsync(new AppSettingsPatch(LogLevel: SettingPatchValue.Set("trace"))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializeSurfacesRecoveredSettingsWarning()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"logLevel\":\"trace\"}");
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, null, proxy);

        try
        {
            await runtime.InitializeAsync();

            StringAssert.Contains(runtime.Snapshot.ErrorMessage, "设置文件已损坏", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(paths.SettingsFile));
            Assert.AreEqual(1, Directory.GetFiles(paths.LocalRoot, "settings.json.corrupt-*").Length);
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

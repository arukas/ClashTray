using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSystemProxyTests
{
    [TestMethod]
    public async Task ChangingProxyBindingRestoresAndReappliesOwnedSystemProxy()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings(
            BypassList: "old",
            SystemProxyEnabled: true));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.On);
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), string.Empty);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings, proxy);

        try
        {
            await runtime.InitializeAsync();
            proxy.SetState(SystemProxyState.On);
            int disableCountBeforeChange = proxy.DisableCount;
            runtime.AttachControllerForTesting(api, usingServiceCore: false);

            await runtime.UpdateSettingsAsync(new AppSettingsPatch(BypassList: SettingPatchValue.Set("new")));

            Assert.AreEqual("new", runtime.Settings.BypassList);
            Assert.AreEqual(disableCountBeforeChange + 1, proxy.DisableCount);
            Assert.AreEqual(1, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.On, proxy.State);
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
    public async Task EnablingSystemProxyBeforeCoreHealthOnlyStoresPreference()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.Off);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, null, proxy);

        try
        {
            await runtime.SetSystemProxyAsync(true);

            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.AreEqual(0, proxy.EnableCount);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
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
    public async Task CoreUnavailableRestoresActualProxyButPreservesPreference()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        TestSettingsStore settings = new TestSettingsStore(new AppSettings(SystemProxyEnabled: true));
        FakeSystemProxyController proxy = new FakeSystemProxyController(SystemProxyState.On);
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths, null, null, settings, proxy);

        try
        {
            await runtime.InitializeAsync();
            proxy.SetState(SystemProxyState.On);
            await runtime.RevokeSystemProxyForTestingAsync();

            Assert.IsTrue(runtime.Settings.SystemProxyEnabled);
            Assert.AreEqual(SystemProxyState.Off, proxy.State);
            Assert.AreEqual(SystemProxyState.Off, runtime.Snapshot.SystemProxy);
            Assert.IsGreaterThanOrEqualTo(2, proxy.DisableCount);
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

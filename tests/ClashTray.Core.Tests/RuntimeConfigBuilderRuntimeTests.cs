using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeConfigBuilderRuntimeTests
{
    [TestMethod]
    public async Task RuntimeConfigBuilderReplacesManagedSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "external-controller: 0.0.0.0:9999\nsecret: old\nmixed-port: 1111\nallow-lan: true\nipv6: true\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191, MixedPort: 8899));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "external-controller: 127.0.0.1:9191", StringComparison.Ordinal);
            StringAssert.Contains(generated, "mixed-port: 8899", StringComparison.Ordinal);
            StringAssert.Contains(generated, "allow-lan: false", StringComparison.Ordinal);
            StringAssert.Contains(generated, "ipv6: false", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("0.0.0.0:9999", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("secret: old", StringComparison.Ordinal));
            StringAssert.Contains(generated, "secret: ''", StringComparison.Ordinal);
            Assert.IsFalse(File.Exists(Path.Combine(paths.LocalRoot, "controller-secret.bin")));
            Assert.IsFalse(generated.Contains("allow-lan: true", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("ipv6: true", StringComparison.Ordinal));
            Assert.AreEqual(
                0,
                Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, "*.tmp").Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderAddsBundledExternalUiAndRemovesRemoteUiSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.ExternalUiRoot);
        await File.WriteAllTextAsync(paths.ExternalUiEntryPoint, "<!doctype html>");
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            "external-ui: old-ui\nexternal-ui-name: old\nexternal-ui-url: https://example.com/ui.zip\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191),
                externalUiPath: paths.ExternalUiRoot);
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, $"external-ui: '{paths.ExternalUiRoot}'", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("external-ui: old-ui", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("external-ui-name:", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("external-ui-url:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderSkipsExternalUiWhenBundledEntryPointIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "external-ui: remote-ui\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191),
                externalUiPath: paths.ExternalUiRoot);
            string generated = await File.ReadAllTextAsync(destination);

            Assert.IsFalse(generated.Contains("external-ui:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesInlineProxyGroupWithoutAddingFilters()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            """
            proxies:
              - { name: US-1, type: direct }
              - { name: US-2, type: direct }
              - { name: JP-1, type: direct }
            proxy-groups:
              - { name: 美国常用, type: select, proxies: [自动选择, US-1] }
            """);

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "- { name: 美国常用, type: select, proxies: [自动选择, US-1] }", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("include-all:", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("filter:", StringComparison.Ordinal));
            string original = await File.ReadAllTextAsync(source);
            StringAssert.StartsWith(generated, original.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesExistingProxyGroupFilter()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            """
            proxies:
              - name: US-1
                type: direct
            proxy-groups:
              - name: 美国常用
                type: select
                include-all: false
                filter: old-filter
                proxies:
                  - 自动选择
            """);

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "include-all: false", StringComparison.Ordinal);
            StringAssert.Contains(generated, "filter: old-filter", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("include-all: true", StringComparison.Ordinal));
            string original = await File.ReadAllTextAsync(source);
            StringAssert.StartsWith(generated, original.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderPreservesNestedPortFields()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            "proxies:\n  - name: node\n    server: example.com\n    port: 443\nport: 1000\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(source, destination, new AppSettings(HttpPort: 8899, ControllerPort: 9191));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "    port: 443", StringComparison.Ordinal);
            StringAssert.Contains(generated, "port: 8899", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("port: 1000", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderProgramTunSettingOverridesProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(
            source,
            "tun:\n  enable: true\n  stack: system\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191, TunEnabled: false));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, $"tun:{Environment.NewLine}  enable: false{Environment.NewLine}  stack: system", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("enable: true", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderAddsNestedSafeTunOverrideWhenEnableIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(root, "source.yaml");
        string destination = Path.Combine(root, "runtime.yaml");
        await File.WriteAllTextAsync(source, "tun:\n  stack: system\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildForCoreStartAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191, TunEnabled: true),
                externalUiPath: null);
            string generated = (await File.ReadAllTextAsync(destination)).Replace("\r\n", "\n", StringComparison.Ordinal);

            StringAssert.Contains(generated, "tun:\n  stack: system\n  enable: false\nproxies: []", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("\nenable: false\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderProgramTunSettingOverridesInlineProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        string destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "tun: { enable: true, stack: system }\nproxies: []\n");

        try
        {
            await RuntimeConfigBuilder.BuildAsync(
                source,
                destination,
                new AppSettings(ControllerPort: 9191, TunEnabled: false));
            string generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "tun: { enable: false, stack: system }", StringComparison.Ordinal);
            Assert.IsFalse(generated.Contains("enable: true", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunningCoreUsesProgramNetworkSettingsOverControllerConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        using RuntimeControllerHandler handler = new RuntimeControllerHandler();
        using HttpClient httpClient = new HttpClient(handler);
        MihomoApiClient api = new MihomoApiClient(httpClient, new Uri("http://127.0.0.1:9090/"), "test-secret");
        await using ClashTrayRuntime runtime = new ClashTrayRuntime(paths);

        try
        {
            runtime.AttachControllerForTesting(api, usingServiceCore: false);
            await runtime.UpdateSettingsAsync(new AppSettingsPatch(AllowLan: SettingPatchValue.Set(true), Ipv6: SettingPatchValue.Set(false)));

            Assert.IsTrue(handler.AllowLan);
            Assert.IsFalse(handler.Ipv6);
            Assert.AreEqual(1, handler.NetworkPatchCount);
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

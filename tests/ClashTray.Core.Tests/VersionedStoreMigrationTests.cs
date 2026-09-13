using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class VersionedStoreMigrationTests
{
    // Captured shape of a 0.2.0 settings.json: every field the 0.2.0 store
    // writes, no schemaVersion. Upgrading must keep every value and must not
    // reset preferences just because newer fields are absent.
    private const string Legacy020SettingsJson = """
        {
          "activeConfigurationId": "0123456789abcdef",
          "startWithWindows": true,
          "startCoreAutomatically": true,
          "httpPort": 17892,
          "socksPort": 17891,
          "mixedPort": 17890,
          "controllerPort": 19090,
          "allowLan": true,
          "ipv6": true,
          "tcpConcurrent": true,
          "logLevel": "warning",
          "bypassList": "localhost;<local>",
          "subscriptionRefreshHours": 12,
          "theme": "dark",
          "systemProxyEnabled": true,
          "tunEnabled": false,
          "disconnectConnectionsAfterProxySwitch": true,
          "nakhimovUnlocked": false
        }
        """;

    [TestMethod]
    public async Task SettingsWithoutSchemaVersionLoadAsLegacy020WithoutResettingPreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, Legacy020SettingsJson);

        try
        {
            SettingsLoadResult result = await new SettingsStore(paths).LoadWithStatusAsync();

            Assert.AreEqual(SettingsLoadStatus.Loaded, result.Status);
            AppSettings settings = result.Settings;
            Assert.AreEqual("0123456789abcdef", settings.ActiveConfigurationId);
            Assert.IsTrue(settings.StartWithWindows);
            Assert.IsTrue(settings.StartCoreAutomatically);
            Assert.AreEqual(17892, settings.HttpPort);
            Assert.AreEqual(17891, settings.SocksPort);
            Assert.AreEqual(17890, settings.MixedPort);
            Assert.AreEqual(19090, settings.ControllerPort);
            Assert.IsTrue(settings.AllowLan);
            Assert.IsTrue(settings.Ipv6);
            Assert.IsTrue(settings.TcpConcurrent);
            Assert.AreEqual("warning", settings.LogLevel);
            Assert.AreEqual("localhost;<local>", settings.BypassList);
            Assert.AreEqual(12, settings.SubscriptionRefreshHours);
            Assert.AreEqual("dark", settings.Theme);
            Assert.IsTrue(settings.SystemProxyEnabled);
            Assert.IsFalse(settings.TunEnabled);
            Assert.IsTrue(settings.DisconnectConnectionsAfterProxySwitch);
            Assert.IsFalse(settings.NakhimovUnlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavingSettingsWritesCurrentSchemaVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        SettingsStore store = new SettingsStore(paths);

        try
        {
            await store.SaveAsync(new AppSettings(AllowLan: true));

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
            Assert.AreEqual(
                SettingsFileDto.CurrentSchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.IsTrue(document.RootElement.GetProperty("allowLan").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnknownFieldsFromNewerVersionsSurviveLoadAndSave()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "schemaVersion": 99,
              "allowLan": true,
              "futureFeatureToggle": { "mode": "enabled", "rollout": 42 }
            }
            """);

        try
        {
            SettingsStore store = new SettingsStore(paths);
            AppSettings settings = await store.LoadAsync();
            Assert.IsTrue(settings.AllowLan);

            await store.SaveAsync(settings);

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
            // An older build rewrites the file as the schema it actually knows;
            // unknown fields ride along so a newer build can migrate again.
            Assert.AreEqual(
                SettingsFileDto.CurrentSchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
            JsonElement future = document.RootElement.GetProperty("futureFeatureToggle");
            Assert.AreEqual("enabled", future.GetProperty("mode").GetString());
            Assert.AreEqual(42, future.GetProperty("rollout").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LegacyFileRoundTripKeepsValuesAndAddsSchemaVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, Legacy020SettingsJson);

        try
        {
            SettingsStore store = new SettingsStore(paths);
            AppSettings settings = await store.LoadAsync();
            await store.SaveAsync(settings);

            string written = await File.ReadAllTextAsync(paths.SettingsFile);
            using JsonDocument document = JsonDocument.Parse(written);
            Assert.AreEqual(SettingsFileDto.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("0123456789abcdef", document.RootElement.GetProperty("activeConfigurationId").GetString());
            Assert.AreEqual(17892, document.RootElement.GetProperty("httpPort").GetInt32());
            Assert.AreEqual("dark", document.RootElement.GetProperty("theme").GetString());
            Assert.IsTrue(document.RootElement.GetProperty("systemProxyEnabled").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

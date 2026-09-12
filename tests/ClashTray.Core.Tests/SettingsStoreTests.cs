using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public async Task LoadFallsBackToDefaultsForInvalidPersistedSettings()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            "{\"httpPort\":0,\"logLevel\":\"trace\",\"theme\":\"neon\"}");

        try
        {
            AppSettings settings = await new SettingsStore(paths).LoadAsync();

            Assert.AreEqual(new AppSettings(), settings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CorruptSettingsAreMovedAsideWithRecoveryStatus()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, "{\"httpPort\":0}");

        try
        {
            SettingsLoadResult result = await new SettingsStore(paths).LoadWithStatusAsync();

            Assert.AreEqual(SettingsLoadStatus.Recovered, result.Status);
            Assert.AreEqual(new AppSettings(), result.Settings);
            string[] backups = Directory.GetFiles(paths.LocalRoot, "settings.json.corrupt-*");
            Assert.AreEqual(1, backups.Length);
            Assert.IsFalse(File.Exists(paths.SettingsFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveRejectsInvalidPersistedValues()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        SettingsStore store = new SettingsStore(paths);

        try
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(
                new AppSettings(LogLevel: "trace")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OversizedSettingsFallBackToDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();

        try
        {
            await File.WriteAllBytesAsync(paths.SettingsFile, new byte[256 * 1024 + 1]);

            AppSettings settings = await new SettingsStore(paths).LoadAsync();

            Assert.AreEqual(new AppSettings(), settings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProgramPreferencesRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        SettingsStore store = new SettingsStore(paths);
        AppSettings expected = new AppSettings(
            AllowLan: true,
            Ipv6: false,
            SystemProxyEnabled: true,
            TunEnabled: true);

        try
        {
            await store.SaveAsync(expected);

            Assert.AreEqual(expected, await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

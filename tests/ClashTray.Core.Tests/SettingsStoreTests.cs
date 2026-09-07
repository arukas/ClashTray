using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public async Task LoadFallsBackToDefaultsForInvalidPersistedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            "{\"httpPort\":0,\"logLevel\":\"trace\",\"theme\":\"neon\"}");

        try
        {
            var settings = await new SettingsStore(paths).LoadAsync();

            Assert.AreEqual(new AppSettings(), settings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveRejectsInvalidPersistedValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        var store = new SettingsStore(paths);

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
}

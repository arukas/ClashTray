using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeStateTests
{
    [TestMethod]
    public void BoundedLogBufferFoldsAdjacentIdenticalLines()
    {
        var buffer = new BoundedLogBuffer(2);
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "info", "same"));
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "info", "same"));
        buffer.Add(new LogEntry(DateTimeOffset.UtcNow, "mihomo", "error", "different"));

        var snapshot = buffer.Snapshot();
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(2, snapshot[0].RepeatCount);
        Assert.AreEqual("different", snapshot[1].Message);
    }

    [TestMethod]
    public void MihomoDataParserReadsMemoryAndTunState()
    {
        using var document = JsonDocument.Parse("{\"inuse\": 4096, \"tun\": {\"enable\": true}}");

        Assert.AreEqual(4096, MihomoDataParser.ParseMemoryBytes(document));
        Assert.AreEqual(true, MihomoDataParser.ParseTunEnabled(document));
    }

    [TestMethod]
    public async Task RuntimeConfigBuilderReplacesManagedSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        var source = Path.Combine(paths.ConfigurationsRoot, "source.yaml");
        var destination = Path.Combine(paths.RuntimeRoot, "mihomo", "active.yaml");
        await File.WriteAllTextAsync(source, "external-controller: 0.0.0.0:9999\nsecret: old\nmixed-port: 1111\nproxies: []\n");

        try
        {
            var builder = new RuntimeConfigBuilder(new ControllerSecretStore(paths));
            await builder.BuildAsync(source, destination, new AppSettings(ControllerPort: 9191, MixedPort: 8899));
            var generated = await File.ReadAllTextAsync(destination);

            StringAssert.Contains(generated, "external-controller: 127.0.0.1:9191");
            StringAssert.Contains(generated, "mixed-port: 8899");
            Assert.IsFalse(generated.Contains("0.0.0.0:9999", StringComparison.Ordinal));
            Assert.IsFalse(generated.Contains("secret: old", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CoreUpdaterRejectsUnapprovedSourceBeforeNetworkAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        var updater = new CoreUpdater(paths);

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

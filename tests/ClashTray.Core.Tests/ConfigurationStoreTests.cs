using ClashTray.Core;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationStoreTests
{
    private static readonly int[] ExpectedNewestItems = [2, 3];

    [TestMethod]
    public void ValidateYamlAcceptsMihomoConfiguration()
    {
        ConfigurationStore.ValidateYaml("mixed-port: 7890\nmode: rule\n"u8);
    }

    [TestMethod]
    public void ValidateYamlRejectsEmptyContent()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ConfigurationStore.ValidateYaml(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    public void BoundedBufferRetainsNewestItems()
    {
        var buffer = new BoundedBuffer<int>(2);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        CollectionAssert.AreEqual(ExpectedNewestItems, buffer.Snapshot().ToArray());
    }

    [TestMethod]
    public async Task ReloadRejectsConfigurationPathOutsideStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        var store = new ConfigurationStore(paths);
        var outsidePath = Path.Combine(root, "outside.yaml");
        await File.WriteAllTextAsync(outsidePath, "mixed-port: 7890\n");

        try
        {
            var profile = new ConfigurationProfile("outside", "outside", outsidePath, null, null, false);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReloadAsync(profile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListIgnoresMetadataWithPathTraversalIdentifier()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        var store = new ConfigurationStore(paths);
        var configurationPath = Path.Combine(paths.ConfigurationsRoot, "valid.yaml");
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
        await File.WriteAllTextAsync(
            Path.Combine(paths.ConfigurationsRoot, "malicious.json"),
            "{\"Id\":\"..\\\\outside\\\\target\",\"Name\":\"bad\",\"Path\":\""
            + configurationPath.Replace("\\", "\\\\")
            + "\",\"IsActive\":false}");

        try
        {
            var profiles = await store.ListAsync();

            Assert.AreEqual(0, profiles.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DeleteRejectsMetadataPathTraversalIdentifier()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        var store = new ConfigurationStore(paths);
        var configurationPath = Path.Combine(paths.ConfigurationsRoot, "valid.yaml");
        await File.WriteAllTextAsync(configurationPath, "mixed-port: 7890\n");
        var outsidePath = Path.Combine(root, "outside.json");
        await File.WriteAllTextAsync(outsidePath, "keep");

        try
        {
            var profile = new ConfigurationProfile(
                "..\\outside",
                "bad",
                configurationPath,
                null,
                null,
                false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.DeleteAsync(profile));
            Assert.AreEqual("keep", await File.ReadAllTextAsync(outsidePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeEndpointTests
{
    [TestMethod]
    public async Task RuntimeExposesLocalAndSavedRemoteEndpointSummaries()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            EndpointCatalogLoadResult saved = await runtime.SaveRemoteEndpointAsync(new EndpointRecord(
                remote,
                SecretReference: "office-secret"));

            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, saved.RemoteStoreStatus);
            Assert.AreEqual(2, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
            Assert.AreEqual(new EndpointId("office"), runtime.Endpoints[1].Id);
            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, runtime.EndpointStoreStatus);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeKeepsLocalEndpointWhenRemoteMetadataIsCorrupt()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.EndpointStoreFile, "{not-json");
        await using ClashTrayRuntime runtime = new(paths);

        try
        {
            EndpointCatalogLoadResult loaded = await runtime.LoadEndpointCatalogAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Recovered, loaded.RemoteStoreStatus);
            Assert.AreEqual(1, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.EndpointStoreMessage));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RuntimeRemovesRemoteEndpointWithoutTouchingLocalTarget()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));

        try
        {
            await runtime.SaveRemoteEndpointAsync(new EndpointRecord(remote));

            EndpointRemovalResult result = await runtime.RemoveRemoteEndpointAsync(remote.Id);

            Assert.IsTrue(result.Removed);
            Assert.AreEqual(1, runtime.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, runtime.Endpoints[0].Id);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateRoot() => Path.Combine(
        Path.GetTempPath(),
        "ClashTrayTests",
        Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

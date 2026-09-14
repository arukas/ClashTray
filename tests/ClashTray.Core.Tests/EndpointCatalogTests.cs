using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointCatalogTests
{
    [TestMethod]
    public async Task FirstRunAlwaysContainsThePermanentLocalEndpoint()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointDescriptor local = ControllerEndpointFactory.CreateLocal(9090);
        EndpointCatalog catalog = new(new EndpointStore(paths), local);

        try
        {
            EndpointCatalogLoadResult result = await catalog.LoadAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.FirstRun, result.RemoteStoreStatus);
            Assert.AreEqual(1, result.Endpoints.Count);
            Assert.AreEqual(local, result.Endpoints[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RemoteMetadataIsAppendedWithoutExposingSecretReferences()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        EndpointStore store = new(paths);
        EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId("office"),
            "Office",
            new Uri("https://office.example.test"));
        await store.UpsertAsync(new EndpointRecord(remote, "office-secret"));
        EndpointCatalog catalog = new(store, ControllerEndpointFactory.CreateLocal(9090));

        try
        {
            EndpointCatalogLoadResult result = await catalog.LoadAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Loaded, result.RemoteStoreStatus);
            Assert.AreEqual(2, result.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, result.Endpoints[0].Id);
            Assert.AreEqual(remote, result.Endpoints[1]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CorruptRemoteMetadataLeavesLocalEndpointAvailable()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        await File.WriteAllTextAsync(paths.EndpointStoreFile, "{not-json");
        EndpointCatalog catalog = new(
            new EndpointStore(paths),
            ControllerEndpointFactory.CreateLocal(9090));

        try
        {
            EndpointCatalogLoadResult result = await catalog.LoadAsync();

            Assert.AreEqual(EndpointStoreLoadStatus.Recovered, result.RemoteStoreStatus);
            Assert.AreEqual(1, result.Endpoints.Count);
            Assert.AreEqual(EndpointId.Local, result.Endpoints[0].Id);
            Assert.IsTrue(result.Message is not null);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void CatalogRejectsANonLocalPermanentEndpoint()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));

        try
        {
            EndpointDescriptor remote = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId("remote"),
                "Remote",
                new Uri("https://remote.example.test"));
            Assert.ThrowsExactly<ArgumentException>(() => new EndpointCatalog(
                new EndpointStore(paths),
                remote));
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

using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointCatalogCoordinatorTests
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<IAsyncDisposable> _asyncDisposables = [];
    private string? _root;

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (IAsyncDisposable asyncDisposable in _asyncDisposables)
        {
            await asyncDisposable.DisposeAsync();
        }

        if (_root is not null && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAsyncOnEmptyStoreReturnsLocalOnly()
    {
        int published = 0;
        EndpointCatalogCoordinator catalog = CreateCoordinator(() => published++);

        EndpointCatalogLoadResult result = await catalog.LoadAsync(CancellationToken.None);

        Assert.AreEqual(EndpointStoreLoadStatus.FirstRun, catalog.StoreStatus);
        Assert.AreEqual(1, catalog.Endpoints.Count);
        Assert.AreEqual(EndpointId.Local, catalog.Endpoints[0].Id);
        Assert.AreEqual(1, published);
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task SelectAsyncRejectsUnknownEndpoint()
    {
        EndpointCatalogCoordinator catalog = CreateCoordinator();

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => catalog.SelectAsync(new EndpointId("missing"), CancellationToken.None));
    }

    [TestMethod]
    public async Task TestAsyncRejectsLocalEndpoint()
    {
        EndpointCatalogCoordinator catalog = CreateCoordinator();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.TestAsync(EndpointId.Local, CancellationToken.None));
    }

    [TestMethod]
    public async Task SelectAsyncLocalDisconnectsAndReturnsNull()
    {
        EndpointCatalogCoordinator catalog = CreateCoordinator();

        EndpointSession? session = await catalog.SelectAsync(EndpointId.Local, CancellationToken.None);

        Assert.IsNull(session);
    }

    [TestMethod]
    public async Task SavedRemoteEndpointAppearsInCatalogAndStore()
    {
        EndpointCatalogCoordinator catalog = CreateCoordinator();
        EndpointRecord record = new(CreateRemoteDescriptor("edge"));

        EndpointCatalogLoadResult result = await catalog.SaveAsync(record, CancellationToken.None);

        Assert.IsTrue(catalog.Endpoints.Any(endpoint => endpoint.Id == record.Descriptor.Id));
        EndpointRecord? resolved = await catalog.ResolveRecordAsync(record.Descriptor.Id, CancellationToken.None);
        Assert.IsNotNull(resolved);
        Assert.AreEqual("edge", resolved.Descriptor.DisplayName);
        Assert.AreEqual(EndpointStoreLoadStatus.Loaded, catalog.StoreStatus);
        Assert.IsTrue(result.Endpoints.Any(endpoint => endpoint.Id == record.Descriptor.Id));
    }

    [TestMethod]
    public async Task SelectAsyncRejectsDisabledEndpoint()
    {
        EndpointCatalogCoordinator catalog = CreateCoordinator();
        EndpointRecord record = new(CreateRemoteDescriptor("edge") with { IsEnabled = false });
        await catalog.SaveAsync(record, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => catalog.SelectAsync(record.Descriptor.Id, CancellationToken.None));
    }

    private EndpointCatalogCoordinator CreateCoordinator(Action? publish = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(_root, "local"), Path.Combine(_root, "program"));
        paths.EnsureDirectories();
        OperationGate gate = new();
        EndpointSessionManager sessions = new(
            ControllerEndpointFactory.CreateLocal(9090),
            new ThrowingConnector());
        _disposables.Add(gate);
        _asyncDisposables.Add(sessions);
        return new EndpointCatalogCoordinator(
            gate,
            sessions,
            new EndpointStore(paths),
            new EndpointSecretStore(paths),
            new EndpointCertificateStore(paths),
            static () => new AppSettings(),
            publish ?? (static () => { }));
    }

    private static EndpointDescriptor CreateRemoteDescriptor(string name) =>
        new(
            new EndpointId(Guid.NewGuid().ToString("N")),
            EndpointKind.Remote,
            name,
            new Uri("https://controller.invalid/"),
            EndpointTransportSecurity.HttpsSystemTrust);

    private sealed class ThrowingConnector : IEndpointSessionConnector
    {
        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

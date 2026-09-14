using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record EndpointCatalogLoadResult(
    IReadOnlyList<EndpointDescriptor> Endpoints,
    EndpointStoreLoadStatus RemoteStoreStatus,
    string? Message);

public sealed class EndpointCatalog
{
    private readonly EndpointStore _store;
    private readonly EndpointDescriptor _localEndpoint;

    public EndpointCatalog(EndpointStore store, EndpointDescriptor localEndpoint)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localEndpoint = localEndpoint ?? throw new ArgumentNullException(nameof(localEndpoint));
        ValidateLocalEndpoint(localEndpoint);
    }

    public EndpointDescriptor LocalEndpoint => _localEndpoint;

    public async Task<EndpointCatalogLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        EndpointStoreLoadResult remote = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        List<EndpointDescriptor> endpoints = [_localEndpoint];
        endpoints.AddRange(remote.Endpoints.Select(record => record.Descriptor));
        return new EndpointCatalogLoadResult(endpoints, remote.Status, remote.Message);
    }

    private static void ValidateLocalEndpoint(EndpointDescriptor endpoint)
    {
        if (endpoint.Id != EndpointId.Local
            || endpoint.Kind != EndpointKind.Local)
        {
            throw new ArgumentException(
                "Endpoint catalog requires the permanent local endpoint.",
                nameof(endpoint));
        }

        EndpointDescriptorValidator.ValidateForActiveSession(endpoint);
    }
}

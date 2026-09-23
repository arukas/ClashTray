using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Owns the remote endpoint catalog: metadata/credential/certificate stores,
/// provisioning and removal coordination, catalog load, and reconciliation of
/// the catalog against the live session. The session manager itself stays with
/// <see cref="ClashTrayRuntime"/> because its transport construction is
/// entangled with the runtime-owned controller pipeline; mutation admission
/// stays on the runtime's exclusive gate lane.
/// </summary>
internal sealed class EndpointCatalogCoordinator
{
    private readonly OperationGate _operationGate;
    private readonly EndpointSessionManager _sessions;
    private readonly EndpointStore _store;
    private readonly EndpointSecretStore _secretStore;
    private readonly EndpointCertificateStore _certificateStore;
    private readonly EndpointRemovalCoordinator _removalCoordinator;
    private readonly EndpointProvisioningCoordinator _provisioningCoordinator;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action _publish;
    private IReadOnlyList<EndpointDescriptor> _remoteEndpointDescriptors = [];
    private EndpointStoreLoadStatus _storeStatus = EndpointStoreLoadStatus.FirstRun;
    private string? _storeMessage;

    public EndpointCatalogCoordinator(
        OperationGate operationGate,
        EndpointSessionManager sessions,
        EndpointStore store,
        EndpointSecretStore secretStore,
        EndpointCertificateStore certificateStore,
        Func<AppSettings> settingsAccessor,
        Action publish)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(certificateStore);
        ArgumentNullException.ThrowIfNull(settingsAccessor);
        ArgumentNullException.ThrowIfNull(publish);
        _operationGate = operationGate;
        _sessions = sessions;
        _store = store;
        _secretStore = secretStore;
        _certificateStore = certificateStore;
        _settingsAccessor = settingsAccessor;
        _publish = publish;
        _removalCoordinator = new EndpointRemovalCoordinator(
            store,
            secretStore,
            certificateStore,
            sessions);
        _provisioningCoordinator = new EndpointProvisioningCoordinator(
            store,
            secretStore,
            certificateStore);
    }

    internal IReadOnlyList<EndpointDescriptor> Endpoints
    {
        get
        {
            List<EndpointDescriptor> endpoints =
            [ControllerEndpointFactory.CreateLocal(_settingsAccessor().ControllerPort)];
            endpoints.AddRange(_remoteEndpointDescriptors);
            return endpoints;
        }
    }

    internal EndpointStoreLoadStatus StoreStatus => _storeStatus;

    internal string? StoreMessage => _storeMessage;

    internal async Task<EndpointCatalogLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        EndpointCatalog catalog = new(
            _store,
            ControllerEndpointFactory.CreateLocal(_settingsAccessor().ControllerPort));
        EndpointCatalogLoadResult result = await catalog.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _remoteEndpointDescriptors = result.Endpoints
            .Where(endpoint => endpoint.Id != EndpointId.Local)
            .ToArray();
        _storeStatus = result.RemoteStoreStatus;
        _storeMessage = result.Message;
        await ReconcileSessionAsync(result.Endpoints).ConfigureAwait(false);
        _publish();
        return result;
    }

    internal async Task<EndpointSession?> SelectAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        if (endpointId == EndpointId.Local)
        {
            await _sessions.DisconnectAsync().ConfigureAwait(false);
            return null;
        }

        EndpointDescriptor endpoint = RequireEnabledEndpoint(endpointId);
        return await _sessions.SelectAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<EndpointHandshakeResult> TestAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        if (endpointId == EndpointId.Local)
        {
            throw new InvalidOperationException("只读连接测试仅适用于远程端点。");
        }

        EndpointDescriptor endpoint = RequireEnabledEndpoint(endpointId);
        return await _sessions.TestAsync(endpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task DisconnectAsync() => _sessions.DisconnectAsync();

    internal async Task<EndpointCatalogLoadResult> SaveAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using (OperationGate.Lease operationLease = await _operationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await _store.UpsertAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<EndpointCatalogLoadResult> ProvisionAsync(
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using (OperationGate.Lease operationLease = await _operationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await _provisioningCoordinator.ProvisionAsync(
                    descriptor,
                    secret,
                    customCaCertificate,
                    insecureHttpAcknowledgedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<EndpointCatalogLoadResult> UpdateAsync(
        EndpointId endpointId,
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        using (OperationGate.Lease operationLease = await _operationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            EndpointRecord? previousRecord = await ResolveRecordAsync(endpointId, cancellationToken)
                .ConfigureAwait(false);
            string? previousSecret = null;
            if (previousRecord?.SecretReference is string previousSecretReference)
            {
                previousSecret = await _secretStore.GetAsync(previousSecretReference, cancellationToken)
                    .ConfigureAwait(false);
            }

            byte[]? previousCertificate = null;
            if (previousRecord?.CertificateReference is string previousCertificateReference)
            {
                previousCertificate = await _certificateStore.GetAsync(
                        previousCertificateReference,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            bool secretChanged = secret is not null
                && !string.Equals(
                    previousSecret,
                    string.IsNullOrEmpty(secret) ? null : secret,
                    StringComparison.Ordinal);
            bool certificateChanged = customCaCertificate.HasValue
                && (previousCertificate is null
                    || !previousCertificate.AsSpan().SequenceEqual(customCaCertificate.Value.Span));

            await _provisioningCoordinator.UpdateAsync(
                    endpointId,
                    descriptor,
                    secret,
                    customCaCertificate,
                    insecureHttpAcknowledgedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            if ((secretChanged || certificateChanged)
                && _sessions.Status.Endpoint.Id == endpointId)
            {
                await _sessions.DisconnectAsync().ConfigureAwait(false);
            }

            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<EndpointRemovalResult> RemoveAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            EndpointRemovalResult result = await _removalCoordinator.RemoveAsync(
                    endpointId,
                    cancellationToken)
                .ConfigureAwait(false);
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    internal async Task<EndpointRecord?> ResolveRecordAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        EndpointStoreLoadResult loaded = await _store.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status == EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message ?? "端点元数据无法读取。");
        }

        return loaded.Endpoints.FirstOrDefault(endpoint => endpoint.Descriptor.Id == endpointId);
    }

    private EndpointDescriptor RequireEnabledEndpoint(EndpointId endpointId)
    {
        EndpointDescriptor? endpoint = Endpoints.FirstOrDefault(candidate => candidate.Id == endpointId);
        if (endpoint is null)
        {
            throw new KeyNotFoundException($"未找到端点 {endpointId.Value}。");
        }

        if (!endpoint.IsEnabled)
        {
            throw new InvalidOperationException($"端点 {endpoint.DisplayName} 已被禁用。");
        }

        return endpoint;
    }

    private async Task ReconcileSessionAsync(
        IReadOnlyList<EndpointDescriptor> catalogEndpoints)
    {
        EndpointSessionStatusEventArgs status = _sessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return;
        }

        EndpointDescriptor? catalogEndpoint = catalogEndpoints.FirstOrDefault(endpoint =>
            endpoint.Id == status.Endpoint.Id);
        if (catalogEndpoint is null || catalogEndpoint != status.Endpoint)
        {
            await _sessions.DisconnectAsync().ConfigureAwait(false);
        }
    }
}

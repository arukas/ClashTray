using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record EndpointRemovalResult(
    bool Removed,
    bool SessionDisconnected,
    bool SecretRemoved,
    bool CertificateRemoved);

/// <summary>
/// Removes a remote endpoint and the protected material that is no longer referenced.
/// </summary>
public sealed class EndpointRemovalCoordinator
{
    private readonly EndpointStore _endpointStore;
    private readonly EndpointSecretStore _secretStore;
    private readonly EndpointCertificateStore _certificateStore;
    private readonly EndpointSessionManager? _sessionManager;

    public EndpointRemovalCoordinator(
        EndpointStore endpointStore,
        EndpointSecretStore secretStore,
        EndpointCertificateStore certificateStore,
        EndpointSessionManager? sessionManager = null)
    {
        _endpointStore = endpointStore ?? throw new ArgumentNullException(nameof(endpointStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
        _sessionManager = sessionManager;
    }

    public async Task<EndpointRemovalResult> RemoveAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        if (endpointId == EndpointId.Local)
        {
            throw new ArgumentException("本机端点不能删除。", nameof(endpointId));
        }

        EndpointStoreLoadResult loaded = await _endpointStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status is EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message ?? "端点元数据无法读取。");
        }

        EndpointRecord? target = loaded.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Descriptor.Id == endpointId);
        if (target is null)
        {
            return new EndpointRemovalResult(
                Removed: false,
                SessionDisconnected: false,
                SecretRemoved: false,
                CertificateRemoved: false);
        }

        bool sessionDisconnected = await DisconnectIfSelectedAsync(endpointId).ConfigureAwait(false);
        EndpointRecord[] remaining = loaded.Endpoints
            .Where(endpoint => endpoint.Descriptor.Id != endpointId)
            .ToArray();

        bool secretRemoved = false;
        if (target.SecretReference is not null
            && !IsReferencedByAnotherEndpoint(target.SecretReference, remaining, static endpoint => endpoint.SecretReference))
        {
            secretRemoved = await _secretStore.DeleteAsync(
                    target.SecretReference,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        bool certificateRemoved = false;
        if (target.CertificateReference is not null
            && !IsReferencedByAnotherEndpoint(
                target.CertificateReference,
                remaining,
                static endpoint => endpoint.CertificateReference))
        {
            certificateRemoved = await _certificateStore.DeleteAsync(
                    target.CertificateReference,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        bool removed = await _endpointStore.DeleteAsync(endpointId, cancellationToken)
            .ConfigureAwait(false);
        return new EndpointRemovalResult(
            removed,
            sessionDisconnected,
            secretRemoved,
            certificateRemoved);
    }

    private async Task<bool> DisconnectIfSelectedAsync(EndpointId endpointId)
    {
        if (_sessionManager is null)
        {
            return false;
        }

        EndpointSessionStatusEventArgs status = _sessionManager.Status;
        EndpointSession? current = _sessionManager.Current;
        if (status.Endpoint.Id != endpointId
            && current?.Endpoint.Id != endpointId)
        {
            return false;
        }

        await _sessionManager.DisconnectAsync().ConfigureAwait(false);
        return true;
    }

    private static bool IsReferencedByAnotherEndpoint(
        string reference,
        IEnumerable<EndpointRecord> endpoints,
        Func<EndpointRecord, string?> referenceSelector) =>
        endpoints.Any(endpoint => string.Equals(
            referenceSelector(endpoint),
            reference,
            StringComparison.OrdinalIgnoreCase));
}

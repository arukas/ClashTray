using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Creates a new remote endpoint and stores its optional secret/CA without connecting.
/// </summary>
public sealed class EndpointProvisioningCoordinator
{
    private readonly EndpointStore _endpointStore;
    private readonly EndpointSecretStore _secretStore;
    private readonly EndpointCertificateStore _certificateStore;

    public EndpointProvisioningCoordinator(
        EndpointStore endpointStore,
        EndpointSecretStore secretStore,
        EndpointCertificateStore certificateStore)
    {
        _endpointStore = endpointStore ?? throw new ArgumentNullException(nameof(endpointStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _certificateStore = certificateStore ?? throw new ArgumentNullException(nameof(certificateStore));
    }

    public async Task<EndpointRecord> ProvisionAsync(
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        EndpointDescriptorValidator.ValidateForActiveSession(descriptor);
        if (descriptor.Kind != EndpointKind.Remote)
        {
            throw new ArgumentException("Only remote endpoints can be provisioned.", nameof(descriptor));
        }

        EndpointStoreLoadResult loaded = await _endpointStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status is EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message ?? "端点元数据无法读取。");
        }

        if (loaded.Endpoints.Any(endpoint => endpoint.Descriptor.Id == descriptor.Id))
        {
            throw new ArgumentException("端点 ID 已存在。", nameof(descriptor));
        }

        bool isCustomHttps = descriptor.Security == EndpointTransportSecurity.HttpsCustomCertificate;
        bool isExplicitHttp = descriptor.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed;
        if (isExplicitHttp != (insecureHttpAcknowledgedAtUtc is not null))
        {
            throw new ArgumentException(
                "HTTP 风险确认时间必须与显式 HTTP 端点安全模式一致。",
                nameof(insecureHttpAcknowledgedAtUtc));
        }

        if (isCustomHttps != (customCaCertificate is not null))
        {
            throw new ArgumentException(
                "自定义 CA 必须且只能用于 HTTPS 自定义信任端点。",
                nameof(customCaCertificate));
        }

        string? secretReference = string.IsNullOrEmpty(secret)
            ? null
            : EndpointReferenceValidator.Normalize(
                $"{descriptor.Id.Value}-secret",
                nameof(descriptor));
        string? certificateReference = customCaCertificate is null
            ? null
            : EndpointReferenceValidator.Normalize(
                $"{descriptor.Id.Value}-ca",
                nameof(descriptor));
        EndpointRecord record = new(
            descriptor,
            secretReference,
            certificateReference,
            insecureHttpAcknowledgedAtUtc);

        bool secretSaved = false;
        bool certificateSaved = false;
        try
        {
            if (secretReference is not null)
            {
                await _secretStore.SetAsync(secretReference, secret!, cancellationToken)
                    .ConfigureAwait(false);
                secretSaved = true;
            }

            if (certificateReference is not null)
            {
                await _certificateStore.SetAsync(
                        certificateReference,
                        customCaCertificate!.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                certificateSaved = true;
            }

            await _endpointStore.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch
        {
            if (certificateSaved)
            {
                await TryDeleteCertificateAsync(certificateReference).ConfigureAwait(false);
            }

            if (secretSaved)
            {
                await TryDeleteSecretAsync(secretReference).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task TryDeleteSecretAsync(string? reference)
    {
        if (reference is null)
        {
            return;
        }

        try
        {
            await _secretStore.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
        catch (UnauthorizedAccessException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
        catch (ArgumentException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
    }

    private async Task TryDeleteCertificateAsync(string? reference)
    {
        if (reference is null)
        {
            return;
        }

        try
        {
            await _certificateStore.DeleteAsync(reference, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
        catch (UnauthorizedAccessException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
        catch (ArgumentException)
        {
            // The original failure is more actionable; the next load can report the orphaned reference.
        }
    }
}

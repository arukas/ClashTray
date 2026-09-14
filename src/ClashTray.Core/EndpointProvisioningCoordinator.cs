using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Creates or updates a remote endpoint and stores its optional secret/CA without connecting.
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

    public async Task<EndpointRecord> UpdateAsync(
        EndpointId endpointId,
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        string normalizedEndpointId = EndpointReferenceValidator.Normalize(
            endpointId.Value,
            nameof(endpointId));
        ArgumentNullException.ThrowIfNull(descriptor);
        EndpointDescriptorValidator.ValidateForActiveSession(descriptor);
        if (descriptor.Kind != EndpointKind.Remote
            || !string.Equals(
                normalizedEndpointId,
                descriptor.Id.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The updated endpoint must keep the existing remote endpoint ID.",
                nameof(descriptor));
        }

        EndpointStoreLoadResult loaded = await _endpointStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status is EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message ?? "端点元数据无法读取。");
        }

        EndpointRecord existing = loaded.Endpoints.FirstOrDefault(endpoint =>
                string.Equals(
                    endpoint.Descriptor.Id.Value,
                    normalizedEndpointId,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException("要编辑的远程端点不存在。");

        bool isCustomHttps = descriptor.Security == EndpointTransportSecurity.HttpsCustomCertificate;
        bool isExplicitHttp = descriptor.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed;
        if (isExplicitHttp != (insecureHttpAcknowledgedAtUtc is not null))
        {
            throw new ArgumentException(
                "HTTP 风险确认时间必须与显式 HTTP 端点安全模式一致。",
                nameof(insecureHttpAcknowledgedAtUtc));
        }

        string? desiredSecretReference = secret is null
            ? existing.SecretReference
            : string.IsNullOrEmpty(secret)
                ? null
                : existing.SecretReference
                    ?? EndpointReferenceValidator.Normalize(
                        $"{normalizedEndpointId}-secret",
                        nameof(endpointId));
        string? desiredCertificateReference = isCustomHttps
            ? customCaCertificate is not null
                ? existing.CertificateReference
                    ?? EndpointReferenceValidator.Normalize(
                        $"{normalizedEndpointId}-ca",
                        nameof(endpointId))
                : existing.CertificateReference
            : null;

        if (isCustomHttps && desiredCertificateReference is null)
        {
            throw new ArgumentException(
                "自定义证书端点必须提供 CA，或保留已有 CA。",
                nameof(customCaCertificate));
        }

        string? previousSecret = existing.SecretReference is null
            ? null
            : await _secretStore.GetAsync(existing.SecretReference, cancellationToken)
                .ConfigureAwait(false);
        byte[]? previousCertificate = existing.CertificateReference is null
            ? null
            : await _certificateStore.GetAsync(existing.CertificateReference, cancellationToken)
                .ConfigureAwait(false);

        bool secretSaved = false;
        bool certificateSaved = false;
        EndpointRecord updated = new(
            descriptor,
            desiredSecretReference,
            desiredCertificateReference,
            insecureHttpAcknowledgedAtUtc,
            existing.CreatedAtUtc,
            existing.UpdatedAtUtc);

        try
        {
            if (secret is not null && desiredSecretReference is not null)
            {
                await _secretStore.SetAsync(desiredSecretReference, secret, cancellationToken)
                    .ConfigureAwait(false);
                secretSaved = true;
            }

            if (customCaCertificate is not null && desiredCertificateReference is not null)
            {
                await _certificateStore.SetAsync(
                        desiredCertificateReference,
                        customCaCertificate.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                certificateSaved = true;
            }

            await _endpointStore.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (certificateSaved)
            {
                await RestoreCertificateAsync(
                    desiredCertificateReference,
                    existing.CertificateReference,
                    previousCertificate).ConfigureAwait(false);
            }

            if (secretSaved)
            {
                await RestoreSecretAsync(
                    desiredSecretReference,
                    existing.SecretReference,
                    previousSecret).ConfigureAwait(false);
            }

            throw;
        }

        if (existing.CertificateReference is not null
            && !string.Equals(
                existing.CertificateReference,
                desiredCertificateReference,
                StringComparison.Ordinal))
        {
            await TryDeleteCertificateAsync(existing.CertificateReference).ConfigureAwait(false);
        }

        if (existing.SecretReference is not null
            && !string.Equals(
                existing.SecretReference,
                desiredSecretReference,
                StringComparison.Ordinal))
        {
            await TryDeleteSecretAsync(existing.SecretReference).ConfigureAwait(false);
        }

        return updated;
    }

    private async Task RestoreSecretAsync(
        string? targetReference,
        string? previousReference,
        string? previousSecret)
    {
        if (targetReference is null)
        {
            return;
        }

        if (string.Equals(targetReference, previousReference, StringComparison.Ordinal)
            && previousSecret is not null)
        {
            try
            {
                await _secretStore.SetAsync(targetReference, previousSecret, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        await TryDeleteSecretAsync(targetReference).ConfigureAwait(false);
    }

    private async Task RestoreCertificateAsync(
        string? targetReference,
        string? previousReference,
        byte[]? previousCertificate)
    {
        if (targetReference is null)
        {
            return;
        }

        if (string.Equals(targetReference, previousReference, StringComparison.Ordinal)
            && previousCertificate is not null)
        {
            try
            {
                await _certificateStore.SetAsync(
                        targetReference,
                        previousCertificate,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ArgumentException)
            {
            }
        }

        await TryDeleteCertificateAsync(targetReference).ConfigureAwait(false);
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

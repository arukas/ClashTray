using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class EndpointStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxEndpoints = 32;
    private const int MaxStoreBytes = 512 * 1024;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public EndpointStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureDirectories();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Endpoint metadata is an input boundary; malformed or incompatible data is quarantined without exposing it to the runtime.")]
    public async Task<EndpointStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.EndpointStoreFile))
        {
            return new EndpointStoreLoadResult([], EndpointStoreLoadStatus.FirstRun, false, null);
        }

        try
        {
            if (new FileInfo(_paths.EndpointStoreFile).Length > MaxStoreBytes)
            {
                throw new InvalidDataException("端点文件超过支持的大小限制。");
            }

            await using FileStream stream = File.OpenRead(_paths.EndpointStoreFile);
            PersistedEndpointSet persisted = await JsonSerializer.DeserializeAsync<PersistedEndpointSet>(
                    stream,
                    _options,
                    cancellationToken)
                ?? throw new InvalidDataException("端点文件为空。");
            EndpointRecord[] endpoints = ToDomain(persisted);
            return new EndpointStoreLoadResult(endpoints, EndpointStoreLoadStatus.Loaded, false, null);
        }
        catch (JsonException)
        {
            return QuarantineCorrupt("端点文件格式无效，原文件已隔离。");
        }
        catch (FormatException)
        {
            return QuarantineCorrupt("端点文件包含无效的 URI 或编码，原文件已隔离。");
        }
        catch (InvalidDataException)
        {
            return QuarantineCorrupt("端点文件内容无效，原文件已隔离。");
        }
        catch (ArgumentException)
        {
            return QuarantineCorrupt("端点文件内容无效，原文件已隔离。");
        }
        catch (InvalidOperationException)
        {
            return QuarantineCorrupt("端点文件违反传输安全规则，原文件已隔离。");
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointStoreLoadResult(
                [],
                EndpointStoreLoadStatus.ReadFailed,
                false,
                "端点文件无权读取，已保留原文件；远程端点已停用。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new EndpointStoreLoadResult(
                [],
                EndpointStoreLoadStatus.ReadFailed,
                false,
                "端点文件无法读取，已保留原文件；远程端点已停用。请检查磁盘或文件权限。");
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<EndpointRecord> endpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ValidateDomain(endpoints);
        PersistedEndpointSet persisted = new(
            CurrentSchemaVersion,
            endpoints.Select(endpoint => new PersistedEndpoint(
                endpoint.Descriptor.Id.Value,
                endpoint.Descriptor.DisplayName,
                endpoint.Descriptor.BaseUri.AbsoluteUri,
                endpoint.Descriptor.Security,
                endpoint.Descriptor.IsEnabled,
                endpoint.SecretReference,
                endpoint.CertificateReference)).ToArray());
        await AtomicFile.WriteJsonAsync(
            _paths.EndpointStoreFile,
            persisted,
            _options,
            cancellationToken);
    }

    public async Task UpsertAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EndpointStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        List<EndpointRecord> endpoints = loaded.Endpoints
            .Where(existing => !string.Equals(
                existing.Descriptor.Id.Value,
                endpoint.Descriptor.Id.Value,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        endpoints.Add(endpoint);
        await SaveAsync(endpoints, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        EndpointId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        EndpointStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        List<EndpointRecord> remaining = loaded.Endpoints
            .Where(endpoint => !string.Equals(
                endpoint.Descriptor.Id.Value,
                id.Value,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool removed = remaining.Count != loaded.Endpoints.Count;
        if (removed)
        {
            await SaveAsync(remaining, cancellationToken);
        }

        return removed;
    }

    private static EndpointRecord[] ToDomain(PersistedEndpointSet persisted)
    {
        if (persisted.SchemaVersion != CurrentSchemaVersion
            || persisted.Endpoints is null)
        {
            throw new InvalidDataException("端点文件 schema 无效。");
        }

        EndpointRecord[] endpoints = persisted.Endpoints
            .Select(ToDomain)
            .ToArray();
        ValidateDomain(endpoints);
        return endpoints;
    }

    private static EndpointRecord ToDomain(PersistedEndpoint persisted)
    {
        EndpointId id = new(EndpointReferenceValidator.Normalize(persisted.Id, nameof(persisted.Id)));
        if (id == EndpointId.Local)
        {
            throw new InvalidDataException("本机端点不能写入远程端点存储。");
        }

        bool allowExplicitHttp = persisted.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed;
        EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
            id,
            persisted.DisplayName,
            new Uri(persisted.BaseUri, UriKind.Absolute),
            allowExplicitHttp,
            persisted.IsEnabled);
        if (persisted.Security == EndpointTransportSecurity.HttpsCustomCertificate)
        {
            if (!descriptor.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("自定义证书只能用于 HTTPS 端点。");
            }

            descriptor = descriptor with { Security = EndpointTransportSecurity.HttpsCustomCertificate };
        }
        else if (descriptor.Security != persisted.Security)
        {
            throw new InvalidDataException("端点传输安全类型与 URI 不匹配。");
        }

        return new EndpointRecord(
            descriptor,
            NormalizeOptionalReference(persisted.SecretReference, nameof(persisted.SecretReference)),
            NormalizeOptionalReference(persisted.CertificateReference, nameof(persisted.CertificateReference)));
    }

    private static void ValidateDomain(IReadOnlyList<EndpointRecord> endpoints)
    {
        if (endpoints.Count > MaxEndpoints)
        {
            throw new ArgumentException("远程端点数量不能超过 32 个。", nameof(endpoints));
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        foreach (EndpointRecord endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(endpoint.Descriptor);
            EndpointDescriptor descriptor = endpoint.Descriptor;
            string id = EndpointReferenceValidator.Normalize(descriptor.Id.Value, nameof(descriptor.Id));
            if (descriptor.Kind != EndpointKind.Remote
                || string.Equals(id, EndpointId.Local.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Endpoint store accepts remote endpoints only.", nameof(endpoints));
            }

            if (!ids.Add(id))
            {
                throw new ArgumentException("Endpoint IDs must be unique.", nameof(endpoints));
            }

            EndpointDescriptor normalized = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId(id),
                descriptor.DisplayName,
                descriptor.BaseUri,
                descriptor.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed,
                descriptor.IsEnabled);
            if (descriptor.Security == EndpointTransportSecurity.HttpsCustomCertificate)
            {
                if (!normalized.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Custom endpoint certificates require HTTPS.", nameof(endpoints));
                }
            }
            else if (normalized.Security != descriptor.Security)
            {
                throw new ArgumentException("Endpoint security does not match its URI.", nameof(endpoints));
            }

            _ = NormalizeOptionalReference(endpoint.SecretReference, nameof(endpoint.SecretReference));
            if (descriptor.Security == EndpointTransportSecurity.HttpsCustomCertificate
                && string.IsNullOrWhiteSpace(endpoint.CertificateReference))
            {
                throw new ArgumentException("Custom certificate endpoints require a certificate reference.", nameof(endpoints));
            }

            string? certificateReference = NormalizeOptionalReference(
                endpoint.CertificateReference,
                nameof(endpoint.CertificateReference));
            if (descriptor.Security != EndpointTransportSecurity.HttpsCustomCertificate
                && certificateReference is not null)
            {
                throw new ArgumentException(
                    "Certificate references require custom HTTPS trust.",
                    nameof(endpoints));
            }
        }
    }

    private static string? NormalizeOptionalReference(string? reference, string parameterName) =>
        string.IsNullOrWhiteSpace(reference)
            ? null
            : EndpointReferenceValidator.Normalize(reference, parameterName);

    private EndpointStoreLoadResult QuarantineCorrupt(string message)
    {
        string quarantinePath =
            $"{_paths.EndpointStoreFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.EndpointStoreFile, quarantinePath);
            return new EndpointStoreLoadResult([], EndpointStoreLoadStatus.Recovered, true, message);
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointStoreLoadResult(
                [],
                EndpointStoreLoadStatus.ReadFailed,
                false,
                "端点文件损坏但无权隔离，已停用远程端点；原文件未覆盖。");
        }
        catch (IOException)
        {
            return new EndpointStoreLoadResult(
                [],
                EndpointStoreLoadStatus.ReadFailed,
                false,
                "端点文件损坏且无法隔离，已停用远程端点；原文件未覆盖。");
        }
    }

    private sealed record PersistedEndpointSet(
        int SchemaVersion,
        IReadOnlyList<PersistedEndpoint>? Endpoints);

    private sealed record PersistedEndpoint(
        string Id,
        string DisplayName,
        string BaseUri,
        EndpointTransportSecurity Security,
        bool IsEnabled,
        string? SecretReference,
        string? CertificateReference);
}

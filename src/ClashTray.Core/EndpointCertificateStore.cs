using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ClashTray.Core;

public sealed class EndpointCertificateStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxCertificateBytes = 128 * 1024;
    private const int MaxStoreBytes = 512 * 1024;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public EndpointCertificateStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureDirectories();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Custom CA material is an input boundary; malformed certificate data is quarantined without being trusted.")]
    public async Task<EndpointCertificateStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.EndpointCertificatesFile))
        {
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.FirstRun,
                false,
                null);
        }

        try
        {
            if (new FileInfo(_paths.EndpointCertificatesFile).Length > MaxStoreBytes)
            {
                throw new InvalidDataException("端点证书文件超过支持的大小限制。");
            }

            await using FileStream stream = File.OpenRead(_paths.EndpointCertificatesFile);
            PersistedCertificates persisted = await JsonSerializer.DeserializeAsync<PersistedCertificates>(
                    stream,
                    _options,
                    cancellationToken)
                ?? throw new InvalidDataException("端点证书文件为空。");
            if (persisted.SchemaVersion != CurrentSchemaVersion || persisted.Certificates is null)
            {
                throw new InvalidDataException("端点证书文件 schema 无效。");
            }

            Dictionary<string, byte[]> certificates = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string reference, string encodedCertificate) in persisted.Certificates)
            {
                string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
                byte[] certificate = NormalizeCertificate(Convert.FromBase64String(encodedCertificate));
                if (!certificates.TryAdd(normalizedReference, certificate))
                {
                    throw new InvalidDataException("端点证书引用重复。");
                }
            }

            return new EndpointCertificateStoreLoadResult(
                certificates,
                EndpointCertificateStoreLoadStatus.Loaded,
                false,
                null);
        }
        catch (JsonException)
        {
            return QuarantineCorrupt("端点证书文件格式无效，原文件已隔离。");
        }
        catch (CryptographicException)
        {
            return QuarantineCorrupt("端点证书无法解析，原文件已隔离。");
        }
        catch (FormatException)
        {
            return QuarantineCorrupt("端点证书编码无效，原文件已隔离。");
        }
        catch (InvalidDataException)
        {
            return QuarantineCorrupt("端点证书文件内容无效，原文件已隔离。");
        }
        catch (ArgumentException)
        {
            return QuarantineCorrupt("端点证书文件内容无效，原文件已隔离。");
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.ReadFailed,
                false,
                "端点证书文件无权读取；自定义证书已停用。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.ReadFailed,
                false,
                "端点证书文件无法读取；自定义证书已停用。请检查磁盘或文件权限。");
        }
    }

    public async Task<byte[]?> GetAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        EndpointCertificateStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointCertificateStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        return loaded.Certificates.TryGetValue(normalizedReference, out byte[]? certificate)
            ? certificate.ToArray()
            : null;
    }

    public async Task SetAsync(
        string reference,
        ReadOnlyMemory<byte> certificate,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        byte[] normalizedCertificate = NormalizeCertificate(certificate.ToArray());
        EndpointCertificateStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointCertificateStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        Dictionary<string, byte[]> certificates = loaded.Certificates
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        certificates[normalizedReference] = normalizedCertificate;
        await SaveAsync(certificates, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        EndpointCertificateStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointCertificateStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        Dictionary<string, byte[]> certificates = loaded.Certificates
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        bool removed = certificates.Remove(normalizedReference);
        if (removed)
        {
            await SaveAsync(certificates, cancellationToken);
        }

        return removed;
    }

    private async Task SaveAsync(
        IReadOnlyDictionary<string, byte[]> certificates,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> encodedCertificates = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string reference, byte[] certificate) in certificates)
        {
            string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
            encodedCertificates[normalizedReference] = Convert.ToBase64String(NormalizeCertificate(certificate));
        }

        await AtomicFile.WriteJsonAsync(
            _paths.EndpointCertificatesFile,
            new PersistedCertificates(CurrentSchemaVersion, encodedCertificates),
            _options,
            cancellationToken);
    }

    private static byte[] NormalizeCertificate(byte[] certificateBytes)
    {
        ArgumentNullException.ThrowIfNull(certificateBytes);
        if (certificateBytes.Length == 0 || certificateBytes.Length > MaxCertificateBytes)
        {
            throw new ArgumentException("Endpoint certificate size is invalid.", nameof(certificateBytes));
        }

        using X509Certificate2 certificate = ParseCertificate(certificateBytes);
        return certificate.Export(X509ContentType.Cert);
    }

    private static X509Certificate2 ParseCertificate(byte[] certificateBytes)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(certificateBytes);
        }
        catch (CryptographicException)
        {
            string pem = Encoding.UTF8.GetString(certificateBytes);
            return X509Certificate2.CreateFromPem(pem);
        }
    }

    private EndpointCertificateStoreLoadResult QuarantineCorrupt(string message)
    {
        string quarantinePath =
            $"{_paths.EndpointCertificatesFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.EndpointCertificatesFile, quarantinePath);
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.Recovered,
                true,
                message);
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.ReadFailed,
                false,
                "端点证书文件损坏但无法隔离；原文件未覆盖。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new EndpointCertificateStoreLoadResult(
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
                EndpointCertificateStoreLoadStatus.ReadFailed,
                false,
                "端点证书文件损坏且无法隔离；原文件未覆盖。请检查磁盘后重试。");
        }
    }

    private sealed record PersistedCertificates(
        int SchemaVersion,
        IReadOnlyDictionary<string, string>? Certificates);
}

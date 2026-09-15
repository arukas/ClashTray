using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTray.Core;

public sealed class EndpointSecretStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxStoreBytes = 512 * 1024;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public EndpointSecretStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureDirectories();
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Protected endpoint secrets are an input boundary; malformed or undecryptable data is quarantined without returning secret material.")]
    public async Task<EndpointSecretStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.EndpointSecretsFile))
        {
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.FirstRun,
                false,
                null);
        }

        try
        {
            if (new FileInfo(_paths.EndpointSecretsFile).Length > MaxStoreBytes)
            {
                throw new InvalidDataException("端点 secret 文件超过支持的大小限制。");
            }

            await using FileStream stream = File.OpenRead(_paths.EndpointSecretsFile);
            PersistedSecrets persisted = await JsonSerializer.DeserializeAsync<PersistedSecrets>(
                    stream,
                    _options,
                    cancellationToken)
                ?? throw new InvalidDataException("端点 secret 文件为空。");
            if (persisted.SchemaVersion != CurrentSchemaVersion || persisted.Secrets is null)
            {
                throw new InvalidDataException("端点 secret 文件 schema 无效。");
            }

            Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string reference, string protectedValue) in persisted.Secrets)
            {
                string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
                string secret = WindowsDataProtection.UnprotectString(protectedValue);
                ValidateSecret(secret);
                if (!secrets.TryAdd(normalizedReference, secret))
                {
                    throw new InvalidDataException("端点 secret 引用重复。");
                }
            }

            return new EndpointSecretStoreLoadResult(secrets, EndpointSecretStoreLoadStatus.Loaded, false, null);
        }
        catch (JsonException)
        {
            return QuarantineCorrupt("端点 secret 文件格式无效，原文件已隔离。");
        }
        catch (CryptographicException)
        {
            return QuarantineCorrupt("端点 secret 无法解密，原文件已隔离。");
        }
        catch (FormatException)
        {
            return QuarantineCorrupt("端点 secret 文件编码无效，原文件已隔离。");
        }
        catch (InvalidDataException)
        {
            return QuarantineCorrupt("端点 secret 文件内容无效，原文件已隔离。");
        }
        catch (ArgumentException)
        {
            return QuarantineCorrupt("端点 secret 文件内容无效，原文件已隔离。");
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.ReadFailed,
                false,
                "端点 secret 文件无权读取；远程端点凭据已停用。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.ReadFailed,
                false,
                "端点 secret 文件无法读取；远程端点凭据已停用。请检查磁盘或文件权限。");
        }
    }

    public async Task<string?> GetAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        EndpointSecretStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointSecretStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        return loaded.Secrets.TryGetValue(normalizedReference, out string? secret) ? secret : null;
    }

    public async Task SetAsync(
        string reference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        ValidateSecret(secret);
        EndpointSecretStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointSecretStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        Dictionary<string, string> secrets = new(loaded.Secrets, StringComparer.OrdinalIgnoreCase)
        {
            [normalizedReference] = secret
        };
        await SaveAsync(secrets, cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
        EndpointSecretStoreLoadResult loaded = await LoadAsync(cancellationToken);
        if (loaded.Status is EndpointSecretStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message);
        }

        Dictionary<string, string> secrets = new(loaded.Secrets, StringComparer.OrdinalIgnoreCase);
        bool removed = secrets.Remove(normalizedReference);
        if (removed)
        {
            await SaveAsync(secrets, cancellationToken);
        }

        return removed;
    }

    private async Task SaveAsync(
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> protectedSecrets = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string reference, string secret) in secrets)
        {
            string normalizedReference = EndpointReferenceValidator.Normalize(reference, nameof(reference));
            ValidateSecret(secret);
            protectedSecrets[normalizedReference] = WindowsDataProtection.ProtectString(secret);
        }

        await AtomicFile.WriteJsonAsync(
            _paths.EndpointSecretsFile,
            new PersistedSecrets(CurrentSchemaVersion, protectedSecrets),
            _options,
            cancellationToken);
    }

    private static void ValidateSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.EnumerateRunes().Count() > EndpointTransportPolicy.MaxSecretCharacters)
        {
            throw new ArgumentException("Endpoint secret is too long.", nameof(secret));
        }
    }

    private EndpointSecretStoreLoadResult QuarantineCorrupt(string message)
    {
        string quarantinePath =
            $"{_paths.EndpointSecretsFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.EndpointSecretsFile, quarantinePath);
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.Recovered,
                true,
                message);
        }
        catch (UnauthorizedAccessException)
        {
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.ReadFailed,
                false,
                "端点 secret 文件损坏但无法隔离；原文件未覆盖。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new EndpointSecretStoreLoadResult(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                EndpointSecretStoreLoadStatus.ReadFailed,
                false,
                "端点 secret 文件损坏且无法隔离；原文件未覆盖。请检查磁盘后重试。");
        }
    }

    private sealed record PersistedSecrets(int SchemaVersion, IReadOnlyDictionary<string, string>? Secrets);
}

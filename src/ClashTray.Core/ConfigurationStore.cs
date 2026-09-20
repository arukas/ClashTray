using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record ConfigurationImportResult(
    ConfigurationProfile Profile,
    bool ContentChanged,
    string Sha256);

internal sealed record ConfigurationProfileBackup(
    string ConfigurationPath,
    string MetadataPath,
    byte[]? ConfigurationBytes,
    byte[]? MetadataBytes);

public sealed class ConfigurationStore
{
    private const int MaxConfigurationBytes = 16 * 1024 * 1024;
    private const int MaxMetadataBytes = 256 * 1024;
    private const int MaxBackupBytes = 24 * 1024 * 1024;
    private const int BackupSchemaVersion = 1;
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];
    private static readonly HttpClient SharedSubscriptionClient = CreateSubscriptionClient();
    private readonly AppPaths _paths;
    private readonly HttpMessageHandler? _subscriptionHandler;
    private readonly IConfigurationCandidateValidator? _candidateValidator;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigurationStore(
        AppPaths paths,
        HttpMessageHandler? subscriptionHandler = null,
        IConfigurationCandidateValidator? candidateValidator = null)
    {
        _paths = paths;
        // An injected handler is owned by the caller; each download still has its own timeout/client.
        _subscriptionHandler = subscriptionHandler;
        _candidateValidator = candidateValidator;
        _paths.EnsureDirectories();
    }

    public async Task<IReadOnlyList<ConfigurationProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<ConfigurationProfile> profiles = new List<ConfigurationProfile>();
        foreach (string path in Directory.EnumerateFiles(_paths.ConfigurationsRoot, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > MaxMetadataBytes)
                {
                    continue;
                }

                byte[]? metadataBytes = await ReadExistingFileAsync(
                    path,
                    MaxMetadataBytes,
                    cancellationToken);
                if (metadataBytes is null)
                {
                    continue;
                }

                StoredConfigurationProfile? stored = JsonSerializer.Deserialize<StoredConfigurationProfile>(
                    metadataBytes,
                    _jsonOptions);
                ConfigurationProfile? profile = CreateProfile(stored, out bool requiresMigration);
                if (profile is not null
                    && IsValidProfileId(profile.Id)
                    && IsConfigurationPathAllowed(profile.Path))
                {
                    ConfigurationProfile normalized = profile with { Path = Path.GetFullPath(profile.Path) };
                    if (requiresMigration)
                    {
                        await SaveMetadataAsync(normalized, cancellationToken);
                    }

                    profiles.Add(normalized);
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (CryptographicException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return profiles.OrderByDescending(profile => profile.IsActive).ThenBy(profile => profile.Name).ToArray();
    }

    public async Task<ConfigurationProfile> ImportLocalAsync(string sourcePath, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .yaml and .yml configuration files are supported.");
        }

        await using FileStream source = File.OpenRead(sourcePath);
        byte[] bytes = await ReadBytesWithLimitAsync(source, cancellationToken);
        ValidateYaml(bytes);
        string id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        string safeName = SanitizeName(displayName ?? Path.GetFileNameWithoutExtension(sourcePath));
        string destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}{extension.ToLowerInvariant()}");
        ConfigurationProfile profile = new ConfigurationProfile(id, safeName, destination, null, DateTimeOffset.UtcNow, false);
        await ValidateCandidateBytesAsync(bytes, extension, cancellationToken);
        await CommitProfileAsync(destination, bytes, profile, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ImportSubscriptionAsync(Uri subscriptionUri, string? displayName = null, CancellationToken cancellationToken = default)
    {
        ConfigurationImportResult result = await ImportSubscriptionWithResultAsync(subscriptionUri, displayName, cancellationToken);
        return result.Profile;
    }

    public async Task<ConfigurationImportResult> ImportSubscriptionWithResultAsync(
        Uri subscriptionUri,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptionUri);
        if (!subscriptionUri.IsAbsoluteUri || subscriptionUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("Subscription URL must be an absolute HTTP or HTTPS URL.");
        }

        byte[] bytes;
        if (_subscriptionHandler is null)
        {
            bytes = await DownloadSubscriptionAsync(SharedSubscriptionClient, subscriptionUri, cancellationToken);
        }
        else
        {
            using HttpClient scopedClient = CreateSubscriptionClient(_subscriptionHandler);
            bytes = await DownloadSubscriptionAsync(scopedClient, subscriptionUri, cancellationToken);
        }

        ValidateYaml(bytes);
        byte[] contentHash = SHA256.HashData(bytes);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subscriptionUri.ToString()))).ToLowerInvariant()[..16];
        string safeName = SanitizeName(displayName ?? subscriptionUri.Host);
        string destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}.yaml");
        byte[]? previousHash = await ComputeFileHashAsync(destination, cancellationToken);
        bool contentChanged = previousHash is null
            || !CryptographicOperations.FixedTimeEquals(previousHash, contentHash);
        if (contentChanged)
        {
            await ValidateCandidateBytesAsync(bytes, ".yaml", cancellationToken);
        }

        ConfigurationProfile profile = new ConfigurationProfile(id, safeName, destination, subscriptionUri, DateTimeOffset.UtcNow, false);
        if (contentChanged)
        {
            await CommitProfileAsync(destination, bytes, profile, cancellationToken);
        }
        else
        {
            await SaveMetadataAsync(profile, cancellationToken);
        }
        return new ConfigurationImportResult(
            profile,
            contentChanged,
            Convert.ToHexString(contentHash).ToLowerInvariant());
    }

    public async Task<ConfigurationProfile> ReloadAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SubscriptionUri is not null)
        {
            throw new InvalidOperationException("订阅配置应使用刷新操作。");
        }

        string path = ValidateConfigurationPath(profile.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("配置文件不存在。", path);
        }

        await using FileStream source = File.OpenRead(path);
        byte[] bytes = await ReadBytesWithLimitAsync(source, cancellationToken);
        ValidateYaml(bytes);
        await ValidateCandidateBytesAsync(bytes, Path.GetExtension(path), cancellationToken);
        ConfigurationProfile refreshed = profile with { LastRefreshed = DateTimeOffset.UtcNow };
        await SaveMetadataAsync(refreshed, cancellationToken);
        return refreshed;
    }

    public async Task ValidateCandidateAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = ValidateConfigurationPath(profile.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("配置文件不存在。", path);
        }

        await using FileStream source = File.OpenRead(path);
        byte[] bytes = await ReadBytesWithLimitAsync(source, cancellationToken);
        ValidateYaml(bytes);
        await ValidateCandidateBytesAsync(bytes, Path.GetExtension(path), cancellationToken);
    }

    internal async Task<ConfigurationProfileBackup> CaptureBackupAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string configurationPath = ValidateConfigurationPath(profile.Path);
        string metadataPath = MetadataPath(profile.Id);
        return new ConfigurationProfileBackup(
            configurationPath,
            metadataPath,
            await ReadExistingFileAsync(configurationPath, MaxConfigurationBytes, cancellationToken),
            await ReadExistingFileAsync(metadataPath, MaxMetadataBytes, cancellationToken));
    }

    internal static async Task RestoreBackupAsync(
        ConfigurationProfileBackup backup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        cancellationToken.ThrowIfCancellationRequested();
        await RestoreFileAsync(backup.ConfigurationPath, backup.ConfigurationBytes);
        await RestoreFileAsync(backup.MetadataPath, backup.MetadataBytes);
    }

    internal async Task SavePersistentBackupAsync(
        Guid backupId,
        ConfigurationProfile profile,
        ConfigurationProfileBackup backup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(backup);
        if (backupId == Guid.Empty)
        {
            throw new ArgumentException("Configuration backup ID is required.", nameof(backupId));
        }

        string configurationPath = ValidateConfigurationPath(profile.Path);
        string metadataPath = MetadataPath(profile.Id);
        if (!string.Equals(configurationPath, backup.ConfigurationPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadataPath, backup.MetadataPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配置备份路径与配置档案不匹配。");
        }

        PersistedConfigurationProfileBackup persisted = new(
            BackupSchemaVersion,
            backupId,
            profile.Id,
            Path.GetExtension(configurationPath).ToLowerInvariant(),
            backup.ConfigurationBytes,
            backup.MetadataBytes);
        await AtomicFile.WriteJsonAsync(
            PersistentBackupPath(backupId),
            persisted,
            _jsonOptions,
            cancellationToken);
    }

    internal async Task<bool> RestorePersistentBackupAsync(
        Guid backupId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (backupId == Guid.Empty)
        {
            throw new ArgumentException("Configuration backup ID is required.", nameof(backupId));
        }

        string backupPath = PersistentBackupPath(backupId);
        byte[]? backupBytes = await ReadExistingFileAsync(
            backupPath,
            MaxBackupBytes,
            cancellationToken);
        if (backupBytes is null)
        {
            return false;
        }

        PersistedConfigurationProfileBackup? persisted;
        try
        {
            persisted = JsonSerializer.Deserialize<PersistedConfigurationProfileBackup>(
                backupBytes,
                _jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("配置切换备份格式无效。", exception);
        }

        if (persisted is null
            || persisted.SchemaVersion != BackupSchemaVersion
            || persisted.BackupId != backupId
            || !string.Equals(persisted.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)
            || !SupportedExtensions.Contains(persisted.Extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("配置切换备份内容无效。");
        }

        if (persisted.ConfigurationBytes?.Length > MaxConfigurationBytes
            || persisted.MetadataBytes?.Length > MaxMetadataBytes)
        {
            throw new InvalidDataException("配置切换备份超过允许的大小限制。");
        }

        string configurationPath = ValidateConfigurationPath(Path.Combine(
            _paths.ConfigurationsRoot,
            $"{profileId}{persisted.Extension.ToLowerInvariant()}"));
        string metadataPath = MetadataPath(profileId);
        await RestoreFileAsync(configurationPath, persisted.ConfigurationBytes);
        await RestoreFileAsync(metadataPath, persisted.MetadataBytes);
        return true;
    }

    internal Task ClearPersistentBackupAsync(Guid backupId)
    {
        if (backupId == Guid.Empty)
        {
            throw new ArgumentException("Configuration backup ID is required.", nameof(backupId));
        }

        string path = PersistentBackupPath(backupId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public async Task DeleteAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = ValidateConfigurationPath(profile.Path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string metadataPath = MetadataPath(profile.Id);
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }

        await Task.CompletedTask;
    }

    public static void ValidateYaml(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            throw new InvalidDataException("Configuration is empty.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Configuration must be valid UTF-8.", exception);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Configuration contains an invalid null character.");
        }

        if (!text.Contains("mixed-port:", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("port:", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("proxies:", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("proxy-groups:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The file does not look like a Mihomo configuration.");
        }
    }

    private async Task SaveMetadataAsync(ConfigurationProfile profile, CancellationToken cancellationToken)
    {
        string? protectedSubscriptionUri = profile.SubscriptionUri is null
            ? null
            : WindowsDataProtection.ProtectString(profile.SubscriptionUri.AbsoluteUri);
        PersistedConfigurationProfile persisted = new PersistedConfigurationProfile(
            profile.Id,
            profile.Name,
            profile.Path,
            protectedSubscriptionUri,
            profile.LastRefreshed,
            profile.IsActive);
        await AtomicFile.WriteJsonAsync(MetadataPath(profile.Id), persisted, _jsonOptions, cancellationToken);
    }

    private static ConfigurationProfile? CreateProfile(
        StoredConfigurationProfile? stored,
        out bool requiresMigration)
    {
        requiresMigration = false;
        if (stored is null
            || string.IsNullOrWhiteSpace(stored.Id)
            || string.IsNullOrWhiteSpace(stored.Name)
            || string.IsNullOrWhiteSpace(stored.Path))
        {
            return null;
        }

        Uri? subscriptionUri = null;
        if (!string.IsNullOrWhiteSpace(stored.SubscriptionUriProtected))
        {
            string decrypted;
            try
            {
                decrypted = WindowsDataProtection.UnprotectString(stored.SubscriptionUriProtected);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException("订阅地址无法解密。", exception);
            }

            if (!Uri.TryCreate(decrypted, UriKind.Absolute, out subscriptionUri))
            {
                throw new InvalidDataException("订阅地址无效。");
            }

            ValidateSubscriptionUri(subscriptionUri);
            requiresMigration = !string.IsNullOrWhiteSpace(stored.SubscriptionUri);
        }
        else if (!string.IsNullOrWhiteSpace(stored.SubscriptionUri))
        {
            if (!Uri.TryCreate(stored.SubscriptionUri, UriKind.Absolute, out subscriptionUri))
            {
                throw new InvalidDataException("订阅地址无效。");
            }

            ValidateSubscriptionUri(subscriptionUri);
            requiresMigration = true;
        }

        return new ConfigurationProfile(
            stored.Id,
            stored.Name,
            stored.Path,
            subscriptionUri,
            stored.LastRefreshed,
            stored.IsActive);
    }

    private static void ValidateSubscriptionUri(Uri subscriptionUri)
    {
        if (!subscriptionUri.IsAbsoluteUri || subscriptionUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("Subscription URL must be an absolute HTTP or HTTPS URL.");
        }
    }

    private async Task ValidateCandidateBytesAsync(
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken)
    {
        if (_candidateValidator is null)
        {
            return;
        }

        string candidatePath = Path.Combine(
            _paths.ConfigurationsRoot,
            $".candidate-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        try
        {
            await AtomicFile.WriteBytesAsync(candidatePath, bytes, cancellationToken);
            await _candidateValidator.ValidateAsync(candidatePath, cancellationToken);
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private async Task CommitProfileAsync(
        string destination,
        byte[] bytes,
        ConfigurationProfile profile,
        CancellationToken cancellationToken)
    {
        string metadataPath = MetadataPath(profile.Id);
        byte[]? previousContent = await ReadExistingFileAsync(
            destination,
            MaxConfigurationBytes,
            cancellationToken);
        byte[]? previousMetadata = await ReadExistingFileAsync(
            metadataPath,
            MaxMetadataBytes,
            cancellationToken);
        try
        {
            await AtomicFile.WriteBytesAsync(destination, bytes, cancellationToken);
            await SaveMetadataAsync(profile, cancellationToken);
        }
        catch (Exception commitException)
        {
            try
            {
                await RestoreFileAsync(destination, previousContent);
                await RestoreFileAsync(metadataPath, previousMetadata);
            }
            catch (Exception rollbackException)
            {
                throw new IOException(
                    "配置提交失败，且无法恢复上一个配置。",
                    new AggregateException(commitException, rollbackException));
            }

            throw;
        }
    }

    private static async Task<byte[]?> ReadExistingFileAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            return null;
        }

        if (fileInfo.Length > maxBytes)
        {
            throw new InvalidDataException("Existing configuration data exceeds the supported size limit.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static async Task RestoreFileAsync(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        await AtomicFile.WriteBytesAsync(path, content, CancellationToken.None);
    }

    private string MetadataPath(string id)
    {
        if (!IsValidProfileId(id))
        {
            throw new InvalidDataException("配置标识无效。");
        }

        return Path.Combine(_paths.ConfigurationsRoot, $"{id}.json");
    }

    private string PersistentBackupPath(Guid backupId) =>
        Path.Combine(_paths.ConfigurationSwitchBackupsRoot, $"{backupId:N}.json");

    private string ValidateConfigurationPath(string path)
    {
        if (!IsConfigurationPathAllowed(path))
        {
            throw new InvalidDataException("配置文件路径不在 ClashTray 配置目录中。");
        }

        return Path.GetFullPath(path);
    }

    private bool IsConfigurationPathAllowed(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ConfigurationsRoot))
            + Path.DirectorySeparatorChar;
        string? directory = Path.GetDirectoryName(fullPath);
        return directory is not null
            && directory.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ConfigurationsRoot)), StringComparison.OrdinalIgnoreCase)
            && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && SupportedExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidProfileId(string id) =>
        id.Length == 16 && id.All(Uri.IsHexDigit);

    private static HttpClient CreateSubscriptionClient(HttpMessageHandler? handler = null)
    {
        HttpClient client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BundledMihomo.UserAgent);
        return client;
    }

    private static async Task<byte[]> DownloadSubscriptionAsync(
        HttpClient httpClient,
        Uri subscriptionUri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            subscriptionUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadBytesWithLimitAsync(responseStream, cancellationToken);
    }

    private static async Task<byte[]> ReadBytesWithLimitAsync(Stream source, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (true)
        {
            int count = await source.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length > MaxConfigurationBytes - count)
            {
                throw new InvalidDataException("配置文件超过 16 MiB 大小限制。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
    }

    private static async Task<byte[]?> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length > MaxConfigurationBytes)
        {
            return null;
        }

        try
        {
            await using FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await SHA256.HashDataAsync(stream, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string SanitizeName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "未命名配置" : sanitized[..Math.Min(64, sanitized.Length)];
    }

    private sealed class StoredConfigurationProfile
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Path { get; set; }

        public string? SubscriptionUri { get; set; }

        public string? SubscriptionUriProtected { get; set; }

        public DateTimeOffset? LastRefreshed { get; set; }

        public bool IsActive { get; set; }
    }

    private sealed record PersistedConfigurationProfileBackup(
        int SchemaVersion,
        Guid BackupId,
        string ProfileId,
        string Extension,
        byte[]? ConfigurationBytes,
        byte[]? MetadataBytes);

    private sealed record PersistedConfigurationProfile(
        string Id,
        string Name,
        string Path,
        string? SubscriptionUriProtected,
        DateTimeOffset? LastRefreshed,
        bool IsActive);
}

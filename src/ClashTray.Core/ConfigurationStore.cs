using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record ConfigurationImportResult(
    ConfigurationProfile Profile,
    bool ContentChanged,
    string Sha256);

public sealed class ConfigurationStore
{
    private const int MaxConfigurationBytes = 16 * 1024 * 1024;
    private const int MaxMetadataBytes = 256 * 1024;
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];
    private readonly AppPaths _paths;
    private readonly HttpMessageHandler? _subscriptionHandler;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ConfigurationStore(AppPaths paths, HttpMessageHandler? subscriptionHandler = null)
    {
        _paths = paths;
        // An injected handler is owned by the caller; each download still has its own timeout/client.
        _subscriptionHandler = subscriptionHandler;
        _paths.EnsureDirectories();
    }

    public async Task<IReadOnlyList<ConfigurationProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<ConfigurationProfile>();
        foreach (var path in Directory.EnumerateFiles(_paths.ConfigurationsRoot, "*.json"))
        {
            try
            {
                if (new FileInfo(path).Length > MaxMetadataBytes)
                {
                    continue;
                }

                await using var stream = File.OpenRead(path);
                var profile = await JsonSerializer.DeserializeAsync<ConfigurationProfile>(stream, _jsonOptions, cancellationToken);
                if (profile is not null
                    && IsValidProfileId(profile.Id)
                    && IsConfigurationPathAllowed(profile.Path))
                {
                    profiles.Add(profile with { Path = Path.GetFullPath(profile.Path) });
                }
            }
            catch (JsonException)
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
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .yaml and .yml configuration files are supported.");
        }

        await using var source = File.OpenRead(sourcePath);
        var bytes = await ReadBytesWithLimitAsync(source, cancellationToken);
        ValidateYaml(bytes);
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        var safeName = SanitizeName(displayName ?? Path.GetFileNameWithoutExtension(sourcePath));
        var destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}{extension.ToLowerInvariant()}");
        await AtomicFile.WriteBytesAsync(destination, bytes, cancellationToken);
        var profile = new ConfigurationProfile(id, safeName, destination, null, DateTimeOffset.UtcNow, false);
        await SaveMetadataAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ImportSubscriptionAsync(Uri subscriptionUri, string? displayName = null, CancellationToken cancellationToken = default)
    {
        var result = await ImportSubscriptionWithResultAsync(subscriptionUri, displayName, cancellationToken);
        return result.Profile;
    }

    public async Task<ConfigurationImportResult> ImportSubscriptionWithResultAsync(
        Uri subscriptionUri,
        string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        if (!subscriptionUri.IsAbsoluteUri || subscriptionUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("Subscription URL must be an absolute HTTP or HTTPS URL.");
        }

        using var httpClient = _subscriptionHandler is null
            ? new HttpClient()
            : new HttpClient(_subscriptionHandler, disposeHandler: false);
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(BundledMihomo.UserAgent);
        using var response = await httpClient.GetAsync(subscriptionUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadBytesWithLimitAsync(responseStream, cancellationToken);
        ValidateYaml(bytes);
        var contentHash = SHA256.HashData(bytes);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subscriptionUri.ToString()))).ToLowerInvariant()[..16];
        var safeName = SanitizeName(displayName ?? subscriptionUri.Host);
        var destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}.yaml");
        var previousHash = await ComputeFileHashAsync(destination, cancellationToken);
        var contentChanged = previousHash is null
            || !CryptographicOperations.FixedTimeEquals(previousHash, contentHash);
        if (contentChanged)
        {
            await AtomicFile.WriteBytesAsync(destination, bytes, cancellationToken);
        }

        var profile = new ConfigurationProfile(id, safeName, destination, subscriptionUri, DateTimeOffset.UtcNow, false);
        await SaveMetadataAsync(profile, cancellationToken);
        return new ConfigurationImportResult(
            profile,
            contentChanged,
            Convert.ToHexString(contentHash).ToLowerInvariant());
    }

    public async Task<ConfigurationProfile> ReloadAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.SubscriptionUri is not null)
        {
            throw new InvalidOperationException("订阅配置应使用刷新操作。");
        }

        var path = ValidateConfigurationPath(profile.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("配置文件不存在。", path);
        }

        await using var source = File.OpenRead(path);
        var bytes = await ReadBytesWithLimitAsync(source, cancellationToken);
        ValidateYaml(bytes);
        var refreshed = profile with { LastRefreshed = DateTimeOffset.UtcNow };
        await SaveMetadataAsync(refreshed, cancellationToken);
        return refreshed;
    }

    public async Task DeleteAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        var path = ValidateConfigurationPath(profile.Path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var metadataPath = MetadataPath(profile.Id);
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

        var text = Encoding.UTF8.GetString(content);
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
        await AtomicFile.WriteJsonAsync(MetadataPath(profile.Id), profile, _jsonOptions, cancellationToken);
    }

    private string MetadataPath(string id)
    {
        if (!IsValidProfileId(id))
        {
            throw new InvalidDataException("配置标识无效。");
        }

        return Path.Combine(_paths.ConfigurationsRoot, $"{id}.json");
    }

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
        var fullPath = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ConfigurationsRoot))
            + Path.DirectorySeparatorChar;
        var directory = Path.GetDirectoryName(fullPath);
        return directory is not null
            && directory.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ConfigurationsRoot)), StringComparison.OrdinalIgnoreCase)
            && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && SupportedExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidProfileId(string id) =>
        id.Length == 16 && id.All(Uri.IsHexDigit);

    private static async Task<byte[]> ReadBytesWithLimitAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(chunk.AsMemory(), cancellationToken);
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
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length > MaxConfigurationBytes)
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
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
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "未命名配置" : sanitized[..Math.Min(64, sanitized.Length)];
    }
}

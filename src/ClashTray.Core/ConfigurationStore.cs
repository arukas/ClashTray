using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class ConfigurationStore
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml"];
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ConfigurationStore(AppPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
    }

    public async Task<IReadOnlyList<ConfigurationProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<ConfigurationProfile>();
        foreach (var path in Directory.EnumerateFiles(_paths.ConfigurationsRoot, "*.json"))
        {
            await using var stream = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<ConfigurationProfile>(stream, _jsonOptions, cancellationToken);
            if (profile is not null)
            {
                profiles.Add(profile);
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

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        ValidateYaml(bytes);
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        var safeName = SanitizeName(displayName ?? Path.GetFileNameWithoutExtension(sourcePath));
        var destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}{extension.ToLowerInvariant()}");
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
        var profile = new ConfigurationProfile(id, safeName, destination, null, DateTimeOffset.UtcNow, false);
        await SaveMetadataAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ImportSubscriptionAsync(Uri subscriptionUri, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (!subscriptionUri.IsAbsoluteUri || subscriptionUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidDataException("Subscription URL must be an absolute HTTP or HTTPS URL.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await httpClient.GetAsync(subscriptionUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        ValidateYaml(bytes);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subscriptionUri.ToString()))).ToLowerInvariant()[..16];
        var safeName = SanitizeName(displayName ?? subscriptionUri.Host);
        var destination = Path.Combine(_paths.ConfigurationsRoot, $"{id}.yaml");
        await File.WriteAllBytesAsync(destination, bytes, cancellationToken);
        var profile = new ConfigurationProfile(id, safeName, destination, subscriptionUri, DateTimeOffset.UtcNow, false);
        await SaveMetadataAsync(profile, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ReloadAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.SubscriptionUri is not null)
        {
            throw new InvalidOperationException("订阅配置应使用刷新操作。");
        }

        if (!File.Exists(profile.Path))
        {
            throw new FileNotFoundException("配置文件不存在。", profile.Path);
        }

        var bytes = await File.ReadAllBytesAsync(profile.Path, cancellationToken);
        ValidateYaml(bytes);
        var refreshed = profile with { LastRefreshed = DateTimeOffset.UtcNow };
        await SaveMetadataAsync(refreshed, cancellationToken);
        return refreshed;
    }

    public async Task DeleteAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        if (File.Exists(profile.Path))
        {
            File.Delete(profile.Path);
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
        await using var stream = File.Create(MetadataPath(profile.Id));
        await JsonSerializer.SerializeAsync(stream, profile, _jsonOptions, cancellationToken);
    }

    private string MetadataPath(string id) => Path.Combine(_paths.ConfigurationsRoot, $"{id}.json");

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "未命名配置" : sanitized[..Math.Min(64, sanitized.Length)];
    }
}

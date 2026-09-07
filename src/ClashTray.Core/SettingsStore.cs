using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
        _paths.EnsureDirectories();
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(_paths.SettingsFile);
        await JsonSerializer.SerializeAsync(stream, settings, _options, cancellationToken);
    }
}

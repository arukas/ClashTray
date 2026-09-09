using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class SettingsStore
{
    private const int MaxSettingsBytes = 256 * 1024;
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

        try
        {
            if (new FileInfo(_paths.SettingsFile).Length > MaxSettingsBytes)
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_paths.SettingsFile);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                ?? new AppSettings();
            SettingsValidator.Validate(settings);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (ArgumentException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsValidator.Validate(settings);
        await AtomicFile.WriteJsonAsync(_paths.SettingsFile, settings, _options, cancellationToken);
    }
}

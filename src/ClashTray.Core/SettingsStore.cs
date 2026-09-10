using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface ISettingsStore
{
    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsStore : ISettingsStore
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

            await using FileStream stream = File.OpenRead(_paths.SettingsFile);
            AppSettings settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
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
        ArgumentNullException.ThrowIfNull(settings);
        SettingsValidator.Validate(settings);
        await AtomicFile.WriteJsonAsync(_paths.SettingsFile, settings, _options, cancellationToken);
    }
}



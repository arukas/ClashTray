using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal interface ISettingsStore
{
    public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public enum SettingsLoadStatus
{
    FirstRun,
    Loaded,
    Recovered,
    ReadFailed,
    RecoveryFailed
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    string? Message);

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

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        (await LoadWithStatusAsync(cancellationToken)).Settings;

    public async Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.FirstRun, null);
        }

        try
        {
            if (new FileInfo(_paths.SettingsFile).Length > MaxSettingsBytes)
            {
                throw new InvalidDataException("设置文件超过支持的大小限制。");
            }

            await using FileStream stream = File.OpenRead(_paths.SettingsFile);
            AppSettings settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _options, cancellationToken)
                ?? new AppSettings();
            SettingsValidator.Validate(settings);
            return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded, null);
        }
        catch (JsonException)
        {
            return RecoverCorruptSettings();
        }
        catch (ArgumentException)
        {
            return RecoverCorruptSettings();
        }
        catch (InvalidDataException)
        {
            return RecoverCorruptSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                SettingsLoadStatus.ReadFailed,
                "设置文件无法读取，已保留原文件；当前使用默认设置。请检查权限后重试。");
        }
        catch (IOException)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                SettingsLoadStatus.ReadFailed,
                "设置文件无法读取，已保留原文件；当前使用默认设置。请检查磁盘或文件权限。");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        SettingsValidator.Validate(settings);
        await AtomicFile.WriteJsonAsync(_paths.SettingsFile, settings, _options, cancellationToken);
    }

    private SettingsLoadResult RecoverCorruptSettings()
    {
        string backupPath = $"{_paths.SettingsFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.SettingsFile, backupPath);
            return new SettingsLoadResult(
                new AppSettings(),
                SettingsLoadStatus.Recovered,
                $"设置文件已损坏，原文件已备份为 {Path.GetFileName(backupPath)}；当前使用默认设置，请重新保存。");
        }
        catch (UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                SettingsLoadStatus.RecoveryFailed,
                "设置文件损坏，但无法安全备份；当前使用默认设置，未覆盖原文件。");
        }
        catch (IOException)
        {
            return new SettingsLoadResult(
                new AppSettings(),
                SettingsLoadStatus.RecoveryFailed,
                "设置文件损坏，但无法安全备份；当前使用默认设置，未覆盖原文件。");
        }
    }
}



using System.Text.Json;
using System.Text.Json.Serialization;
using ClashTray.Contracts;

namespace ClashTray.Core;

public enum ConfigurationSwitchSource
{
    Manual,
    SubscriptionRefresh,
    NetworkRule,
    NetworkDefault,
    Recovery
}

public enum ConfigurationSwitchStage
{
    Prepared,
    CandidateValidated,
    NetworkStateSafeguarded,
    RuntimePromoted,
    CoreRestarted,
    Committed,
    RollingBack,
    RollbackFailed
}

public sealed record ConfigurationSwitchJournal(
    int SchemaVersion,
    Guid OperationId,
    ConfigurationSwitchSource Source,
    ConfigurationSwitchStage Stage,
    string? PreviousConfigurationId,
    string CandidateConfigurationId,
    bool PreviousCoreWasRunning,
    bool PreviousSystemProxyPreference,
    SystemProxyState PreviousSystemProxyState,
    bool PreviousTunPreference,
    TunState PreviousTunState,
    long PreviousControllerGeneration,
    DateTimeOffset StartedAtUtc)
{
    public const int CurrentSchemaVersion = 1;

    public static ConfigurationSwitchJournal Create(
        ConfigurationSwitchSource source,
        string? previousConfigurationId,
        string candidateConfigurationId,
        bool previousCoreWasRunning,
        bool previousSystemProxyPreference,
        SystemProxyState previousSystemProxyState,
        bool previousTunPreference,
        TunState previousTunState,
        long previousControllerGeneration) =>
        new(
            CurrentSchemaVersion,
            Guid.NewGuid(),
            source,
            ConfigurationSwitchStage.Prepared,
            previousConfigurationId,
            candidateConfigurationId,
            previousCoreWasRunning,
            previousSystemProxyPreference,
            previousSystemProxyState,
            previousTunPreference,
            previousTunState,
            previousControllerGeneration,
            DateTimeOffset.UtcNow);

    public ConfigurationSwitchJournal WithStage(ConfigurationSwitchStage stage) =>
        this with { Stage = stage };

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported configuration switch journal schema: {SchemaVersion}.");
        }

        if (OperationId == Guid.Empty)
        {
            throw new InvalidDataException("Configuration switch journal operation ID is missing.");
        }

        if (string.IsNullOrWhiteSpace(CandidateConfigurationId))
        {
            throw new InvalidDataException("Configuration switch journal candidate ID is missing.");
        }

        if (PreviousControllerGeneration < 0)
        {
            throw new InvalidDataException("Configuration switch journal generation is invalid.");
        }
    }
}

public sealed record ConfigurationSwitchJournalLoadResult(
    ConfigurationSwitchJournal? Journal,
    bool WasQuarantined,
    string? Message);

public sealed class ConfigurationSwitchJournalStore
{
    private const int MaxJournalBytes = 64 * 1024;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConfigurationSwitchJournalStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureDirectories();
    }

    public async Task<ConfigurationSwitchJournalLoadResult> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ConfigurationSwitchJournalFile))
        {
            return new ConfigurationSwitchJournalLoadResult(null, false, null);
        }

        try
        {
            if (new FileInfo(_paths.ConfigurationSwitchJournalFile).Length > MaxJournalBytes)
            {
                throw new InvalidDataException("Configuration switch journal exceeds the supported size.");
            }

            await using FileStream stream = File.OpenRead(_paths.ConfigurationSwitchJournalFile);
            ConfigurationSwitchJournal? journal =
                await JsonSerializer.DeserializeAsync<ConfigurationSwitchJournal>(
                    stream,
                    _options,
                    cancellationToken);
            if (journal is null)
            {
                throw new InvalidDataException("Configuration switch journal is empty.");
            }

            journal.Validate();
            return new ConfigurationSwitchJournalLoadResult(journal, false, null);
        }
        catch (JsonException)
        {
            return QuarantineCorruptJournal("配置切换记录格式无效，原文件已隔离。");
        }
        catch (InvalidDataException)
        {
            return QuarantineCorruptJournal("配置切换记录无效，原文件已隔离。");
        }
        catch (IOException)
        {
            return new ConfigurationSwitchJournalLoadResult(
                null,
                false,
                "配置切换记录无法读取，已跳过恢复；请检查用户目录权限。");
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigurationSwitchJournalLoadResult(
                null,
                false,
                "配置切换记录无权读取，已跳过恢复；请检查用户目录权限。");
        }
    }

    public async Task SaveAsync(
        ConfigurationSwitchJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        await AtomicFile.WriteJsonAsync(
            _paths.ConfigurationSwitchJournalFile,
            journal,
            _options,
            cancellationToken);
    }

    public Task ClearAsync()
    {
        if (File.Exists(_paths.ConfigurationSwitchJournalFile))
        {
            File.Delete(_paths.ConfigurationSwitchJournalFile);
        }

        return Task.CompletedTask;
    }

    private ConfigurationSwitchJournalLoadResult QuarantineCorruptJournal(string message)
    {
        string backupPath =
            $"{_paths.ConfigurationSwitchJournalFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.ConfigurationSwitchJournalFile, backupPath);
            return new ConfigurationSwitchJournalLoadResult(null, true, message);
        }
        catch (IOException)
        {
            return new ConfigurationSwitchJournalLoadResult(
                null,
                false,
                "配置切换记录损坏且无法隔离，已跳过恢复；原文件未覆盖。");
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigurationSwitchJournalLoadResult(
                null,
                false,
                "配置切换记录损坏但无权隔离，已跳过恢复；原文件未覆盖。");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClashTray.Core;

internal enum SystemProxyTransitionStage
{
    Prepared,
    Applying,
    Restoring,
    Committed
}

internal enum SystemProxyRegistryField
{
    None,
    ProxyEnable,
    ProxyServer,
    ProxyOverride
}

internal sealed record SystemProxyTransitionJournal(
    Guid OperationId,
    SystemProxyTransitionStage Stage,
    SystemProxyRegistryField LastWriteStarted,
    ProxyRegistryState PreviousState,
    ProxyRegistryState IntendedState,
    ProxyOwnershipState? PreviousOwnership,
    bool CreatedBackup)
{
    public static SystemProxyTransitionJournal Create(
        ProxyRegistryState previousState,
        ProxyRegistryState intendedState,
        ProxyOwnershipState? previousOwnership,
        bool createdBackup) =>
        new(
            Guid.NewGuid(),
            SystemProxyTransitionStage.Prepared,
            SystemProxyRegistryField.None,
            previousState,
            intendedState,
            previousOwnership,
            createdBackup);

    public void Validate()
    {
        if (OperationId == Guid.Empty
            || !Enum.IsDefined(Stage)
            || !Enum.IsDefined(LastWriteStarted)
            || string.IsNullOrWhiteSpace(IntendedState.ProxyServer)
            || IntendedState.ProxyEnable != 1)
        {
            throw new InvalidDataException("系统代理恢复事务记录无效。");
        }

        if (CreatedBackup && PreviousOwnership is not null)
        {
            throw new InvalidDataException("新建的系统代理备份不能已有 ClashTray 所有权记录。");
        }
    }
}

internal interface ISystemProxyRegistry
{
    public ProxyRegistryState ReadCurrentState();

    public void SetProxyEnable(int value);

    public void SetProxyServer(string? value);

    public void SetProxyOverride(string? value);

    public void WriteState(ProxyRegistryState state);
}

internal sealed class WindowsSystemProxyRegistry : ISystemProxyRegistry
{
    internal const string InternetSettingsPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";

    private readonly Func<RegistryKey?> _openKey;

    public WindowsSystemProxyRegistry(Func<RegistryKey?> openKey)
    {
        _openKey = openKey ?? throw new ArgumentNullException(nameof(openKey));
    }

    public static WindowsSystemProxyRegistry ForCurrentUser() =>
        new(() => Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true));

    public static WindowsSystemProxyRegistry ForUser(string sid) =>
        new(() => Registry.Users.OpenSubKey($"{sid}\\{InternetSettingsPath}", writable: true));

    public ProxyRegistryState ReadCurrentState()
    {
        using RegistryKey key = OpenKey();
        return new ProxyRegistryState(
            GetDword(key, "ProxyEnable"),
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string,
            key.GetValue("AutoConfigURL") as string,
            GetDword(key, "AutoDetect"));
    }

    public void SetProxyEnable(int value)
    {
        using RegistryKey key = OpenKey();
        key.SetValue("ProxyEnable", value, RegistryValueKind.DWord);
    }

    public void SetProxyServer(string? value)
    {
        using RegistryKey key = OpenKey();
        SetOrDelete(key, "ProxyServer", value);
    }

    public void SetProxyOverride(string? value)
    {
        using RegistryKey key = OpenKey();
        SetOrDelete(key, "ProxyOverride", value);
    }

    public void WriteState(ProxyRegistryState state)
    {
        using RegistryKey key = OpenKey();
        key.SetValue("ProxyEnable", state.ProxyEnable, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", state.ProxyServer);
        SetOrDelete(key, "ProxyOverride", state.ProxyOverride);
        SetOrDelete(key, "AutoConfigURL", state.AutoConfigUrl);
        key.SetValue("AutoDetect", state.AutoDetect, RegistryValueKind.DWord);
    }

    private RegistryKey OpenKey() =>
        _openKey() ?? throw new InvalidOperationException("Windows Internet Settings registry key is unavailable.");

    private static int GetDword(RegistryKey key, string name) =>
        key.GetValue(name) is int value ? value : 0;

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }
}

internal sealed class SystemProxyTransactionJournalStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SystemProxyTransactionJournalStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<SystemProxyTransitionJournal?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ProxyTransactionFile))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(_paths.ProxyTransactionFile, cancellationToken);
        SystemProxyTransitionJournal journal =
            JsonSerializer.Deserialize<SystemProxyTransitionJournal>(json, _options)
            ?? throw new InvalidDataException("系统代理恢复事务记录为空。");
        journal.Validate();
        return journal;
    }

    public SystemProxyTransitionJournal? Load()
    {
        if (!File.Exists(_paths.ProxyTransactionFile))
        {
            return null;
        }

        SystemProxyTransitionJournal journal =
            JsonSerializer.Deserialize<SystemProxyTransitionJournal>(
                File.ReadAllText(_paths.ProxyTransactionFile),
                _options)
            ?? throw new InvalidDataException("系统代理恢复事务记录为空。");
        journal.Validate();
        return journal;
    }

    public Task SaveAsync(
        SystemProxyTransitionJournal journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        return AtomicFile.WriteJsonAsync(
            _paths.ProxyTransactionFile,
            journal,
            _options,
            cancellationToken);
    }

    public Task ClearAsync()
    {
        if (File.Exists(_paths.ProxyTransactionFile))
        {
            File.Delete(_paths.ProxyTransactionFile);
        }

        return Task.CompletedTask;
    }
}

internal enum SystemProxyTransactionRecoveryResult
{
    None,
    Restored,
    Committed,
    Conflict
}

internal static class SystemProxyTransactionRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<SystemProxyTransactionRecoveryResult> RecoverAsync(
        AppPaths paths,
        ISystemProxyRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registry);
        SystemProxyTransactionJournalStore store = new(paths);
        SystemProxyTransitionJournal? journal = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (journal is null)
        {
            return SystemProxyTransactionRecoveryResult.None;
        }

        if (journal.Stage == SystemProxyTransitionStage.Committed)
        {
            await store.ClearAsync().ConfigureAwait(false);
            return SystemProxyTransactionRecoveryResult.Committed;
        }

        ProxyRegistryState current = registry.ReadCurrentState();
        if (!CanRecover(current, journal))
        {
            return SystemProxyTransactionRecoveryResult.Conflict;
        }

        await store.SaveAsync(
                journal with { Stage = SystemProxyTransitionStage.Restoring },
                CancellationToken.None)
            .ConfigureAwait(false);
        registry.WriteState(journal.PreviousState);
        if (registry.ReadCurrentState() != journal.PreviousState)
        {
            throw new IOException("Windows proxy registry did not match the restored transaction state.");
        }

        if (journal.PreviousOwnership is not null)
        {
            await AtomicFile.WriteJsonAsync(
                    paths.ProxyOwnershipFile,
                    journal.PreviousOwnership,
                    JsonOptions,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        else if (journal.CreatedBackup)
        {
            DeleteIfPresent(paths.ProxyOwnershipFile);
            DeleteIfPresent(paths.ProxyBackupFile);
        }

        await store.ClearAsync().ConfigureAwait(false);
        return SystemProxyTransactionRecoveryResult.Restored;
    }

    internal static bool CanRecover(
        ProxyRegistryState current,
        SystemProxyTransitionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(journal);

        int lastAttemptedField = journal.LastWriteStarted switch
        {
            SystemProxyRegistryField.None => 0,
            SystemProxyRegistryField.ProxyEnable => 1,
            SystemProxyRegistryField.ProxyServer => 2,
            SystemProxyRegistryField.ProxyOverride => 3,
            _ => -1
        };
        if (lastAttemptedField < 0)
        {
            return false;
        }

        return Matches(current.ProxyEnable, journal.PreviousState.ProxyEnable, journal.IntendedState.ProxyEnable, lastAttemptedField >= 1)
            && Matches(current.ProxyServer, journal.PreviousState.ProxyServer, journal.IntendedState.ProxyServer, lastAttemptedField >= 2, StringComparison.OrdinalIgnoreCase)
            && Matches(current.ProxyOverride, journal.PreviousState.ProxyOverride, journal.IntendedState.ProxyOverride, lastAttemptedField >= 3, StringComparison.Ordinal)
            && current.AutoDetect == journal.PreviousState.AutoDetect
            && string.Equals(current.AutoConfigUrl, journal.PreviousState.AutoConfigUrl, StringComparison.Ordinal);
    }

    private static bool Matches(
        int current,
        int previous,
        int intended,
        bool writeMayHaveRun) =>
        current == previous || writeMayHaveRun && current == intended;

    private static bool Matches(
        string? current,
        string? previous,
        string? intended,
        bool writeMayHaveRun,
        StringComparison comparison) =>
        string.Equals(current, previous, comparison)
        || writeMayHaveRun && string.Equals(current, intended, comparison);

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
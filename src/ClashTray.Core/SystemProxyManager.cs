using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class SystemProxyManager : ISystemProxyController
{
    private readonly AppPaths _paths;
    private readonly ISystemProxyRegistry _registry;
    private readonly SystemProxyTransactionJournalStore _transactionStore;
    private readonly Action _notifySystemProxyChanged;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SystemProxyManager(AppPaths paths)
        : this(paths, WindowsSystemProxyRegistry.ForCurrentUser(), InternetSettingsNotifier.Notify)
    {
    }

    internal SystemProxyManager(AppPaths paths, ISystemProxyRegistry registry, Action? notifySystemProxyChanged = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _transactionStore = new SystemProxyTransactionJournalStore(paths);
        _notifySystemProxyChanged = notifySystemProxyChanged ?? InternetSettingsNotifier.Notify;
        _paths.EnsureDirectories();
    }

    public SystemProxyState State { get; private set; } = SystemProxyState.Off;

    public static bool IsEnabled => ReadCurrentState().ProxyEnable == 1;

    public SystemProxyState DetectState()
    {
        try
        {
            SystemProxyTransitionJournal? transaction = _transactionStore.Load();
            if (transaction is not null && transaction.Stage != SystemProxyTransitionStage.Committed)
            {
                State = SystemProxyState.RestoreRequired;
                return State;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            State = SystemProxyState.RestoreRequired;
            return State;
        }

        ProxyRegistryState current = _registry.ReadCurrentState();
        ProxyOwnershipState? ownership = ReadOwnership();
        if (ownership is not null && SystemProxyOwnershipPolicy.IsOwnedByClashTray(current, ownership))
        {
            State = SystemProxyState.On;
        }
        else if (File.Exists(_paths.ProxyBackupFile))
        {
            State = SystemProxyState.RestoreRequired;
        }
        else
        {
            State = SystemProxyState.Off;
        }

        return State;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Registry writes are journaled before mutation; a failed write is reconciled only when current values still match this transaction.")]
    public async Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
    {
        State = SystemProxyState.Enabling;
        ProxyOwnershipState? previousOwnership = null;
        bool backupCreated = false;
        bool transactionStarted = false;
        try
        {
            SystemProxyTransitionJournal? pendingTransaction =
                await _transactionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (pendingTransaction is not null)
            {
                if (pendingTransaction.Stage == SystemProxyTransitionStage.Committed)
                {
                    await _transactionStore.ClearAsync().ConfigureAwait(false);
                }
                else
                {
                    State = SystemProxyState.RestoreRequired;
                    throw new InvalidOperationException("系统代理有待恢复的中断操作，请先禁用并恢复原设置。");
                }
            }

            ProxyRegistryState previousState = _registry.ReadCurrentState();
            previousOwnership = ReadOwnership();
            if (previousOwnership is not null
                && !SystemProxyOwnershipPolicy.IsOwnedByClashTray(previousState, previousOwnership))
            {
                State = SystemProxyState.RestoreRequired;
                throw new InvalidOperationException("系统代理已被其他程序修改，请先确认并恢复原设置。");
            }

            if (previousOwnership is null && File.Exists(_paths.ProxyBackupFile))
            {
                State = SystemProxyState.RestoreRequired;
                throw new InvalidOperationException("系统代理存在待恢复的原始设置，请先完成恢复后再启用。");
            }

            if (!File.Exists(_paths.ProxyBackupFile))
            {
                await AtomicFile.WriteJsonAsync(
                        _paths.ProxyBackupFile,
                        previousState,
                        _jsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                backupCreated = true;
            }

            ProxyRegistryState intendedState = previousState with
            {
                ProxyEnable = 1,
                ProxyServer = $"127.0.0.1:{port}",
                ProxyOverride = bypassList
            };
            SystemProxyTransitionJournal journal = SystemProxyTransitionJournal.Create(
                previousState,
                intendedState,
                previousOwnership,
                backupCreated);

            transactionStarted = true;
            await _transactionStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteJsonAsync(
                    _paths.ProxyOwnershipFile,
                    SystemProxyOwnershipPolicy.Create(intendedState),
                    _jsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);

            journal = journal with
            {
                Stage = SystemProxyTransitionStage.Applying,
                LastWriteStarted = SystemProxyRegistryField.ProxyEnable
            };
            await _transactionStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            _registry.SetProxyEnable(intendedState.ProxyEnable);

            journal = journal with { LastWriteStarted = SystemProxyRegistryField.ProxyServer };
            await _transactionStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            _registry.SetProxyServer(intendedState.ProxyServer);

            journal = journal with { LastWriteStarted = SystemProxyRegistryField.ProxyOverride };
            await _transactionStore.SaveAsync(journal, cancellationToken).ConfigureAwait(false);
            _registry.SetProxyOverride(intendedState.ProxyOverride);

            journal = journal with { Stage = SystemProxyTransitionStage.Committed };
            await _transactionStore.SaveAsync(journal, CancellationToken.None).ConfigureAwait(false);
            await _transactionStore.ClearAsync().ConfigureAwait(false);
            _notifySystemProxyChanged();
            State = SystemProxyState.On;
        }
        catch
        {
            if (transactionStarted)
            {
                try
                {
                    SystemProxyTransactionRecoveryResult recovery =
                        await SystemProxyTransactionRecovery.RecoverAsync(
                                _paths,
                                _registry,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    if (recovery == SystemProxyTransactionRecoveryResult.Conflict)
                    {
                        State = SystemProxyState.RestoreRequired;
                    }
                    else if (recovery == SystemProxyTransactionRecoveryResult.Restored)
                    {
                        _notifySystemProxyChanged();
                    }
                    else if (recovery == SystemProxyTransactionRecoveryResult.None && backupCreated)
                    {
                        DeleteIfPresent(_paths.ProxyOwnershipFile);
                        DeleteIfPresent(_paths.ProxyBackupFile);
                    }
                }
                catch
                {
                    State = SystemProxyState.RestoreRequired;
                }
            }
            else if (backupCreated)
            {
                DeleteIfPresent(_paths.ProxyBackupFile);
            }

            if (State != SystemProxyState.RestoreRequired)
            {
                State = previousOwnership is null
                    ? SystemProxyState.Failed
                    : DetectState();
            }

            throw;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed registry restoration leaves the saved transaction and exposes RestoreRequired for a later retry.")]
    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        State = SystemProxyState.Disabling;
        try
        {
            SystemProxyTransitionJournal? pendingTransaction =
                await _transactionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (pendingTransaction is not null
                && pendingTransaction.Stage != SystemProxyTransitionStage.Committed)
            {
                SystemProxyTransactionRecoveryResult recovery =
                    await SystemProxyTransactionRecovery.RecoverAsync(
                            _paths,
                            _registry,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                if (recovery == SystemProxyTransactionRecoveryResult.Conflict)
                {
                    State = SystemProxyState.RestoreRequired;
                    return;
                }

                if (recovery == SystemProxyTransactionRecoveryResult.Restored)
                {
                    _notifySystemProxyChanged();
                }
            }
            else if (pendingTransaction is not null)
            {
                await _transactionStore.ClearAsync().ConfigureAwait(false);
            }

            ProxyRegistryState current = _registry.ReadCurrentState();
            ProxyOwnershipState? ownership = ReadOwnership();
            if (File.Exists(_paths.ProxyBackupFile))
            {
                ProxyRegistryState? backup = JsonSerializer.Deserialize<ProxyRegistryState>(
                    await File.ReadAllTextAsync(_paths.ProxyBackupFile, cancellationToken).ConfigureAwait(false),
                    _jsonOptions);
                if (backup is not null
                    && ownership is not null
                    && SystemProxyOwnershipPolicy.CanRestore(current, backup, ownership))
                {
                    _registry.WriteState(backup);
                    DeleteIfPresent(_paths.ProxyBackupFile);
                    DeleteIfPresent(_paths.ProxyOwnershipFile);
                }
                else if (backup is not null)
                {
                    State = SystemProxyState.RestoreRequired;
                    return;
                }
            }
            else if (ownership is not null && SystemProxyOwnershipPolicy.IsOwnedByClashTray(current, ownership))
            {
                current = current with { ProxyEnable = 0, ProxyServer = null, ProxyOverride = null };
                _registry.WriteState(current);
                DeleteIfPresent(_paths.ProxyOwnershipFile);
            }

            _notifySystemProxyChanged();
            State = SystemProxyState.Off;
        }
        catch
        {
            State = SystemProxyState.Failed;
            throw;
        }
    }

    public static ProxyRegistryState ReadCurrentState() =>
        WindowsSystemProxyRegistry.ForCurrentUser().ReadCurrentState();

    private ProxyOwnershipState? ReadOwnership()
    {
        if (!File.Exists(_paths.ProxyOwnershipFile))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProxyOwnershipState>(
                File.ReadAllText(_paths.ProxyOwnershipFile),
                _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed record ProxyRegistryState(
    int ProxyEnable,
    string? ProxyServer,
    string? ProxyOverride,
    string? AutoConfigUrl,
    int AutoDetect);

internal static class InternetSettingsNotifier
{
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
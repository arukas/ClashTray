using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

// One persistence owner for preferences, recovery merges and off-only intent.
// Callers retain operation admission and confirmed OS/core side effects.
internal sealed class SettingsCommitCoordinator(ISettingsStore store, SettingsRecoveryJournal journal)
{
    private readonly ISettingsStore _store = store;
    private readonly SettingsRecoveryJournal _journal = journal;
    private NetworkDisableIntent _disableIntent = new();
    private Exception? _failure;
    public AppSettings Current { get; set; } = new();
    public Exception? Failure => Volatile.Read(ref _failure);
    public AppSettings ApplyDisableIntent(AppSettings settings) => _disableIntent.Apply(settings);
    public bool IsTunDisableRequested => _disableIntent.TunOff;
    public bool IsProxyDisableRequested => _disableIntent.SystemProxyOff;
    public Action<Exception>? FailureObserver { get; set; }
    public Task<SettingsLoadResult> LoadWithStatusAsync(CancellationToken cancellationToken) => _store.LoadWithStatusAsync(cancellationToken);
    public void CompletePrepared() => _journal.Complete();

    public async Task SavePreparedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _journal.PrepareAsync(Current, settings, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Current = settings;
    }

    public async Task SaveSelectionAsync(string? id, CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        AppSettings settings = _disableIntent.Apply(Current with { ActiveConfigurationId = id });
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Current = settings;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Failure rollback must run to completion regardless of which restore step fails.")]
    public async Task<SettingsRestorationResult> RestoreAsync(AppSettings settings)
    {
        if (!_journal.Exists && Current == settings)
        {
            return new SettingsRestorationResult(true);
        }
        Current = _disableIntent.Apply(settings);
        try
        {
            using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(3));
            SettingsRecoveryRecord? record = await _journal.ReadAsync(recoveryTimeout.Token);
            if (record is not null)
            {
                SettingsLoadResult loaded = await _store.LoadWithStatusAsync(recoveryTimeout.Token);
                EnsureSettingsStorageReadable(loaded);
                Current = _disableIntent.Apply(record.RestoreOnto(loaded.Settings));
            }
            await _store.SaveAsync(Current, recoveryTimeout.Token);
            _journal.Complete();
            Volatile.Write(ref _failure, null);
            return new SettingsRestorationResult(true);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            return new SettingsRestorationResult(false, exception);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Persisted recovery failures remain visible and pending; no network success is inferred.")]
    public async Task RecoverAsync(CancellationToken cancellationToken, bool initializing = false)
    {
        try
        {
            _disableIntent = _disableIntent.Merge(await _journal.ReadNetworkDisableIntentAsync(cancellationToken));
            Current = _disableIntent.Apply(Current);
            if (!_journal.Exists && _disableIntent.IsEmpty)
            {
                if (_failure is not null)
                {
                    // A transient failure can leave no journal to replay. Only
                    // clear the old failure after confirming storage is readable.
                    EnsureSettingsStorageReadable(await _store.LoadWithStatusAsync(cancellationToken));
                    Volatile.Write(ref _failure, null);
                }
                return;
            }
            SettingsRecoveryRecord? record = await _journal.ReadAsync(cancellationToken);
            SettingsLoadResult loaded = await _store.LoadWithStatusAsync(cancellationToken);
            EnsureSettingsStorageReadable(loaded);
            AppSettings restored = _disableIntent.Apply(record?.RestoreOnto(loaded.Settings) ?? loaded.Settings);
            // Keep the off-only marker until a successful explicit enable. A
            // late rollback may still contain the old enabled preference.
            await _journal.WriteNetworkDisableIntentAsync(_disableIntent, cancellationToken);
            Current = restored;
            await _store.SaveAsync(restored, cancellationToken);
            _journal.Complete();
            Volatile.Write(ref _failure, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // An unreadable recovery record cannot be an admission for a
            // background enable, including during a subsequent App restart.
            RecordFailure(exception);
            if (!initializing) { throw new InvalidOperationException("设置恢复未完成，无法开始新的设置操作。", exception); }
        }
    }

    public void RecordFailure(Exception exception)
    {
        Volatile.Write(ref _failure, exception);
        FailureObserver?.Invoke(exception);
    }

    private static void EnsureSettingsStorageReadable(SettingsLoadResult loaded)
    {
        if (loaded.Status is not (SettingsLoadStatus.Loaded or SettingsLoadStatus.FirstRun))
        {
            throw new InvalidOperationException("持久化设置无法可靠读取；保留恢复记录，请检查设置存储后重试。");
        }
    }

    public void ThrowIfIncomplete(Exception original)
    {
        if (_failure is Exception failure)
        {
            throw new InvalidOperationException("操作失败，且设置恢复未完成。", new AggregateException(original, failure));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Storage failures are recorded separately and must not veto owned network cleanup.")]
    public async Task RememberDisableAsync(bool tun)
    {
        _disableIntent = _disableIntent with
        {
            SystemProxyOff = _disableIntent.SystemProxyOff || !tun,
            TunOff = _disableIntent.TunOff || tun
        };
        Current = _disableIntent.Apply(Current);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            _disableIntent = _disableIntent.Merge(await _journal.ReadNetworkDisableIntentAsync(timeout.Token));
            Current = _disableIntent.Apply(Current);
            await _journal.WriteNetworkDisableIntentAsync(_disableIntent, timeout.Token);
        }
        catch (Exception exception) { RecordFailure(exception); }
    }

    public async Task ClearDisableIntentAsync(bool tun, CancellationToken cancellationToken)
    {
        NetworkDisableIntent remaining = _disableIntent with
        {
            SystemProxyOff = tun && _disableIntent.SystemProxyOff,
            TunOff = !tun && _disableIntent.TunOff
        };
        await _journal.WriteNetworkDisableIntentAsync(remaining, cancellationToken);
        _disableIntent = remaining;
    }

}

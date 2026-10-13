using ClashTray.Contracts;
using System.Diagnostics.CodeAnalysis;

namespace ClashTray.Core;

public sealed class SubscriptionScheduler : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ConfigurationProfile>>> _listProfiles;
    private readonly Func<ConfigurationProfile, CancellationToken, Task> _refreshProfile;
    private readonly Func<AppSettings> _settings;
    private readonly Action<ConfigurationProfile, Exception>? _onRefreshFailed;
    private readonly Action<Exception>? _onCycleFailed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public SubscriptionScheduler(
        Func<CancellationToken, Task<IReadOnlyList<ConfigurationProfile>>> listProfiles,
        Func<ConfigurationProfile, CancellationToken, Task> refreshProfile,
        Func<AppSettings> settings,
        Action<ConfigurationProfile, Exception>? onRefreshFailed = null,
        Action<Exception>? onCycleFailed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _listProfiles = listProfiles;
        _refreshProfile = refreshProfile;
        _settings = settings;
        _onRefreshFailed = onRefreshFailed;
        _onCycleFailed = onCycleFailed;
        _delay = delay ?? Task.Delay;
    }

    public void Start() => _task ??= Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_task is not null)
        {
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The scheduler loop must keep running; per-profile and per-cycle failures are reported through the failure callbacks.")]
    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int hours = Math.Clamp(_settings().SubscriptionRefreshHours, 1, 168);
                await _delay(TimeSpan.FromHours(hours), _cts.Token);
                IReadOnlyList<ConfigurationProfile> profiles = await _listProfiles(_cts.Token);
                foreach (ConfigurationProfile? profile in profiles.Where(profile => profile.SubscriptionUri is not null))
                {
                    try
                    {
                        await _refreshProfile(profile, _cts.Token);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        NotifyRefreshFailed(profile, exception);
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                NotifyCycleFailed(exception);
                try
                {
                    await _delay(TimeSpan.FromMinutes(5), _cts.Token);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A throwing notification callback must not terminate the scheduler loop.")]
    private void NotifyRefreshFailed(ConfigurationProfile profile, Exception exception)
    {
        try
        {
            _onRefreshFailed?.Invoke(profile, exception);
        }
        catch
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A throwing notification callback must not terminate the scheduler loop.")]
    private void NotifyCycleFailed(Exception exception)
    {
        try
        {
            _onCycleFailed?.Invoke(exception);
        }
        catch
        {
        }
    }
}

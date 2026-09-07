using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class SubscriptionScheduler : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ConfigurationProfile>>> _listProfiles;
    private readonly Func<ConfigurationProfile, CancellationToken, Task> _refreshProfile;
    private readonly Func<AppSettings> _settings;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public SubscriptionScheduler(
        Func<CancellationToken, Task<IReadOnlyList<ConfigurationProfile>>> listProfiles,
        Func<ConfigurationProfile, CancellationToken, Task> refreshProfile,
        Func<AppSettings> settings)
    {
        _listProfiles = listProfiles;
        _refreshProfile = refreshProfile;
        _settings = settings;
    }

    public void Start() => _task ??= Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
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

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            var hours = Math.Clamp(_settings().SubscriptionRefreshHours, 1, 168);
            await Task.Delay(TimeSpan.FromHours(hours), _cts.Token);
            var profiles = await _listProfiles(_cts.Token);
            foreach (var profile in profiles.Where(profile => profile.SubscriptionUri is not null))
            {
                try
                {
                    await _refreshProfile(profile, _cts.Token);
                }
                catch (HttpRequestException)
                {
                }
                catch (InvalidDataException)
                {
                }
                catch (Exception)
                {
                }
            }
        }
    }
}

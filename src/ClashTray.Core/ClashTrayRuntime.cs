using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class ClashTrayRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _subscriptionOperationLock = new(1, 1);
    private readonly SemaphoreSlim _dataRefreshLock = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly BoundedLogBuffer _logBuffer = new(500);
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configurationStore;
    private readonly ISettingsStore _settingsStore;
    private readonly CoreDiscovery _coreDiscovery;
    private readonly SystemProxyManager _systemProxy;
    private readonly IServicePipeClient _servicePipeClient;
    private readonly CoreUpdater _coreUpdater;
    private readonly SubscriptionScheduler _subscriptionScheduler;
    private readonly MihomoProcessManager _processManager = new();
    private readonly IStartupRegistration _startupRegistration;
    private readonly object _logStreamGate = new();
    private MihomoApiClient? _api;
    private Task? _pollingTask;
    private Task? _dataRefreshTask;
    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private AppSettings _settings = new();
    private RuntimeSnapshot _snapshot = CreateInitialSnapshot();
    private bool _usingServiceCore;

    private const int MaxLogMessageBytes = 1024 * 1024;

    private sealed record ProxyDataResult(
        bool Succeeded,
        IReadOnlyList<ProxyGroup> Groups,
        IReadOnlyList<ProxyNode> Nodes);

    private readonly record struct TrafficDataResult(bool Succeeded, TrafficSnapshot? Value);

    private readonly record struct MemoryDataResult(bool Succeeded, long Value);

    private readonly record struct ConnectionDataResult(
        bool Succeeded,
        IReadOnlyList<ConnectionInfo> Value);

    public ClashTrayRuntime(AppPaths? paths = null)
        : this(paths, null, null, null)
    {
    }

    internal ClashTrayRuntime(
        AppPaths? paths,
        IStartupRegistration? startupRegistration,
        IServicePipeClient? servicePipeClient,
        ISettingsStore? settingsStore)
    {
        var useDefaultEnvironment = paths is null;
        _paths = paths ?? new AppPaths();
        _paths.EnsureDirectories();
        _configurationStore = new ConfigurationStore(_paths);
        _settingsStore = settingsStore ?? new SettingsStore(_paths);
        _startupRegistration = startupRegistration
            ?? (useDefaultEnvironment ? new StartupManager() : new StartupManager(new InMemoryStartupRegistry()));
        _servicePipeClient = servicePipeClient
            ?? (useDefaultEnvironment ? new ServicePipeClient() : new IsolatedServicePipeClient());
        _coreDiscovery = new CoreDiscovery(_paths);
        _systemProxy = new SystemProxyManager(_paths);
        _coreUpdater = new CoreUpdater(_paths);
        _subscriptionScheduler = new SubscriptionScheduler(
            cancellation => _configurationStore.ListAsync(cancellation),
            (profile, cancellation) => RefreshSubscriptionAsync(profile, cancellation),
            () => _settings,
            OnScheduledSubscriptionRefreshFailed,
            OnScheduledSubscriptionCycleFailed);
        _processManager.StateChanged += (_, state) => UpdateCoreState(state, state == CoreState.Failed ? "Mihomo 进程已退出" : null);
        _processManager.LogLineReceived += OnProcessLogLine;
    }

    public RuntimeSnapshot Snapshot => _snapshot;

    public AppSettings Settings => _settings;

    public StartupRegistrationStatus GetStartupStatus() => _startupRegistration.GetStatus();

    internal static bool ShouldAutomaticallyStartCore(
        AppSettings settings,
        bool hasActiveConfiguration,
        CoreState currentState) =>
        settings.StartCoreAutomatically
        && hasActiveConfiguration
        && currentState is CoreState.Missing or CoreState.Stopped or CoreState.Failed;

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        var storedConfigurations = await _configurationStore.ListAsync(cancellationToken);
        var activeConfigurationId = _settings.ActiveConfigurationId
            ?? storedConfigurations.FirstOrDefault(configuration => configuration.IsActive)?.Id;
        var configurations = storedConfigurations
            .Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, activeConfigurationId, StringComparison.OrdinalIgnoreCase)
            })
            .ToArray();
        _snapshot = _snapshot with
        {
            Configurations = configurations,
            Core = _snapshot.Core with
            {
                State = _coreDiscovery.FindExecutable() is null ? CoreState.Missing : CoreState.Stopped,
                Version = FindCoreVersion()
            },
            SystemProxy = _systemProxy.DetectState()
        };
        await ApplyProgramOverridesAsync(coreRunning: false, cancellationToken: cancellationToken);
        try
        {
            var serviceStatus = await _servicePipeClient.SendAsync(ServiceCommand.GetStatus, cancellationToken: cancellationToken);
            _snapshot = _snapshot with
            {
                Tun = serviceStatus.Tun,
                Core = _snapshot.Core with
                {
                    State = serviceStatus.Core == CoreState.Stopped && _snapshot.Core.State == CoreState.Missing
                        ? CoreState.Missing
                        : serviceStatus.Core
                }
            };
            Publish();
            if (serviceStatus.Core == CoreState.Running)
            {
                _usingServiceCore = true;
                _api = CreateApiClient();
                try
                {
                    await RefreshCoreHealthWithRetryAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    MarkCoreHealthUnconfirmed("初始化", exception);
                }

                await ApplyProgramOverridesAsync(coreRunning: true, cancellationToken: cancellationToken);
                StartPolling();
                StartOptionalRefreshInBackground(_api);
            }
        }
        catch (TimeoutException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
        }
        catch (IOException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
        }
        catch (UnauthorizedAccessException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
        }
        Publish();
        _subscriptionScheduler.Start();

        if (ShouldAutomaticallyStartCore(
            _settings,
            configurations.Any(configuration => configuration.IsActive),
            _snapshot.Core.State))
        {
            await StartCoreAsync(cancellationToken);
        }
    }

    public async Task StartCoreAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var coreStarted = false;
        try
        {
            if (_snapshot.Core.State == CoreState.Running)
            {
                return;
            }

            var profile = GetActiveConfiguration();
            var executable = _coreDiscovery.FindExecutable();
            if (executable is null)
            {
                UpdateCoreState(CoreState.Missing, "未找到 Mihomo 核心，请在设置中安装或选择 mihomo.exe");
                return;
            }

            if (profile is null)
            {
                UpdateCoreState(CoreState.Failed, "请先导入一个 Mihomo 配置");
                return;
            }

            UpdateCoreState(CoreState.Validating, null);
            var runtimeConfigPath = Path.Combine(_paths.RuntimeRoot, "mihomo", "active-config.yaml");
            await RuntimeConfigBuilder.BuildAsync(profile.Path, runtimeConfigPath, _settings, cancellationToken);

            var runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
            var servicePayload = JsonSerializer.Serialize(new ServiceCorePayload(
                executable,
                runtimeConfigPath,
                runtimeDirectory,
                _settings.ControllerPort,
                string.Empty));
            UpdateCoreState(CoreState.Starting, null);
            ServiceResponse? serviceResponse = null;
            try
            {
                serviceResponse = await _servicePipeClient.SendAsync(ServiceCommand.StartCore, servicePayload, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            catch (ServiceUnavailableException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (ServiceRequestUnknownException exception)
            {
                UpdateCoreState(CoreState.Failed, $"ClashTray 服务启动结果无法确认，请检查服务状态后重试：{exception.Message}");
                return;
            }
            catch (IOException exception)
            {
                UpdateCoreState(CoreState.Failed, $"ClashTray 服务通信失败，启动结果无法确认：{exception.Message}");
                return;
            }

            if (serviceResponse is not null)
            {
                if (!serviceResponse.Succeeded)
                {
                    throw new InvalidOperationException(serviceResponse.Error ?? "ClashTray 服务无法启动 Mihomo。");
                }

                _usingServiceCore = true;
            }
            else
            {
                if (!await _processManager.ValidateAsync(executable, runtimeConfigPath, cancellationToken))
                {
                    UpdateCoreState(CoreState.Failed, "Mihomo 配置验证失败");
                    return;
                }

                await _processManager.StartAsync(executable, runtimeConfigPath, runtimeDirectory, cancellationToken);
            }

            coreStarted = true;
            _api = CreateApiClient();
            SetCoreRunningPendingHealth(serviceResponse?.Tun ?? TunState.Unknown);
            try
            {
                await RefreshCoreHealthWithRetryAsync(cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                if (coreStarted && !_runtimeCts.IsCancellationRequested)
                {
                    MarkCoreHealthUnconfirmed("启动取消后同步", exception);
                    StartPolling();
                    StartOptionalRefreshInBackground(_api);
                }

                throw;
            }
            catch (Exception exception)
            {
                MarkCoreHealthUnconfirmed("启动", exception);
            }

            await ApplyProgramOverridesAsync(
                coreRunning: true,
                cancellationToken: cancellationToken,
                operationLockHeld: true);
            StartPolling();
            StartOptionalRefreshInBackground(_api);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateCoreState(CoreState.Failed, exception.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task StopCoreAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            UpdateCoreState(CoreState.Stopping, null);
            _api = null;
            await StopLogStreamAsync();
            if (_usingServiceCore)
            {
                try
                {
                    var response = await _servicePipeClient.SendAsync(ServiceCommand.StopCore, cancellationToken: cancellationToken);
                    if (!response.Succeeded)
                    {
                        _snapshot = _snapshot with { Tun = response.Tun };
                        if (response.Core == CoreState.Stopped)
                        {
                            _usingServiceCore = false;
                        }

                        UpdateCoreState(response.Core, response.Error ?? "ClashTray 服务无法停止 Mihomo。");
                        return;
                    }
                }
                catch (TimeoutException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{exception.Message}");
                    return;
                }
                catch (ServiceUnavailableException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{exception.Message}");
                    return;
                }
                catch (UnauthorizedAccessException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务访问被拒绝，停止结果无法确认：{exception.Message}");
                    return;
                }
                catch (ServiceRequestUnknownException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务停止结果无法确认，请检查服务状态后重试：{exception.Message}");
                    return;
                }
                catch (IOException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务通信失败，停止结果无法确认：{exception.Message}");
                    return;
                }

                _usingServiceCore = false;
            }
            else
            {
                await _processManager.StopAsync(cancellationToken);
            }

            UpdateCoreState(CoreState.Stopped, null);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartCoreAsync(CancellationToken cancellationToken = default)
    {
        UpdateCoreState(CoreState.Restarting, null);
        await StopCoreAsync(cancellationToken);
        await StartCoreAsync(cancellationToken);
    }

    public async Task<ConfigurationProfile> ImportLocalConfigurationAsync(string path, string? name = null, CancellationToken cancellationToken = default)
    {
        var profile = await _configurationStore.ImportLocalAsync(path, name, cancellationToken);
        await SetActiveConfigurationAsync(profile.Id, cancellationToken);
        return profile;
    }

    public async Task<ConfigurationProfile> ImportSubscriptionAsync(Uri uri, string? name = null, CancellationToken cancellationToken = default)
    {
        await _subscriptionOperationLock.WaitAsync(cancellationToken);
        try
        {
            UpdateSubscriptionState(SubscriptionState.Downloading, null);
            try
            {
                var profile = await _configurationStore.ImportSubscriptionAsync(uri, name, cancellationToken);
                UpdateSubscriptionState(SubscriptionState.Succeeded, null);
                await SetActiveConfigurationAsync(profile.Id, cancellationToken);
                return profile;
            }
            catch (Exception exception)
            {
                UpdateSubscriptionState(SubscriptionState.Failed, exception.Message);
                throw;
            }
        }
        finally
        {
            _subscriptionOperationLock.Release();
        }
    }

    public async Task RefreshSubscriptionAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        await _subscriptionOperationLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshSubscriptionCoreAsync(profile, cancellationToken);
        }
        finally
        {
            _subscriptionOperationLock.Release();
        }
    }

    private async Task RefreshSubscriptionCoreAsync(ConfigurationProfile profile, CancellationToken cancellationToken)
    {
        if (profile.SubscriptionUri is null)
        {
            return;
        }

        var shouldRemainActive = profile.IsActive
            || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
        var activeSelectionChanged = !string.Equals(
            _settings.ActiveConfigurationId,
            profile.Id,
            StringComparison.OrdinalIgnoreCase);
        UpdateSubscriptionState(SubscriptionState.Downloading, null);
        try
        {
            UpdateSubscriptionState(SubscriptionState.Validating, null);
            var update = await _configurationStore.ImportSubscriptionWithResultAsync(
                profile.SubscriptionUri,
                profile.Name,
                cancellationToken);
            UpdateSubscriptionState(SubscriptionState.Applying, null);
            UpdateSubscriptionState(SubscriptionState.Succeeded, null);
            var configurations = await _configurationStore.ListAsync(cancellationToken);
            if (shouldRemainActive)
            {
                await SetActiveConfigurationAsync(profile.Id, restartCore: false, cancellationToken: cancellationToken);
            }
            else
            {
                _snapshot = _snapshot with
                {
                    Configurations = configurations.Select(configuration => configuration with
                    {
                        IsActive = configuration.Id == _settings.ActiveConfigurationId
                    }).ToArray()
                };
                Publish();
            }

            if (shouldRemainActive && _snapshot.Core.State == CoreState.Running)
            {
                if (update.ContentChanged || activeSelectionChanged)
                {
                    await RestartCoreAsync(cancellationToken);
                }
                else
                {
                    _logBuffer.Add(new LogEntry(
                        DateTimeOffset.UtcNow,
                        "ClashTray",
                        "info",
                        "订阅内容 SHA-256 未变化，已跳过 Mihomo 重启。"));
                    _snapshot = _snapshot with { Logs = _logBuffer.Snapshot() };
                    Publish();
                }
            }
        }
        catch (Exception exception)
        {
            UpdateSubscriptionState(SubscriptionState.Failed, exception.Message);
            throw;
        }
    }

    public async Task ReloadConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.SubscriptionUri is not null)
        {
            await RefreshSubscriptionAsync(profile, cancellationToken);
            return;
        }

        await _configurationStore.ReloadAsync(profile, cancellationToken);
        var configurations = await _configurationStore.ListAsync(cancellationToken);
        _snapshot = _snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = configuration.Id == _settings.ActiveConfigurationId
            }).ToArray()
        };
        Publish();

        if (string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase)
            && _snapshot.Core.State == CoreState.Running)
        {
            await RestartCoreAsync(cancellationToken);
        }
    }

    public Task SetActiveConfigurationAsync(string id, CancellationToken cancellationToken = default) =>
        SetActiveConfigurationAsync(id, restartCore: true, cancellationToken: cancellationToken);

    private async Task SetActiveConfigurationAsync(
        string id,
        bool restartCore,
        CancellationToken cancellationToken = default)
    {
        var configurations = await _configurationStore.ListAsync(cancellationToken);
        var selected = configurations.FirstOrDefault(configuration => configuration.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            throw new FileNotFoundException("Configuration profile not found.", id);
        }

        var changed = !string.Equals(_settings.ActiveConfigurationId, id, StringComparison.OrdinalIgnoreCase);
        _settings = _settings with { ActiveConfigurationId = id };
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        _snapshot = _snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with { IsActive = configuration.Id == id }).ToArray(),
            Core = _snapshot.Core with { ConfigurationName = selected.Name }
        };
        Publish();

        if (changed && restartCore && _snapshot.Core.State == CoreState.Running)
        {
            await RestartCoreAsync(cancellationToken);
        }
    }

    public async Task DeleteConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        var wasActive = profile.IsActive
            || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
        if (wasActive && _snapshot.Core.State == CoreState.Running)
        {
            await StopCoreAsync(cancellationToken);
        }

        await _configurationStore.DeleteAsync(profile, cancellationToken);
        var configurations = await _configurationStore.ListAsync(cancellationToken);
        if (wasActive)
        {
            _settings = _settings with { ActiveConfigurationId = null };
            await _settingsStore.SaveAsync(_settings, cancellationToken);
        }

        _snapshot = _snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = configuration.Id == _settings.ActiveConfigurationId
            }).ToArray(),
            Core = wasActive ? _snapshot.Core with { ConfigurationName = null } : _snapshot.Core
        };
        Publish();
    }

    public async Task SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_api is null)
            {
                throw new InvalidOperationException("Mihomo 核心尚未运行。");
            }

            await _api.SetModeAsync(mode, cancellationToken);
            await RefreshFromApiAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            throw new InvalidOperationException("Mihomo 核心尚未运行。");
        }

        await _api.SelectProxyAsync(group, proxy, cancellationToken);
        await RefreshFromApiAsync(cancellationToken);
    }

    public async Task<int?> TestProxyDelayAsync(string proxy, CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            throw new InvalidOperationException("Mihomo 核心尚未运行。");
        }

        using var response = await _api.TestDelayAsync(proxy, new Uri("https://www.gstatic.com/generate_204"), 5000, cancellationToken);
        if (response.RootElement.TryGetProperty("delay", out var delay) && delay.TryGetInt32(out var milliseconds))
        {
            return milliseconds;
        }

        return null;
    }

    public async Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayAsync(
        string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtimeCts.Token);
        var token = linked.Token;
        await _operationLock.WaitAsync(token);
        try
        {
            var api = _api ?? throw new InvalidOperationException("Mihomo 核心尚未运行。");
            using var response = await api.TestGroupDelayAsync(group, new Uri("https://www.gstatic.com/generate_204"), 5000, token);
            var delays = MihomoDataParser.ParseGroupDelays(response);
            await _dataRefreshLock.WaitAsync(token);
            try
            {
                // Refresh now/history from the core and apply the confirmed batch result atomically.
                var proxies = await TryGetProxyDataAsync(api, token);
                if (!ReferenceEquals(_api, api)) throw new InvalidOperationException("测速期间核心已切换，请重新测速。");
                string? LatestDelay(string name, string? previous) => delays.TryGetValue(name, out var delay)
                    ? delay?.ToString(System.Globalization.CultureInfo.InvariantCulture) : previous;
                _snapshot = _snapshot with
                {
                    ProxyGroups = proxies.Groups.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray(),
                    ProxyNodes = proxies.Nodes.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray()
                };
                Publish();
            }
            finally { _dataRefreshLock.Release(); }
            return delays;
        }
        finally { _operationLock.Release(); }
    }

    public async Task CloseConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return;
        }

        await _api.CloseConnectionAsync(id, cancellationToken);
        await RefreshFromApiAsync(cancellationToken);
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return;
        }

        await _api.CloseAllConnectionsAsync(cancellationToken);
        await RefreshFromApiAsync(cancellationToken);
    }

    public async Task RefreshProviderAsync(string name, bool rules, CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return;
        }

        if (rules)
        {
            await _api.RefreshRuleProviderAsync(name, cancellationToken);
        }
        else
        {
            await _api.RefreshProviderAsync(name, cancellationToken);
        }

        await RefreshFromApiAsync(cancellationToken);
    }

    public void ClearLogs()
    {
        _logBuffer.Clear();
        _snapshot = _snapshot with { Logs = [] };
        Publish();
    }

    public async Task ClearFakeIpCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_api is not null)
        {
            await _api.ClearFakeIpCacheAsync(cancellationToken);
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default, bool reconcileStartup = false)
    {
        ValidateSettings(settings);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            var networkSettingsChanged = settings.AllowLan != _settings.AllowLan
                || settings.Ipv6 != _settings.Ipv6;
            StartupRegistrationChange? startupChange = null;
            if (reconcileStartup)
            {
                startupChange = _startupRegistration.Ensure(
                    settings.StartWithWindows,
                    ResolveStartupExecutablePath(settings.StartWithWindows));
            }

            try
            {
                await _settingsStore.SaveAsync(settings, cancellationToken);
            }
            catch (Exception saveException)
            {
                if (startupChange is { Changed: true })
                {
                    try
                    {
                        _startupRegistration.Rollback(startupChange);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "设置保存失败，且 Windows 启动项回滚失败。",
                            new AggregateException(saveException, rollbackException));
                    }
                }

                throw;
            }
            _settings = settings;
            if (networkSettingsChanged && _api is not null)
            {
                try
                {
                    await ApplyProgramNetworkPreferencesAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogControllerFailure("程序局域网/IPv6 设置覆盖", "/configs", exception, 0);
                    _snapshot = _snapshot with
                    {
                        ErrorMessage = $"程序局域网/IPv6 设置应用失败：{exception.Message}",
                        Logs = _logBuffer.Snapshot()
                    };
                }
            }

            Publish();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task SetSystemProxyAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetSystemProxyCoreAsync(enabled, persistPreference: true, cancellationToken: cancellationToken);

    private async Task SetSystemProxyCoreAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var previousSettings = _settings;
        var preferenceChanged = persistPreference && previousSettings.SystemProxyEnabled != enabled;
        try
        {
            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { SystemProxyEnabled = enabled },
                    cancellationToken);
            }

            _snapshot = _snapshot with { SystemProxy = enabled ? SystemProxyState.Enabling : SystemProxyState.Disabling };
            Publish();
            if (enabled)
            {
                await _systemProxy.EnableAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
            else
            {
                await _systemProxy.DisableAsync(cancellationToken);
            }

            _snapshot = _snapshot with { SystemProxy = _systemProxy.State, ErrorMessage = null };
            Publish();
        }
        catch
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { SystemProxy = _systemProxy.State };
            Publish();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task SetTunAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetTunCoreAsync(enabled, persistPreference: true, cancellationToken: cancellationToken);

    private async Task SetTunCoreAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        var previousSettings = _settings;
        var preferenceChanged = persistPreference && previousSettings.TunEnabled != enabled;
        try
        {
            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { TunEnabled = enabled },
                    cancellationToken);
            }

            _snapshot = _snapshot with { Tun = enabled ? TunState.Enabling : TunState.Disabling };
            Publish();
            var payload = JsonSerializer.Serialize(new ServiceTunPayload(
                _settings.ControllerPort,
                string.Empty,
                enabled));
            var response = await _servicePipeClient.SendAsync(
                enabled ? ServiceCommand.EnableTun : ServiceCommand.DisableTun,
                payload,
                cancellationToken: cancellationToken);
            if (!response.Succeeded)
            {
                throw new InvalidOperationException(response.Error ?? "TUN 操作失败。");
            }

            _snapshot = _snapshot with { Tun = response.Tun, ErrorMessage = null };
            Publish();
        }
        catch (TimeoutException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (ServiceRequestUnknownException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Failed, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 操作结果无法确认，请检查服务状态后重试。", exception);
        }
        catch (IOException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Failed };
            Publish();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ClearDnsCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_api is not null)
        {
            await _api.ClearDnsCacheAsync(cancellationToken);
        }
    }

    public async Task UpdateGeoAsync(CancellationToken cancellationToken = default)
    {
        if (_api is not null)
        {
            await _api.UpdateGeoAsync(cancellationToken);
            await RefreshFromApiAsync(cancellationToken);
        }
    }

    public async Task<string> InstallCoreUpdateAsync(CoreUpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        var wasRunning = _snapshot.Core.State == CoreState.Running;
        if (wasRunning)
        {
            await StopCoreAsync(cancellationToken);
        }

        try
        {
            var path = await _coreUpdater.DownloadAndInstallAsync(manifest, cancellationToken);
            _snapshot = _snapshot with { Core = _snapshot.Core with { Version = FindCoreVersion(), ErrorMessage = null }, ErrorMessage = null };
            Publish();
            if (wasRunning)
            {
                await StartCoreAsync(cancellationToken);
            }

            return path;
        }
        catch
        {
            if (wasRunning)
            {
                await StartCoreAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task RefreshDataAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null)
        {
            return;
        }

        await RefreshFromApiWithRetryAsync(cancellationToken);
    }

    internal void AttachControllerForTesting(MihomoApiClient api, bool usingServiceCore)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _usingServiceCore = usingServiceCore;
        _snapshot = _snapshot with { Core = _snapshot.Core with { State = CoreState.Running } };
    }

    internal Task RefreshControllerDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_snapshot.Tun == TunState.On)
        {
            try
            {
                await SetTunCoreAsync(false, persistPreference: false, cancellationToken: CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_snapshot.SystemProxy is SystemProxyState.On or SystemProxyState.RestoreRequired)
        {
            try
            {
                await SetSystemProxyCoreAsync(false, persistPreference: false, cancellationToken: CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_usingServiceCore)
        {
            try
            {
                await StopCoreAsync(CancellationToken.None);
            }
            catch
            {
            }
        }

        _runtimeCts.Cancel();
        _api = null;
        await StopLogStreamAsync();
        if (_dataRefreshTask is not null)
        {
            try
            {
                await _dataRefreshTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _subscriptionScheduler.DisposeAsync();
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _processManager.DisposeAsync();
        _httpClient.Dispose();
        _subscriptionOperationLock.Dispose();
        _dataRefreshLock.Dispose();
        _operationLock.Dispose();
        _runtimeCts.Dispose();
    }

    private async Task RefreshFromApiAsync(CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null)
        {
            return;
        }

        await RefreshCoreHealthAsync(api, cancellationToken);
        await RefreshOptionalDataAsync(api, cancellationToken);
    }

    private async Task RefreshCoreHealthAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        using var version = await api.GetVersionAsync(cancellationToken);
        var versionText = MihomoDataParser.ParseVersion(version);
        using var configurationState = await api.GetConfigurationAsync(force: false, cancellationToken);
        var mode = MihomoDataParser.ParseMode(configurationState);
        var tunEnabled = MihomoDataParser.ParseTunEnabled(configurationState);

        if (!ReferenceEquals(_api, api))
        {
            return;
        }

        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with
            {
                State = CoreState.Running,
                Version = versionText ?? _snapshot.Core.Version,
                Mode = mode ?? _snapshot.Core.Mode,
                ErrorMessage = null
            },
            Logs = _logBuffer.Snapshot(),
            Tun = tunEnabled is null
                ? _snapshot.Tun
                : tunEnabled.Value ? TunState.On : TunState.Off,
            ErrorMessage = null
        };
        Publish();
        EnsureLogStreamStarted();
    }

    private async Task RefreshOptionalDataAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_api, api))
            {
                return;
            }

            var proxyTask = TryGetProxyDataAsync(api, cancellationToken);
            var trafficTask = TryGetTrafficSnapshotAsync(api, cancellationToken);
            var memoryTask = TryGetMemoryAsync(api, cancellationToken);
            var connectionsTask = TryGetConnectionDataAsync(api, cancellationToken);
            var rulesTask = TryGetRulesAsync(api, cancellationToken);
            var providersTask = TryGetProvidersAsync(api, cancellationToken);
            await Task.WhenAll(proxyTask, trafficTask, memoryTask, connectionsTask, rulesTask, providersTask);

            if (!ReferenceEquals(_api, api))
            {
                return;
            }

            var proxyData = await proxyTask;
            var trafficData = await trafficTask;
            var memoryData = await memoryTask;
            var connectionData = await connectionsTask;
            var rulesData = await rulesTask;
            var providerData = await providersTask;
            var currentCore = _snapshot.Core;
            var traffic = trafficData.Value;

            _snapshot = _snapshot with
            {
                Core = currentCore with
                {
                    UploadBytes = traffic?.UploadBytes ?? currentCore.UploadBytes,
                    DownloadBytes = traffic?.DownloadBytes ?? currentCore.DownloadBytes,
                    UploadBytesPerSecond = traffic?.UploadBytesPerSecond ?? currentCore.UploadBytesPerSecond,
                    DownloadBytesPerSecond = traffic?.DownloadBytesPerSecond ?? currentCore.DownloadBytesPerSecond,
                    TrafficAvailable = trafficData.Succeeded,
                    ConnectionCount = connectionData.Succeeded ? connectionData.Value.Count : currentCore.ConnectionCount,
                    MemoryBytes = memoryData.Value,
                    MemoryAvailable = memoryData.Succeeded
                },
                ProxyGroups = proxyData.Succeeded ? proxyData.Groups : _snapshot.ProxyGroups,
                ProxyNodes = proxyData.Succeeded ? proxyData.Nodes : _snapshot.ProxyNodes,
                Connections = connectionData.Succeeded ? connectionData.Value : _snapshot.Connections,
                Rules = rulesData,
                Providers = providerData.Providers,
                RuleProviders = providerData.RuleProviders,
                Logs = _logBuffer.Snapshot()
            };
            Publish();
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    private async Task RefreshFromApiWithRetryAsync(CancellationToken cancellationToken)
    {
        await RefreshCoreHealthWithRetryAsync(cancellationToken);
        await ApplyProgramOverridesAsync(coreRunning: true, cancellationToken: cancellationToken);
        var api = _api;
        if (api is not null)
        {
            await RefreshOptionalDataAsync(api, cancellationToken);
        }
    }

    private async Task RefreshCoreHealthWithRetryAsync(CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null)
        {
            return;
        }

        Exception? lastException = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await RefreshCoreHealthAsync(api, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (attempt < 4)
            {
                lastException = exception;
                LogControllerFailure("核心健康检查", "/version 或 /configs", exception, attempt + 1, stopwatch.Elapsed);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
            }
            catch (Exception exception)
            {
                lastException = exception;
                LogControllerFailure("核心健康检查", "/version 或 /configs", exception, attempt + 1, stopwatch.Elapsed);
            }
        }

        throw lastException ?? new HttpRequestException("Mihomo 控制器暂未就绪。");
    }

    private void StartPolling()
    {
        if (_pollingTask is { IsCompleted: false })
        {
            return;
        }

        _pollingTask = Task.Run(RunPollingAsync, CancellationToken.None);
    }

    private async Task RunPollingAsync()
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var retryCount = 0;
        while (ShouldContinuePolling())
        {
            try
            {
                await Task.Delay(retryDelay, _runtimeCts.Token);
                if (!ShouldContinuePolling())
                {
                    break;
                }

                await RefreshFromApiAsync(_runtimeCts.Token);
                await ApplyProgramOverridesAsync(coreRunning: true, cancellationToken: _runtimeCts.Token);
                retryDelay = TimeSpan.FromSeconds(2);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                retryCount++;
                if (!_usingServiceCore)
                {
                    if (_processManager.State == CoreState.Running)
                    {
                        MarkCoreHealthUnconfirmed("轮询", exception, retryCount);
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    await StopLogStreamAsync();
                    break;
                }

                ServiceResponse? serviceStatus = null;
                Exception? serviceException = null;
                try
                {
                    serviceStatus = await _servicePipeClient.SendAsync(
                        ServiceCommand.GetStatus,
                        cancellationToken: _runtimeCts.Token);
                }
                catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception statusException)
                {
                    serviceException = statusException;
                }

                if (serviceStatus is null)
                {
                    _api = null;
                    await StopLogStreamAsync();
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    MarkCoreHealthUnconfirmed("服务重连", serviceException ?? exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                if (serviceStatus.Core == CoreState.Running)
                {
                    _api = CreateApiClient();
                    _snapshot = _snapshot with { Tun = serviceStatus.Tun };
                    MarkCoreHealthUnconfirmed("控制器重连", exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                _api = null;
                await StopLogStreamAsync();
                _snapshot = _snapshot with { Tun = serviceStatus.Tun };
                if (serviceStatus.Core is CoreState.Stopped or CoreState.Failed)
                {
                    UpdateCoreState(
                        serviceStatus.Core,
                        serviceStatus.Core == CoreState.Failed ? "Mihomo 服务进程已停止" : null);
                    break;
                }

                UpdateCoreState(serviceStatus.Core, "核心状态暂时无法确认，正在等待服务完成状态同步。");
                retryDelay = IncreaseRetryDelay(retryDelay);
            }
        }
    }

    private bool ShouldContinuePolling() =>
        !_runtimeCts.IsCancellationRequested
        && (_usingServiceCore || (_api is not null && _processManager.State == CoreState.Running));

    private static TimeSpan IncreaseRetryDelay(TimeSpan current) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Max(2, current.TotalSeconds * 2)));

    private void StartOptionalRefreshInBackground(MihomoApiClient? api)
    {
        if (api is null || _dataRefreshTask is { IsCompleted: false })
        {
            return;
        }

        _dataRefreshTask = Task.Run(
            () => RunOptionalRefreshAsync(api),
            CancellationToken.None);
    }

    private async Task RunOptionalRefreshAsync(MihomoApiClient api)
    {
        try
        {
            await RefreshOptionalDataAsync(api, _runtimeCts.Token);
        }
        catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogControllerFailure("后台数据刷新", "/metrics", exception, 0);
        }
    }

    private MihomoApiClient CreateApiClient()
    {
        var controllerUri = new Uri($"http://127.0.0.1:{_settings.ControllerPort}/");
        return new MihomoApiClient(_httpClient, controllerUri, string.Empty);
    }

    private async Task ApplyProgramOverridesAsync(
        bool coreRunning,
        CancellationToken cancellationToken,
        bool operationLockHeld = false)
    {
        if (!operationLockHeld)
        {
            await _operationLock.WaitAsync(cancellationToken);
        }

        try
        {
            await ApplyProgramOverridesCoreAsync(coreRunning, cancellationToken);
        }
        finally
        {
            if (!operationLockHeld)
            {
                _operationLock.Release();
            }
        }
    }

    private async Task ApplyProgramOverridesCoreAsync(bool coreRunning, CancellationToken cancellationToken)
    {
        if (coreRunning && _api is not null)
        {
            try
            {
                await ApplyProgramNetworkPreferencesAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogControllerFailure("程序局域网/IPv6 设置覆盖", "/configs", exception, 0);
                _snapshot = _snapshot with
                {
                    ErrorMessage = $"程序局域网/IPv6 设置应用失败：{exception.Message}",
                    Logs = _logBuffer.Snapshot()
                };
                Publish();
            }

            try
            {
                await ApplyProgramTunPreferenceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogControllerFailure("程序 TUN 设置覆盖", "/configs", exception, 0);
                _snapshot = _snapshot with
                {
                    ErrorMessage = $"程序 TUN 设置应用失败：{exception.Message}",
                    Logs = _logBuffer.Snapshot()
                };
                Publish();
            }
        }

        try
        {
            await ReconcileSystemProxyAsync(coreRunning, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logBuffer.Add(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", $"程序系统代理设置应用失败：{exception.Message}"));
            _snapshot = _snapshot with
            {
                SystemProxy = _systemProxy.State,
                ErrorMessage = $"程序系统代理设置应用失败：{exception.Message}",
                Logs = _logBuffer.Snapshot()
            };
            Publish();
        }
    }

    private async Task ApplyProgramNetworkPreferencesAsync(CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null)
        {
            return;
        }

        using var configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        var currentAllowLan = MihomoDataParser.ParseAllowLan(configuration);
        var currentIpv6 = MihomoDataParser.ParseIpv6(configuration);
        if (currentAllowLan is bool currentAllowLanValue
            && currentAllowLanValue == _settings.AllowLan
            && currentIpv6 is bool currentIpv6Value
            && currentIpv6Value == _settings.Ipv6)
        {
            return;
        }

        try
        {
            using var response = await api.SetNetworkSettingsAsync(
                _settings.AllowLan,
                _settings.Ipv6,
                cancellationToken);
            if (!await ConfirmNetworkSettingsAsync(
                    api,
                    _settings.AllowLan,
                    _settings.Ipv6,
                    cancellationToken))
            {
                throw new InvalidOperationException("Mihomo 未确认程序局域网/IPv6 设置。");
            }
        }
        catch
        {
            if (!await TryRestoreNetworkSettingsAsync(api, currentAllowLan, currentIpv6))
            {
                _logBuffer.Add(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "error",
                    "程序局域网/IPv6 设置应用失败，且无法恢复核心原始设置。"));
            }

            throw;
        }
    }

    private async Task ApplyProgramTunPreferenceAsync(CancellationToken cancellationToken)
    {
        var api = _api;
        if (api is null)
        {
            return;
        }

        using var configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        var current = MihomoDataParser.ParseTunEnabled(configuration);
        if (current is not bool currentValue || currentValue == _settings.TunEnabled)
        {
            return;
        }

        try
        {
            await api.SetTunAsync(_settings.TunEnabled, cancellationToken);
            if (!await ConfirmTunStateAsync(api, _settings.TunEnabled, cancellationToken))
            {
                throw new InvalidOperationException("Mihomo 未确认程序 TUN 设置。");
            }
        }
        catch
        {
            if (!await TryRestoreTunStateAsync(api, currentValue))
            {
                _snapshot = _snapshot with { Tun = TunState.Unknown };
                Publish();
            }

            throw;
        }

        if (ReferenceEquals(_api, api))
        {
            _snapshot = _snapshot with { Tun = _settings.TunEnabled ? TunState.On : TunState.Off, ErrorMessage = null };
            Publish();
        }
    }

    private async Task ReconcileSystemProxyAsync(bool coreRunning, CancellationToken cancellationToken)
    {
        if (_settings.SystemProxyEnabled)
        {
            if (coreRunning && _systemProxy.State is (SystemProxyState.Off or SystemProxyState.Failed))
            {
                await _systemProxy.EnableAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
        }
        else if (_systemProxy.State is SystemProxyState.On or SystemProxyState.RestoreRequired)
        {
            await _systemProxy.DisableAsync(cancellationToken);
        }

        var state = _systemProxy.State;
        if (_snapshot.SystemProxy != state)
        {
            _snapshot = _snapshot with { SystemProxy = state };
            Publish();
        }
    }

    private async Task SaveSettingsForOperationAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        await _settingsStore.SaveAsync(settings, cancellationToken);
    }

    private async Task RestoreSettingsAfterOperationFailureAsync(AppSettings settings)
    {
        _settings = settings;
        try
        {
            await _settingsStore.SaveAsync(settings, CancellationToken.None);
        }
        catch
        {
        }
    }

    private static async Task<bool> ConfirmTunStateAsync(
        MihomoApiClient api,
        bool expected,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
            if (MihomoDataParser.ParseTunEnabled(configuration) == expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private static async Task<bool> TryRestoreTunStateAsync(MihomoApiClient api, bool expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await api.SetTunAsync(expected, timeout.Token);
            return await ConfirmTunStateAsync(api, expected, timeout.Token);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ConfirmNetworkSettingsAsync(
        MihomoApiClient api,
        bool? expectedAllowLan,
        bool? expectedIpv6,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
            var allowLan = MihomoDataParser.ParseAllowLan(configuration);
            var ipv6 = MihomoDataParser.ParseIpv6(configuration);
            var allowLanMatches = !expectedAllowLan.HasValue
                || allowLan is bool allowLanValue && allowLanValue == expectedAllowLan.Value;
            var ipv6Matches = !expectedIpv6.HasValue
                || ipv6 is bool ipv6Value && ipv6Value == expectedIpv6.Value;
            if (allowLanMatches && ipv6Matches)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private static async Task<bool> TryRestoreNetworkSettingsAsync(
        MihomoApiClient api,
        bool? allowLan,
        bool? ipv6)
    {
        if (!allowLan.HasValue && !ipv6.HasValue)
        {
            return true;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await api.SetNetworkSettingsAsync(allowLan, ipv6, timeout.Token);
            return await ConfirmNetworkSettingsAsync(api, allowLan, ipv6, timeout.Token);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureLogStreamStarted()
    {
        if (!_usingServiceCore)
        {
            return;
        }

        lock (_logStreamGate)
        {
            var api = _api;
            if (api is null || _logStreamTask is { IsCompleted: false })
            {
                return;
            }

            _logStreamCts?.Dispose();
            var streamCts = CancellationTokenSource.CreateLinkedTokenSource(_runtimeCts.Token);
            _logStreamCts = streamCts;
            _logStreamTask = Task.Run(
                () => RunLogStreamAsync(api, streamCts.Token),
                CancellationToken.None);
        }
    }

    private async Task StopLogStreamAsync()
    {
        Task? task;
        CancellationTokenSource? streamCts;
        lock (_logStreamGate)
        {
            task = _logStreamTask;
            streamCts = _logStreamCts;
            _logStreamTask = null;
            _logStreamCts = null;
        }

        streamCts?.Cancel();
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            streamCts?.Dispose();
        }
    }

    private async Task RunLogStreamAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var path = $"/logs?level={Uri.EscapeDataString(_settings.LogLevel)}&format=structured";
        while (!cancellationToken.IsCancellationRequested && ReferenceEquals(_api, api))
        {
            try
            {
                using var socket = await api.ConnectWebSocketAsync(path, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(1);
                await ReceiveLogMessagesAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (WebSocketException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }

            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(_api, api))
            {
                break;
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
        }
    }

    private async Task ReceiveLogMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            var isText = true;
            var isOversized = false;
            while (true)
            {
                var received = await socket.ReceiveAsync(
                    new ArraySegment<byte>(receiveBuffer),
                    cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                isText &= received.MessageType == WebSocketMessageType.Text;
                if (isText && !isOversized)
                {
                    if (message.Length > MaxLogMessageBytes - received.Count)
                    {
                        isOversized = true;
                    }
                    else
                    {
                        message.Write(receiveBuffer, 0, received.Count);
                    }
                }

                if (received.EndOfMessage)
                {
                    break;
                }
            }

            if (!isText || isOversized || message.Length == 0)
            {
                continue;
            }

            message.Position = 0;
            try
            {
                using var document = JsonDocument.Parse(message);
                foreach (var entry in MihomoDataParser.ParseLogs(document, "mihomo"))
                {
                    AddMihomoLog(entry);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private async Task<ProxyDataResult> TryGetProxyDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await api.GetProxiesAsync(cancellationToken);
            var data = MihomoDataParser.ParseProxies(document);
            return new ProxyDataResult(true, data.Groups, data.Nodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("代理数据刷新", "/proxies", exception, 0);
            return new ProxyDataResult(false, _snapshot.ProxyGroups, _snapshot.ProxyNodes);
        }
    }

    private async Task<TrafficDataResult> TryGetTrafficSnapshotAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var document = await api.GetTrafficAsync(cancellationToken);
            return new TrafficDataResult(true, MihomoDataParser.ParseTraffic(document));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("指标刷新", "/traffic", exception, 0, stopwatch.Elapsed);
            return new TrafficDataResult(false, null);
        }
    }

    private async Task<MemoryDataResult> TryGetMemoryAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var memory = await api.GetMemoryAsync(cancellationToken);
            return new MemoryDataResult(true, MihomoDataParser.ParseMemoryBytes(memory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("指标刷新", "/memory", exception, 0, stopwatch.Elapsed);
            return new MemoryDataResult(false, _snapshot.Core.MemoryBytes);
        }
    }

    private async Task<ConnectionDataResult> TryGetConnectionDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await api.GetConnectionsAsync(cancellationToken);
            return new ConnectionDataResult(true, MihomoDataParser.ParseConnections(document));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("连接数据刷新", "/connections", exception, 0);
            return new ConnectionDataResult(false, _snapshot.Connections);
        }
    }

    private async Task<IReadOnlyList<RuleInfo>> TryGetRulesAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await api.GetRulesAsync(cancellationToken);
            return MihomoDataParser.ParseRules(document);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("规则数据刷新", "/rules", exception, 0);
            return _snapshot.Rules;
        }
    }

    private async Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> TryGetProvidersAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken)
    {
        try
        {
            using var providers = await api.GetProvidersAsync(cancellationToken);
            using var ruleProviders = await api.GetRuleProvidersAsync(cancellationToken);
            return (MihomoDataParser.ParseProviders(providers, "proxy"), MihomoDataParser.ParseProviders(ruleProviders, "rule"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("Provider 数据刷新", "/providers", exception, 0);
            return (_snapshot.Providers, _snapshot.RuleProviders);
        }
    }

    private async Task<IReadOnlyList<LogEntry>> TryGetLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var logs = await _api!.GetLogsAsync(_settings.LogLevel, cancellationToken);
            foreach (var entry in MihomoDataParser.ParseLogs(logs, "mihomo"))
            {
                _logBuffer.Add(entry);
            }

            return _logBuffer.Snapshot();
        }
        catch (HttpRequestException)
        {
            return _logBuffer.Snapshot();
        }
    }

    private ConfigurationProfile? GetActiveConfiguration() =>
        _snapshot.Configurations.FirstOrDefault(configuration => configuration.IsActive)
        ?? _snapshot.Configurations.FirstOrDefault(configuration => configuration.Id == _settings.ActiveConfigurationId);

    private string? FindCoreVersion()
    {
        var path = _coreDiscovery.FindExecutable();
        return path is null ? null : CoreDiscovery.GetVersion(path);
    }

    private void SetCoreRunningPendingHealth(TunState tunState)
    {
        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with
            {
                State = CoreState.Running,
                ErrorMessage = null,
                TrafficAvailable = false,
                MemoryAvailable = false
            },
            Tun = tunState,
            ErrorMessage = null
        };
        Publish();
    }

    private void MarkCoreHealthUnconfirmed(string phase, Exception exception, int retryCount = 0)
    {
        var message = $"核心状态暂时无法确认（{phase}：{DescribeControllerError(exception)}）。";
        LogControllerFailure(phase, "/version 或 /configs", exception, retryCount);
        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with { State = CoreState.Running, ErrorMessage = message },
            ErrorMessage = message,
            Logs = _logBuffer.Snapshot()
        };
        Publish();
    }

    private void LogControllerFailure(
        string phase,
        string path,
        Exception exception,
        int retryCount,
        TimeSpan? elapsed = null)
    {
        var status = exception is HttpRequestException { StatusCode: { } statusCode }
            ? $"HTTP {(int)statusCode}"
            : "HTTP 未确认";
        var duration = elapsed is null ? "未测量" : $"{elapsed.Value.TotalMilliseconds:0}ms";
        var hosting = _usingServiceCore ? "service" : "local";
        var message = $"{phase}失败：托管方式={hosting}，路径={path}，{status}，耗时={duration}，重试={retryCount}，错误类型={DescribeControllerError(exception)}。";
        _logBuffer.Add(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "warning", message));
    }

    private static string DescribeControllerError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
        MihomoStreamException streamException => $"{streamException.Path} {streamException.Kind}",
        TimeoutException => "首条记录超时",
        OperationCanceledException => "已取消",
        _ => exception.GetType().Name
    };

    private void UpdateCoreState(CoreState state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logBuffer.Add(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with { State = state, ErrorMessage = error },
            ErrorMessage = error,
            Logs = _logBuffer.Snapshot()
        };
        Publish();
    }

    private void UpdateSubscriptionState(SubscriptionState state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logBuffer.Add(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _snapshot = _snapshot with { Subscription = state, ErrorMessage = error, Logs = _logBuffer.Snapshot() };
        Publish();
    }

    private void OnScheduledSubscriptionRefreshFailed(ConfigurationProfile profile, Exception exception)
    {
        UpdateSubscriptionState(SubscriptionState.Failed, $"订阅 {profile.Name} 定时刷新失败：{exception.Message}");
    }

    private void OnScheduledSubscriptionCycleFailed(Exception exception)
    {
        UpdateSubscriptionState(SubscriptionState.Failed, $"定时订阅任务失败：{exception.Message}");
    }

    private void Publish() => SnapshotChanged?.Invoke(this, _snapshot);

    private void OnProcessLogLine(string line, bool isError)
    {
        AddMihomoLog(new LogEntry(DateTimeOffset.UtcNow, "mihomo", isError ? "error" : "info", line));
    }

    private void AddMihomoLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _snapshot = _snapshot with { Logs = _logBuffer.Snapshot() };
        Publish();
    }

    private static void ValidateSettings(AppSettings settings)
        => SettingsValidator.Validate(settings);

    private static string? ResolveStartupExecutablePath(bool required)
    {
        var path = Environment.ProcessPath;
        var valid = !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path)
            && File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        if (valid)
        {
            return Path.GetFullPath(path!);
        }

        if (required)
        {
            throw new InvalidOperationException(
                "无法确定 ClashTray 的真实可执行文件路径，未修改 Windows 启动项。请从已安装目录启动应用。");
        }

        return null;
    }

    private static RuntimeSnapshot CreateInitialSnapshot() => new(
        new CoreStatus(CoreState.Missing, null, null, ProxyMode.Rule, 0, 0, 0, 0, 0, 0, null),
        SystemProxyState.Off,
        TunState.Unavailable,
        SubscriptionState.Idle,
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        null);
}









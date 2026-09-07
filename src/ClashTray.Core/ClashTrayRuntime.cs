using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class ClashTrayRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly BoundedLogBuffer _logBuffer = new(500);
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configurationStore;
    private readonly SettingsStore _settingsStore;
    private readonly CoreDiscovery _coreDiscovery;
    private readonly SystemProxyManager _systemProxy;
    private readonly ControllerSecretStore _secretStore;
    private readonly RuntimeConfigBuilder _runtimeConfigBuilder;
    private readonly ServicePipeClient _servicePipeClient = new();
    private readonly CoreUpdater _coreUpdater;
    private readonly SubscriptionScheduler _subscriptionScheduler;
    private readonly MihomoProcessManager _processManager = new();
    private readonly object _logStreamGate = new();
    private MihomoApiClient? _api;
    private Task? _pollingTask;
    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private AppSettings _settings = new();
    private RuntimeSnapshot _snapshot = CreateInitialSnapshot();
    private bool _usingServiceCore;

    private const int MaxLogMessageBytes = 1024 * 1024;

    public ClashTrayRuntime(AppPaths? paths = null)
    {
        _paths = paths ?? new AppPaths();
        _paths.EnsureDirectories();
        _configurationStore = new ConfigurationStore(_paths);
        _settingsStore = new SettingsStore(_paths);
        _coreDiscovery = new CoreDiscovery(_paths);
        _systemProxy = new SystemProxyManager(_paths);
        _secretStore = new ControllerSecretStore(_paths);
        _runtimeConfigBuilder = new RuntimeConfigBuilder(_secretStore);
        _coreUpdater = new CoreUpdater(_paths);
        _subscriptionScheduler = new SubscriptionScheduler(
            cancellation => _configurationStore.ListAsync(cancellation),
            (profile, cancellation) => RefreshSubscriptionAsync(profile, cancellation),
            () => _settings);
        _processManager.StateChanged += (_, state) => UpdateCoreState(state, state == CoreState.Failed ? "Mihomo 进程已退出" : null);
        _processManager.LogLineReceived += OnProcessLogLine;
    }

    public RuntimeSnapshot Snapshot => _snapshot;

    public AppSettings Settings => _settings;

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
            if (serviceStatus.Core == CoreState.Running)
            {
                _usingServiceCore = true;
                _api = CreateApiClient();
                try
                {
                    await RefreshFromApiWithRetryAsync(cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    UpdateCoreState(CoreState.Failed, $"Mihomo 控制器暂未就绪：{exception.Message}");
                }
                StartPolling();
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

        if (_settings.StartCoreAutomatically && configurations.Any(configuration => configuration.IsActive))
        {
            _ = StartCoreAsync(cancellationToken);
        }
    }

    public async Task StartCoreAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
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
            await _runtimeConfigBuilder.BuildAsync(profile.Path, runtimeConfigPath, _settings, cancellationToken);

            var runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
            var servicePayload = JsonSerializer.Serialize(new ServiceCorePayload(
                executable,
                runtimeConfigPath,
                runtimeDirectory,
                _settings.ControllerPort,
                _secretStore.GetOrCreate()));
            UpdateCoreState(CoreState.Starting, null);
            ServiceResponse? serviceResponse = null;
            try
            {
                serviceResponse = await _servicePipeClient.SendAsync(ServiceCommand.StartCore, servicePayload, cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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

            _api = CreateApiClient();
            try
            {
                await RefreshFromApiWithRetryAsync(cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                UpdateCoreState(CoreState.Failed, $"Mihomo 控制器暂未就绪：{exception.Message}");
            }
            StartPolling();
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
                        throw new InvalidOperationException(response.Error ?? "ClashTray 服务无法停止 Mihomo。");
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

    public async Task RefreshSubscriptionAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.SubscriptionUri is null)
        {
            return;
        }

        var shouldRemainActive = profile.IsActive
            || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
        UpdateSubscriptionState(SubscriptionState.Downloading, null);
        try
        {
            UpdateSubscriptionState(SubscriptionState.Validating, null);
            await _configurationStore.ImportSubscriptionAsync(profile.SubscriptionUri, profile.Name, cancellationToken);
            UpdateSubscriptionState(SubscriptionState.Applying, null);
            UpdateSubscriptionState(SubscriptionState.Succeeded, null);
            var configurations = await _configurationStore.ListAsync(cancellationToken);
            if (shouldRemainActive)
            {
                await SetActiveConfigurationAsync(profile.Id, cancellationToken);
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
                await RestartCoreAsync(cancellationToken);
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

    public async Task SetActiveConfigurationAsync(string id, CancellationToken cancellationToken = default)
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

        if (changed && _snapshot.Core.State == CoreState.Running)
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

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        if (settings.StartWithWindows != _settings.StartWithWindows)
        {
            StartupManager.SetEnabled(settings.StartWithWindows, Environment.ProcessPath ?? AppContext.BaseDirectory);
        }

        _settings = settings;
        await _settingsStore.SaveAsync(settings, cancellationToken);
        Publish();
    }

    public async Task SetSystemProxyAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
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
            _snapshot = _snapshot with { SystemProxy = _systemProxy.State };
            Publish();
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SetTunAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _snapshot = _snapshot with { Tun = enabled ? TunState.Enabling : TunState.Disabling };
            Publish();
            var payload = JsonSerializer.Serialize(new ServiceTunPayload(
                _settings.ControllerPort,
                _secretStore.GetOrCreate(),
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
            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (IOException exception)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = exception.Message };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch
        {
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

        await RefreshFromApiAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_snapshot.Tun == TunState.On)
        {
            try
            {
                await SetTunAsync(false, CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_snapshot.SystemProxy is SystemProxyState.On or SystemProxyState.RestoreRequired)
        {
            try
            {
                await SetSystemProxyAsync(false, CancellationToken.None);
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

        _api = null;
        await StopLogStreamAsync();
        _runtimeCts.Cancel();
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
        _operationLock.Dispose();
        _runtimeCts.Dispose();
    }

    private async Task RefreshFromApiAsync(CancellationToken cancellationToken)
    {
        if (_api is null)
        {
            return;
        }

        using var version = await _api.GetVersionAsync(cancellationToken);
        var versionText = MihomoDataParser.ParseVersion(version);
        using var configurationState = await _api.GetConfigurationAsync(force: false, cancellationToken);
        var mode = MihomoDataParser.ParseMode(configurationState);
        var tunEnabled = MihomoDataParser.ParseTunEnabled(configurationState);
        using var proxies = await _api.GetProxiesAsync(cancellationToken);
        var proxyData = MihomoDataParser.ParseProxies(proxies);
        using var traffic = await _api.GetTrafficAsync(cancellationToken);
        var trafficData = MihomoDataParser.ParseTraffic(traffic);
        var memoryBytes = await TryGetMemoryAsync(cancellationToken);
        using var connections = await _api.GetConnectionsAsync(cancellationToken);
        var connectionData = MihomoDataParser.ParseConnections(connections);
        var rulesData = await TryGetRulesAsync(cancellationToken);
        var providerData = await TryGetProvidersAsync(cancellationToken);
        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with
            {
                State = CoreState.Running,
                Version = versionText ?? _snapshot.Core.Version,
                Mode = mode ?? _snapshot.Core.Mode,
                UploadBytes = trafficData.UploadBytes,
                DownloadBytes = trafficData.DownloadBytes,
                UploadBytesPerSecond = trafficData.UploadBytesPerSecond,
                DownloadBytesPerSecond = trafficData.DownloadBytesPerSecond,
                ConnectionCount = connectionData.Count,
                MemoryBytes = memoryBytes,
                ErrorMessage = null
            },
            ProxyGroups = proxyData.Groups,
            ProxyNodes = proxyData.Nodes,
            Connections = connectionData,
            Rules = rulesData,
            Providers = providerData.Providers,
            RuleProviders = providerData.RuleProviders,
            Logs = _logBuffer.Snapshot(),
            Tun = tunEnabled is null
                ? _snapshot.Tun
                : tunEnabled.Value ? TunState.On : TunState.Off,
            ErrorMessage = null
        };
        Publish();
        EnsureLogStreamStarted();
    }

    private async Task RefreshFromApiWithRetryAsync(CancellationToken cancellationToken)
    {
        HttpRequestException? lastException = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await RefreshFromApiAsync(cancellationToken);
                return;
            }
            catch (HttpRequestException exception) when (attempt < 4)
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
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

        _pollingTask = Task.Run(async () =>
        {
            var retryDelay = TimeSpan.FromSeconds(2);
            while (!_runtimeCts.IsCancellationRequested && (_usingServiceCore ? _api is not null : _processManager.State == CoreState.Running))
            {
                try
                {
                    await Task.Delay(retryDelay, _runtimeCts.Token);
                    await RefreshFromApiAsync(_runtimeCts.Token);
                    retryDelay = TimeSpan.FromSeconds(2);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    UpdateCoreState(CoreState.Failed, exception.Message);
                    if (!_usingServiceCore || _processManager.State != CoreState.Running)
                    {
                        await StopLogStreamAsync();
                        break;
                    }

                    ServiceResponse? serviceStatus = null;
                    try
                    {
                        serviceStatus = await _servicePipeClient.SendAsync(ServiceCommand.GetStatus, cancellationToken: _runtimeCts.Token);
                    }
                    catch (TimeoutException)
                    {
                    }
                    catch (IOException)
                    {
                        _api = null;
                        await StopLogStreamAsync();
                        _snapshot = _snapshot with { Tun = TunState.Unavailable };
                        UpdateCoreState(CoreState.Failed, "ClashTray 服务暂时不可用");
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _api = null;
                        await StopLogStreamAsync();
                        _snapshot = _snapshot with { Tun = TunState.Unavailable };
                        UpdateCoreState(CoreState.Failed, "ClashTray 服务暂时不可用");
                        break;
                    }

                    if (serviceStatus is null)
                    {
                        retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
                        continue;
                    }

                    if (serviceStatus.Core == CoreState.Running)
                    {
                        _api = CreateApiClient();
                    }
                    else
                    {
                        _api = null;
                        await StopLogStreamAsync();
                        _snapshot = _snapshot with { Tun = serviceStatus.Tun };
                        UpdateCoreState(serviceStatus.Core, serviceStatus.Core == CoreState.Failed ? "Mihomo 服务进程已停止" : null);
                        break;
                    }

                    retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
                }
            }
        }, _runtimeCts.Token);
    }

    private MihomoApiClient CreateApiClient()
    {
        var controllerUri = new Uri($"http://127.0.0.1:{_settings.ControllerPort}/");
        return new MihomoApiClient(_httpClient, controllerUri, _secretStore.GetOrCreate());
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

    private async Task<IReadOnlyList<RuleInfo>> TryGetRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await _api!.GetRulesAsync(cancellationToken);
            return MihomoDataParser.ParseRules(document);
        }
        catch (HttpRequestException)
        {
            return _snapshot.Rules;
        }
    }

    private async Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> TryGetProvidersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var providers = await _api!.GetProvidersAsync(cancellationToken);
            using var ruleProviders = await _api.GetRuleProvidersAsync(cancellationToken);
            return (MihomoDataParser.ParseProviders(providers, "proxy"), MihomoDataParser.ParseProviders(ruleProviders, "rule"));
        }
        catch (HttpRequestException)
        {
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

    private async Task<long> TryGetMemoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var memory = await _api!.GetMemoryAsync(cancellationToken);
            return MihomoDataParser.ParseMemoryBytes(memory);
        }
        catch (HttpRequestException)
        {
            return _snapshot.Core.MemoryBytes;
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
    {
        var ports = new[] { settings.HttpPort, settings.SocksPort, settings.MixedPort, settings.ControllerPort };
        if (ports.Any(port => port is < 1 or > 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "端口必须在 1 到 65535 之间。");
        }

        if (ports.Distinct().Count() != ports.Length)
        {
            throw new ArgumentException("HTTP、SOCKS、Mixed 和控制器端口不能重复。", nameof(settings));
        }

        if (settings.SubscriptionRefreshHours is < 1 or > 168)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "订阅刷新间隔必须在 1 到 168 小时之间。");
        }
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

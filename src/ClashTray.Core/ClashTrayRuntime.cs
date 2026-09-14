using System.Diagnostics;
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
    private readonly SnapshotPublishThrottle _throttledPublisher;
    private readonly AppPaths _paths;
    private readonly ConfigurationStore _configurationStore;
    private readonly EndpointStore _endpointStore;
    private readonly EndpointSecretStore _endpointSecretStore;
    private readonly EndpointCertificateStore _endpointCertificateStore;
    private readonly EndpointTransportOptionsResolver _endpointTransportOptionsResolver;
    private readonly EndpointSessionManager _endpointSessions;
    private readonly EndpointRemovalCoordinator _endpointRemovalCoordinator;
    private readonly EndpointProvisioningCoordinator _endpointProvisioningCoordinator;
    private readonly NetworkRuleStore _networkRuleStore;
    private readonly ConfigurationSwitchJournalStore _configurationSwitchJournalStore;
    private readonly ConfigurationSwitchCoordinator _configurationSwitchCoordinator;
    private readonly NetworkSwitchRuntimeController _networkSwitchRuntimeController;
    private readonly RuntimeConfigurationSwitchOperations _configurationSwitchOperations;
    private readonly MihomoControllerSessionRegistry _controllerSessions = new();
    private readonly ISettingsStore _settingsStore;
    private readonly CoreDiscovery _coreDiscovery;
    private readonly LocalDeviceCoordinator _localDevice;
    private readonly SubscriptionScheduler _subscriptionScheduler;
    private readonly MihomoProcessManager _processManager = new();
    private readonly IStartupRegistration _startupRegistration;
    private readonly object _logStreamGate = new();
    private MihomoApiClient? _api => _controllerSessions.Current?.Api;
    private long ControllerGeneration => _controllerSessions.Generation;
    private Task? _pollingTask;
    private Task? _dataRefreshTask;
    private CancellationTokenSource? _logStreamCts;
    private Task? _logStreamTask;
    private AppSettings _settings = new();
    private RuntimeSnapshot _snapshot = CreateInitialSnapshot();
    private IReadOnlyList<EndpointDescriptor> _remoteEndpointDescriptors = [];
    private EndpointStoreLoadStatus _endpointStoreStatus = EndpointStoreLoadStatus.FirstRun;
    private string? _endpointStoreMessage;
    private readonly object _remoteRefreshGate = new();
    private readonly SemaphoreSlim _remoteRefreshLifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _remoteRefreshReadLock = new(1, 1);
    private CancellationTokenSource? _remoteRefreshCts;
    private Task? _remoteRefreshTask;
    private RemoteControllerData? _remoteControllerData;
    private RemoteRefreshCompletion? _remoteRefreshCompletion;
    private readonly Func<TimeSpan, CancellationToken, Task> _remoteRefreshDelayAsync;
    private bool _usingServiceCore;
    private bool _coreHealthConfirmed;
    private readonly object _proxyRecoveryGate = new();
    private Task? _proxyRecoveryTask;

    private const int MaxLogMessageBytes = 1024 * 1024;
    private static readonly TimeSpan RemoteRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RemoteRefreshRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteRefreshMaxRetryDelay = TimeSpan.FromSeconds(30);

    private sealed record ProxyDataResult(
        bool Succeeded,
        IReadOnlyList<ProxyGroup> Groups,
        IReadOnlyList<ProxyNode> Nodes);

    private sealed record RemoteControllerData(
        long Generation,
        long SelectionRevision,
        MihomoControllerSnapshotData Snapshot);

    private sealed record RemoteRefreshCompletion(
        long Generation,
        long SelectionRevision,
        TaskCompletionSource<bool> Completion);

    private readonly record struct TrafficDataResult(bool Succeeded, TrafficSnapshot? Value);

    private readonly record struct MemoryDataResult(bool Succeeded, long Value);

    private readonly record struct ConnectionDataResult(
        bool Succeeded,
        IReadOnlyList<ConnectionInfo> Value);

    public ClashTrayRuntime(AppPaths? paths = null)
        : this(paths, null, null, null)
    {
    }

    public ClashTrayRuntime(AppPaths? paths, INetworkContextSource? networkContextSource)
        : this(paths, null, null, null, null, null, networkContextSource, null)
    {
    }

    internal ClashTrayRuntime(
        AppPaths? paths,
        IStartupRegistration? startupRegistration,
        IServicePipeClient? servicePipeClient,
        ISettingsStore? settingsStore,
        ISystemProxyController? systemProxy = null,
        IConfigurationCandidateValidator? candidateValidator = null,
        INetworkContextSource? networkContextSource = null,
        IEndpointSessionConnector? endpointSessionConnector = null,
        Func<TimeSpan, CancellationToken, Task>? remoteRefreshDelayAsync = null)
    {
        bool useDefaultEnvironment = paths is null;
        _paths = paths ?? new AppPaths();
        _paths.EnsureDirectories();
        _endpointStore = new EndpointStore(_paths);
        _endpointSecretStore = new EndpointSecretStore(_paths);
        _endpointCertificateStore = new EndpointCertificateStore(_paths);
        _endpointTransportOptionsResolver = new EndpointTransportOptionsResolver(
            _endpointSecretStore,
            _endpointCertificateStore);
        IEndpointSessionConnector resolvedEndpointSessionConnector = endpointSessionConnector
            ?? new MihomoEndpointSessionConnector(
                ResolveEndpointRecordAsync,
                _endpointTransportOptionsResolver);
        _endpointSessions = new EndpointSessionManager(
            ControllerEndpointFactory.CreateLocal(_settings.ControllerPort),
            resolvedEndpointSessionConnector);
        _endpointSessions.StatusChanged += OnEndpointSessionStatusChanged;
        _endpointRemovalCoordinator = new EndpointRemovalCoordinator(
            _endpointStore,
            _endpointSecretStore,
            _endpointCertificateStore,
            _endpointSessions);
        _endpointProvisioningCoordinator = new EndpointProvisioningCoordinator(
            _endpointStore,
            _endpointSecretStore,
            _endpointCertificateStore);
        _coreDiscovery = new CoreDiscovery(_paths);
        _configurationStore = new ConfigurationStore(
            _paths,
            candidateValidator: candidateValidator ?? new MihomoConfigurationCandidateValidator(
                _paths,
                () => _settings,
                () => _coreDiscovery.FindExecutable()));
        _networkRuleStore = new NetworkRuleStore(_paths);
        _configurationSwitchJournalStore = new ConfigurationSwitchJournalStore(_paths);
        _configurationSwitchCoordinator = new ConfigurationSwitchCoordinator(
            _configurationSwitchJournalStore);
        _configurationSwitchOperations = new RuntimeConfigurationSwitchOperations(this);
        _networkSwitchRuntimeController = new NetworkSwitchRuntimeController(
            _networkRuleStore,
            networkContextSource,
            CreateNetworkSwitchPolicyInput,
            (request, cancellation) => ExecuteConfigurationSwitchAsync(request, cancellation));
        _networkSwitchRuntimeController.StatusChanged += OnNetworkSwitchStatusChanged;
        _settingsStore = settingsStore ?? new SettingsStore(_paths);
        _startupRegistration = startupRegistration
            ?? (useDefaultEnvironment ? new StartupManager() : new StartupManager(new InMemoryStartupRegistry()));
        IServicePipeClient resolvedServicePipeClient = servicePipeClient
            ?? (useDefaultEnvironment ? new ServicePipeClient() : new IsolatedServicePipeClient());
        ISystemProxyController resolvedSystemProxy = systemProxy ?? new SystemProxyManager(_paths);
        _localDevice = new LocalDeviceCoordinator(
            EndpointKind.Local,
            resolvedServicePipeClient,
            resolvedSystemProxy);
        _subscriptionScheduler = new SubscriptionScheduler(
            cancellation => _configurationStore.ListAsync(cancellation),
            (profile, cancellation) => RefreshSubscriptionAsync(profile, cancellation),
            () => _settings,
            OnScheduledSubscriptionRefreshFailed,
            OnScheduledSubscriptionCycleFailed);
        _remoteRefreshDelayAsync = remoteRefreshDelayAsync ?? Task.Delay;
        _throttledPublisher = new SnapshotPublishThrottle(Publish, _runtimeCts.Token);
        _processManager.StateChanged += OnProcessStateChanged;
        _processManager.LogLineReceived += OnProcessLogLine;
    }

    public RuntimeSnapshot Snapshot => _snapshot;

    public AppSnapshot AppSnapshot => AppSnapshotComposer.Compose(
        _snapshot,
        _settings,
        Endpoints,
        activeController: BuildActiveControllerSnapshot(),
        controllerGeneration: ControllerGeneration);

    public AppSettings Settings => _settings;

    public IReadOnlyList<EndpointDescriptor> Endpoints
    {
        get
        {
            List<EndpointDescriptor> endpoints =
            [ControllerEndpointFactory.CreateLocal(_settings.ControllerPort)];
            endpoints.AddRange(_remoteEndpointDescriptors);
            return endpoints;
        }
    }

    public EndpointStoreLoadStatus EndpointStoreStatus => _endpointStoreStatus;

    public string? EndpointStoreMessage => _endpointStoreMessage;

    public EndpointSessionStatusEventArgs EndpointSessionStatus => _endpointSessions.Status;

    public NetworkSwitchRuleSet NetworkSwitchRules => _networkSwitchRuntimeController.Rules;

    public NetworkSwitchStatus NetworkSwitchStatus => _networkSwitchRuntimeController.Status;

    public bool DashboardAvailable => File.Exists(_paths.ExternalUiEntryPoint);

    public StartupRegistrationStatus GetStartupStatus() => _startupRegistration.GetStatus();

    internal static bool ShouldAutomaticallyStartCore(
        AppSettings settings,
        bool hasActiveConfiguration,
        CoreState currentState) =>
        settings.StartCoreAutomatically
        && hasActiveConfiguration
        && currentState is CoreState.Missing or CoreState.Stopped or CoreState.Failed;

    public event EventHandler<RuntimeSnapshot>? SnapshotChanged;

    public event EventHandler<AppSnapshot>? AppSnapshotChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SettingsLoadResult settingsLoad = await _settingsStore.LoadWithStatusAsync(cancellationToken);
        _settings = settingsLoad.Settings;
        await LoadEndpointCatalogAsync(cancellationToken);
        ConfigurationSwitchJournalLoadResult journalLoad =
            await _configurationSwitchJournalStore.LoadAsync(cancellationToken);
        ConfigurationSwitchJournal? recoveryJournal = journalLoad.Journal;
        string? journalRecoveryMessage = journalLoad.Message;
        bool journalContentRestored = true;
        if (recoveryJournal is { Stage: ConfigurationSwitchStage.Committed })
        {
            try
            {
                await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                recoveryJournal = null;
            }
            catch (Exception exception)
            {
                journalRecoveryMessage = $"配置切换已完成，但无法清理恢复记录：{ErrorSanitizer.Sanitize(exception)}";
                recoveryJournal = null;
            }
        }
        else if (recoveryJournal is not null)
        {
            if (recoveryJournal.ContentBackupId is Guid backupId)
            {
                try
                {
                    journalContentRestored = await _configurationStore.RestorePersistentBackupAsync(
                        backupId,
                        recoveryJournal.CandidateConfigurationId,
                        cancellationToken);
                    if (!journalContentRestored)
                    {
                        journalRecoveryMessage = "配置切换恢复记录缺少旧配置备份，无法恢复订阅内容。";
                    }
                }
                catch (Exception exception)
                {
                    journalContentRestored = false;
                    journalRecoveryMessage = $"订阅配置备份恢复失败：{ErrorSanitizer.Sanitize(exception)}";
                }
            }

            IReadOnlyList<ConfigurationProfile> configurationsBeforeRestore =
                await _configurationStore.ListAsync(cancellationToken);
            string? restoreMessage = await RestoreConfigurationFromJournalAsync(
                recoveryJournal,
                configurationsBeforeRestore,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(restoreMessage))
            {
                journalRecoveryMessage = restoreMessage;
            }
        }

        IReadOnlyList<ConfigurationProfile> storedConfigurations =
            await _configurationStore.ListAsync(cancellationToken);

        string? activeConfigurationId = _settings.ActiveConfigurationId
            ?? storedConfigurations.FirstOrDefault(configuration => configuration.IsActive)?.Id;
        ConfigurationProfile[] configurations = storedConfigurations
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
            SystemProxy = _localDevice.DetectSystemProxyState()
        };
        await ApplyProgramOverridesAsync(coreRunning: false, cancellationToken: cancellationToken);
        try
        {
            ServiceResponse serviceStatus = await _localDevice.GetStatusAsync(cancellationToken);
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
                SetController(CreateApiClient());
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

                await ApplyProgramOverridesAsync(
                    coreRunning: _coreHealthConfirmed,
                    cancellationToken: cancellationToken);
                StartPolling();
                StartOptionalRefreshInBackground(_api);

                if (recoveryJournal is not null)
                {
                    (bool completed, string? message) = await CompleteConfigurationSwitchRecoveryAsync(
                        recoveryJournal,
                        serviceStatus.Core,
                        journalContentRestored,
                        cancellationToken);
                    if (!completed)
                    {
                        journalRecoveryMessage = message;
                    }
                    else
                    {
                        recoveryJournal = null;
                    }
                }
            }
            else if (recoveryJournal is not null)
            {
                (bool completed, string? message) = await CompleteConfigurationSwitchRecoveryAsync(
                    recoveryJournal,
                    serviceStatus.Core,
                    journalContentRestored,
                    cancellationToken);
                if (!completed)
                {
                    journalRecoveryMessage = message;
                }
                else
                {
                    recoveryJournal = null;
                }
            }
        }
        catch (TimeoutException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        catch (IOException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            _snapshot = _snapshot with { Tun = TunState.Unavailable };
            if (recoveryJournal is not null)
            {
                if (!recoveryJournal.PreviousCoreWasRunning && journalContentRestored)
                {
                    await ClearRecoveredConfigurationSwitchArtifactsAsync(recoveryJournal);
                    recoveryJournal = null;
                }
                else
                {
                    journalRecoveryMessage = "配置切换恢复记录仍未完成，服务状态暂时无法确认。";
                }
            }
        }
        Publish();
        _subscriptionScheduler.Start();

        if (recoveryJournal is null
            && ShouldAutomaticallyStartCore(
                _settings,
                configurations.Any(configuration => configuration.IsActive),
                _snapshot.Core.State))
        {
            await StartCoreAsync(cancellationToken);
        }

        await _networkSwitchRuntimeController.InitializeAsync(cancellationToken);

        if (settingsLoad.Status is SettingsLoadStatus.Recovered
            or SettingsLoadStatus.ReadFailed
            or SettingsLoadStatus.RecoveryFailed)
        {
            _snapshot = _snapshot with { ErrorMessage = settingsLoad.Message };
            Publish();
        }

        if (!string.IsNullOrWhiteSpace(journalRecoveryMessage))
        {
            _snapshot = _snapshot with { ErrorMessage = journalRecoveryMessage };
            Publish();
        }
    }

    public async Task<EndpointCatalogLoadResult> LoadEndpointCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        EndpointCatalog catalog = new(
            _endpointStore,
            ControllerEndpointFactory.CreateLocal(_settings.ControllerPort));
        EndpointCatalogLoadResult result = await catalog.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _remoteEndpointDescriptors = result.Endpoints
            .Where(endpoint => endpoint.Id != EndpointId.Local)
            .ToArray();
        _endpointStoreStatus = result.RemoteStoreStatus;
        _endpointStoreMessage = result.Message;
        await ReconcileEndpointSessionAsync(result.Endpoints).ConfigureAwait(false);
        Publish();
        return result;
    }

    public async Task<EndpointSession?> SelectEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (endpointId == EndpointId.Local)
            {
                await _endpointSessions.DisconnectAsync().ConfigureAwait(false);
                return null;
            }

            EndpointDescriptor? endpoint = Endpoints.FirstOrDefault(candidate => candidate.Id == endpointId);
            if (endpoint is null)
            {
                throw new KeyNotFoundException($"未找到端点 {endpointId.Value}。");
            }

            if (!endpoint.IsEnabled)
            {
                throw new InvalidOperationException($"端点 {endpoint.DisplayName} 已被禁用。");
            }

            EndpointSession? session = await _endpointSessions.SelectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (session is not null)
            {
                await WaitForRemoteRefreshAsync(session, cancellationToken).ConfigureAwait(false);
            }

            return session;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DisconnectEndpointAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _endpointSessions.DisconnectAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<EndpointCatalogLoadResult> SaveRemoteEndpointAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _endpointStore.UpsertAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<EndpointCatalogLoadResult> ProvisionRemoteEndpointAsync(
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _endpointProvisioningCoordinator.ProvisionAsync(
                    descriptor,
                    secret,
                    customCaCertificate,
                    insecureHttpAcknowledgedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<EndpointCatalogLoadResult> UpdateRemoteEndpointAsync(
        EndpointId endpointId,
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _endpointProvisioningCoordinator.UpdateAsync(
                    endpointId,
                    descriptor,
                    secret,
                    customCaCertificate,
                    insecureHttpAcknowledgedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<EndpointRemovalResult> RemoveRemoteEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EndpointRemovalResult result = await _endpointRemovalCoordinator.RemoveAsync(
                    endpointId,
                    cancellationToken)
                .ConfigureAwait(false);
            await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task StartCoreAsync(CancellationToken cancellationToken = default) =>
        StartCoreCoreAsync(operationLockHeld: false, cancellationToken);

    private async Task StartCoreCoreAsync(
        bool operationLockHeld,
        CancellationToken cancellationToken)
    {
        if (!operationLockHeld)
        {
            await _operationLock.WaitAsync(cancellationToken);
        }

        try
        {
            if (_snapshot.Core.State == CoreState.Running)
            {
                return;
            }

            ConfigurationProfile? profile = GetActiveConfiguration();
            string? executable = _coreDiscovery.FindExecutable();
            if (executable is null)
            {
                UpdateCoreState(CoreState.Missing, "未找到 Mihomo 核心，请在设置中安装或选择 mihomo.exe");
                await RevokeSystemProxyForCoreLossAsync(operationLockHeld: true);
                return;
            }

            if (profile is null)
            {
                UpdateCoreState(CoreState.Failed, "请先导入一个 Mihomo 配置");
                await RevokeSystemProxyForCoreLossAsync(operationLockHeld: true);
                return;
            }

            UpdateCoreState(CoreState.Validating, null);
            string runtimeConfigPath = Path.Combine(_paths.RuntimeRoot, "mihomo", "active-config.yaml");
            await RuntimeConfigBuilder.BuildAsync(
                profile.Path,
                runtimeConfigPath,
                _settings,
                externalUiPath: _paths.ExternalUiRoot,
                cancellationToken: cancellationToken);

            string runtimeDirectory = Path.Combine(_paths.RuntimeRoot, "mihomo");
            ServiceCorePayload servicePayload = new(
                runtimeConfigPath,
                runtimeDirectory,
                _settings.ControllerPort,
                string.Empty);
            UpdateCoreState(CoreState.Starting, null);
            ServiceResponse? serviceResponse = null;
            try
            {
                serviceResponse = await _localDevice.StartCoreAsync(servicePayload, cancellationToken);
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
                UpdateCoreState(CoreState.Failed, $"ClashTray 服务启动结果无法确认，请检查服务状态后重试：{ErrorSanitizer.Sanitize(exception)}");
                return;
            }
            catch (IOException exception)
            {
                UpdateCoreState(CoreState.Failed, $"ClashTray 服务通信失败，启动结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
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
                if (!await _processManager.ValidateAsync(
                    executable,
                    runtimeConfigPath,
                    runtimeDirectory,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken))
                {
                    UpdateCoreState(CoreState.Failed, "Mihomo 配置验证失败");
                    return;
                }

                await _processManager.StartAsync(
                    executable,
                    runtimeConfigPath,
                    runtimeDirectory,
                    safePaths: _paths.ExternalUiRoot,
                    cancellationToken: cancellationToken);
            }

            bool coreStarted = true;
            SetController(CreateApiClient());
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
                coreRunning: _coreHealthConfirmed,
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
            await RevokeSystemProxyForCoreLossAsync(operationLockHeld: true);
            UpdateCoreState(CoreState.Failed, ErrorSanitizer.Sanitize(exception));
        }
        finally
        {
            if (!operationLockHeld)
            {
                _operationLock.Release();
            }
        }
    }

    public Task StopCoreAsync(CancellationToken cancellationToken = default) =>
        StopCoreCoreAsync(operationLockHeld: false, cancellationToken);

    private async Task StopCoreCoreAsync(
        bool operationLockHeld,
        CancellationToken cancellationToken)
    {
        if (!operationLockHeld)
        {
            await _operationLock.WaitAsync(cancellationToken);
        }

        try
        {
            UpdateCoreState(CoreState.Stopping, null);
            _coreHealthConfirmed = false;
            SetController(null);
            await StopLogStreamAsync();
            if (_usingServiceCore)
            {
                try
                {
                    ServiceResponse response = await _localDevice.StopCoreAsync(cancellationToken);
                    if (!response.Succeeded)
                    {
                        _snapshot = _snapshot with { Tun = response.Tun };
                        if (response.Core == CoreState.Stopped)
                        {
                            _usingServiceCore = false;
                        }

                        if (response.Core == CoreState.Running)
                        {
                            SetController(CreateApiClient());
                        }

                        UpdateCoreState(response.Core, response.Error ?? "ClashTray 服务无法停止 Mihomo。");
                        throw new InvalidOperationException(
                            response.Error ?? "ClashTray 服务无法停止 Mihomo。");
                    }
                }
                catch (TimeoutException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (ServiceUnavailableException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务访问被拒绝，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务访问被拒绝，停止结果无法确认。",
                        exception);
                }
                catch (ServiceRequestUnknownException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务停止结果无法确认，请检查服务状态后重试：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务停止结果无法确认，请检查服务状态后重试。",
                        exception);
                }
                catch (IOException exception)
                {
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务通信失败，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务通信失败，停止结果无法确认。",
                        exception);
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
            await RevokeSystemProxyForCoreLossAsync(operationLockHeld: true);
            if (!operationLockHeld)
            {
                _operationLock.Release();
            }
        }
    }

    public async Task RestartCoreAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await RestartCoreCoreAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RestartCoreCoreAsync(CancellationToken cancellationToken)
    {
        UpdateCoreState(CoreState.Restarting, null);
        await StopCoreCoreAsync(operationLockHeld: true, cancellationToken);
        await StartCoreCoreAsync(operationLockHeld: true, cancellationToken);
    }

    public async Task<ConfigurationProfile> ImportLocalConfigurationAsync(string path, string? name = null, CancellationToken cancellationToken = default)
    {
        ConfigurationProfile profile = await _configurationStore.ImportLocalAsync(path, name, cancellationToken);
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
                ConfigurationProfile profile = await _configurationStore.ImportSubscriptionAsync(uri, name, cancellationToken);
                UpdateSubscriptionState(SubscriptionState.Succeeded, null);
                await SetActiveConfigurationAsync(profile.Id, cancellationToken);
                return profile;
            }
            catch (Exception exception)
            {
                UpdateSubscriptionState(SubscriptionState.Failed, ErrorSanitizer.Sanitize(exception));
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
        ArgumentNullException.ThrowIfNull(profile);
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshSubscriptionCoreLockedAsync(profile, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task RefreshSubscriptionCoreLockedAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.SubscriptionUri is null)
        {
            return;
        }

        bool shouldRemainActive = profile.IsActive
            || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
        bool activeSelectionChanged = !string.Equals(
            _settings.ActiveConfigurationId,
            profile.Id,
            StringComparison.OrdinalIgnoreCase);
        ConfigurationProfileBackup? contentBackup = null;
        ConfigurationSwitchRuntimeState? previousState = null;
        Guid operationId = Guid.NewGuid();
        Guid? persistentBackupId = null;
        bool contentChanged = false;
        bool switchCommitted = false;
        UpdateSubscriptionState(SubscriptionState.Downloading, null);
        try
        {
            if (shouldRemainActive)
            {
                previousState = await _configurationSwitchOperations.CaptureStateAsync(cancellationToken);
                contentBackup = await _configurationStore.CaptureBackupAsync(profile, cancellationToken);
                persistentBackupId = Guid.NewGuid();
                await _configurationStore.SavePersistentBackupAsync(
                    persistentBackupId.Value,
                    profile,
                    contentBackup,
                    cancellationToken);
                ConfigurationSwitchJournal preparedJournal = ConfigurationSwitchJournal.Create(
                    operationId,
                    ConfigurationSwitchSource.SubscriptionRefresh,
                    previousState.ActiveConfigurationId,
                    profile.Id,
                    previousState.CoreWasRunning,
                    previousState.SystemProxyPreference,
                    previousState.SystemProxyState,
                    previousState.TunPreference,
                    previousState.TunState,
                    previousState.ControllerGeneration)
                    .WithContentBackup(persistentBackupId);
                await _configurationSwitchJournalStore.SaveAsync(preparedJournal, cancellationToken);
            }

            UpdateSubscriptionState(SubscriptionState.Validating, null);
            ConfigurationImportResult update = await _configurationStore.ImportSubscriptionWithResultAsync(
                profile.SubscriptionUri,
                profile.Name,
                cancellationToken);
            contentChanged = update.ContentChanged;
            UpdateSubscriptionState(SubscriptionState.Applying, null);
            UpdateSubscriptionState(SubscriptionState.Succeeded, null);
            if (shouldRemainActive)
            {
                ConfigurationSwitchRequest request = new(
                    operationId,
                    ConfigurationSwitchSource.SubscriptionRefresh,
                    profile.Id,
                    RestartCore: update.ContentChanged || activeSelectionChanged,
                    ForceApply: update.ContentChanged);
                ConfigurationSwitchResult result = await ExecuteConfigurationSwitchAsync(
                    request,
                    cancellationToken,
                    operationLockHeld: true);
                switchCommitted = result.Outcome is
                    ConfigurationSwitchOutcome.NoOp
                    or ConfigurationSwitchOutcome.Committed;
                if (switchCommitted)
                {
                    Guid backupId = persistentBackupId
                        ?? throw new InvalidOperationException("订阅备份标识丢失。");
                    await ClearConfigurationSwitchArtifactsAsync(operationId, backupId);
                    persistentBackupId = null;
                }
                if (result.Outcome == ConfigurationSwitchOutcome.NoOp)
                {
                    await RefreshConfigurationSnapshotAsync(cancellationToken);
                }
            }
            else
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }

            if (shouldRemainActive && !update.ContentChanged && !activeSelectionChanged)
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
        catch (Exception exception)
        {
            Exception finalException = exception;
            if (persistentBackupId is Guid backupId && !switchCommitted)
            {
                try
                {
                    if (contentBackup is not null && contentChanged)
                    {
                        await ConfigurationStore.RestoreBackupAsync(contentBackup, CancellationToken.None);
                        await RefreshConfigurationSnapshotAsync(CancellationToken.None);
                    }

                    ConfigurationSwitchJournalLoadResult currentJournal =
                        await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
                    bool keepRecoveryJournal = currentJournal.Journal is { } journal
                        && journal.OperationId == operationId
                        && journal.Stage == ConfigurationSwitchStage.RollbackFailed;
                    if (!keepRecoveryJournal)
                    {
                        await ClearConfigurationSwitchArtifactsAsync(operationId, backupId);
                        persistentBackupId = null;
                    }
                }
                catch (Exception restoreException)
                {
                    Exception recoveryException = restoreException;
                    try
                    {
                        await EnsureRecoveryJournalAsync(
                            operationId,
                            profile,
                            previousState,
                            backupId);
                    }
                    catch (Exception journalException)
                    {
                        recoveryException = new AggregateException(
                            restoreException,
                            journalException);
                    }
                    finalException = new InvalidOperationException(
                        "订阅切换失败，且旧配置文件恢复失败。",
                        new AggregateException(exception, recoveryException));
                }
            }

            UpdateSubscriptionState(SubscriptionState.Failed, ErrorSanitizer.Sanitize(finalException));
            throw finalException;
        }
    }

    public async Task ReloadConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SubscriptionUri is not null)
        {
            await RefreshSubscriptionAsync(profile, cancellationToken);
            return;
        }

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await _configurationStore.ReloadAsync(profile, cancellationToken);
            bool isActive = profile.IsActive
                || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
            if (isActive)
            {
                await ExecuteConfigurationSwitchAsync(
                    ConfigurationSwitchRequest.Create(
                        ConfigurationSwitchSource.Manual,
                        profile.Id,
                        restartCore: _snapshot.Core.State == CoreState.Running,
                        forceApply: true),
                    cancellationToken,
                    operationLockHeld: true);
            }
            else
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SetActiveConfigurationAsync(string id, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            ConfigurationSwitchResult result = await ExecuteConfigurationSwitchAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, id),
                cancellationToken,
                operationLockHeld: true);
            if (result.Outcome == ConfigurationSwitchOutcome.NoOp)
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }

            if (_networkSwitchRuntimeController.IsInitialized)
            {
                _networkSwitchRuntimeController.SetManualOverrideForCurrentNetwork(id);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task UpdateNetworkSwitchRulesAsync(
        NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken = default) =>
        UpdateNetworkSwitchRulesCoreAsync(rules, cancellationToken);

    private async Task UpdateNetworkSwitchRulesCoreAsync(
        NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await _networkSwitchRuntimeController.SetRulesAsync(rules, cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ClearNetworkSwitchManualOverride()
    {
        if (_networkSwitchRuntimeController.IsInitialized)
        {
            _networkSwitchRuntimeController.ClearManualOverride();
        }
    }

    private async Task<ConfigurationSwitchResult> ExecuteConfigurationSwitchAsync(
        ConfigurationSwitchRequest request,
        CancellationToken cancellationToken,
        bool operationLockHeld = false)
    {
        if (!operationLockHeld)
        {
            await _operationLock.WaitAsync(cancellationToken);
        }

        try
        {
            ConfigurationSwitchResult result = await _configurationSwitchCoordinator.ExecuteAsync(
                request,
                _configurationSwitchOperations,
                cancellationToken);
            ThrowIfConfigurationSwitchFailed(result);
            return result;
        }
        finally
        {
            if (!operationLockHeld)
            {
                _operationLock.Release();
            }
        }
    }

    private static void ThrowIfConfigurationSwitchFailed(ConfigurationSwitchResult result)
    {
        switch (result.Outcome)
        {
            case ConfigurationSwitchOutcome.NoOp:
            case ConfigurationSwitchOutcome.Committed:
                return;
            case ConfigurationSwitchOutcome.Rejected when result.ErrorCode == ErrorCode.ConfigurationSwitchTargetNotFound:
                throw new FileNotFoundException("Configuration profile not found.");
            case ConfigurationSwitchOutcome.Rejected when result.ErrorCode == ErrorCode.ConfigurationSwitchRecoveryRequired:
                throw new InvalidOperationException("上一个配置切换尚未完成恢复，请先重启 ClashTray 后再试。");
            case ConfigurationSwitchOutcome.RolledBack when result.Failure is OperationCanceledException cancellation:
                throw cancellation;
            case ConfigurationSwitchOutcome.RolledBack:
                throw new InvalidOperationException("配置切换失败，已恢复旧配置。", result.Failure);
            case ConfigurationSwitchOutcome.RollbackFailed:
                List<Exception> failures = new List<Exception>();
                if (result.Failure is not null)
                {
                    failures.Add(result.Failure);
                }

                if (result.RollbackFailure is not null)
                {
                    failures.Add(result.RollbackFailure);
                }

                throw new InvalidOperationException(
                    "配置切换失败，且旧配置恢复失败。",
                    new AggregateException(failures));
            default:
                throw new InvalidOperationException($"配置切换失败：{result.ErrorCode}。");
        }
    }

    private async Task RefreshConfigurationSnapshotAsync(
        CancellationToken cancellationToken,
        bool publish = true)
    {
        IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
        _snapshot = _snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = _snapshot.Core with
            {
                ConfigurationName = configurations.FirstOrDefault(configuration =>
                    string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase))?.Name
            }
        };
        if (publish)
        {
            Publish();
        }
    }

    private async Task PromoteConfigurationInMemoryAsync(
        ConfigurationProfile candidate,
        CancellationToken cancellationToken)
    {
        _settings = _settings with { ActiveConfigurationId = candidate.Id };
        IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
        _snapshot = _snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = _snapshot.Core with { ConfigurationName = candidate.Name }
        };
    }

    private async Task CommitConfigurationSelectionAsync(
        ConfigurationProfile candidate,
        CancellationToken cancellationToken)
    {
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken);
    }

    private async Task RestoreConfigurationSelectionAsync(
        ConfigurationSwitchRuntimeState previousState,
        CancellationToken cancellationToken)
    {
        _settings = previousState.PreviousSettings
            ?? _settings with { ActiveConfigurationId = previousState.ActiveConfigurationId };
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        await RefreshConfigurationSnapshotAsync(cancellationToken, publish: false);
    }

    private async Task<string?> RestoreConfigurationFromJournalAsync(
        ConfigurationSwitchJournal journal,
        IReadOnlyList<ConfigurationProfile> configurations,
        CancellationToken cancellationToken)
    {
        string? previousConfigurationId = journal.PreviousConfigurationId;
        string? message = null;
        if (previousConfigurationId is not null
            && !configurations.Any(configuration =>
                string.Equals(configuration.Id, previousConfigurationId, StringComparison.OrdinalIgnoreCase)))
        {
            previousConfigurationId = null;
            message = "配置切换恢复记录指向的旧配置已不存在，已恢复为未选择配置。";
        }

        _settings = _settings with { ActiveConfigurationId = previousConfigurationId };
        await _settingsStore.SaveAsync(_settings, cancellationToken);
        return message;
    }

    private async Task ClearConfigurationSwitchArtifactsAsync(
        Guid operationId,
        Guid backupId)
    {
        ConfigurationSwitchJournalLoadResult currentJournal =
            await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
        if (currentJournal.Journal is null || currentJournal.Journal.OperationId == operationId)
        {
            await _configurationSwitchJournalStore.ClearAsync();
        }

        await _configurationStore.ClearPersistentBackupAsync(backupId);
    }

    private async Task ClearRecoveredConfigurationSwitchArtifactsAsync(
        ConfigurationSwitchJournal journal)
    {
        await _configurationSwitchJournalStore.ClearAsync();
        if (journal.ContentBackupId is Guid backupId)
        {
            await _configurationStore.ClearPersistentBackupAsync(backupId);
        }
    }

    private async Task EnsureRecoveryJournalAsync(
        Guid operationId,
        ConfigurationProfile profile,
        ConfigurationSwitchRuntimeState? previousState,
        Guid backupId)
    {
        if (previousState is null)
        {
            return;
        }

        ConfigurationSwitchJournalLoadResult currentJournal =
            await _configurationSwitchJournalStore.LoadAsync(CancellationToken.None);
        if (currentJournal.Journal is { } existingJournal
            && existingJournal.OperationId != operationId)
        {
            return;
        }

        ConfigurationSwitchJournal journal = currentJournal.Journal ?? ConfigurationSwitchJournal.Create(
            operationId,
            ConfigurationSwitchSource.SubscriptionRefresh,
            previousState.ActiveConfigurationId,
            profile.Id,
            previousState.CoreWasRunning,
            previousState.SystemProxyPreference,
            previousState.SystemProxyState,
            previousState.TunPreference,
            previousState.TunState,
            previousState.ControllerGeneration);
        await _configurationSwitchJournalStore.SaveAsync(
            journal.WithContentBackup(backupId).WithStage(ConfigurationSwitchStage.RollbackFailed),
            CancellationToken.None);
    }

    private async Task<bool> RecoverCoreFromJournalAsync(
        ConfigurationSwitchJournal journal,
        CoreState observedCoreState,
        CancellationToken cancellationToken)
    {
        if (!journal.PreviousCoreWasRunning)
        {
            return true;
        }

        if (observedCoreState == CoreState.Running)
        {
            await RestartCoreAsync(cancellationToken);
        }
        else
        {
            await StartCoreAsync(cancellationToken);
        }

        return IsCoreHealthy();
    }

    private async Task<(bool Completed, string? Message)> CompleteConfigurationSwitchRecoveryAsync(
        ConfigurationSwitchJournal journal,
        CoreState observedCoreState,
        bool contentRestored,
        CancellationToken cancellationToken)
    {
        if (!contentRestored)
        {
            if (observedCoreState == CoreState.Running)
            {
                await StopCoreAsync(cancellationToken);
            }

            return (false, "配置切换恢复记录仍未完成，旧订阅内容无法确认，核心已保持停止。");
        }

        if (journal.PreviousCoreWasRunning
            && !await RecoverCoreFromJournalAsync(journal, observedCoreState, cancellationToken))
        {
            return (false, "配置切换恢复记录仍未完成，旧核心未能通过健康检查。");
        }

        await ClearRecoveredConfigurationSwitchArtifactsAsync(journal);
        return (true, null);
    }

    public async Task DeleteConfigurationAsync(ConfigurationProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            bool wasActive = profile.IsActive
                || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
            if (wasActive && _snapshot.Core.State == CoreState.Running)
            {
                await StopCoreCoreAsync(operationLockHeld: true, cancellationToken);
            }

            await _configurationStore.DeleteAsync(profile, cancellationToken);
            IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
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
        finally
        {
            _operationLock.Release();
        }
    }

    public Task SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default) =>
        SetModeCoreAsync(mode, routeToRemote: true, cancellationToken);

    public Task SetLocalModeAsync(ProxyMode mode, CancellationToken cancellationToken = default) =>
        SetModeCoreAsync(mode, routeToRemote: false, cancellationToken);

    private async Task SetModeCoreAsync(
        ProxyMode mode,
        bool routeToRemote,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.SwitchMode,
                "模式切换期间核心会话已切换，请重试。",
                "远程端点模式切换结果无法确认，请重试。",
                (api, _, token) => api.SetModeAsync(mode, token),
                (session, token) => session.Api.SetModeAsync(mode, token),
                cancellationToken,
                routeToRemote);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            string? previousProxy = _snapshot.ProxyGroups
                .FirstOrDefault(item => string.Equals(item.Name, group, StringComparison.Ordinal))
                ?.Current;
            Exception? disconnectException = null;
            bool selectionChanged = previousProxy is not null
                && !string.Equals(previousProxy, proxy, StringComparison.Ordinal);

            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.SwitchProxy,
                "节点切换期间核心会话已切换，请重新选择节点。",
                "远程端点节点切换结果无法确认，请重试。",
                async (api, generation, token) =>
                {
                    await api.SelectProxyAsync(group, proxy, token);
                    EnsureControllerSession(
                        api,
                        generation,
                        "节点切换期间核心会话已切换，请重新选择节点。");
                    if (_settings.DisconnectConnectionsAfterProxySwitch && selectionChanged)
                    {
                        try
                        {
                            EnsureControllerCommand(
                                api,
                                generation,
                                EndpointCommand.CloseConnection,
                                "节点切换期间核心会话已切换，请重新选择节点。");
                            await api.CloseAllConnectionsAsync(token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            EnsureControllerSession(
                                api,
                                generation,
                                "节点切换期间核心会话已切换，请重新选择节点。");
                            disconnectException = exception;
                            _logBuffer.Add(new LogEntry(
                                DateTimeOffset.UtcNow,
                                "ClashTray",
                                "error",
                                $"节点已切换，但未能断开旧连接：{ErrorSanitizer.Sanitize(exception)}"));
                        }
                    }
                },
                (session, token) => session.Api.SelectProxyAsync(group, proxy, token),
                cancellationToken);

            if (disconnectException is not null)
            {
                const string message = "节点已切换，但未能断开旧连接。";
                _snapshot = _snapshot with
                {
                    ErrorMessage = message,
                    Logs = _logBuffer.Snapshot()
                };
                Publish();
                throw new InvalidOperationException(message, disconnectException);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<int?> TestProxyDelayAsync(string proxy, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            EndpointSession? remoteSession = CaptureActiveRemoteSession(
                EndpointCommand.TestDelay,
                "测速期间远程端点会话已切换，请重新测速。");
            if (remoteSession is not null)
            {
                EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
                using JsonDocument remoteResponse = await remoteSession.Api.TestDelayAsync(
                    proxy,
                    new Uri("https://www.gstatic.com/generate_204"),
                    5000,
                    cancellationToken);
                if (!IsCurrentRemoteSession(remoteSession, remoteStatus))
                {
                    throw new InvalidOperationException(
                        "测速期间远程端点会话已切换，请重新测速。");
                }

                int? remoteDelay = remoteResponse.RootElement.TryGetProperty(
                        "delay",
                        out JsonElement remoteDelayElement)
                    && remoteDelayElement.TryGetInt32(out int remoteMilliseconds)
                    ? remoteMilliseconds
                    : null;
                if (!await RefreshRemoteControllerSnapshotAsync(
                        remoteSession,
                        remoteStatus,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "远程节点测速结果无法确认，请重试。");
                }

                return remoteDelay;
            }

            (MihomoApiClient api, long generation) = CaptureControllerSession();
            EnsureControllerCommand(
                api,
                generation,
                EndpointCommand.TestDelay,
                "测速期间核心会话已切换，请重新测速。");

            using JsonDocument response = await api.TestDelayAsync(
                proxy,
                new Uri("https://www.gstatic.com/generate_204"),
                5000,
                cancellationToken);
            EnsureControllerSession(api, generation, "测速期间核心会话已切换，请重新测速。");
            if (response.RootElement.TryGetProperty("delay", out JsonElement delay)
                && delay.TryGetInt32(out int milliseconds))
            {
                return milliseconds;
            }

            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayAsync(
        string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runtimeCts.Token);
        CancellationToken token = linked.Token;
        await _operationLock.WaitAsync(token);
        try
        {
            EndpointSession? remoteSession = CaptureActiveRemoteSession(
                EndpointCommand.TestDelay,
                "测速期间远程端点会话已切换，请重新测速。");
            if (remoteSession is not null)
            {
                EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
                using JsonDocument remoteResponse = await remoteSession.Api.TestGroupDelayAsync(
                    group,
                    new Uri("https://www.gstatic.com/generate_204"),
                    5000,
                    token);
                IReadOnlyDictionary<string, int?> remoteDelays =
                    MihomoDataParser.ParseGroupDelays(remoteResponse);
                if (!IsCurrentRemoteSession(remoteSession, remoteStatus))
                {
                    throw new InvalidOperationException(
                        "测速期间远程端点会话已切换，请重新测速。");
                }

                if (!await RefreshRemoteControllerSnapshotAsync(
                        remoteSession,
                        remoteStatus,
                        token)
                    .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "远程代理组测速结果无法确认，请重试。");
                }

                return remoteDelays;
            }

            (MihomoApiClient api, long generation) = CaptureControllerSession();
            EnsureControllerCommand(
                api,
                generation,
                EndpointCommand.TestDelay,
                "测速期间核心会话已切换，请重新测速。");
            using JsonDocument response = await api.TestGroupDelayAsync(group, new Uri("https://www.gstatic.com/generate_204"), 5000, token);
            IReadOnlyDictionary<string, int?> delays = MihomoDataParser.ParseGroupDelays(response);
            await _dataRefreshLock.WaitAsync(token);
            try
            {
                // Refresh now/history from the core and apply the confirmed batch result atomically.
                ProxyDataResult proxies = await TryGetProxyDataAsync(api, token);
                EnsureControllerSession(api, generation, "测速期间核心会话已切换，请重新测速。");

                string? LatestDelay(string name, string? previous) => delays.TryGetValue(name, out int? delay)
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接关闭结果无法确认，请重试。",
                (api, _, token) => api.CloseConnectionAsync(id, token),
                (session, token) => session.Api.CloseConnectionAsync(id, token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接清理结果无法确认，请重试。",
                (api, _, token) => api.CloseAllConnectionsAsync(token),
                (session, token) => session.Api.CloseAllConnectionsAsync(token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RefreshProviderAsync(string name, bool rules, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.RefreshProvider,
                "刷新 Provider 期间核心会话已切换，请重试。",
                "远程 Provider 刷新结果无法确认，请重试。",
                (api, _, token) => rules
                    ? api.RefreshRuleProviderAsync(name, token)
                    : api.RefreshProviderAsync(name, token),
                (session, token) => rules
                    ? session.Api.RefreshRuleProviderAsync(name, token)
                    : session.Api.RefreshProviderAsync(name, token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ClearLogs()
    {
        _logBuffer.Clear();
        _snapshot = _snapshot with { Logs = [] };
        Publish();
    }

    public async Task ClearFakeIpCacheAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 FakeIP 缓存期间核心会话已切换，请重试。",
                "远程 FakeIP 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearFakeIpCacheAsync(token),
                (session, token) => session.Api.ClearFakeIpCacheAsync(token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings, bool reconcileStartup = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);
        await _operationLock.WaitAsync(cancellationToken);
        AppSettings previousSettings = _settings;
        bool networkSettingsChanged = settings.AllowLan != previousSettings.AllowLan
            || settings.Ipv6 != previousSettings.Ipv6;
        bool coreRestartRequired = RequiresCoreRestart(previousSettings, settings);
        bool systemProxyBindingChanged = HasSystemProxyBindingChanged(previousSettings, settings);
        bool coreWasRunning = IsCoreRunningForSettings();
        bool settingsSaved = false;
        bool restartStarted = false;
        StartupRegistrationChange? startupChange = null;
        try
        {
            if (reconcileStartup)
            {
                startupChange = _startupRegistration.Ensure(
                    settings.StartWithWindows,
                    ResolveStartupExecutablePath(settings.StartWithWindows));
            }

            await _settingsStore.SaveAsync(settings, cancellationToken);
            settingsSaved = true;
            _settings = settings;

            if (systemProxyBindingChanged
                && _localDevice.SystemProxyState is SystemProxyState.On
                    or SystemProxyState.RestoreRequired
                    or SystemProxyState.Enabling)
            {
                await ReconcileSystemProxyAsync(coreRunning: false, cancellationToken);
            }

            if (coreRestartRequired && coreWasRunning)
            {
                restartStarted = true;
                await RestartCoreCoreAsync(cancellationToken);
                if (!IsCoreHealthy())
                {
                    throw new InvalidOperationException("运行中设置已保存，但核心重启健康检查失败。");
                }
            }
            else if (networkSettingsChanged && _api is not null)
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
                        ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                        Logs = _logBuffer.Snapshot()
                    };
                    throw new InvalidOperationException("运行中网络设置应用失败，正在恢复旧设置。", exception);
                }
            }

            if (systemProxyBindingChanged)
            {
                await ReconcileSystemProxyAsync(
                    coreRunning: _snapshot.Core.State == CoreState.Running && _coreHealthConfirmed,
                    cancellationToken);
            }

            Publish();
        }
        catch (Exception exception)
        {
            if (!settingsSaved)
            {
                Exception? startupRollbackException = null;
                if (startupChange is { Changed: true })
                {
                    try
                    {
                        _startupRegistration.Rollback(startupChange);
                    }
                    catch (Exception startupException)
                    {
                        startupRollbackException = startupException;
                    }
                }

                if (startupRollbackException is not null)
                {
                    throw new InvalidOperationException(
                        "设置保存失败，且 Windows 启动项回滚失败。",
                        new AggregateException(exception, startupRollbackException));
                }

                throw;
            }

            Exception? rollbackException = null;
            try
            {
                await RollbackSettingsChangeAsync(
                    previousSettings,
                    coreRestartRequired,
                    coreWasRunning,
                    networkSettingsChanged,
                    systemProxyBindingChanged,
                    restartStarted);
            }
            catch (Exception restoreException)
            {
                rollbackException = restoreException;
            }

            Exception? startupRollbackFailure = null;
            if (startupChange is { Changed: true })
            {
                try
                {
                    _startupRegistration.Rollback(startupChange);
                }
                catch (Exception restoreException)
                {
                    startupRollbackFailure = restoreException;
                }
            }

            Exception?[] rollbackFailures = [rollbackException, startupRollbackFailure];
            Exception[] failures = rollbackFailures.OfType<Exception>().ToArray();
            string message = failures.Length == 0
                ? "设置应用失败，已恢复旧设置。"
                : "设置应用失败，旧设置或核心状态恢复失败，请检查核心状态。";
            _snapshot = _snapshot with
            {
                ErrorMessage = message,
                Logs = _logBuffer.Snapshot()
            };
            Publish();

            if (failures.Length > 0)
            {
                throw new InvalidOperationException(
                    message,
                    new AggregateException(new[] { exception }.Concat(failures)));
            }

            throw;
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
        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.SystemProxyEnabled != enabled;
        try
        {
            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { SystemProxyEnabled = enabled },
                    cancellationToken);
            }

            if (enabled && !_coreHealthConfirmed)
            {
                await RevokeSystemProxyForCoreLossAsync(operationLockHeld: true);
                _snapshot = _snapshot with { SystemProxy = _localDevice.SystemProxyState };
                Publish();
                return;
            }

            _snapshot = _snapshot with { SystemProxy = enabled ? SystemProxyState.Enabling : SystemProxyState.Disabling };
            Publish();
            if (enabled)
            {
                await _localDevice.EnableSystemProxyAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
            else
            {
                await _localDevice.DisableSystemProxyAsync(cancellationToken);
            }

            _snapshot = _snapshot with { SystemProxy = _localDevice.SystemProxyState, ErrorMessage = null };
            Publish();
        }
        catch
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { SystemProxy = _localDevice.SystemProxyState };
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
        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.TunEnabled != enabled;
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
            ServiceTunPayload payload = new(
                _settings.ControllerPort,
                string.Empty,
                enabled);
            ServiceResponse response = enabled
                ? await _localDevice.EnableTunAsync(payload, cancellationToken)
                : await _localDevice.DisableTunAsync(payload, cancellationToken);
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

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (ServiceRequestUnknownException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Failed, ErrorMessage = ErrorSanitizer.Sanitize(exception) };
            Publish();
            throw new InvalidOperationException("TUN 操作结果无法确认，请检查服务状态后重试。", exception);
        }
        catch (IOException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) };
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _snapshot = _snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) };
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
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 DNS 缓存期间核心会话已切换，请重试。",
                "远程 DNS 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearDnsCacheAsync(token),
                (session, token) => session.Api.ClearDnsCacheAsync(token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task UpdateGeoAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.UpdateGeo,
                "更新 Geo 数据库期间核心会话已切换，请重试。",
                "远程 Geo 数据库更新结果无法确认，请重试。",
                (api, _, token) => api.UpdateGeoAsync(token),
                (session, token) => session.Api.UpdateGeoAsync(token),
                cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<string> InstallCoreUpdateAsync(CoreUpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            bool wasRunning = _snapshot.Core.State == CoreState.Running;
            if (wasRunning)
            {
                await StopCoreCoreAsync(operationLockHeld: true, cancellationToken);
            }

            bool installed = false;
            bool rolledBack = false;
            try
            {
                ServiceCoreUpdatePayload payload = new(
                    manifest.Version,
                    manifest.DownloadUri,
                    manifest.Sha256);
                ServiceResponse response = await _localDevice.InstallCoreAsync(payload, cancellationToken);
                if (!response.Succeeded)
                {
                    throw new InvalidOperationException(response.Error ?? "ClashTray 服务无法安装 Mihomo 核心。");
                }

                installed = true;
                string path = response.Payload ?? _coreDiscovery.ManagedExecutablePath;
                _snapshot = _snapshot with { Core = _snapshot.Core with { Version = FindCoreVersion(), ErrorMessage = null }, ErrorMessage = null };
                Publish();
                if (wasRunning)
                {
                    await StartCoreCoreAsync(operationLockHeld: true, cancellationToken);
                    if (!IsCoreHealthy())
                    {
                        await StopCoreCoreAsync(operationLockHeld: true, CancellationToken.None);
                        await RollbackCoreUpdateAsync(CancellationToken.None);
                        rolledBack = true;
                        _snapshot = _snapshot with
                        {
                            Core = _snapshot.Core with
                            {
                                Version = FindCoreVersion(),
                                ErrorMessage = "新核心健康检查失败，已自动回滚。"
                            },
                            ErrorMessage = "新核心健康检查失败，已自动回滚。"
                        };
                        Publish();
                        await StartCoreCoreAsync(operationLockHeld: true, CancellationToken.None);
                        if (!IsCoreHealthy())
                        {
                            throw new InvalidOperationException("核心更新失败，且回滚后的旧核心也未能恢复。");
                        }

                        _logBuffer.Add(new LogEntry(
                            DateTimeOffset.UtcNow,
                            "ClashTray",
                            "warning",
                            "新核心健康检查失败，已自动回滚并恢复旧核心。"));
                        _snapshot = _snapshot with { Logs = _logBuffer.Snapshot() };
                        Publish();
                        throw new InvalidOperationException("核心更新健康检查失败，已自动回滚并恢复旧核心。");
                    }
                }

                return path;
            }
            catch
            {
                if (installed && !rolledBack)
                {
                    if (_snapshot.Core.State == CoreState.Running || _api is not null || _usingServiceCore)
                    {
                        await StopCoreCoreAsync(operationLockHeld: true, CancellationToken.None);
                    }

                    await RollbackCoreUpdateAsync(CancellationToken.None);
                    _snapshot = _snapshot with
                    {
                        Core = _snapshot.Core with { Version = FindCoreVersion() }
                    };
                    Publish();
                }

                if (wasRunning && !IsCoreHealthy())
                {
                    await StartCoreCoreAsync(operationLockHeld: true, CancellationToken.None);
                }

                throw;
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private bool IsCoreHealthy() =>
        _snapshot.Core.State == CoreState.Running && _coreHealthConfirmed;

    private async Task RollbackCoreUpdateAsync(CancellationToken cancellationToken)
    {
        ServiceResponse response = await _localDevice.RollbackCoreAsync(cancellationToken);
        if (!response.Succeeded)
        {
            throw new InvalidOperationException(response.Error ?? "ClashTray 服务无法回滚 Mihomo 核心。");
        }
    }

    public async Task RefreshDataAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            EndpointSession? remoteSession = CaptureActiveRemoteSession(
                EndpointCommand.ObserveStatus,
                "刷新远程端点数据期间会话已切换，请重试。");
            if (remoteSession is not null)
            {
                EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
                if (!await RefreshRemoteControllerSnapshotAsync(
                        remoteSession,
                        remoteStatus,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "远程端点数据刷新结果无法确认，请重试。");
                }

                return;
            }

            if (_api is not null)
            {
                await RefreshFromApiWithRetryAsync(
                    cancellationToken,
                    operationLockHeld: true);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal void AttachControllerForTesting(MihomoApiClient api, bool usingServiceCore)
    {
        ArgumentNullException.ThrowIfNull(api);
        SetController(api);
        _usingServiceCore = usingServiceCore;
        _coreHealthConfirmed = true;
        _snapshot = _snapshot with { Core = _snapshot.Core with { State = CoreState.Running } };
    }

    internal Task RefreshControllerDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken);

    internal Task RefreshControllerDataForTestingAsync(
        bool includeRulesAndProviders,
        CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken, includeRulesAndProviders);

    internal Task RevokeSystemProxyForTestingAsync() =>
        RevokeSystemProxyForCoreLossAsync(operationLockHeld: false);

    public async ValueTask DisposeAsync()
    {
        _endpointSessions.StatusChanged -= OnEndpointSessionStatusChanged;
        await StopRemoteRefreshAsync();
        await _endpointSessions.DisposeAsync();
        _networkSwitchRuntimeController.StatusChanged -= OnNetworkSwitchStatusChanged;
        await _networkSwitchRuntimeController.DisposeAsync();

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

        await _runtimeCts.CancelAsync();
        await _throttledPublisher.DisposeAsync();
        SetController(null);
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
        await AwaitQueuedProxyRecoveryAsync();
        _processManager.StateChanged -= OnProcessStateChanged;
        _processManager.LogLineReceived -= OnProcessLogLine;
        _httpClient.Dispose();
        _subscriptionOperationLock.Dispose();
        _dataRefreshLock.Dispose();
        _remoteRefreshLifecycleLock.Dispose();
        _remoteRefreshReadLock.Dispose();
        await _configurationSwitchCoordinator.DisposeAsync();
        _operationLock.Dispose();
        _runtimeCts.Dispose();
    }

    private async Task RefreshFromApiAsync(
        CancellationToken cancellationToken,
        bool includeRulesAndProviders = true)
    {
        MihomoApiClient? api = _api;
        if (api is null)
        {
            return;
        }

        bool coreHealthWasUnconfirmed = !_coreHealthConfirmed;
        await RefreshCoreHealthAsync(api, cancellationToken);
        await RefreshOptionalDataAsync(
            api,
            cancellationToken,
            includeRulesAndProviders || coreHealthWasUnconfirmed);
    }

    private async Task RefreshCoreHealthAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        using JsonDocument version = await api.GetVersionAsync(cancellationToken);
        string? versionText = MihomoDataParser.ParseVersion(version);
        using JsonDocument configurationState = await api.GetConfigurationAsync(force: false, cancellationToken);
        ProxyMode? mode = MihomoDataParser.ParseMode(configurationState);
        bool? tunEnabled = MihomoDataParser.ParseTunEnabled(configurationState);

        if (!ReferenceEquals(_api, api))
        {
            return;
        }

        _coreHealthConfirmed = true;
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

    private async Task RefreshOptionalDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken,
        bool includeRulesAndProviders = true)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_api, api))
            {
                return;
            }

            Task<ProxyDataResult> proxyTask = TryGetProxyDataAsync(api, cancellationToken);
            Task<TrafficDataResult> trafficTask = TryGetTrafficSnapshotAsync(api, cancellationToken);
            Task<MemoryDataResult> memoryTask = TryGetMemoryAsync(api, cancellationToken);
            Task<ConnectionDataResult> connectionsTask = TryGetConnectionDataAsync(api, cancellationToken);
            Task<IReadOnlyList<RuleInfo>> rulesTask = includeRulesAndProviders
                ? TryGetRulesAsync(api, cancellationToken)
                : Task.FromResult<IReadOnlyList<RuleInfo>>(_snapshot.Rules);
            Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providersTask =
                includeRulesAndProviders
                    ? TryGetProvidersAsync(api, cancellationToken)
                    : Task.FromResult<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>(
                        (_snapshot.Providers, _snapshot.RuleProviders));
            await Task.WhenAll(proxyTask, trafficTask, memoryTask, connectionsTask, rulesTask, providersTask);

            if (!ReferenceEquals(_api, api))
            {
                return;
            }

            ProxyDataResult proxyData = await proxyTask;
            TrafficDataResult trafficData = await trafficTask;
            MemoryDataResult memoryData = await memoryTask;
            ConnectionDataResult connectionData = await connectionsTask;
            IReadOnlyList<RuleInfo> rulesData = await rulesTask;
            (IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders) providerData = await providersTask;
            CoreStatus currentCore = _snapshot.Core;
            TrafficSnapshot? traffic = trafficData.Value;
            IReadOnlyList<ProxyGroup> proxyGroups = proxyData.Succeeded
                ? ReuseIfEqual(_snapshot.ProxyGroups, proxyData.Groups, ProxyGroupsEqual)
                : _snapshot.ProxyGroups;
            IReadOnlyList<ProxyNode> proxyNodes = proxyData.Succeeded
                ? ReuseIfEqual(_snapshot.ProxyNodes, proxyData.Nodes, ProxyNodesEqual)
                : _snapshot.ProxyNodes;
            IReadOnlyList<ConnectionInfo> connections = connectionData.Succeeded
                ? ReuseIfEqual(_snapshot.Connections, connectionData.Value, EqualityComparer<ConnectionInfo>.Default.Equals)
                : _snapshot.Connections;
            IReadOnlyList<RuleInfo> rules = ReuseIfEqual(
                _snapshot.Rules,
                rulesData,
                EqualityComparer<RuleInfo>.Default.Equals);
            IReadOnlyList<ProviderStatus> providers = ReuseIfEqual(
                _snapshot.Providers,
                providerData.Providers,
                EqualityComparer<ProviderStatus>.Default.Equals);
            IReadOnlyList<ProviderStatus> ruleProviders = ReuseIfEqual(
                _snapshot.RuleProviders,
                providerData.RuleProviders,
                EqualityComparer<ProviderStatus>.Default.Equals);

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
                ProxyGroups = proxyGroups,
                ProxyNodes = proxyNodes,
                Connections = connections,
                Rules = rules,
                Providers = providers,
                RuleProviders = ruleProviders,
                Logs = _logBuffer.Snapshot()
            };
            await _throttledPublisher.RequestAsync(cancellationToken);
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    private async Task RefreshFromApiWithRetryAsync(
        CancellationToken cancellationToken,
        bool operationLockHeld = false)
    {
        await RefreshCoreHealthWithRetryAsync(cancellationToken);
        await ApplyProgramOverridesAsync(
            coreRunning: true,
            cancellationToken: cancellationToken,
            operationLockHeld: operationLockHeld);
        MihomoApiClient? api = _api;
        if (api is not null)
        {
            await RefreshOptionalDataAsync(api, cancellationToken);
        }
    }

    private static IReadOnlyList<T> ReuseIfEqual<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> current,
        Func<T, T, bool> equals)
    {
        if (previous.Count != current.Count)
        {
            return current;
        }

        for (int index = 0; index < previous.Count; index++)
        {
            if (!equals(previous[index], current[index]))
            {
                return current;
            }
        }

        return previous;
    }

    private static bool ProxyGroupsEqual(ProxyGroup left, ProxyGroup right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.Current, right.Current, StringComparison.Ordinal)
        && string.Equals(left.Delay, right.Delay, StringComparison.Ordinal)
        && left.Members.SequenceEqual(right.Members, StringComparer.Ordinal);

    private static bool ProxyNodesEqual(ProxyNode left, ProxyNode right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.Delay, right.Delay, StringComparison.Ordinal)
        && left.IsCurrent == right.IsCurrent
        && left.Providers.SequenceEqual(right.Providers, StringComparer.Ordinal);

    private async Task RefreshCoreHealthWithRetryAsync(CancellationToken cancellationToken)
    {
        MihomoApiClient? api = _api;
        if (api is null)
        {
            return;
        }

        Exception? lastException = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
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
        TimeSpan retryDelay = TimeSpan.FromSeconds(2);
        int retryCount = 0;
        while (ShouldContinuePolling())
        {
            try
            {
                await Task.Delay(retryDelay, _runtimeCts.Token);
                if (!ShouldContinuePolling())
                {
                    break;
                }

                await _operationLock.WaitAsync(_runtimeCts.Token);
                try
                {
                    await RefreshFromApiAsync(
                        _runtimeCts.Token,
                        includeRulesAndProviders: false);
                    await ApplyProgramOverridesAsync(
                        coreRunning: true,
                        cancellationToken: _runtimeCts.Token,
                        operationLockHeld: true);
                    retryDelay = TimeSpan.FromSeconds(2);
                    retryCount = 0;
                }
                finally
                {
                    _operationLock.Release();
                }
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
                        await RevokeSystemProxyForCoreLossAsync(operationLockHeld: false);
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
                    serviceStatus = await _localDevice.GetStatusAsync(_runtimeCts.Token);
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
                    SetController(null);
                    await StopLogStreamAsync();
                    await RevokeSystemProxyForCoreLossAsync(operationLockHeld: false);
                    _snapshot = _snapshot with { Tun = TunState.Unavailable };
                    MarkCoreHealthUnconfirmed("服务重连", serviceException ?? exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                if (serviceStatus.Core == CoreState.Running)
                {
                    SetController(CreateApiClient());
                    await RevokeSystemProxyForCoreLossAsync(operationLockHeld: false);
                    _snapshot = _snapshot with { Tun = serviceStatus.Tun };
                    MarkCoreHealthUnconfirmed("控制器重连", exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                SetController(null);
                await StopLogStreamAsync();
                await RevokeSystemProxyForCoreLossAsync(operationLockHeld: false);
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
        Uri controllerUri = new Uri($"http://127.0.0.1:{_settings.ControllerPort}/");
        return new MihomoApiClient(_httpClient, controllerUri, string.Empty);
    }

    private async Task<EndpointRecord?> ResolveEndpointRecordAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken)
    {
        EndpointStoreLoadResult loaded = await _endpointStore.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Status == EndpointStoreLoadStatus.ReadFailed)
        {
            throw new IOException(loaded.Message ?? "端点元数据无法读取。");
        }

        return loaded.Endpoints.FirstOrDefault(endpoint => endpoint.Descriptor.Id == endpointId);
    }

    private async Task ReconcileEndpointSessionAsync(
        IReadOnlyList<EndpointDescriptor> catalogEndpoints)
    {
        EndpointSessionStatusEventArgs status = _endpointSessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return;
        }

        EndpointDescriptor? catalogEndpoint = catalogEndpoints.FirstOrDefault(endpoint =>
            endpoint.Id == status.Endpoint.Id);
        if (catalogEndpoint is null || catalogEndpoint != status.Endpoint)
        {
            await _endpointSessions.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private ControllerSessionSnapshot? BuildActiveControllerSnapshot()
    {
        EndpointSessionStatusEventArgs status = _endpointSessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return null;
        }

        EndpointSession? session = _endpointSessions.Current;
        ControllerSessionSnapshot snapshot = EndpointSessionSnapshotFactory.Create(
            status.Endpoint,
            status,
            session?.Handshake);
        MihomoControllerSnapshotData? remoteData = null;
        lock (_remoteRefreshGate)
        {
            if (_remoteControllerData is { } currentData
                && currentData.Generation == status.Generation
                && currentData.SelectionRevision == status.SelectionRevision)
            {
                remoteData = currentData.Snapshot;
            }
        }

        return remoteData is null
            ? snapshot
            : snapshot with
            {
                Status = remoteData.Status,
                ProxyGroups = remoteData.ProxyGroups,
                ProxyNodes = remoteData.ProxyNodes,
                Connections = remoteData.Connections,
                Rules = remoteData.Rules,
                Providers = remoteData.Providers,
                RuleProviders = remoteData.RuleProviders,
                Logs = remoteData.Logs,
                ErrorMessage = remoteData.ErrorMessage ?? snapshot.ErrorMessage
            };
    }

    private void OnEndpointSessionStatusChanged(
        object? sender,
        EndpointSessionStatusEventArgs status)
    {
        CancellationTokenSource? previousRefresh;
        RemoteRefreshCompletion? previousCompletion;
        lock (_remoteRefreshGate)
        {
            previousRefresh = _remoteRefreshCts;
            previousCompletion = _remoteRefreshCompletion;
            _remoteRefreshCts = null;
            _remoteControllerData = null;
            _remoteRefreshCompletion = status.State == EndpointSessionState.Connected
                ? new RemoteRefreshCompletion(
                    status.Generation,
                    status.SelectionRevision,
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously))
                : null;
        }

        if (previousCompletion is not null)
        {
            previousCompletion.Completion.TrySetCanceled();
        }

        if (previousRefresh is not null)
        {
            _ = CancelRemoteRefreshAsync(previousRefresh);
        }
        PublishAppSnapshot();
        _ = RestartRemoteRefreshAsync(status);
    }

    private async Task CancelRemoteRefreshAsync(CancellationTokenSource refreshCts)
    {
        try
        {
            await refreshCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogControllerFailure("远程 Controller 刷新取消", "/configs 或 /proxies", exception, 0);
        }
    }

    private async Task RestartRemoteRefreshAsync(EndpointSessionStatusEventArgs requestedStatus)
    {
        try
        {
            await _remoteRefreshLifecycleLock.WaitAsync(_runtimeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            CancellationTokenSource? previousRefresh;
            Task? previousTask;
            lock (_remoteRefreshGate)
            {
                previousRefresh = _remoteRefreshCts;
                previousTask = _remoteRefreshTask;
                _remoteRefreshCts = null;
                _remoteRefreshTask = null;
            }

            if (previousRefresh is not null)
            {
                await previousRefresh.CancelAsync().ConfigureAwait(false);
            }
            if (previousTask is not null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    LogControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                }
            }

            previousRefresh?.Dispose();
            if (requestedStatus.State != EndpointSessionState.Connected)
            {
                return;
            }

            EndpointSession? session = _endpointSessions.Current;
            EndpointSessionStatusEventArgs currentStatus = _endpointSessions.Status;
            if (session is null
                || currentStatus.Endpoint.Id != requestedStatus.Endpoint.Id
                || currentStatus.Generation != requestedStatus.Generation
                || currentStatus.SelectionRevision != requestedStatus.SelectionRevision
                || currentStatus.State != EndpointSessionState.Connected)
            {
                return;
            }

            CancellationTokenSource refreshCts = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCts.Token);
            Task refreshTask = RefreshRemoteControllerAsync(
                session,
                requestedStatus,
                refreshCts);
            lock (_remoteRefreshGate)
            {
                _remoteRefreshCts = refreshCts;
                _remoteRefreshTask = refreshTask;
            }
        }
        finally
        {
            _remoteRefreshLifecycleLock.Release();
        }
    }

    private async Task RefreshRemoteControllerAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        CancellationTokenSource refreshCts)
    {
        bool initialRefreshPending = true;
        TimeSpan retryDelay = RemoteRefreshRetryDelay;
        try
        {
            while (true)
            {
                refreshCts.Token.ThrowIfCancellationRequested();
                try
                {
                    if (!await RefreshRemoteControllerSnapshotAsync(
                            session,
                            status,
                            refreshCts.Token)
                        .ConfigureAwait(false))
                    {
                        return;
                    }
                    initialRefreshPending = false;
                    retryDelay = RemoteRefreshRetryDelay;
                    await _remoteRefreshDelayAsync(RemoteRefreshInterval, refreshCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (!IsCurrentRemoteSession(session, status))
                    {
                        return;
                    }

                    lock (_remoteRefreshGate)
                    {
                        if (initialRefreshPending)
                        {
                            CompleteRemoteRefreshUnsafe(
                                status.Generation,
                                status.SelectionRevision,
                                succeeded: false);
                            initialRefreshPending = false;
                        }
                    }

                    LogControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                    PublishAppSnapshot();
                    await _remoteRefreshDelayAsync(retryDelay, refreshCts.Token)
                        .ConfigureAwait(false);
                    retryDelay = TimeSpan.FromTicks(Math.Min(
                        RemoteRefreshMaxRetryDelay.Ticks,
                        retryDelay.Ticks * 2));
                }
            }
        }
        catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRemoteSession(session, status))
            {
                lock (_remoteRefreshGate)
                {
                    CompleteRemoteRefreshUnsafe(
                        status.Generation,
                        status.SelectionRevision,
                        succeeded: false);
                }
                LogControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                PublishAppSnapshot();
            }
        }
    }

    private async Task<bool> RefreshRemoteControllerSnapshotAsync(
        EndpointSession session,
        EndpointSessionStatusEventArgs status,
        CancellationToken cancellationToken)
    {
        await _remoteRefreshReadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MihomoControllerSnapshotData? previousData = null;
            lock (_remoteRefreshGate)
            {
                if (_remoteControllerData is { } currentData
                    && currentData.Generation == status.Generation
                    && currentData.SelectionRevision == status.SelectionRevision)
                {
                    previousData = currentData.Snapshot;
                }
            }

            MihomoControllerSnapshotData snapshot = await MihomoControllerSnapshotReader.ReadAsync(
                    session.Api,
                    session.Handshake.Version,
                    $"mihomo/{status.Endpoint.DisplayName}",
                    previousData,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!IsCurrentRemoteSession(session, status))
            {
                return false;
            }

            lock (_remoteRefreshGate)
            {
                _remoteControllerData = new RemoteControllerData(
                    status.Generation,
                    status.SelectionRevision,
                    snapshot);
                CompleteRemoteRefreshUnsafe(
                    status.Generation,
                    status.SelectionRevision,
                    succeeded: true);
            }

            PublishAppSnapshot();
            return true;
        }
        finally
        {
            _remoteRefreshReadLock.Release();
        }
    }

    private async Task WaitForRemoteRefreshAsync(
        EndpointSession session,
        CancellationToken cancellationToken)
    {
        Task? completion = null;
        lock (_remoteRefreshGate)
        {
            if (_remoteRefreshCompletion is { } current
                && current.Generation == session.Generation
                && current.SelectionRevision == session.SelectionRevision)
            {
                completion = current.Completion.Task;
            }
        }

        if (completion is not null)
        {
            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void CompleteRemoteRefreshUnsafe(
        long generation,
        long selectionRevision,
        bool succeeded)
    {
        if (_remoteRefreshCompletion is { } completion
            && completion.Generation == generation
            && completion.SelectionRevision == selectionRevision)
        {
            completion.Completion.TrySetResult(succeeded);
        }
    }

    private bool IsCurrentRemoteSession(
        EndpointSession session,
        EndpointSessionStatusEventArgs status)
    {
        EndpointSession? current = _endpointSessions.Current;
        EndpointSessionStatusEventArgs currentStatus = _endpointSessions.Status;
        return ReferenceEquals(current, session)
            && current.Generation == status.Generation
            && current.SelectionRevision == status.SelectionRevision
            && currentStatus.Endpoint.Id == status.Endpoint.Id
            && currentStatus.Generation == status.Generation
            && currentStatus.SelectionRevision == status.SelectionRevision
            && currentStatus.State == EndpointSessionState.Connected;
    }

    private EndpointSession? CaptureActiveRemoteSession(
        EndpointCommand command,
        string staleSessionMessage)
    {
        EndpointSessionStatusEventArgs status = _endpointSessions.Status;
        if (status.Endpoint.Kind != EndpointKind.Remote)
        {
            return null;
        }

        EndpointSession? session = _endpointSessions.Current;
        if (session is null || !IsCurrentRemoteSession(session, status))
        {
            throw new InvalidOperationException(staleSessionMessage);
        }

        EndpointCommandPolicy.EnsureAllowed(
            session.Endpoint.Kind,
            session.Capabilities,
            command);
        return session;
    }

    private async Task ExecuteControllerMutationAndRefreshAsync(
        EndpointCommand command,
        string staleSessionMessage,
        string remoteRefreshFailureMessage,
        Func<MihomoApiClient, long, CancellationToken, Task> localOperation,
        Func<EndpointSession, CancellationToken, Task> remoteOperation,
        CancellationToken cancellationToken,
        bool routeToRemote = true,
        bool includeRulesAndProviders = true)
    {
        ArgumentNullException.ThrowIfNull(localOperation);
        ArgumentNullException.ThrowIfNull(remoteOperation);

        EndpointSession? remoteSession = routeToRemote
            ? CaptureActiveRemoteSession(command, staleSessionMessage)
            : null;
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            await remoteOperation(remoteSession, cancellationToken).ConfigureAwait(false);
            if (!IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

            if (!await RefreshRemoteControllerSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(remoteRefreshFailureMessage);
            }

            return;
        }

        if (_api is null)
        {
            if (!routeToRemote)
            {
                _ = CaptureControllerSession();
            }

            return;
        }

        (MihomoApiClient api, long generation) = CaptureControllerSession();
        EnsureControllerCommand(api, generation, command, staleSessionMessage);
        await localOperation(api, generation, cancellationToken).ConfigureAwait(false);
        EnsureControllerSession(api, generation, staleSessionMessage);
        await RefreshFromApiAsync(
                cancellationToken,
                includeRulesAndProviders)
            .ConfigureAwait(false);
        EnsureControllerSession(api, generation, staleSessionMessage);
    }

    private async Task StopRemoteRefreshAsync()
    {
        await _remoteRefreshLifecycleLock.WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            CancellationTokenSource? refreshCts;
            Task? refreshTask;
            lock (_remoteRefreshGate)
            {
                refreshCts = _remoteRefreshCts;
                refreshTask = _remoteRefreshTask;
                _remoteRefreshCts = null;
                _remoteRefreshTask = null;
                _remoteControllerData = null;
                _remoteRefreshCompletion?.Completion.TrySetCanceled();
                _remoteRefreshCompletion = null;
            }

            if (refreshCts is not null)
            {
                await refreshCts.CancelAsync().ConfigureAwait(false);
            }
            if (refreshTask is not null)
            {
                try
                {
                    await refreshTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    LogControllerFailure("远程 Controller 刷新", "/configs 或 /proxies", exception, 0);
                }
            }

            refreshCts?.Dispose();
        }
        finally
        {
            _remoteRefreshLifecycleLock.Release();
        }
    }

    private void SetController(MihomoApiClient? api)
    {
        if (api is null)
        {
            _controllerSessions.Detach();
            return;
        }

        _controllerSessions.Attach(
            api,
            ControllerEndpointFactory.CreateLocal(_settings.ControllerPort),
            EndpointCapabilityDefaults.Local);
    }

    private (MihomoApiClient Api, long Generation) CaptureControllerSession()
    {
        MihomoControllerSession session = _controllerSessions.Capture();
        return (session.Api, session.Generation);
    }

    private void EnsureControllerSession(
        MihomoApiClient api,
        long generation,
        string message)
    {
        if (!_controllerSessions.IsCurrent(api, generation))
        {
            throw new InvalidOperationException(message);
        }
    }

    private void EnsureControllerCommand(
        MihomoApiClient api,
        long generation,
        EndpointCommand command,
        string staleSessionMessage)
    {
        EndpointId endpointId = _controllerSessions.Current?.Endpoint.Id ?? EndpointId.Local;
        TargetCommand targetCommand = new(endpointId, generation, command);
        _controllerSessions.EnsureTargetCommandAllowed(
            api,
            targetCommand,
            staleSessionMessage);
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
        await ApplyControllerProgramOverridesAsync(coreRunning, cancellationToken);
        await ApplyLocalDeviceProgramOverridesAsync(coreRunning, cancellationToken);
    }

    private async Task ApplyControllerProgramOverridesAsync(
        bool coreRunning,
        CancellationToken cancellationToken)
    {
        if (!coreRunning || _api is null)
        {
            return;
        }

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
                ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
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
                ErrorMessage = $"程序 TUN 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logBuffer.Snapshot()
            };
            Publish();
        }
    }

    private async Task ApplyLocalDeviceProgramOverridesAsync(
        bool coreRunning,
        CancellationToken cancellationToken)
    {
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
            _logBuffer.Add(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}"));
            _snapshot = _snapshot with
            {
                SystemProxy = _localDevice.SystemProxyState,
                ErrorMessage = $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logBuffer.Snapshot()
            };
            Publish();
        }
    }

    private async Task ApplyProgramNetworkPreferencesAsync(CancellationToken cancellationToken)
    {
        MihomoControllerSession? session = _controllerSessions.Current;
        if (session is null)
        {
            return;
        }

        MihomoApiClient api = session.Api;
        EnsureControllerCommand(
            api,
            session.Generation,
            EndpointCommand.ControlLocalCore,
            "程序局域网/IPv6 设置期间核心会话已切换，请重试。");
        using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        bool? currentAllowLan = MihomoDataParser.ParseAllowLan(configuration);
        bool? currentIpv6 = MihomoDataParser.ParseIpv6(configuration);
        if (currentAllowLan is bool currentAllowLanValue
            && currentAllowLanValue == _settings.AllowLan
            && currentIpv6 is bool currentIpv6Value
            && currentIpv6Value == _settings.Ipv6)
        {
            return;
        }

        try
        {
            using JsonDocument response = await api.SetNetworkSettingsAsync(
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
        MihomoControllerSession? session = _controllerSessions.Current;
        if (session is null)
        {
            return;
        }

        MihomoApiClient api = session.Api;
        EnsureControllerCommand(
            api,
            session.Generation,
            EndpointCommand.ControlLocalCore,
            "程序 TUN 设置期间核心会话已切换，请重试。");
        using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
        bool? current = MihomoDataParser.ParseTunEnabled(configuration);
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
        if (_settings.SystemProxyEnabled && coreRunning && _coreHealthConfirmed)
        {
            if (_localDevice.SystemProxyState is (SystemProxyState.Off or SystemProxyState.Failed))
            {
                await _localDevice.EnableSystemProxyAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
        }
        else
        {
            SystemProxyState detectedState = _localDevice.DetectSystemProxyState();
            if (detectedState is SystemProxyState.On
                or SystemProxyState.RestoreRequired
                or SystemProxyState.Enabling)
            {
                await _localDevice.DisableSystemProxyAsync(cancellationToken);
            }
        }

        SystemProxyState state = _localDevice.SystemProxyState;
        if (_snapshot.SystemProxy != state)
        {
            _snapshot = _snapshot with { SystemProxy = state };
            Publish();
        }
    }

    private async Task RollbackSettingsChangeAsync(
        AppSettings previousSettings,
        bool coreRestartRequired,
        bool coreWasRunning,
        bool networkSettingsChanged,
        bool systemProxyBindingChanged,
        bool restartStarted)
    {
        _settings = previousSettings;
        await _settingsStore.SaveAsync(previousSettings, CancellationToken.None);

        if (restartStarted)
        {
            await RestartCoreCoreAsync(CancellationToken.None);
            if (coreWasRunning && !IsCoreHealthy())
            {
                throw new InvalidOperationException("旧设置已恢复，但核心未能恢复健康。");
            }
        }
        else if (networkSettingsChanged && _api is not null)
        {
            await ApplyProgramNetworkPreferencesAsync(CancellationToken.None);
        }

        if (systemProxyBindingChanged)
        {
            await ReconcileSystemProxyAsync(
                coreRunning: _snapshot.Core.State == CoreState.Running && _coreHealthConfirmed,
                CancellationToken.None);
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
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
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
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
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
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using JsonDocument configuration = await api.GetConfigurationAsync(force: false, cancellationToken);
            bool? allowLan = MihomoDataParser.ParseAllowLan(configuration);
            bool? ipv6 = MihomoDataParser.ParseIpv6(configuration);
            bool allowLanMatches = !expectedAllowLan.HasValue
                || allowLan is bool allowLanValue && allowLanValue == expectedAllowLan.Value;
            bool ipv6Matches = !expectedIpv6.HasValue
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

        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using JsonDocument response = await api.SetNetworkSettingsAsync(allowLan, ipv6, timeout.Token);
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
            MihomoApiClient? api = _api;
            if (api is null || _logStreamTask is { IsCompleted: false })
            {
                return;
            }

            _logStreamCts?.Dispose();
            CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(_runtimeCts.Token);
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

        if (streamCts is not null)
        {
            await streamCts.CancelAsync();
        }
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
        TimeSpan retryDelay = TimeSpan.FromSeconds(1);
        string path = $"/logs?level={Uri.EscapeDataString(_settings.LogLevel)}&format=structured";
        while (!cancellationToken.IsCancellationRequested && ReferenceEquals(_api, api))
        {
            try
            {
                using ClientWebSocket socket = await api.ConnectWebSocketAsync(path, cancellationToken);
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
        byte[] receiveBuffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using MemoryStream message = new MemoryStream();
            bool isText = true;
            bool isOversized = false;
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(
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
                        await message.WriteAsync(receiveBuffer.AsMemory(0, received.Count), cancellationToken);
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
                using JsonDocument document = await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken);
                foreach (LogEntry entry in MihomoDataParser.ParseLogs(document, "mihomo"))
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
            using JsonDocument document = await api.GetProxiesAsync(cancellationToken);
            (IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes) data = MihomoDataParser.ParseProxies(document);
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
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using JsonDocument document = await api.GetTrafficAsync(cancellationToken);
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
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using JsonDocument memory = await api.GetMemoryAsync(cancellationToken);
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
            using JsonDocument document = await api.GetConnectionsAsync(cancellationToken);
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
            using JsonDocument document = await api.GetRulesAsync(cancellationToken);
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
            using JsonDocument providers = await api.GetProvidersAsync(cancellationToken);
            using JsonDocument ruleProviders = await api.GetRuleProvidersAsync(cancellationToken);
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
            using JsonDocument logs = await _api!.GetLogsAsync(_settings.LogLevel, cancellationToken);
            foreach (LogEntry entry in MihomoDataParser.ParseLogs(logs, "mihomo"))
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

    private NetworkSwitchPolicyInput CreateNetworkSwitchPolicyInput(
        NetworkContextSnapshot context,
        NetworkSwitchRuleSet rules) =>
        new(
            rules.AutomaticSwitchingEnabled,
            context,
            rules.Rules,
            rules.DefaultConfigurationId,
            GetActiveConfiguration()?.Id,
            _snapshot.Configurations
                .Select(configuration => configuration.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private void OnNetworkSwitchStatusChanged(object? sender, NetworkSwitchStatus status)
    {
        _snapshot = _snapshot with { NetworkSwitch = status };
        Publish();
    }

    private string? FindCoreVersion()
    {
        string? path = _coreDiscovery.FindExecutable();
        return path is null ? null : CoreDiscovery.GetVersion(path);
    }

    private void SetCoreRunningPendingHealth(TunState tunState)
    {
        _coreHealthConfirmed = false;
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
        _coreHealthConfirmed = false;
        string message = $"核心状态暂时无法确认（{phase}：{DescribeControllerError(exception)}）。";
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
        string status = exception is HttpRequestException { StatusCode: { } statusCode }
            ? $"HTTP {(int)statusCode}"
            : "HTTP 未确认";
        string duration = elapsed is null ? "未测量" : $"{elapsed.Value.TotalMilliseconds:0}ms";
        string hosting = _usingServiceCore ? "service" : "local";
        string message = $"{phase}失败：托管方式={hosting}，路径={path}，{status}，耗时={duration}，重试={retryCount}，错误类型={DescribeControllerError(exception)}。";
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

    private void OnProcessStateChanged(object? sender, CoreState state) =>
        UpdateCoreState(state, state == CoreState.Failed ? "Mihomo 进程已退出" : null);

    private void QueueSystemProxyRecovery()
    {
        lock (_proxyRecoveryGate)
        {
            if (_proxyRecoveryTask is { IsCompleted: false })
            {
                return;
            }

            _proxyRecoveryTask = Task.Run(() => RevokeSystemProxyForCoreLossAsync(operationLockHeld: false));
        }
    }

    private async Task RevokeSystemProxyForCoreLossAsync(bool operationLockHeld)
    {
        if (!operationLockHeld)
        {
            await _operationLock.WaitAsync(CancellationToken.None);
        }

        try
        {
            try
            {
                await ReconcileSystemProxyAsync(coreRunning: false, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logBuffer.Add(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "error",
                    $"核心不可用时撤销系统代理失败：{ErrorSanitizer.Sanitize(exception)}"));
                _snapshot = _snapshot with
                {
                    SystemProxy = _localDevice.SystemProxyState,
                    ErrorMessage = "核心不可用时撤销系统代理失败，系统代理状态需要恢复。",
                    Logs = _logBuffer.Snapshot()
                };
                Publish();
            }
        }
        finally
        {
            if (!operationLockHeld)
            {
                _operationLock.Release();
            }
        }
    }

    private async Task AwaitQueuedProxyRecoveryAsync()
    {
        Task? recoveryTask;
        lock (_proxyRecoveryGate)
        {
            recoveryTask = _proxyRecoveryTask;
        }

        if (recoveryTask is null)
        {
            return;
        }

        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateCoreState(CoreState state, string? error)
    {
        if (state != CoreState.Running)
        {
            _coreHealthConfirmed = false;
        }

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
        if (state is CoreState.Failed or CoreState.Stopped or CoreState.Missing)
        {
            QueueSystemProxyRecovery();
        }
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
        UpdateSubscriptionState(SubscriptionState.Failed, $"订阅 {profile.Name} 定时刷新失败：{ErrorSanitizer.Sanitize(exception)}");
    }

    private void OnScheduledSubscriptionCycleFailed(Exception exception)
    {
        UpdateSubscriptionState(SubscriptionState.Failed, $"定时订阅任务失败：{ErrorSanitizer.Sanitize(exception)}");
    }

    private void Publish()
    {
        string? error = ErrorSanitizer.SanitizeNullable(_snapshot.ErrorMessage);
        string? coreError = ErrorSanitizer.SanitizeNullable(_snapshot.Core.ErrorMessage);
        _snapshot = _snapshot with
        {
            Core = _snapshot.Core with { ErrorMessage = coreError },
            ErrorMessage = error
        };
        SnapshotChanged?.Invoke(this, _snapshot);
        PublishAppSnapshot();
    }

    private void PublishAppSnapshot() => AppSnapshotChanged?.Invoke(this, AppSnapshot);

    private void OnProcessLogLine(string line, bool isError)
    {
        AddMihomoLog(new LogEntry(DateTimeOffset.UtcNow, "mihomo", isError ? "error" : "info", line));
    }

    private void AddMihomoLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _snapshot = _snapshot with { Logs = _logBuffer.Snapshot() };
        _throttledPublisher.Queue();
    }

    private bool IsCoreRunningForSettings() =>
        _snapshot.Core.State == CoreState.Running
        || _api is not null
        || _usingServiceCore;

    private static bool RequiresCoreRestart(AppSettings previous, AppSettings next) =>
        previous.HttpPort != next.HttpPort
        || previous.SocksPort != next.SocksPort
        || previous.MixedPort != next.MixedPort
        || previous.ControllerPort != next.ControllerPort
        || previous.TcpConcurrent != next.TcpConcurrent
        || !string.Equals(previous.LogLevel, next.LogLevel, StringComparison.OrdinalIgnoreCase);

    private static bool HasSystemProxyBindingChanged(AppSettings previous, AppSettings next) =>
        previous.MixedPort != next.MixedPort
        || !string.Equals(previous.BypassList, next.BypassList, StringComparison.Ordinal);

    private static void ValidateSettings(AppSettings settings)
        => SettingsValidator.Validate(settings);

    private static string? ResolveStartupExecutablePath(bool required)
    {
        string? path = Environment.ProcessPath;
        bool valid = !string.IsNullOrWhiteSpace(path)
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

    private sealed class RuntimeConfigurationSwitchOperations : IConfigurationSwitchOperations
    {
        private readonly ClashTrayRuntime _runtime;

        public RuntimeConfigurationSwitchOperations(ClashTrayRuntime runtime)
        {
            _runtime = runtime;
        }

        public string? CurrentConfigurationId => _runtime._settings.ActiveConfigurationId;

        public async Task<ConfigurationProfile?> ResolveCandidateAsync(
            string id,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ConfigurationProfile> configurations =
                await _runtime._configurationStore.ListAsync(cancellationToken);
            return configurations.FirstOrDefault(configuration =>
                string.Equals(configuration.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public Task ValidateCandidateAsync(
            ConfigurationProfile candidate,
            CancellationToken cancellationToken) =>
            _runtime._configurationStore.ValidateCandidateAsync(candidate, cancellationToken);

        public Task<ConfigurationSwitchRuntimeState> CaptureStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConfigurationSwitchRuntimeState(
                _runtime._settings.ActiveConfigurationId,
                _runtime._snapshot.Core.State == CoreState.Running,
                _runtime._settings.SystemProxyEnabled,
                _runtime._snapshot.SystemProxy,
                _runtime._settings.TunEnabled,
                _runtime._snapshot.Tun,
                _runtime.ControllerGeneration,
                _runtime._settings));
        }

        public async Task ApplyAsync(
            ConfigurationSwitchContext context,
            CancellationToken cancellationToken)
        {
            bool restartCore = context.Request.RestartCore && context.PreviousState.CoreWasRunning;
            if (restartCore)
            {
                await _runtime.StopCoreCoreAsync(operationLockHeld: true, cancellationToken);
                await context.SetStageAsync(
                    ConfigurationSwitchStage.NetworkStateSafeguarded,
                    CancellationToken.None);
            }

            await _runtime.PromoteConfigurationInMemoryAsync(context.Candidate, cancellationToken);
            await context.SetStageAsync(
                ConfigurationSwitchStage.RuntimePromoted,
                CancellationToken.None);

            if (restartCore)
            {
                await _runtime.StartCoreCoreAsync(operationLockHeld: true, cancellationToken);
                if (!_runtime.IsCoreHealthy())
                {
                    throw new InvalidOperationException("切换后的 Mihomo 核心健康检查失败。");
                }

                await context.SetStageAsync(
                    ConfigurationSwitchStage.CoreRestarted,
                    CancellationToken.None);
            }

            await _runtime.CommitConfigurationSelectionAsync(
                context.Candidate,
                CancellationToken.None);
        }

        public async Task RollbackAsync(
            ConfigurationSwitchContext context,
            Exception failure,
            CancellationToken cancellationToken)
        {
            await _runtime.RestoreConfigurationSelectionAsync(
                context.PreviousState,
                cancellationToken);
            if (context.PreviousState.CoreWasRunning)
            {
                await _runtime.RestartCoreCoreAsync(cancellationToken);
                if (!_runtime.IsCoreHealthy())
                {
                    throw new InvalidOperationException("旧配置核心恢复后的健康检查失败。");
                }
            }

            _runtime.Publish();
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









using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class ClashTrayRuntime : IAsyncDisposable
{
    private readonly OperationGate _operationLock = new();
    private readonly BooleanSingleFlight<TunState> _tunOperation = new(
        "TUN",
        cancelWhenNoWaiters: false);
    private readonly LatestWinsOperation<ModeIntent> _modeOperation = new("模式");
    private readonly object _proxyOperationGate = new();
    private readonly Dictionary<string, LatestWinsOperation<ProxySelectionIntent>> _proxyOperations =
        new(StringComparer.Ordinal);
    private readonly object _delayOperationGate = new();
    private readonly Dictionary<string, SingleFlightOperation<int?>> _proxyDelayOperations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SingleFlightOperation<IReadOnlyDictionary<string, int?>>> _proxyGroupDelayOperations =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _subscriptionOperationLock = new(1, 1);
    private readonly SemaphoreSlim _dataRefreshLock = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly object _disposeGate = new();
    private readonly object _publishGate = new();
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
    private readonly RuntimeStateStore _stateStore = new(CreateInitialSnapshot());
    private IReadOnlyList<EndpointDescriptor> _remoteEndpointDescriptors = [];
    private EndpointStoreLoadStatus _endpointStoreStatus = EndpointStoreLoadStatus.FirstRun;
    private string? _endpointStoreMessage;
    private readonly RemoteControllerRefreshCoordinator _remoteRefresh;
    private readonly Func<MihomoApiClient>? _controllerApiFactory;
    private bool _usingServiceCore;
    private long _confirmedCoreLifecycleEpoch = long.MinValue;
    private long _confirmedCoreProcessGeneration = long.MinValue;
    private long _confirmedControllerGeneration = long.MinValue;
    private TunState _confirmedTunState = TunState.Unavailable;
    private long _coreLifecycleEpoch;
    private long _proxyOwnershipRevision;
    private long _proxyIntentRevision;
    private readonly object _proxyRecoveryGate = new();
    private Task? _proxyRecoveryTask;
    private Task? _disposeTask;

    private static readonly TimeSpan DisposeCleanupTimeout = TimeSpan.FromSeconds(30);

    private sealed record ProxyDataResult(
        bool Succeeded,
        IReadOnlyList<ProxyGroup> Groups,
        IReadOnlyList<ProxyNode> Nodes);

    private readonly record struct TrafficDataResult(bool Succeeded, TrafficSnapshot? Value);

    private readonly record struct MemoryDataResult(bool Succeeded, long Value);

    private sealed record ProxySelectionIntent(string Group, string Proxy);

    private sealed record ModeIntent(ProxyMode Mode, bool RouteToRemote);

    private readonly record struct CoreLossContext(
        long LifecycleEpoch,
        long ProcessGeneration,
        long ControllerGeneration,
        long ProxyOwnershipRevision,
        long ProxyIntentRevision,
        bool CoreHealthConfirmed);

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
        Func<TimeSpan, CancellationToken, Task>? remoteRefreshDelayAsync = null,
        Func<EndpointSession, EndpointSessionStatusEventArgs, CancellationToken, Task>? remoteLogStreamRunner = null,
        Func<MihomoApiClient>? controllerApiFactory = null)
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
            (request, cancellation) => ExecuteConfigurationSwitchWithLeaseAsync(request, cancellation));
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
        _controllerApiFactory = controllerApiFactory;
        _throttledPublisher = new SnapshotPublishThrottle(Publish, _runtimeCts.Token);
        _remoteRefresh = new RemoteControllerRefreshCoordinator(
            _endpointSessions,
            () => _settings.LogLevel,
            PublishAppSnapshot,
            () => _throttledPublisher.Queue(),
            (phase, path, exception, retryCount) => LogControllerFailure(phase, path, exception, retryCount),
            remoteRefreshDelayAsync,
            remoteLogStreamRunner,
            _runtimeCts.Token);
        _endpointSessions.StatusChanged += _remoteRefresh.HandleSessionStatusChanged;
        _processManager.StateChanged += OnProcessStateChanged;
        _processManager.LogLineReceived += OnProcessLogLine;
    }

    public RuntimeSnapshot Snapshot => _stateStore.Snapshot;

    public long SnapshotRevision => _stateStore.Revision;

    public AppSnapshot AppSnapshot => ComposeAppSnapshot(Snapshot);

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
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations,
            Core = snapshot.Core with
            {
                State = _coreDiscovery.FindExecutable() is null ? CoreState.Missing : CoreState.Stopped,
                Version = FindCoreVersion()
            },
            SystemProxy = _localDevice.DetectSystemProxyState()
        });
        await ApplyProgramOverridesWithLeaseAsync(coreRunning: false, cancellationToken: cancellationToken);
        try
        {
            ServiceResponse serviceStatus = await _localDevice.GetStatusAsync(cancellationToken);
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = AdoptServiceTunState(serviceStatus.Tun),
                Core = snapshot.Core with
                {
                    State = serviceStatus.Core == CoreState.Stopped && snapshot.Core.State == CoreState.Missing
                        ? CoreState.Missing
                        : serviceStatus.Core
                }
            });
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

                await ApplyProgramOverridesWithLeaseAsync(
                    coreRunning: CoreHealthConfirmed,
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
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
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
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
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
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
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
                Snapshot.Core.State))
        {
            await StartCoreAsync(cancellationToken);
        }

        await _networkSwitchRuntimeController.InitializeAsync(cancellationToken);

        if (settingsLoad.Status is SettingsLoadStatus.Recovered
            or SettingsLoadStatus.ReadFailed
            or SettingsLoadStatus.RecoveryFailed)
        {
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = settingsLoad.Message });
            Publish();
        }

        if (!string.IsNullOrWhiteSpace(journalRecoveryMessage))
        {
            _stateStore.Update(snapshot => snapshot with { ErrorMessage = journalRecoveryMessage });
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
        ThrowIfRuntimeQuiescing();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeCts.Token);
        CancellationToken token = linked.Token;
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

        EndpointSession? session = await _endpointSessions.SelectAsync(endpoint, token)
            .ConfigureAwait(false);
        if (session is not null)
        {
            await _remoteRefresh.WaitForRefreshAsync(session, token).ConfigureAwait(false);
        }

        return session;
    }

    public async Task<EndpointHandshakeResult> TestRemoteEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        ThrowIfRuntimeQuiescing();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeCts.Token);
        if (endpointId == EndpointId.Local)
        {
            throw new InvalidOperationException("只读连接测试仅适用于远程端点。");
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

        return await _endpointSessions.TestAsync(endpoint, linked.Token)
            .ConfigureAwait(false);
    }

    public async Task DisconnectEndpointAsync()
    {
        ThrowIfRuntimeQuiescing();
        await _endpointSessions.DisconnectAsync().ConfigureAwait(false);
    }

    public async Task<EndpointCatalogLoadResult> SaveRemoteEndpointAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            await _endpointStore.UpsertAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
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
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
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
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
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
    }

    public async Task<EndpointRemovalResult> RemoveRemoteEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            EndpointRemovalResult result = await _endpointRemovalCoordinator.RemoveAsync(
                    endpointId,
                    cancellationToken)
                .ConfigureAwait(false);
            await LoadEndpointCatalogAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
    }

    public Task StartCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            (operationLease, token) => StartCoreCoreAsync(operationLease, token),
            cancellationToken);

    private async Task AdmitCoreLifecycleAsync(
        string operationName,
        Func<OperationGate.Lease, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        OperationGate.Lease? lease = _operationLock.TryAcquire();
        if (lease is null)
        {
            throw new OperationBusyException(operationName);
        }

        using (lease)
        {
            await operation(lease, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Snapshot.Core.State == CoreState.Running)
            {
                return;
            }

            Interlocked.Increment(ref _coreLifecycleEpoch);

            ConfigurationProfile? profile = GetActiveConfiguration();
            string? executable = _coreDiscovery.FindExecutable();
            if (executable is null)
            {
                UpdateCoreState(CoreState.Missing, "未找到 Mihomo 核心，请在设置中安装或选择 mihomo.exe");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return;
            }

            if (profile is null)
            {
                UpdateCoreState(CoreState.Failed, "请先导入一个 Mihomo 配置");
                await RevokeSystemProxyForCoreLossAsync(
                    operationLease,
                    cancellationToken: cancellationToken);
                return;
            }

            UpdateCoreState(CoreState.Validating, null);
            string runtimeConfigPath = Path.Combine(_paths.RuntimeRoot, "mihomo", "active-config.yaml");
            await RuntimeConfigBuilder.BuildForCoreStartAsync(
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
            catch (ServiceUnavailableException exception) when (exception.DispatchState == ServiceDispatchState.NotDispatched)
            {
            }
            catch (ServiceRequestUnknownException exception)
            {
                serviceResponse = await ReconcileUnknownServiceStartAsync(exception).ConfigureAwait(false);
                if (serviceResponse is null)
                {
                    return;
                }
            }
            catch (ServiceProtocolVersionMismatchException)
            {
                // An incompatible service must fail explicitly; reconciling it
                // as an unknown outcome would disguise a deployment error.
                throw;
            }
            catch (IOException exception)
            {
                serviceResponse = await ReconcileUnknownServiceStartAsync(exception).ConfigureAwait(false);
                if (serviceResponse is null)
                {
                    return;
                }
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
                _usingServiceCore = false;
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
            SetCoreRunningPendingHealth(serviceResponse?.Tun ?? TunState.Unavailable);
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
                coreRunning: CoreHealthConfirmed,
                cancellationToken: cancellationToken,
                operationLease: operationLease);
            StartPolling();
            StartOptionalRefreshInBackground(_api);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RevokeSystemProxyForCoreLossAsync(
                operationLease,
                cancellationToken: cancellationToken);
            UpdateCoreState(CoreState.Failed, ErrorSanitizer.Sanitize(exception));
        }
    }

    private async Task<ServiceResponse?> ReconcileUnknownServiceStartAsync(Exception startException)
    {
        // Once StartCore was dispatched, conservatively retain ownership even
        // when the response was lost. This prevents a local fallback from
        // starting a second core and guarantees shutdown will still issue the
        // matching service StopCore request.
        _usingServiceCore = true;
        ServiceResponse? status = null;
        Exception? statusException = null;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _runtimeCts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            status = await _localDevice.GetStatusAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            statusException = exception;
        }

        if (status?.Core == CoreState.Running)
        {
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = AdoptServiceTunState(status.Tun)
            });
            return status with { Succeeded = true, Error = null };
        }

        string statusDetail = status is null
            ? $"状态查询失败：{ErrorSanitizer.Sanitize(statusException ?? startException)}"
            : $"服务当前报告 {status.Core}";
        UpdateCoreState(
            CoreState.Failed,
            $"ClashTray 服务启动请求已发送，但结果尚未确认（{statusDetail}）。程序将继续后台核对，并在退出时保守停止服务核心。");
        StartPolling();
        return null;
    }

    public Task StopCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            (operationLease, token) => StopCoreCoreAsync(operationLease, token),
            cancellationToken);

    private async Task StopCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        try
        {
            Interlocked.Increment(ref _coreLifecycleEpoch);
            UpdateCoreState(CoreState.Stopping, null);
            InvalidateCoreHealth();
            SetController(null);
            await StopLogStreamAsync();
            if (_usingServiceCore)
            {
                try
                {
                    ServiceResponse response = await _localDevice.StopCoreAsync(cancellationToken);
                    if (!response.Succeeded)
                    {
                        _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(response.Tun) });
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

                    _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(response.Tun) });
                }
                catch (TimeoutException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (ServiceUnavailableException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务不可用，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务不可用，停止结果无法确认。",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务访问被拒绝，停止结果无法确认：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务访问被拒绝，停止结果无法确认。",
                        exception);
                }
                catch (ServiceRequestUnknownException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    UpdateCoreState(CoreState.Failed, $"ClashTray 服务停止结果无法确认，请检查服务状态后重试：{ErrorSanitizer.Sanitize(exception)}");
                    throw new InvalidOperationException(
                        "ClashTray 服务停止结果无法确认，请检查服务状态后重试。",
                        exception);
                }
                catch (IOException exception)
                {
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
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
                _confirmedTunState = TunState.Off;
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Off });
            }

            UpdateCoreState(CoreState.Stopped, null);
        }
        finally
        {
            await RevokeSystemProxyForCoreLossAsync(
                operationLease,
                cancellationToken: cancellationToken);
        }
    }

    public Task RestartCoreAsync(CancellationToken cancellationToken = default) =>
        AdmitCoreLifecycleAsync(
            "核心",
            RestartCoreCoreAsync,
            cancellationToken);

    private async Task RestartCoreCoreAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        UpdateCoreState(CoreState.Restarting, null);
        await StopCoreCoreAsync(operationLease, cancellationToken);
        await StartCoreCoreAsync(operationLease, cancellationToken);
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
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            await RefreshSubscriptionCoreLockedAsync(profile, operationLease, cancellationToken);
        }
    }

    private async Task RefreshSubscriptionCoreLockedAsync(
        ConfigurationProfile profile,
        OperationGate.Lease operationLease,
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
                    operationLease,
                    cancellationToken);
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
                AddApplicationLog(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "info",
                    "订阅内容 SHA-256 未变化，已跳过 Mihomo 重启。"));
                _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
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

        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
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
                        restartCore: Snapshot.Core.State == CoreState.Running,
                        forceApply: true),
                    operationLease,
                    cancellationToken);
            }
            else
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }
        }
    }

    public async Task SetActiveConfigurationAsync(string id, CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            ConfigurationSwitchResult result = await ExecuteConfigurationSwitchAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, id),
                operationLease,
                cancellationToken);
            if (result.Outcome == ConfigurationSwitchOutcome.NoOp)
            {
                await RefreshConfigurationSnapshotAsync(cancellationToken);
            }

            if (_networkSwitchRuntimeController.IsInitialized)
            {
                _networkSwitchRuntimeController.SetManualOverrideForCurrentNetwork(id);
            }
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
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            await _networkSwitchRuntimeController.SetRulesAsync(rules, cancellationToken);
        }
    }

    public async Task ClearNetworkSwitchManualOverrideAsync(
        CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            if (_networkSwitchRuntimeController.IsInitialized)
            {
                _networkSwitchRuntimeController.ClearManualOverride();
            }
        }
    }

    private async Task<ConfigurationSwitchResult> ExecuteConfigurationSwitchAsync(
        ConfigurationSwitchRequest request,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        _configurationSwitchOperations.ActiveLease = operationLease;
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
            _configurationSwitchOperations.ActiveLease = null;
        }
    }

    private async Task<ConfigurationSwitchResult> ExecuteConfigurationSwitchWithLeaseAsync(
        ConfigurationSwitchRequest request,
        CancellationToken cancellationToken)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteConfigurationSwitchAsync(request, operationLease, cancellationToken).ConfigureAwait(false);
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
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = snapshot.Core with
            {
                ConfigurationName = configurations.FirstOrDefault(configuration =>
                    string.Equals(configuration.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase))?.Name
            }
        });
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
        _stateStore.Update(snapshot => snapshot with
        {
            Configurations = configurations.Select(configuration => configuration with
            {
                IsActive = string.Equals(configuration.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)
            }).ToArray(),
            Core = snapshot.Core with { ConfigurationName = candidate.Name }
        });
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
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            bool wasActive = profile.IsActive
                || string.Equals(profile.Id, _settings.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase);
            if (wasActive && Snapshot.Core.State == CoreState.Running)
            {
                await StopCoreCoreAsync(operationLease, cancellationToken);
            }

            await _configurationStore.DeleteAsync(profile, cancellationToken);
            IReadOnlyList<ConfigurationProfile> configurations = await _configurationStore.ListAsync(cancellationToken);
            if (wasActive)
            {
                _settings = _settings with { ActiveConfigurationId = null };
                await _settingsStore.SaveAsync(_settings, cancellationToken);
            }

            _stateStore.Update(snapshot => snapshot with
            {
                Configurations = configurations.Select(configuration => configuration with
                {
                    IsActive = configuration.Id == _settings.ActiveConfigurationId
                }).ToArray(),
                Core = wasActive ? snapshot.Core with { ConfigurationName = null } : snapshot.Core
            });
            Publish();
        }
    }

    private LatestWinsOperation<ProxySelectionIntent> GetProxySelectionOperation(string group)
    {
        lock (_proxyOperationGate)
        {
            if (!_proxyOperations.TryGetValue(group, out LatestWinsOperation<ProxySelectionIntent>? operation))
            {
                // Keep the intent table bounded even if a remote endpoint
                // returns attacker-controlled group names over time.
                if (!TrimIdleLatestOperations(_proxyOperations))
                {
                    throw new OperationBusyException("节点");
                }

                operation = new LatestWinsOperation<ProxySelectionIntent>("节点");
                _proxyOperations[group] = operation;
            }

            return operation;
        }
    }

    public async Task SetModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        await _modeOperation.RequestAsync(
                new ModeIntent(mode, RouteToRemote: true),
                (intent, token) => SetModeIntentCoreAsync(
                    intent,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetLocalModeAsync(ProxyMode mode, CancellationToken cancellationToken = default)
    {
        await _modeOperation.RequestAsync(
                new ModeIntent(mode, RouteToRemote: false),
                (intent, token) => SetModeIntentCoreAsync(
                    intent,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ModeIntent> SetModeIntentCoreAsync(
        ModeIntent intent,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.SwitchMode,
                "模式切换期间核心会话已切换，请重试。",
                "远程端点模式切换结果无法确认，请重试。",
                (api, _, token) => api.SetModeAsync(intent.Mode, token),
                (session, token) => session.Api.SetModeAsync(intent.Mode, token),
                cancellationToken,
                intent.RouteToRemote,
                refreshScope: MutationRefreshScope.Mode);
        }

        return intent;
    }

    public Task SelectProxyAsync(string group, string proxy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);
        LatestWinsOperation<ProxySelectionIntent> operation = GetProxySelectionOperation(group);
        ProxySelectionIntent intent = new(group, proxy);
        return SelectProxyLatestAsync(operation, intent, cancellationToken);
    }

    private async Task SelectProxyLatestAsync(
        LatestWinsOperation<ProxySelectionIntent> operation,
        ProxySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        await operation.RequestAsync(
                intent,
                (requested, token) => SelectProxyIntentCoreAsync(requested, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProxySelectionIntent> SelectProxyIntentCoreAsync(
        ProxySelectionIntent intent,
        CancellationToken cancellationToken)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            string? previousProxy = Snapshot.ProxyGroups
                .FirstOrDefault(item => string.Equals(item.Name, intent.Group, StringComparison.Ordinal))
                ?.Current;
            Exception? disconnectException = null;
            bool selectionChanged = previousProxy is not null
                && !string.Equals(previousProxy, intent.Proxy, StringComparison.Ordinal);

            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.SwitchProxy,
                "节点切换期间核心会话已切换，请重新选择节点。",
                "远程端点节点切换结果无法确认，请重试。",
                async (api, generation, token) =>
                {
                    await api.SelectProxyAsync(intent.Group, intent.Proxy, token);
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
                            AddApplicationLog(new LogEntry(
                                DateTimeOffset.UtcNow,
                                "ClashTray",
                                "error",
                                $"节点已切换，但未能断开旧连接：{ErrorSanitizer.Sanitize(exception)}"));
                        }
                    }
                },
                (session, token) => session.Api.SelectProxyAsync(intent.Group, intent.Proxy, token),
                cancellationToken,
                refreshScope: MutationRefreshScope.ProxySelection);

            if (disconnectException is not null)
            {
                const string message = "节点已切换，但未能断开旧连接。";
                _stateStore.Update(snapshot => snapshot with
                {
                    ErrorMessage = message,
                    Logs = _logBuffer.Snapshot()
                });
                Publish();
                throw new InvalidOperationException(message, disconnectException);
            }
        }

        return intent;
    }

    private SingleFlightOperation<int?> GetProxyDelayOperation(string proxy)
    {
        lock (_delayOperationGate)
        {
            if (!_proxyDelayOperations.TryGetValue(proxy, out SingleFlightOperation<int?>? operation))
            {
                if (!TrimIdleOperations(_proxyDelayOperations))
                {
                    throw new OperationBusyException("节点测速");
                }

                operation = new SingleFlightOperation<int?>("节点测速");
                _proxyDelayOperations[proxy] = operation;
            }

            return operation;
        }
    }

    private SingleFlightOperation<IReadOnlyDictionary<string, int?>> GetProxyGroupDelayOperation(string group)
    {
        lock (_delayOperationGate)
        {
            if (!_proxyGroupDelayOperations.TryGetValue(group, out SingleFlightOperation<IReadOnlyDictionary<string, int?>>? operation))
            {
                if (!TrimIdleOperations(_proxyGroupDelayOperations))
                {
                    throw new OperationBusyException("代理组测速");
                }

                operation = new SingleFlightOperation<IReadOnlyDictionary<string, int?>>("代理组测速");
                _proxyGroupDelayOperations[group] = operation;
            }

            return operation;
        }
    }

    private static bool TrimIdleOperations<T>(Dictionary<string, SingleFlightOperation<T>> operations)
    {
        const int MaxRetainedOperations = 256;
        if (operations.Count < MaxRetainedOperations)
        {
            return true;
        }

        string? idleKey = operations
            .FirstOrDefault(entry => !entry.Value.IsBusy)
            .Key;
        if (idleKey is not null)
        {
            operations.Remove(idleKey);
            return true;
        }

        return false;
    }

    private static bool TrimIdleLatestOperations(
        Dictionary<string, LatestWinsOperation<ProxySelectionIntent>> operations)
    {
        const int MaxRetainedOperations = 256;
        if (operations.Count < MaxRetainedOperations)
        {
            return true;
        }

        string? idleKey = operations
            .FirstOrDefault(entry => !entry.Value.IsBusy)
            .Key;
        if (idleKey is not null)
        {
            operations.Remove(idleKey);
            return true;
        }

        return false;
    }

    public Task<int?> TestProxyDelayAsync(string proxy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxy);
        SingleFlightOperation<int?> operation = GetProxyDelayOperation(proxy);
        return operation.RequestAsync(
            token => TestProxyDelayCoreAsync(proxy, token),
            cancellationToken);
    }

    private async Task<int?> TestProxyDelayCoreAsync(
        string proxy,
        CancellationToken operationCancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            _runtimeCts.Token);
        CancellationToken cancellationToken = linked.Token;
        ThrowIfRuntimeQuiescing();
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
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
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
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
            if (!await _remoteRefresh.RefreshSnapshotAsync(
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

    public Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayAsync(
        string group, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        SingleFlightOperation<IReadOnlyDictionary<string, int?>> operation = GetProxyGroupDelayOperation(group);
        return operation.RequestAsync(
            token => TestProxyGroupDelayCoreAsync(group, token),
            cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, int?>> TestProxyGroupDelayCoreAsync(
        string group,
        CancellationToken operationCancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            operationCancellationToken,
            _runtimeCts.Token);
        CancellationToken token = linked.Token;
        ThrowIfRuntimeQuiescing();
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
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
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(
                    "测速期间远程端点会话已切换，请重新测速。");
            }

            if (!await _remoteRefresh.RefreshSnapshotAsync(
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
            _stateStore.Update(snapshot => snapshot with
            {
                ProxyGroups = proxies.Groups.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray(),
                ProxyNodes = proxies.Nodes.Select(item => item with { Delay = LatestDelay(item.Name, item.Delay) }).ToArray()
            });
            Publish();
        }
        finally { _dataRefreshLock.Release(); }
        return delays;
    }

    public async Task CloseConnectionAsync(string id, CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接关闭结果无法确认，请重试。",
                (api, _, token) => api.CloseConnectionAsync(id, token),
                (session, token) => session.Api.CloseConnectionAsync(id, token),
                cancellationToken);
        }
    }

    public async Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接清理结果无法确认，请重试。",
                (api, _, token) => api.CloseAllConnectionsAsync(token),
                (session, token) => session.Api.CloseAllConnectionsAsync(token),
                cancellationToken);
        }
    }

    public async Task RefreshProviderAsync(string name, bool rules, CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
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
    }

    public void ClearLogs()
    {
        _logBuffer.Clear();
        _stateStore.Update(snapshot => snapshot with { Logs = [] });
        Publish();
    }

    public async Task ClearFakeIpCacheAsync(CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 FakeIP 缓存期间核心会话已切换，请重试。",
                "远程 FakeIP 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearFakeIpCacheAsync(token),
                (session, token) => session.Api.ClearFakeIpCacheAsync(token),
                cancellationToken);
        }
    }

    public async Task UpdateSettingsAsync(AppSettings settings, bool reconcileStartup = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSettings(settings);
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken);
        AppSettings previousSettings = _settings;
        bool networkSettingsChanged = settings.AllowLan != previousSettings.AllowLan
            || settings.Ipv6 != previousSettings.Ipv6;
        bool coreRestartRequired = RequiresCoreRestart(previousSettings, settings);
        bool systemProxyBindingChanged = HasSystemProxyBindingChanged(previousSettings, settings);
        bool systemProxyPreferenceChanged = settings.SystemProxyEnabled != previousSettings.SystemProxyEnabled;
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
            if (systemProxyPreferenceChanged)
            {
                Interlocked.Increment(ref _proxyIntentRevision);
            }

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
                await RestartCoreCoreAsync(operationLease, cancellationToken);
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
                    _stateStore.Update(snapshot => snapshot with
                    {
                        ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                        Logs = _logBuffer.Snapshot()
                    });
                    throw new InvalidOperationException("运行中网络设置应用失败，正在恢复旧设置。", exception);
                }
            }

            if (systemProxyBindingChanged)
            {
                await ReconcileSystemProxyAsync(
                    coreRunning: Snapshot.Core.State == CoreState.Running && CoreHealthConfirmed,
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
                    restartStarted,
                    operationLease);
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
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = message,
                Logs = _logBuffer.Snapshot()
            });
            Publish();

            if (failures.Length > 0)
            {
                throw new InvalidOperationException(
                    message,
                    new AggregateException(new[] { exception }.Concat(failures)));
            }

            throw;
        }
    }

    public Task SetSystemProxyAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SetSystemProxyCoreAsync(
            enabled,
            persistPreference: true,
            cancellationToken: cancellationToken,
            operationLease: null);

    private async Task SetSystemProxyCoreAsync(
        bool enabled,
        bool persistPreference,
        CancellationToken cancellationToken,
        OperationGate.Lease? operationLease = null)
    {
        OperationGate.Lease? ownedLease = null;
        if (operationLease is null)
        {
            ownedLease = await _operationLock.AcquireAsync(cancellationToken);
        }

        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.SystemProxyEnabled != enabled;
        try
        {
            if (persistPreference)
            {
                Interlocked.Increment(ref _proxyIntentRevision);
            }

            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { SystemProxyEnabled = enabled },
                    cancellationToken);
            }

            if (enabled && !CoreHealthConfirmed)
            {
                await RevokeSystemProxyForCoreLossAsync(operationLease ?? ownedLease!);
                _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState });
                Publish();
                return;
            }

            _stateStore.Update(snapshot => snapshot with { SystemProxy = enabled ? SystemProxyState.Enabling : SystemProxyState.Disabling });
            Publish();
            if (enabled)
            {
                await _localDevice.EnableSystemProxyAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
            }
            else
            {
                await _localDevice.DisableSystemProxyAsync(cancellationToken);
            }

            Interlocked.Increment(ref _proxyOwnershipRevision);
            _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState, ErrorMessage = null });
            Publish();
        }
        catch
        {
            Interlocked.Increment(ref _proxyOwnershipRevision);
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _stateStore.Update(snapshot => snapshot with { SystemProxy = _localDevice.SystemProxyState });
            Publish();
            throw;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

    public async Task SetTunAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await RequestTunOperationAsync(
                enabled,
                persistPreference: true,
                operationLease: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<TunState> RequestTunOperationAsync(
        bool enabled,
        bool persistPreference,
        OperationGate.Lease? operationLease,
        CancellationToken cancellationToken) =>
        _tunOperation.RequestAsync(
            enabled,
            (target, token) => SetTunCoreAsync(
                target,
                persistPreference,
                operationLease,
                token),
            cancellationToken);

    private async Task<TunState> SetTunCoreAsync(
        bool enabled,
        bool persistPreference,
        OperationGate.Lease? operationLease,
        CancellationToken cancellationToken)
    {
        OperationGate.Lease? ownedLease = null;
        if (operationLease is null)
        {
            ownedLease = _operationLock.TryAcquire();
            if (ownedLease is null)
            {
                throw new OperationBusyException("TUN");
            }
        }

        AppSettings previousSettings = _settings;
        bool preferenceChanged = persistPreference && previousSettings.TunEnabled != enabled;
        TunState? serviceResponseState = null;
        try
        {
            _stateStore.Update(snapshot => snapshot with { Tun = enabled ? TunState.Enabling : TunState.Disabling });
            Publish();
            ServiceTunPayload payload = new(
                _settings.ControllerPort,
                string.Empty,
                enabled);
            ServiceResponse response = await _localDevice.SetTunAsync(payload, cancellationToken)
                .ConfigureAwait(false);
            serviceResponseState = response.Tun;
            if (!response.Succeeded)
            {
                _confirmedTunState = response.Tun;
                _stateStore.Update(snapshot => snapshot with
                {
                    Tun = response.Tun,
                    ErrorMessage = response.Error ?? "TUN 操作失败。"
                });
                Publish();
                throw new ServiceCommandException(
                    response.ErrorCode,
                    response.Error ?? "TUN 操作失败。");
            }

            if (response.Tun != (enabled ? TunState.On : TunState.Off))
            {
                _confirmedTunState = response.Tun;
                _stateStore.Update(snapshot => snapshot with { Tun = response.Tun, ErrorMessage = response.Error });
                Publish();
                throw new ServiceCommandException(
                    response.ErrorCode == ServiceErrorCode.None
                        ? ServiceErrorCode.TunStateUnknown
                        : response.ErrorCode,
                    response.Error ?? "TUN 状态无法确认。");
            }

            if (preferenceChanged)
            {
                await SaveSettingsForOperationAsync(
                    previousSettings with { TunEnabled = enabled },
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            _confirmedTunState = response.Tun;
            _stateStore.Update(snapshot => snapshot with { Tun = response.Tun, ErrorMessage = null });
            Publish();
            return response.Tun;
        }
        catch (OperationCanceledException)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            TunState state = serviceResponseState is TunState confirmedState
                ? confirmedState
                : TunState.Unknown;
            _confirmedTunState = state;
            _stateStore.Update(snapshot => snapshot with
            {
                Tun = state,
                ErrorMessage = serviceResponseState is TunState
                    ? null
                    : "TUN 操作已取消，状态无法确认。"
            });
            Publish();
            throw;
        }
        catch (TimeoutException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (ServiceRequestUnknownException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Failed;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Failed, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 操作结果无法确认，请检查服务状态后重试。", exception);
        }
        catch (IOException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            _confirmedTunState = TunState.Unavailable;
            _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable, ErrorMessage = ErrorSanitizer.Sanitize(exception) });
            Publish();
            throw new InvalidOperationException("TUN 需要已安装并运行的 ClashTray 服务。", exception);
        }
        catch
        {
            if (preferenceChanged)
            {
                await RestoreSettingsAfterOperationFailureAsync(previousSettings);
            }

            if (serviceResponseState is TunState responseState)
            {
                _confirmedTunState = responseState;
                _stateStore.Update(snapshot => snapshot with
                {
                    Tun = responseState,
                    ErrorMessage = snapshot.ErrorMessage ?? "TUN 操作结果无法确认。"
                });
            }
            else
            {
                _confirmedTunState = TunState.Failed;
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Failed });
            }
            Publish();
            throw;
        }
        finally
        {
            ownedLease?.Dispose();
        }
    }

    public async Task ClearDnsCacheAsync(CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 DNS 缓存期间核心会话已切换，请重试。",
                "远程 DNS 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearDnsCacheAsync(token),
                (session, token) => session.Api.ClearDnsCacheAsync(token),
                cancellationToken);
        }
    }

    public async Task UpdateGeoAsync(CancellationToken cancellationToken = default)
    {
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.UpdateGeo,
                "更新 Geo 数据库期间核心会话已切换，请重试。",
                "远程 Geo 数据库更新结果无法确认，请重试。",
                (api, _, token) => api.UpdateGeoAsync(token),
                (session, token) => session.Api.UpdateGeoAsync(token),
                cancellationToken);
        }
    }

    public async Task<string> InstallCoreUpdateAsync(CoreUpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            bool wasRunning = Snapshot.Core.State == CoreState.Running;
            if (wasRunning)
            {
                await StopCoreCoreAsync(operationLease, cancellationToken);
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
                _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { Version = FindCoreVersion(), ErrorMessage = null }, ErrorMessage = null });
                Publish();
                if (wasRunning)
                {
                    await StartCoreCoreAsync(operationLease, cancellationToken);
                    if (!IsCoreHealthy())
                    {
                        await StopCoreCoreAsync(operationLease, CancellationToken.None);
                        await RollbackCoreUpdateAsync(CancellationToken.None);
                        rolledBack = true;
                        _stateStore.Update(snapshot => snapshot with
                        {
                            Core = snapshot.Core with
                            {
                                Version = FindCoreVersion(),
                                ErrorMessage = "新核心健康检查失败，已自动回滚。"
                            },
                            ErrorMessage = "新核心健康检查失败，已自动回滚。"
                        });
                        Publish();
                        await StartCoreCoreAsync(operationLease, CancellationToken.None);
                        if (!IsCoreHealthy())
                        {
                            throw new InvalidOperationException("核心更新失败，且回滚后的旧核心也未能恢复。");
                        }

                        AddApplicationLog(new LogEntry(
                            DateTimeOffset.UtcNow,
                            "ClashTray",
                            "warning",
                            "新核心健康检查失败，已自动回滚并恢复旧核心。"));
                        _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
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
                    if (Snapshot.Core.State == CoreState.Running || _api is not null || _usingServiceCore)
                    {
                        await StopCoreCoreAsync(operationLease, CancellationToken.None);
                    }

                    await RollbackCoreUpdateAsync(CancellationToken.None);
                    _stateStore.Update(snapshot => snapshot with
                    {
                        Core = snapshot.Core with { Version = FindCoreVersion() }
                    });
                    Publish();
                }

                if (wasRunning && !IsCoreHealthy())
                {
                    await StartCoreCoreAsync(operationLease, CancellationToken.None);
                }

                throw;
            }
        }
    }

    private bool IsCoreHealthy() =>
        Snapshot.Core.State == CoreState.Running && CoreHealthConfirmed;

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
        EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(
            EndpointCommand.ObserveStatus,
            "刷新远程端点数据期间会话已切换，请重试。");
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            if (!await _remoteRefresh.RefreshSnapshotAsync(
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
            await RefreshFromApiWithRetryAsync(cancellationToken);
        }
    }

    internal void AttachControllerForTesting(MihomoApiClient api, bool usingServiceCore)
    {
        ArgumentNullException.ThrowIfNull(api);
        SetController(api);
        _usingServiceCore = usingServiceCore;
        ConfirmCoreHealth(
            Volatile.Read(ref _coreLifecycleEpoch),
            _processManager.Generation,
            ControllerGeneration);
        _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { State = CoreState.Running } });
    }

    internal bool IsCoreHealthConfirmedForTesting => CoreHealthConfirmed;

    internal void SetCoreStateForTesting(CoreState state) => UpdateCoreState(state, null);

    internal Task RefreshControllerDataForTestingAsync(CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken);

    internal Task RefreshControllerDataForTestingAsync(
        bool includeRulesAndProviders,
        CancellationToken cancellationToken = default) =>
        RefreshFromApiAsync(cancellationToken, includeRulesAndProviders);

    internal async Task RevokeSystemProxyForTestingAsync()
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync();
        await RevokeSystemProxyForCoreLossAsync(operationLease);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> cleanupFailures = [];
        _operationLock.BeginQuiescing();
        Interlocked.Increment(ref _coreLifecycleEpoch);
        await _runtimeCts.CancelAsync().ConfigureAwait(false);

        _endpointSessions.StatusChanged -= _remoteRefresh.HandleSessionStatusChanged;
        _networkSwitchRuntimeController.StatusChanged -= OnNetworkSwitchStatusChanged;
        _processManager.StateChanged -= OnProcessStateChanged;
        _processManager.LogLineReceived -= OnProcessLogLine;

        await RunCleanupStepAsync(
            cleanupFailures,
            "停止远程端点刷新",
            _ => _remoteRefresh.StopRefreshAsync());
        await RunCleanupStepAsync(
            cleanupFailures,
            "释放端点会话",
            _ => _endpointSessions.DisposeAsync().AsTask());
        await RunCleanupStepAsync(
            cleanupFailures,
            "释放网络切换运行时",
            _ => _networkSwitchRuntimeController.DisposeAsync().AsTask());
        await RunCleanupStepAsync(
            cleanupFailures,
            "停止订阅调度器",
            _ => _subscriptionScheduler.DisposeAsync().AsTask());

        {
            using OperationGate.Lease cleanupLease = await _operationLock.AcquireCleanupOwnershipAsync(CancellationToken.None)
                .ConfigureAwait(false);

            await RunCleanupStepAsync(
                cleanupFailures,
                "等待 TUN 操作安全点",
                token => _tunOperation.WaitForIdleAsync(DisposeCleanupTimeout, token));

            bool usingServiceCore = _usingServiceCore;
            await RunCleanupStepAsync(
                cleanupFailures,
                "关闭 TUN",
                async token =>
                {
                    if (!usingServiceCore || Snapshot.Tun is TunState.Off or TunState.Unavailable)
                    {
                        return;
                    }

                    await RequestTunOperationAsync(
                            enabled: false,
                            persistPreference: false,
                            operationLease: cleanupLease,
                            cancellationToken: token)
                        .ConfigureAwait(false);
                });

            await RunCleanupStepAsync(
                cleanupFailures,
                "恢复系统代理",
                async token =>
                {
                    if (Snapshot.SystemProxy is SystemProxyState.On or SystemProxyState.RestoreRequired)
                    {
                        await SetSystemProxyCoreAsync(
                                false,
                                persistPreference: false,
                                cancellationToken: token,
                                operationLease: cleanupLease)
                            .ConfigureAwait(false);
                    }
                });

            await RunCleanupStepAsync(
                cleanupFailures,
                "停止核心",
                async token =>
                {
                    if (usingServiceCore
                        || Snapshot.Core.State is CoreState.Running
                            or CoreState.Starting
                            or CoreState.Stopping
                            or CoreState.Restarting
                        || _api is not null)
                    {
                        await StopCoreCoreAsync(cleanupLease, token).ConfigureAwait(false);
                    }
                });
        }

        SetController(null);
        await RunCleanupStepAsync(
            cleanupFailures,
            "停止日志流",
            _ => StopLogStreamAsync());
        await RunCleanupStepAsync(
            cleanupFailures,
            "等待数据刷新",
            token => AwaitTaskBoundedAsync(_dataRefreshTask, token));
        await RunCleanupStepAsync(
            cleanupFailures,
            "等待轮询",
            token => AwaitTaskBoundedAsync(_pollingTask, token));
        await RunCleanupStepAsync(
            cleanupFailures,
            "等待系统代理恢复",
            _ => AwaitQueuedProxyRecoveryAsync());
        await RunCleanupStepAsync(
            cleanupFailures,
            "释放核心进程管理器",
            _ => _processManager.DisposeAsync().AsTask());
        await RunCleanupStepAsync(
            cleanupFailures,
            "释放配置切换协调器",
            _ => _configurationSwitchCoordinator.DisposeAsync().AsTask());
        await RunCleanupStepAsync(
            cleanupFailures,
            "停止发布 worker",
            _ => _throttledPublisher.DisposeAsync().AsTask());

        _logStreamCts?.Dispose();
        _logStreamCts = null;
        _remoteRefresh.Dispose();

        if (cleanupFailures.Count > 0)
        {
            string message = $"ClashTray 退出清理有 {cleanupFailures.Count} 项未能确认，已尽力释放其余资源。";
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = message,
                Logs = _logBuffer.Snapshot()
            });
            try
            {
                Publish();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        _httpClient.Dispose();
        _subscriptionOperationLock.Dispose();
        _dataRefreshLock.Dispose();
        _operationLock.Dispose();
        _runtimeCts.Dispose();
    }

    private async Task RunCleanupStepAsync(
        ICollection<Exception> failures,
        string operationName,
        Func<CancellationToken, Task> operation)
    {
        using CancellationTokenSource timeout = new(DisposeCleanupTimeout);
        try
        {
            await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(failures, operationName, exception);
        }
    }

    private void RecordCleanupFailure(
        ICollection<Exception> failures,
        string operationName,
        Exception exception)
    {
        Exception sanitized = new InvalidOperationException(
            $"{operationName}：{ErrorSanitizer.Sanitize(exception)}",
            exception);
        failures.Add(sanitized);
        AddApplicationLog(new LogEntry(
            DateTimeOffset.UtcNow,
            "ClashTray",
            "error",
            sanitized.Message));
    }

    private static async Task AwaitTaskBoundedAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
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

        bool coreHealthWasUnconfirmed = !CoreHealthConfirmed;
        await RefreshCoreHealthAsync(api, cancellationToken);
        await RefreshOptionalDataAsync(
            api,
            cancellationToken,
            includeRulesAndProviders || coreHealthWasUnconfirmed);
    }

    private async Task RefreshCoreHealthAsync(MihomoApiClient api, CancellationToken cancellationToken)
    {
        long lifecycleEpoch = Volatile.Read(ref _coreLifecycleEpoch);
        long processGeneration = _processManager.Generation;
        long controllerGeneration = ControllerGeneration;
        using JsonDocument version = await api.GetVersionAsync(cancellationToken);
        string? versionText = MihomoDataParser.ParseVersion(version);
        using JsonDocument configurationState = await api.GetConfigurationAsync(force: false, cancellationToken);
        ProxyMode? mode = MihomoDataParser.ParseMode(configurationState);
        bool? tunEnabled = MihomoDataParser.ParseTunEnabled(configurationState);

        TunState observedTun = ResolveConfirmedTunState(tunEnabled);
        bool committed = _stateStore.TryUpdate(
            snapshot => snapshot.Core.State == CoreState.Running
                && IsCurrentCoreBinding(
                    api,
                    controllerGeneration,
                    lifecycleEpoch,
                    processGeneration),
            snapshot => snapshot with
            {
                Core = snapshot.Core with
                {
                    State = CoreState.Running,
                    Version = versionText ?? snapshot.Core.Version,
                    Mode = mode ?? snapshot.Core.Mode,
                    ErrorMessage = null
                },
                Logs = _logBuffer.Snapshot(),
                Tun = observedTun,
                ErrorMessage = null
            },
            out _);
        if (!committed)
        {
            return;
        }

        ConfirmCoreHealth(lifecycleEpoch, processGeneration, controllerGeneration);
        if (!CoreHealthConfirmed)
        {
            return;
        }

        Publish();
        EnsureLogStreamStarted();
    }

    private TunState ResolveConfirmedTunState(bool? controllerEnabled)
    {
        if (controllerEnabled is false)
        {
            return _confirmedTunState is TunState.Off or TunState.Unavailable
                ? _confirmedTunState
                : TunState.Unknown;
        }

        // The service is the only writer. A controller boolean by itself is
        // deliberately represented as Unknown until the service has also
        // confirmed the Windows network probe for the same process.
        if (controllerEnabled is true && _confirmedTunState == TunState.On)
        {
            return TunState.On;
        }

        return _confirmedTunState == TunState.Unavailable
            ? TunState.Unavailable
            : TunState.Unknown;
    }

    private TunState AdoptServiceTunState(TunState state)
    {
        _confirmedTunState = state switch
        {
            TunState.On => TunState.On,
            TunState.Off => TunState.Off,
            _ => state
        };
        return state;
    }

    private async Task RefreshOptionalDataAsync(
        MihomoApiClient api,
        CancellationToken cancellationToken,
        bool includeRulesAndProviders = true)
    {
        long lifecycleEpoch = Volatile.Read(ref _coreLifecycleEpoch);
        long processGeneration = _processManager.Generation;
        long controllerGeneration = ControllerGeneration;
        await _dataRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentCoreBinding(api, controllerGeneration, lifecycleEpoch, processGeneration))
            {
                return;
            }

            Task<ProxyDataResult> proxyTask = TryGetProxyDataAsync(api, cancellationToken);
            Task<TrafficDataResult> trafficTask = TryGetTrafficSnapshotAsync(api, cancellationToken);
            Task<MemoryDataResult> memoryTask = TryGetMemoryAsync(api, cancellationToken);
            Task<ConnectionDataResult> connectionsTask = TryGetConnectionDataAsync(api, cancellationToken);
            Task<IReadOnlyList<RuleInfo>> rulesTask = includeRulesAndProviders
                ? TryGetRulesAsync(api, cancellationToken)
                : Task.FromResult<IReadOnlyList<RuleInfo>>([]);
            Task<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)> providersTask =
                includeRulesAndProviders
                    ? TryGetProvidersAsync(api, cancellationToken)
                    : Task.FromResult<(IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders)>(
                        ([], []));
            await Task.WhenAll(proxyTask, trafficTask, memoryTask, connectionsTask, rulesTask, providersTask);

            if (!IsCurrentCoreBinding(api, controllerGeneration, lifecycleEpoch, processGeneration))
            {
                return;
            }

            ProxyDataResult proxyData = await proxyTask;
            TrafficDataResult trafficData = await trafficTask;
            MemoryDataResult memoryData = await memoryTask;
            ConnectionDataResult connectionData = await connectionsTask;
            IReadOnlyList<RuleInfo> rulesData = await rulesTask;
            (IReadOnlyList<ProviderStatus> Providers, IReadOnlyList<ProviderStatus> RuleProviders) providerData = await providersTask;
            TrafficSnapshot? traffic = trafficData.Value;
            bool committed = _stateStore.TryUpdate(
                snapshot => snapshot.Core.State == CoreState.Running
                    && IsCurrentCoreBinding(
                        api,
                        controllerGeneration,
                        lifecycleEpoch,
                        processGeneration),
                snapshot =>
                {
                    CoreStatus currentCore = snapshot.Core;
                    return snapshot with
                    {
                        Core = currentCore with
                        {
                            UploadBytes = traffic?.UploadBytes ?? currentCore.UploadBytes,
                            DownloadBytes = traffic?.DownloadBytes ?? currentCore.DownloadBytes,
                            UploadBytesPerSecond = traffic?.UploadBytesPerSecond ?? currentCore.UploadBytesPerSecond,
                            DownloadBytesPerSecond = traffic?.DownloadBytesPerSecond ?? currentCore.DownloadBytesPerSecond,
                            TrafficAvailable = trafficData.Succeeded,
                            ConnectionCount = connectionData.Succeeded
                                ? connectionData.Value.Count
                                : currentCore.ConnectionCount,
                            MemoryBytes = memoryData.Value,
                            MemoryAvailable = memoryData.Succeeded
                        },
                        ProxyGroups = proxyData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyGroups, proxyData.Groups, SnapshotDataComparer.ProxyGroupsEqual)
                            : snapshot.ProxyGroups,
                        ProxyNodes = proxyData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyNodes, proxyData.Nodes, SnapshotDataComparer.ProxyNodesEqual)
                            : snapshot.ProxyNodes,
                        Connections = connectionData.Succeeded
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Connections,
                                connectionData.Value,
                                EqualityComparer<ConnectionInfo>.Default.Equals)
                            : snapshot.Connections,
                        Rules = includeRulesAndProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Rules,
                                rulesData,
                                EqualityComparer<RuleInfo>.Default.Equals)
                            : snapshot.Rules,
                        Providers = includeRulesAndProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.Providers,
                                providerData.Providers,
                                EqualityComparer<ProviderStatus>.Default.Equals)
                            : snapshot.Providers,
                        RuleProviders = includeRulesAndProviders
                            ? SnapshotDataComparer.ReuseIfEqual(
                                snapshot.RuleProviders,
                                providerData.RuleProviders,
                                EqualityComparer<ProviderStatus>.Default.Equals)
                            : snapshot.RuleProviders,
                        Logs = _logBuffer.Snapshot()
                    };
                },
                out _);
            if (committed)
            {
                await _throttledPublisher.RequestAsync(cancellationToken);
            }
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    private async Task RefreshFromApiWithRetryAsync(CancellationToken cancellationToken)
    {
        await RefreshCoreHealthWithRetryAsync(cancellationToken);
        await ApplyProgramOverridesWithLeaseAsync(coreRunning: true, cancellationToken);
        MihomoApiClient? api = _api;
        if (api is not null)
        {
            await RefreshOptionalDataAsync(api, cancellationToken);
        }
    }

    private async Task RefreshModeSnapshotAsync(
        MihomoApiClient api,
        long generation,
        CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureControllerSession(api, generation, "模式刷新期间核心会话已切换，请重试。");
            using JsonDocument configuration = await api.GetConfigurationAsync(
                force: false,
                cancellationToken);
            EnsureControllerSession(api, generation, "模式刷新期间核心会话已切换，请重试。");
            ProxyMode? mode = MihomoDataParser.ParseMode(configuration);
            if (mode is not ProxyMode confirmedMode)
            {
                throw new InvalidOperationException("Mihomo 未返回可识别的代理模式。");
            }

            _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { Mode = confirmedMode } });
            Publish();
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

    private async Task RefreshProxySelectionSnapshotAsync(
        MihomoApiClient api,
        long generation,
        CancellationToken cancellationToken)
    {
        await _dataRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureControllerSession(api, generation, "节点刷新期间核心会话已切换，请重试。");
            using JsonDocument proxies = await api.GetProxiesAsync(cancellationToken);
            EnsureControllerSession(api, generation, "节点刷新期间核心会话已切换，请重试。");
            (IReadOnlyList<ProxyGroup> groups, IReadOnlyList<ProxyNode> nodes) = MihomoDataParser.ParseProxies(proxies);
            _stateStore.Update(snapshot => snapshot with
            {
                ProxyGroups = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyGroups, groups, SnapshotDataComparer.ProxyGroupsEqual),
                ProxyNodes = SnapshotDataComparer.ReuseIfEqual(snapshot.ProxyNodes, nodes, SnapshotDataComparer.ProxyNodesEqual)
            });
            Publish();
        }
        finally
        {
            _dataRefreshLock.Release();
        }
    }

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

                if (_usingServiceCore && _api is null)
                {
                    ServiceResponse pendingStatus = await _localDevice.GetStatusAsync(_runtimeCts.Token)
                        .ConfigureAwait(false);
                    _stateStore.Update(snapshot => snapshot with
                    {
                        Tun = AdoptServiceTunState(pendingStatus.Tun)
                    });
                    if (pendingStatus.Core != CoreState.Running)
                    {
                        const string pendingMessage =
                            "服务核心启动结果仍未确认，正在等待后台状态收敛。";
                        _stateStore.Update(snapshot => snapshot with
                        {
                            Core = snapshot.Core with
                            {
                                State = CoreState.Failed,
                                ErrorMessage = pendingMessage
                            },
                            ErrorMessage = pendingMessage
                        });
                        Publish();
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    SetController(CreateApiClient());
                    SetCoreRunningPendingHealth(pendingStatus.Tun);
                }

                await RefreshFromApiAsync(
                    _runtimeCts.Token,
                    includeRulesAndProviders: false);
                await ApplyProgramOverridesWithLeaseAsync(
                    coreRunning: true,
                    cancellationToken: _runtimeCts.Token);
                retryDelay = TimeSpan.FromSeconds(2);
                retryCount = 0;
            }
            catch (OperationCanceledException) when (_runtimeCts.IsCancellationRequested)
            {
                break;
            }
            catch (RuntimeQuiescingException) when (_runtimeCts.IsCancellationRequested)
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
                        await RevokeSystemProxyForCoreLossWithLeaseAsync();
                        MarkCoreHealthUnconfirmed("轮询", exception, retryCount);
                        retryDelay = IncreaseRetryDelay(retryDelay);
                        continue;
                    }

                    // Polling can be the first observer of an exited process when
                    // the exit event was missed; commit the CoreLost fact instead
                    // of silently abandoning the loop on a stale Running snapshot.
                    bool unexpectedCoreLost = Snapshot.Core.State is CoreState.Running or CoreState.Starting;
                    if (unexpectedCoreLost)
                    {
                        SetController(null);
                    }

                    await StopLogStreamAsync();
                    UpdateCoreState(CoreState.Failed, "Mihomo 进程已退出", unexpectedCoreLost);
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
                    await RevokeSystemProxyForCoreLossWithLeaseAsync();
                    _confirmedTunState = TunState.Unavailable;
                    _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                    MarkCoreHealthUnconfirmed("服务重连", serviceException ?? exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                if (serviceStatus.Core == CoreState.Running)
                {
                    SetController(CreateApiClient());
                    await RevokeSystemProxyForCoreLossWithLeaseAsync();
                    _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(serviceStatus.Tun) });
                    MarkCoreHealthUnconfirmed("控制器重连", exception, retryCount);
                    retryDelay = IncreaseRetryDelay(retryDelay);
                    continue;
                }

                SetController(null);
                await StopLogStreamAsync();
                await RevokeSystemProxyForCoreLossWithLeaseAsync();
                _stateStore.Update(snapshot => snapshot with { Tun = AdoptServiceTunState(serviceStatus.Tun) });
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
            _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
            Publish();
        }
    }

    private MihomoApiClient CreateApiClient()
    {
        if (_controllerApiFactory is not null)
        {
            return _controllerApiFactory();
        }

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

    private bool IsCurrentCoreBinding(
        MihomoApiClient api,
        long controllerGeneration,
        long lifecycleEpoch,
        long processGeneration) =>
        !_runtimeCts.IsCancellationRequested
        && lifecycleEpoch == Volatile.Read(ref _coreLifecycleEpoch)
        && processGeneration == _processManager.Generation
        && _controllerSessions.IsCurrent(api, controllerGeneration);

    private async Task ExecuteControllerMutationAndRefreshAsync(
        EndpointCommand command,
        string staleSessionMessage,
        string remoteRefreshFailureMessage,
        Func<MihomoApiClient, long, CancellationToken, Task> localOperation,
        Func<EndpointSession, CancellationToken, Task> remoteOperation,
        CancellationToken cancellationToken,
        bool routeToRemote = true,
        bool includeRulesAndProviders = true,
        MutationRefreshScope refreshScope = MutationRefreshScope.Full)
    {
        ArgumentNullException.ThrowIfNull(localOperation);
        ArgumentNullException.ThrowIfNull(remoteOperation);

        EndpointSession? remoteSession = routeToRemote
            ? _remoteRefresh.CaptureActiveRemoteSession(command, staleSessionMessage)
            : null;
        if (remoteSession is not null)
        {
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            await remoteOperation(remoteSession, cancellationToken).ConfigureAwait(false);
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

            if (!await _remoteRefresh.RefreshMutationSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    refreshScope,
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
        if (refreshScope == MutationRefreshScope.Mode)
        {
            await RefreshModeSnapshotAsync(api, generation, cancellationToken).ConfigureAwait(false);
        }
        else if (refreshScope == MutationRefreshScope.ProxySelection)
        {
            await RefreshProxySelectionSnapshotAsync(api, generation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RefreshFromApiAsync(
                    cancellationToken,
                    includeRulesAndProviders)
                .ConfigureAwait(false);
        }
        EnsureControllerSession(api, generation, staleSessionMessage);
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
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        await ApplyProgramOverridesCoreAsync(coreRunning, operationLease, cancellationToken);
    }

    private async Task ApplyProgramOverridesWithLeaseAsync(
        bool coreRunning,
        CancellationToken cancellationToken)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await ApplyProgramOverridesAsync(coreRunning, operationLease, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyProgramOverridesCoreAsync(
        bool coreRunning,
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        await ApplyControllerProgramOverridesAsync(coreRunning, operationLease, cancellationToken);
        await ApplyLocalDeviceProgramOverridesAsync(coreRunning, cancellationToken);
    }

    private async Task ApplyControllerProgramOverridesAsync(
        bool coreRunning,
        OperationGate.Lease operationLease,
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
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = $"程序局域网/IPv6 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logBuffer.Snapshot()
            });
            Publish();
        }

        try
        {
            await ApplyProgramTunPreferenceAsync(operationLease, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogControllerFailure("程序 TUN 设置覆盖", "/configs", exception, 0);
            _stateStore.Update(snapshot => snapshot with
            {
                ErrorMessage = $"程序 TUN 设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logBuffer.Snapshot()
            });
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
            AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}"));
            _stateStore.Update(snapshot => snapshot with
            {
                SystemProxy = _localDevice.SystemProxyState,
                ErrorMessage = $"程序系统代理设置应用失败：{ErrorSanitizer.Sanitize(exception)}",
                Logs = _logBuffer.Snapshot()
            });
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
                AddApplicationLog(new LogEntry(
                    DateTimeOffset.UtcNow,
                    "ClashTray",
                    "error",
                    "程序局域网/IPv6 设置应用失败，且无法恢复核心原始设置。"));
            }

            throw;
        }
    }

    private async Task ApplyProgramTunPreferenceAsync(
        OperationGate.Lease operationLease,
        CancellationToken cancellationToken)
    {
        if (!_usingServiceCore)
        {
            _confirmedTunState = TunState.Unavailable;
            if (Snapshot.Tun != TunState.Unavailable)
            {
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Unavailable });
                Publish();
            }

            return;
        }

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
        if (current is not bool currentValue)
        {
            return;
        }

        TunState expectedConfirmedState = currentValue ? TunState.On : TunState.Off;
        if (currentValue == _settings.TunEnabled
            && _confirmedTunState == expectedConfirmedState)
        {
            _confirmedTunState = currentValue ? TunState.On : TunState.Off;
            if (!currentValue && ReferenceEquals(_api, api) && Snapshot.Tun is not (TunState.Enabling or TunState.Disabling))
            {
                _stateStore.Update(snapshot => snapshot with { Tun = TunState.Off });
                Publish();
            }

            return;
        }

        // TUN writes belong exclusively to the service. The desktop process
        // may observe /configs here, but it never PATCHes the controller.
        await RequestTunOperationAsync(
                _settings.TunEnabled,
                persistPreference: false,
                operationLease: operationLease,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReconcileSystemProxyAsync(bool coreRunning, CancellationToken cancellationToken)
    {
        if (_settings.SystemProxyEnabled && coreRunning && CoreHealthConfirmed)
        {
            if (_localDevice.SystemProxyState is (SystemProxyState.Off or SystemProxyState.Failed))
            {
                await _localDevice.EnableSystemProxyAsync(_settings.MixedPort, _settings.BypassList, cancellationToken);
                Interlocked.Increment(ref _proxyOwnershipRevision);
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
                Interlocked.Increment(ref _proxyOwnershipRevision);
            }
        }

        SystemProxyState state = _localDevice.SystemProxyState;
        if (Snapshot.SystemProxy != state)
        {
            _stateStore.Update(snapshot => snapshot with { SystemProxy = state });
            Publish();
        }
    }

    private async Task RollbackSettingsChangeAsync(
        AppSettings previousSettings,
        bool coreRestartRequired,
        bool coreWasRunning,
        bool networkSettingsChanged,
        bool systemProxyBindingChanged,
        bool restartStarted,
        OperationGate.Lease operationLease)
    {
        _settings = previousSettings;
        await _settingsStore.SaveAsync(previousSettings, CancellationToken.None);

        if (restartStarted)
        {
            await RestartCoreCoreAsync(operationLease, CancellationToken.None);
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
                coreRunning: Snapshot.Core.State == CoreState.Running && CoreHealthConfirmed,
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
                await ControllerLogStreamReceiver.ReceiveLogMessagesAsync(
                    socket,
                    "mihomo",
                    AddMihomoLog,
                    cancellationToken);
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
            return new ProxyDataResult(false, Snapshot.ProxyGroups, Snapshot.ProxyNodes);
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
            return new MemoryDataResult(false, Snapshot.Core.MemoryBytes);
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
            return new ConnectionDataResult(false, Snapshot.Connections);
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
            return Snapshot.Rules;
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
            return (Snapshot.Providers, Snapshot.RuleProviders);
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
        Snapshot.Configurations.FirstOrDefault(configuration => configuration.IsActive)
        ?? Snapshot.Configurations.FirstOrDefault(configuration => configuration.Id == _settings.ActiveConfigurationId);

    private NetworkSwitchPolicyInput CreateNetworkSwitchPolicyInput(
        NetworkContextSnapshot context,
        NetworkSwitchRuleSet rules) =>
        new(
            rules.AutomaticSwitchingEnabled,
            context,
            rules.Rules,
            rules.DefaultConfigurationId,
            GetActiveConfiguration()?.Id,
            Snapshot.Configurations
                .Select(configuration => configuration.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private void OnNetworkSwitchStatusChanged(object? sender, NetworkSwitchStatus status)
    {
        _stateStore.Update(snapshot => snapshot with { NetworkSwitch = status });
        Publish();
    }

    private string? FindCoreVersion()
    {
        string? path = _coreDiscovery.FindExecutable();
        return path is null ? null : CoreDiscovery.GetVersion(path);
    }

    private void SetCoreRunningPendingHealth(TunState tunState)
    {
        InvalidateCoreHealth();
        TunState observedTun = AdoptServiceTunState(tunState);
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with
            {
                State = CoreState.Running,
                ErrorMessage = null,
                TrafficAvailable = false,
                MemoryAvailable = false
            },
            Tun = observedTun,
            ErrorMessage = null
        });
        Publish();
    }

    private void MarkCoreHealthUnconfirmed(string phase, Exception exception, int retryCount = 0)
    {
        InvalidateCoreHealth();
        string message = $"核心状态暂时无法确认（{phase}：{DescribeControllerError(exception)}）。";
        LogControllerFailure(phase, "/version 或 /configs", exception, retryCount);
        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = CoreState.Running, ErrorMessage = message },
            ErrorMessage = message,
            Logs = _logBuffer.Snapshot()
        });
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
        AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "warning", message));
    }

    private static string DescribeControllerError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
        MihomoStreamException streamException => $"{streamException.Path} {streamException.Kind}",
        TimeoutException => "首条记录超时",
        OperationCanceledException => "已取消",
        _ => exception.GetType().Name
    };

    private void OnProcessStateChanged(object? sender, CoreState state)
    {
        bool unexpectedCoreLost = state == CoreState.Failed
            && Snapshot.Core.State is CoreState.Running or CoreState.Starting
            && !_runtimeCts.IsCancellationRequested;
        if (unexpectedCoreLost)
        {
            // Invalidate the controller binding before publishing CoreLost. Any
            // health response already in flight now fails the generation check.
            SetController(null);
        }

        UpdateCoreState(
            state,
            state == CoreState.Failed ? "Mihomo 进程已退出" : null,
            unexpectedCoreLost);
    }

    private void QueueSystemProxyRecovery(CoreLossContext context)
    {
        if (_runtimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_proxyRecoveryGate)
        {
            if (_proxyRecoveryTask is { IsCompleted: false })
            {
                return;
            }

            _proxyRecoveryTask = Task.Run(() => RevokeSystemProxyForCoreLossWithLeaseAsync(
                context,
                CancellationToken.None));
        }
    }

    private async Task RevokeSystemProxyForCoreLossAsync(
        OperationGate.Lease operationLease,
        CoreLossContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (context is { } initialContext && !CanApplyCoreLossRecovery(initialContext))
        {
            return;
        }

        if (context is { } committedContext && !CanApplyCoreLossRecovery(committedContext))
        {
            return;
        }

        try
        {
            await ReconcileSystemProxyAsync(coreRunning: false, cancellationToken);
        }
        catch (Exception exception)
        {
            AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "error",
                $"核心不可用时撤销系统代理失败：{ErrorSanitizer.Sanitize(exception)}"));
            _stateStore.Update(snapshot => snapshot with
            {
                SystemProxy = _localDevice.SystemProxyState,
                ErrorMessage = "核心不可用时撤销系统代理失败，系统代理状态需要恢复。",
                Logs = _logBuffer.Snapshot()
            });
            Publish();
        }
    }

    private async Task RevokeSystemProxyForCoreLossWithLeaseAsync(
        CoreLossContext? context = null,
        CancellationToken cancellationToken = default)
    {
        using OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await RevokeSystemProxyForCoreLossAsync(operationLease, context, cancellationToken).ConfigureAwait(false);
    }

    private bool CanApplyCoreLossRecovery(CoreLossContext context) =>
        context.LifecycleEpoch == Volatile.Read(ref _coreLifecycleEpoch)
        && context.ProcessGeneration == _processManager.Generation
        && context.ControllerGeneration == ControllerGeneration
        && context.ProxyOwnershipRevision == Volatile.Read(ref _proxyOwnershipRevision)
        && context.ProxyIntentRevision == Volatile.Read(ref _proxyIntentRevision)
        && !context.CoreHealthConfirmed
        && Snapshot.Core.State != CoreState.Running
        && !_runtimeCts.IsCancellationRequested;

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
        catch (Exception exception)
        {
            AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "error",
                $"等待系统代理恢复任务失败：{ErrorSanitizer.Sanitize(exception)}"));
            Publish();
        }
    }

    private void UpdateCoreState(CoreState state, string? error, bool unexpectedCoreLost = false)
    {
        if (state != CoreState.Running)
        {
            InvalidateCoreHealth();
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _stateStore.Update(snapshot => snapshot with
        {
            Core = snapshot.Core with { State = state, ErrorMessage = error },
            ErrorMessage = error,
            Logs = _logBuffer.Snapshot()
        });
        Publish();
        if (unexpectedCoreLost)
        {
            QueueSystemProxyRecovery(new CoreLossContext(
                Volatile.Read(ref _coreLifecycleEpoch),
                _processManager.Generation,
                ControllerGeneration,
                Volatile.Read(ref _proxyOwnershipRevision),
                Volatile.Read(ref _proxyIntentRevision),
                CoreHealthConfirmed));
        }
    }

    private void UpdateSubscriptionState(SubscriptionState state, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            AddApplicationLog(new LogEntry(DateTimeOffset.UtcNow, "ClashTray", "error", error));
        }

        _stateStore.Update(snapshot => snapshot with { Subscription = state, ErrorMessage = error, Logs = _logBuffer.Snapshot() });
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
        lock (_publishGate)
        {
            RuntimeSnapshot snapshot = Snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
            AppSnapshotChanged?.Invoke(this, ComposeAppSnapshot(snapshot));
        }
    }

    private void PublishAppSnapshot()
    {
        lock (_publishGate)
        {
            RuntimeSnapshot snapshot = Snapshot;
            AppSnapshotChanged?.Invoke(this, ComposeAppSnapshot(snapshot));
        }
    }

    private AppSnapshot ComposeAppSnapshot(RuntimeSnapshot snapshot) => AppSnapshotComposer.Compose(
        snapshot,
        _settings,
        Endpoints,
        activeController: _remoteRefresh.BuildActiveControllerSnapshot(),
        controllerGeneration: ControllerGeneration);

    private void OnProcessLogLine(string line, bool isError)
    {
        AddMihomoLog(new LogEntry(DateTimeOffset.UtcNow, "mihomo", isError ? "error" : "info", line));
    }

    private void AddMihomoLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
        _throttledPublisher.Queue();
    }

    private void AddApplicationLog(LogEntry entry)
    {
        _logBuffer.Add(entry);
        _stateStore.Update(snapshot => snapshot with { Logs = _logBuffer.Snapshot() });
    }

    private bool IsCoreRunningForSettings() =>
        Snapshot.Core.State == CoreState.Running
        || _api is not null
        || _usingServiceCore;

    private bool CoreHealthConfirmed =>
        Volatile.Read(ref _confirmedCoreLifecycleEpoch)
            == Volatile.Read(ref _coreLifecycleEpoch)
        && Volatile.Read(ref _confirmedCoreProcessGeneration)
            == _processManager.Generation
        && Volatile.Read(ref _confirmedControllerGeneration)
            == ControllerGeneration
        && _controllerSessions.Current is not null;

    private void ConfirmCoreHealth(
        long lifecycleEpoch,
        long processGeneration,
        long controllerGeneration)
    {
        Volatile.Write(ref _confirmedCoreProcessGeneration, processGeneration);
        Volatile.Write(ref _confirmedControllerGeneration, controllerGeneration);
        Volatile.Write(ref _confirmedCoreLifecycleEpoch, lifecycleEpoch);
    }

    private void InvalidateCoreHealth()
    {
        Volatile.Write(ref _confirmedCoreLifecycleEpoch, long.MinValue);
        Volatile.Write(ref _confirmedCoreProcessGeneration, long.MinValue);
        Volatile.Write(ref _confirmedControllerGeneration, long.MinValue);
    }

    private void ThrowIfRuntimeQuiescing()
    {
        if (_operationLock.IsQuiescing)
        {
            throw new RuntimeQuiescingException();
        }
    }

    private static bool RequiresCoreRestart(AppSettings previous, AppSettings next) =>
        previous.HttpPort != next.HttpPort
        || previous.SocksPort != next.SocksPort
        || previous.MixedPort != next.MixedPort
        || previous.ControllerPort != next.ControllerPort
        || previous.TcpConcurrent != next.TcpConcurrent
        || !string.Equals(previous.TunStack, next.TunStack, StringComparison.OrdinalIgnoreCase)
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
        private OperationGate.Lease? _activeLease;

        public RuntimeConfigurationSwitchOperations(ClashTrayRuntime runtime)
        {
            _runtime = runtime;
        }

        internal OperationGate.Lease? ActiveLease
        {
            get => _activeLease;
            set => _activeLease = value;
        }

        private OperationGate.Lease RequireLease() =>
            _activeLease ?? throw new InvalidOperationException("配置切换未在操作锁内执行。");

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
                _runtime.Snapshot.Core.State == CoreState.Running,
                _runtime._settings.SystemProxyEnabled,
                _runtime.Snapshot.SystemProxy,
                _runtime._settings.TunEnabled,
                _runtime.Snapshot.Tun,
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
                await _runtime.StopCoreCoreAsync(RequireLease(), cancellationToken);
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
                await _runtime.StartCoreCoreAsync(RequireLease(), cancellationToken);
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
                await _runtime.RestartCoreCoreAsync(RequireLease(), cancellationToken);
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


using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime : IAsyncDisposable
{
    private readonly OperationGate _operationLock = new();
    private readonly BooleanSingleFlight<TunState> _tunOperation = new(
        "TUN",
        cancelWhenNoWaiters: false);
    private readonly SemaphoreSlim _subscriptionOperationLock = new(1, 1);
    private readonly SemaphoreSlim _dataRefreshLock = new(1, 1);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "If a bounded shutdown step remains active, this token source stays alive until its task completes.")]
    private readonly CancellationTokenSource _runtimeCts = new();
    private readonly object _disposeGate = new();
    private readonly object _publishGate = new();
    private readonly HttpClient _httpClient;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "Released only after shutdown workers settle; timed out workers retain the runtime and its resources.")]
    private readonly SnapshotPublishThrottle _throttledPublisher;
    private readonly AppPaths _paths;
    private readonly LocalCoreShutdownJournal _localCoreShutdownJournal;
    private readonly ConfigurationStore _configurationStore;
    private readonly EndpointTransportOptionsResolver _endpointTransportOptionsResolver;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "The session manager is released only after admitted work drains; an unresponsive shutdown intentionally retains it.")]
    private readonly EndpointSessionManager _endpointSessions;
    private readonly EndpointCatalogCoordinator _endpointCatalog;
    private readonly NetworkRuleStore _networkRuleStore;
    private readonly ConfigurationSwitchJournalStore _configurationSwitchJournalStore;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "Released only after shutdown workers settle; timed out workers retain the runtime and its resources.")]
    private readonly ConfigurationSwitchCoordinator _configurationSwitchCoordinator;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "The controller is released only after admitted work drains; an unresponsive shutdown intentionally retains it.")]
    private readonly NetworkSwitchRuntimeController _networkSwitchRuntimeController;
    private readonly RuntimeConfigurationSwitchOperations _configurationSwitchOperations;
    private readonly MihomoControllerSessionRegistry _controllerSessions = new();
    private readonly ISettingsStore _settingsStore;
    private readonly SettingsRecoveryJournal _settingsRecovery;
    private Exception? _settingsRecoveryFailure;
    private NetworkDisableIntent _networkDisableIntent = new();
    private readonly CoreDiscovery _coreDiscovery;
    private readonly LocalDeviceCoordinator _localDevice;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "Disposed by the bounded shutdown sequence; an incomplete dispose retains the runtime.")]
    private readonly SubscriptionScheduler _subscriptionScheduler;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed", Justification = "The process manager is released only after shutdown actions settle; an unresponsive shutdown intentionally retains it.")]
    private readonly CoreLifecycleCoordinator _coreLifecycle;
    private MihomoProcessManager _processManager => _coreLifecycle.Process;
    private readonly IStartupRegistration _startupRegistration;
    private MihomoApiClient? _api => _controllerSessions.Current?.Api;
    private long ControllerGeneration => _controllerSessions.Generation;
    private Task? _pollingTask;
    private Task? _dataRefreshTask;
    private AppSettings _settings = new();
    private readonly RuntimeStateStore _stateStore = new(CreateInitialSnapshot());
    private readonly RemoteControllerRefreshCoordinator _remoteRefresh;
    private readonly RuntimeLogCoordinator _logs;
    private readonly ControllerSessionGuard _controllerGuard;
    private readonly RuntimeDataRefreshCoordinator _dataRefresh;
    private readonly PanelRefreshPolicy _panelRefresh = new();
    private readonly ProxyOperationCoordinator _proxyOps;
    private readonly SubscriptionRefreshCoordinator _subscriptionRefresh;
    private readonly Func<MihomoApiClient>? _controllerApiFactory;
    private bool _usingServiceCore { get => _coreLifecycle.UsingServiceCore; set => _coreLifecycle.UsingServiceCore = value; }
    private TunState _confirmedTunState = TunState.Unavailable;
    private long _proxyOwnershipRevision;
    private long _proxyIntentRevision;
    private readonly object _proxyRecoveryGate = new();
    private Task? _proxyRecoveryTask;
    private Task<RuntimeShutdownResult>? _shutdownTask;
    private readonly Func<string, CancellationToken, Task>? _shutdownStepTestHook;
    private readonly Func<LocalCoreProcessIdentity?>? _localCoreProcessIdentityProvider;
    private readonly Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>>? _localCoreRecoveryAction;

    private static readonly TimeSpan DisposeCleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _disposeCleanupTimeout;
    private readonly TimeProvider _shutdownTimeProvider;
    private readonly TimeSpan _coreStartupBudget;

    private readonly record struct CoreLossContext(
        long LifecycleEpoch,
        long ProcessGeneration,
        long ControllerGeneration,
        long ProxyOwnershipRevision,
        long ProxyIntentRevision,
        bool CoreHealthConfirmed);

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
        Func<MihomoApiClient>? controllerApiFactory = null,
        TimeSpan? disposeCleanupTimeout = null,
        Func<string, CancellationToken, Task>? shutdownStepTestHook = null,
        Func<LocalCoreProcessIdentity?>? localCoreProcessIdentityProvider = null,
        Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>>? localCoreRecoveryAction = null,
        TimeSpan? coreStartupBudget = null,
        HttpMessageHandler? controllerHttpMessageHandler = null,
        TimeProvider? shutdownTimeProvider = null)
        : this(RuntimeEnvironmentDependencies.Compatible(paths, startupRegistration, servicePipeClient, settingsStore, systemProxy,
            controllerApiFactory is null ? null : new FixtureCoreOwnershipObservationSource()),
            candidateValidator, networkContextSource, endpointSessionConnector, remoteRefreshDelayAsync, remoteLogStreamRunner,
            controllerApiFactory, disposeCleanupTimeout, shutdownStepTestHook, localCoreProcessIdentityProvider, localCoreRecoveryAction,
            coreStartupBudget, controllerHttpMessageHandler, shutdownTimeProvider)
    {
    }
    internal ClashTrayRuntime(
        RuntimeEnvironmentDependencies environment,
        IConfigurationCandidateValidator? candidateValidator = null,
        INetworkContextSource? networkContextSource = null,
        IEndpointSessionConnector? endpointSessionConnector = null,
        Func<TimeSpan, CancellationToken, Task>? remoteRefreshDelayAsync = null,
        Func<EndpointSession, EndpointSessionStatusEventArgs, CancellationToken, Task>? remoteLogStreamRunner = null,
        Func<MihomoApiClient>? controllerApiFactory = null,
        TimeSpan? disposeCleanupTimeout = null,
        Func<string, CancellationToken, Task>? shutdownStepTestHook = null,
        Func<LocalCoreProcessIdentity?>? localCoreProcessIdentityProvider = null,
        Func<LocalCoreProcessIdentity, CancellationToken, Task<LocalCoreShutdownJournalResult>>? localCoreRecoveryAction = null,
        TimeSpan? coreStartupBudget = null,
        HttpMessageHandler? controllerHttpMessageHandler = null,
        TimeProvider? shutdownTimeProvider = null)
    {
        _httpClient = controllerHttpMessageHandler is null
            ? EndpointTransportPolicy.CreateControllerHttpClient()
            : new HttpClient(controllerHttpMessageHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        _disposeCleanupTimeout = disposeCleanupTimeout ?? DisposeCleanupTimeout;
        _shutdownTimeProvider = shutdownTimeProvider ?? TimeProvider.System;
        _coreStartupBudget = coreStartupBudget ?? TimeSpan.FromSeconds(30);
        _shutdownStepTestHook = shutdownStepTestHook;
        _localCoreProcessIdentityProvider = localCoreProcessIdentityProvider;
        _localCoreRecoveryAction = localCoreRecoveryAction;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_disposeCleanupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_coreStartupBudget, TimeSpan.Zero);
        _paths = environment.Paths;
        _paths.EnsureDirectories();
        _localCoreShutdownJournal = new LocalCoreShutdownJournal(_paths);
        EndpointStore endpointStore = new(_paths);
        EndpointSecretStore endpointSecretStore = new(_paths);
        EndpointCertificateStore endpointCertificateStore = new(_paths);
        _endpointTransportOptionsResolver = new EndpointTransportOptionsResolver(
            endpointSecretStore,
            endpointCertificateStore);
        IEndpointSessionConnector resolvedEndpointSessionConnector = endpointSessionConnector
            ?? new MihomoEndpointSessionConnector(
                ResolveEndpointRecordAsync,
                _endpointTransportOptionsResolver);
        _endpointSessions = new EndpointSessionManager(
            ControllerEndpointFactory.CreateLocal(_settings.ControllerPort),
            resolvedEndpointSessionConnector);
        _endpointCatalog = new EndpointCatalogCoordinator(
            _operationLock,
            _endpointSessions,
            endpointStore,
            endpointSecretStore,
            endpointCertificateStore,
            () => _settings,
            Publish,
            () => ActiveRuntimeBinding?.ControllerPort ?? _settings.ControllerPort);
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
        _settingsStore = environment.Settings;
        _settingsRecovery = new SettingsRecoveryJournal(_paths);
        _startupRegistration = environment.Startup;
        IServicePipeClient resolvedServicePipeClient = environment.Service;
        ISystemProxyController resolvedSystemProxy = environment.SystemProxy;
        _localDevice = new LocalDeviceCoordinator(
            EndpointKind.Local,
            resolvedServicePipeClient,
            resolvedSystemProxy);
        _coreLifecycle = new CoreLifecycleCoordinator(_localDevice, environment.Ownership);
        _subscriptionScheduler = new SubscriptionScheduler(
            cancellation => _configurationStore.ListAsync(cancellation),
            (profile, cancellation) => RefreshSubscriptionAsync(profile, cancellation),
            () => _settings,
            OnScheduledSubscriptionRefreshFailed,
            OnScheduledSubscriptionCycleFailed);
        _controllerApiFactory = controllerApiFactory;
        _throttledPublisher = new SnapshotPublishThrottle(Publish, _runtimeCts.Token);
        _logs = new RuntimeLogCoordinator(
            _stateStore,
            () => _api,
            () => _usingServiceCore,
            () => _settings.LogLevel,
            () => _throttledPublisher.Queue(),
            Publish,
            _runtimeCts.Token);
        _remoteRefresh = new RemoteControllerRefreshCoordinator(
            _endpointSessions,
            () => _settings.LogLevel,
            PublishAppSnapshot,
            () => _throttledPublisher.Queue(),
            (phase, path, exception, retryCount) => LogControllerFailure(phase, path, exception, retryCount),
            remoteRefreshDelayAsync,
            remoteLogStreamRunner,
            _runtimeCts.Token,
            () => _panelRefresh.GetPollingDemand(EndpointKind.Remote, activeController: true));
        _controllerGuard = new ControllerSessionGuard(_controllerSessions);
        _dataRefresh = new RuntimeDataRefreshCoordinator(
            _stateStore,
            _controllerGuard,
            _dataRefreshLock,
            CaptureCoreBindingEpochs,
            IsCurrentCoreBinding,
            LogControllerFailure,
            _throttledPublisher.RequestAsync,
            Publish);
        _proxyOps = new ProxyOperationCoordinator(
            _operationLock,
            _stateStore,
            _endpointSessions,
            _remoteRefresh,
            _logs,
            _controllerGuard,
            () => _settings,
            ExecuteControllerMutationAndRefreshAsync,
            CommitGroupDelayResultsAsync,
            Publish,
            _runtimeCts.Token,
            CaptureCurrentEndpointCommandTarget,
            CaptureLocalEndpointCommandTarget);
        _subscriptionRefresh = new SubscriptionRefreshCoordinator(
            _configurationStore,
            _configurationSwitchOperations,
            _configurationSwitchJournalStore,
            _logs,
            _stateStore,
            () => _settings,
            UpdateSubscriptionState,
            ExecuteConfigurationSwitchAsync,
            cancellation => RefreshConfigurationSnapshotAsync(cancellation),
            Publish);
        _endpointSessions.StatusChanged += _remoteRefresh.HandleSessionStatusChanged;
        _processManager.StateChanged += OnProcessStateChanged;
        _processManager.LogLineReceived += _logs.OnProcessLogLine;
    }

    public RuntimeSnapshot Snapshot
    {
        get
        {
            _logs.FlushPendingLogs();
            RuntimeSnapshot snapshot = _stateStore.Snapshot;
            Exception? recoveryFailure = Volatile.Read(ref _settingsRecoveryFailure);
            return recoveryFailure is null ? snapshot : snapshot with
            {
                ErrorMessage = $"{snapshot.ErrorMessage} 设置恢复未完成：{ErrorSanitizer.Sanitize(recoveryFailure)}".Trim()
            };
        }
    }

    public CoreRuntimeBinding? ActiveRuntimeBinding => _coreLifecycle.Binding;

    public long SnapshotRevision => _stateStore.Revision;

    public AppSnapshot AppSnapshot => ComposeAppSnapshot(Snapshot);

    public AppSettings Settings => _settings;

    public IReadOnlyList<EndpointDescriptor> Endpoints => _endpointCatalog.Endpoints;

    public EndpointStoreLoadStatus EndpointStoreStatus => _endpointCatalog.StoreStatus;

    public string? EndpointStoreMessage => _endpointCatalog.StoreMessage;

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

    private void Publish()
    {
        RuntimeSnapshot snapshot;
        AppSnapshot appSnapshot;
        EventHandler<RuntimeSnapshot>? snapshotChanged;
        EventHandler<AppSnapshot>? appSnapshotChanged;
        lock (_publishGate)
        {
            snapshot = Snapshot;
            appSnapshot = ComposeAppSnapshot(snapshot);
            snapshotChanged = SnapshotChanged;
            appSnapshotChanged = AppSnapshotChanged;
        }

        // Subscriber callbacks run outside the gate: a slow or re-entrant
        // subscriber must not extend the critical section for other publishers.
        snapshotChanged?.Invoke(this, snapshot);
        appSnapshotChanged?.Invoke(this, appSnapshot);
    }

    private void PublishAppSnapshot()
    {
        AppSnapshot appSnapshot;
        EventHandler<AppSnapshot>? appSnapshotChanged;
        lock (_publishGate)
        {
            appSnapshot = ComposeAppSnapshot(Snapshot);
            appSnapshotChanged = AppSnapshotChanged;
        }

        appSnapshotChanged?.Invoke(this, appSnapshot);
    }

    private void SetRuntimeBinding(CoreRuntimeBinding? binding)
    {
        if (binding is not null)
        {
            RuntimeBindingValidationResult validation = RuntimeBindingValidator.Validate(binding);
            if (!validation.IsValid)
            {
                throw RuntimeBindingValidator.CreateAdmissionException(validation);
            }
        }

        StoreRuntimeBinding(binding);
    }

    private void StoreRuntimeBinding(CoreRuntimeBinding? binding)
    {
        _coreLifecycle.StoreBinding(binding);
        _endpointSessions.UpdateLocalEndpoint(
            ControllerEndpointFactory.CreateLocal(binding?.ControllerPort ?? _settings.ControllerPort));
        if (!string.IsNullOrWhiteSpace(binding?.ListenerPlanWarning))
        {
            _logs.AddApplicationLog(new LogEntry(
                DateTimeOffset.UtcNow,
                "ClashTray",
                "warning",
                ErrorSanitizer.Sanitize(binding.ListenerPlanWarning)));
        }
    }

    private AppSnapshot ComposeAppSnapshot(RuntimeSnapshot snapshot) => AppSnapshotComposer.Compose(
        snapshot,
        _settings,
        Endpoints,
        activeController: _remoteRefresh.BuildActiveControllerSnapshot(),
        controllerGeneration: ControllerGeneration,
        localControllerPort: ActiveRuntimeBinding?.ControllerPort);

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

using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class NetworkSwitchRuntimeController : IAsyncDisposable
{
    private readonly NetworkRuleStore _store;
    private readonly INetworkContextSource? _source;
    private readonly Func<NetworkContextSnapshot, NetworkSwitchRuleSet, NetworkSwitchPolicyInput> _inputFactory;
    private readonly Func<ConfigurationSwitchRequest, CancellationToken, Task<ConfigurationSwitchResult>> _switchExecutor;
    private readonly TimeSpan? _debounce;
    private readonly TimeSpan? _cooldown;
    private readonly TimeProvider? _timeProvider;
    private readonly object _stateGate = new();
    private NetworkSwitchEventPipeline? _pipeline;
    private NetworkSwitchRuleSet _rules = new(false, null, []);
    private NetworkSwitchStatus _status = new(
        false,
        NetworkSwitchState.Disabled,
        NetworkPermissionState.Unknown,
        null,
        null,
        null);
    private bool _initialized;
    private bool _disposed;

    public NetworkSwitchRuntimeController(
        NetworkRuleStore store,
        INetworkContextSource? source,
        Func<NetworkContextSnapshot, NetworkSwitchRuleSet, NetworkSwitchPolicyInput> inputFactory,
        Func<ConfigurationSwitchRequest, CancellationToken, Task<ConfigurationSwitchResult>> switchExecutor,
        TimeSpan? debounce = null,
        TimeSpan? cooldown = null,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _source = source;
        _inputFactory = inputFactory ?? throw new ArgumentNullException(nameof(inputFactory));
        _switchExecutor = switchExecutor ?? throw new ArgumentNullException(nameof(switchExecutor));
        _debounce = debounce;
        _cooldown = cooldown;
        _timeProvider = timeProvider;
    }

    [SuppressMessage(
        "Design",
        "CA1003:Use generic event handler instances",
        Justification = "The immutable network status is the event payload and is intentionally not modeled as mutable EventArgs.")]
    public event EventHandler<NetworkSwitchStatus>? StatusChanged;

    public NetworkSwitchRuleSet Rules
    {
        get
        {
            lock (_stateGate)
            {
                return _rules;
            }
        }
    }

    public NetworkSwitchStatus Status
    {
        get
        {
            lock (_stateGate)
            {
                return _status;
            }
        }
    }

    public bool IsInitialized
    {
        get
        {
            lock (_stateGate)
            {
                return _initialized && !_disposed;
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Initialization is an isolation boundary; unexpected failures disable network switching and are surfaced as a sanitized status.")]
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                throw new InvalidOperationException("Network switch runtime controller has already been initialized.");
            }

            _initialized = true;
        }

        NetworkSwitchEventPipeline? pipeline = null;
        try
        {
            NetworkRuleStoreLoadResult loaded = await _store.LoadAsync(cancellationToken);
            lock (_stateGate)
            {
                _rules = loaded.Rules;
            }

            PublishStatus(CreateStatusFromRules(loaded.Message));
            if (_source is null)
            {
                return;
            }

            pipeline = new NetworkSwitchEventPipeline(
                _source,
                CreatePolicyInput,
                ExecuteDecisionAsync,
                _debounce,
                _cooldown,
                _timeProvider);
            pipeline.DecisionChanged += OnDecisionChanged;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _pipeline = pipeline;
            }

            await pipeline.StartAsync(cancellationToken);
            PublishStatus(CreateStatusFromPipeline(pipeline));
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            PublishStatus(CreateUnavailableStatus(ErrorSanitizer.Sanitize(exception)));
            await DisposeAsync();
        }
    }

    public async Task SetRulesAsync(
        NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        EnsureInitialized();
        await _store.SaveAsync(rules, cancellationToken);

        NetworkSwitchEventPipeline? pipeline;
        lock (_stateGate)
        {
            _rules = rules;
            pipeline = _pipeline;
        }

        if (pipeline is null)
        {
            PublishStatus(CreateStatusFromRules(null));
            return;
        }

        if (!rules.AutomaticSwitchingEnabled)
        {
            pipeline.ClearManualOverride();
        }

        pipeline.ReevaluateCurrentContext();
        PublishStatus(CreateStatusFromPipeline(pipeline));
    }

    public bool SetManualOverrideForCurrentNetwork(string configurationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        EnsureInitialized();

        NetworkSwitchEventPipeline? pipeline;
        lock (_stateGate)
        {
            pipeline = _pipeline;
        }

        NetworkContextSnapshot? context = pipeline?.LatestContext;
        if (pipeline is null || context is null)
        {
            return false;
        }

        pipeline.SetManualOverride(configurationId, context.Revision);
        pipeline.ReevaluateCurrentContext();
        PublishStatus(CreateStatusFromPipeline(pipeline));
        return true;
    }

    public void ClearManualOverride()
    {
        EnsureInitialized();

        NetworkSwitchEventPipeline? pipeline;
        lock (_stateGate)
        {
            pipeline = _pipeline;
        }

        if (pipeline is null)
        {
            return;
        }

        pipeline.ClearManualOverride();
        pipeline.ReevaluateCurrentContext();
        PublishStatus(CreateStatusFromPipeline(pipeline));
    }

    public async ValueTask DisposeAsync()
    {
        NetworkSwitchEventPipeline? pipeline;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pipeline = _pipeline;
            _pipeline = null;
        }

        if (pipeline is not null)
        {
            pipeline.DecisionChanged -= OnDecisionChanged;
            await pipeline.DisposeAsync();
        }
        else if (_source is not null)
        {
            await _source.DisposeAsync();
        }
    }

    private NetworkSwitchPolicyInput CreatePolicyInput(NetworkContextSnapshot context)
    {
        lock (_stateGate)
        {
            return _inputFactory(context, _rules);
        }
    }

    private async Task<NetworkSwitchExecutionResult> ExecuteDecisionAsync(
        NetworkSwitchDecision decision,
        CancellationToken cancellationToken)
    {
        ConfigurationSwitchRequest? request = NetworkSwitchPolicyEngine.CreateSwitchRequest(decision);
        if (request is null)
        {
            return NetworkSwitchExecutionResult.NotAttempted;
        }

        ConfigurationSwitchResult result = await _switchExecutor(request, cancellationToken);
        bool succeeded = result.Outcome is ConfigurationSwitchOutcome.NoOp
            or ConfigurationSwitchOutcome.Committed;
        return new NetworkSwitchExecutionResult(true, succeeded);
    }

    private void OnDecisionChanged(object? sender, NetworkSwitchDecision decision)
    {
        NetworkSwitchEventPipeline? pipeline;
        lock (_stateGate)
        {
            pipeline = _pipeline;
        }

        PublishStatus(CreateStatusFromPipeline(pipeline, decision));
    }

    private NetworkSwitchStatus CreateStatusFromRules(string? errorMessage)
    {
        NetworkSwitchRuleSet rules = Rules;
        return new(
            false,
            rules.AutomaticSwitchingEnabled
                ? NetworkSwitchState.WaitingForNetwork
                : NetworkSwitchState.Disabled,
            NetworkPermissionState.Unknown,
            null,
            null,
            errorMessage);
    }

    private NetworkSwitchStatus CreateStatusFromPipeline(
        NetworkSwitchEventPipeline? pipeline,
        NetworkSwitchDecision? decision = null)
    {
        NetworkSwitchRuleSet rules = Rules;
        NetworkContextSnapshot? context = pipeline?.LatestContext;
        NetworkSwitchDecision? lastDecision = decision ?? pipeline?.LastDecision;
        NetworkSwitchState state = lastDecision?.State
            ?? (rules.AutomaticSwitchingEnabled
                ? NetworkSwitchState.WaitingForNetwork
                : NetworkSwitchState.Disabled);
        string? errorMessage = lastDecision?.State == NetworkSwitchState.Failed
            ? lastDecision.Message
            : null;
        return new(
            true,
            state,
            context?.PermissionState ?? NetworkPermissionState.Unknown,
            context,
            lastDecision,
            errorMessage);
    }

    private static NetworkSwitchStatus CreateUnavailableStatus(string errorMessage) => new(
        false,
        NetworkSwitchState.Disabled,
        NetworkPermissionState.Unknown,
        null,
        null,
        errorMessage);

    private void PublishStatus(NetworkSwitchStatus status)
    {
        EventHandler<NetworkSwitchStatus>? handler;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _status = status;
            handler = StatusChanged;
        }

        handler?.Invoke(this, status);
    }

    private void EnsureInitialized()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                throw new InvalidOperationException("Network switch runtime controller has not been initialized.");
            }
        }
    }
}

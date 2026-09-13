using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed record NetworkSwitchExecutionResult(bool Attempted, bool Succeeded)
{
    public static NetworkSwitchExecutionResult NotAttempted { get; } = new(false, false);
}

public sealed class NetworkSwitchEventPipeline : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    private readonly INetworkContextSource _source;
    private readonly Func<NetworkContextSnapshot, NetworkSwitchPolicyInput> _inputFactory;
    private readonly Func<NetworkSwitchDecision, CancellationToken, Task<NetworkSwitchExecutionResult>> _decisionHandler;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<NetworkContextSnapshot> _contexts = Channel.CreateBounded<NetworkContextSnapshot>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _stateGate = new();
    private Task? _worker;
    private NetworkSwitchDecision? _lastDecision;
    private DateTimeOffset? _cooldownUntilUtc;
    private string? _manualOverrideConfigurationId;
    private long? _manualOverrideRevision;
    private long _lastProcessedRevision = -1;
    private bool _started;
    private bool _disposed;

    public NetworkSwitchEventPipeline(
        INetworkContextSource source,
        Func<NetworkContextSnapshot, NetworkSwitchPolicyInput> inputFactory,
        Func<NetworkSwitchDecision, CancellationToken, Task<NetworkSwitchExecutionResult>> decisionHandler,
        TimeSpan? debounce = null,
        TimeSpan? cooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(inputFactory);
        ArgumentNullException.ThrowIfNull(decisionHandler);
        if (debounce is not null && debounce <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        if (cooldown is not null && cooldown <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        _source = source;
        _inputFactory = inputFactory;
        _decisionHandler = decisionHandler;
        _debounce = debounce ?? DefaultDebounce;
        _cooldown = cooldown ?? DefaultCooldown;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    [SuppressMessage(
        "Design",
        "CA1003:Use generic event handler instances",
        Justification = "The immutable network decision is the event payload and is intentionally not modeled as mutable EventArgs.")]
    public event EventHandler<NetworkSwitchDecision>? DecisionChanged;

    public NetworkSwitchDecision? LastDecision
    {
        get
        {
            lock (_stateGate)
            {
                return _lastDecision;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("Network switch event pipeline has already started.");
            }

            _started = true;
            _source.ContextChanged += OnContextChanged;
            _worker = ProcessAsync(_cancellation.Token);
        }

        try
        {
            NetworkContextSnapshot current = await _source.GetCurrentAsync(cancellationToken);
            Enqueue(current);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public void SetManualOverride(string configurationId, long networkRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        ArgumentOutOfRangeException.ThrowIfNegative(networkRevision);

        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _manualOverrideConfigurationId = configurationId;
            _manualOverrideRevision = networkRevision;
        }
    }

    public void ClearManualOverride()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _manualOverrideConfigurationId = null;
            _manualOverrideRevision = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source.ContextChanged -= OnContextChanged;
            _contexts.Writer.TryComplete();
            worker = _worker;
        }

        await _cancellation.CancelAsync();

        if (worker is not null)
        {
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _source.DisposeAsync();
        _cancellation.Dispose();
    }

    private void OnContextChanged(object? sender, NetworkContextSnapshot context) => Enqueue(context);

    private void Enqueue(NetworkContextSnapshot context)
    {
        if (context.Revision < 0)
        {
            return;
        }

        _contexts.Writer.TryWrite(context);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _contexts.Reader.WaitToReadAsync(cancellationToken))
            {
                NetworkContextSnapshot latest = await _contexts.Reader.ReadAsync(cancellationToken);
                latest = await WaitForStableContextAsync(latest, cancellationToken);
                await EvaluateStableContextAsync(latest, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<NetworkContextSnapshot> WaitForStableContextAsync(
        NetworkContextSnapshot latest,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task delay = Task.Delay(_debounce, _timeProvider, cancellationToken);
            using CancellationTokenSource waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<bool> contextAvailable = _contexts.Reader.WaitToReadAsync(waitCancellation.Token).AsTask();
            Task completed = await Task.WhenAny(delay, contextAvailable);
            if (completed == contextAvailable)
            {
                if (!await contextAvailable)
                {
                    return latest;
                }

                latest = DrainLatest(latest);

                continue;
            }

            await waitCancellation.CancelAsync();
            try
            {
                await contextAvailable;
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
            }

            return DrainLatest(latest);
        }
    }

    private NetworkContextSnapshot DrainLatest(NetworkContextSnapshot latest)
    {
        while (_contexts.Reader.TryRead(out NetworkContextSnapshot? newer))
        {
            latest = newer ?? latest;
        }

        return latest;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The execution delegate is an isolation boundary; every failure must become a failed decision so the event worker remains alive.")]
    private async Task EvaluateStableContextAsync(
        NetworkContextSnapshot context,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (context.Revision <= _lastProcessedRevision)
            {
                return;
            }

            if (_manualOverrideRevision is long manualRevision && manualRevision != context.Revision)
            {
                _manualOverrideConfigurationId = null;
                _manualOverrideRevision = null;
            }

            _lastProcessedRevision = context.Revision;
        }

        NetworkSwitchPolicyInput input = _inputFactory(context);
        lock (_stateGate)
        {
            input = input with
            {
                ManualOverrideConfigurationId = _manualOverrideConfigurationId,
                ManualOverrideRevision = _manualOverrideRevision,
                CoolingDownUntilUtc = _cooldownUntilUtc
            };
        }

        NetworkSwitchDecision decision = NetworkSwitchPolicyEngine.Evaluate(input);
        PublishDecision(decision);
        if (decision.Kind is not (NetworkSwitchDecisionKind.SwitchToMappedConfiguration
            or NetworkSwitchDecisionKind.SwitchToDefaultConfiguration))
        {
            return;
        }

        NetworkSwitchExecutionResult execution;
        try
        {
            execution = await _decisionHandler(decision, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            execution = new NetworkSwitchExecutionResult(true, false);
        }
        catch (Exception)
        {
            execution = new NetworkSwitchExecutionResult(true, false);
        }

        if (execution.Attempted)
        {
            lock (_stateGate)
            {
                _cooldownUntilUtc = _timeProvider.GetUtcNow().Add(_cooldown);
            }
        }

        if (!execution.Succeeded)
        {
            PublishDecision(decision with
            {
                Kind = NetworkSwitchDecisionKind.KeepCurrentWithWarning,
                State = NetworkSwitchState.Failed,
                Reason = NetworkSwitchReason.ExecutionFailed,
                Message = "自动切换执行失败，已保持当前配置。"
            });
        }
    }

    private void PublishDecision(NetworkSwitchDecision decision)
    {
        lock (_stateGate)
        {
            _lastDecision = decision;
        }

        DecisionChanged?.Invoke(this, decision);
    }
}

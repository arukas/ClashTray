using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed class EndpointSessionStatusEventArgs : EventArgs
{
    public EndpointSessionStatusEventArgs(
        EndpointDescriptor endpoint,
        EndpointSessionState state,
        long generation,
        long selectionRevision,
        DateTimeOffset? lastConfirmedAt,
        int attempt,
        TimeSpan? nextRetryDelay,
        string? errorMessage)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        State = state;
        Generation = generation;
        SelectionRevision = selectionRevision;
        LastConfirmedAt = lastConfirmedAt;
        Attempt = attempt;
        NextRetryDelay = nextRetryDelay;
        ErrorMessage = ErrorSanitizer.SanitizeNullable(errorMessage);
    }

    public EndpointDescriptor Endpoint { get; }

    public EndpointSessionState State { get; }

    public long Generation { get; }

    public long SelectionRevision { get; }

    public DateTimeOffset? LastConfirmedAt { get; }

    public int Attempt { get; }

    public TimeSpan? NextRetryDelay { get; }

    public string? ErrorMessage { get; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5394:Do not use insecure randomness",
    Justification = "Backoff jitter is scheduling noise and is never used for security or identity.")]
public sealed class EndpointSessionBackoffPolicy
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly TimeSpan[] _delays;
    private readonly object _randomGate = new();
    private readonly TimeSpan _maxJitter;
    private readonly Random _random;

    public EndpointSessionBackoffPolicy(
        IReadOnlyList<TimeSpan>? delays = null,
        TimeSpan? maxJitter = null,
        Random? random = null)
    {
        _delays = (delays ?? DefaultDelays).ToArray();
        if (_delays.Length == 0 || _delays.Any(delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentException("Backoff delays must contain only positive values.", nameof(delays));
        }

        _maxJitter = maxJitter ?? TimeSpan.FromMilliseconds(250);
        if (_maxJitter < TimeSpan.Zero || _maxJitter > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(maxJitter));
        }

        _random = random ?? Random.Shared;
    }

    public TimeSpan GetDelay(int retryAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retryAttempt);

        TimeSpan baseDelay = _delays[Math.Min(retryAttempt - 1, _delays.Length - 1)];
        if (_maxJitter == TimeSpan.Zero)
        {
            return baseDelay;
        }

        double randomFactor;
        lock (_randomGate)
        {
            randomFactor = (_random.NextDouble() * 2) - 1;
        }

        long jitterTicks = (long)(_maxJitter.Ticks * randomFactor);
        long combinedTicks = Math.Clamp(
            baseDelay.Ticks + jitterTicks,
            TimeSpan.TicksPerMillisecond,
            TimeSpan.MaxValue.Ticks);
        return TimeSpan.FromTicks(combinedTicks);
    }
}

public sealed class EndpointSessionManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly EndpointSessionBackoffPolicy _backoffPolicy;
    private readonly IEndpointSessionConnector _connector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly EndpointDescriptor _localEndpoint;
    private EndpointSession? _current;
    private long _generation;
    private bool _disposed;
    private SelectionOperation? _operation;
    private EndpointSessionStatusEventArgs _status;

    public EndpointSessionManager(
        EndpointDescriptor localEndpoint,
        IEndpointSessionConnector connector,
        EndpointSessionBackoffPolicy? backoffPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        ValidateLocalEndpoint(localEndpoint);
        _localEndpoint = localEndpoint;
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _backoffPolicy = backoffPolicy ?? new EndpointSessionBackoffPolicy();
        _delayAsync = delayAsync ?? Task.Delay;
        _status = new EndpointSessionStatusEventArgs(
            _localEndpoint,
            EndpointSessionState.Disconnected,
            generation: 0,
            selectionRevision: 0,
            lastConfirmedAt: null,
            attempt: 0,
            nextRetryDelay: null,
            errorMessage: null);
    }

    public event EventHandler<EndpointSessionStatusEventArgs>? StatusChanged;

    public EndpointDescriptor LocalEndpoint => _localEndpoint;

    public EndpointSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public EndpointSessionStatusEventArgs Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public Task<EndpointSession?> SelectAsync(
        EndpointDescriptor endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateTargetEndpoint(endpoint);
        return SelectCoreAsync(endpoint, cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        EndpointSession? previousSession;
        SelectionOperation? previousOperation;
        EndpointSessionStatusEventArgs status;
        lock (_gate)
        {
            ThrowIfDisposed();
            previousSession = _current;
            _current = null;
            previousOperation = _operation;
            _operation = null;
            long generation = Interlocked.Increment(ref _generation);
            status = new EndpointSessionStatusEventArgs(
                _localEndpoint,
                EndpointSessionState.Disconnected,
                generation,
                _status.SelectionRevision + 1,
                lastConfirmedAt: null,
                attempt: 0,
                nextRetryDelay: null,
                errorMessage: null);
            _status = status;
        }

        _ = CancelAndDisposeOperationAsync(previousOperation);
        await DisposeSessionAsync(previousSession);
        RaiseStatusChanged(status);
    }

    public async ValueTask DisposeAsync()
    {
        EndpointSession? previousSession;
        SelectionOperation? previousOperation;
        EndpointSessionStatusEventArgs status;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previousSession = _current;
            _current = null;
            previousOperation = _operation;
            _operation = null;
            long generation = Interlocked.Increment(ref _generation);
            status = new EndpointSessionStatusEventArgs(
                _localEndpoint,
                EndpointSessionState.Disconnected,
                generation,
                _status.SelectionRevision + 1,
                lastConfirmedAt: null,
                attempt: 0,
                nextRetryDelay: null,
                errorMessage: null);
            _status = status;
        }

        _ = CancelAndDisposeOperationAsync(previousOperation);
        await _lifetimeCancellation.CancelAsync();
        await DisposeSessionAsync(previousSession);
        RaiseStatusChanged(status);
        _lifetimeCancellation.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The selection operation owns this linked token source until its replacement or manager disposal.")]
    private async Task<EndpointSession?> SelectCoreAsync(
        EndpointDescriptor endpoint,
        CancellationToken cancellationToken)
    {
        EndpointSession? previousSession;
        SelectionOperation? previousOperation;
        SelectionOperation operation;
        EndpointSessionStatusEventArgs connectingStatus;
        lock (_gate)
        {
            ThrowIfDisposed();
            previousSession = _current;
            _current = null;
            previousOperation = _operation;
            long generation = Interlocked.Increment(ref _generation);
            long selectionRevision = _status.SelectionRevision + 1;
            CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken);
            operation = new SelectionOperation(endpoint, generation, selectionRevision, cancellation);
            _operation = operation;
            connectingStatus = new EndpointSessionStatusEventArgs(
                endpoint,
                EndpointSessionState.Connecting,
                generation,
                selectionRevision,
                lastConfirmedAt: null,
                attempt: 0,
                nextRetryDelay: null,
                errorMessage: null);
            _status = connectingStatus;
        }

        _ = CancelAndDisposeOperationAsync(previousOperation);
        await DisposeSessionAsync(previousSession);
        RaiseStatusChanged(connectingStatus);
        return await RunSelectionAsync(operation).ConfigureAwait(false);
    }

    private async Task<EndpointSession?> RunSelectionAsync(SelectionOperation operation)
    {
        int attempt = 0;
        try
        {
            while (true)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    EndpointSession session = await _connector.ConnectAsync(
                            operation.Endpoint,
                            operation.Generation,
                            operation.SelectionRevision,
                            operation.Cancellation.Token)
                        .ConfigureAwait(false);
                    ArgumentNullException.ThrowIfNull(session);

                    if (!IsCurrent(operation)
                        || session.Generation != operation.Generation
                        || session.SelectionRevision != operation.SelectionRevision
                        || session.Endpoint.Id != operation.Endpoint.Id)
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        return null;
                    }

                    EndpointSessionStatusEventArgs connectedStatus;
                    lock (_gate)
                    {
                        if (!IsCurrentUnsafe(operation))
                        {
                            connectedStatus = _status;
                        }
                        else
                        {
                            _current = session;
                            connectedStatus = new EndpointSessionStatusEventArgs(
                                operation.Endpoint,
                                EndpointSessionState.Connected,
                                operation.Generation,
                                operation.SelectionRevision,
                                session.ConnectedAt,
                                attempt,
                                nextRetryDelay: null,
                                errorMessage: null);
                            _status = connectedStatus;
                        }
                    }

                    if (connectedStatus.State != EndpointSessionState.Connected)
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        return null;
                    }

                    RaiseStatusChanged(connectedStatus);
                    return session;
                }
                catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
                {
                    EndpointSessionStatusEventArgs? disconnectedStatus = TrySetDisconnected(operation);
                    if (disconnectedStatus is not null)
                    {
                        RaiseStatusChanged(disconnectedStatus);
                    }

                    return null;
                }
                catch (Exception exception) when (IsExpectedConnectionFailure(exception))
                {
                    (EndpointSessionState failureState, bool isTransient) = ClassifyFailure(exception);
                    if (!isTransient)
                    {
                        EndpointSessionStatusEventArgs? failureStatus = TrySetFailure(
                            operation,
                            failureState,
                            attempt,
                            ErrorSanitizer.Sanitize(exception));
                        if (failureStatus is not null)
                        {
                            RaiseStatusChanged(failureStatus);
                        }

                        return null;
                    }

                    TimeSpan delay = _backoffPolicy.GetDelay(attempt);
                    EndpointSessionStatusEventArgs? retryStatus = TrySetRetrying(
                        operation,
                        attempt,
                        delay,
                        ErrorSanitizer.Sanitize(exception));
                    if (retryStatus is null)
                    {
                        return null;
                    }

                    RaiseStatusChanged(retryStatus);
                    try
                    {
                        await _delayAsync(delay, operation.Cancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
                    {
                        EndpointSessionStatusEventArgs? disconnectedStatus = TrySetDisconnected(operation);
                        if (disconnectedStatus is not null)
                        {
                            RaiseStatusChanged(disconnectedStatus);
                        }

                        return null;
                    }
                }
            }
        }
        finally
        {
            operation.Completion.TrySetResult(null);
        }
    }

    private EndpointSessionStatusEventArgs? TrySetDisconnected(SelectionOperation operation)
    {
        lock (_gate)
        {
            if (!IsCurrentUnsafe(operation))
            {
                return null;
            }

            EndpointSessionStatusEventArgs status = new(
                operation.Endpoint,
                EndpointSessionState.Disconnected,
                operation.Generation,
                operation.SelectionRevision,
                lastConfirmedAt: null,
                attempt: 0,
                nextRetryDelay: null,
                errorMessage: null);
            _status = status;
            return status;
        }
    }

    private EndpointSessionStatusEventArgs? TrySetRetrying(
        SelectionOperation operation,
        int attempt,
        TimeSpan delay,
        string errorMessage)
    {
        lock (_gate)
        {
            if (!IsCurrentUnsafe(operation))
            {
                return null;
            }

            EndpointSessionStatusEventArgs status = new(
                operation.Endpoint,
                EndpointSessionState.Reconnecting,
                operation.Generation,
                operation.SelectionRevision,
                lastConfirmedAt: null,
                attempt,
                delay,
                errorMessage);
            _status = status;
            return status;
        }
    }

    private EndpointSessionStatusEventArgs? TrySetFailure(
        SelectionOperation operation,
        EndpointSessionState state,
        int attempt,
        string errorMessage)
    {
        lock (_gate)
        {
            if (!IsCurrentUnsafe(operation))
            {
                return null;
            }

            EndpointSessionStatusEventArgs status = new(
                operation.Endpoint,
                state,
                operation.Generation,
                operation.SelectionRevision,
                lastConfirmedAt: null,
                attempt,
                nextRetryDelay: null,
                errorMessage);
            _status = status;
            return status;
        }
    }

    private bool IsCurrent(SelectionOperation operation)
    {
        lock (_gate)
        {
            return IsCurrentUnsafe(operation);
        }
    }

    private bool IsCurrentUnsafe(SelectionOperation operation) =>
        !_disposed
        && ReferenceEquals(_operation, operation)
        && _generation == operation.Generation;

    private void RaiseStatusChanged(EndpointSessionStatusEventArgs status) =>
        StatusChanged?.Invoke(this, status);

    private static async Task DisposeSessionAsync(EndpointSession? session)
    {
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CancelAndDisposeOperationAsync(SelectionOperation? operation)
    {
        if (operation is null)
        {
            return;
        }

        try
        {
            await operation.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        await operation.Completion.Task.ConfigureAwait(false);
        operation.Cancellation.Dispose();
    }

    private static bool IsExpectedConnectionFailure(Exception exception) =>
        exception is EndpointSessionConnectException
            or HttpRequestException
            or WebSocketException
            or TimeoutException
            or AuthenticationException
            or CryptographicException
            or IOException;

    private static (EndpointSessionState State, bool IsTransient) ClassifyFailure(Exception exception)
    {
        if (exception is EndpointSessionConnectException endpointException)
        {
            return (endpointException.FailureState, endpointException.IsTransient);
        }

        if (exception is HttpRequestException httpException)
        {
            if (httpException.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (EndpointSessionState.AuthenticationFailed, false);
            }

            if (httpException.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return (EndpointSessionState.Incompatible, false);
            }

            if (httpException.InnerException is AuthenticationException)
            {
                return (EndpointSessionState.CertificateFailed, false);
            }
        }

        if (exception is AuthenticationException or CryptographicException)
        {
            return (EndpointSessionState.CertificateFailed, false);
        }

        return (EndpointSessionState.Failed, true);
    }

    private static void ValidateLocalEndpoint(EndpointDescriptor endpoint)
        => EndpointDescriptorValidator.ValidateForActiveSession(endpoint);

    private static void ValidateTargetEndpoint(EndpointDescriptor endpoint)
        => EndpointDescriptorValidator.ValidateForActiveSession(endpoint);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SelectionOperation
    {
        public SelectionOperation(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationTokenSource cancellation)
        {
            Endpoint = endpoint;
            Generation = generation;
            SelectionRevision = selectionRevision;
            Cancellation = cancellation;
        }

        public EndpointDescriptor Endpoint { get; }

        public long Generation { get; }

        public long SelectionRevision { get; }

        public CancellationTokenSource Cancellation { get; }

        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

namespace ClashTray.Core;

/// <summary>
/// Runs the current request and retains only the latest pending request. Every
/// caller receives the completion belonging to its own intent: an in-flight
/// intent gets its own result, the retained latest intent gets its own result,
/// and an intermediate pending intent is completed as Superseded.
/// </summary>
public sealed class LatestWinsOperation<T>
{
    private readonly object _gate = new();
    private readonly string _operationName;
    private bool _running;
    private ActiveIntent? _active;
    private ActiveIntent? _pending;

    public LatestWinsOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        _operationName = operationName;
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public Task<T> RequestAsync(
        T value,
        Func<T, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        ActiveIntent intent = new(value, operation, cancellationToken);
        ActiveIntent? operationToStart = null;
        ActiveIntent? superseded = null;
        lock (_gate)
        {
            if (_running)
            {
                superseded = _pending;
                _pending = intent;
            }
            else
            {
                _running = true;
                _active = intent;
                operationToStart = intent;
            }
        }

        if (superseded is not null)
        {
            CompleteDiscardedIntent(superseded);
        }

        if (operationToStart is not null)
        {
            _ = RunAsync(operationToStart);
        }

        return intent.Completion.Task.WaitAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The operation task captures arbitrary external failures and completes its own waiter.")]
    private async Task RunAsync(ActiveIntent active)
    {
        while (true)
        {
            T? result = default;
            Exception? failure = null;
            try
            {
                result = await active.Operation(active.Value, active.Lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                failure = exception;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            active.Lifetime.Dispose();
            Complete(active.Completion, result, failure);

            while (true)
            {
                ActiveIntent? next;
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, active))
                    {
                        return;
                    }

                    next = _pending;
                    _pending = null;
                    if (next is null)
                    {
                        _active = null;
                        _running = false;
                        return;
                    }

                    _active = next;
                }

                active = next;
                if (!active.CallerCancellation.IsCancellationRequested)
                {
                    break;
                }

                active.Completion.TrySetCanceled(active.CallerCancellation);
                active.Lifetime.Dispose();
                // A newer request may have arrived while this canceled pending
                // intent was being discarded. Loop back and promote it under
                // the same ownership instead of leaving it orphaned in
                // _pending with _running set to false.
            }

            await Task.Yield();
        }
    }

    private void CompleteDiscardedIntent(ActiveIntent intent)
    {
        if (intent.CallerCancellation.IsCancellationRequested)
        {
            intent.Completion.TrySetCanceled(intent.CallerCancellation);
        }
        else
        {
            intent.Completion.TrySetException(new OperationSupersededException(_operationName));
        }

        intent.Lifetime.Dispose();
    }

    private static void Complete(
        TaskCompletionSource<T> completion,
        T? result,
        Exception? failure)
    {
        if (failure is OperationCanceledException canceledException)
        {
            CancellationToken token = canceledException.CancellationToken.CanBeCanceled
                ? canceledException.CancellationToken
                : new CancellationToken(canceled: true);
            completion.TrySetCanceled(token);
        }
        else if (failure is not null)
        {
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult(result!);
        }
    }

    private sealed class ActiveIntent
    {
        public ActiveIntent(
            T value,
            Func<T, CancellationToken, Task<T>> operation,
            CancellationToken callerCancellation)
        {
            Value = value;
            Operation = operation;
            CallerCancellation = callerCancellation;
            Lifetime = new CancellationTokenSource();
            Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public T Value { get; }

        public Func<T, CancellationToken, Task<T>> Operation { get; }

        public CancellationToken CallerCancellation { get; }

        public CancellationTokenSource Lifetime { get; }

        public TaskCompletionSource<T> Completion { get; }
    }
}

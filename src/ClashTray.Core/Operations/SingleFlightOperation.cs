namespace ClashTray.Core;

/// <summary>
/// Shares one in-flight operation for a keyed request. Duplicate callers can
/// stop waiting independently without canceling the shared external work.
/// </summary>
public sealed class SingleFlightOperation<T>
{
    private readonly object _gate = new();
    private readonly string _operationName;
    private readonly bool _cancelWhenNoWaiters;
    private ActiveOperation? _active;

    public SingleFlightOperation(string operationName, bool cancelWhenNoWaiters = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        _operationName = operationName;
        _cancelWhenNoWaiters = cancelWhenNoWaiters;
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    public Task<T> RequestAsync(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        ActiveOperation active;
        ActiveOperation? retired = null;
        bool start = false;
        lock (_gate)
        {
            if (_active is null)
            {
                _active = new ActiveOperation(operation);
                start = true;
                active = _active;
                active.WaiterCount++;
            }
            else if (_cancelWhenNoWaiters && _active.WaiterCount == 0)
            {
                active = _active;
                retired = _active;
            }
            else
            {
                active = _active;
                active.WaiterCount++;
            }
        }

        if (retired is not null)
        {
            return RetryAfterRetiredAsync(retired, operation, cancellationToken);
        }

        if (start)
        {
            _ = RunAsync(active);
        }

        return WaitForCallerAsync(active, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A canceled retired shared operation is deliberately ignored before starting the next caller's request.")]
    private async Task<T> RetryAfterRetiredAsync(
        ActiveOperation retired,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await retired.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return await RequestAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The operation task captures arbitrary external failures and completes its own waiter.")]
    private async Task RunAsync(ActiveOperation active)
    {
        T? result = default;
        Exception? failure = null;
        try
        {
            result = await active.Operation(active.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, active))
                {
                    _active = null;
                }
            }

            active.Lifetime.Dispose();
        }

        if (failure is OperationCanceledException canceledException)
        {
            CancellationToken token = canceledException.CancellationToken.CanBeCanceled
                ? canceledException.CancellationToken
                : new CancellationToken(canceled: true);
            active.Completion.TrySetCanceled(token);
        }
        else if (failure is not null)
        {
            active.Completion.TrySetException(failure);
        }
        else
        {
            active.Completion.TrySetResult(result!);
        }
    }

    private async Task<T> WaitForCallerAsync(
        ActiveOperation active,
        CancellationToken cancellationToken)
    {
        try
        {
            return await active.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, active) && active.WaiterCount > 0)
                {
                    active.WaiterCount--;
                    if (active.WaiterCount == 0 && _cancelWhenNoWaiters)
                    {
                        _ = active.Lifetime.CancelAsync();
                    }
                }
            }
        }
    }

    private sealed class ActiveOperation
    {
        public ActiveOperation(Func<CancellationToken, Task<T>> operation)
        {
            Operation = operation;
            Lifetime = new CancellationTokenSource();
            Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<CancellationToken, Task<T>> Operation { get; }

        public CancellationTokenSource Lifetime { get; }

        public TaskCompletionSource<T> Completion { get; }

        public int WaiterCount { get; set; }
    }
}

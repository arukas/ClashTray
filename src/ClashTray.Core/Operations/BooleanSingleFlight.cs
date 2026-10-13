namespace ClashTray.Core;

/// <summary>
/// Coalesces repeated requests for one boolean target. The accepted system
/// operation owns an internal lifetime token; a caller token only controls
/// that caller's wait. An opposite target is rejected immediately and is never
/// queued behind the active operation.
/// </summary>
public sealed class BooleanSingleFlight<T>
{
    private readonly object _gate = new();
    private readonly string _operationName;
    private readonly bool _cancelWhenNoWaiters;
    private ActiveOperation? _active;
    private TaskCompletionSource? _idleCompletion;

    public BooleanSingleFlight(string operationName, bool cancelWhenNoWaiters = true)
    {
        _operationName = string.IsNullOrWhiteSpace(operationName) ? "目标" : operationName;
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

    public Task WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Task idleTask;
        lock (_gate)
        {
            if (_active is null)
            {
                return Task.CompletedTask;
            }

            _idleCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            idleTask = _idleCompletion.Task;
        }

        return idleTask.WaitAsync(timeout, cancellationToken);
    }

    public Task<T> RequestAsync(
        bool target,
        Func<bool, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        ActiveOperation active;
        ActiveOperation? retired = null;
        bool start = false;
        lock (_gate)
        {
            if (_active is not null)
            {
                if (_cancelWhenNoWaiters && _active.WaiterCount == 0)
                {
                    retired = _active;
                    active = _active;
                }
                else if (_active.Target != target)
                {
                    return Task.FromException<T>(new OperationBusyException(_operationName));
                }
                else
                {
                    active = _active;
                    active.WaiterCount++;
                }
            }
            else
            {
                active = new ActiveOperation(target, operation);
                _active = active;
                start = true;
                active.WaiterCount++;
            }
        }

        if (retired is not null)
        {
            return RetryAfterRetiredAsync(retired, target, operation, cancellationToken);
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
        bool target,
        Func<bool, CancellationToken, Task<T>> operation,
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

        return await RequestAsync(target, operation, cancellationToken).ConfigureAwait(false);
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
            result = await active.Operation(active.Target, active.Lifetime.Token).ConfigureAwait(false);
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
            TaskCompletionSource? idleCompletion = null;
            lock (_gate)
            {
                if (ReferenceEquals(_active, active))
                {
                    _active = null;
                    idleCompletion = _idleCompletion;
                    _idleCompletion = null;
                }
            }

            active.Lifetime.Dispose();
            idleCompletion?.TrySetResult();
        }

        Complete(active.Completion, result, failure);
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
            ReleaseWaiter(active);
        }
    }

    private void ReleaseWaiter(ActiveOperation active)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, active) || active.WaiterCount == 0)
            {
                return;
            }

            active.WaiterCount--;
            if (active.WaiterCount == 0 && _cancelWhenNoWaiters)
            {
                _ = active.Lifetime.CancelAsync();
            }
        }
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

    private sealed class ActiveOperation
    {
        public ActiveOperation(
            bool target,
            Func<bool, CancellationToken, Task<T>> operation)
        {
            Target = target;
            Operation = operation;
            Lifetime = new CancellationTokenSource();
            Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool Target { get; }

        public Func<bool, CancellationToken, Task<T>> Operation { get; }

        public CancellationTokenSource Lifetime { get; }

        public TaskCompletionSource<T> Completion { get; }

        public int WaiterCount { get; set; }
    }
}

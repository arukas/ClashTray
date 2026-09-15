namespace ClashTray.Core;

/// <summary>
/// Indicates that a non-queueable operation was requested while a conflicting
/// operation was already admitted.
/// </summary>
public sealed class OperationBusyException : InvalidOperationException
{
    public OperationBusyException()
        : this("操作")
    {
    }

    public OperationBusyException(string operationName)
        : base($"{operationName} 操作正在进行，请稍后重试。")
    {
    }

    public OperationBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A serializing gate that supports ordinary queued work and a strict
/// try-enter path for lifecycle operations. The try-enter path also refuses to
/// leapfrog an already waiting ordinary operation.
/// </summary>
internal sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _waiterCount;
    private int _disposed;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _waiterCount);
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }
    }

    public bool TryEnter()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _waiterCount) != 0)
        {
            return false;
        }

        return _semaphore.Wait(0);
    }

    public void Exit()
    {
        ThrowIfDisposed();
        _semaphore.Release();
    }

    public void Release() => Exit();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

/// <summary>
/// Coalesces repeated requests for one boolean target. A request for the
/// target already in flight shares its task; a request for the opposite target
/// fails immediately and is never queued.
/// </summary>
public sealed class BooleanSingleFlight<T>
{
    private readonly object _gate = new();
    private readonly string _operationName;
    private ActiveOperation? _active;

    public BooleanSingleFlight(string operationName)
    {
        _operationName = string.IsNullOrWhiteSpace(operationName) ? "目标" : operationName;
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
        bool target,
        Func<bool, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        ActiveOperation active;
        bool start = false;
        lock (_gate)
        {
            if (_active is not null)
            {
                if (_active.Target != target)
                {
                    return Task.FromException<T>(new OperationBusyException(_operationName));
                }

                active = _active;
            }
            else
            {
                active = new ActiveOperation(target, operation, cancellationToken);
                _active = active;
                start = true;
            }
        }

        if (start)
        {
            _ = RunAsync(active);
            // The caller that admitted the operation owns its cancellation
            // token. Return the operation completion directly so a canceled
            // owner cannot observe cancellation before the underlying work
            // has released the single-flight slot. Duplicate callers retain
            // independently cancellable waits below.
            return active.Completion.Task;
        }

        return WaitForCallerAsync(active.Completion.Task, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The operation task must capture arbitrary user-provided failures and release the slot.")]
    private async Task RunAsync(ActiveOperation active)
    {
        T? result = default;
        Exception? failure = null;
        try
        {
            result = await active.Operation(active.Target, active.CancellationToken).ConfigureAwait(false);
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

    private static async Task<T> WaitForCallerAsync(Task<T> task, CancellationToken cancellationToken)
    {
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ActiveOperation
    {
        public ActiveOperation(
            bool target,
            Func<bool, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Target = target;
            Operation = operation;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool Target { get; }

        public Func<bool, CancellationToken, Task<T>> Operation { get; }

        public CancellationToken CancellationToken { get; }

        public TaskCompletionSource<T> Completion { get; }
    }
}

/// <summary>
/// Shares one in-flight operation for a keyed request. Unlike
/// <see cref="LatestWinsOperation{T}"/>, a new request does not replace the
/// current work; it observes the same result. This is appropriate for
/// idempotent delay probes where duplicate clicks have no additional value.
/// </summary>
public sealed class SingleFlightOperation<T>
{
    private readonly object _gate = new();
    private ActiveOperation? _active;

    public SingleFlightOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
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
        bool start = false;
        lock (_gate)
        {
            if (_active is null)
            {
                _active = new ActiveOperation(operation, cancellationToken);
                start = true;
            }

            active = _active;
        }

        if (start)
        {
            _ = RunAsync(active);
            // See the boolean single-flight implementation above: the
            // admitting caller must observe completion after cleanup, while
            // duplicate callers may stop waiting independently.
            return active.Completion.Task;
        }

        return WaitForCallerAsync(active.Completion.Task, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The operation task must capture arbitrary user-provided failures and release the slot.")]
    private async Task RunAsync(ActiveOperation active)
    {
        T? result = default;
        Exception? failure = null;
        try
        {
            result = await active.Operation(active.CancellationToken).ConfigureAwait(false);
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

    private static async Task<T> WaitForCallerAsync(Task<T> task, CancellationToken cancellationToken) =>
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);

    private sealed class ActiveOperation
    {
        public ActiveOperation(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Operation = operation;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<CancellationToken, Task<T>> Operation { get; }

        public CancellationToken CancellationToken { get; }

        public TaskCompletionSource<T> Completion { get; }
    }
}

/// <summary>
/// Runs the current request and, while it is running, retains only the latest
/// pending request. This is used for UI intent streams such as mode and node
/// selection where stale intermediate clicks have no value.
/// </summary>
public sealed class LatestWinsOperation<T>
{
    private readonly object _gate = new();
    private bool _running;
    private ActiveOperation? _pending;
    private ActiveOperation? _active;
    private List<TaskCompletionSource<T>> _waiters = [];

    public LatestWinsOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
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

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActiveOperation? operationToStart = null;
        lock (_gate)
        {
            _waiters.Add(completion);
            if (_running)
            {
                // Retain the complete latest request, not only its value. A
                // superseded caller's cancellation token must not own a newer
                // intent that arrived while the first operation was running.
                _pending = new ActiveOperation(value, operation, cancellationToken);
            }
            else
            {
                _running = true;
                operationToStart = new ActiveOperation(value, operation, cancellationToken);
                _active = operationToStart;
            }
        }

        if (operationToStart is not null)
        {
            _ = RunAsync(operationToStart);
        }

        return WaitForCallerAsync(completion.Task, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "The operation task must capture arbitrary user-provided failures and complete every waiter.")]
    private async Task RunAsync(ActiveOperation active)
    {
        T? result = default;
        Exception? failure = null;
        try
        {
            result = await active.Operation(active.Value, active.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        ActiveOperation? next = null;
        List<TaskCompletionSource<T>>? completions = null;
        lock (_gate)
        {
            if (ReferenceEquals(_active, active) && _pending is not null)
            {
                next = _pending;
                _pending = null;
                _active = next;
            }
            else
            {
                _active = null;
                _running = false;
                completions = _waiters;
                _waiters = [];
            }
        }

        if (next is not null)
        {
            // A test double or a fast local endpoint can complete inline. Yield
            // before starting the retained intent so a long click burst cannot
            // recurse synchronously and overflow the stack.
            await Task.Yield();
            _ = RunAsync(next);
            return;
        }

        if (completions is null)
        {
            return;
        }

        foreach (TaskCompletionSource<T> completion in completions)
        {
            if (failure is OperationCanceledException exception)
            {
                CancellationToken token = exception.CancellationToken.CanBeCanceled
                    ? exception.CancellationToken
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
    }

    private static async Task<T> WaitForCallerAsync(Task<T> task, CancellationToken cancellationToken)
    {
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ActiveOperation
    {
        public ActiveOperation(
            T value,
            Func<T, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Value = value;
            Operation = operation;
            CancellationToken = cancellationToken;
        }

        public T Value { get; }

        public Func<T, CancellationToken, Task<T>> Operation { get; }

        public CancellationToken CancellationToken { get; }
    }
}

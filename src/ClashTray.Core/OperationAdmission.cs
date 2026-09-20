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
        OperationName = string.IsNullOrWhiteSpace(operationName) ? "操作" : operationName;
        Outcome = OperationOutcome.Busy;
    }

    public OperationBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
        OperationName = "操作";
        Outcome = OperationOutcome.Busy;
    }

    public string OperationName { get; }

    public OperationOutcome Outcome { get; }
}

/// <summary>
/// Serializes local-device mutations and provides an explicit quiescing phase
/// for shutdown. Exclusive admissions serialize with everything; shared
/// admissions run concurrently with each other but never overlap an exclusive
/// admission, and a waiting exclusive operation blocks new shared admissions
/// (writer-preferring). Queued ordinary work is rejected once quiescing
/// begins; the cleanup owner waits for already admitted work to reach a safe
/// point and then runs cleanup through its internal ownership path.
/// </summary>
internal sealed class OperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _stateGate = new();
    private int _waiterCount;
    private int _activeCount;
    private int _quiescing;
    private int _disposed;
    private int _activeReaders;
    private int _waitingWriters;
    private bool _writerActive;
    private TaskCompletionSource? _idleCompletion;
    private TaskCompletionSource? _readerWaiters;
    private TaskCompletionSource? _readersDrained;

    public bool IsQuiescing => Volatile.Read(ref _quiescing) != 0;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfQuiescing();
        Interlocked.Increment(ref _waiterCount);
        BeginWriterWait();
        bool acquired = false;
        try
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await WaitForReadersToDrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (acquired)
            {
                _semaphore.Release();
            }

            EndWriterWait();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiterCount);
        }

        if (!TryMarkWriterActive())
        {
            _semaphore.Release();
            throw new RuntimeQuiescingException();
        }

        Interlocked.Increment(ref _activeCount);
    }

    public bool TryEnter()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            if (IsQuiescing
                || Volatile.Read(ref _waiterCount) != 0
                || _writerActive
                || _activeReaders != 0)
            {
                return false;
            }

            if (!_semaphore.Wait(0))
            {
                return false;
            }

            if (IsQuiescing)
            {
                _semaphore.Release();
                return false;
            }

            _writerActive = true;
        }

        Interlocked.Increment(ref _activeCount);
        return true;
    }

    public void Exit()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            _writerActive = false;
            OpenReaderGateLocked();
        }

        _semaphore.Release();
        if (Interlocked.Decrement(ref _activeCount) == 0)
        {
            CompleteIdleWaiter();
        }
    }

    public void Release() => Exit();

    public void BeginQuiescing()
    {
        Interlocked.Exchange(ref _quiescing, 1);
        lock (_stateGate)
        {
            TaskCompletionSource? waiters = _readerWaiters;
            _readerWaiters = null;
            waiters?.TrySetResult();
        }
    }

    /// <summary>
    /// Admits a shared operation. Shared admissions run concurrently with each
    /// other but never overlap an exclusive admission; a waiting exclusive
    /// operation blocks new shared admissions (writer-preferring).
    /// </summary>
    public async Task<Lease> AcquireSharedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        while (true)
        {
            Task waitTask;
            lock (_stateGate)
            {
                ThrowIfQuiescing();
                if (!_writerActive && _waitingWriters == 0)
                {
                    _activeReaders++;
                    Interlocked.Increment(ref _activeCount);
                    return new Lease(this, isShared: true);
                }

                _readerWaiters ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _readerWaiters.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Task idleTask;
        lock (_stateGate)
        {
            if (Volatile.Read(ref _activeCount) == 0)
            {
                return Task.CompletedTask;
            }

            _idleCompletion ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            idleTask = _idleCompletion.Task;
        }

        return idleTask.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Acquires the mutation lane for shutdown cleanup after quiescing has
    /// started. Unlike <see cref="WaitAsync"/>, this admission is reserved for
    /// the cleanup owner and therefore deliberately ignores the quiescing flag.
    /// The caller must release the ownership with <see cref="Exit"/>.
    /// </summary>
    public async Task WaitForCleanupOwnershipAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ThrowIfDisposed();
        if (!IsQuiescing)
        {
            throw new InvalidOperationException("Cleanup ownership is available only after quiescing begins.");
        }

        if (!await _semaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out waiting for runtime cleanup ownership.");
        }

        await CompleteCleanupAdmissionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForCleanupOwnershipAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsQuiescing)
        {
            throw new InvalidOperationException("Cleanup ownership is available only after quiescing begins.");
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        await CompleteCleanupAdmissionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires the mutation lane exclusively and returns a lease that releases
    /// it on disposal. Ownership is lexical: holders pass the lease down the
    /// call chain instead of tracking a boolean flag.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this, isShared: false);
    }

    /// <summary>
    /// Non-blocking variant of <see cref="AcquireAsync"/>; returns <see langword="null"/>
    /// when the lane is busy or quiescing instead of waiting.
    /// </summary>
    public Lease? TryAcquire() => TryEnter() ? new Lease(this, isShared: false) : null;

    /// <summary>
    /// Lease-returning variant of <see cref="WaitForCleanupOwnershipAsync(TimeSpan, CancellationToken)"/>.
    /// </summary>
    public async Task<Lease> AcquireCleanupOwnershipAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        await WaitForCleanupOwnershipAsync(timeout, cancellationToken).ConfigureAwait(false);
        return new Lease(this, isShared: false);
    }

    /// <summary>
    /// Lease-returning variant of <see cref="WaitForCleanupOwnershipAsync(CancellationToken)"/>.
    /// </summary>
    public async Task<Lease> AcquireCleanupOwnershipAsync(CancellationToken cancellationToken = default)
    {
        await WaitForCleanupOwnershipAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this, isShared: false);
    }

    /// <summary>
    /// Lexical ownership token for the mutation lane. Disposing releases the
    /// lane exactly once; repeated disposal is a no-op.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private readonly bool _shared;
        private OperationGate? _owner;

        internal Lease(OperationGate owner, bool isShared)
        {
            _owner = owner;
            _shared = isShared;
        }

        public void Dispose()
        {
            OperationGate? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            if (_shared)
            {
                owner.ExitShared();
            }
            else
            {
                owner.Exit();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private void BeginWriterWait()
    {
        lock (_stateGate)
        {
            _waitingWriters++;
        }
    }

    private void EndWriterWait()
    {
        lock (_stateGate)
        {
            _waitingWriters--;
            OpenReaderGateLocked();
        }
    }

    private bool TryMarkWriterActive()
    {
        lock (_stateGate)
        {
            _waitingWriters--;
            if (IsQuiescing)
            {
                OpenReaderGateLocked();
                return false;
            }

            _writerActive = true;
            return true;
        }
    }

    private void OpenReaderGateLocked()
    {
        if (_writerActive || _waitingWriters != 0)
        {
            return;
        }

        TaskCompletionSource? waiters = _readerWaiters;
        _readerWaiters = null;
        waiters?.TrySetResult();
    }

    private Task WaitForReadersToDrainAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_activeReaders == 0)
            {
                return Task.CompletedTask;
            }

            _readersDrained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _readersDrained.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteCleanupAdmissionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WaitForReadersToDrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }

        lock (_stateGate)
        {
            _writerActive = true;
        }

        Interlocked.Increment(ref _activeCount);
    }

    private void ExitShared()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            _activeReaders--;
            if (_activeReaders == 0)
            {
                TaskCompletionSource? drained = _readersDrained;
                _readersDrained = null;
                drained?.TrySetResult();
            }
        }

        if (Interlocked.Decrement(ref _activeCount) == 0)
        {
            CompleteIdleWaiter();
        }
    }

    private void CompleteIdleWaiter()
    {
        TaskCompletionSource? completion;
        lock (_stateGate)
        {
            completion = _idleCompletion;
            _idleCompletion = null;
        }

        completion?.TrySetResult();
    }

    private void ThrowIfQuiescing()
    {
        if (IsQuiescing)
        {
            throw new RuntimeQuiescingException();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

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

namespace ClashTray.Core;

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
            cancellationToken.ThrowIfCancellationRequested();
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

        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await _semaphore.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
            await CompleteCleanupAdmissionAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for runtime cleanup ownership.");
        }
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
            cancellationToken.ThrowIfCancellationRequested();
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

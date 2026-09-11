namespace ClashTray.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\ClashTray.Desktop.SingleInstance";
    private const string ActivationEventName = "Local\\ClashTray.Desktop.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _listenerCts = new();
    private readonly Task _listener;
    private bool _disposed;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activationEvent)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _listener = Task.Run(ListenAsync);
    }

    public event Action? ActivationRequested;

    public static SingleInstanceCoordinator? TryAcquire(bool diagnostic = false)
    {

        string suffix = diagnostic ? ".Diagnostic" : string.Empty;
        Mutex mutex = new Mutex(initiallyOwned: true, MutexName + suffix, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(ActivationEventName + suffix);
                activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            return null;
        }

        EventWaitHandle activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName + suffix);
        return new SingleInstanceCoordinator(mutex, activation);

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCts.Cancel();
        _activationEvent.Set();
        try
        {
            _listener.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _activationEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _listenerCts.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_listenerCts.IsCancellationRequested)
        {
            await Task.Run(() => _activationEvent.WaitOne(), _listenerCts.Token);
            if (!_listenerCts.IsCancellationRequested)
            {
                ActivationRequested?.Invoke();
            }
        }
    }
}

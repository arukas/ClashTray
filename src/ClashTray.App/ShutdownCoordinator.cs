using System.Diagnostics.CodeAnalysis;

namespace ClashTray.App;

internal sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Func<Action, Task> _dispatchUiActionAsync;
    private readonly Action _beginShutdown;
    private readonly Func<Task> _disposeAsync;
    private readonly Action _exitProcess;
    private Task? _completion;

    public ShutdownCoordinator(
        Func<Action, Task> dispatchUiActionAsync,
        Action beginShutdown,
        Func<Task> disposeAsync,
        Action exitProcess)
    {
        _dispatchUiActionAsync = dispatchUiActionAsync ?? throw new ArgumentNullException(nameof(dispatchUiActionAsync));
        _beginShutdown = beginShutdown ?? throw new ArgumentNullException(nameof(beginShutdown));
        _disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
        _exitProcess = exitProcess ?? throw new ArgumentNullException(nameof(exitProcess));
    }

    public Task RequestQuitAsync()
    {
        TaskCompletionSource completionSource;
        lock (_gate)
        {
            if (_completion is not null)
            {
                return _completion;
            }

            completionSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completionSource.Task;
        }

        _ = CompleteRequestAsync(completionSource);
        return completionSource.Task;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Quit cleanup must publish one shared completion result, including unexpected dispatcher and callback failures, to every caller.")]
    private async Task CompleteRequestAsync(TaskCompletionSource completionSource)
    {
        try
        {
            await _dispatchUiActionAsync(_beginShutdown).ConfigureAwait(false);
            await _disposeAsync().ConfigureAwait(false);
            await _dispatchUiActionAsync(_exitProcess).ConfigureAwait(false);
            completionSource.TrySetResult();
        }
        catch (Exception exception)
        {
            completionSource.TrySetException(exception);
        }
    }
}

internal sealed class ShutdownUiDispatcherRejectedException : InvalidOperationException
{
    public ShutdownUiDispatcherRejectedException()
        : this("The UI dispatcher is unavailable; the shutdown callback could not be run on the window thread.")
    {
    }

    public ShutdownUiDispatcherRejectedException(string message)
        : base(message)
    {
    }

    public ShutdownUiDispatcherRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using System.Diagnostics.CodeAnalysis;

namespace ClashTray.App;

internal sealed class ShutdownCoordinator
{
    private readonly object _gate = new();
    private readonly Action _beginShutdown;
    private readonly Func<Task> _disposeAsync;
    private readonly Action _exitProcess;
    private Task? _completion;

    public ShutdownCoordinator(Action beginShutdown, Func<Task> disposeAsync, Action exitProcess)
    {
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Quit cleanup must publish one shared completion result, including unexpected callback failures, to every caller.")]
    private async Task CompleteRequestAsync(TaskCompletionSource completionSource)
    {
        try
        {
            _beginShutdown();
            await _disposeAsync().ConfigureAwait(false);
            _exitProcess();
            completionSource.TrySetResult();
        }
        catch (Exception exception)
        {
            completionSource.TrySetException(exception);
        }
    }
}
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace ClashTray.Core;

internal sealed record SnapshotPublication(Action? NotifyRuntime, Action NotifyApp);

// One dispatcher owns delivery; publishers only replace a bounded pending slot.
// Capture and enqueue share the gate so capture order is also delivery order.
internal sealed class SnapshotPublicationQueue(object gate)
{
    private SnapshotPublication? _pending;
    private bool _dispatching;

    public void Publish(Func<SnapshotPublication> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (gate)
        {
            SnapshotPublication publication = capture();
            // An app-only update must not erase a pending local notification.
            _pending = publication with { NotifyRuntime = publication.NotifyRuntime ?? _pending?.NotifyRuntime };
            if (_dispatching) { return; }
            _dispatching = true;
        }

        Drain();
    }

    private void Drain()
    {
        ExceptionDispatchInfo? failure = null;
        while (true)
        {
            SnapshotPublication publication;
            lock (gate)
            {
                if (_pending is null)
                {
                    _dispatching = false;
                    break;
                }
                publication = _pending;
                _pending = null;
            }

            Notify(publication.NotifyRuntime, ref failure);
            Notify(publication.NotifyApp, ref failure);
        }

        failure?.Throw();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Finish ordered state delivery before rethrowing the first subscriber failure to the dispatching publisher.")]
    private static void Notify(Action? callback, ref ExceptionDispatchInfo? failure)
    {
        try { callback?.Invoke(); }
        catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
    }
}

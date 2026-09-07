using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstanceCoordinator.TryAcquire(out var coordinator))
        {
            return;
        }

        Application.Start(_ =>
        {
            var synchronizationContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            var app = new App(coordinator!);
        });

        coordinator!.Dispose();
    }
}

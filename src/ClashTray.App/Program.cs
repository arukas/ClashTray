using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var smokeDirectory = args.FirstOrDefault(arg => arg.StartsWith("--ui-smoke-test=", StringComparison.Ordinal))?["--ui-smoke-test=".Length..];
        if (!SingleInstanceCoordinator.TryAcquire(out var coordinator, smokeDirectory is not null))
        {
            return;
        }

        Application.Start(_ =>
        {
            var synchronizationContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            var app = new App(coordinator!, smokeDirectory);
        });

        coordinator!.Dispose();
    }
}

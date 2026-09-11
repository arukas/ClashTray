using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        string? smokeDirectory = args.FirstOrDefault(arg => arg.StartsWith("--ui-smoke-test=", StringComparison.Ordinal))?["--ui-smoke-test=".Length..];
        if (!SingleInstanceCoordinator.TryAcquire(out SingleInstanceCoordinator? coordinator, smokeDirectory is not null))
        {
            return;
        }

        try
        {
            Application.Start(_ =>
            {
                DispatcherQueueSynchronizationContext synchronizationContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(synchronizationContext);
                App app = new App(coordinator!, smokeDirectory);
                coordinator = null;
            });
        }
        finally
        {
            coordinator?.Dispose();
        }
    }
}

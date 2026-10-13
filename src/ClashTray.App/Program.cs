using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if DEBUG
        string? smokeDirectory = args.FirstOrDefault(arg => arg.StartsWith("--ui-smoke-test=", StringComparison.Ordinal))?["--ui-smoke-test=".Length..];
        if (smokeDirectory is not null)
        {
            Directory.CreateDirectory(smokeDirectory);
            File.AppendAllText(Path.Combine(smokeDirectory, "marker.log"), $"{DateTimeOffset.Now:O} entered Main{Environment.NewLine}");
        }
#else
        string? smokeDirectory = null;
#endif

#if DEBUG
        using SingleInstanceCoordinator? coordinator = SingleInstanceCoordinator.TryAcquire(smokeDirectory is not null);
#else
        using SingleInstanceCoordinator? coordinator = SingleInstanceCoordinator.TryAcquire(diagnostic: false);
#endif
        if (coordinator is null)
        {
#if DEBUG
            if (smokeDirectory is not null)
            {
                File.AppendAllText(Path.Combine(smokeDirectory, "marker.log"), $"{DateTimeOffset.Now:O} coordinator null{Environment.NewLine}");
            }
#endif

            return;
        }

        Application.Start(_ =>
        {
            DispatcherQueueSynchronizationContext synchronizationContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            App app = new App(coordinator, smokeDirectory);
        });
    }
}

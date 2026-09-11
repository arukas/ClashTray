using ClashTray.Core;

namespace ClashTray.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("--restore-owned-proxy", StringComparer.OrdinalIgnoreCase))
        {
            SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers();
            return;
        }

        string userSid = args.FirstOrDefault(argument => argument.StartsWith("--user-sid=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2).ElementAtOrDefault(1)
            ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Unable to resolve the service user SID.");

        if (!Environment.UserInteractive && !args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            using WindowsServiceHost serviceHost = new WindowsServiceHost(userSid);
            System.ServiceProcess.ServiceBase.Run(serviceHost);
            return;
        }

        await using ServiceCommandHost commandHost = new ServiceCommandHost(userSid);
        commandHost.Start();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

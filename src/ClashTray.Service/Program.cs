namespace ClashTray.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        string userSid = args.FirstOrDefault(argument => argument.StartsWith("--user-sid=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2).ElementAtOrDefault(1)
            ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Unable to resolve the service user SID.");

        if (!Environment.UserInteractive && !args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            using WindowsServiceHost host = new WindowsServiceHost(userSid);
            System.ServiceProcess.ServiceBase.Run(host);
            return;
        }

        await using ServiceCommandHost host = new ServiceCommandHost(userSid);
        host.Start();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

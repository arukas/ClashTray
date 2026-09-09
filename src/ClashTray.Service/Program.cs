namespace ClashTray.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var userSid = args.FirstOrDefault(argument => argument.StartsWith("--user-sid=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2).ElementAtOrDefault(1)
            ?? System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Unable to resolve the service user SID.");

        if (!Environment.UserInteractive && !args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            System.ServiceProcess.ServiceBase.Run(new WindowsServiceHost(userSid));
            return;
        }

        await using var host = new ServiceCommandHost(userSid);
        host.Start();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

using ClashTray.Core;
using Microsoft.Extensions.Logging;

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

        AppPaths paths = new();
        try
        {
            paths.EnsureProgramDataDirectories(userSid);
        }
        catch (IOException)
        {
            // Directory/ACL setup failure must not block service start; the log
            // provider recreates the log directory without ACL hardening.
        }
        catch (UnauthorizedAccessException)
        {
        }

        // LoggerFactory does not dispose provider instances handed to AddProvider;
        // keep ownership explicit so the log handle is released on shutdown.
        using RollingFileLoggerProvider loggerProvider = new(paths.ServiceLogsRoot);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(loggerProvider));
        ILogger logger = loggerFactory.CreateLogger("ClashTray.Service");
        bool serviceMode = !Environment.UserInteractive && !args.Contains("--console", StringComparer.OrdinalIgnoreCase);
        logger.LogInformation("ClashTray service starting (mode: {Mode}).", serviceMode ? "service" : "console");

        if (serviceMode)
        {
            using WindowsServiceHost serviceHost = new WindowsServiceHost(userSid, loggerFactory);
            System.ServiceProcess.ServiceBase.Run(serviceHost);
            logger.LogInformation("ClashTray service stopped.");
            return;
        }

        await using ServiceCommandHost commandHost = new ServiceCommandHost(userSid, loggerFactory);
        commandHost.Start();

        // Console mode must still run the full cleanup path on Ctrl+C /
        // process exit; otherwise the hosted core and TUN state leak.
        using CancellationTokenSource shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }

        logger.LogInformation("ClashTray service stopped.");
    }
}

using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace ClashTray.Service;

internal sealed class WindowsServiceHost : ServiceBase
{
    private readonly string _userSid;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private ServiceCommandHost? _commandHost;

    public WindowsServiceHost(string userSid, ILoggerFactory loggerFactory)
    {
        ServiceName = "ClashTrayService";
        CanStop = true;
        CanShutdown = true;
        _userSid = userSid;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WindowsServiceHost>();
    }

    protected override void OnStart(string[] args)
    {
        _logger.LogInformation("Windows service host starting command host.");
        _commandHost = new ServiceCommandHost(_userSid, _loggerFactory);
        _commandHost.Start();
    }

    protected override void OnStop()
    {
        _logger.LogInformation("Windows service host stopping (OnStop).");
        DisposeCommandHost();
    }

    protected override void OnShutdown()
    {
        _logger.LogInformation("Windows service host stopping (OnShutdown).");
        DisposeCommandHost();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCommandHost();
        }

        base.Dispose(disposing);
    }

    private void DisposeCommandHost()
    {
        _commandHost?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _commandHost = null;
    }
}

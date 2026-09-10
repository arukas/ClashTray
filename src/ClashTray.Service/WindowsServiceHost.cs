using System.ServiceProcess;

namespace ClashTray.Service;

internal sealed class WindowsServiceHost : ServiceBase
{
    private readonly string _userSid;
    private ServiceCommandHost? _commandHost;

    public WindowsServiceHost(string userSid)
    {
        ServiceName = "ClashTrayService";
        CanStop = true;
        CanShutdown = true;
        _userSid = userSid;
    }

    protected override void OnStart(string[] args)
    {
        _commandHost = new ServiceCommandHost(_userSid);
        _commandHost.Start();
    }

    protected override void OnStop() => DisposeCommandHost();

    protected override void OnShutdown() => DisposeCommandHost();

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

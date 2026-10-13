namespace ClashTray.Contracts;

public enum CoreState
{
    Missing,
    Stopped,
    Validating,
    Starting,
    Running,
    Stopping,
    Restarting,
    Failed
}

public enum SystemProxyState
{
    Off,
    Enabling,
    On,
    Disabling,
    RestoreRequired,
    Failed
}

public enum TunState
{
    Unavailable,
    Unknown,
    Off,
    Enabling,
    On,
    Disabling,
    Failed
}

public enum SubscriptionState
{
    Idle,
    Downloading,
    Validating,
    Applying,
    Succeeded,
    Failed
}

public enum ProxyMode
{
    Rule,
    Global,
    Direct
}

public enum TrayState
{
    Stopped,
    Connecting,
    Running,
    SystemProxy,
    Tun,
    Paused,
    Error
}

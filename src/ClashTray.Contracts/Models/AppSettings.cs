namespace ClashTray.Contracts;

public enum ControllerPortConflictPolicy
{
    AutomaticFallback,
    Fixed
}

public sealed record AppSettings(
    string? ActiveConfigurationId = null,
    bool StartWithWindows = false,
    bool StartCoreAutomatically = false,
    int HttpPort = 7892,
    int SocksPort = 7891,
    int MixedPort = 7890,
    int ControllerPort = 9090,
    bool AllowLan = false,
    bool Ipv6 = false,
    bool TcpConcurrent = false,
    string LogLevel = "info",
    string BypassList = "localhost;127.*;192.168.*;10.*;172.16.*;<local>",
    int SubscriptionRefreshHours = 24,
    string Theme = "system",
    bool SystemProxyEnabled = false,
    bool TunEnabled = false,
    bool DisconnectConnectionsAfterProxySwitch = false,
    bool NakhimovUnlocked = false,
    string Language = "system",
    string TunStack = "configuration",
    ControllerPortConflictPolicy ControllerPortConflictPolicy = ControllerPortConflictPolicy.AutomaticFallback);

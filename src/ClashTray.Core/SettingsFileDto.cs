using System.Text.Json;
using System.Text.Json.Serialization;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Stable disk representation of settings.json. The domain record
/// <see cref="AppSettings"/> may evolve freely; this DTO is the versioned
/// contract that controls what is written and how older files are read.
/// Files written by 0.2.0 carry no schemaVersion and are treated as version 0:
/// every property default below is the migration value for a missing field.
/// Unknown fields from newer builds are preserved across a load/save cycle.
/// </summary>
internal sealed class SettingsFileDto
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? ActiveConfigurationId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool StartCoreAutomatically { get; set; }

    public int HttpPort { get; set; } = 7892;

    public int SocksPort { get; set; } = 7891;

    public int MixedPort { get; set; } = 7890;

    public int ControllerPort { get; set; } = 9090;

    public bool AllowLan { get; set; }

    public bool Ipv6 { get; set; }

    public bool TcpConcurrent { get; set; }

    public string LogLevel { get; set; } = "info";

    public string BypassList { get; set; } = "localhost;127.*;192.168.*;10.*;172.16.*;<local>";

    public int SubscriptionRefreshHours { get; set; } = 24;

    public string Theme { get; set; } = "system";

    public bool SystemProxyEnabled { get; set; }

    public bool TunEnabled { get; set; }

    public bool DisconnectConnectionsAfterProxySwitch { get; set; }

    public bool NakhimovUnlocked { get; set; }

    public string Language { get; set; } = "system";

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    public static SettingsFileDto FromDomain(
        AppSettings settings,
        IDictionary<string, JsonElement>? preservedFields)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SettingsFileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveConfigurationId = settings.ActiveConfigurationId,
            StartWithWindows = settings.StartWithWindows,
            StartCoreAutomatically = settings.StartCoreAutomatically,
            HttpPort = settings.HttpPort,
            SocksPort = settings.SocksPort,
            MixedPort = settings.MixedPort,
            ControllerPort = settings.ControllerPort,
            AllowLan = settings.AllowLan,
            Ipv6 = settings.Ipv6,
            TcpConcurrent = settings.TcpConcurrent,
            LogLevel = settings.LogLevel,
            BypassList = settings.BypassList,
            SubscriptionRefreshHours = settings.SubscriptionRefreshHours,
            Theme = settings.Theme,
            SystemProxyEnabled = settings.SystemProxyEnabled,
            TunEnabled = settings.TunEnabled,
            DisconnectConnectionsAfterProxySwitch = settings.DisconnectConnectionsAfterProxySwitch,
            NakhimovUnlocked = settings.NakhimovUnlocked,
            Language = settings.Language,
            AdditionalFields = preservedFields is null
                ? null
                : new Dictionary<string, JsonElement>(preservedFields)
        };
    }

    public AppSettings ToDomain() => new(
        ActiveConfigurationId: ActiveConfigurationId,
        StartWithWindows: StartWithWindows,
        StartCoreAutomatically: StartCoreAutomatically,
        HttpPort: HttpPort,
        SocksPort: SocksPort,
        MixedPort: MixedPort,
        ControllerPort: ControllerPort,
        AllowLan: AllowLan,
        Ipv6: Ipv6,
        TcpConcurrent: TcpConcurrent,
        LogLevel: LogLevel,
        BypassList: BypassList,
        SubscriptionRefreshHours: SubscriptionRefreshHours,
        Theme: Theme,
        SystemProxyEnabled: SystemProxyEnabled,
        TunEnabled: TunEnabled,
        DisconnectConnectionsAfterProxySwitch: DisconnectConnectionsAfterProxySwitch,
        NakhimovUnlocked: NakhimovUnlocked,
        Language: Language);
}

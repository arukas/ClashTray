using ClashTray.Contracts;

namespace ClashTray.Core;

public readonly record struct SettingPatchValue<T>(bool IsSpecified, T Value)
{
    public T Merge(T current) => IsSpecified ? Value : current;
}

public static class SettingPatchValue
{
    public static SettingPatchValue<T> Set<T>(T value) => new(true, value);
}

/// <summary>
/// Describes only the settings a caller intends to change. Unspecified fields
/// are merged with the latest settings after the runtime has acquired its
/// operation lease; explicitly specified false, zero, and null values remain
/// distinguishable from an omitted field.
/// </summary>
public sealed record AppSettingsPatch(
    SettingPatchValue<string?> ActiveConfigurationId = default,
    SettingPatchValue<bool> StartWithWindows = default,
    SettingPatchValue<bool> StartCoreAutomatically = default,
    SettingPatchValue<int> HttpPort = default,
    SettingPatchValue<int> SocksPort = default,
    SettingPatchValue<int> MixedPort = default,
    SettingPatchValue<int> ControllerPort = default,
    SettingPatchValue<bool> AllowLan = default,
    SettingPatchValue<bool> Ipv6 = default,
    SettingPatchValue<bool> TcpConcurrent = default,
    SettingPatchValue<string> LogLevel = default,
    SettingPatchValue<string> BypassList = default,
    SettingPatchValue<int> SubscriptionRefreshHours = default,
    SettingPatchValue<string> Theme = default,
    SettingPatchValue<bool> SystemProxyEnabled = default,
    SettingPatchValue<bool> TunEnabled = default,
    SettingPatchValue<bool> DisconnectConnectionsAfterProxySwitch = default,
    SettingPatchValue<bool> NakhimovUnlocked = default,
    SettingPatchValue<string> Language = default,
    SettingPatchValue<string> TunStack = default)
{
    public bool IsEmpty =>
        !ActiveConfigurationId.IsSpecified
        && !StartWithWindows.IsSpecified
        && !StartCoreAutomatically.IsSpecified
        && !HttpPort.IsSpecified
        && !SocksPort.IsSpecified
        && !MixedPort.IsSpecified
        && !ControllerPort.IsSpecified
        && !AllowLan.IsSpecified
        && !Ipv6.IsSpecified
        && !TcpConcurrent.IsSpecified
        && !LogLevel.IsSpecified
        && !BypassList.IsSpecified
        && !SubscriptionRefreshHours.IsSpecified
        && !Theme.IsSpecified
        && !SystemProxyEnabled.IsSpecified
        && !TunEnabled.IsSpecified
        && !DisconnectConnectionsAfterProxySwitch.IsSpecified
        && !NakhimovUnlocked.IsSpecified
        && !Language.IsSpecified
        && !TunStack.IsSpecified;

    public AppSettings Apply(AppSettings latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        return latest with
        {
            ActiveConfigurationId = ActiveConfigurationId.Merge(latest.ActiveConfigurationId),
            StartWithWindows = StartWithWindows.Merge(latest.StartWithWindows),
            StartCoreAutomatically = StartCoreAutomatically.Merge(latest.StartCoreAutomatically),
            HttpPort = HttpPort.Merge(latest.HttpPort),
            SocksPort = SocksPort.Merge(latest.SocksPort),
            MixedPort = MixedPort.Merge(latest.MixedPort),
            ControllerPort = ControllerPort.Merge(latest.ControllerPort),
            AllowLan = AllowLan.Merge(latest.AllowLan),
            Ipv6 = Ipv6.Merge(latest.Ipv6),
            TcpConcurrent = TcpConcurrent.Merge(latest.TcpConcurrent),
            LogLevel = LogLevel.Merge(latest.LogLevel),
            BypassList = BypassList.Merge(latest.BypassList),
            SubscriptionRefreshHours = SubscriptionRefreshHours.Merge(latest.SubscriptionRefreshHours),
            Theme = Theme.Merge(latest.Theme),
            SystemProxyEnabled = SystemProxyEnabled.Merge(latest.SystemProxyEnabled),
            TunEnabled = TunEnabled.Merge(latest.TunEnabled),
            DisconnectConnectionsAfterProxySwitch = DisconnectConnectionsAfterProxySwitch.Merge(latest.DisconnectConnectionsAfterProxySwitch),
            NakhimovUnlocked = NakhimovUnlocked.Merge(latest.NakhimovUnlocked),
            Language = Language.Merge(latest.Language),
            TunStack = TunStack.Merge(latest.TunStack)
        };
    }

    public static AppSettingsPatch Diff(AppSettings baseline, AppSettings proposed)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(proposed);

        return new AppSettingsPatch(
            Changed(baseline.ActiveConfigurationId, proposed.ActiveConfigurationId),
            Changed(baseline.StartWithWindows, proposed.StartWithWindows),
            Changed(baseline.StartCoreAutomatically, proposed.StartCoreAutomatically),
            Changed(baseline.HttpPort, proposed.HttpPort),
            Changed(baseline.SocksPort, proposed.SocksPort),
            Changed(baseline.MixedPort, proposed.MixedPort),
            Changed(baseline.ControllerPort, proposed.ControllerPort),
            Changed(baseline.AllowLan, proposed.AllowLan),
            Changed(baseline.Ipv6, proposed.Ipv6),
            Changed(baseline.TcpConcurrent, proposed.TcpConcurrent),
            Changed(baseline.LogLevel, proposed.LogLevel),
            Changed(baseline.BypassList, proposed.BypassList),
            Changed(baseline.SubscriptionRefreshHours, proposed.SubscriptionRefreshHours),
            Changed(baseline.Theme, proposed.Theme),
            Changed(baseline.SystemProxyEnabled, proposed.SystemProxyEnabled),
            Changed(baseline.TunEnabled, proposed.TunEnabled),
            Changed(
                baseline.DisconnectConnectionsAfterProxySwitch,
                proposed.DisconnectConnectionsAfterProxySwitch),
            Changed(baseline.NakhimovUnlocked, proposed.NakhimovUnlocked),
            Changed(baseline.Language, proposed.Language),
            Changed(baseline.TunStack, proposed.TunStack));
    }

    private static SettingPatchValue<T> Changed<T>(T baseline, T proposed) =>
        EqualityComparer<T>.Default.Equals(baseline, proposed)
            ? default
            : SettingPatchValue.Set(proposed);
}

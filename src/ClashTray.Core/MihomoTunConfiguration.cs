using System.Net;
using System.Text.Json;

namespace ClashTray.Core;

/// <summary>
/// The small subset of Mihomo's TUN configuration required to validate a
/// transition without inventing an address or treating the user source file
/// as mutable runtime state.
/// </summary>
public sealed record MihomoTunConfiguration(
    bool? Enabled,
    string? DeviceName,
    bool AutoRoute,
    bool HasExplicitAddress,
    bool HasValidAddress,
    bool HasFakeIpRange,
    bool RequiresDnsHealth)
{
    public bool CanProduceInterfaceAddress =>
        !HasExplicitAddress || HasValidAddress || HasFakeIpRange;

    public static MihomoTunConfiguration Empty { get; } = new(
        Enabled: null,
        DeviceName: null,
        AutoRoute: true,
        HasExplicitAddress: false,
        HasValidAddress: false,
        HasFakeIpRange: false,
        RequiresDnsHealth: false);
}

internal static class MihomoTunConfigurationParser
{
    public static MihomoTunConfiguration Parse(JsonElement root, bool? enabled)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tun", out JsonElement tun)
            || tun.ValueKind != JsonValueKind.Object)
        {
            return MihomoTunConfiguration.Empty with { Enabled = enabled };
        }

        bool hasInet4 = tun.TryGetProperty("inet4-address", out JsonElement inet4);
        bool hasInet6 = tun.TryGetProperty("inet6-address", out JsonElement inet6);
        bool hasExplicitAddress = hasInet4 || hasInet6;
        bool hasValidAddress = HasValidAddress(inet4) || HasValidAddress(inet6);
        bool hasFakeIpRange = HasValidCidr(tun, "fake-ip-range")
            || root.TryGetProperty("dns", out JsonElement dns)
            && dns.ValueKind == JsonValueKind.Object
            && HasValidCidr(dns, "fake-ip-range");
        bool autoRoute = !tun.TryGetProperty("auto-route", out JsonElement autoRouteValue)
            || autoRouteValue.ValueKind != JsonValueKind.False;
        string? deviceName = ReadString(tun, "device");
        // sing-tun configures per-interface DNS on Windows as part of the
        // auto-route path. dns-hijack alone does not imply that a manual-route
        // TUN interface must expose DNS server addresses.
        bool requiresDnsHealth = autoRoute && HasDnsRequirement(root);
        return new MihomoTunConfiguration(
            enabled,
            deviceName,
            autoRoute,
            hasExplicitAddress,
            hasValidAddress,
            hasFakeIpRange,
            requiresDnsHealth);
    }

    private static bool HasValidAddress(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && IsValidCidr(item.GetString()));
        }

        return value.ValueKind == JsonValueKind.String && IsValidCidr(value.GetString());
    }

    private static bool HasDnsRequirement(JsonElement root)
    {
        if (root.TryGetProperty("tun", out JsonElement tun)
            && tun.ValueKind == JsonValueKind.Object
            && tun.TryGetProperty("dns-hijack", out JsonElement tunHijack)
            && HasValue(tunHijack))
        {
            return true;
        }

        if (!root.TryGetProperty("dns", out JsonElement dns)
            || dns.ValueKind != JsonValueKind.Object
            || dns.TryGetProperty("enable", out JsonElement enabled)
                && enabled.ValueKind == JsonValueKind.False
            || !dns.TryGetProperty("hijack", out JsonElement hijack))
        {
            return false;
        }

        return HasValue(hijack);
    }

    private static bool HasValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        _ => false
    };

    private static bool HasValidCidr(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement value)
        && (value.ValueKind == JsonValueKind.String && IsValidCidr(value.GetString())
            || value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && IsValidCidr(item.GetString())));

    private static bool IsValidCidr(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out IPAddress? address)
            || address.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork
                or System.Net.Sockets.AddressFamily.InterNetworkV6)
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || !int.TryParse(parts[1], out int prefixLength))
        {
            return false;
        }

        int maxPrefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return prefixLength is >= 0 and <= 128 && prefixLength <= maxPrefixLength;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

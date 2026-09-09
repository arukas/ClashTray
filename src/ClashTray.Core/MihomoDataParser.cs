using System.Globalization;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class MihomoDataParser
{
    private const int MaxProxyEntries = 2_000;
    private const int MaxConnectionEntries = 2_000;
    private const int MaxRuleEntries = 5_000;
    private const int MaxProviderEntries = 500;

    public static string? ParseVersion(JsonDocument document) =>
        GetString(document.RootElement, "version");

    public static ProxyMode? ParseMode(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("mode", out var mode)
            || mode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return mode.GetString()?.ToLowerInvariant() switch
        {
            "rule" => ProxyMode.Rule,
            "global" => ProxyMode.Global,
            "direct" => ProxyMode.Direct,
            _ => null
        };
    }

    public static (IReadOnlyList<ProxyGroup> Groups, IReadOnlyList<ProxyNode> Nodes) ParseProxies(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("proxies", out var proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            return ([], []);
        }

        var groups = new List<ProxyGroup>();
        var nodes = new List<ProxyNode>();
        foreach (var property in proxies.EnumerateObject().Take(MaxProxyEntries))
        {
            var value = property.Value;
            var type = GetString(value, "type") ?? "Unknown";
            var current = GetString(value, "now");
            var members = GetStringArray(value, "all");
            var isGroup = members.Length > 0 || type is "Selector" or "URLTest" or "Fallback" or "LoadBalance";
            if (isGroup)
            {
                groups.Add(new ProxyGroup(property.Name, type, current, members));
            }
            else
            {
                nodes.Add(new ProxyNode(property.Name, type, null, false, []));
            }
        }

        var currentNames = groups.Select(group => group.Current).Where(name => name is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        nodes = nodes.Select(node => node with { IsCurrent = currentNames.Contains(node.Name) }).ToList();
        return (groups, nodes);
    }

    public static TrafficSnapshot ParseTraffic(JsonDocument document)
    {
        var root = document.RootElement;
        var upTotal = GetLongOrNull(root, "upTotal")
            ?? GetLongOrNull(root, "uploadTotal")
            ?? GetLongOrNull(root, "upload")
            ?? 0;
        var downTotal = GetLongOrNull(root, "downTotal")
            ?? GetLongOrNull(root, "downloadTotal")
            ?? GetLongOrNull(root, "download")
            ?? 0;
        var upSpeed = GetDoubleOrNull(root, "up")
            ?? GetDoubleOrNull(root, "upSpeed")
            ?? 0;
        var downSpeed = GetDoubleOrNull(root, "down")
            ?? GetDoubleOrNull(root, "downSpeed")
            ?? 0;
        return new TrafficSnapshot(
            upTotal,
            downTotal,
            upSpeed,
            downSpeed,
            DateTimeOffset.UtcNow);
    }

    public static long ParseMemoryBytes(JsonDocument document)
    {
        var root = document.RootElement;
        var inUse = GetLong(root, "inuse");
        if (inUse != 0)
        {
            return inUse;
        }

        var camelCaseInUse = GetLong(root, "inUse");
        if (camelCaseInUse != 0)
        {
            return camelCaseInUse;
        }

        var memory = GetLong(root, "memory");
        return memory != 0 ? memory : GetLong(root, "bytes");
    }

    public static bool? ParseTunEnabled(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("tun", out var tun)
            || tun.ValueKind != JsonValueKind.Object
            || !tun.TryGetProperty("enable", out var enable))
        {
            return null;
        }

        return enable.ValueKind is JsonValueKind.True or JsonValueKind.False ? enable.GetBoolean() : null;
    }

    public static bool? ParseAllowLan(JsonDocument document) =>
        GetBoolean(document.RootElement, "allow-lan", "allowLan");

    public static bool? ParseIpv6(JsonDocument document) =>
        GetBoolean(document.RootElement, "ipv6");

    public static IReadOnlyList<ConnectionInfo> ParseConnections(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ConnectionInfo>();
        foreach (var connection in connections.EnumerateArray().Take(MaxConnectionEntries))
        {
            var metadata = connection.TryGetProperty("metadata", out var metadataElement) ? metadataElement : default;
            var chains = GetStringArray(connection, "chains");
            result.Add(new ConnectionInfo(
                GetString(connection, "id") ?? Guid.NewGuid().ToString("N"),
                GetString(metadata, "network") ?? "-",
                GetString(metadata, "sourceIP") ?? GetString(metadata, "source") ?? "-",
                GetString(metadata, "destinationIP") ?? GetString(metadata, "host") ?? GetString(metadata, "destination") ?? "-",
                GetString(connection, "rule") ?? "-",
                string.Join(" → ", chains),
                GetLong(connection, "upload"),
                GetLong(connection, "download"),
                ParseTimestamp(GetString(connection, "start")),
                GetString(connection, "rulePayload") ?? "-"));
        }

        return result;
    }

    public static IReadOnlyList<RuleInfo> ParseRules(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("rules", out var rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<RuleInfo>();
        foreach (var rule in rules.EnumerateArray().Take(MaxRuleEntries))
        {
            if (rule.ValueKind == JsonValueKind.Object)
            {
                result.Add(new RuleInfo(
                    GetString(rule, "type") ?? "-",
                    GetString(rule, "payload") ?? "-",
                    GetString(rule, "proxy") ?? "-",
                    GetInt(rule, "size")));
                continue;
            }

            if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() < 3)
            {
                continue;
            }

            var type = GetText(rule[0]) ?? "-";
            var payload = GetText(rule[1]) ?? "-";
            var proxy = GetText(rule[2]) ?? "-";
            result.Add(new RuleInfo(type, payload, proxy, GetInt(rule, 3)));
        }

        return result;
    }

    public static IReadOnlyList<ProviderStatus> ParseProviders(JsonDocument document, string fallbackType)
    {
        if (!document.RootElement.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<ProviderStatus>();
        foreach (var property in providers.EnumerateObject().Take(MaxProviderEntries))
        {
            var value = property.Value;
            result.Add(new ProviderStatus(
                property.Name,
                fallbackType,
                GetString(value, "vehicleType") ?? "-",
                ParseTimestamp(GetString(value, "updatedAt")),
                GetString(value, "message"),
                GetCount(value, "proxies") + GetCount(value, "rules")));
        }

        return result;
    }

    public static IReadOnlyList<LogEntry> ParseLogs(JsonDocument document, string source)
    {
        var result = new List<LogEntry>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                result.Add(ParseLog(item, source));
            }
        }
        else if (document.RootElement.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in logs.EnumerateArray())
            {
                result.Add(ParseLog(item, source));
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object && IsLogObject(document.RootElement))
        {
            result.Add(ParseLog(document.RootElement, source));
        }

        return result;
    }

    private static LogEntry ParseLog(JsonElement item, string source)
    {
        var timestamp = ParseTimestamp(GetString(item, "time"));
        var level = GetString(item, "type") ?? GetString(item, "level") ?? "info";
        var message = GetString(item, "payload") ?? GetString(item, "message") ?? item.ToString();
        return new LogEntry(timestamp, source, level, message);
    }

    private static bool IsLogObject(JsonElement item) =>
        item.TryGetProperty("time", out _)
        || item.TryGetProperty("type", out _)
        || item.TryGetProperty("level", out _)
        || item.TryGetProperty("payload", out _)
        || item.TryGetProperty("message", out _);

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp
            : DateTimeOffset.UtcNow;

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? GetBoolean(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in properties)
        {
            if (element.TryGetProperty(property, out var value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static string? GetText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            _ => element.ToString()
        };

    private static string[] GetStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
    }

    private static long GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static long? GetLongOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static double? GetDoubleOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.TryGetDouble(out var number)
                ? number
                : null;

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static int GetInt(JsonElement element, int index) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > index && element[index].TryGetInt32(out var number)
            ? number
            : 0;

    private static int GetCount(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : value.TryGetInt32(out var count) ? count : 0;
    }
}

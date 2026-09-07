using System.Globalization;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

public static class MihomoDataParser
{
    public static string? ParseVersion(JsonDocument document) =>
        document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;

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
        foreach (var property in proxies.EnumerateObject())
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
        var upTotal = GetLong(root, "upTotal");
        var downTotal = GetLong(root, "downTotal");
        var upSpeed = GetDouble(root, "up");
        var downSpeed = GetDouble(root, "down");
        return new TrafficSnapshot(
            upTotal != 0 ? upTotal : GetLong(root, "up"),
            downTotal != 0 ? downTotal : GetLong(root, "down"),
            upSpeed != 0 ? upSpeed : GetDouble(root, "upSpeed"),
            downSpeed != 0 ? downSpeed : GetDouble(root, "downSpeed"),
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

    public static IReadOnlyList<ConnectionInfo> ParseConnections(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("connections", out var connections)
            || connections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ConnectionInfo>();
        foreach (var connection in connections.EnumerateArray())
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
        foreach (var rule in rules.EnumerateArray())
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

            var type = rule[0].GetString() ?? "-";
            var payload = rule[1].GetString() ?? "-";
            var proxy = rule[2].GetString() ?? "-";
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
        foreach (var property in providers.EnumerateObject())
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
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
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

    private static string[] GetStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null).Cast<string>().ToArray();
    }

    private static long GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static double GetDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;

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

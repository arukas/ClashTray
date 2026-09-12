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
    private const int MaxLogEntries = 500;
    private const int MaxIdentifierCharacters = 256;
    private const int MaxConnectionFieldCharacters = 1_024;
    private const int MaxRuleTextCharacters = 4_096;
    private const int MaxLogMessageCharacters = 4_096;
    private const int MaxGroupMemberEntries = 500;
    private const int MaxChainEntries = 64;

    public static string? ParseVersion(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetString(document.RootElement, "version");
    }

    public static ProxyMode? ParseMode(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty("mode", out JsonElement mode)
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
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty("proxies", out JsonElement proxies)
            || proxies.ValueKind != JsonValueKind.Object)
        {
            return ([], []);
        }

        List<ProxyGroup> groups = new List<ProxyGroup>();
        List<ProxyNode> nodes = new List<ProxyNode>();
        foreach (JsonProperty property in proxies.EnumerateObject().Take(MaxProxyEntries))
        {
            JsonElement value = property.Value;
            if (value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? delay = ReadLatestDelay(value);
            string type = GetString(value, "type", MaxIdentifierCharacters) ?? "Unknown";
            string? current = GetIdentifier(value, "now");
            string[] members = GetStringArray(value, "all", MaxGroupMemberEntries, MaxIdentifierCharacters);
            string name = property.Name;
            if (name.Length > MaxIdentifierCharacters)
            {
                continue;
            }

            bool isGroup = members.Length > 0 || type is "Selector" or "URLTest" or "Fallback" or "LoadBalance";
            if (isGroup)
            {
                groups.Add(new ProxyGroup(name, type, current, members, delay));
            }
            else
            {
                nodes.Add(new ProxyNode(name, type, delay, false, []));
            }
        }

        HashSet<string?> currentNames = groups.Select(group => group.Current).Where(name => name is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        nodes = nodes.Select(node => node with { IsCurrent = currentNames.Contains(node.Name) }).ToList();
        return (groups, nodes);
    }

    private static string? ReadLatestDelay(JsonElement proxy)
    {
        if (!proxy.TryGetProperty("history", out JsonElement history) || history.ValueKind != JsonValueKind.Array
            || history.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement latest = history[history.GetArrayLength() - 1];
        return latest.ValueKind == JsonValueKind.Object && latest.TryGetProperty("delay", out JsonElement delay)
            && delay.ValueKind == JsonValueKind.Number && delay.TryGetInt32(out int milliseconds) && milliseconds >= 0
                ? milliseconds.ToString(CultureInfo.InvariantCulture) : null;
    }

    public static IReadOnlyDictionary<string, int?> ParseGroupDelays(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Mihomo 组测速结果格式无效。");
        }

        Dictionary<string, int?> delays = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject().Take(MaxProxyEntries))
        {
            if (property.Name.Length > MaxIdentifierCharacters)
            {
                continue;
            }

            delays[property.Name] = property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out int value) && value >= 0 ? value : null;
        }

        return delays;
    }

    public static TrafficSnapshot ParseTraffic(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        JsonElement root = document.RootElement;
        long upTotal = GetLongOrNull(root, "upTotal")
            ?? GetLongOrNull(root, "uploadTotal")
            ?? GetLongOrNull(root, "upload")
            ?? 0;
        long downTotal = GetLongOrNull(root, "downTotal")
            ?? GetLongOrNull(root, "downloadTotal")
            ?? GetLongOrNull(root, "download")
            ?? 0;
        double upSpeed = GetDoubleOrNull(root, "up")
            ?? GetDoubleOrNull(root, "upSpeed")
            ?? 0;
        double downSpeed = GetDoubleOrNull(root, "down")
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
        ArgumentNullException.ThrowIfNull(document);
        JsonElement root = document.RootElement;
        long inUse = GetLong(root, "inuse");
        if (inUse != 0)
        {
            return inUse;
        }

        long camelCaseInUse = GetLong(root, "inUse");
        if (camelCaseInUse != 0)
        {
            return camelCaseInUse;
        }

        long memory = GetLong(root, "memory");
        return memory != 0 ? memory : GetLong(root, "bytes");
    }

    public static bool? ParseTunEnabled(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty("tun", out JsonElement tun)
            || tun.ValueKind != JsonValueKind.Object
            || !tun.TryGetProperty("enable", out JsonElement enable))
        {
            return null;
        }

        return enable.ValueKind is JsonValueKind.True or JsonValueKind.False ? enable.GetBoolean() : null;
    }

    public static bool? ParseAllowLan(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetBoolean(document.RootElement, "allow-lan", "allowLan");
    }

    public static bool? ParseIpv6(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetBoolean(document.RootElement, "ipv6");
    }

    public static IReadOnlyList<ConnectionInfo> ParseConnections(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty("connections", out JsonElement connections)
            || connections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<ConnectionInfo> result = new List<ConnectionInfo>();
        foreach (JsonElement connection in connections.EnumerateArray().Take(MaxConnectionEntries))
        {
            string? id = GetRawString(connection, "id");
            if (id is { Length: > MaxIdentifierCharacters })
            {
                continue;
            }

            JsonElement metadata = connection.TryGetProperty("metadata", out JsonElement metadataElement) ? metadataElement : default;
            string[] chains = GetStringArray(connection, "chains", MaxChainEntries, MaxConnectionFieldCharacters);
            result.Add(new ConnectionInfo(
                id ?? Guid.NewGuid().ToString("N"),
                GetString(metadata, "network", MaxConnectionFieldCharacters) ?? "-",
                GetString(metadata, "sourceIP", MaxConnectionFieldCharacters)
                    ?? GetString(metadata, "source", MaxConnectionFieldCharacters)
                    ?? "-",
                GetString(metadata, "destinationIP", MaxConnectionFieldCharacters)
                    ?? GetString(metadata, "host", MaxConnectionFieldCharacters)
                    ?? GetString(metadata, "destination", MaxConnectionFieldCharacters)
                    ?? "-",
                GetString(connection, "rule", MaxConnectionFieldCharacters) ?? "-",
                string.Join(" → ", chains),
                GetLong(connection, "upload"),
                GetLong(connection, "download"),
                ParseTimestamp(GetString(connection, "start")),
                GetString(connection, "rulePayload", MaxConnectionFieldCharacters) ?? "-"));
        }

        return result;
    }

    public static IReadOnlyList<RuleInfo> ParseRules(JsonDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.RootElement.TryGetProperty("rules", out JsonElement rules)
            || rules.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<RuleInfo> result = new List<RuleInfo>();
        foreach (JsonElement rule in rules.EnumerateArray().Take(MaxRuleEntries))
        {
            if (rule.ValueKind == JsonValueKind.Object)
            {
                result.Add(new RuleInfo(
                    GetString(rule, "type", MaxIdentifierCharacters) ?? "-",
                    GetString(rule, "payload", MaxRuleTextCharacters) ?? "-",
                    GetString(rule, "proxy", MaxIdentifierCharacters) ?? "-",
                    GetInt(rule, "size")));
                continue;
            }

            if (rule.ValueKind != JsonValueKind.Array || rule.GetArrayLength() < 3)
            {
                continue;
            }

            string type = GetText(rule[0], MaxIdentifierCharacters) ?? "-";
            string payload = GetText(rule[1], MaxRuleTextCharacters) ?? "-";
            string proxy = GetText(rule[2], MaxIdentifierCharacters) ?? "-";
            result.Add(new RuleInfo(type, payload, proxy, GetInt(rule, 3)));
        }

        return result;
    }

    public static IReadOnlyList<ProviderStatus> ParseProviders(JsonDocument document, string fallbackType)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fallbackType);
        if (!document.RootElement.TryGetProperty("providers", out JsonElement providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        List<ProviderStatus> result = new List<ProviderStatus>();
        foreach (JsonProperty property in providers.EnumerateObject().Take(MaxProviderEntries))
        {
            if (property.Name.Length > MaxIdentifierCharacters)
            {
                continue;
            }

            JsonElement value = property.Value;
            result.Add(new ProviderStatus(
                property.Name,
                fallbackType,
                GetString(value, "vehicleType", MaxIdentifierCharacters) ?? "-",
                ParseTimestamp(GetString(value, "updatedAt")),
                GetString(value, "message", MaxConnectionFieldCharacters),
                GetCount(value, "proxies") + GetCount(value, "rules")));
        }

        return result;
    }

    public static IReadOnlyList<LogEntry> ParseLogs(JsonDocument document, string source)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        List<LogEntry> result = new List<LogEntry>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in document.RootElement.EnumerateArray().Take(MaxLogEntries))
            {
                result.Add(ParseLog(item, source));
            }
        }
        else if (document.RootElement.TryGetProperty("logs", out JsonElement logs) && logs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in logs.EnumerateArray().Take(MaxLogEntries))
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
        DateTimeOffset timestamp = ParseTimestamp(GetString(item, "time"));
        string level = GetString(item, "type", MaxIdentifierCharacters)
            ?? GetString(item, "level", MaxIdentifierCharacters)
            ?? "info";
        string message = GetString(item, "payload", MaxLogMessageCharacters)
            ?? GetString(item, "message", MaxLogMessageCharacters)
            ?? LimitText(item.ToString(), MaxLogMessageCharacters)
            ?? "-";
        return new LogEntry(timestamp, source, level, message);
    }

    private static bool IsLogObject(JsonElement item) =>
        item.TryGetProperty("time", out _)
        || item.TryGetProperty("type", out _)
        || item.TryGetProperty("level", out _)
        || item.TryGetProperty("payload", out _)
        || item.TryGetProperty("message", out _);

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp)
            ? timestamp
            : DateTimeOffset.UtcNow;

    private static string? GetString(
        JsonElement element,
        string property,
        int maxCharacters = MaxConnectionFieldCharacters)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return LimitText(GetRawString(value), maxCharacters);
    }

    private static bool? GetBoolean(JsonElement element, params string[] properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (string property in properties)
        {
            if (element.TryGetProperty(property, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static string? GetText(JsonElement element, int maxCharacters = MaxConnectionFieldCharacters) =>
        LimitText(
            element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                _ => element.ToString()
            },
            maxCharacters);

    private static string[] GetStringArray(
        JsonElement element,
        string property,
        int maxEntries,
        int maxCharacters)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Take(maxEntries)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .Where(item => item.Length <= maxCharacters)
            .ToArray();
    }

    private static string? GetIdentifier(JsonElement element, string property)
    {
        string? value = GetRawString(element, property);
        return value is { Length: <= MaxIdentifierCharacters } ? value : null;
    }

    private static string? GetRawString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return GetRawString(value);
    }

    private static string GetRawString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => value.ToString()
        };

    private static string? LimitText(string? value, int maxCharacters)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= maxCharacters)
        {
            return value;
        }

        return maxCharacters <= 1
            ? "…"
            : value[..(maxCharacters - 1)] + "…";
    }

    private static long GetLong(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long number)
            ? number
            : 0;

    private static long? GetLongOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.TryGetInt64(out long number)
                ? number
                : null;

    private static double? GetDoubleOrNull(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.TryGetDouble(out double number)
                ? number
                : null;

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : 0;

    private static int GetInt(JsonElement element, int index) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > index && element[index].TryGetInt32(out int number)
            ? number
            : 0;

    private static int GetCount(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : value.TryGetInt32(out int count) ? count : 0;
    }
}
